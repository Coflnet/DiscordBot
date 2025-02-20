

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Discord;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Octokit.GraphQL;

public class Commands : InteractionModuleBase
{
    ISearchApi searchApi;
    ILogger<Commands> logger;
    ProfileClient profileClient;
    Persistence persistence;
    UserInfoUpdater userInfoUpdater;
    ChatService chatService;
    Coflnet.Payments.Client.Api.ITransactionApi transactionApi;
    IConnectApi connectApi;
    Coflnet.Payments.Client.Api.IUserApi userApi;
    Coflnet.Payments.Client.Api.ITopUpApi topUpApi;
    public Commands(ISearchApi searchApi,
                    ILogger<Commands> logger,
                    ProfileClient profileClient,
                    Persistence persistence,
                    Coflnet.Payments.Client.Api.ITransactionApi transactionApi,
                    IConnectApi connectApi,
                    UserInfoUpdater userInfoUpdater,
                    ChatService chatService,
                    Coflnet.Payments.Client.Api.IUserApi userApi,
                    Coflnet.Payments.Client.Api.ITopUpApi topUpApi)
    {
        this.searchApi = searchApi;
        this.logger = logger;
        this.profileClient = profileClient;
        this.persistence = persistence;
        this.userInfoUpdater = userInfoUpdater;
        this.chatService = chatService;
        this.transactionApi = transactionApi;
        this.connectApi = connectApi;
        this.userApi = userApi;
        this.topUpApi = topUpApi;
    }

    public override Task BeforeExecuteAsync(ICommandInfo command)
    {
        Console.WriteLine("BeforeExecuteAsync " + command.Name);
        return base.BeforeExecuteAsync(command);
    }

    public override Task AfterExecuteAsync(ICommandInfo command)
    {
        return base.AfterExecuteAsync(command);
    }

    [SlashCommand("update-mc-user", "Request an update to Minecraft user via hypixel profile", true)]
    [DefaultMemberPermissions(GuildPermission.SendMessages)]
    public async Task UpdateMcUser([Summary("name", "Minecraft user name"), Autocomplete] string userName)
    {
        await DeferAsync(ephemeral: true);
        var user = (await searchApi.ApiSearchPlayerPlayerNameGetAsync(userName)).First();
        var profile = await profileClient.GetLookup(user.Uuid);
        if (DoesNotMatchExecutor(profile))
        {
            profile = await profileClient.GetLookup(user.Uuid, true);
        }
        if (DoesNotMatchExecutor(profile))
        {
            logger.LogInformation("Profile data: " + Newtonsoft.Json.JsonConvert.SerializeObject(profile));
            await FollowupAsync("", embed: new EmbedBuilder()
                .WithTitle("Error")
                .WithDescription(
                $"""
                The player `{user.Name}` has not linked their Discord account to their Hypixel account.
                Join Hypixel and follow these steps to set your Discord link:

                1. Click on My Profile (Right Click) in a Hypixel lobby
                2. Click on `Social Media` (Player head next to compas)
                3. Left-click on `Discord`
                4. Paste this in the Minecraft ingame chat: {Context.Interaction.User.Username}
                5. Rerun this command
                """)
                .WithColor(Color.Red)
                .Build());
            return;
        }

        var existing = await persistence.GetDiscordAccountInfo(Context.Interaction.User.Id) ?? new DiscordAccountInfo();
        await userInfoUpdater.UpdateuserDetails(Context.Interaction.User.Id, user, existing);
        await FollowupAsync("", embed: new EmbedBuilder()
            .WithTitle("Success")
            .WithDescription($"Your Minecraft account `{user.Name}` has been linked to your Discord account")
            .WithColor(Color.Green)
            .Build(), ephemeral: true);
    }


    [SlashCommand("transactions", "List a users transactions", true)]
    [DefaultMemberPermissions(GuildPermission.ManageRoles)]
    public async Task GetTransactions(string user)
    {
        var userId = await NewMethod(user);
        if (userId == null)
        {
            return;
        }
        var transactions = await transactionApi.TransactionUUserIdGetAsync(userId, 0, 10);
        await FollowupAsync("", ephemeral: true, embed: new EmbedBuilder()
            .WithTitle("Transactions for " + userId)
            .WithDescription(string.Join("\n", transactions.Select(t => $"{t.Id} {t.TimeStamp} {t.Amount} {t.ProductId} - {t.Reference}")))
            .WithColor(Color.Green)
            .Build());
    }

    [SlashCommand("compensate", "Compensate a user", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task Compensate(string user, string amount, string reason)
    {
        var userId = await NewMethod(user);
        if (userId == null)
        {
            return;
        }
        if (!int.TryParse(amount, out var parsedAmount))
        {
            await FollowupAsync("Invalid amount");
            return;
        }
        var transaction = await topUpApi.TopUpCustomPostAsync(userId, new()
        {
            Amount = parsedAmount,
            ProductId = "compensation",
            Reference = reason
        });
        await FollowupAsync("", ephemeral: true, embed: new EmbedBuilder()
            .WithTitle("Compensated " + userId)
            .WithDescription($"Compensated {userId} with {parsedAmount} - {reason}\nTransaction id: {transaction.Id}")
            .WithColor(Color.Green)
            .Build());
    }

    [SlashCommand("revert", "Revert a transactions", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task RevertTransaction(string user, string transactionId)
    {
        var userId = await NewMethod(user);
        if (userId == null)
        {
            return;
        }
        if (!int.TryParse(transactionId, out var parsedId))
        {
            await FollowupAsync("Invalid transaction id");
            return;
        }
        var transaction = await userApi.UserUserIdTransactionIdDeleteAsync(userId.ToString(), parsedId);
        await FollowupAsync("", ephemeral: true, embed: new EmbedBuilder()
            .WithTitle("Reverted " + transactionId)
            .WithDescription($"Reverted transaction {transactionId} for {userId}, changed {transaction.Amount} {transaction.Id} - {transaction.Reference}")
            .WithColor(Color.Green)
            .Build());
    }

    private async Task<string> NewMethod(string user)
    {
        await DeferAsync(true);
        if (!ulong.TryParse(user.Replace("#", ""), out var userId))
        {
            userId = await GetUserIdFromMcName(user);
        }
        else if (user.Contains("#"))
        {
            return user; // is a license user
        }
        if (userId > 10000000000)
        {
            // discord id, try to get
            var discordInfo = await persistence.GetDiscordAccountInfo(userId);
            if (discordInfo == null)
            {
                await FollowupAsync("No user found with that id");
                return null;
            }
            userId = await GetUserIdFromMcName(discordInfo.MinecraftName);
        }
        return userId.ToString();
    }


    private async Task<ulong> GetUserIdFromMcName(string user)
    {
        var uuid = (await searchApi.ApiSearchPlayerPlayerNameGetAsync(user)).First().Uuid;
        var connect = await connectApi.ConnectMinecraftMcUuidGetAsync(uuid);
        if (connect == null || string.IsNullOrEmpty(connect.ExternalId))
        {
            await FollowupAsync("No user found with that name");
            return 0;
        }
        return ulong.Parse(connect.ExternalId);
    }

    private bool DoesNotMatchExecutor(ProfileClient.HypixelProfile profile)
    {
        return profile.SocialMedia?.Links.Where(l => l.Key.ToLower() == "discord").FirstOrDefault().Value != Context.Interaction.User.Username;
    }

    [MessageCommand("Mute for rule 1")]
    [Discord.Interactions.RequireUserPermission(ChannelPermission.ManageRoles)]
    public async Task MuteForRule1(IMessage message)
    {
        await ExecuteMute(message, 1);
    }

    private async Task ExecuteMute(IMessage message, int ruleId)
    {
        await DeferAsync();
        var userRoles = (Context.User as SocketGuildUser).Roles;
        if (!userRoles.Any(r => r.Name.ToLower() == "mod"))
        {
            Console.WriteLine(string.Join(", ", userRoles.Select(r => r.Name)));
            await FollowupAsync("You need to be a moderator to use this command", ephemeral: true);
            return;
        }
        var user = message.Author as SocketWebhookUser;
        var displayName = user.Username;
        var apiResult = await searchApi.ApiSearchPlayerPlayerNameGetAsync(displayName);
        var uuid = apiResult.First().Uuid;

        await chatService.Mute(new()
        {
            Muter = Context.User.Id.ToString(),
            Reason = $"rule {ruleId}",
            Uuid = uuid,
            Message = $"Violating rule {ruleId} with message \"{message.Content}\""
        });

        await FollowupAsync($"Muted {displayName} for rule {ruleId}");
    }

    [MessageCommand("Mute for rule 2")]
    [Discord.Commands.RequireUserPermission(ChannelPermission.ManageRoles)]
    public async Task MuteForRule2(IMessage message)
    {
        await ExecuteMute(message, 2);
    }

    [AutocompleteCommand("name", "update-mc-user")]
    public async Task Autocomplete()
    {
        logger.LogInformation("Searching players ");
        string userInput = (Context.Interaction as SocketAutocompleteInteraction).Data.Current.Value.ToString();
        if (string.IsNullOrEmpty(userInput))
        {
            await (Context.Interaction as SocketAutocompleteInteraction).RespondAsync(new AutocompleteResult[] { new AutocompleteResult("Technoblade", "b876ec32e396476ba1158438d83c67d4") });
            return;
        }
        var apiResult = await searchApi.ApiSearchPlayerPlayerNameGetAsync(userInput);
        IEnumerable<AutocompleteResult> results = apiResult.Select(a => new AutocompleteResult(a.Name, a.Uuid));

        await (Context.Interaction as SocketAutocompleteInteraction).RespondAsync(results.Take(25));
    }
}
