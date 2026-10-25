using Discord;
using Discord.WebSocket;

// TEMPORARY BRIDGE: DiscordBot lost the privileged Message Content intent. Until Discord restores it,
// a second read-only gateway connection (logged in with the "discord-me" bot token) supplies message
// content. It never performs actions itself - every side effect is executed by the main client.
// To remove: delete this file and IncomingMessage's fallback handling, drop FALLBACK_BOT_TOKEN,
// and re-add GatewayIntents.MessageContent to the main client.

public enum MessageSource
{
    Main,
    Fallback
}

public static class FallbackMessageSource
{
    public const string TokenConfigKey = "FALLBACK_BOT_TOKEN";

    /// <summary>Only what is needed to read messages; no members, no reactions, no voice.</summary>
    public const GatewayIntents Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent;

    public static bool IsEnabled(string? token) => !string.IsNullOrWhiteSpace(token);

    /// <summary>
    /// Decides whether a message received from <paramref name="source"/> must run through the pipeline.
    /// Guilds the fallback client is in are served by the fallback client only (it has the content);
    /// other guilds keep being served by the main client. The fallback never serves DMs or guilds
    /// the main bot is not in (it could not act there).
    /// </summary>
    /// <param name="guildId">null for DMs</param>
    public static bool ShouldProcess(MessageSource source, ulong? guildId, bool fallbackInGuild, bool mainInGuild)
    {
        if (source == MessageSource.Fallback)
            return guildId != null && fallbackInGuild && mainInGuild;
        return guildId == null || !fallbackInGuild;
    }

    /// <summary>
    /// Whether the message text was actually delivered. Without the Message Content intent the main
    /// client only gets text for DMs and messages that mention the bot; everything else arrives empty,
    /// so "no text" must not be treated as a signal (e.g. by the image-only spam rule).
    /// </summary>
    public static bool IsContentAvailable(MessageSource origin, bool isDm, bool mentionsMainBot)
        => origin == MessageSource.Fallback || isDm || mentionsMainBot;

    /// <summary>Evidence reads need message content, so use the fallback client whenever it can serve the guild.</summary>
    public static MessageSource ChooseEvidenceSource(bool fallbackConnected, bool fallbackInGuild)
        => fallbackConnected && fallbackInGuild ? MessageSource.Fallback : MessageSource.Main;

    /// <summary>
    /// True when every message read through the main client really carries its text/attachments
    /// (authored by the main bot or mentioning it); otherwise blank messages must not be passed off as complete.
    /// </summary>
    public static bool AllContentAvailable(IEnumerable<(bool AuthoredByMainBot, bool MentionsMainBot)> messages)
        => messages.All(message => message.AuthoredByMainBot || message.MentionsMainBot);

    /// <summary>
    /// Whether a message read for the message cache carries its real text/attachments. Reads through the
    /// fallback client always do; reads through main only for its own messages and ones mentioning it.
    /// </summary>
    public static bool IsCacheContentTrusted(MessageSource source, bool authoredByMainBot, bool mentionsMainBot)
        => source == MessageSource.Fallback || authoredByMainBot || mentionsMainBot;

    public static DiscordSocketClient CreateClient() => new(new DiscordSocketConfig
    {
        LogLevel = LogSeverity.Info,
        GatewayIntents = Intents,
        AlwaysDownloadUsers = false
    });
}
