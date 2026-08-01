using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Coflnet.DiscordBot.Phone;

public sealed record PhoneVoicemail(
    string RecordingSid,
    string AccountSid,
    DateTimeOffset Timestamp,
    string CallerReference,
    MissedCallReason Reason,
    int DurationSeconds);

public sealed class TwilioVoicemailService(
    IConnectionMultiplexer redis,
    IHttpClientFactory httpClientFactory,
    DiscordHandler discord,
    IOptions<TwilioVoiceOptions> options,
    ILogger<TwilioVoicemailService> logger) : BackgroundService
{
    private const string DataKey = "discordbot:phone:voicemails:data";
    private const string IndexKey = "discordbot:phone:voicemails:index";
    private const int MaximumDownloadBytes = 10 * 1024 * 1024;
    private static readonly LuaScript StoreScript = LuaScript.Prepare(
        """
        if redis.call('HEXISTS', @dataKey, @recordingSid) == 1 then return 0 end
        redis.call('HSET', @dataKey, @recordingSid, @data)
        redis.call('ZADD', @indexKey, @timestamp, @recordingSid)
        return 1
        """);
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TwilioVoiceOptions options = options.Value;

    public async Task<bool> StoreAsync(
        string accountSid,
        string recordingSid,
        string callerReference,
        MissedCallReason reason,
        int durationSeconds)
    {
        if (!IsSid(accountSid, "AC")
            || !IsSid(recordingSid, "RE")
            || callerReference.Length != 8
            || !callerReference.All(char.IsAsciiHexDigit))
            return false;

        var voicemail = new PhoneVoicemail(
            recordingSid,
            accountSid,
            DateTimeOffset.UtcNow,
            callerReference,
            reason,
            Math.Max(0, durationSeconds));
        var stored = (long)await database.ScriptEvaluateAsync(StoreScript, new
        {
            dataKey = (RedisKey)DataKey,
            indexKey = (RedisKey)IndexKey,
            recordingSid,
            data = JsonSerializer.Serialize(voicemail),
            timestamp = voicemail.Timestamp.ToUnixTimeSeconds()
        }) == 1;
        if (!stored)
            return false;

        try
        {
            await discord.NotifyPhoneVoicemailAsync(
                options.VoicemailNotificationChannelId,
                options.TargetUserId,
                voicemail);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not send the new voicemail notification to Discord");
        }
        return true;
    }

    public async Task<IReadOnlyList<PhoneVoicemail>> GetAsync(int limit = 25)
    {
        var recordingSids = await database.SortedSetRangeByRankAsync(
            IndexKey,
            0,
            Math.Clamp(limit, 1, 25) - 1,
            Order.Descending);
        if (recordingSids.Length == 0)
            return [];

        var values = await database.HashGetAsync(DataKey, recordingSids);
        return values
            .Where(value => value.HasValue)
            .Select(value => JsonSerializer.Deserialize<PhoneVoicemail>(value.ToString()))
            .Where(voicemail => voicemail is not null)
            .Select(voicemail => voicemail!)
            .ToArray();
    }

    public async Task<PhoneVoicemail?> GetAsync(string recordingSid)
    {
        if (!IsSid(recordingSid, "RE"))
            return null;
        var value = await database.HashGetAsync(DataKey, recordingSid);
        return value.HasValue
            ? JsonSerializer.Deserialize<PhoneVoicemail>(value.ToString())
            : null;
    }

    public async Task<byte[]?> DownloadAsync(string recordingSid, CancellationToken cancellationToken)
    {
        var voicemail = await GetAsync(recordingSid);
        if (voicemail is null)
            return null;

        using var request = CreateRequest(HttpMethod.Get, voicemail, ".wav");
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("Twilio voicemail exceeds 10 MiB");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumDownloadBytes)
                throw new InvalidDataException("Twilio voicemail exceeds 10 MiB");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    public async Task<bool> DeleteAsync(string recordingSid, CancellationToken cancellationToken)
    {
        var voicemail = await GetAsync(recordingSid);
        if (voicemail is null)
            return false;

        using var request = CreateRequest(HttpMethod.Delete, voicemail, ".json");
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();

        await Task.WhenAll(
            database.HashDeleteAsync(DataKey, recordingSid),
            database.SortedSetRemoveAsync(IndexKey, recordingSid));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete expired Twilio voicemails");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.VoicemailRetentionDays).ToUnixTimeSeconds();
        var recordingSids = await database.SortedSetRangeByScoreAsync(
            IndexKey,
            stop: cutoff,
            take: 100);
        foreach (var recordingSid in recordingSids)
            await DeleteAsync(recordingSid.ToString(), cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, PhoneVoicemail voicemail, string suffix)
    {
        var host = options.Region.ToLowerInvariant() switch
        {
            "ie1" => "api.dublin.ie1.twilio.com",
            "au1" => "api.sydney.au1.twilio.com",
            _ => "api.twilio.com"
        };
        var request = new HttpRequestMessage(
            method,
            $"https://{host}/2010-04-01/Accounts/{voicemail.AccountSid}/Recordings/{voicemail.RecordingSid}{suffix}");
        var username = string.IsNullOrWhiteSpace(options.ApiKeySid)
            ? voicemail.AccountSid
            : options.ApiKeySid;
        var password = string.IsNullOrWhiteSpace(options.ApiKeySecret)
            ? options.AuthToken
            : options.ApiKeySecret;
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));
        return request;
    }

    private static bool IsSid(string value, string prefix)
        => value.Length == 34
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.AsSpan(2).ToString().All(char.IsAsciiHexDigit);
}
