

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Collections.Concurrent;
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
using Discord.Audio;
using Discord.WebSocket;
using Newtonsoft.Json;
using Coflnet.DiscordBot.Phone;

public class DiscordHandler : BackgroundService
{
    private const int RealtimeVoiceBufferMilliseconds = 100;
    private const int PhoneAcknowledgementPcmBytes = 48000 * 2 * sizeof(short) * 2;
    private static readonly TimeSpan PhoneAcknowledgementTimeout = TimeSpan.FromSeconds(15);
    private readonly ILogger<DiscordHandler> logger;
    private readonly IConfiguration _config;
    private DiscordSocketClient? client;
    private InteractionService? interactionService;
    private readonly SemaphoreSlim interactionInitializationLock = new(1, 1);
    private bool interactionServiceInitialized;
    private int phoneStateRecovered;
    private IServiceProvider _serviceProvider;
    private ChatService chatService;
    private HashSet<string> ChatWebhooks = new();
    private Persistence persistence;
    private UserInfoUpdater userInfoUpdater;
    private FaqService faqService;
    private Dictionary<string, string[]> QuickResponses = new(){
        {"!new-user", ["## (Quick tutorial for MORE flips)",
        "1. Use /cofl setgui cofl (Makes it so you don't have to move your mouse while buying)",
        "2. When there is a BED (A countdown auction) Don't spam your mouse like crazy, but rather keep it at either 8-10~ cps. (Because of a hypixel mechanic it will just not register any other clicks and it will seem like you are just not clicking in the gui)",
        "3. Set a Keybind to open next/best flip in your Minecraft settings (under the skycofl section). When holding the keybind it automatically opens the flip without having to click on the flip message (not bannable)",
        "## (Premium+ advice)",
        "4. Use the /cofl switchregion us instance for lower ping (if your playing minecraft from America)  "
        ]},
        {"!wm", new[]{
            "Using the mod over the website to flip is better and faster, because you can get the flips in-game, without having to copy the link from the website, saving you a lot of time"
        } }
    };

    // Nitro scam link regex (ported from Node.js bot)
    private static readonly Regex NitroRegex = new(
        @"((.*http.*)(.*nitro.*))|((.*nitro.*)(.*http.*))|((.*http.*)(.*gift.*))|((.*gift.*)(.*http.*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // One-word spam tracking per user (ported from Node.js bot)
    private readonly ConcurrentDictionary<ulong, DateTime> _oneWordMessageTimes = new();

    // Multi-channel image spam tracking: recent image posts per user across channels.
    // A compromised account typically dumps the same scam image into several channels
    // within a couple of minutes - something a normal user never does.
    private readonly ConcurrentDictionary<ulong, List<ImagePost>> _recentImagePosts = new();

    // How close together image posts must be to count as one burst.
    private static readonly TimeSpan MultiChannelImageWindow = TimeSpan.FromMinutes(2);

    // Extra false-positive guard: only auto-kick when the user posted nothing in the
    // weeks before the burst (throwaway / freshly compromised account pattern).
    private static readonly TimeSpan PriorActivityWindow = TimeSpan.FromDays(14);

    // A single image post used to correlate a burst across channels.
    private readonly record struct ImagePost(ulong ChannelId, ulong ContextChannelId, ulong MessageId, DateTime Time);

    // Exempt roles that bypass one-word spam detection
    private static readonly string[] ExemptRoles = { "669258959495888907", "869942341442600990", "933807456151285770", "893869139129692190", "941738849808298045" };

    // Channel IDs for auto-thread creation (configurable via appsettings)
    private ulong _supportChannelId;
    private ulong _bugReportChannelId;

    public DiscordHandler(ILogger<DiscordHandler> logger, IConfiguration config, IServiceProvider serviceProvider, ChatService chatService, Persistence persistence, UserInfoUpdater userInfoUpdater, FaqService faqService)
    {
        this.logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        this.chatService = chatService;
        this.persistence = persistence;
        this.userInfoUpdater = userInfoUpdater;
        this.faqService = faqService;

        // Load auto-thread channel IDs from config
        _supportChannelId = config.GetValue<ulong>("CHANNEL_ID_SUPPORT");
        _bugReportChannelId = config.GetValue<ulong>("CHANNEL_ID_BUGREPORT");

        // Load FAQ data
        var faqPath = config["FAQ_PATH"] ?? "Configuration/faq.json";
        faqService.Load(Path.Combine(AppContext.BaseDirectory, faqPath));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Debug,
            // GuildScheduledEvents is in AllUnprivileged but we don't handle those events
            // (Discord.Net warns about it), so drop it.
            GatewayIntents = (GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent) & ~GatewayIntents.GuildScheduledEvents,
            // AlwaysDownloadUsers triggers a member download across every guild on READY,
            // which requires the privileged GuildMembers intent (not enabled) so it throws,
            // and stalls guild availability - causing "Unknown Channel" races on startup.
            // The code never enumerates the member cache, so keep it off.
            AlwaysDownloadUsers = false,
            EnableVoiceDaveEncryption = true
        });
        interactionService = new InteractionService(client.Rest);
        interactionService.Log += Log;
        client.Log += Log;
        client.Ready += Init;
        client.Disconnected += OnDisconnected;
        client.MessageReceived += OnMessageReceived;
        client.InteractionCreated += OnInteractionCreated;
        client.JoinedGuild += OnJoinedGuild;
        await client!.LoginAsync(TokenType.Bot, _config["BotToken"]);
        // set intent to receive message
        await client.StartAsync();
        var sub = await chatService.Subscribe(OnMcChatMessage);
        logger.LogInformation("Discord bot starting, waiting for gateway to become ready (not connected yet)");

        await Task.Delay(-1, stoppingToken);
        sub.Unsubscribe();
    }

    public async Task<IEnumerable<IMessage>> GetMessagesFromChannel(ulong channelId, ulong beforeMessageId = 0, int limit = 100)
    {
        if (await client!.GetChannelAsync(channelId) is not IMessageChannel channel)
        {
            logger.LogError("Channel with ID {id} not found", channelId);
            return [];
        }
        if (beforeMessageId != 0)
        {
            return await channel.GetMessagesAsync(beforeMessageId, Direction.Before, limit: limit).FlattenAsync();
        }
        return await channel.GetMessagesAsync(limit: limit).FlattenAsync();
    }

    public async Task<IMessage?> GetMessageFromChannel(ulong channelId, ulong messageId)
    {
        if (await client!.GetChannelAsync(channelId) is not IMessageChannel channel)
        {
            logger.LogError("Channel with ID {id} not found", channelId);
            return null;
        }
        try
        {
            return await channel.GetMessageAsync(messageId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to get message {messageId} from channel {channelId}", messageId, channelId);
            return null;
        }
    }

    public bool IsUserInVoiceChannel(ulong userId, ulong channelId)
    {
        return client?.GetChannel(channelId) is SocketVoiceChannel channel
            && channel.ConnectedUsers.Any(user => user.Id == userId);
    }

    public async Task<DiscordVoiceSession?> AnnounceAndMoveAsync(
        ulong userId,
        ulong waitingChannelId,
        ulong privateChannelId,
        ReadOnlyMemory<byte> noticePcm,
        CancellationToken cancellationToken)
    {
        if (client?.GetChannel(waitingChannelId) is not SocketVoiceChannel waitingChannel
            || client.GetChannel(privateChannelId) is not SocketVoiceChannel privateChannel
            || waitingChannel.Guild.Id != privateChannel.Guild.Id
            || privateChannel.ConnectedUsers.Any(user => user.Id != client.CurrentUser.Id)
            || waitingChannel.ConnectedUsers.FirstOrDefault(user => user.Id == userId) is not { } target)
            return null;

        var botPermissions = privateChannel.Guild.CurrentUser.GetPermissions(privateChannel);
        if (!botPermissions.Connect || !botPermissions.Speak)
        {
            logger.LogError("The Discord bot needs Connect and Speak permissions in the private phone channel");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var waitingAudio = await waitingChannel.ConnectAsync(selfDeaf: false, selfMute: false);
        var moved = false;
        try
        {
            await using (var notice = waitingAudio.CreatePCMStream(
                AudioApplication.Voice,
                bufferMillis: RealtimeVoiceBufferMilliseconds))
            {
                await notice.WriteAsync(noticePcm, cancellationToken);
                await notice.FlushAsync(cancellationToken);
            }

            logger.LogInformation("Waiting for phone-call microphone acknowledgement from Discord user {userId}", userId);
            if (!await WaitForMicrophoneInputAsync(waitingAudio, userId, cancellationToken)
                || !waitingChannel.ConnectedUsers.Any(user => user.Id == userId)
                || privateChannel.ConnectedUsers.Any(user => user.Id != client.CurrentUser.Id))
            {
                logger.LogInformation("Phone-call handoff was not accepted by Discord user {userId}", userId);
                await waitingChannel.DisconnectAsync();
                return null;
            }

            await waitingChannel.Guild.MoveAsync(target, privateChannel);
            moved = true;
            await waitingChannel.DisconnectAsync();
            var privateAudio = await privateChannel.ConnectAsync(selfDeaf: false, selfMute: false);
            return new DiscordVoiceSession(privateAudio, client.CurrentUser.Id);
        }
        catch
        {
            try
            {
                await waitingChannel.DisconnectAsync();
            }
            finally
            {
                if (moved)
                    await waitingChannel.Guild.MoveAsync(target, waitingChannel);
            }
            throw;
        }
    }

    private static async Task<bool> WaitForMicrophoneInputAsync(
        IAudioClient audioClient,
        ulong userId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PhoneAcknowledgementTimeout);
        var streamReady = new TaskCompletionSource<AudioInStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task StreamCreated(ulong streamUserId, AudioInStream stream)
        {
            if (streamUserId == userId)
                streamReady.TrySetResult(stream);
            return Task.CompletedTask;
        }

        audioClient.StreamCreated += StreamCreated;
        try
        {
            foreach (var stream in audioClient.GetStreams())
                if (stream.Key == userId)
                    streamReady.TrySetResult(stream.Value);

            var input = await streamReady.Task.WaitAsync(timeout.Token);
            var buffer = new byte[3840];
            var received = 0;
            while (received < PhoneAcknowledgementPcmBytes)
            {
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    return false;
                received += read;
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            audioClient.StreamCreated -= StreamCreated;
        }
    }

    public async Task EndCallAsync(
        ulong userId,
        ulong privateChannelId,
        ulong waitingChannelId,
        DiscordVoiceSession? session)
    {
        var privateChannel = client?.GetChannel(privateChannelId) as SocketVoiceChannel;
        var waitingChannel = client?.GetChannel(waitingChannelId) as SocketVoiceChannel;
        try
        {
            var guild = privateChannel?.Guild ?? waitingChannel?.Guild;
            if (guild?.GetUser(userId) is { } target && waitingChannel is not null)
            {
                logger.LogInformation("Returning Discord user {userId} to the phone waiting room", userId);
                await guild.MoveAsync(target, waitingChannel);
            }
        }
        finally
        {
            if (privateChannel is not null)
                await privateChannel.DisconnectAsync();
            else if (session is not null)
                await session.AudioClient.StopAsync();
        }
    }

    public async Task NotifyPhoneVoicemailAsync(
        ulong channelId,
        ulong targetUserId,
        PhoneVoicemail voicemail)
    {
        if (client is null || await client.GetChannelAsync(channelId) is not IMessageChannel channel)
        {
            logger.LogWarning("Could not find the configured phone voicemail notification channel");
            return;
        }

        var reason = voicemail.Reason == MissedCallReason.TargetUnavailable
            ? "you were unavailable"
            : "another call was in progress";
        await channel.SendMessageAsync(
            $"<@{targetUserId}> New phone voicemail ({voicemail.DurationSeconds}s, caller `{voicemail.CallerReference}`, {reason}). "
            + "Use `/phone-voicemails` to listen or delete it.");
    }


    private bool OnMcChatMessage(ChatMessage message)
    {
        if (message.ClientName == "cofl-discord")
            return true; // abort self sent messages
        TryRun(async () =>
        {
            var profilePicture = $"https://mc-heads.net/avatar/{message.Uuid}";
            // replace all §[a-f0-9] with empty string
            var messageContent = Regex.Replace(message.Message, "§[a-f0-9r]", "");
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
                logger.LogInformation("Forwarded Minecraft chat message to Discord");
            }
            var account = persistence.GetDiscordAccountInfoByMcUuid(Guid.Parse(message.Uuid));
            if (account == default)
            {
                using var scope = _serviceProvider.CreateScope();
                var updater = scope.ServiceProvider.GetRequiredService<UserInfoUpdater>();
                await updater.UpdateUserDetails(client!, message.Uuid, message.Name ?? "");
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
            logger.LogInformation("Discord client ready, authenticated as {username}#{discriminator} (id {botId})",
                client!.CurrentUser?.Username, client.CurrentUser?.Discriminator, client.CurrentUser?.Id);
            await RecoverPhoneStateAsync();
            await EnsureInteractionServiceInitialized();
            var guildId = ulong.Parse(_config["GUILD_ID"] ?? throw new Exception("Guild ID not set"));
            var guild = client!.GetGuild(guildId);
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
                    if (await client.GetChannelAsync(c.Id) is not ITextChannel channel)
                    {
                        continue;
                    }

                    var webhooks = await channel.GetWebhooksAsync();
                    if (webhooks.Count == 0)
                    {
                        var webhook = await channel.CreateWebhookAsync("Minecraft Chat");
                        ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
                    }
                    else
                    {
                        var webhook = webhooks.First();
                        ChatWebhooks.Add($"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}");
                    }
                }
            }

            await client.SetActivityAsync(new Game("being developed ...", ActivityType.Watching, ActivityProperties.Embedded, "at hyperspeed"));

            foreach (var item in client.Guilds)
            {
                try
                {
                    logger.LogInformation("Setting up guild integration for guild {guildName} ({guildId})", item.Name, item.Id);
                    await SetupGuildIntegration(item);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error setting up guild integration for guild {guildId}", item.Id);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error during Discord ready initialization - bot may not be fully ready");
        }
        logger.LogInformation("Discord bot ready");
    }

    private async Task RecoverPhoneStateAsync()
    {
        if (Interlocked.Exchange(ref phoneStateRecovered, 1) != 0)
            return;

        var options = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TwilioVoiceOptions>>().Value;
        if (!options.Enabled)
            return;

        try
        {
            await EndCallAsync(options.TargetUserId, options.PrivateVoiceChannelId, options.VoiceChannelId, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not recover Discord phone-call state after startup");
        }

        try
        {
            await _serviceProvider.GetRequiredService<TwilioCallGate>().ResetActiveCallAsync();
            logger.LogInformation("Reset phone-call state after startup");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not reset the active phone-call lease after startup");
        }
    }

    private async Task EnsureInteractionServiceInitialized()
    {
        if (interactionServiceInitialized)
        {
            return;
        }

        await interactionInitializationLock.WaitAsync();
        try
        {
            if (interactionServiceInitialized)
            {
                return;
            }

            if (interactionService == null)
            {
                throw new InvalidOperationException("Interaction service not initialized");
            }

            await interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), _serviceProvider);
            await interactionService.RegisterCommandsGloballyAsync(true);
            interactionServiceInitialized = true;
        }
        finally
        {
            interactionInitializationLock.Release();
        }
    }

    private async Task OnMessageReceived(SocketMessage msg)
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
    }

    private async Task OnInteractionCreated(SocketInteraction interaction)
    {
        await EnsureInteractionServiceInitialized();
        if (interactionService == null || client == null)
        {
            return;
        }

        var scope = _serviceProvider.CreateScope();
        var ctx = new SocketInteractionContext(client, interaction);
        await interactionService.ExecuteCommandAsync(ctx, scope.ServiceProvider);
    }

    private async Task OnJoinedGuild(SocketGuild guild)
    {
        try
        {
            await EnsureInteractionServiceInitialized();
            logger.LogInformation("Joined new guild: {guildName} ({guildId})", guild.Name, guild.Id);
            await SetupGuildIntegration(guild);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling guild join for guild {guildId}", guild.Id);
        }
    }

    private async Task SetupGuildIntegration(SocketGuild guild)
    {
        if (interactionService == null)
        {
            return;
        }

        await interactionService.RemoveModulesFromGuildAsync(guild.Id, interactionService.Modules.ToArray());

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

    private Task Log(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };
        logger.Log(level, message.Exception, "[Discord:{source}] {message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private Task OnDisconnected(Exception ex)
    {
        if (ex == null)
        {
            logger.LogWarning("Discord gateway disconnected (no exception). Waiting for reconnect...");
        }
        else
        {
            logger.LogError(ex, "Discord gateway disconnected with an error - this may prevent the bot from becoming ready");
        }
        return Task.CompletedTask;
    }

    private async Task OnMessage(SocketMessage msg)
    {
        // ignore messages from bots and webhooks
        if (msg.Author.IsBot || msg.Author.IsWebhook) return;
        var channelName = (msg.Channel as SocketGuildChannel)?.Name;

        if (MessageController.IsConfiguredCacheChannel(msg.Channel.Id))
        {
            var mentionedUsers = msg.MentionedUsers.ToDictionary(u => u.Id, u => u.Username);
            await persistence.SaveDiscordMessage(MessageController.MapMessage(msg, mentionedUsers));
        }

        // Detect a compromised account dumping images into multiple channels in fast succession.
        if (await HandleMultiChannelImageSpam(msg))
            return;

        if ((msg.Channel as SocketGuildChannel)?.Guild is SocketGuild guild
            && msg.Author is SocketGuildUser compromisedGuildUser
            && IsBroWithFourImages(msg))
        {
            var hasMessageInLast24Hours = await UserHasRecentMessageInGuild(guild, msg.Author.Id, DateTimeOffset.UtcNow.AddHours(-24), msg.Id);
            logger.LogWarning("Detected likely compromised account pattern from user {userId} ({userName}): 'bro' with 4 images. Has recent activity: {hasRecent}",
                msg.Author.Id, msg.Author.Username, hasMessageInLast24Hours);

            // Always delete the message regardless of recent history
            try
            {
                await msg.DeleteAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete suspicious compromised-account message {messageId}", msg.Id);
            }

            // Only kick and DM if the user had no prior activity (avoids false positives)
            if (!hasMessageInLast24Hours)
            {
                try
                {
                    await msg.Author.SendMessageAsync(
                        "Your account appears to have been compromised. We removed a suspicious message and kicked you for safety. " +
                        "You can join back any time using the invite at https://sky.coflnet.com");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not send compromised-account DM to user {userId}", msg.Author.Id);
                }

                try
                {
                    await compromisedGuildUser.KickAsync("Likely compromised account: 'bro' + 4 images + no message activity in 24h");
                    logger.LogInformation("Kicked user {userId} for likely compromised-account behavior", msg.Author.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to kick likely compromised user {userId}", msg.Author.Id);
                }
            }

            return;
        }
        
        // Check for suspicious hacked account messages: empty content + 4 image attachments
        if (string.IsNullOrWhiteSpace(msg.Content) && msg.Attachments.Count == 4)
        {
            // Check if all attachments are images
            bool allImages = msg.Attachments.All(att => 
                att.ContentType?.StartsWith("image/") == true);
            
            if (allImages)
            {
                logger.LogWarning("Detected suspicious message from user {userId} ({userName}) with 4 image attachments and no text content. Scheduling for deletion.", 
                    msg.Author.Id, msg.Author.Username);
                
                // Schedule deletion after 5 minutes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await msg.DeleteAsync();
                        logger.LogInformation("Deleted suspicious message {messageId} from user {userId}", msg.Id, msg.Author.Id);
                        
                        // Optionally timeout the user
                        if (msg.Author is SocketGuildUser guildUser)
                        {
                            await guildUser.SetTimeOutAsync(TimeSpan.FromHours(1));
                            await msg.Author.SendMessageAsync(
                                "Your account appears to have been compromised. A message with only images was detected and removed. " +
                                "Please secure your account immediately. You have been timed out for 1 hour.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to delete suspicious message {messageId}", msg.Id);
                    }
                });
                return;
            }
        }
        
        // #6: Nitro scam link detection (ported from Node.js bot)
        if (NitroRegex.IsMatch(msg.Content))
        {
            logger.LogInformation("Deleted nitro scam link message {messageId} from {userId} in {channelId}: {messageContent}",
                msg.Id, msg.Author.Id, msg.Channel.Id, msg.Content);
            await msg.DeleteAsync();
            return;
        }

        // #11: One-word message spam detection (ported from Node.js bot)
        if (CheckOneWordSpam(msg))
            return;

        if (msg.Content.Contains("steamcommunity.com"))
        {
            // delete steam links
            await msg.DeleteAsync();
            if (msg.Author is SocketGuildUser steamUser)
            {
                await steamUser.SetTimeOutAsync(TimeSpan.FromHours(1));
                var serverName = (msg.Channel as SocketGuildChannel)?.Guild.Name ?? "Coflnet";
                await msg.Author.SendMessageAsync($"You posted a steam link (probably got hacked). Please secure your account. We timed you out for 1 hour on {serverName}");
                return;
            }
            await msg.Author.SendMessageAsync("You posted a scam link (probably got hacked). Please secure your account. You can rejoin Coflnet discord in 10 minutes via the link on https://sky.coflnet.com");
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

        // #8: Auto-thread creation for support, bug report, and suggestions channels (ported from Node.js bot)
        if (CheckAutoThreadCreation(msg))
            return;

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
            return;
        }

        // #7: FAQ auto-reply (ported from Node.js bot answer.json)
        var faqAnswer = faqService.GetResponse(msg.Content);
        if (faqAnswer != null)
        {
            await msg.Channel.SendMessageAsync(faqAnswer, messageReference: msg.Reference);
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff" };

    private static bool IsImageAttachment(IAttachment att)
    {
        if (att.ContentType?.StartsWith("image/") == true)
            return true;
        var ext = Path.GetExtension(att.Filename);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    private static bool IsBroWithFourImages(SocketMessage msg)
    {
        return string.Equals(msg.Content.Trim(), "bro", StringComparison.OrdinalIgnoreCase)
            && msg.Attachments.Count == 4
            && msg.Attachments.All(IsImageAttachment);
    }

    /// <summary>
    /// Detects and mitigates the "compromised account" pattern where a user uploads image
    /// posts into multiple channels within a short window. Since the image is re-uploaded
    /// per channel the attachment URLs differ, so detection relies purely on the same user
    /// posting images in 2+ distinct channels in fast succession. Auto-kick is additionally
    /// gated on the user having no message history in the weeks prior, to avoid false
    /// positives on genuinely active members.
    /// </summary>
    /// <returns>true if the message was handled (and processing should stop).</returns>
    private async Task<bool> HandleMultiChannelImageSpam(SocketMessage msg)
    {
        if (msg.Channel is not SocketGuildChannel guildChannel
            || msg.Author is not SocketGuildUser guildUser)
            return false;

        var burst = TrackMultiChannelImagePosts(msg);
        if (burst == null)
            return false; // no image, or not yet spread across multiple channels

        var guild = guildChannel.Guild;
        var burstStart = burst.Min(p => p.Time);
        var burstIds = burst.Select(p => p.MessageId).ToHashSet();
        var channelCount = burst.Select(p => p.ContextChannelId).Distinct().Count();

        // Stop tracking immediately so a late straggler from the same burst doesn't
        // re-trigger the whole flow (double kick / DM).
        _recentImagePosts.TryRemove(msg.Author.Id, out _);

        // Check for any activity in the weeks before the burst started. Bounding the scan
        // by burstStart means the burst messages themselves don't count as "prior activity".
        bool hasPriorActivity;
        try
        {
            hasPriorActivity = await UserHasMessageInWindow(
                guild, msg.Author.Id,
                DateTimeOffset.UtcNow - PriorActivityWindow,
                burstStart,
                burstIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check prior activity for user {userId}; treating as none", msg.Author.Id);
            hasPriorActivity = false;
        }

        logger.LogWarning("Detected multi-channel image spam from user {userId} ({userName}): images in {channelCount} channels within {window}. Has prior activity: {hasPrior}",
            msg.Author.Id, msg.Author.Username, channelCount, MultiChannelImageWindow, hasPriorActivity);

        // Always remove the offending messages across every channel of the burst.
        foreach (var post in burst)
            await TryDeleteMessage(post.ChannelId, post.MessageId);

        // Only kick when there's no prior history (compromised/throwaway account).
        if (!hasPriorActivity)
        {
            try
            {
                await msg.Author.SendMessageAsync(
                    "Your account appears to have been compromised. We removed image spam posted across several channels and kicked you for safety. " +
                    "Please secure your account. You can rejoin any time using the invite at https://sky.coflnet.com");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send multi-channel image spam DM to user {userId}", msg.Author.Id);
            }

            try
            {
                await guildUser.KickAsync($"Likely compromised account: image spam across {channelCount} channels + no prior message activity");
                logger.LogInformation("Kicked user {userId} for multi-channel image spam", msg.Author.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to kick user {userId} for multi-channel image spam", msg.Author.Id);
            }
        }

        return true;
    }

    /// <summary>
    /// Records this message as an image post for its author and returns the current burst
    /// (all image posts within <see cref="MultiChannelImageWindow"/>) when it spans 2+
    /// distinct channels, otherwise null.
    /// </summary>
    private List<ImagePost>? TrackMultiChannelImagePosts(SocketMessage msg)
    {
        if (msg.Attachments.Count == 0 || !msg.Attachments.Any(IsImageAttachment))
            return null;

        var now = DateTime.UtcNow;
        // A thread is part of its parent channel for spam detection. In particular,
        // continuing an auto-created bug report thread is not cross-channel posting.
        var contextChannelId = msg.Channel is SocketThreadChannel thread
            ? thread.ParentChannel.Id
            : msg.Channel.Id;
        var post = new ImagePost(msg.Channel.Id, contextChannelId, msg.Id, now);
        var list = _recentImagePosts.GetOrAdd(msg.Author.Id, _ => new List<ImagePost>());

        List<ImagePost> snapshot;
        lock (list)
        {
            list.RemoveAll(p => now - p.Time > MultiChannelImageWindow);
            list.Add(post);
            snapshot = list.ToList();
        }

        var distinctChannels = snapshot.Select(p => p.ContextChannelId).Distinct().Count();
        return distinctChannels >= 2 ? snapshot : null;
    }

    private async Task TryDeleteMessage(ulong channelId, ulong messageId)
    {
        try
        {
            if (await client!.GetChannelAsync(channelId) is IMessageChannel channel)
                await channel.DeleteMessageAsync(messageId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete message {messageId} in channel {channelId}", messageId, channelId);
        }
    }

    // #11: One-word spam detection (ported from Node.js bot)
    private bool CheckOneWordSpam(SocketMessage msg)
    {
        // Only moderate one-word spam on this specific guild
        var guildUser = msg.Author as SocketGuildUser;
        if (guildUser == null || guildUser.Guild.Id != 267680588666896385)
            return false;

        var words = msg.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 1 || words[0].Length == 0)
            return false;

        // Allow URLs through
        if (IsValidHttpUrl(words[0]))
            return false;

        // Check if user has an exempt role
        foreach (var role in guildUser.Roles)
        {
            if (ExemptRoles.Contains(role.Id.ToString()))
                return false;
        }

        // Check if this user sent a one-word message within the last 10 seconds
        var now = DateTime.UtcNow;
        if (_oneWordMessageTimes.TryGetValue(msg.Author.Id, out var lastTime)
            && (now - lastTime).TotalMilliseconds < 10000)
        {
            // Delete and warn
            _ = Task.Run(async () =>
            {
                await msg.DeleteAsync();
                try
                {
                    await msg.Author.SendMessageAsync(
                        "Your message was deleted due to one word message spamming. Please do not send 1 word messages");
                }
                catch
                {
                    var sentMessage = await msg.Channel.SendMessageAsync(
                        $"<@{msg.Author.Id}>, your message was deleted due to one word message spamming. Please do not send 1 word messages");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        await sentMessage.DeleteAsync();
                    });
                }
            });
            return true;
        }

        _oneWordMessageTimes[msg.Author.Id] = now;
        return false;
    }

    private static bool IsValidHttpUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    // #8: Auto-thread creation for support, bug report, and suggestions channels (ported from Node.js bot)
    private bool CheckAutoThreadCreation(SocketMessage msg)
    {
        var channelId = msg.Channel.Id;

        if (channelId == _supportChannelId)
        {
            CreateAnswerThread(msg, "Support Help", "Needed a separate thread for moderation", thread =>
            {
                SendFaqAnswer(thread, msg.Content);
                thread.SendMessageAsync("Please provide as much information as possible so its easier to help you.\nA new support ticket was made <@&1057620211005661204>");
            });
            return true;
        }

        if (channelId == _bugReportChannelId)
        {
            CreateAnswerThread(msg, $" {msg.Author.Username} Bug Help", "help with bug", thread =>
            {
                thread.SendMessageAsync("Thank you for making a ticket\nPlease state the below\n- What you did\n- What you intended to do\n- what happened (even better if you take a screenshot/video of it)\n- What you expected");
                SendFaqAnswer(thread, msg.Content);

                _ = Task.Run(async () =>
                {
                    await Task.Delay(15000);
                    await thread.SendMessageAsync("If you use the mod please also use `/cofl report (optional message)` to easily create a report and copy the id you get into this thread");
                    await Task.Delay(25000);
                    await thread.SendMessageAsync("Try to be as precise and complete as possible. (Its faster to read some duplicate text than to ask you something)");
                });
            });
            return true;
        }

        return false;
    }

    private void CreateAnswerThread(SocketMessage msg, string name, string _reason, Action<IThreadChannel> callback)
    {
        if (msg.Channel is not ITextChannel textChannel)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var thread = await textChannel.CreateThreadAsync(
                    name,
                    autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                    message: msg);
                callback(thread);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create auto-thread for message {messageId}", msg.Id);
            }
        });
    }

    private void SendFaqAnswer(IThreadChannel thread, string text)
    {
        var answer = faqService.GetResponse(text);
        if (answer != null)
        {
            _ = thread.SendMessageAsync(answer);
        }
    }

    /// <summary>
    /// Scans recent channel history for any message from <paramref name="userId"/> with a
    /// timestamp in [after, before), ignoring the given message ids. Used to tell a freshly
    /// compromised account (no prior history) apart from an active member.
    /// </summary>
    private async Task<bool> UserHasMessageInWindow(SocketGuild guild, ulong userId, DateTimeOffset after, DateTimeOffset before, ISet<ulong> excludeMessageIds)
    {
        foreach (var textChannel in guild.TextChannels)
        {
            ulong beforeMessageId = 0;
            for (int page = 0; page < 20; page++)
            {
                List<IMessage> batch;
                try
                {
                    batch = beforeMessageId == 0
                        ? (await textChannel.GetMessagesAsync(limit: 100).FlattenAsync()).ToList()
                        : (await textChannel.GetMessagesAsync(beforeMessageId, Direction.Before, 100).FlattenAsync()).ToList();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping channel {channelId} while scanning history for user {userId}", textChannel.Id, userId);
                    break;
                }

                if (batch.Count == 0)
                    break;

                if (batch.Any(m => m.Author.Id == userId
                    && !excludeMessageIds.Contains(m.Id)
                    && m.Timestamp >= after && m.Timestamp < before))
                    return true;

                var oldestTimestamp = batch.Min(m => m.Timestamp);
                if (oldestTimestamp < after)
                    break;

                beforeMessageId = batch.Min(m => m.Id);
            }
        }

        return false;
    }

    private async Task<bool> UserHasRecentMessageInGuild(SocketGuild guild, ulong userId, DateTimeOffset cutoff, ulong excludeMessageId)
    {
        foreach (var textChannel in guild.TextChannels)
        {
            ulong beforeMessageId = 0;
            for (int page = 0; page < 20; page++)
            {
                List<IMessage> batch;
                try
                {
                    batch = beforeMessageId == 0
                    ? (await textChannel.GetMessagesAsync(limit: 100).FlattenAsync()).ToList()
                    : (await textChannel.GetMessagesAsync(beforeMessageId, Direction.Before, 100).FlattenAsync()).ToList();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping channel {channelId} while scanning recent history for user {userId}", textChannel.Id, userId);
                    break;
                }

                if (batch.Count == 0)
                    break;

                if (batch.Any(m => m.Author.Id == userId && m.Id != excludeMessageId && m.Timestamp >= cutoff))
                    return true;

                var oldestTimestamp = batch.Min(m => m.Timestamp);
                if (oldestTimestamp < cutoff)
                    break;

                beforeMessageId = batch.Min(m => m.Id);
            }
        }

        return false;
    }

    private async Task HandleInGameChat(SocketMessage msg)
    {
        var profile = await persistence.GetDiscordAccountInfo(msg.Author.Id);
        if (profile == default)
        {
            var handle = await msg.ReplyAsync("", embed: new EmbedBuilder()
                .WithTitle("You need to select your Minecraft account")
                .WithDescription("To do so run **/update-mc-user** ")
                .WithColor(Color.Red)
                .Build());
            if (!msg.Content.Contains('<')) // only keep messages with pings
                await msg.DeleteAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2));
                await handle.DeleteAsync();
            });
            return;
        }
        if (msg.Content.StartsWith("/update-mc-user"))
        {
            await msg.ReplyAsync("Please type the command manually and wait for discord to recognize it");
            return;
        }
        var message = msg.Content;
        message = await ReplacePingsIgn(message);
        if (profile.ExpiresAt < DateTime.UtcNow)
        {
            await userInfoUpdater.UpdatePremiumTierAndSave(profile);
        }

        int attempts = 0;
        while (attempts < 3)
        {
            try
            {
                await chatService.Send(new()
                {
                    SenderUuid = profile.MinecraftUuid.ToString("n"),
                    Message = message,
                    SenderName = profile?.MinecraftName ?? msg.Author.Username,
                    AccountTier = profile?.AccountTier ?? AccountTier.NONE
                });
                break; // success
            }
            catch (ApiException e)
            {
                attempts++;
                if (attempts >= 3)
                {
                    var handle = await msg.ReplyAsync(e.Message);
                    await msg.DeleteAsync();
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(30000);
                        await handle.DeleteAsync();
                    });
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                attempts++;
                if (attempts >= 3)
                {
                    logger.LogError(e, "Error sending message to chat");
                    await msg.ReplyAsync("Could not send message, <@267680402594988033> ");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
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
