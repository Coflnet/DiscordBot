using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Coflnet.Discord;

public enum CreatorOnboardingStatus
{
    Pending = 1,
    Approved = 2,
    Suspended = 3,
    Rejected = 4
}

public enum CreatorSellerType
{
    Individual = 1,
    Business = 2
}

public enum CreatorCapacityStatus
{
    AdultDeclared = 1,
    Minor16PlusWithGuardian = 2,
    Insufficient = 3
}

public enum CreatorTaxDocumentRoute
{
    NotApplicable = 1,
    Statement = 2,
    CreatorInvoice = 3,
    UkSelfBilling = 4,
    UsSettlement = 5
}

public sealed class CreatorOnboardingClient(
    HttpClient http,
    IConfiguration configuration)
{
    public async Task<CreatorReview?> GetLatest(
        string creatorUserId,
        CancellationToken cancellationToken = default)
    {
        var (uri, token) = Connection();
        using var request = Request(
            HttpMethod.Get,
            new Uri(uri, $"/api/creator-onboarding/{Uri.EscapeDataString(creatorUserId)}/reviews/latest"),
            token);
        request.Headers.Add("X-Reviewer-Id",
            $"discord:{CreatorReviewCommands.ReviewerId}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CreatorReview>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Creator onboarding returned an empty response.");
    }

    public async Task<CreatorReview> Append(
        CreatorReviewRequest review,
        ulong reviewerDiscordId,
        CancellationToken cancellationToken = default)
    {
        var (uri, token) = Connection();
        using var request = Request(
            HttpMethod.Post,
            new Uri(uri, "/api/creator-onboarding/reviews"),
            token);
        request.Headers.Add("X-Reviewer-Id", $"discord:{reviewerDiscordId}");
        request.Content = JsonContent.Create(review);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CreatorReview>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Creator onboarding returned an empty response.");
    }

    public async Task<CreatorReview> AcceptGuardian(
        Guid reviewId,
        string agreementHash,
        string locale,
        ulong representativeDiscordId,
        CancellationToken cancellationToken = default)
    {
        var (uri, token) = Connection();
        using var request = Request(
            HttpMethod.Post,
            new Uri(uri,
                $"/api/creator-onboarding/reviews/{reviewId:D}/guardian-acceptance"),
            token);
        request.Headers.Add(
            "X-Reviewer-Id", $"discord:{representativeDiscordId}");
        request.Content = JsonContent.Create(new
        {
            agreementHash,
            locale
        });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CreatorReview>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Creator onboarding returned an empty response.");
    }

    public async Task<CreatorAgreement> GetCurrentAgreement(
        string locale,
        CancellationToken cancellationToken = default)
    {
        locale = locale?.StartsWith(
            "de", StringComparison.OrdinalIgnoreCase) == true ? "de" : "en";
        var manifestUri = new Uri(
            configuration["LEGAL_MANIFEST_URL"]
            ?? "https://coflnet.com/legal/manifest.json");
        RequireCoflnetUri(manifestUri);
        using var manifest = JsonDocument.Parse(
            await http.GetByteArrayAsync(manifestUri, cancellationToken));
        var root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("source").GetString() != "https://coflnet.com/"
            || !root.GetProperty("agreements").TryGetProperty(
                "creatorMarketplace", out var agreement)
            || !root.GetProperty("documents").TryGetProperty(
                "creatorLicense", out var license))
            throw new InvalidOperationException(
                "The Creator agreement manifest is invalid.");

        var hash = agreement.GetProperty("agreementHash").GetString();
        var agreementUri = new Uri(
            agreement.GetProperty("agreementUrl").GetString()!);
        var localized = license.GetProperty("locales").GetProperty(locale);
        var licenseUri = new Uri(localized.GetProperty("url").GetString()!);
        var licenseHash = localized.GetProperty("sha256").GetString();
        if (!IsSha256(hash) || !IsSha256(licenseHash))
            throw new InvalidOperationException(
                "The Creator agreement hashes are invalid.");
        RequireCoflnetUri(agreementUri);
        RequireCoflnetUri(licenseUri);
        await Verify(agreementUri, hash!, cancellationToken);
        await Verify(licenseUri, licenseHash!, cancellationToken);
        return new(
            hash!,
            agreementUri.ToString(),
            license.GetProperty("version").GetString()!,
            licenseUri.ToString(),
            locale);
    }

    private (Uri Uri, string Token) Connection()
    {
        var token = configuration["CREATOR_ONBOARDING:REVIEW_TOKEN"];
        if (!Uri.TryCreate(configuration["REFERRAL_BASE_URL"], UriKind.Absolute, out var uri)
            || token?.Length < 32)
            throw new InvalidOperationException(
                "Creator onboarding review access is not configured.");
        return (uri, token!);
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        Uri uri,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task Verify(
        Uri uri,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var actual = Convert.ToHexString(SHA256.HashData(
            await http.GetByteArrayAsync(uri, cancellationToken)))
            .ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Legal document hash mismatch for {uri}.");
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task EnsureSuccess(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Message))
                throw new HttpRequestException(
                    error.Message, null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
    }

    private static void RequireCoflnetUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uri.Host, "coflnet.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Legal documents must use the Coflnet HTTPS origin.");
    }
}

file sealed record ErrorResponse(string Message);

public record CreatorReviewRequest(
    Guid ReviewId,
    string CreatorUserId,
    string MinecraftUuid,
    CreatorOnboardingStatus Status,
    string ResidenceCountry,
    string TaxResidenceCountry,
    CreatorSellerType SellerType,
    string CapacityJurisdiction,
    CreatorCapacityStatus CapacityStatus,
    CreatorTaxDocumentRoute TaxDocumentRoute,
    string PrivacyNoticeVersion,
    string? VerificationReference,
    string? RepresentativeAccountId,
    string? RepresentativeAgreementHash,
    DateTime? RepresentativeAcceptedAtUtc,
    string EvidenceReference,
    string EvidenceSha256,
    string RuleVersion,
    DateTime? ValidUntilUtc,
    string Reason,
    Guid? PreviousReviewId);

public record CreatorReview(
    Guid Id,
    string CreatorUserId,
    string MinecraftUuid,
    CreatorOnboardingStatus Status,
    string ResidenceCountry,
    string TaxResidenceCountry,
    CreatorSellerType SellerType,
    string CapacityJurisdiction,
    CreatorCapacityStatus CapacityStatus,
    CreatorTaxDocumentRoute TaxDocumentRoute,
    string PrivacyNoticeVersion,
    string? VerificationReference,
    string? RepresentativeAccountId,
    string? RepresentativeAgreementHash,
    DateTime? RepresentativeAcceptedAtUtc,
    string EvidenceReference,
    string EvidenceSha256,
    string ReviewedBy,
    DateTime ReviewedAtUtc,
    string RuleVersion,
    DateTime? ValidUntilUtc,
    string Reason)
{
    public CreatorReviewRequest Next(
        CreatorOnboardingStatus status,
        string reason,
        DateTime? validUntilUtc = null) => new(
            Guid.NewGuid(), CreatorUserId, MinecraftUuid, status,
            ResidenceCountry, TaxResidenceCountry, SellerType,
            CapacityJurisdiction, CapacityStatus,
            TaxDocumentRoute,
            PrivacyNoticeVersion, VerificationReference,
            RepresentativeAccountId, RepresentativeAgreementHash,
            RepresentativeAcceptedAtUtc,
            EvidenceReference, EvidenceSha256, RuleVersion,
            validUntilUtc ?? ValidUntilUtc, reason, Id);
}

public sealed record CreatorAgreement(
    string Hash,
    string DescriptorUrl,
    string Version,
    string LicenseUrl,
    string Locale);

/// <summary>
/// Explains, from a stored review alone, why the reviewed creator may not
/// publish or may not sell paid Configs. The rules mirror
/// <c>CreatorOnboardingService.GetEligibility</c> in SkyReferral, which stays
/// the authority: keep both in sync when the seller territories change.
/// </summary>
public static class CreatorPublishing
{
    private static readonly string[] EuCountries =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    ];

    /// <summary>
    /// Reasons the creator may not publish any Config, free ones included.
    /// </summary>
    public static IReadOnlyList<string> PublishingBlockers(
        CreatorReview review,
        DateTime utcNow)
    {
        var blockers = new List<string>();
        if (review.Status != CreatorOnboardingStatus.Approved)
            blockers.Add(
                $"the review is {review.Status}, only Approved may publish");
        if (review.ValidUntilUtc <= utcNow)
            blockers.Add(
                $"the approval expired {review.ValidUntilUtc:yyyy-MM-dd}");
        if (review.CapacityStatus == CreatorCapacityStatus.Insufficient)
            blockers.Add("the declared capacity is insufficient");
        if (review.CapacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
            && review.RepresentativeAcceptedAtUtc == null)
            blockers.Add(
                "the legal representative has not accepted yet, send `/creator request-guardian`");
        if (review.CapacityStatus == CreatorCapacityStatus.AdultDeclared
            && review.RepresentativeAccountId != null)
            blockers.Add("an adult review must not carry a representative account");
        return blockers;
    }

    /// <summary>
    /// Reasons a publishable creator may still only offer free Configs.
    /// </summary>
    public static IReadOnlyList<string> PaidSellingBlockers(CreatorReview review)
    {
        var blockers = new List<string>();
        if (review.TaxDocumentRoute == CreatorTaxDocumentRoute.NotApplicable)
            blockers.Add(
                "`tax-document` is unset, so no payout route was reviewed");
        if (!PaidCountry(review.ResidenceCountry))
            blockers.Add(
                $"residence {review.ResidenceCountry} is not a paid seller country (GB, US, CH or EU)");
        if (!PaidCountry(review.TaxResidenceCountry))
            blockers.Add(
                $"tax residence {review.TaxResidenceCountry} is not a paid seller country (GB, US, CH or EU)");
        if (!PaidCapacityJurisdiction(
                review.ResidenceCountry,
                review.CapacityJurisdiction,
                review.SellerType))
            blockers.Add(
                $"`capacity-law` {review.CapacityJurisdiction} is not paid-supported ({ExpectedJurisdictions(review.ResidenceCountry)})");
        if (review.TaxDocumentRoute != CreatorTaxDocumentRoute.NotApplicable
            && !ValidTaxRoute(
                review.TaxResidenceCountry,
                review.SellerType,
                review.TaxDocumentRoute))
            blockers.Add(
                $"`tax-document` {review.TaxDocumentRoute} does not fit {review.SellerType} sellers in {review.TaxResidenceCountry} ({ExpectedRoutes(review.TaxResidenceCountry, review.SellerType)})");
        if (review.SellerType == CreatorSellerType.Business
            && review.VerificationReference == null)
            blockers.Add("a business seller needs a `verification` reference");
        return blockers;
    }

    /// <summary>
    /// One line a reviewer can act on, stating whether selling Configs works.
    /// </summary>
    public static string Summary(CreatorReview review, DateTime utcNow)
    {
        var publishing = PublishingBlockers(review, utcNow);
        if (publishing.Count > 0)
            return "Publishing Configs is **blocked**: "
                + $"{string.Join("; ", publishing)}.";
        var paid = PaidSellingBlockers(review);
        return paid.Count > 0
            ? $"Free Configs from `{review.MinecraftUuid}` are allowed, "
                + $"**paid selling is blocked**: {string.Join("; ", paid)}."
            : $"**Paid selling is enabled** from `{review.MinecraftUuid}` only; "
                + "the creator must still accept the Creator agreement in game.";
    }

    private static bool PaidCountry(string country) =>
        country is "GB" or "US" or "CH" || EuCountries.Contains(country);

    private static bool PaidCapacityJurisdiction(
        string country,
        string jurisdiction,
        CreatorSellerType sellerType)
    {
        var supported = jurisdiction is "GB-ENG" or "GB-WLS" or "CH" or "US"
            || jurisdiction.Length == 5 && jurisdiction.StartsWith("US-")
            || jurisdiction.Length == 2 && EuCountries.Contains(jurisdiction);
        if (!supported || sellerType == CreatorSellerType.Business)
            return supported;
        return country switch
        {
            "GB" => jurisdiction.StartsWith("GB-"),
            "US" => jurisdiction == "US" || jurisdiction.StartsWith("US-"),
            _ => jurisdiction == country
        };
    }

    private static bool ValidTaxRoute(
        string country,
        CreatorSellerType sellerType,
        CreatorTaxDocumentRoute route) => country switch
        {
            "GB" => sellerType == CreatorSellerType.Business
                ? route is CreatorTaxDocumentRoute.Statement
                    or CreatorTaxDocumentRoute.UkSelfBilling
                : route == CreatorTaxDocumentRoute.Statement,
            "US" => route == CreatorTaxDocumentRoute.UsSettlement,
            _ when country is "CH" || EuCountries.Contains(country) =>
                route == (sellerType == CreatorSellerType.Business
                    ? CreatorTaxDocumentRoute.CreatorInvoice
                    : CreatorTaxDocumentRoute.Statement),
            _ => false
        };

    private static string ExpectedJurisdictions(string country) => country switch
    {
        "GB" => "use GB-ENG or GB-WLS",
        "US" => "use US or US-XX",
        _ => $"use {country}"
    };

    private static string ExpectedRoutes(
        string country,
        CreatorSellerType sellerType) => country switch
    {
        "GB" => sellerType == CreatorSellerType.Business
            ? "use Statement or UkSelfBilling"
            : "use Statement",
        "US" => "use UsSettlement",
        _ when country is "CH" || EuCountries.Contains(country) =>
            sellerType == CreatorSellerType.Business
                ? "use CreatorInvoice"
                : "use Statement",
        _ => "no paid route exists for that country"
    };
}
