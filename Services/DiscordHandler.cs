

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text;
using Cassandra.Data.Linq;
using Coflnet.Discord;
using Coflnet.Sky.Chat.Client.Model;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;

internal class DiscordHandler : BackgroundService
{
    private readonly ILogger<DiscordHandler> logger;
    private readonly IConfiguration _config;
    private DiscordSocketClient client;
    private IServiceProvider _serviceProvider;
    private ChatService chatService;
    private List<string> ChatWebhooks = new();
    private Persistence persistence;

    public DiscordHandler(ILogger<DiscordHandler> logger, IConfiguration config, IServiceProvider serviceProvider, ChatService chatService, Persistence persistence)
    {
        this.logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        this.chatService = chatService;
        this.persistence = persistence;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Debug,
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = true
        });
        await client.LoginAsync(TokenType.Bot, _config["BotToken"]);
        // set intent to receive message
        await client.StartAsync();
        client.Ready += Init;


        client.MessageReceived += async (msg) =>
        {
            try
            {
                if (msg.Author.IsBot) return;
                await OnMessage(msg);
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "Error handling message");
            }
        };
        client.MessageCommandExecuted += MessageCommandHandler;
        var sub = await chatService.Subscribe(OnMcChatMessage);
        logger.LogInformation("Discord bot started");

        await Task.Delay(-1, stoppingToken);
        sub.Unsubscribe();
    }

    private async Task MessageCommandHandler(SocketMessageCommand command)
    {
        logger.LogInformation($"Command {command.CommandName} executed by {command.User.Username} on {command.Data.Message.Content} by {command.Data.Message.Author.Username}");
        await command.RespondAsync("sorry, this isn't a thing yet");
    }

    private bool OnMcChatMessage(ChatMessage message)
    {
        TryRun(async () =>
        {
            var profilePicture = $"https://mc-heads.net/avatar/{message.Uuid}";
            foreach (var target in ChatWebhooks)
            {
                var content = JsonConvert.SerializeObject(new
                {
                    content = message.Message,
                    username = message.Name ?? "user",
                    avatar_url = profilePicture,
                    // prevent mentions
                    allowed_mentions = new
                    {
                        parse = new string[] { }
                    }
                });
                using var client = new HttpClient();
                var response = await client.PostAsync(target, new StringContent(content, Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogError("Failed to send message to discord");
                }
            }
            var account = persistence.GetDiscordAccountInfoByMcUuid(Guid.Parse(message.Uuid));
            if (account == default)
            {
                using var scope = _serviceProvider.CreateScope();
                var updater = scope.ServiceProvider.GetRequiredService<UserInfoUpdater>();
                await updater.UpdateUserDetails(client, message.Uuid, message.Name);
            }
        });
        return true;
    }

    private void TryRun(Func<Task> action)
    {
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error executing");
            }
        });
    }

    private async Task Init()
    {
        try
        {
            var guildId = ulong.Parse(_config["GUILD_ID"]);
            var guild = client.GetGuild(guildId);
            var _interactionService = new InteractionService(client.Rest);
            await _interactionService.AddModuleAsync<Commands>(_serviceProvider);
            if (guild == null)
            {
                logger.LogError("Guild not found");
                return;
            }
            foreach (var c in guild.Channels.ToList())
            {
                if (c.Name == "in-game-chat")
                {
                    // list webhooks
                    var channel = await client.GetChannelAsync(c.Id);
                    var webhooks = await (channel as ITextChannel).GetWebhooksAsync();
                    if (webhooks.Count == 0)
                    {
                        var webhook = await (channel as ITextChannel).CreateWebhookAsync("Minecraft Chat");
                        ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
                    }
                    else
                    {
                        var webhook = webhooks.First();
                        ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
                    }
                }
            }

            await _interactionService.RegisterCommandsToGuildAsync(guildId, true);
            _interactionService.Log += Log;

            await client.SetActivityAsync(new Game("being developed ...", ActivityType.Watching, ActivityProperties.Embedded, "at hyperspeed"));

            client.InteractionCreated += async interaction =>
            {
                var scope = _serviceProvider.CreateScope();
                var ctx = new SocketInteractionContext(client, interaction);
                await _interactionService.ExecuteCommandAsync(ctx, scope.ServiceProvider);
            };
        }
        catch (Exception exception)
        {

            // You can send this error somewhere or just print it to the console, for this example we're just going to print it.
            Console.WriteLine(exception);
        }
        logger.LogInformation("Discord bot ready");
    }

    private async Task Log(LogMessage message)
    {
        logger.LogInformation(message.ToString());
    }

    private async Task OnMessage(SocketMessage msg)
    {
        var channelName = (msg.Channel as SocketGuildChannel)?.Name;
        Console.WriteLine(msg.Content + " in " + channelName);

        if (channelName == "in-game-chat")
        {
            var profile = await persistence.GetDiscordAccountInfo(msg.Author.Id);
            if (profile == default)
            {
                await msg.ReplyAsync("", embed: new EmbedBuilder()
                    .WithTitle("You need to link your Minecraft account")
                    .WithDescription("To do so run **/update-mc-user** ")
                    .WithColor(Color.Red)
                    .Build());
                return;
            }
            try
            {
                await chatService.Send(new()
                {
                    SenderUuid = "384a029294fc445e863f2c42fe9709cb",
                    Message = msg.Content,
                    SenderName = msg.Author.Username
                });
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "Error sending message to chat");
                await msg.ReplyAsync("Could not send message, <@267680402594988033>");
            }
            return;
        }
    }
}

public static class DiscordExtensions
{
    public static async Task ReplyAsync(this IMessage message, string content, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null, MessageComponent component = null)
    {
        await message.Channel.SendMessageAsync(content, isTTS, embed, options, allowedMentions, new MessageReference(message.Id), component);
    }
}