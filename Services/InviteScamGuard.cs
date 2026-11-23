using System.Collections.Concurrent;

namespace Coflnet.Discord;

/// <summary>
/// Short-lived in-memory guard so a burst of scam messages from one user triggers a single
/// REST lookup / ban attempt instead of one per message.
/// </summary>
public sealed class InviteScamGuard
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> entries = new();

    /// <summary>True when the caller should handle this user now, false when a recent attempt is still guarded.</summary>
    public bool TryEnter(ulong guildId, ulong userId, DateTimeOffset now)
    {
        PruneExpired(now);
        var key = (guildId, userId);
        var added = entries.TryAdd(key, now);
        if (added)
            return true;
        // lost the race or an entry exists: only an expired one can be replaced
        if (!entries.TryGetValue(key, out var since) || now - since < Ttl)
            return false;
        return entries.TryUpdate(key, now, since);
    }

    public void Clear(ulong guildId, ulong userId) => entries.TryRemove((guildId, userId), out _);

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in entries)
        {
            if (now - entry.Value >= Ttl)
                entries.TryRemove(entry);
        }
    }
}
