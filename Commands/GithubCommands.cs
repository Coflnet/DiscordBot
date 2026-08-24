using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Octokit;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;

public class GithubCommands : InteractionModuleBase
{
    GitHubClient github;
    Octokit.GraphQL.Connection connection;
    ILogger<GithubCommands> logger;
    IssueEvidenceService evidence;
    IssueDraftService drafts;
    DiscordHandler discord;
    internal const int MaxPublicIssueImages = 3;
    internal const int MaxPublicIssueImageBytes = 10 << 20;
    const int MaxIssueSourceMessages = 25;
    static readonly Regex DiscordAttachmentPath = new(@"^/attachments/[0-9]{17,20}/[0-9]{17,20}/[^/?#\x00-\x20]{1,768}$", RegexOptions.CultureInvariant);
    static readonly Regex DiscordAttachmentQuery = new(@"^\?ex=[0-9a-f]{8}&is=[0-9a-f]{8}&hm=[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    internal static readonly Regex DiscordMessageLink = new(@"^https://discord\.com/channels/(?<guild>@me|[0-9]{17,20})/(?<channel>[0-9]{17,20})/(?<message>[0-9]{17,20})$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> PublicIssueImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif" };
    static readonly HashSet<string> PastedIssueImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif" };

    public GithubCommands(GitHubClient github, Octokit.GraphQL.Connection connection, ILogger<GithubCommands> logger,
        IssueEvidenceService evidence, IssueDraftService drafts, DiscordHandler discord)
    {
        this.github = github;
        this.connection = connection;
        this.logger = logger;
        this.evidence = evidence;
        this.drafts = drafts;
        this.discord = discord;
    }

    [SlashCommand("issue", "Creates a github issue", true)]
    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.PrivateChannel, InteractionContextType.BotDm, InteractionContextType.Guild)]
    public async Task Issue([Summary("title", "Title of the issue")] string title,
        [Summary("repo", "Repository to create the issue in"), Autocomplete<GitRepoAutocompleteHandler>()] string repo,
        [Summary("body", "Body of the issue")] string body = "",
        [Summary("message", "Issue details, or an exact Discord report message link")] string message = "",
        [Summary("image", "Screenshot to include in the issue")] IAttachment? image = null)
    {
        try
        {
            await DeferAsync(false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error deferring interaction");
            await DeferAsync(true);
        }
        var callingChannel = Context.Channel;
        if (Context.User.Id != 267680402594988033)
        {
            await FollowupAsync("This can currently only be executed if you connected your Github account");
            return;
        }
        bool canread = false;
        ulong reportGuildId = 0;
        ulong reportChannelId = 0;
        ulong reportMessageId = 0;
        var resolvedInput = ResolveMessageInput(body, message, Context.Interaction.GuildId, Context.Interaction.ChannelId);
        body = resolvedInput.Body;
        IMessage? reportMessage = null;
        try
        {
            if (callingChannel == null)
                throw new Exception("Calling channel is null");
            IMessageChannel reportChannel = callingChannel;
            var directMessageSource = resolvedInput.DirectMessageChannelId != null;
            if (directMessageSource)
            {
                var linkedChannel = await Context.Client.GetChannelAsync(resolvedInput.DirectMessageChannelId!.Value);
                if (linkedChannel is not IDMChannel directMessage
                    || !IsAuthorizedDirectMessage(directMessage.Id, directMessage.Recipient.Id,
                        resolvedInput.DirectMessageChannelId.Value, Context.User.Id))
                    throw new Exception("Linked direct message channel is not the invoking user's bot DM");
                reportChannel = directMessage;
            }
            else if (Context.Interaction.GuildId == null)
            {
                if (callingChannel is not IDMChannel directMessage || directMessage.Recipient.Id != Context.User.Id)
                    throw new Exception("Private issue sources must be the invoking user's one-to-one bot DM");
                directMessageSource = true;
            }
            if (resolvedInput.MessageId != null)
            {
                reportMessage = await reportChannel.GetMessageAsync(resolvedInput.MessageId.Value);
            }
            else
            {
                // DeferAsync creates the newest channel entry. Use the interaction
                // snowflake as the exclusive cursor, then take the nearest ordinary
                // guild message even when the operator did not author the report.
                var candidates = (await callingChannel.GetMessagesAsync(Context.Interaction.Id, Direction.Before, MaxIssueSourceMessages).FlattenAsync()).ToList();
                var selectedReportId = SelectReportMessageId(candidates.Select(candidate => (candidate.Id, candidate.Author.IsBot, candidate.Author.IsWebhook)));
                reportMessage = selectedReportId == null ? null : candidates.First(candidate => candidate.Id == selectedReportId);
            }
            if (reportMessage == null || reportMessage.Author.IsBot || reportMessage.Author.IsWebhook
                || directMessageSource && reportMessage.Author.Id != Context.User.Id)
                throw new Exception("No ordinary user report message was found in the bounded channel history");
            reportGuildId = directMessageSource ? 0 : Context.Interaction.GuildId ?? 0;
            reportChannelId = reportChannel.Id;
            reportMessageId = reportMessage.Id;
            canread = true;
        }
        catch (Exception e) when (resolvedInput.MessageId != null)
        {
            // The bot has no access to the channel/DM holding the linked message (e.g. a DM between
            // the operator and a third party) - a permanent Discord limitation, not something a retry
            // or a different fetch path can work around. The operator did supply an exact link though,
            // so let them paste the content instead of losing the issue.
            logger.LogInformation("Issue source message could not be read: {Category}", e.GetType().Name);
            // Carry the image: attachment (if any) forward - it's the best evidence input available
            // here (no copy-link step, no expiry) and must not be silently dropped.
            var attachedImageUrl = image != null && IsPublicIssueImage((long)image.Size, image.ContentType, image.Url) ? image.Url : "";
            try
            {
                var token = drafts.Create(title, repo, body, Context.User.Id,
                    Context.Interaction.GuildId ?? 0, Context.Interaction.ChannelId ?? 0, message, attachedImageUrl);
                var components = new ComponentBuilder()
                    .WithButton("Add report content", $"issue-content:{token}", ButtonStyle.Primary)
                    .Build();
                var prompt = attachedImageUrl.Length != 0
                    ? "I could not read that message (the bot is not in this DM). Your attached image will be included as evidence - add the report text and any further image links."
                    : "I could not read that message (the bot is not in this DM). Paste the report text and any image links and I'll put them in the issue.";
                await FollowupAsync(prompt, components: components, ephemeral: true);
            }
            catch (IssueDraftDenied denied)
            {
                logger.LogWarning("Issue draft could not be created: {Category}", denied.Message);
                await FollowupAsync("I could not preserve this issue draft. Run `/issue` again.", ephemeral: true);
            }
            return;
        }
        catch (Exception e)
        {
            logger.LogInformation("Automatic issue source resolution failed: {Category}", e.GetType().Name);
            try
            {
                var token = drafts.Create(title, repo, body, Context.User.Id,
                    Context.Interaction.GuildId ?? 0, Context.Interaction.ChannelId ?? 0);
                var components = new ComponentBuilder()
                    .WithButton("Choose report message", $"issue-source:{token}", ButtonStyle.Primary)
                    .Build();
                await FollowupAsync("I could not identify your recent report message automatically. Choose it explicitly to continue the same issue.", components: components, ephemeral: true);
            }
            catch (IssueDraftDenied denied)
            {
                logger.LogWarning("Issue draft could not be created: {Category}", denied.Message);
                await FollowupAsync("I could not preserve this issue draft. Run `/issue` again.", ephemeral: true);
            }
            return;
        }
        // The source message was read directly (Coflnet-server or the operator's own bot DM), so the
        // binding stays on that message as before. image: is scoped to the unreadable/paste-flow
        // mirror path only - do not silently drop it here, tell the operator it was not used.
        var readableSourceNotes = image != null
            ? new[] { "The linked report message remains the evidence source; the attached image was not included." }
            : null;
        var harvestedAttachments = reportMessage!.Attachments
            .Select(attachment => ((long)attachment.Size, (string?)attachment.ContentType, attachment.Url));
        await CreateIssue(title, repo, body, reportMessage.GetJumpUrl(), harvestedAttachments, Enumerable.Empty<string>(),
            reportGuildId, reportChannelId, reportMessageId, Context.User.Id, canread, extraNotes: readableSourceNotes);
    }

    // Shared by the direct (readable source) path and the "add report content" modal path
    // (unreadable source, reportGuildId/reportChannelId/reportMessageId all 0, canread false).
    private async Task CreateIssue(string title, string repo, string body, string contextUrl,
        IEnumerable<(long Size, string? ContentType, string Url)> harvestedAttachments, IEnumerable<string> attachedUrls,
        ulong reportGuildId, ulong reportChannelId, ulong reportMessageId, ulong invokingUserId, bool canread,
        string? sourceKind = null, IEnumerable<string>? extraNotes = null)
    {
        // Single choke point for the final image URLs: resolving harvested attachments and
        // operator-attached URLs separately (rather than inline here) is where a future download +
        // re-host-on-GitHub step slots in, since Discord CDN links expire after ~24h.
        var harvestedUrls = ResolveHarvestedImageUrls(harvestedAttachments);
        var resolvedAttachedUrls = ResolveAttachedImageUrls(attachedUrls);
        body = AppendIssueContext(body, repo, contextUrl, harvestedUrls, resolvedAttachedUrls);
        var canBind = CanBindEvidence("Coflnet/" + repo, reportGuildId, reportMessageId);
        var attachEvidence = canBind && evidence.IsConfigured;
        if (canBind && !attachEvidence)
        {
            body += "\n\n> Note: Discord context retrieval was unavailable when this issue was created. Inspect the context link manually.";
        }
        body = body.Replace("https://discord.com/channels//", "https://discord.com/channels/@me/"); // dm messages
        var newIssue = new NewIssue(title)
        {
            Body = body,
        };
        try
        {
            var issue = await github.Issue.Create("Coflnet", repo, newIssue);

            if (attachEvidence)
            {
                var binding = evidence.CreateBinding("Coflnet/" + repo, issue.Number, reportGuildId, reportChannelId,
                    reportMessageId, reportGuildId == 0 ? invokingUserId : 0, sourceKind);
                var marker = $"<!-- coflnet-discord-evidence:v1 binding={binding} -->";
                body += "\n" + marker;
                await FinalizeEvidenceMarker(
                    () => github.Issue.Update("Coflnet", repo, issue.Number, new IssueUpdate { Body = body }),
                    async () => (await github.Issue.Get("Coflnet", repo, issue.Number)).Body,
                    () => github.Issue.Update("Coflnet", repo, issue.Number,
                        new IssueUpdate { State = ItemState.Closed, StateReason = ItemStateReason.NotPlanned }),
                    marker);
            }

            var assignees = string.Equals(repo, "SkySniper", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Ekwav", "ekwav-agent" } : new[] { "Ekwav" };
            await github.Issue.Assignee.AddAssignees("Coflnet", repo, issue.Number, new(assignees));
            Console.WriteLine("Created issue " + issue.NodeId);

            // assign issue onto first project board in organization with memex
            await PutIssueOnBoard(issue.NodeId);

            var description = $"Issue created at https://github.com/Coflnet/{repo}/issues/{issue.Number}";
            if (EvidenceApplies(repo) && !attachEvidence)
                description += "\nDiscord evidence was not attached.";
            foreach (var note in extraNotes ?? Enumerable.Empty<string>())
                description += "\n" + note;
            await FollowupAsync("", embed: new EmbedBuilder()
                .WithTitle("Issue created")
                .WithDescription(description)
                .WithColor(Color.Green)
                .Build(), ephemeral: !canread);
        }
        catch (ApiException apiEx)
        {
            logger.LogError(apiEx, "GitHub API error creating issue in repo {Repo}", repo);
            await FollowupAsync($"Error creating issue in repository '{repo}': {apiEx.Message}", ephemeral: true);
            return;
        }
        catch (IssueMarkerFinalizationException)
        {
            logger.LogError("Could not finalize Discord evidence marker for newly created issue in {Repo}", repo);
            await FollowupAsync("Issue evidence finalization failed; issue creation was aborted. Ask a maintainer to verify the incomplete issue, then run `/issue` again.", ephemeral: true);
            return;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    internal static bool CanBindEvidence(string repository, ulong guildId, ulong messageId)
        => messageId != 0 && IssueEvidenceService.IsAllowedIssueSource(repository, guildId);

    // Single source of truth for "does Discord evidence apply to this repo" - a second, separately
    // spelled repo list here is what made /issue repo:skymodcommands create an unbound issue.
    internal static bool EvidenceApplies(string repository)
        => IssueEvidenceService.IsAllowedRepository("Coflnet/" + repository);

    internal static async Task FinalizeEvidenceMarker(Func<Task> update, Func<Task<string?>> reread,
        Func<Task> close, string marker)
    {
        try
        {
            await update();
            return;
        }
        catch
        {
            try
            {
                var body = await reread();
                if ((body ?? "").ReplaceLineEndings("\n").Split('\n').Contains(marker, StringComparer.Ordinal))
                    return;
            }
            catch
            {
                // Without an exact marker read-back, fail closed below.
            }

            try
            {
                await close();
            }
            catch
            {
                // Preserve the fixed, non-sensitive finalization error.
            }
            throw new IssueMarkerFinalizationException();
        }
    }

    internal sealed class IssueMarkerFinalizationException : Exception;

    internal static bool IsPublicIssueImage(IAttachment attachment) =>
        IsPublicIssueImage(attachment.Size, attachment.ContentType, attachment.Url);

    internal static bool IsPublicIssueImage(long size, string? contentType, string url)
    {
        if (size <= 0 || size > MaxPublicIssueImageBytes || !PublicIssueImageTypes.Contains(contentType ?? ""))
            return false;
        return IsDiscordAttachmentUrl(url, out _);
    }

    // Pasted links (from a modal) carry no size/content-type, so the file extension stands in for
    // the PublicIssueImageTypes check.
    internal static bool IsPastedIssueImageUrl(string url) =>
        IsDiscordAttachmentUrl(url, out var filename) && PastedIssueImageExtensions.Contains(Path.GetExtension(filename));

    private static bool IsDiscordAttachmentUrl(string url, out string filename)
    {
        filename = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Authority, "cdn.discordapp.com", StringComparison.Ordinal)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !DiscordAttachmentPath.IsMatch(uri.AbsolutePath))
            return false;
        if (uri.Query.Length != 0 && !DiscordAttachmentQuery.IsMatch(uri.Query))
            return false;
        filename = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]);
        return filename is not "." and not ".." && filename.Length <= 255 && !filename.Contains('/') && !filename.Contains('\\')
            && !filename.Any(character => char.IsControl(character));
    }

    internal static IReadOnlyList<string> ParsePastedImageUrls(string value) =>
        value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(IsPastedIssueImageUrl)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPublicIssueImages)
            .ToList();

    internal static ulong? ExactMessageId(string url, ulong? guildId, ulong? channelId)
    {
        var match = DiscordMessageLink.Match(url);
        if (!match.Success || channelId == null || !ulong.TryParse(match.Groups["channel"].Value, out var linkedChannel)
            || linkedChannel != channelId || !ulong.TryParse(match.Groups["message"].Value, out var messageId))
            return null;
        var linkedGuild = match.Groups["guild"].Value;
        if (guildId == null ? linkedGuild != "@me" : linkedGuild != guildId.Value.ToString())
            return null;
        return messageId;
    }

    internal static (string Body, ulong? MessageId, ulong? DirectMessageChannelId) ResolveMessageInput(string body, string message, ulong? guildId, ulong? channelId)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (body, null, null);
        var match = DiscordMessageLink.Match(message);
        if (match.Success && match.Groups["guild"].Value == "@me"
            && ulong.TryParse(match.Groups["channel"].Value, out var directMessageChannelId)
            && ulong.TryParse(match.Groups["message"].Value, out var directMessageId))
            return (body, directMessageId, directMessageChannelId);
        var messageId = ExactMessageId(message, guildId, channelId);
        if (messageId != null)
            return (body, messageId, null);
        return (string.IsNullOrWhiteSpace(body) ? message : body + "\n\n" + message, null, null);
    }

    internal static bool IsAuthorizedDirectMessage(ulong actualChannelId, ulong recipientId, ulong linkedChannelId, ulong invokingUserId) =>
        actualChannelId == linkedChannelId && recipientId == invokingUserId;

    internal static ulong? SelectReportMessageId(IEnumerable<(ulong Id, bool IsBot, bool IsWebhook)> candidates)
    {
        foreach (var candidate in candidates)
            if (!candidate.IsBot && !candidate.IsWebhook)
                return candidate.Id;
        return null;
    }

    // Resolves harvested report-message attachments to the final image URLs used in the issue body.
    // Today this only validates and passes the Discord CDN URL through; because those links expire,
    // this is where a future download + re-host-on-GitHub step slots in without touching the
    // repo-gating/rendering logic in AppendIssueContext.
    internal static IReadOnlyList<string> ResolveHarvestedImageUrls(IEnumerable<(long Size, string? ContentType, string Url)> attachments) =>
        attachments.Where(attachment => IsPublicIssueImage(attachment.Size, attachment.ContentType, attachment.Url))
            .Select(attachment => attachment.Url).ToList();

    // Resolves operator-supplied image URLs (the image: option, or pasted links) the same way.
    internal static IReadOnlyList<string> ResolveAttachedImageUrls(IEnumerable<string> urls) =>
        urls.Where(IsPastedIssueImageUrl).ToList();

    // A pasted-link download does an outbound HTTP call (IssueEvidenceService.DownloadIssueImage)
    // that can throw (timeout, connection reset, ...). By the time this runs the draft has already
    // been consumed, so a throw must never escape here - it has to be treated exactly like a
    // rejected/expired link (null), not abort the whole issue-creation flow.
    internal static async Task<(byte[] Data, string MediaType)?> TryDownloadIssueImage(
        Func<string, CancellationToken, Task<(byte[] Data, string MediaType)?>> download, string url, CancellationToken cancellationToken)
    {
        try
        {
            return await download(url, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    internal static string AppendIssueContext(string body, string repository, string jumpUrl,
        IEnumerable<string> harvestedUrls, IEnumerable<string> attachedUrls)
    {
        body += "\ncontext:" + jumpUrl;
        // Attached-first ordering matters: with 3+ harvested images the operator's own attachment must
        // still survive the Take(MaxPublicIssueImages) cap.
        var candidateUrls = attachedUrls.Concat(harvestedUrls);
        // No image URLs in the issue body - Discord CDN links rot after ~24h either way. Developers
        // read the linked message directly; the DevServer pulls the bytes live via the evidence
        // binding. Just record how many screenshots are available as evidence.
        var count = candidateUrls.Distinct(StringComparer.Ordinal).Take(MaxPublicIssueImages).Count();
        if (count > 0)
            body += $"\n{count} screenshot{(count == 1 ? "" : "s")} attached as Discord evidence";
        return body;
    }

    [ComponentInteraction("issue-source:*", true)]
    public async Task ChooseIssueSource(string token)
    {
        try
        {
            drafts.Peek(token, Context.User.Id, Context.Interaction.GuildId ?? 0, Context.Interaction.ChannelId ?? 0);
            if (Context.Interaction is not SocketMessageComponent component)
                throw new IssueDraftDenied("invalid_component_context");
            await component.RespondWithModalAsync<IssueSourceModal>($"issue-source-modal:{token}");
        }
        catch (IssueDraftDenied)
        {
            await RespondAsync("This issue draft expired or belongs to a different user or channel. Run `/issue` again.", ephemeral: true);
        }
    }

    [ModalInteraction("issue-source-modal:*", true)]
    public async Task SubmitIssueSource(string token, IssueSourceModal modal)
    {
        try
        {
            var guildId = Context.Interaction.GuildId ?? 0;
            var channelId = Context.Interaction.ChannelId ?? 0;
            if (ResolveMessageInput("", modal.MessageLink, Context.Interaction.GuildId, Context.Interaction.ChannelId).MessageId == null)
            {
                await RespondAsync("Paste one exact message link from this guild channel or your one-to-one bot DM.", ephemeral: true);
                return;
            }
            var pending = drafts.Peek(token, Context.User.Id, guildId, channelId);
            if (pending.Body.Length + (modal.ExtraDetails?.Length ?? 0) > 64 << 10)
            {
                await RespondAsync("The combined issue details exceed the bounded draft size.", ephemeral: true);
                return;
            }
            var draft = drafts.Take(token, Context.User.Id, guildId, channelId);
            var details = string.IsNullOrWhiteSpace(modal.ExtraDetails)
                ? draft.Body
                : draft.Body + (string.IsNullOrWhiteSpace(draft.Body) ? "" : "\n\n") + modal.ExtraDetails;
            await Issue(draft.Title, draft.Repository, details, modal.MessageLink);
        }
        catch (IssueDraftDenied)
        {
            await RespondAsync("This issue draft expired or was already submitted. Run `/issue` again.", ephemeral: true);
        }
    }

    public sealed class IssueSourceModal : IModal
    {
        public string Title => "Choose report message";

        [ModalTextInput("message-link", TextInputStyle.Short, "Exact Discord message link", 1, 300)]
        public string MessageLink { get; set; } = "";

        [ModalTextInput("extra-details", TextInputStyle.Paragraph, "Optional extra details", 0, 2000)]
        [RequiredInput(false)]
        public string ExtraDetails { get; set; } = "";
    }

    [ComponentInteraction("issue-content:*", true)]
    public async Task AddReportContent(string token)
    {
        try
        {
            drafts.Peek(token, Context.User.Id, Context.Interaction.GuildId ?? 0, Context.Interaction.ChannelId ?? 0);
            if (Context.Interaction is not SocketMessageComponent component)
                throw new IssueDraftDenied("invalid_component_context");
            await component.RespondWithModalAsync<IssueReportContentModal>($"issue-content-modal:{token}");
        }
        catch (IssueDraftDenied)
        {
            await RespondAsync("This issue draft expired or belongs to a different user or channel. Run `/issue` again.", ephemeral: true);
        }
    }

    [ModalInteraction("issue-content-modal:*", true)]
    public async Task SubmitReportContent(string token, IssueReportContentModal modal)
    {
        try
        {
            var guildId = Context.Interaction.GuildId ?? 0;
            var channelId = Context.Interaction.ChannelId ?? 0;
            var draft = drafts.Take(token, Context.User.Id, guildId, channelId);
            try
            {
                await DeferAsync(false);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error deferring interaction");
                await DeferAsync(true);
            }
            // The draft is already consumed at this point, so from here on nothing may escape
            // unhandled - that would leave the interaction dead with no way to recover the
            // operator's title/body. Report a generic failure instead of throwing through.
            try
            {
                var body = string.IsNullOrWhiteSpace(modal.Content)
                    ? draft.Body
                    : draft.Body + (string.IsNullOrWhiteSpace(draft.Body) ? "" : "\n\n") + modal.Content;

                // The source message is unreadable, so there is no permanent place to point evidence
                // at. Mirror any attached/pasted images into the bot's own (always-readable) DM with
                // the operator, and bind evidence to that mirror instead. Only bother for repos
                // evidence ever applies to. The draft's attached image (from the image: option) goes
                // first, consistent with AppendIssueContext's attached-first ordering, so it survives
                // the 3-image cap.
                var notes = new List<string>();
                IUserMessage? mirror = null;
                var attachedUrls = Enumerable.Empty<string>();
                if (EvidenceApplies(draft.Repository))
                {
                    var pastedUrls = ParsePastedImageUrls(modal.Images);
                    var candidateUrls = (draft.AttachedImageUrl.Length != 0 ? new[] { draft.AttachedImageUrl } : Array.Empty<string>())
                        .Concat(pastedUrls)
                        .Distinct(StringComparer.Ordinal)
                        .Take(MaxPublicIssueImages)
                        .ToList();
                    var downloaded = new List<(string Url, string Name, byte[] Data, string MediaType)>();
                    foreach (var url in candidateUrls)
                    {
                        var download = await TryDownloadIssueImage(evidence.DownloadIssueImage, url, CancellationToken.None);
                        if (download == null)
                            continue;
                        var extension = download.Value.MediaType switch { "image/jpeg" => "jpg", "image/gif" => "gif", _ => "png" };
                        downloaded.Add((url, $"report-image-{downloaded.Count + 1}.{extension}", download.Value.Data, download.Value.MediaType));
                    }
                    var failedDownloads = candidateUrls.Count - downloaded.Count;
                    var mirrorContent = string.IsNullOrWhiteSpace(modal.Content) ? "(no additional details provided)" : modal.Content;
                    mirror = await discord.MirrorIssueEvidence(Context.User.Id, mirrorContent,
                        downloaded.Select(image => (image.Name, image.Data, image.MediaType)).ToList());
                    if (mirror == null)
                        notes.Add("Could not create the Discord DM mirror for evidence (do you have DMs enabled from this bot?).");
                    else if (failedDownloads > 0)
                        notes.Add($"{failedDownloads} pasted image link(s) could not be downloaded (they may be older than ~24h) - re-copy them from Discord and try again if you'd like them attached.");
                    if (mirror != null)
                        attachedUrls = downloaded.Select(image => image.Url);
                }
                await CreateIssue(draft.Title, draft.Repository, body, draft.SourceUrl,
                    Enumerable.Empty<(long Size, string? ContentType, string Url)>(), attachedUrls,
                    0, mirror?.Channel.Id ?? 0, mirror?.Id ?? 0, Context.User.Id, false, "bot-dm-mirror", notes);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to create an issue from pasted report content in {Repo}", draft.Repository);
                await FollowupAsync("Something went wrong while creating the issue from your pasted content. Check GitHub before running `/issue` again, in case it was partially created.", ephemeral: true);
            }
        }
        catch (IssueDraftDenied)
        {
            await RespondAsync("This issue draft expired or was already submitted. Run `/issue` again.", ephemeral: true);
        }
    }

    public sealed class IssueReportContentModal : IModal
    {
        public string Title => "Add report content";

        [ModalTextInput("report-content", TextInputStyle.Paragraph, "Paste the report text", 0, 3000)]
        [RequiredInput(false)]
        public string Content { get; set; } = "";

        [ModalTextInput("report-images", TextInputStyle.Paragraph, "Additional image links, one per line", 0, 1000)]
        [RequiredInput(false)]
        public string Images { get; set; } = "";
    }

    private async Task PutIssueOnBoard(string issueId)
    {
        var projectQuery = new Query()
                    .Organization("Coflnet")
                    .ProjectsV2(first: 1, query: "Kanban Board")
                    .Nodes
                    .Select(p => new { p.Id, p.Number })
                    .Compile();
        var result = await connection.Run(projectQuery);
        var projectId = result.First();
        Console.WriteLine("project: " + result.First().Number);

        // {"memexProjectItem":{"contentType":"Issue","content":{"id":2526958817,"repositoryId":439900481},"memexProjectColumnValues":[]}}

        var projectItemMutation = new Mutation()
            .AddProjectV2ItemById(new AddProjectV2ItemByIdInput()
            {
                ContentId = new ID(issueId.ToString()),
                ProjectId = projectId.Id,
            })
            .Select(p => p.Item.Id)
            .Compile();
        var projectItemResult = await connection.Run(projectItemMutation);
        Console.WriteLine(projectItemResult.Value);
    }
}

public class GitRepoAutocompleteHandler : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var github = services.GetRequiredService<GitHubClient>();
        var searchTerm = GetSearchTerm(autocompleteInteraction.Data.Current.Value?.ToString());
        var repos = await github.Search.SearchRepo(new SearchRepositoriesRequest(searchTerm)
        {
            // Restrict autocomplete to repos owned by the Coflnet org.
            User = "Coflnet",
            In = new[] { InQualifier.Name }
        });
        // Create a collection with suggestions for autocomplete
        IEnumerable<AutocompleteResult> results = repos.Items.Select(i =>
        {
            Console.WriteLine(i.Name + " " + i.FullName);
            return new AutocompleteResult(i.Name, i.Name);
        });

        // max - 25 suggestions at a time (API limit)
        return AutocompletionResult.FromSuccess(results.Take(25));
    }

    internal static string GetSearchTerm(string? value) => string.IsNullOrWhiteSpace(value) ? "sky" : value;
}
