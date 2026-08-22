using System.Security.Cryptography;
using System.Text.RegularExpressions;

public sealed class IssueDraftService
{
    internal const int MaxPending = 128;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly Regex Repository = new(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"^[A-Za-z0-9_-]{24}$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, IssueDraft> pending = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now;
    private readonly Func<byte[]> random;

    public IssueDraftService() : this(() => DateTimeOffset.UtcNow, () => RandomNumberGenerator.GetBytes(18)) { }

    internal IssueDraftService(Func<DateTimeOffset> now, Func<byte[]> random)
    {
        this.now = now;
        this.random = random;
    }

    public string Create(string title, string repository, string body, ulong userId, ulong guildId, ulong channelId)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 256 || !Repository.IsMatch(repository)
            || body.Length > 64 << 10 || userId == 0 || channelId == 0)
            throw new IssueDraftDenied("invalid_draft");
        lock (gate)
        {
            RemoveExpired();
            if (pending.Count >= MaxPending)
                throw new IssueDraftDenied("draft_capacity_reached");
            var token = Base64Url(random());
            if (!Token.IsMatch(token) || pending.ContainsKey(token))
                throw new IssueDraftDenied("invalid_draft_token");
            pending[token] = new(title, repository, body, userId, guildId, channelId, now().Add(Lifetime));
            return token;
        }
    }

    public IssueDraft Peek(string token, ulong userId, ulong guildId, ulong channelId)
        => Access(token, userId, guildId, channelId, false);

    public IssueDraft Take(string token, ulong userId, ulong guildId, ulong channelId)
        => Access(token, userId, guildId, channelId, true);

    private IssueDraft Access(string token, ulong userId, ulong guildId, ulong channelId, bool consume)
    {
        lock (gate)
        {
            RemoveExpired();
            if (!Token.IsMatch(token) || !pending.TryGetValue(token, out var draft)
                || draft.UserId != userId || draft.GuildId != guildId || draft.ChannelId != channelId)
                throw new IssueDraftDenied("draft_missing_expired_or_mismatched");
            if (consume) pending.Remove(token);
            return draft;
        }
    }

    private void RemoveExpired()
    {
        foreach (var token in pending.Where(value => value.Value.ExpiresAt <= now()).Select(value => value.Key).ToList())
            pending.Remove(token);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record IssueDraft(string Title, string Repository, string Body, ulong UserId, ulong GuildId, ulong ChannelId, DateTimeOffset ExpiresAt);

public sealed class IssueDraftDenied(string reason) : Exception(reason);
