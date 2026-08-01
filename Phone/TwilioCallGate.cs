using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Coflnet.DiscordBot.Phone;

public enum CallAdmission
{
    Allowed,
    Anonymous,
    RateLimited
}

public sealed class TwilioCallGate(
    IConnectionMultiplexer redis,
    IOptions<TwilioVoiceOptions> options)
{
    private const string ActiveCallKey = "discordbot:phone:active";
    private static readonly LuaScript RateLimitScript = LuaScript.Prepare(
        """
        redis.call('ZREMRANGEBYSCORE', @key, '-inf', @cutoff)
        if redis.call('ZCARD', @key) >= tonumber(@limit) then
            return 0
        end
        redis.call('ZADD', @key, @now, @callSid)
        redis.call('PEXPIRE', @key, @window)
        return 1
        """);
    private static readonly LuaScript ActivateScript = LuaScript.Prepare(
        """
        if redis.call('GET', @key) ~= @callSid then return 0 end
        redis.call('PEXPIRE', @key, @expiry)
        return 1
        """);
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(
        """
        if redis.call('GET', @key) ~= @callSid then return 0 end
        return redis.call('DEL', @key)
        """);

    private readonly IDatabase database = redis.GetDatabase();
    private readonly TwilioVoiceOptions options = options.Value;

    public async Task<CallAdmission> AdmitCallerAsync(string? caller, string callSid)
    {
        if (string.IsNullOrWhiteSpace(caller)
            || caller[0] != '+'
            || caller.Length is < 8 or > 20
            || !caller.AsSpan(1).ToString().All(char.IsAsciiDigit))
            return CallAdmission.Anonymous;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window = (long)TimeSpan.FromHours(24).TotalMilliseconds;
        var allowed = (long)await database.ScriptEvaluateAsync(RateLimitScript, new
        {
            key = (RedisKey)$"discordbot:phone:caller:{HashCaller(caller)}",
            cutoff = now - window,
            limit = options.MaxCallsPer24Hours,
            now,
            callSid,
            window
        });
        return allowed == 1 ? CallAdmission.Allowed : CallAdmission.RateLimited;
    }

    public Task<bool> TryReserveAsync(string callSid)
    {
        return database.StringSetAsync(
            ActiveCallKey,
            callSid,
            TimeSpan.FromSeconds(90),
            When.NotExists);
    }

    public async Task<bool> ActivateAsync(string callSid)
    {
        var result = (long)await database.ScriptEvaluateAsync(ActivateScript, new
        {
            key = (RedisKey)ActiveCallKey,
            callSid,
            expiry = (long)TimeSpan.FromMinutes(options.MaxCallMinutes + 1).TotalMilliseconds
        });
        return result == 1;
    }

    public Task ReleaseAsync(string callSid)
    {
        return database.ScriptEvaluateAsync(ReleaseScript, new
        {
            key = (RedisKey)ActiveCallKey,
            callSid
        });
    }

    public string CreateStreamToken(string callSid)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(options.MaxCallMinutes + 2).ToUnixTimeSeconds();
        var value = $"{expires.ToString(CultureInfo.InvariantCulture)}.{Sign(callSid, expires)}";
        return value;
    }

    public bool IsValidStreamToken(string callSid, string? token)
    {
        var parts = token?.Split('.', 2);
        if (parts is not { Length: 2 }
            || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var expires)
            || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        var expected = Encoding.ASCII.GetBytes(Sign(callSid, expires));
        var actual = Encoding.ASCII.GetBytes(parts[1]);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string HashCaller(string caller)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.CallerHashKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"caller:{caller}")));
    }

    private string Sign(string callSid, long expires)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.StreamSigningKey));
        var signature = hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"stream:{callSid}:{expires.ToString(CultureInfo.InvariantCulture)}"));
        return Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
