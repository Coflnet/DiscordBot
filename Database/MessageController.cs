using System.Numerics;
using Coflnet.Core;
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

    [HttpGet("{channelName}")]
    public async Task<IEnumerable<DiscordMessage>> Get(string channelName, DateTime before = default)
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
            logger.LogInformation($"Retrieved {stored.Count} messages from database for channel '{channelName}' (ID: {channelId}).");
            return stored;
        }


        // Assuming GetMessagesAsync is a method that retrieves messages for the given channel ID
        var messages = (await discordHandler.GetMessagesFromChannel(channelId)).Select(r => r as RestUserMessage).Where(m => m != null).Select(m => MapMessages(m, channelId)).ToList();
        while (messages.Count >= 100)
        {
            logger.LogInformation($"Retrieved {messages.Count} messages from channel '{channelName}' (ID: {channelId}).");
            foreach (var message in messages)
            {
                await persistence.SaveDiscordMessage(message);
            }
            var lastMessageId = messages.Last().MessageId;
            await Task.Delay(2000); // Wait for 2 seconds before fetching more messages
            messages = (await discordHandler.GetMessagesFromChannel(channelId, lastMessageId)).Select(r => r as RestUserMessage).Where(m => m != null).Select(m => MapMessages(m, channelId)).ToList();
        }
        foreach (var message in messages)
        {
            await persistence.SaveDiscordMessage(message);
        }
        return messages;
    }

    private static DiscordMessage MapMessages(RestUserMessage m, ulong channelId)
    {
        return new DiscordMessage
        {
            MessageId = m!.Id,
            Content = m.Content,
            Attachments = m.Attachments.ToDictionary(a => (long)a.Id, a => a.Url),
            CreatedAt = m.CreatedAt,
            AuthorId = m.Author.Id,
            AuthorName = m.Author.Username,
            ChannelId = channelId,
            UpdateAt = DateTime.UtcNow // Set the update time to now
        };
    }
}
