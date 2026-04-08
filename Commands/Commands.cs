

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Discord;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.ModCommands.Client.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Octokit.GraphQL;
using RestSharp;

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
    IVpsApi vpsApi;
    IConfiguration configuration;
    public Commands(ISearchApi searchApi,
                    ILogger<Commands> logger,
                    ProfileClient profileClient,
                    Persistence persistence,
                    Coflnet.Payments.Client.Api.ITransactionApi transactionApi,
                    IConnectApi connectApi,
                    UserInfoUpdater userInfoUpdater,
                    ChatService chatService,
                    Coflnet.Payments.Client.Api.IUserApi userApi,
                    Coflnet.Payments.Client.Api.ITopUpApi topUpApi,
                    IConfiguration configuration,
                    IVpsApi vpsApi)
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
        this.configuration = configuration;
        this.vpsApi = vpsApi;
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
    public async Task UpdateMcUser([Summary("name", "Minecraft user name"), Autocomplete(typeof(McNameAutocompleteHandler))] string userName)
    {
        await DeferAsync(ephemeral: true);
        var user = (await searchApi.ApiSearchPlayerPlayerNameGetAsync(userName)).First();
        await FollowupAsync("", embed: new EmbedBuilder()
            .WithTitle("Checking ownership")
            .WithDescription($"Updating Minecraft user `{user.Name}` with UUID `{user.Uuid}`")
            .WithColor(Color.Blue)
            .Build(), ephemeral: true);
        var profile = await profileClient.GetLookup(user.Uuid);

        if (DoesNotMatchExecutor(profile))
        {
            profile = await profileClient.GetLookup(user.Uuid, true);
        }
        if (DoesNotMatchExecutor(profile))
        {
            logger.LogInformation("Profile data: " + Newtonsoft.Json.JsonConvert.SerializeObject(profile));
            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = new EmbedBuilder()
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
                    .Build();
            });
            return;
        }

        var existing = await persistence.GetDiscordAccountInfo(Context.Interaction.User.Id) ?? new DiscordAccountInfo();
        if (Context.Client is DiscordSocketClient socketClient)
        {
            userInfoUpdater.SetDiscordClient(socketClient);
        }
        else
        {
            logger.LogWarning("Client is not a DiscordSocketClient, cannot set discord client");
        }
        await userInfoUpdater.UpdateuserDetails(Context.Interaction.User.Id, user, existing);
        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Embed = new EmbedBuilder()
                .WithTitle("Success")
                .WithDescription($"Your Minecraft account `{user.Name}` has been linked to your Discord account")
                .WithColor(Color.Green)
                .Build();
        });
    }

    [SlashCommand("run", "Run a command as one of your minecraft accounts", true)]
    public async Task RunCommand([Summary("command", "The command to run")] string command, [Summary("player", "Command to run"), Autocomplete,] string? playerName = null)
    {
        await DeferAsync(ephemeral: true);
        var profile = await persistence.GetDiscordAccountInfo(Context.Interaction.User.Id);
        if (profile == null)
        {
            await FollowupAsync("You have no linked any Minecraft accounts, please run `/update-mc-user` first");
            return;
        }
        if (playerName == null)
        {
            playerName = profile?.MinecraftName;
            if (playerName == null)
            {
                await FollowupAsync("You have no linked any Minecraft accounts, please run `/update-mc-user` first");
                return;
            }
        }
        var user = (await searchApi.ApiSearchPlayerPlayerNameGetAsync(playerName)).First();
        if (user == null)
        {
            await FollowupAsync("No user found with that name");
            return;
        }
        var accountUuid = Guid.Parse(user.Uuid);
        if (!profile!.MinecraftUuids.Contains(accountUuid))
        {
            await FollowupAsync("", embed: new EmbedBuilder()
                .WithTitle("Error")
                .WithDescription(
                $"""
                The player `{user.Name}` is not linked to your Discord account.
                Join Hypixel and follow these steps to set your Discord link:

                1. Click on My Profile (Right Click) in a Hypixel lobby
                2. Click on `Social Media` (Player head next to compas)
                3. Left-click on `Discord`
                4. Paste this in the Minecraft ingame chat: {Context.Interaction.User.Username}
                5. Run `/update-mc-user` command
                """)
                .WithColor(Color.Red)
                .Build());
            return;
        }
        await vpsApi.VpsExecutePostAsync(new(new()
        {
            Command = command,
            MinecraftName = playerName,
            UserId = Context.Interaction.User.Id.ToString(),
        }));
        await FollowupAsync($"Sent comand to be executed as `{playerName}` nothing will happen if there is no connection");
    }


    [SlashCommand("transactions", "List a users transactions", true)]
    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.PrivateChannel, InteractionContextType.BotDm, InteractionContextType.Guild)]
    [DefaultMemberPermissions(GuildPermission.ManageRoles)]
    public async Task GetTransactions(string user)
    {
        var roles = (Context.User as SocketGuildUser)?.Roles;
        if (Context.User.Id != 267680402594988033 && !roles.Any(r => r.Id == 869942341442600990 || r.Id == 842102236024930304))
        {
            logger.LogWarning("User {userId} ({id}) tried to get transactions of {user}", Context.User.GlobalName, Context.User.Id, user);
            await RespondAsync("You need to be a moderator to use this command", ephemeral: true);
            return;
        }
        try
        {
            var userId = await FindUserId(user);
            if (userId == null)
            {
                return;
            }
            logger.LogInformation("User {userId} ({id}) requested transactions of {user}", Context.User.GlobalName, Context.User.Id, userId);
            var transactions = await transactionApi.TransactionUUserIdGetAsync(userId, 0, 10);
            await FollowupAsync("", ephemeral: true, embed: new EmbedBuilder()
                .WithTitle("Transactions for " + userId)
                .WithDescription(string.Join("\n", transactions.Select(t => $"{t.Id} {t.TimeStamp} {t.Amount} {t.ProductId} - {t.Reference}")))
                .WithColor(Color.Green)
                .Build());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing transactions command for {user}", user);
            try { await FollowupAsync("An error occurred while fetching transactions", ephemeral: true); } catch { }
        }
    }

    [SlashCommand("compensate", "Compensate a user", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireRole(842102236024930304)]
    public async Task Compensate(string user, string amount, string reason)
    {
        try
        {
            var userId = await FindUserId(user);
            if (userId == null)
            {
                return;
            }
            if (!int.TryParse(amount, out var parsedAmount))
            {
                await FollowupAsync("Invalid amount");
                return;
            }
            await topUpApi.TopUpCustomPostAsync(userId, new()
            {
                Amount = parsedAmount,
                ProductId = "compensation",
                Reference = reason
            });
            logger.LogInformation("User {userId} ({id}) compensated {user} with {amount} by {executor}", Context.User.GlobalName, Context.User.Id, userId, parsedAmount, Context.User.Id);
            await FollowupAsync("", ephemeral: true, embed: new EmbedBuilder()
                .WithTitle("Compensated " + userId)
                .WithDescription($"Compensated {userId} with {parsedAmount} - {reason}")
                .WithColor(Color.Green)
                .Build());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing compensate command for {user}", user);
            try { await FollowupAsync("An error occurred while compensating user", ephemeral: true); } catch { }
        }
    }

    [SlashCommand("delete-bot-messages", "Delete messages from a bot/app in the current channel", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [CommandContextType(InteractionContextType.Guild)]
    public async Task DeleteBotMessages([Summary("bot", "The bot/app user to delete messages from")] IUser bot, [Summary("limit", "Number of messages to check (max 100)")] int limit = 100)
    {
        await DeferAsync(ephemeral: true);

        if (!bot.IsBot)
        {
            await FollowupAsync("The specified user is not a bot", ephemeral: true);
            return;
        }

        if (limit < 1 || limit > 100)
        {
            await FollowupAsync("Limit must be between 1 and 100", ephemeral: true);
            return;
        }

        var channel = Context.Channel as ITextChannel;
        if (channel == null)
        {
            await FollowupAsync("This command can only be used in text channels", ephemeral: true);
            return;
        }

        var messages = await channel.GetMessagesAsync(limit).FlattenAsync();
        var botMessages = messages.Where(m => m.Author.Id == bot.Id && (DateTimeOffset.UtcNow - m.CreatedAt).TotalDays < 14).ToList();

        if (botMessages.Count == 0)
        {
            await FollowupAsync($"No messages from {bot.Username} found in the last {limit} messages", ephemeral: true);
            return;
        }

        await channel.DeleteMessagesAsync(botMessages);

        await FollowupAsync($"Deleted {botMessages.Count} message(s) from {bot.Username}", ephemeral: true);
    }

    [SlashCommand("revert", "Revert a transactions", true)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [RequireRole(842102236024930304)]
    public async Task RevertTransaction(string user, string transactionId)
    {
        try
        {
            var userId = await FindUserId(user);
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing revert command for {user}", user);
            try { await FollowupAsync("An error occurred while reverting transaction", ephemeral: true); } catch { }
        }
    }

    private async Task<string?> FindUserId(string user)
    {
        try
        {
            await DeferAsync(true);
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.InteractionHasAlreadyBeenAcknowledged)
        {
            // Already acknowledged, continue
        }
        if (user.Contains('@'))
        {
            return await GetUserFromEmail(user);
        }
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
            if (discordInfo?.MinecraftName == null)
            {
                await FollowupAsync("No user found with that id (or not verified)");
                return null;
            }
            userId = await GetUserIdFromMcName(discordInfo.MinecraftName);
        }
        return userId.ToString();
    }

    private async Task<string?> GetUserFromEmail(string user)
    {
        var restClient = new RestClient(configuration["INDEXER_BASE_URL"]);
        var request = new RestRequest("/user/" + user, Method.Get);
        var response = await restClient.ExecuteAsync(request);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            await FollowupAsync("No user found with that email");
            return null;
        }
        var userInfo = Newtonsoft.Json.JsonConvert.DeserializeObject<UserInfo>(response.Content);
        if (userInfo == null)
        {
            await FollowupAsync("No user found with that email");
            return null;
        }
        return userInfo.Id.ToString();
    }
    public class UserInfo
    {
        public int Id { get; set; }
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
        return profile?.SocialMedia?.Links.Where(l => l.Key.ToLower() == "discord").FirstOrDefault().Value != Context.Interaction.User.Username;
    }

    [MessageCommand("Mute for rule 1")]
    [CommandContextType(InteractionContextType.Guild)]
    [RequireUserPermission(ChannelPermission.ManageRoles)]
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
}
