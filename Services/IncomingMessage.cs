using Discord;
using Discord.WebSocket;

/// <summary>
/// A message to run through the moderation pipeline: a thin wrapper around the gateway message
/// together with its author, channel and guild.
/// </summary>
public sealed class IncomingMessage
{
    public IncomingMessage(SocketMessage source, IUser author, IMessageChannel channel, SocketGuild? guild)
    {
        Source = source;
        Author = author;
        Channel = channel;
        Guild = guild;
    }

    public SocketMessage Source { get; }
    /// <summary>The author (an <see cref="IGuildUser"/> for guild messages).</summary>
    public IUser Author { get; }
    public IMessageChannel Channel { get; }
    /// <summary>The guild, null for DMs.</summary>
    public SocketGuild? Guild { get; }

    public ulong Id => Source.Id;
    public string Content => Source.Content;
    public IReadOnlyCollection<IAttachment> Attachments => Source.Attachments;
    public MessageReference Reference => Source.Reference;
    public IReadOnlyCollection<SocketUser> MentionedUsers => Source.MentionedUsers;
    public string? ChannelName => (Channel as IGuildChannel)?.Name;
    /// <summary>Parent channel id for threads, the channel's own id otherwise.</summary>
    public ulong ContextChannelId => Source.Channel is SocketThreadChannel thread ? thread.ParentChannel.Id : Channel.Id;

    public Task DeleteAsync(RequestOptions? options = null) => Channel.DeleteMessageAsync(Id, options);

    public Task<IUserMessage> ReplyAsync(string content, Embed? embed = null)
        => Channel.SendMessageAsync(content, false, embed, null, null, new MessageReference(Id));

    /// <summary>A message object usable for API calls that need an IMessage.</summary>
    public Task<IMessage> GetMessageAsync() => Task.FromResult<IMessage>(Source);
}
