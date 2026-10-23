using Discord;
using Discord.WebSocket;

/// <summary>
/// A message to run through the moderation pipeline. Content, attachments, ids and mentions come from
/// <see cref="Source"/> (which may belong to the temporary fallback client); the author, channel and
/// guild are resolved through the MAIN client so all actions (delete, timeout, kick, reply, threads)
/// are performed by the main bot identity.
/// </summary>
public sealed class IncomingMessage
{
    public IncomingMessage(SocketMessage source, MessageSource origin, IUser author, IMessageChannel channel, SocketGuild? guild, bool contentAvailable)
    {
        ContentAvailable = contentAvailable;
        Source = source;
        Origin = origin;
        Author = author;
        Channel = channel;
        Guild = guild;
    }

    public SocketMessage Source { get; }
    public MessageSource Origin { get; }
    /// <summary>Main-client user (an <see cref="IGuildUser"/> for guild messages).</summary>
    public IUser Author { get; }
    /// <summary>Main-client channel.</summary>
    public IMessageChannel Channel { get; }
    /// <summary>Main-client guild, null for DMs.</summary>
    public SocketGuild? Guild { get; }

    /// <summary>False when the text was withheld by Discord (missing Message Content intent), so an empty Content means "unknown".</summary>
    public bool ContentAvailable { get; }
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

    /// <summary>A message object usable for API calls that need an IMessage (only the id matters).</summary>
    public async Task<IMessage> GetMessageAsync()
        => Origin == MessageSource.Main ? Source : await Channel.GetMessageAsync(Id);
}
