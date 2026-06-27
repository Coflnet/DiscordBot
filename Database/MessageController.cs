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
    private Dictionary<string, ulong> NameToChannelIdMap;
    private DiscordHandler discordHandler;
    private ILogger<MessageController> logger;
    private Persistence persistence;

    public MessageController(DiscordHandler discordHandler, ILogger<MessageController> logger, Persistence persistence)
    {
        this.discordHandler = discordHandler;
        // Initialize the mapping of channel names to IDs
        NameToChannelIdMap = new Dictionary<string, ulong>
        {
            { "devlog", 888932870318612490 },
            { "test", 870408637204553758 }
        };
        this.logger = logger;
        this.persistence = persistence;
    }

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
        ulong oldest = 0;
        for (int i = 0; i < 500; i++)
        {
            var messages = (await discordHandler.GetMessagesFromChannel(channelId, oldest)).OfType<RestUserMessage>().Select(m => MapMessages(m)).ToList();
            if (messages.Count == 0)
            {
                logger.LogInformation($"No more messages found in channel '{channelName}' (ID: {channelId}).");
                return Ok(new { message = $"Messages fetched and saved for channel '{channelName}'. about {i * 100}" });
            }
            foreach (var message in messages)
            {
                await persistence.SaveDiscordMessage(message);
            }
            oldest = messages.Min(m => m.MessageId);
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
        if (stored.Count > 0)
        {
            // Refresh messages older than 24 hours by fetching fresh versions from Discord.
            var now = DateTime.UtcNow;
            var refreshed = new List<DiscordMessage>();

            var toUpdate = stored.Where(m => now - m.UpdateAt > TimeSpan.FromHours(24) && m.Attachments != null && m.Attachments.Count > 0).ToList();

            if (toUpdate.Count > 0)
            {
                logger.LogInformation($"Refreshing {toUpdate.Count} of {stored.Count} stored messages for channel '{channelName}' (ID: {channelId}) from Discord to revalidate attachments...");
                var getBefore = stored.Max(m => m.MessageId);
                var loadesMessages = await discordHandler.GetMessagesFromChannel(channelId, getBefore, stored.Count);
                var mapped = loadesMessages.OfType<RestUserMessage>().Select(m => MapMessages(m));
                await persistence.SaveDiscordMessages(mapped);
                return mapped.ToList();
            }

            return stored;
        }


        // Assuming GetMessagesAsync is a method that retrieves messages for the given channel ID
        var messages = (await discordHandler.GetMessagesFromChannel(channelId)).OfType<RestUserMessage>().Select(m => MapMessages(m)).ToList();
        while (messages.Count >= 100)
        {
            logger.LogInformation($"Retrieved {messages.Count} messages from channel '{channelName}' (ID: {channelId}).");
            // batch save to avoid per-message round trips
            await persistence.SaveDiscordMessages(messages);
            var lastMessageId = messages.Last().MessageId;
            await Task.Delay(2000); // Wait for 2 seconds before fetching more messages
            messages = (await discordHandler.GetMessagesFromChannel(channelId, lastMessageId)).OfType<RestUserMessage>().Select(m => MapMessages(m)).ToList();
        }
        await persistence.SaveDiscordMessages(messages);
        return messages;
    }

    private static DiscordMessage MapMessages(RestUserMessage m)
    {
        var userNames = m.MentionedUsers.ToDictionary(u => u.Id, u => u.Username);
        return MapMessage(m, userNames);
    }

    public static DiscordMessage MapMessage(IMessage m, Dictionary<ulong, string> userNames)
    {
        var contentWithNamesReplaced = m.Content;
        foreach (var user in userNames)
        {
            contentWithNamesReplaced = contentWithNamesReplaced.Replace($"<@{user.Key}>", $"@{user.Value}");
            contentWithNamesReplaced = contentWithNamesReplaced.Replace($"<@!{user.Key}>", $"@{user.Value}");
        }
        return new DiscordMessage
        {
            MessageId = m!.Id,
            Content = contentWithNamesReplaced,
            Attachments = m.Attachments.ToDictionary(a => (long)a.Id, a => a.Url),
            CreatedAt = m.CreatedAt,
            AuthorId = m.Author.Id,
            AuthorName = m.Author.Username,
            ChannelId = m.Channel.Id,
            UpdateAt = DateTime.UtcNow // Set the update time to now
        };
    }
}
