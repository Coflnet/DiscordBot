using System.Numerics;

public class DiscordMessage
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int Month { get; set; }
    public Dictionary<long, string> Attachments { get; set; } = new Dictionary<long, string>();
    public string AuthorName { get; set; } = string.Empty;
    public DateTimeOffset UpdateAt { get; set; } = DateTime.UtcNow;
}
