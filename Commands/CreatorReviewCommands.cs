using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Coflnet.Discord;

[Group("creator", "Manage Expert creator applications")]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
[CommandContextType(InteractionContextType.Guild)]
public sealed class CreatorReviewCommands(
    Persistence persistence,
    DiscordHandler discord,
    CreatorOnboardingClient onboarding,
    ILogger<CreatorReviewCommands> logger) : InteractionModuleBase
{
    internal const ulong ReviewerId = 267680402594988033;
    private const string PrivacyNoticeVersion = "2026-09-04";
    private const string ReviewRuleVersion = "creator-review-2026-09-04";
    private const string DefaultReviewReason = "Manual application review completed";
    private static readonly Regex MessageLink = new(
        @"^https://discord\.com/channels/(?<guild>@me|[0-9]{17,20})/(?<channel>[0-9]{17,20})/(?<message>[0-9]{17,20})$",
        RegexOptions.CultureInvariant);

    [SlashCommand("review", "Write an immutable Expert application review")]
    public async Task Review(
        [Summary("applicant", "Discord user who submitted the application")] IUser applicant,
        [Summary("application", "Exact Discord application-message link")] string application,
        [Summary("residence", "Residence country, ISO alpha-2")] string residenceCountry,
        [Summary("capacity", "Adult, or age 16+ with a legal representative")] CreatorCapacityStatus capacityStatus,
        [Summary("decision", "Optional override of the capacity-based outcome")] CreatorOnboardingStatus? decision = null,
        [Summary("reason", "Optional concise review rationale")] string reason = DefaultReviewReason,
        [Summary("seller-type", "Individual or business")] CreatorSellerType sellerType = CreatorSellerType.Individual,
        [Summary("capacity-law", "Optional reviewed country/subdivision; defaults to residence")] string capacityJurisdiction = "",
        [Summary("representative", "Required separate Discord account for a minor")] IUser? representative = null,
        [Summary("tax-residence", "Optional paid-sale ISO country; defaults to residence")] string taxResidenceCountry = "",
        [Summary("tax-document", "Required only to enable paid publication")] CreatorTaxDocumentRoute taxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable,
        [Summary("verification", "Optional opaque payout-verification reference")] string verificationReference = "",
        [Summary("valid-until", "Optional exclusive UTC expiry: yyyy-MM-dd")] string validUntil = "",
        [Summary("minecraft-uuid", "Optional linked UUID; defaults to the primary UUID")] string minecraftUuid = "")
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            var account = await Account(applicant, minecraftUuid);
            if (account == null)
                return;
            var evidence = await Evidence(application, applicant.Id);
            if (evidence == null)
                return;
            if (!TryOptionalUtcDate(validUntil, out var validUntilUtc))
            {
                await FollowupAsync(
                    "valid-until must use `yyyy-MM-dd`.",
                    ephemeral: true);
                return;
            }
            decision ??= capacityStatus switch
            {
                CreatorCapacityStatus.Minor16PlusWithGuardian =>
                    CreatorOnboardingStatus.Pending,
                CreatorCapacityStatus.Insufficient =>
                    CreatorOnboardingStatus.Rejected,
                _ => CreatorOnboardingStatus.Approved
            };
            if (capacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
                && (representative == null || representative.Id == applicant.Id))
            {
                await FollowupAsync(
                    "A minor requires a separately controlled legal-representative Discord account.",
                    ephemeral: true);
                return;
            }
            if (sellerType == CreatorSellerType.Business
                && capacityStatus != CreatorCapacityStatus.AdultDeclared)
            {
                await FollowupAsync(
                    "A business review requires an adult authorized signatory; a minor cannot use the business route as a capacity shortcut.",
                    ephemeral: true);
                return;
            }
            if (capacityStatus != CreatorCapacityStatus.Minor16PlusWithGuardian
                && representative != null)
            {
                await FollowupAsync(
                    "A representative account is accepted only for a minor review.",
                    ephemeral: true);
                return;
            }
            if (capacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
                && decision == CreatorOnboardingStatus.Approved)
            {
                await FollowupAsync(
                    "Store the minor review as Pending, send the guardian request, then approve it after the separate acceptance is recorded.",
                    ephemeral: true);
                return;
            }

            var latest = await onboarding.GetLatest(account.Value.Info.UserId);
            taxResidenceCountry = string.IsNullOrWhiteSpace(taxResidenceCountry)
                ? residenceCountry
                : taxResidenceCountry;
            if (string.IsNullOrWhiteSpace(capacityJurisdiction)
                && residenceCountry.Trim().Equals(
                    "GB", StringComparison.OrdinalIgnoreCase))
            {
                await FollowupAsync(
                    "capacity-law is required for England/Wales: use `GB-ENG` or `GB-WLS`.",
                    ephemeral: true);
                return;
            }
            capacityJurisdiction = string.IsNullOrWhiteSpace(capacityJurisdiction)
                ? residenceCountry
                : capacityJurisdiction;
            var verification = string.IsNullOrWhiteSpace(verificationReference)
                ? null
                : verificationReference;
            var request = new CreatorReviewRequest(
                Guid.NewGuid(),
                account.Value.Info.UserId,
                account.Value.MinecraftUuid,
                decision.Value,
                residenceCountry,
                taxResidenceCountry,
                sellerType,
                capacityJurisdiction,
                capacityStatus,
                taxDocumentRoute,
                PrivacyNoticeVersion,
                verification,
                representative == null ? null : $"discord:{representative.Id}",
                null,
                null,
                evidence.Value.Reference,
                evidence.Value.Sha256,
                ReviewRuleVersion,
                validUntilUtc,
                reason,
                latest?.Id);
            var stored = await onboarding.Append(request, Context.User.Id);
            logger.LogInformation(
                "Creator review {reviewId} set {status} for user {creatorUserId} by Discord reviewer {reviewerId}",
                stored.Id, stored.Status, stored.CreatorUserId, Context.User.Id);
            await FollowupAsync(
                $"Stored immutable review `{stored.Id}`: **{stored.Status}** for {applicant.Mention}.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Creator application review failed for Discord applicant {applicantId} by reviewer {reviewerId}",
                applicant.Id, Context.User.Id);
            await FollowupAsync(
                exception is HttpRequestException { StatusCode: null }
                    ? "The review service is unavailable, so nothing was stored. Check its connection and try again."
                    : "The review was not stored. Check the supplied fields and service logs.",
                ephemeral: true);
        }
    }

    [SlashCommand("request-guardian", "Send the current Creator agreement to the reviewed guardian")]
    public async Task RequestGuardian(
        [Summary("applicant", "Minor creator with a pending review")] IUser applicant,
        [Summary("language", "Agreement language: en or de")] string language = "en")
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            var account = await Account(applicant, "");
            if (account == null)
                return;
            var review = await onboarding.GetLatest(account.Value.Info.UserId);
            if (review == null
                || review.Status != CreatorOnboardingStatus.Pending
                || review.CapacityStatus != CreatorCapacityStatus.Minor16PlusWithGuardian
                || review.RepresentativeAcceptedAtUtc != null
                || !TryDiscordAccount(
                    review.RepresentativeAccountId, out var representativeId))
            {
                await FollowupAsync(
                    "The latest review is not awaiting a legal representative.",
                    ephemeral: true);
                return;
            }
            var representative = await Context.Client.GetUserAsync(representativeId);
            if (representative == null)
            {
                await FollowupAsync(
                    "The legal representative's Discord account is unavailable.",
                    ephemeral: true);
                return;
            }
            var agreement = await onboarding.GetCurrentAgreement(language);
            var components = new ComponentBuilder()
                .WithButton(
                    "Review Creator agreement",
                    style: ButtonStyle.Link,
                    url: agreement.LicenseUrl)
                .WithButton(
                    "Immutable agreement root",
                    style: ButtonStyle.Link,
                    url: agreement.DescriptorUrl)
                .WithButton(
                    "I have authority and accept",
                    GuardianCustomId(review.Id, agreement.Hash, agreement.Locale),
                    ButtonStyle.Success)
                .Build();
            await representative.SendMessageAsync(
                $"""
                Coflnet has reviewed **{applicant.Username}** as a minor Expert Config creator.

                Before they may publish a Config, review the linked Creator agreement. By selecting **I have authority and accept**, you confirm that you are their legal representative, have sole authority or authorization from every other person whose approval is required, approve the creator's acceptance, licence grant and earning of Creator fees, and accept the exact pinned agreement on their behalf.

                Creator fees belong to the creator; this does not make you the fee owner. After Coflnet's manual approval, the creator may publish Configs. Paid publication and fee accrual also require a supported seller country and the applicable settlement or invoice route. Coflnet will pay accrued fees only after separate payout onboarding and may verify a payout account held by you solely in your representative capacity.

                Privacy notice: https://coflnet.com/privacy
                Agreement version: {agreement.Version}
                Agreement root: {agreement.Hash}
                Review: {review.Id}
                """,
                components: components);
            await FollowupAsync(
                $"Sent the exact agreement and acceptance control to <@{representativeId}>.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Sending guardian acceptance failed for Discord applicant {applicantId}",
                applicant.Id);
            await FollowupAsync(
                "The guardian request was not sent. Check the account, DMs and service logs.",
                ephemeral: true);
        }
    }

    [SlashCommand("set-status", "Append a status change using the latest reviewed identity data")]
    public async Task SetStatus(
        [Summary("applicant", "Reviewed Discord applicant")] IUser applicant,
        [Summary("decision", "New review status")] CreatorOnboardingStatus decision,
        [Summary("reason", "Reason for the status change")] string reason,
        [Summary("valid-until", "Optional replacement exclusive UTC expiry: yyyy-MM-dd")] string validUntil = "")
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            var account = await Account(applicant, "");
            if (account == null)
                return;
            if (!TryOptionalUtcDate(validUntil, out var validUntilUtc))
            {
                await FollowupAsync("valid-until must use `yyyy-MM-dd`.", ephemeral: true);
                return;
            }
            var latest = await onboarding.GetLatest(account.Value.Info.UserId);
            if (latest == null)
            {
                await FollowupAsync("This applicant has no prior review.", ephemeral: true);
                return;
            }
            var stored = await onboarding.Append(
                latest.Next(decision, reason, validUntilUtc),
                Context.User.Id);
            logger.LogInformation(
                "Creator review {reviewId} changed to {status} for user {creatorUserId} by Discord reviewer {reviewerId}",
                stored.Id, stored.Status, stored.CreatorUserId, Context.User.Id);
            await FollowupAsync(
                $"Stored immutable status review `{stored.Id}`: **{stored.Status}** for {applicant.Mention}.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Creator status change failed for Discord applicant {applicantId} by reviewer {reviewerId}",
                applicant.Id, Context.User.Id);
            await FollowupAsync("The status change was not stored. Check service logs.", ephemeral: true);
        }
    }

    [SlashCommand("show", "Show the latest immutable Expert application review")]
    public async Task Show(
        [Summary("applicant", "Reviewed Discord applicant")] IUser applicant)
    {
        if (!await RequireReviewer())
            return;
        await DeferAsync(ephemeral: true);
        try
        {
            var account = await Account(applicant, "");
            if (account == null)
                return;
            var review = await onboarding.GetLatest(account.Value.Info.UserId);
            if (review == null)
            {
                await FollowupAsync("This applicant has no review.", ephemeral: true);
                return;
            }
            var embed = new EmbedBuilder()
                .WithTitle($"Creator review: {applicant.Username}")
                .WithColor(review.Status == CreatorOnboardingStatus.Approved
                    ? Color.Green
                    : Color.Orange)
                .AddField("Status", review.Status, true)
                .AddField("Residence / tax", $"{review.ResidenceCountry} / {review.TaxResidenceCountry}", true)
                .AddField("Capacity", $"{review.CapacityStatus} ({review.CapacityJurisdiction})", true)
                .AddField("Representative", review.CapacityStatus != CreatorCapacityStatus.Minor16PlusWithGuardian
                    ? "not required"
                    : review.RepresentativeAcceptedAtUtc == null ? "missing" : "accepted", true)
                .AddField("Payout route", review.TaxDocumentRoute == CreatorTaxDocumentRoute.NotApplicable
                    ? "not completed"
                    : review.TaxDocumentRoute, true)
                .AddField("Privacy notice", review.PrivacyNoticeVersion)
                .AddField("Rule", review.RuleVersion)
                .AddField("Evidence", review.EvidenceReference)
                .AddField("Reason", review.Reason)
                .WithFooter($"Review {review.Id} · {review.ReviewedBy}")
                .WithTimestamp(review.ReviewedAtUtc)
                .Build();
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Creator review lookup failed for Discord applicant {applicantId} by reviewer {reviewerId}",
                applicant.Id, Context.User.Id);
            await FollowupAsync("The review could not be loaded. Check service logs.", ephemeral: true);
        }
    }

    private async Task<bool> RequireReviewer()
    {
        if (IsAuthorizedReviewer(Context.User.Id))
            return true;
        await RespondAsync("You are not allowed to manage creator reviews.", ephemeral: true);
        return false;
    }

    internal static bool IsAuthorizedReviewer(ulong userId) => userId == ReviewerId;

    internal static string GuardianCustomId(
        Guid reviewId,
        string agreementHash,
        string locale) =>
        $"creator-guardian:{reviewId:N}:{Convert.ToBase64String(Convert.FromHexString(agreementHash)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}:{locale}";

    internal static bool TryDiscordAccount(string? value, out ulong id) =>
        ulong.TryParse(
            value?.StartsWith("discord:", StringComparison.Ordinal) == true
                ? value[8..]
                : "",
            out id);

    private async Task<(DiscordAccountInfo Info, string MinecraftUuid)?> Account(
        IUser applicant,
        string minecraftUuid)
    {
        var account = await persistence.GetDiscordAccountInfo(applicant.Id);
        if (account == null || string.IsNullOrWhiteSpace(account.UserId))
        {
            await FollowupAsync(
                "The applicant must link a verified Minecraft account first.",
                ephemeral: true);
            return null;
        }
        var selected = account.MinecraftUuid;
        if (!string.IsNullOrWhiteSpace(minecraftUuid)
            && !Guid.TryParse(minecraftUuid, out selected))
        {
            await FollowupAsync("minecraft-uuid is invalid.", ephemeral: true);
            return null;
        }
        if (selected == Guid.Empty
            || selected != account.MinecraftUuid
                && account.MinecraftUuids?.Contains(selected) != true)
        {
            await FollowupAsync(
                "That Minecraft UUID is not linked to the applicant.",
                ephemeral: true);
            return null;
        }
        return (account, selected.ToString("N"));
    }

    private async Task<(string Reference, string Sha256)?> Evidence(
        string link,
        ulong applicantId)
    {
        if (!TryMessageLink(link, out var guildId, out var channelId, out var messageId)
            || guildId != 0 && Context.Guild?.Id != guildId)
        {
            await FollowupAsync(
                "application must be an exact Discord message link from this server or a bot-accessible DM.",
                ephemeral: true);
            return null;
        }
        var message = await discord.GetMessageFromChannel(channelId, messageId);
        if (message == null || message.Author.Id != applicantId)
        {
            await FollowupAsync(
                "The bot cannot access that application message or it was not authored by the applicant.",
                ephemeral: true);
            return null;
        }
        var canonical = JsonSerializer.Serialize(new
        {
            message.Id,
            authorId = message.Author.Id,
            message.Timestamp,
            message.EditedTimestamp,
            message.Content,
            attachments = message.Attachments.OrderBy(item => item.Id).Select(item => new
            {
                item.Id,
                item.Filename,
                item.Size,
                item.ContentType
            })
        });
        return ($"discord:{(guildId == 0 ? "@me" : guildId.ToString())}:{channelId}:{messageId}",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant());
    }

    internal static bool TryMessageLink(
        string value,
        out ulong guildId,
        out ulong channelId,
        out ulong messageId)
    {
        var match = MessageLink.Match(value ?? "");
        guildId = channelId = messageId = 0;
        if (!match.Success
            || match.Groups["guild"].Value != "@me"
                && !ulong.TryParse(match.Groups["guild"].Value, out guildId)
            || !ulong.TryParse(match.Groups["channel"].Value, out channelId)
            || !ulong.TryParse(match.Groups["message"].Value, out messageId))
            return false;
        return true;
    }

    internal static bool TryUtcDate(string value, out DateTime result)
        => DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);

    private static bool TryOptionalUtcDate(string value, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!TryUtcDate(value, out var parsed))
            return false;
        result = parsed;
        return true;
    }
}

public sealed class CreatorGuardianAcceptanceCommands(
    CreatorOnboardingClient onboarding,
    ILogger<CreatorGuardianAcceptanceCommands> logger) : InteractionModuleBase
{
    [ComponentInteraction("creator-guardian:*:*:*")]
    public async Task Accept(
        string reviewIdText,
        string encodedHash,
        string locale)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            if (!Guid.TryParseExact(reviewIdText, "N", out var reviewId))
                throw new InvalidOperationException("Guardian review ID is invalid.");
            var padded = encodedHash.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var agreementHash = Convert.ToHexString(
                Convert.FromBase64String(padded)).ToLowerInvariant();
            var current = await onboarding.GetCurrentAgreement(locale);
            if (!string.Equals(
                    current.Hash, agreementHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The Creator agreement changed; ask Coflnet to send a new request.");
            var stored = await onboarding.AcceptGuardian(
                reviewId,
                agreementHash,
                current.Locale,
                Context.User.Id);
            if (Context.Interaction is SocketMessageComponent component)
                await component.Message.ModifyAsync(message =>
                {
                    message.Content +=
                        $"\n\nAccepted by <@{Context.User.Id}> at {stored.ReviewedAtUtc:u}.";
                    message.Components = new ComponentBuilder().Build();
                });
            await FollowupAsync(
                "Your legal-representative acceptance was recorded. Coflnet must complete the manual review before the creator can publish. Identity and tax checks may be requested later before payout.",
                ephemeral: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Guardian acceptance failed for Discord account {guardianId}",
                Context.User.Id);
            await FollowupAsync(
                "The acceptance could not be recorded. Ask Coflnet to check that this is the current request and agreement.",
                ephemeral: true);
        }
    }
}
