using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;

namespace Coflnet.Discord;

public sealed partial class CreatorReviewCommands
{
    [SlashCommand("payout-request", "Request a payout of your creator earnings (the payout document is generated for you)")]
    public async Task PayoutRequest(
        [Summary("user", "Reviewer only: the creator's Discord user; defaults to you")] IUser? user = null,
        [Summary("invoice_number", "Reviewer only: document number, e.g. SB-2026-0001")] string invoiceNumber = "",
        [Summary("amount", "EUR amount; defaults to your full available balance")] decimal? amount = null,
        [Summary("name", "Creator legal name. Give name, address and bank_details together to skip the form")] string name = "",
        [Summary("address", "Creator postal address; separate lines with ;")] string address = "",
        [Summary("bank_details", "Creator bank details or PayPal; separate lines with ;")] string bankDetails = "",
        [Summary("vat_id", "Creator VAT id (optional)")] string vatId = "",
        [Summary("period", "Service period text (optional); default: first unpaid award through today")] string period = "")
    {
        var target = user?.Id ?? Context.User.Id;
        invoiceNumber = invoiceNumber.Trim();
        if (!CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(
                Context.User.Id, target, invoiceNumber.Length > 0))
        {
            await RespondAsync("You are not allowed to request a payout for someone else or to choose the invoice number.", ephemeral: true);
            return;
        }
        string? error = invoiceNumber.Length == 0
            ? null
            : CreatorPayoutDocumentService.ValidateInvoiceNumber(invoiceNumber);
        long? cents = null;
        if (error == null && amount != null)
        {
            cents = CreatorPayoutDocumentService.ToCents(amount.Value);
            if (cents == null)
                error = "amount must be a positive EUR value with at most two decimals.";
        }
        var missing = new[] { ("name", name), ("address", address), ("bank_details", bankDetails) }
            .Where(item => string.IsNullOrWhiteSpace(item.Item2)).Select(item => item.Item1).ToList();
        if (error == null && missing.Count is > 0 and < 3)
            error = $"Give name, address and bank_details together to skip the form, or none of them to fill in the form. Missing: {string.Join(", ", missing)}.";
        if (error != null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }
        if (missing.Count == 0)
        {
            await ProcessPayoutRequest(target, invoiceNumber.Length == 0 ? null : invoiceNumber, cents,
                name, address, bankDetails, vatId, period);
            return;
        }
        // A modal must be the first response, so no Defer (and no slow eligibility lookup) before it;
        // eligibility is checked on submit.
        await RespondWithModalAsync<PayoutModal>(
            CreatorPayoutMessages.EncodeModalId(
                target, invoiceNumber.Length == 0 ? null : invoiceNumber, cents));
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
        string invoiceNumberText,
        string amountText,
        PayoutModal modal)
    {
        if (!CreatorPayoutMessages.TryDecodeModalId(
                userIdText, invoiceNumberText, amountText,
                out var userId, out var requestedCents, out var suppliedInvoice))
        {
            await RespondAsync("The payout form is invalid, run the command again.", ephemeral: true);
            return;
        }
        await ProcessPayoutRequest(userId, suppliedInvoice, requestedCents,
            modal.CreatorName, modal.Address, modal.BankDetails, modal.VatId, modal.Period);
    }

    /// <summary>Shared by the slash-option path and the modal submit: authorise, defer, check, book, deliver.</summary>
    private async Task ProcessPayoutRequest(
        ulong userId,
        string? suppliedInvoice,
        long? requestedCents,
        string creatorName,
        string address,
        string bankDetails,
        string? vatId,
        string? period)
    {
        // Never trust a custom id or option alone: a creator may only file for their own account.
        if (!CreatorPayoutDocumentService.IsAuthorizedForPayoutRequest(
                Context.User.Id, userId, suppliedInvoice != null))
        {
            await RespondAsync("You are not allowed to request this payout.", ephemeral: true);
            return;
        }
        if (creatorName.Length > 100 || address.Length > 500 || bankDetails.Length > 500
            || vatId?.Length > 40 || period?.Length > 60)
        {
            await RespondAsync("A value is too long: name 100, address 500, bank details 500, VAT id 40, period 60 characters.", ephemeral: true);
            return;
        }
        var byReviewer = IsAuthorizedReviewer(Context.User.Id);
        await DeferAsync(ephemeral: true);
        try
        {
            var addressLines = CreatorPayoutDocumentService.ParseAddress(address);
            var bankLines = CreatorPayoutDocumentService.ParseAddress(bankDetails);
            string? error = null;
            if (string.IsNullOrWhiteSpace(creatorName) || addressLines.Count == 0 || bankLines.Count == 0)
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
            error = CreatorPayoutDocumentService.ValidateOnboarding(review, DateTime.UtcNow, !byReviewer);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }
            var balance = await rewards.GetBalance(account.UserId);
            var cents = requestedCents ?? balance.AvailableEurCents;
            error = CreatorPayoutDocumentService.ValidateAmount(
                cents, balance.AvailableEurCents, balance.PayoutThresholdEurCents, !byReviewer);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }

            var route = PayoutRoute.Find(review!.TaxDocumentRoute)!;
            error = CreatorPayoutDocumentService.ValidateCreatorVatId(route, vatId);
            if (error != null)
            {
                await FollowupAsync(error, ephemeral: true);
                return;
            }
            var invoiceNumber = suppliedInvoice
                ?? CreatorPayoutDocumentService.GenerateInvoiceNumber(account.UserId, DateTime.UtcNow, route);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (string.IsNullOrWhiteSpace(period))
                period = CreatorPayoutDocumentService.DefaultPeriod(
                    await rewards.GetLedger(account.UserId), today);
            var reference = CreatorPayoutDocumentService.LedgerReference(invoiceNumber);
            var pdf = CreatorPayoutDocumentService.RenderPdf(new PayoutInvoiceInput(
                invoiceNumber, today, period.Trim(), creatorName.Trim(), addressLines,
                review.ResidenceCountry, string.IsNullOrWhiteSpace(vatId) ? null : vatId.Trim(),
                bankLines, issuer!, cents, account.UserId, reference, route));
            var sha = CreatorPayoutDocumentService.Sha256Hex(pdf);
            var booking = CreatorPayoutDocumentService.BuildBookingJson(
                invoiceNumber, today, cents, account.UserId, review.ResidenceCountry, sha, route);

            // Open the reviewer DM before booking so an unreachable reviewer fails with nothing booked.
            IDMChannel? reviewerDm = null;
            if (!byReviewer)
            {
                try
                {
                    var reviewer = await Context.Client.GetUserAsync(ReviewerId);
                    reviewerDm = await reviewer.CreateDMChannelAsync();
                }
                catch (Exception e)
                {
                    logger.LogError("Reviewer DM unavailable for payout request {account}: {type}", account.UserId, e.GetType().Name);
                }
                if (reviewerDm == null)
                {
                    await FollowupAsync("Payouts are temporarily unavailable, please contact support. Nothing was booked.", ephemeral: true);
                    return;
                }
            }

            RewardGrantResult result;
            try
            {
                result = await rewards.RecordPayout(new RewardGrantRequest(
                    reference, account.UserId, 4, null, cents, null,
                    $"Creator payout requested, {route.DocumentKind} {invoiceNumber}",
                    CreatorPayoutDocumentService.BuildEvidenceJson(
                        cents, 0, 0, 0,
                        CreatorPayoutDocumentService.EvidenceReference(invoiceNumber, route), sha)));
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
                    byReviewer
                        ? $"A payout request for invoice `{invoiceNumber}` already exists in the ledger. Nothing new was booked and no document is attached, "
                            + "because a regenerated PDF would not match the stored evidence hash. Use a new invoice number (cancel the existing request first if it should not be paid)."
                        : $"A payout request was already filed today (invoice `{invoiceNumber}`). Nothing new was booked. Please contact support if it needs changing.",
                    ephemeral: true);
                return;
            }

            var reply = CreatorPayoutMessages.PayoutRequestReply(
                cents, creatorName.Trim(), bankLines, invoiceNumber,
                account.UserId, reference, sha,
                balance.AvailableEurCents - cents, false,
                CreatorPayoutMessages.RequestedBy(Context.User.Id, userId), route);
            if (byReviewer)
            {
                using var pdfStream = new MemoryStream(pdf);
                using var bookingStream = new MemoryStream(Encoding.UTF8.GetBytes(booking));
                await FollowupWithFilesAsync(
                    [
                        new FileAttachment(pdfStream, $"{invoiceNumber}.pdf"),
                        new FileAttachment(bookingStream, $"{invoiceNumber}.booking.json")
                    ],
                    reply, ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }

            var notified = true;
            using (var dmPdf = new MemoryStream(pdf))
            using (var dmBooking = new MemoryStream(Encoding.UTF8.GetBytes(booking)))
            {
                try
                {
                    await reviewerDm!.SendFilesAsync(
                        [
                            new FileAttachment(dmPdf, $"{invoiceNumber}.pdf"),
                            new FileAttachment(dmBooking, $"{invoiceNumber}.booking.json")
                        ],
                        reply, allowedMentions: AllowedMentions.None);
                }
                catch (Exception e)
                {
                    notified = false;
                    logger.LogError("Reviewer DM failed after booking invoice {invoice}, account {account}: {type}",
                        invoiceNumber, account.UserId, e.GetType().Name);
                }
            }
            using var creatorPdf = new MemoryStream(pdf);
            await FollowupWithFileAsync(
                new FileAttachment(creatorPdf, $"{invoiceNumber}.pdf"),
                CreatorPayoutMessages.CreatorConfirmation(cents, invoiceNumber, notified, route),
                ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception e)
        {
            // Never log the exception message or form content: it may echo creator identity.
            logger.LogError("Creator payout request failed for Discord user {userId}: {type}", userId, e.GetType().Name);
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
                    $"Creator payout paid, document {invoiceNumber}",
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
