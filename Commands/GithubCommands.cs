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
    const int MaxPublicIssueImages = 3;
    const int MaxPublicIssueImageBytes = 10 << 20;
    static readonly Regex DiscordAttachmentPath = new(@"^/attachments/[0-9]{17,20}/[0-9]{17,20}/[^/?#\x00-\x20]{1,768}$", RegexOptions.CultureInvariant);
    static readonly Regex DiscordAttachmentQuery = new(@"^\?ex=[0-9a-f]{8}&is=[0-9a-f]{8}&hm=[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> PublicIssueImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif" };

    public GithubCommands(GitHubClient github, Octokit.GraphQL.Connection connection, ILogger<GithubCommands> logger)
    {
        this.github = github;
        this.connection = connection;
        this.logger = logger;
    }

    [SlashCommand("issue", "Creates a github issue", true)]
    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.PrivateChannel, InteractionContextType.BotDm, InteractionContextType.Guild)]
    public async Task Issue([Summary("title", "Title of the issue")] string title,
        [Summary("repo", "Repository to create the issue in"), Autocomplete<GitRepoAutocompleteHandler>()] string repo,
        [Summary("body", "Body of the issue")] string body = "")
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
        try
        {
            if (callingChannel == null)
                throw new Exception("Calling channel is null");
            var lastMessage = (await callingChannel.GetMessagesAsync(1).FlattenAsync()).First();
            body += "\ncontext:" + lastMessage.GetJumpUrl();
            if (string.Equals(repo, "SkySniper", StringComparison.OrdinalIgnoreCase))
            {
                var imageUrls = lastMessage.Attachments
                    .Where(IsPublicIssueImage)
                    .Select(attachment => attachment.Url)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxPublicIssueImages);
                body += string.Concat(imageUrls.Select((imageUrl, index) => $"\n![Discord issue image {index + 1}]({imageUrl})"));
            }
            canread = true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error getting last message");
            string? guildId = Context.Interaction.GuildId?.ToString();
            if (string.IsNullOrEmpty(guildId))
                guildId = "@me";
            ulong channelId = Context.Interaction.ChannelId ?? 0UL;
            logger.LogInformation("App command context - GuildId: {GuildId}, ChannelId: {ChannelId}", guildId, channelId);
            var channelUrl = "https://discord.com/channels/" + Context.Interaction.GuildId + "/" + Context.Interaction.ChannelId;
            body += $"\ncontext: {channelUrl}";
        }
        body = body.Replace(" https://discord.com/channels//", "https://discord.com/channels/@me/"); // dm messages
        var newIssue = new NewIssue(title)
        {
            Body = body,
        };
        try
        {
            var issue = await github.Issue.Create("Coflnet", repo, newIssue);

            var assignees = string.Equals(repo, "SkySniper", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Ekwav", "ekwav-agent" } : new[] { "Ekwav" };
            await github.Issue.Assignee.AddAssignees("Coflnet", repo, issue.Number, new(assignees));
            Console.WriteLine("Created issue " + issue.NodeId);

            // assign issue onto first project board in organization with memex
            await PutIssueOnBoard(issue.NodeId);
            
            await FollowupAsync("", embed: new EmbedBuilder()
                .WithTitle("Issue created")
                .WithDescription($"Issue created at https://github.com/Coflnet/{repo}/issues/{issue.Number}")
                .WithColor(Color.Green)
                .Build(), ephemeral: !canread);
        }
        catch (ApiException apiEx)
        {
            logger.LogError(apiEx, "GitHub API error creating issue in repo {Repo}", repo);
            await FollowupAsync($"Error creating issue in repository '{repo}': {apiEx.Message}", ephemeral: true);
            return;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

    }

    internal static bool IsPublicIssueImage(IAttachment attachment) =>
        IsPublicIssueImage(attachment.Size, attachment.ContentType, attachment.Url);

    internal static bool IsPublicIssueImage(long size, string? contentType, string url)
    {
        if (size <= 0 || size > MaxPublicIssueImageBytes || !PublicIssueImageTypes.Contains(contentType ?? "")
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Authority, "cdn.discordapp.com", StringComparison.Ordinal)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !DiscordAttachmentPath.IsMatch(uri.AbsolutePath))
            return false;
        if (uri.Query.Length != 0 && !DiscordAttachmentQuery.IsMatch(uri.Query))
            return false;
        var filename = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]);
        return filename is not "." and not ".." && filename.Length <= 255 && !filename.Contains('/') && !filename.Contains('\\')
            && !filename.Any(character => char.IsControl(character));
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
