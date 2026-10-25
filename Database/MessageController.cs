using System.Numerics;
using Coflnet.Core;
using Discord;
using Discord.Rest;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Allows querying messages from a channel
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MessageController : ControllerBase
{
    internal const ulong DeveloperUpdateChannelId = 888932870318612490;
    internal const ulong TestChannelId = 870408637204553758;

    private static readonly IReadOnlyDictionary<string, ulong> NameToChannelIdMap =
        new Dictionary<string, ulong>
        {
            { "devlog", DeveloperUpdateChannelId },
            { "test", TestChannelId }
        };
    private DiscordHandler discordHandler;
    private ILogger<MessageController> logger;
    private Persistence persistence;

    public MessageController(DiscordHandler discordHandler, ILogger<MessageController> logger, Persistence persistence)
    {
        this.discordHandler = discordHandler;
        this.logger = logger;
        this.persistence = persistence;
    }

    internal static bool IsConfiguredCacheChannel(ulong channelId)
        => NameToChannelIdMap.Values.Contains(channelId);

    [HttpGet("{channelName}/fetch")]
    public async Task<IActionResult> FetchMessages(string channelName)
    {
        if (string.IsNullOrEmpty(channelName))
        {
            return BadRequest(new { error = "invalid_channel_name", message = "Channel name cannot be null or empty." });
        }
        if (!NameToChannelIdMap.TryGetValue(channelName, out var channelId))
        {
            return NotFound(new { error = "channel_not_found", message = $"Channel '{channelName}' not found." });
        }
        logger.LogInformation($"Fetching messages for channel '{channelName}' (ID: {channelId}).");
        // TEMPORARY BRIDGE: reads go through the fallback client (see FallbackMessageSource); remove together with it
        ulong oldest = 0;
        for (int i = 0; i < 500; i++)
        {
            var page = await discordHandler.GetCacheMessages(channelId, oldest);
            if (page.RawCount == 0)
            {
                logger.LogInformation($"No more messages found in channel '{channelName}' (ID: {channelId}).");
                return Ok(new { message = $"Messages fetched and saved for channel '{channelName}'. about {i * 100}" });
            }
            var messages = page.Usable.OfType<RestUserMessage>().Select(MapMessage).ToList();
            foreach (var message in messages)
            {
                await persistence.SaveDiscordMessage(message);
            }
            oldest = page.OldestId;
            logger.LogInformation($"Saved {messages.Count} messages to database for channel '{channelName}' (ID: {channelId}).");
            await Task.Delay(2000); // Wait for 2 seconds before fetching more messages
        }
        return Ok(new { message = $"Messages fetched and saved for channel '{channelName}'. but too many messages found aborted" });
    }

    [HttpGet("{channelName}")]
    [ResponseCache(Duration = 600, Location = ResponseCacheLocation.Any, VaryByQueryKeys = new[] { "before" })]
    public async Task<IEnumerable<DiscordMessage>> GetMessages(string channelName, DateTime before = default)
    {
        if (before == default)
            before = DateTime.UtcNow;
        if (string.IsNullOrEmpty(channelName))
        {
            throw new ApiException("invalid_channel_name", "Channel name cannot be null or empty.");
        }

        if (!NameToChannelIdMap.TryGetValue(channelName, out var channelId))
        {
            throw new ApiException("channel_not_found", $"Channel '{channelName}' not found.");
        }

        var stored = (await persistence.GetDiscordMessages(channelId, before)).ToList();
        var window = MonthWindow.Of(before);
        if (stored.Count > 0)
        {
            // Refresh messages older than 24 hours by fetching fresh versions from Discord.
            var now = DateTime.UtcNow;
            var toUpdate = stored.Where(m => now - m.UpdateAt > TimeSpan.FromHours(24) && m.Attachments != null && m.Attachments.Count > 0).ToList();

            if (toUpdate.Count > 0)
            {
                logger.LogInformation($"Refreshing {toUpdate.Count} of {stored.Count} stored messages for channel '{channelName}' (ID: {channelId}) from Discord to revalidate attachments...");
                var fetched = await FetchMonth(channelId, window);
                var merged = MergeForCache(stored, fetched);
                await persistence.SaveDiscordMessages(merged.Where(m => fetched.Any(f => f.MessageId == m.MessageId)));
                return merged.Where(window.Contains).ToList();
            }

            return stored;
        }

        // nothing stored for this month: load only this month, never the whole channel history
        var month = await FetchMonth(channelId, window);
        await persistence.SaveDiscordMessages(month);
        return month;
    }

    /// <summary>Pages backwards from the end of the month until messages are older than its start.</summary>
    private async Task<List<DiscordMessage>> FetchMonth(ulong channelId, MonthWindow window)
    {
        var result = new List<DiscordMessage>();
        var cursor = window.EndSnowflake;
        while (true)
        {
            var page = await discordHandler.GetCacheMessages(channelId, cursor);
            if (page.RawCount == 0)
                break;
            logger.LogInformation($"Retrieved {page.RawCount} messages from channel (ID: {channelId}).");
            result.AddRange(page.Usable.OfType<RestUserMessage>().Select(MapMessage).Where(window.Contains));
            if (page.RawCount < 100 || page.OldestId < window.StartSnowflake)
                break;
            cursor = page.OldestId;
            await Task.Delay(2000); // Wait for 2 seconds before fetching more messages
        }
        return result;
    }

    /// <summary>
    /// A UTC calendar month; the snowflakes bound the Discord history that has to be read for it.
    /// </summary>
    internal readonly record struct MonthWindow(DateTimeOffset Start, DateTimeOffset End)
    {
        public static MonthWindow Of(DateTime time)
        {
            var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
            var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return new MonthWindow(start, start.AddMonths(1));
        }
        public ulong StartSnowflake => SnowflakeUtils.ToSnowflake(Start);
        public ulong EndSnowflake => SnowflakeUtils.ToSnowflake(End);
        public bool Contains(DiscordMessage m) => m.CreatedAt >= Start && m.CreatedAt < End;
    }

    /// <summary>
    /// Combines stored and freshly fetched messages (newest first). A fetched message never replaces
    /// stored content or attachments with empty ones; stored messages Discord no longer returned are kept.
    /// </summary>
    internal static List<DiscordMessage> MergeForCache(IEnumerable<DiscordMessage> stored, IEnumerable<DiscordMessage> fetched)
    {
        var byId = stored.GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.First());
        foreach (var message in fetched)
        {
            if (byId.TryGetValue(message.MessageId, out var old))
            {
                if (string.IsNullOrWhiteSpace(message.Content) && !string.IsNullOrWhiteSpace(old.Content))
                    message.Content = old.Content;
                if ((message.Attachments == null || message.Attachments.Count == 0) && old.Attachments?.Count > 0)
                    message.Attachments = old.Attachments;
            }
            byId[message.MessageId] = message;
        }
        return byId.Values.OrderByDescending(m => m.MessageId).ToList();
    }

    private static DiscordMessage MapMessage(RestUserMessage m)
        => MapMessage(m, m.MentionedUsers.ToDictionary(u => u.Id, u => u.Username));

    public static DiscordMessage MapMessage(IMessage m, IReadOnlyDictionary<ulong, string> userNames)
    {
        return new DiscordMessage
        {
            MessageId = m!.Id,
            Content = ReplaceDiscordUserMentions(m.Content, userNames),
            Attachments = m.Attachments.ToDictionary(a => (long)a.Id, a => a.Url),
            CreatedAt = m.CreatedAt,
            AuthorName = m.Author.Username,
            ChannelId = m.Channel.Id,
            UpdateAt = DateTime.UtcNow // Set the update time to now
        };
    }

    internal static string ReplaceDiscordUserMentions(string content, IReadOnlyDictionary<ulong, string> userNames)
    {
        foreach (var user in userNames)
        {
            content = content.Replace($"<@{user.Key}>", $"@{user.Value}");
            content = content.Replace($"<@!{user.Key}>", $"@{user.Value}");
        }
        return content;
    }
}
