using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Coflnet.Discord;

/// <summary>Our company details, taken from configuration.</summary>
public sealed record PayoutIssuer(string Name, string Address, string VatId);

/// <summary>Per-route differences of the generated payout document, as data.</summary>
public sealed record PayoutRoute(
    CreatorTaxDocumentRoute Route,
    string DocumentTitle,
    string DocumentKind,
    string EvidenceNamespace,
    string NumberPrefix,
    bool ReverseCharge,
    bool PrintCreatorVatId,
    string? StatementText,
    string? VatNote,
    string LineDescription,
    string PartyLabelCreator,
    string PartyLabelIssuer,
    string AttachedSentence,
    string AcceptanceSentence,
    CreatorSellerType? RequiredSellerType)
{
    public static readonly PayoutRoute UkSelfBilling = new(
        CreatorTaxDocumentRoute.UkSelfBilling,
        "Self-billing invoice (Gutschrift)", "self-billing invoice", "selfbilling", "SB", true, true,
        "This invoice is issued by the recipient of the service under a self-billing agreement.",
        "VAT 0.00 – Reverse charge – VAT is owed by the recipient of the service "
            + "(Steuerschuldnerschaft des Leistungsempfängers, § 13b UStG)",
        "Creator programme commission", "Supplier (creator)", "Recipient / issuer",
        "Your self-billing invoice is attached.",
        "By submitting this request you accept this self-billing invoice.",
        null);

    public static readonly PayoutRoute Statement = new(
        CreatorTaxDocumentRoute.Statement,
        "Payout statement (Abrechnung)", "payout statement", "statement", "PS", false, false,
        null,
        "No VAT is shown. The payee is recorded as a private individual and not as a VAT-registered business.",
        "Creator programme remuneration", "Payee (creator)", "Payer",
        "Your payout statement is attached.",
        "By submitting this request you confirm this payout statement.",
        CreatorSellerType.Individual);

    public static readonly IReadOnlyList<PayoutRoute> Supported = [UkSelfBilling, Statement];

    public static PayoutRoute? Find(CreatorTaxDocumentRoute route) =>
        Supported.FirstOrDefault(item => item.Route == route);

    /// <summary>Message naming the route for routes this command cannot pay; null when supported.</summary>
    public static string? Unsupported(CreatorTaxDocumentRoute route) => Find(route) != null ? null : route switch
    {
        CreatorTaxDocumentRoute.CreatorInvoice =>
            "The tax document route is CreatorInvoice: the creator must send their own invoice, this command cannot create one.",
        _ => $"The tax document route is {route}; only UkSelfBilling and Statement payouts are supported by this command."
    };
}

/// <summary>
/// Everything the payout document needs. The creator's name and address
/// live only in this in-memory record; nothing here is persisted or logged.
/// </summary>
public sealed record PayoutInvoiceInput(
    string InvoiceNumber,
    DateOnly IssueDate,
    string ServicePeriod,
    string CreatorName,
    IReadOnlyList<string> CreatorAddressLines,
    string CreatorCountry,
    string? CreatorVatId,
    IReadOnlyList<string> BankDetailLines,
    PayoutIssuer Issuer,
    long NetEurCents,
    string RewardAccountId,
    string LedgerReference,
    PayoutRoute? Route = null);

/// <summary>
/// Pure helpers and the PDF renderer for creator payouts. No I/O, no Discord.
/// </summary>
public static class CreatorPayoutDocumentService
{
    public const string UkSelfBillingRoute = "UkSelfBilling";
    private const int MaxDetailsLength = 4000;
    private static readonly Regex InvoiceNumberPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{2,39}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions BookingOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string LedgerReference(string invoiceNumber) =>
        $"discord:payout-request:{invoiceNumber}";

    public static string PayoutReference(string invoiceNumber) =>
        $"discord:payout:{invoiceNumber}";

    public static string EvidenceReference(string invoiceNumber, PayoutRoute? route = null) =>
        $"{(route ?? PayoutRoute.UkSelfBilling).EvidenceNamespace}:{invoiceNumber}";

    /// <summary>
    /// Deterministic per creator per UTC day: a second request that day hits the ledger's existing reference.
    /// </summary>
    public static string GenerateInvoiceNumber(string rewardAccountId, DateTime utcNow, PayoutRoute? route = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(route ?? PayoutRoute.UkSelfBilling).NumberPrefix}-{utcNow:yyyyMMdd}-{Sha256Hex(System.Text.Encoding.UTF8.GetBytes(rewardAccountId))[..6].ToUpperInvariant()}");

    /// <summary>
    /// Reviewer: any target and an optional own invoice number. Everyone else: only themselves, generated number.
    /// </summary>
    public static bool IsAuthorizedForPayoutRequest(
        ulong submitterId,
        ulong targetUserId,
        bool invoiceNumberSupplied) =>
        CreatorReviewCommands.IsAuthorizedReviewer(submitterId)
        || submitterId == targetUserId && !invoiceNumberSupplied;

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string? ValidateInvoiceNumber(string? value) =>
        value != null && InvoiceNumberPattern.IsMatch(value)
            ? null
            : "invoice_number must be 3-40 characters of letters, digits, `.`, `_` or `-` (for example SB-2026-0001).";

    /// <summary>
    /// Converts an EUR decimal to whole cents; null when it has sub-cent precision or is not positive.
    /// </summary>
    public static long? ToCents(decimal euro)
    {
        var cents = euro * 100m;
        return cents > 0 && cents == decimal.Truncate(cents)
            && cents <= long.MaxValue ? (long)cents : null;
    }

    /// <summary>Returns an error message, or null when the amount can be requested.</summary>
    public static string? ValidateAmount(
        long amountCents,
        long availableCents,
        long thresholdCents,
        bool friendly = false)
    {
        if (friendly && availableCents < thresholdCents)
            return $"You can request a payout from €{thresholdCents / 100m:F2}; you have €{availableCents / 100m:F2} available, so €{(thresholdCents - availableCents) / 100m:F2} more is needed."
                .Replace(',', '.');
        if (amountCents <= 0)
            return "The payout amount must be positive.";
        if (amountCents > availableCents)
            return $"The amount €{amountCents / 100m:F2} exceeds the available balance €{availableCents / 100m:F2}."
                .Replace(',', '.');
        if (amountCents < thresholdCents)
            return $"The amount €{amountCents / 100m:F2} is below the payout threshold €{thresholdCents / 100m:F2}."
                .Replace(',', '.');
        return null;
    }

    /// <summary>Only an approved, unexpired review on a supported payout route may be paid here.</summary>
    public static string? ValidateOnboarding(CreatorReview? review, DateTime utcNow, bool friendly = false)
    {
        if (friendly)
        {
            var problem = ValidateOnboarding(review, utcNow);
            return problem == null
                ? null
                : review is { Status: CreatorOnboardingStatus.Approved } && !(review.ValidUntilUtc <= utcNow)
                    ? PayoutRoute.Find(review.TaxDocumentRoute) == null
                        ? $"Your tax document route is {review.TaxDocumentRoute}, which self-service payouts do not support. Please contact support."
                        : "Your creator profile is inconsistent with your payout route. Please contact support."
                    : "Your creator approval is not active (missing, not approved or expired), so no payout can be requested. Please contact support.";
        }
        if (review == null)
            return "The creator has no onboarding review.";
        if (review.Status != CreatorOnboardingStatus.Approved)
            return $"The creator's onboarding review is {review.Status}, it must be Approved.";
        if (review.ValidUntilUtc <= utcNow)
            return $"The creator's approval expired {review.ValidUntilUtc:yyyy-MM-dd}.";
        if (PayoutRoute.Unsupported(review.TaxDocumentRoute) is { } unsupported)
            return unsupported;
        var route = PayoutRoute.Find(review.TaxDocumentRoute)!;
        if (route.RequiredSellerType is { } seller && review.SellerType != seller)
            return $"The review is inconsistent: route {route.Route} requires a {seller} seller but the review says {review.SellerType}. Update the review.";
        return null;
    }

    /// <summary>A VAT id may only be printed on routes that show creator VAT ids.</summary>
    public static string? ValidateCreatorVatId(PayoutRoute route, string? vatId) =>
        route.PrintCreatorVatId || string.IsNullOrWhiteSpace(vatId)
            ? null
            : $"A VAT id cannot be used on the {route.Route} route: a VAT-registered seller needs a different tax route (update the review).";

    public static string? ValidatePaymentReference(string? value)
    {
        value = value?.Trim();
        var separator = value?.IndexOf(':') ?? -1;
        return value != null && value.Length <= 190 && separator >= 1
            && separator < value.Length - 1 && !value.Any(char.IsWhiteSpace)
                ? null
                : "payment_reference must be namespaced like `paypal:TXID` or `bank:REF` (no spaces).";
    }

    public static IReadOnlyList<string> ParseAddress(string? value) =>
        (value ?? "").Replace("\\n", "\n").Split(
                [';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Default service period: first balance-affecting entry (Award, Correction)
    /// after the creator's last Payout, through today.
    /// </summary>
    public static string DefaultPeriod(
        IEnumerable<RewardLedgerEntryInfo> ledger,
        DateOnly today)
    {
        var entries = ledger.ToList();
        var lastPayout = entries.Where(item => item.Kind == 5)
            .Select(item => (DateTime?)item.CreatedAt).Max();
        var first = entries
            .Where(item => item.Kind is 2 or 3
                && (lastPayout == null || item.CreatedAt > lastPayout))
            .Select(item => (DateTime?)item.CreatedAt).Min();
        var start = first == null ? today : DateOnly.FromDateTime(first.Value);
        return Period(start, today);
    }

    public static string Period(DateOnly from, DateOnly to) =>
        string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}");

    /// <summary>The PayoutRequest DetailsJson; keys are exactly those SkyReferral validates.</summary>
    public static string BuildEvidenceJson(
        long grossCents,
        long creatorVatCents,
        long withholdingCents,
        long solidarityCents,
        string evidenceReference,
        string evidenceSha256)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["grossEurCents"] = grossCents,
            ["creatorVatEurCents"] = creatorVatCents,
            ["withholdingTaxEurCents"] = withholdingCents,
            ["solidaritySurchargeEurCents"] = solidarityCents,
            ["netEurCents"] = grossCents + creatorVatCents - withholdingCents - solidarityCents,
            ["payoutEvidenceReference"] = evidenceReference,
            ["payoutEvidenceSha256"] = evidenceSha256
        });
        if (json.Length > MaxDetailsLength)
            throw new InvalidOperationException("Payout evidence exceeds the ledger limit.");
        return json;
    }

    public static string BuildPayoutDetailsJson(string paymentReference) =>
        JsonSerializer.Serialize(new { paymentReference = paymentReference.Trim() });

    /// <summary>The machine-readable booking file for the accounting tool. Contains no creator identity.</summary>
    public static string BuildBookingJson(
        string invoiceNumber,
        DateOnly voucherDate,
        long grossCents,
        string rewardAccountId,
        string creatorCountry,
        string pdfSha256,
        PayoutRoute? route = null) =>
        JsonSerializer.Serialize(new BookingFile(
            1, "creator-payout", invoiceNumber, invoiceNumber,
            voucherDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "EUR", grossCents, 0, 0, 0, grossCents, rewardAccountId,
            creatorCountry, (route ?? PayoutRoute.UkSelfBilling).Route.ToString(),
            (route ?? PayoutRoute.UkSelfBilling).ReverseCharge,
            LedgerReference(invoiceNumber),
            new BookingEvidence($"{invoiceNumber}.pdf", pdfSha256)),
            BookingOptions);

    /// <summary>
    /// Finds the not yet paid or cancelled PayoutRequest for an invoice number.
    /// </summary>
    public static (RewardLedgerEntryInfo? Request, string? Error) FindOpenRequest(
        IEnumerable<RewardLedgerEntryInfo> ledger,
        string invoiceNumber)
    {
        var entries = ledger.ToList();
        var request = entries.FirstOrDefault(item => item.Kind == 4
            && item.Reference == LedgerReference(invoiceNumber));
        if (request == null)
            return (null, $"No payout request with reference `{LedgerReference(invoiceNumber)}` exists for this account.");
        if (entries.Any(item => item.RelatedEntryId == request.Id && item.Kind is 5 or 6))
            return (null, "That payout request is already paid or cancelled.");
        return (request, null);
    }

    private const string RequestReferencePrefix = "discord:payout-request:";

    /// <summary>
    /// Picks the payout request to complete. With an invoice number this is
    /// <see cref="FindOpenRequest"/>; without, the creator's single open request created by this bot.
    /// Several open requests, or a foreign reference, are listed instead of guessed.
    /// </summary>
    public static (RewardLedgerEntryInfo? Request, string? InvoiceNumber, string? Error) SelectOpenRequest(
        IEnumerable<RewardLedgerEntryInfo> ledger,
        string? invoiceNumber)
    {
        var entries = ledger.ToList();
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
        {
            var (found, error) = FindOpenRequest(entries, invoiceNumber);
            return (found, found == null ? null : invoiceNumber, error);
        }
        var open = CreatorPayoutMessages.OpenRequests(entries);
        var completable = open.Where(item => item.Reference.StartsWith(RequestReferencePrefix, StringComparison.Ordinal)
            && item.Reference.Length > RequestReferencePrefix.Length).ToList();
        if (open.Count == 0)
            return (null, null, "No open payout request exists for this account.");
        if (completable.Count == 1 && open.Count == 1)
            return (completable[0], completable[0].Reference[RequestReferencePrefix.Length..], null);
        var list = string.Join("\n", open.Select(item => completable.Contains(item)
            ? $"- `{item.Reference[RequestReferencePrefix.Length..]}`: {CreatorPayoutMessages.Eur(item.RemunerationEurCents)} EUR"
            : $"- `{item.Reference}`: {CreatorPayoutMessages.Eur(item.RemunerationEurCents)} EUR (not completable here, foreign reference)"));
        return (null, null, completable.Count == 0
            ? $"The open payout request(s) were not created by this bot and cannot be completed here:\n{list}"
            : $"There are several open payout requests, pass `invoice_number` to choose one:\n{list}");
    }

    public static byte[] RenderPdf(PayoutInvoiceInput input)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var route = input.Route ?? PayoutRoute.UkSelfBilling;
        var amount = FormattableString.Invariant($"{input.NetEurCents / 100m:F2}");
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(style => style.FontSize(10));
            page.Header().Column(column =>
            {
                column.Item().Text(route.DocumentTitle).FontSize(20).Bold();
                if (route.StatementText != null)
                    column.Item().Text(route.StatementText).Italic();
            });
            page.Content().PaddingVertical(15).Column(column =>
            {
                column.Spacing(12);
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.ConstantColumn(130); c.RelativeColumn(); });
                    Row(table, route.Route == CreatorTaxDocumentRoute.Statement ? "Statement number" : "Invoice number", input.InvoiceNumber);
                    Row(table, "Issue date", input.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    Row(table, "Service period", input.ServicePeriod);
                });
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(party =>
                    {
                        party.Item().Text(route.PartyLabelCreator).Bold();
                        party.Item().Text(input.CreatorName);
                        foreach (var line in input.CreatorAddressLines)
                            party.Item().Text(line);
                        party.Item().Text($"Country: {input.CreatorCountry}");
                        if (route.PrintCreatorVatId && !string.IsNullOrWhiteSpace(input.CreatorVatId))
                            party.Item().Text($"VAT ID: {input.CreatorVatId}");
                    });
                    row.RelativeItem().Column(party =>
                    {
                        party.Item().Text(route.PartyLabelIssuer).Bold();
                        party.Item().Text(input.Issuer.Name);
                        foreach (var line in ParseAddress(input.Issuer.Address))
                            party.Item().Text(line);
                        party.Item().Text($"VAT ID: {input.Issuer.VatId}");
                    });
                });
                column.Item().Background(Colors.Grey.Lighten3).Padding(8)
                    .Text($"Amount payable: EUR {amount}").FontSize(16).Bold();
                column.Item().Column(bank =>
                {
                    bank.Item().Text("Payment details (payee: creator)").Bold();
                    foreach (var line in input.BankDetailLines)
                        bank.Item().Text(line);
                });
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(100); });
                    table.Header(header =>
                    {
                        header.Cell().BorderBottom(1).Text("Description").Bold();
                        header.Cell().BorderBottom(1).AlignRight().Text("Net amount (EUR)").Bold();
                    });
                    table.Cell().Text(route.LineDescription);
                    table.Cell().AlignRight().Text(amount);
                    if (route.ReverseCharge)
                    {
                        table.Cell().Text(route.VatNote!);
                        table.Cell().AlignRight().Text("0.00");
                    }
                    table.Cell().BorderTop(1).Text("Total payable").Bold();
                    table.Cell().BorderTop(1).AlignRight().Text($"EUR {amount}").Bold();
                });
            });
            page.Footer().Column(column =>
            {
                if (!route.ReverseCharge && route.VatNote != null)
                    column.Item().Text(route.VatNote).FontSize(8);
                column.Item().Text($"Reward account: {input.RewardAccountId}").FontSize(8);
                column.Item().Text($"Ledger reference: {input.LedgerReference}").FontSize(8);
            });
        })).GeneratePdf();
    }

    private static void Row(TableDescriptor table, string label, string value)
    {
        table.Cell().Text(label).Bold();
        table.Cell().Text(value);
    }

    private sealed record BookingFile(
        int SchemaVersion,
        string Provider,
        string PayoutId,
        string VoucherNumber,
        string VoucherDate,
        string Currency,
        long GrossCents,
        long CreatorVatCents,
        long WithholdingTaxCents,
        long SolidaritySurchargeCents,
        long NetCents,
        string RewardAccountId,
        string CreatorCountry,
        string TaxDocumentRoute,
        bool ReverseCharge,
        string LedgerReference,
        BookingEvidence Evidence);

    private sealed record BookingEvidence(string Pdf, string PdfSha256);
}
