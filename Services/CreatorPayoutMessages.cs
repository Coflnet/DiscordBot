using System.Globalization;

namespace Coflnet.Discord;

/// <summary>Pure formatting and encoding helpers for the creator payout commands.</summary>
public static class CreatorPayoutMessages
{
    private const string ModalPrefix = "creator-payout";

    public static string Eur(long cents) =>
        string.Create(CultureInfo.InvariantCulture, $"{cents / 100m:F2}");

    /// <summary>Custom id carrying user, invoice number and optional amount; well below the 100 char limit.</summary>
    public const string GenerateMarker = "-";

    /// <summary>A null invoice number is encoded as the marker: the handler derives it from the resolved account.</summary>
    public static string EncodeModalId(ulong userId, string? invoiceNumber, long? cents) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ModalPrefix}:{userId}:{invoiceNumber ?? GenerateMarker}:{(cents == null ? "-" : cents.Value.ToString(CultureInfo.InvariantCulture))}");

    public static bool TryDecodeModalId(
        string userIdText,
        string invoiceNumber,
        string amountText,
        out ulong userId,
        out long? cents,
        out string? decodedInvoiceNumber)
    {
        cents = null;
        userId = 0;
        decodedInvoiceNumber = invoiceNumber == GenerateMarker ? null : invoiceNumber;
        if (!ulong.TryParse(userIdText, NumberStyles.None, CultureInfo.InvariantCulture, out userId)
            || userId == 0
            || decodedInvoiceNumber != null
                && CreatorPayoutDocumentService.ValidateInvoiceNumber(decodedInvoiceNumber) != null)
            return false;
        if (amountText == "-")
            return true;
        if (!long.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
            return false;
        cents = parsed;
        return true;
    }

    public static string PayoutRequestReply(
        long cents,
        string creatorName,
        IEnumerable<string> bankLines,
        string invoiceNumber,
        string accountId,
        string ledgerReference,
        string sha256,
        long remainingAvailableCents,
        bool alreadyExisted = false,
        string? requestedBy = null,
        PayoutRoute? route = null) =>
        $"""
        Pay **{Eur(cents)} EUR** to {Safe(creatorName)}
        ```
        {string.Join("\n", bankLines.Select(Safe))}
        ```
        Document: {(route ?? PayoutRoute.UkSelfBilling).DocumentKind} (route {(route ?? PayoutRoute.UkSelfBilling).Route})
        Invoice number: `{invoiceNumber}`
        Account: `{accountId}`
        Ledger reference: `{ledgerReference}`{(alreadyExisted ? " (already existed)" : "")}
        PDF sha256: `{sha256}`
        Remaining available balance: **{Eur(remainingAvailableCents)} EUR**{(requestedBy == null ? "" : $"\nRequested by: {requestedBy}")}
        Pay the creator, then run `/creator payout-complete`.
        """;

    public static string RequestedBy(ulong requesterId, ulong targetId) =>
        requesterId == targetId
            ? $"<@{targetId}> (`{targetId}`)"
            : $"reviewer <@{requesterId}> on behalf of <@{targetId}> (`{targetId}`)";

    /// <summary>What the creator sees: no booking data, hash or ledger reference.</summary>
    public static string CreatorConfirmation(long cents, string invoiceNumber, bool teamNotified = true, PayoutRoute? route = null) =>
        $"""
        Your payout request for **{Eur(cents)} EUR** is booked and the amount is reserved (invoice `{invoiceNumber}`). It will be reviewed and paid to the bank details you entered. {(route ?? PayoutRoute.UkSelfBilling).AttachedSentence}
        {(route ?? PayoutRoute.UkSelfBilling).AcceptanceSentence}{(teamNotified ? "" : $"\nWe could not notify the team automatically. Please contact support and quote invoice `{invoiceNumber}`.")}
        """;

    public static string PayoutCompleteReply(
        long paidCents,
        string invoiceNumber,
        string paymentReference,
        RewardBalanceInfo balance) =>
        $"""
        Booked as paid: **{Eur(paidCents)} EUR** for invoice `{invoiceNumber}`
        Payment reference: `{paymentReference.Trim()}`
        New balance: outstanding {Eur(balance.OutstandingEurCents)} EUR, reserved {Eur(balance.ReservedEurCents)} EUR, available {Eur(balance.AvailableEurCents)} EUR
        """;

    public static IReadOnlyList<RewardLedgerEntryInfo> OpenRequests(
        IEnumerable<RewardLedgerEntryInfo> ledger)
    {
        var entries = ledger.ToList();
        return entries.Where(item => item.Kind == 4
                && !entries.Any(other => other.RelatedEntryId == item.Id && other.Kind is 5 or 6))
            .ToList();
    }

    public static string StatusReply(
        RewardBalanceInfo balance,
        CreatorReview? review,
        IEnumerable<RewardLedgerEntryInfo> ledger)
    {
        var reached = balance.AvailableEurCents >= balance.PayoutThresholdEurCents;
        var payable = reached
            ? $"**{Eur(balance.AvailableEurCents)} EUR** payable now"
            : $"0.00 EUR payable, {Eur(balance.PayoutThresholdEurCents - balance.AvailableEurCents)} EUR missing";
        var route = review == null
            ? "no onboarding review"
            : $"{review.TaxDocumentRoute}, country {review.ResidenceCountry} ({review.Status})";
        var open = OpenRequests(ledger);
        var openText = open.Count == 0
            ? "none"
            : string.Join("\n", open.Select(item =>
                $"- `{item.Reference}` {Eur(item.RemunerationEurCents)} EUR"));
        return $"""
            **Creator payout status (EUR)**
            Pending: **{Eur(balance.PendingEurCents)}**
            Outstanding: **{Eur(balance.OutstandingEurCents)}**
            Reserved for payout: **{Eur(balance.ReservedEurCents)}**
            Available: **{Eur(balance.AvailableEurCents)}**
            Payout threshold: **{Eur(balance.PayoutThresholdEurCents)}** ({(reached ? "reached" : "not reached")})
            Amount payable: {payable}
            Tax document route: {route}
            Open payout requests: {openText}
            """;
    }

    private static string Safe(string value) => value.Replace("```", "'''");
}
