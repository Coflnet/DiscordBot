

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Discord;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

public class Commands : InteractionModuleBase
{
    ISearchApi searchApi;
    ILogger<Commands> logger;
    ProfileClient profileClient;
    Persistence persistence;
    UserInfoUpdater userInfoUpdater;
    ChatService chatService;
    public Commands(ISearchApi searchApi,
                    ILogger<Commands> logger,
                    ProfileClient profileClient,
                    Persistence persistence,
                    Coflnet.Payments.Client.Api.IUserApi userApi,
                    IConnectApi connectApi,
                    UserInfoUpdater userInfoUpdater,
                    ChatService chatService)
    {
        this.searchApi = searchApi;
        this.logger = logger;
        this.profileClient = profileClient;
        this.persistence = persistence;
        this.userInfoUpdater = userInfoUpdater;
        this.chatService = chatService;
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
