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

/// <summary>
/// Everything the self-billing invoice needs. The creator's name and address
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
    string LedgerReference);

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

    public static string EvidenceReference(string invoiceNumber) =>
        $"selfbilling:{invoiceNumber}";

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
        long thresholdCents)
    {
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

    /// <summary>Only an approved, unexpired review on the UK self-billing route may be paid here.</summary>
    public static string? ValidateOnboarding(CreatorReview? review, DateTime utcNow)
    {
        if (review == null)
            return "The creator has no onboarding review.";
        if (review.Status != CreatorOnboardingStatus.Approved)
            return $"The creator's onboarding review is {review.Status}, it must be Approved.";
        if (review.ValidUntilUtc <= utcNow)
            return $"The creator's approval expired {review.ValidUntilUtc:yyyy-MM-dd}.";
        if (review.TaxDocumentRoute != CreatorTaxDocumentRoute.UkSelfBilling)
            return $"The tax document route is {review.TaxDocumentRoute}; only UkSelfBilling payouts are supported by this command.";
        return null;
    }

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
        string pdfSha256) =>
        JsonSerializer.Serialize(new BookingFile(
            1, "creator-payout", invoiceNumber, invoiceNumber,
            voucherDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "EUR", grossCents, 0, 0, 0, grossCents, rewardAccountId,
            creatorCountry, UkSelfBillingRoute, true,
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

    public static byte[] RenderPdf(PayoutInvoiceInput input)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var amount = FormattableString.Invariant($"{input.NetEurCents / 100m:F2}");
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(style => style.FontSize(10));
            page.Header().Column(column =>
            {
                column.Item().Text("Self-billing invoice (Gutschrift)").FontSize(20).Bold();
                column.Item().Text(
                    "This invoice is issued by the recipient of the service under a self-billing agreement.")
                    .Italic();
            });
            page.Content().PaddingVertical(15).Column(column =>
            {
                column.Spacing(12);
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.ConstantColumn(130); c.RelativeColumn(); });
                    Row(table, "Invoice number", input.InvoiceNumber);
                    Row(table, "Issue date", input.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    Row(table, "Service period", input.ServicePeriod);
                });
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(party =>
                    {
                        party.Item().Text("Supplier (creator)").Bold();
                        party.Item().Text(input.CreatorName);
                        foreach (var line in input.CreatorAddressLines)
                            party.Item().Text(line);
                        party.Item().Text($"Country: {input.CreatorCountry}");
                        if (!string.IsNullOrWhiteSpace(input.CreatorVatId))
                            party.Item().Text($"VAT ID: {input.CreatorVatId}");
                    });
                    row.RelativeItem().Column(party =>
                    {
                        party.Item().Text("Recipient / issuer").Bold();
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
                    table.Cell().Text("Creator programme commission");
                    table.Cell().AlignRight().Text(amount);
                    table.Cell().Text("VAT 0.00 – Reverse charge – VAT is owed by the recipient of the service "
                        + "(Steuerschuldnerschaft des Leistungsempfängers, § 13b UStG)");
                    table.Cell().AlignRight().Text("0.00");
                    table.Cell().BorderTop(1).Text("Total payable").Bold();
                    table.Cell().BorderTop(1).AlignRight().Text($"EUR {amount}").Bold();
                });
            });
            page.Footer().Column(column =>
            {
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
