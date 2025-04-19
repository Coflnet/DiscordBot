

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Cassandra.Data.Linq;
using Coflnet.Core;
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
    private HashSet<string> ChatWebhooks = new();
    private Persistence persistence;
    private UserInfoUpdater userInfoUpdater;
    private Dictionary<string, string[]> QuickResponses = new(){
        {"!new-user", ["## (Quick tutorial for MORE flips)",
        "1. Use /cofl setgui tfm (Makes it so you don't have to move your mouse while buying)",
        "2. When there is a BED (A countdown auction) Don't spam your mouse like crazy, but rather keep it at either 8-10~ cps. (Because of a hypixel mechanic it will just not register any other clicks and it will seem like you are just not clicking in the gui)",
        "3. Set a Keybind to open next/best flip in your Minecraft settings (under the skycofl section). When holding the keybind it automatically opens the flip without having to click on the flip message (not bannable)",
        "## (Premium+ advice)",
        "4. Use the /cofl switchregion us instance for lower ping (if your playing minecraft from America)  "
        ]},
        {"!wm", new[]{
            "Using the mod over the website to flip is better and faster, because you can get the flips in-game, without having to copy the link from the website, saving you a lot of time"
        } }
    };

    public DiscordHandler(ILogger<DiscordHandler> logger, IConfiguration config, IServiceProvider serviceProvider, ChatService chatService, Persistence persistence, UserInfoUpdater userInfoUpdater)
    {
        this.logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        this.chatService = chatService;
        this.persistence = persistence;
        this.userInfoUpdater = userInfoUpdater;
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
        var sub = await chatService.Subscribe(OnMcChatMessage);
        logger.LogInformation("Discord bot started");

        await Task.Delay(-1, stoppingToken);
        sub.Unsubscribe();
    }


    private bool OnMcChatMessage(ChatMessage message)
    {
        if (message.ClientName == "cofl-discord")
            return true; // abort self sent messages
        TryRun(async () =>
        {
            var profilePicture = $"https://mc-heads.net/avatar/{message.Uuid}";
            // replace all §[a-f0-9] with empty string
            var messageContent = Regex.Replace(message.Message, "§[a-f0-9]", "");
            foreach (var target in ChatWebhooks)
            {
                var content = JsonConvert.SerializeObject(new
                {
                    content = messageContent,
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
                logger.LogInformation("Sent message to discord {msg}", messageContent);
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
            var guildId = ulong.Parse(_config["GUILD_ID"] ?? throw new Exception("Guild ID not set"));
            var guild = client.GetGuild(guildId);
            var _interactionService = new InteractionService(client.Rest);
            await _interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), _serviceProvider);
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

            client.JoinedGuild += async (guild) =>
            {
                try
                {
                    logger.LogInformation("Joined new guild: {guildName} ({guildId})", guild.Name, guild.Id);
                    await SetupGuildIntegration(guild, _interactionService);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error handling guild join for guild {guildId}", guild.Id);
                }
            };

            foreach (var item in  client.Guilds)
            {
                try
                {
                    logger.LogInformation("Setting up guild integration for guild {guildName} ({guildId})", item.Name, item.Id);
                    await SetupGuildIntegration(item, _interactionService);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error setting up guild integration for guild {guildId}", item.Id);
                }
            }
        }
        catch (Exception exception)
        {

            // You can send this error somewhere or just print it to the console, for this example we're just going to print it.
            Console.WriteLine(exception);
        }
        logger.LogInformation("Discord bot ready");
    }

    private async Task SetupGuildIntegration(SocketGuild guild, InteractionService _interactionService)
    {
        await _interactionService.RegisterCommandsToGuildAsync(guild.Id, true);

        // Find or create in-game-chat webhook for the new server
        var chatChannel = guild.Channels.FirstOrDefault(c => c.Name == "in-game-chat") as ITextChannel;
        if (chatChannel != null)
        {
            var webhooks = await chatChannel.GetWebhooksAsync();
            if (webhooks.Count == 0)
            {
                var webhook = await chatChannel.CreateWebhookAsync("Minecraft Chat");
                ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
            }
            else
            {
                var webhook = webhooks.First();
                ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
            }
            logger.LogInformation("Set up in-game-chat webhook for guild {guildId}", guild.Id);
        }
    }

    private async Task Log(LogMessage message)
    {
        logger.LogInformation(message.ToString());
    }

    private async Task OnMessage(SocketMessage msg)
    {
        // ignore messages from bots and webhooks
        if (msg.Author.IsBot || msg.Author.IsWebhook) return;
        var channelName = (msg.Channel as SocketGuildChannel)?.Name;
        Console.WriteLine(msg.Content + " in " + channelName);
        if (msg.Content.Contains("steamcommunity.com"))
        {
            // delete steam links
            await msg.DeleteAsync();
            await msg.Author.SendMessageAsync("You posted a scam link (probably got hacked). Please secure your account. You can rejoin Coflnet discord in 10 minutes via the link on https://sky.coflent.com");
            var kickTask = (msg.Author as SocketGuildUser)?.KickAsync();
            if (kickTask != null)
                await kickTask;
            return;
        }
        if (msg.Content.Contains("@everyone"))
        {
            await msg.DeleteAsync();
            if (msg.Author is SocketGuildUser guildUser)
            {
                await guildUser.SetTimeOutAsync(TimeSpan.FromHours(1));
                await msg.Author.SendMessageAsync("You have been timed out for 1 hour for using @everyone");
            }
            return;
        }

        if (channelName == "in-game-chat")
        {
            await HandleInGameChat(msg);
            return;
        }
        if (QuickResponses.ContainsKey(msg.Content))
        {
            var responses = QuickResponses[msg.Content];
            await msg.Channel.SendMessageAsync(string.Join("\n", responses), messageReference: msg.Reference);
            await msg.DeleteAsync(new() { AuditLogReason = "Quick response" });
        }
    }

    private async Task HandleInGameChat(SocketMessage msg)
    {
        var profile = await persistence.GetDiscordAccountInfo(msg.Author.Id);
        if (profile == default)
        {
            await msg.ReplyAsync("", embed: new EmbedBuilder()
                .WithTitle("You need to select your Minecraft account")
                .WithDescription("To do so run **/update-mc-user** ")
                .WithColor(Color.Red)
                .Build());
            if (!msg.Content.Contains('<')) // only keep messages with pings
                await msg.DeleteAsync();
            return;
        }
        if (msg.Content.StartsWith("/update-mc-user"))
        {
            await msg.ReplyAsync("Please type the command manually and wait for discord to recognize it");
            return;
        }
        var message = msg.Content;
        message = await ReplacePingsIgn(message);
        try
        {
            if (profile.ExpiresAt < DateTime.UtcNow)
            {
                await userInfoUpdater.UpdatePremiumTierAndSave(profile);
            }
            await chatService.Send(new()
            {
                SenderUuid = profile.MinecraftUuid.ToString("n"),
                Message = message,
                SenderName = profile?.MinecraftName ?? msg.Author.Username,
                AccountTier = profile?.AccountTier ?? AccountTier.NONE
            });
        }
        catch (Coflnet.Core.ApiException e)
        {
            var handle = await msg.ReplyAsync(e.Message);
            await msg.DeleteAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(30000);
                await handle.DeleteAsync();
            });
        }
        catch (System.Exception e)
        {
            logger.LogError(e, "Error sending message to chat");
            await msg.ReplyAsync("Could not send message, <@267680402594988033> ");
        }
    }

    private async Task<string> ReplacePingsIgn(string message)
    {
        var pings = message.Split(" ").Where(x => x.StartsWith("<@") && x.EndsWith(">"));
        foreach (var ping in pings)
        {
            var id = ulong.Parse(ping.Substring(2, ping.Length - 3));
            logger.LogInformation("Ping: " + id);
            var account = await persistence.GetDiscordAccountInfo(id);
            if (account != default)
            {
                message = message.Replace(ping, account.MinecraftName);
                logger.LogInformation("Replaced: " + account.MinecraftName);
            }
        }

        return message;
    }
}

public static class DiscordExtensions
{
    public static async Task<IUserMessage> ReplyAsync(this IMessage message, string content, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null, MessageComponent component = null)
    {
        return await message.Channel.SendMessageAsync(content, isTTS, embed, options, allowedMentions, new MessageReference(message.Id), component);
    }
}