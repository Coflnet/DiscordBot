using System.Linq;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Interactions;

namespace Coflnet.Discord;

[Group("reward", "Manage approved reward grants")]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[CommandContextType(InteractionContextType.Guild)]
public sealed class RewardCommands(
    RewardLedgerClient rewards,
    ILogger<RewardCommands> logger) : InteractionModuleBase
{
    private const int PendingKind = 1;
    private const int AwardKind = 2;
    private const int CancellationKind = 6;

    [SlashCommand("record", "Record an approved EUR report or referral reward")]
    public async Task Record(
        [Summary("recipient", "Coflnet reward account ID or reporter email")] string recipient,
        [Summary("source", "Approved reward source")] RewardGrantSource source,
        [Summary("amount-cents", "Positive EUR amount in cents")] long amountEurCents,
        [Summary("offer-version", "Version of the accepted reward offer")] string offerVersion,
        [Summary("reference", "Stable unique source reference; reuse it when retrying")] string reference,
        [Summary("reason", "Concise reason for approving the reward")] string reason,
        [Summary("evidence", "Optional opaque evidence reference; never raw identity data")] string evidenceReference = "",
        [Summary("pending", "Entry ID of a Pending reward this award approves; must match its amount, source and offer-version")] string pending = "")
    {
        if (!CreatorReviewCommands.IsAuthorizedReviewer(Context.User.Id))
        {
            await RespondAsync(
                "You are not allowed to record rewards.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        if (amountEurCents <= 0)
        {
            await FollowupAsync(
                "amount-cents must be positive.", ephemeral: true);
            return;
        }

        try
        {
            var rewardAccountId = RecipientId(recipient);
            var relatedEntryId = OptionalEntryId(pending);
            var ledgerReference = Reference(source, reference);
            offerVersion = LedgerText(offerVersion, "offer-version", 128);
            reason = LedgerText(reason, "reason", 300);
            var details = JsonSerializer.Serialize(new
            {
                reviewerDiscordId = Context.User.Id.ToString(),
                evidenceReference = EvidenceReference(evidenceReference)
            });
            var result = await rewards.Record(new(
                ledgerReference,
                rewardAccountId,
                AwardKind,
                source,
                amountEurCents,
                offerVersion,
                reason,
                details,
                relatedEntryId));
            logger.LogInformation(
                "Reward {entryId} ({reference}) {action} for account {rewardAccountId} by Discord reviewer {reviewerId}",
                result.Entry.Id, ledgerReference,
                result.Created ? "created" : "already existed",
                rewardAccountId, Context.User.Id);
            await FollowupAsync(
                $"{(result.Created ? "Recorded" : "Already recorded")} `{ledgerReference}` as `{result.Entry.Id}`: **{amountEurCents} EUR cents** for `{rewardAccountId}`.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Reward recording failed by Discord reviewer {reviewerId}",
                Context.User.Id);
            await FollowupAsync(
                "The reward was not recorded. Check the fields and reward service logs.",
                ephemeral: true);
        }
    }

    [SlashCommand("pending", "Record a pending EUR report reward awaiting review")]
    public async Task Pending(
        [Summary("email", "Reporter email; no Coflnet account required")] string email,
        [Summary("amount-cents", "Positive EUR amount in cents")] long amountEurCents,
        [Summary("offer-version", "Version of the accepted reward offer")] string offerVersion,
        [Summary("reference", "Stable unique report/case reference; reuse it when retrying")] string reference,
        [Summary("reason", "Concise reason for the pending reward")] string reason,
        [Summary("evidence", "Optional opaque evidence reference; never raw identity data")] string evidenceReference = "")
    {
        if (!CreatorReviewCommands.IsAuthorizedReviewer(Context.User.Id))
        {
            await RespondAsync(
                "You are not allowed to record rewards.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        if (amountEurCents <= 0)
        {
            await FollowupAsync(
                "amount-cents must be positive.", ephemeral: true);
            return;
        }

        try
        {
            var rewardAccountId = RecipientId(email);
            var ledgerReference = Reference(RewardGrantSource.Report, reference);
            offerVersion = LedgerText(offerVersion, "offer-version", 128);
            reason = LedgerText(reason, "reason", 300);
            var details = JsonSerializer.Serialize(new
            {
                reviewerDiscordId = Context.User.Id.ToString(),
                evidenceReference = EvidenceReference(evidenceReference)
            });
            var result = await rewards.Record(new(
                ledgerReference,
                rewardAccountId,
                PendingKind,
                RewardGrantSource.Report,
                amountEurCents,
                offerVersion,
                reason,
                details));
            logger.LogInformation(
                "Pending reward {entryId} ({reference}) {action} for account {rewardAccountId} by Discord reviewer {reviewerId}",
                result.Entry.Id, ledgerReference,
                result.Created ? "created" : "already existed",
                rewardAccountId, Context.User.Id);
            await FollowupAsync(
                $"{(result.Created ? "Recorded" : "Already recorded")} pending `{ledgerReference}` as `{result.Entry.Id}` on account `{rewardAccountId}`: **{amountEurCents} EUR cents**. Approve with `/reward record ... pending:{result.Entry.Id}` using the same amount, source and offer-version, or reject with `/reward cancel email:<email> entry:{result.Entry.Id} reason:<...>`.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Pending reward recording failed by Discord reviewer {reviewerId}",
                Context.User.Id);
            await FollowupAsync(
                "The pending reward was not recorded. Check the fields and reward service logs.",
                ephemeral: true);
        }
    }

    [SlashCommand("query", "Show an account's reward balance and recent ledger entries")]
    public async Task Query(
        [Summary("email", "Coflnet reward account ID or reporter email")] string email)
    {
        if (!CreatorReviewCommands.IsAuthorizedReviewer(Context.User.Id))
        {
            await RespondAsync(
                "You are not allowed to query rewards.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);

        try
        {
            var rewardAccountId = RecipientId(email);
            var balance = await rewards.GetBalance(rewardAccountId);
            var entries = await rewards.GetLedger(rewardAccountId);
            var recent = entries
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(10)
                .Select(entry =>
                    $"`{entry.Id}` kind {entry.Kind} **{entry.RemunerationEurCents} EUR cents** `{entry.Reference}` — {entry.Reason} ({entry.CreatedAt:yyyy-MM-dd})")
                .ToList();
            var summary =
                $"Account `{rewardAccountId}`\n" +
                $"Pending: **{balance.PendingEurCents} EUR cents**\n" +
                $"Outstanding: **{balance.OutstandingEurCents} EUR cents**\n" +
                $"Available: **{balance.AvailableEurCents} EUR cents**\n" +
                $"Payout threshold: **{balance.PayoutThresholdEurCents} EUR cents**\n\n" +
                (recent.Count == 0
                    ? "No ledger entries yet."
                    : string.Join('\n', recent));
            await FollowupAsync(summary, ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Reward query failed by Discord reviewer {reviewerId}",
                Context.User.Id);
            await FollowupAsync(
                "The reward balance could not be retrieved. Check the fields and reward service logs.",
                ephemeral: true);
        }
    }

    [SlashCommand("cancel", "Cancel a pending report reward that was rejected")]
    public async Task Cancel(
        [Summary("email", "Reporter email; no Coflnet account required")] string email,
        [Summary("entry", "Entry ID of the Pending reward to cancel")] string entry,
        [Summary("reason", "Concise reason for rejecting the report")] string reason)
    {
        if (!CreatorReviewCommands.IsAuthorizedReviewer(Context.User.Id))
        {
            await RespondAsync(
                "You are not allowed to cancel rewards.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);

        try
        {
            var rewardAccountId = RecipientId(email);
            var relatedEntryId = EntryId(entry, "entry");
            var ledgerReference = Reference(
                RewardGrantSource.Report, $"cancel:{relatedEntryId:N}");
            reason = LedgerText(reason, "reason", 300);
            var details = JsonSerializer.Serialize(new
            {
                reviewerDiscordId = Context.User.Id.ToString()
            });
            var result = await rewards.Record(new(
                ledgerReference,
                rewardAccountId,
                CancellationKind,
                null,
                0,
                null,
                reason,
                details,
                relatedEntryId));
            logger.LogInformation(
                "Pending reward {relatedEntryId} cancelled as {entryId} ({reference}) for account {rewardAccountId} by Discord reviewer {reviewerId}",
                relatedEntryId, result.Entry.Id, ledgerReference,
                rewardAccountId, Context.User.Id);
            await FollowupAsync(
                $"{(result.Created ? "Cancelled" : "Already cancelled")} pending `{relatedEntryId}` as `{result.Entry.Id}` on account `{rewardAccountId}`.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Reward cancellation failed by Discord reviewer {reviewerId}",
                Context.User.Id);
            await FollowupAsync(
                "The pending reward was not cancelled. Check the fields and reward service logs.",
                ephemeral: true);
        }
    }

    internal static string RecipientId(string recipient)
    {
        var normalized = recipient?.Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("recipient is required", nameof(recipient));
        if (!normalized.Contains('@'))
        {
            if (normalized.Length > 128)
                throw new ArgumentException(
                    "recipient must not exceed 128 characters", nameof(recipient));
            return normalized;
        }
        if (!MailAddress.TryCreate(normalized, out var address)
            || !address.Address.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("recipient email is invalid", nameof(recipient));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            "reward-email-v1\0" + address.Address.ToLowerInvariant()));
        return "email-sha256-v1:"
            + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string Reference(
        RewardGrantSource source,
        string reference)
        => $"discord:{source.ToString().ToLowerInvariant()}:"
            + LedgerText(reference, "reference", 160);

    internal static string? EvidenceReference(string reference)
    {
        var normalized = reference?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        var separator = normalized.IndexOf(':');
        if (separator < 1 || separator == normalized.Length - 1
            || normalized.Length > 256 || normalized.Contains('@'))
            throw new ArgumentException(
                "evidence must be a namespaced opaque reference",
                nameof(reference));
        return normalized;
    }

    internal static Guid EntryId(string value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || !Guid.TryParse(normalized, out var id))
            throw new ArgumentException($"{name} must be a valid entry ID", name);
        return id;
    }

    internal static Guid? OptionalEntryId(string value)
        => string.IsNullOrWhiteSpace(value) ? null : EntryId(value, "pending");

    internal static string LedgerText(
        string value,
        string name,
        int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maxLength
            || normalized.Contains('@'))
            throw new ArgumentException(
                $"{name} is required, must fit its limit and cannot contain an email address",
                name);
        return normalized;
    }
}
