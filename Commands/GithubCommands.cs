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

    public GithubCommands(GitHubClient github, Octokit.GraphQL.Connection connection, ILogger<GithubCommands> logger)
    {
        this.github = github;
        this.connection = connection;
        this.logger = logger;
    }

    [SlashCommand("issue", "Creates a github issue", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task Issue([Summary("title", "Title of the issue")] string title,
        [Summary("repo", "Repository to create the issue in"), Autocomplete<GitRepoAutocompleteHandler>()] string repo,
        [Summary("body", "Body of the issue")] string body = "")
    {
        await DeferAsync();
        var callingChannel = (SocketTextChannel)Context.Channel;
        if (Context.User.Id != 267680402594988033)
        {
            await FollowupAsync("This can currently only be executed by <@267680402594988033>");
            return;
        }
        try
        {
            var lastMessage = callingChannel.GetMessagesAsync(1).FlattenAsync().Result.First();
            var linkToMessage = lastMessage.GetJumpUrl();
            body += "\ncontext:" + linkToMessage;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error getting last message");
            var channelUrl = "https://discord.com/channels/" + callingChannel.Guild.Id + "/" + callingChannel.Id;
            body += $"\ncontext: {channelUrl}";
        }
        var newIssue = new NewIssue(title)
        {
            Body = body,
        };
        var issue = await github.Issue.Create("Coflnet", repo, newIssue);

        // assign Ekwav
        await github.Issue.Assignee.AddAssignees("Coflnet", repo, issue.Number, new(["Ekwav"]));
        Console.WriteLine("Created issue " + issue.NodeId);

        // assign issue onto first project board in organization with memex
        try
        {
            await PutIssueOnBoard(issue.NodeId);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        await FollowupAsync("", embed: new EmbedBuilder()
            .WithTitle("Issue created")
            .WithDescription($"Issue created at https://github.com/Coflnet/{repo}/issues/{issue.Number}")
            .WithColor(Color.Green)
            .Build());
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
        var repos = await github.Search.SearchRepo(new SearchRepositoriesRequest("Coflnet/" + autocompleteInteraction.Data.Current.Value.ToString()));
        // Create a collection with suggestions for autocomplete
        IEnumerable<AutocompleteResult> results = repos.Items.Where(i => i.FullName.Contains("Coflnet")).Select(i =>
        {
            Console.WriteLine(i.Name + " " + i.FullName);
            return new AutocompleteResult(i.Name, i.Name);
        });

        // max - 25 suggestions at a time (API limit)
        return AutocompletionResult.FromSuccess(results.Take(25));
    }
}
