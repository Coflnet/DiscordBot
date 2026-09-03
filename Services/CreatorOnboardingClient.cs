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
        response.EnsureSuccessStatusCode();
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
        response.EnsureSuccessStatusCode();
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
        response.EnsureSuccessStatusCode();
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

    private static void RequireCoflnetUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uri.Host, "coflnet.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Legal documents must use the Coflnet HTTPS origin.");
    }
}

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
