using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;

namespace Coflnet.Discord;

public sealed partial class CreatorReviewCommands
{
    [SlashCommand("payout-request", "Book a creator payout and generate the self-billing invoice")]
    public async Task PayoutRequest(
        [Summary("user", "The creator's Discord user")] IUser user,
        [Summary("invoice_number", "Our self-billing number, e.g. SB-2026-0001")] string invoiceNumber,
        [Summary("amount", "EUR amount; defaults to the full available balance")] decimal? amount = null)
    {
        if (!await RequireReviewer())
            return;
        invoiceNumber = invoiceNumber.Trim();
        var error = CreatorPayoutDocumentService.ValidateInvoiceNumber(invoiceNumber);
        long? cents = null;
        if (error == null && amount != null)
        {
            cents = CreatorPayoutDocumentService.ToCents(amount.Value);
            if (cents == null)
                error = "amount must be a positive EUR value with at most two decimals.";
        }
        if (error != null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }
        // A modal must be the first response, so no Defer before it.
        await RespondWithModalAsync<PayoutModal>(
            CreatorPayoutMessages.EncodeModalId(user.Id, invoiceNumber, cents));
    }

    public sealed class PayoutModal : IModal
    {
        public string Title => "Creator payout details";

        [InputLabel("Creator legal name")]
        [ModalTextInput("name", TextInputStyle.Short, maxLength: 100)]
        public string CreatorName { get; set; } = "";

        [InputLabel("Postal address (one line per row)")]
        [ModalTextInput("address", TextInputStyle.Paragraph, maxLength: 500)]
        public string Address { get; set; } = "";

        [InputLabel("Bank details / PayPal")]
        [ModalTextInput("bank", TextInputStyle.Paragraph, "Holder, IBAN or sort code + account, BIC, bank name", maxLength: 500)]
        public string BankDetails { get; set; } = "";

        [InputLabel("Creator VAT id (optional)")]
        [RequiredInput(false)]
        [ModalTextInput("vat", TextInputStyle.Short, maxLength: 40)]
        public string? VatId { get; set; }

        [InputLabel("Service period (optional)")]
        [RequiredInput(false)]
        [ModalTextInput("period", TextInputStyle.Short, "Default: first unpaid award through today", maxLength: 60)]
        public string? Period { get; set; }
    }

    [ModalInteraction("creator-payout:*:*:*", true)]
    public async Task PayoutRequestSubmitted(
        string userIdText,
        string invoiceNumber,
        string amountText,
        PayoutModal modal)
    {
        // The slash command is gated too, but a modal custom id can be replayed.
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            if (!CreatorPayoutMessages.TryDecodeModalId(
                    userIdText, invoiceNumber, amountText,
                    out var userId, out var requestedCents))
            {
                await FollowupAsync("The payout form is invalid, run the command again.", ephemeral: true);
                return;
            }
            var addressLines = CreatorPayoutDocumentService.ParseAddress(modal.Address);
            var bankLines = CreatorPayoutDocumentService.ParseAddress(modal.BankDetails);
            string? error = null;
            if (string.IsNullOrWhiteSpace(modal.CreatorName) || addressLines.Count == 0 || bankLines.Count == 0)
                error = "name, address and bank details are required.";
            var issuer = rewards.GetIssuer();
            if (error == null && issuer == null)
                error = "Issuer details are not configured (CREATOR_PAYOUT:ISSUER_NAME, ISSUER_ADDRESS, ISSUER_VAT_ID).";
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var account = await persistence.GetDiscordAccountInfo(userId);
            if (string.IsNullOrWhiteSpace(account?.UserId))
            {
                await FollowupAsync("That user has not linked an account (`/update-mc-user`).", ephemeral: true);
                return;
            }
            var review = await onboarding.GetLatest(account.UserId);
            error = CreatorPayoutDocumentService.ValidateOnboarding(review, DateTime.UtcNow);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }
            var balance = await rewards.GetBalance(account.UserId);
            var cents = requestedCents ?? balance.AvailableEurCents;
            error = CreatorPayoutDocumentService.ValidateAmount(
                cents, balance.AvailableEurCents, balance.PayoutThresholdEurCents);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var period = modal.Period;
            if (string.IsNullOrWhiteSpace(period))
                period = CreatorPayoutDocumentService.DefaultPeriod(
                    await rewards.GetLedger(account.UserId), today);
            var reference = CreatorPayoutDocumentService.LedgerReference(invoiceNumber);
            var pdf = CreatorPayoutDocumentService.RenderPdf(new PayoutInvoiceInput(
                invoiceNumber, today, period.Trim(), modal.CreatorName.Trim(), addressLines,
                review!.ResidenceCountry, string.IsNullOrWhiteSpace(modal.VatId) ? null : modal.VatId.Trim(),
                bankLines, issuer!, cents, account.UserId, reference));
            var sha = CreatorPayoutDocumentService.Sha256Hex(pdf);
            var booking = CreatorPayoutDocumentService.BuildBookingJson(
                invoiceNumber, today, cents, account.UserId, review.ResidenceCountry, sha);

            RewardGrantResult result;
            try
            {
                result = await rewards.RecordPayout(new RewardGrantRequest(
                    reference, account.UserId, 4, null, cents, null,
                    $"Creator payout requested, self-billing invoice {invoiceNumber}",
                    CreatorPayoutDocumentService.BuildEvidenceJson(
                        cents, 0, 0, 0,
                        CreatorPayoutDocumentService.EvidenceReference(invoiceNumber), sha)));
            }
            catch (HttpRequestException e)
            {
                await FollowupAsync($"The ledger rejected the payout request, nothing was booked: {e.Message}", ephemeral: true);
                return;
            }

            if (!result.Created)
            {
                // The ledger keeps the hash of the first PDF; a regenerated one would not match it.
                await FollowupAsync(
                    $"A payout request for invoice `{invoiceNumber}` already exists in the ledger. Nothing new was booked and no document is attached, "
                    + "because a regenerated PDF would not match the stored evidence hash. Use a new invoice number (cancel the existing request first if it should not be paid).",
                    ephemeral: true);
                return;
            }

            using var pdfStream = new MemoryStream(pdf);
            using var bookingStream = new MemoryStream(Encoding.UTF8.GetBytes(booking));
            await FollowupWithFilesAsync(
                [
                    new FileAttachment(pdfStream, $"{invoiceNumber}.pdf"),
                    new FileAttachment(bookingStream, $"{invoiceNumber}.booking.json")
                ],
                CreatorPayoutMessages.PayoutRequestReply(
                    cents, modal.CreatorName.Trim(), bankLines, invoiceNumber,
                    account.UserId, reference, sha,
                    balance.AvailableEurCents - cents, !result.Created),
                ephemeral: true,
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception e)
        {
            // Never log the exception message or form content: it may echo creator identity.
            logger.LogError("Creator payout request failed for Discord user {userIdText}: {type}", userIdText, e.GetType().Name);
            await FollowupAsync($"The payout request failed: {e.Message}", ephemeral: true);
        }
    }

    [SlashCommand("payout-status", "Show a creator's payout balance and open payout requests")]
    public async Task PayoutStatus(
        [Summary("user", "The creator's Discord user")] IUser user)
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            var account = await persistence.GetDiscordAccountInfo(user.Id);
            if (string.IsNullOrWhiteSpace(account?.UserId))
            {
                await FollowupAsync("That user has not linked an account (`/update-mc-user`).", ephemeral: true);
                return;
            }
            var balance = await rewards.GetBalance(account.UserId);
            var ledger = await rewards.GetLedger(account.UserId);
            var review = await onboarding.GetLatest(account.UserId);
            await FollowupAsync(
                CreatorPayoutMessages.StatusReply(balance, review, ledger),
                ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Creator payout status failed for Discord user {userId}", user.Id);
            await FollowupAsync("The payout status could not be loaded.", ephemeral: true);
        }
    }

    [SlashCommand("payout-complete", "Record that a booked creator payout was paid")]
    public async Task PayoutComplete(
        [Summary("user", "The creator's Discord user")] IUser user,
        [Summary("invoice_number", "The invoice number of the payout request")] string invoiceNumber,
        [Summary("payment_reference", "Namespaced payment reference, e.g. paypal:TXID or bank:REF")] string paymentReference)
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            invoiceNumber = invoiceNumber.Trim();
            var error = CreatorPayoutDocumentService.ValidateInvoiceNumber(invoiceNumber)
                ?? CreatorPayoutDocumentService.ValidatePaymentReference(paymentReference);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }
            var account = await persistence.GetDiscordAccountInfo(user.Id);
            if (string.IsNullOrWhiteSpace(account?.UserId))
            {
                await FollowupAsync("That user has not linked an account (`/update-mc-user`).", ephemeral: true);
                return;
            }
            var (request, notFound) = CreatorPayoutDocumentService.FindOpenRequest(
                await rewards.GetLedger(account.UserId), invoiceNumber);
            if (request == null)
            {
                await FollowupAsync(notFound!, ephemeral: true);
                return;
            }
            try
            {
                await rewards.RecordPayout(new RewardGrantRequest(
                    CreatorPayoutDocumentService.PayoutReference(invoiceNumber),
                    account.UserId, 5, null, -request.RemunerationEurCents, null,
                    $"Creator payout paid, self-billing invoice {invoiceNumber}",
                    CreatorPayoutDocumentService.BuildPayoutDetailsJson(paymentReference),
                    request.Id));
            }
            catch (HttpRequestException e)
            {
                await FollowupAsync($"The ledger rejected the payout: {e.Message}", ephemeral: true);
                return;
            }
            var balance = await rewards.GetBalance(account.UserId);
            await FollowupAsync(
                CreatorPayoutMessages.PayoutCompleteReply(
                    request.RemunerationEurCents, invoiceNumber, paymentReference, balance),
                ephemeral: true);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Creator payout completion failed for Discord user {userId}", user.Id);
            await FollowupAsync($"The payout could not be recorded: {e.Message}", ephemeral: true);
        }
    }
}
