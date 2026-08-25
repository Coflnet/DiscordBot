using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

public sealed class IssueEvidenceService
{
    internal const ulong CoflnetGuildId = 267680588666896385;
    internal const int MaxMessages = 200;
    internal const int MaxMessageBytes = 8 << 10;
    internal const int MaxTextBytes = 512 << 10;
    internal const int MaxImages = 5;
    internal const int MaxImageBytes = 10 << 20;
    internal const int MaxTotalImageBytes = 25 << 20;
    // Any repository in the Coflnet org is a valid evidence target. What bounds the blast radius is
    // the binding itself - it names one exact issue and one exact Discord message, and the fetch is
    // separately authenticated with the client key - not an enumerated repo list, which only ever
    // managed to silently drop evidence when a repo was not on it.
    private static readonly Regex RepositoryName = new(@"^Coflnet/(?!\.{1,2}$)[A-Za-z0-9_.-]{1,100}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UserMention = new(@"<@!?(?<id>[0-9]{17,20})>", RegexOptions.CultureInvariant);
    private static readonly Regex RoleMention = new(@"<@&(?<id>[0-9]{17,20})>", RegexOptions.CultureInvariant);
    private static readonly Regex ChannelMention = new(@"<#(?<id>[0-9]{17,20})>", RegexOptions.CultureInvariant);
    private static readonly Regex DiscordUrl = new(@"https?://(?:[a-z0-9-]+\.)*(?:discord\.com|discordapp\.com|discordapp\.net|discord\.gg|discord\.media|discordcdn\.com|discord\.gift|discordstatus\.com|dis\.gd)(?::[0-9]{1,5})?(?:[/#?][^\s<>]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly byte[] bindingKey;
    private readonly byte[] clientKey;
    private readonly DiscordHandler discord;
    private readonly IHttpClientFactory httpClients;
    private readonly ILogger<IssueEvidenceService> logger;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<string, DateTimeOffset> recentNonces = new(StringComparer.Ordinal);
    private readonly object nonceLock = new();

    public IssueEvidenceService(IConfiguration configuration, DiscordHandler discord,
        IHttpClientFactory httpClients, ILogger<IssueEvidenceService> logger)
        : this(ReadKey(configuration["AgentEvidence:BindingKey"]), ReadKey(configuration["AgentEvidence:ClientKey"]),
            discord, httpClients, logger, () => DateTimeOffset.UtcNow) { }

    internal IssueEvidenceService(byte[] bindingKey, byte[] clientKey, DiscordHandler discord,
        IHttpClientFactory httpClients, ILogger<IssueEvidenceService> logger, Func<DateTimeOffset> now)
    {
        this.bindingKey = bindingKey;
        this.clientKey = clientKey;
        this.discord = discord;
        this.httpClients = httpClients;
        this.logger = logger;
        this.now = now;
    }

    public bool IsConfigured => bindingKey.Length == 32 && clientKey.Length == 32;

    internal static bool IsAllowedRepository(string repository) => RepositoryName.IsMatch(repository);

    internal static bool IsAllowedIssueSource(string repository, ulong guildId) =>
        IsAllowedRepository(repository) && guildId is 0 or CoflnetGuildId;

    public string CreateBinding(string repository, long issueNumber, ulong guildId, ulong channelId, ulong messageId,
        ulong recipientId = 0, string? sourceKind = null)
    {
        if (!IsConfigured || !IsAllowedIssueSource(repository, guildId) || issueNumber < 1
            || channelId == 0 || messageId == 0 || (guildId == 0) != (recipientId != 0)
            || sourceKind is not (null or "" or "bot-dm-mirror")
            || (sourceKind == "bot-dm-mirror" && !(guildId == 0 && recipientId != 0)))
            throw new InvalidOperationException("Discord issue evidence binding is unavailable for this target");
        var payload = new BindingPayload(1, repository, issueNumber, guildId.ToString(), channelId.ToString(),
            messageId.ToString(), recipientId.ToString(), sourceKind, now().UtcDateTime.ToString("O"), Base64Url(RandomNumberGenerator.GetBytes(16)));
        var encodedPayload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var envelope = new BindingEnvelope(Base64Url(encodedPayload), Base64Url(Sign(bindingKey, encodedPayload)));
        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
    }

    internal BindingPayload ValidateBinding(string binding, string repository, long issueNumber)
    {
        if (!IsConfigured || binding.Length is < 80 or > 2048)
            throw new EvidenceDenied("invalid_binding");
        BindingEnvelope? envelope;
        byte[] payloadBytes;
        try
        {
            envelope = JsonSerializer.Deserialize<BindingEnvelope>(FromBase64Url(binding), JsonOptions);
            payloadBytes = FromBase64Url(envelope?.Payload ?? "");
        }
        catch
        {
            throw new EvidenceDenied("invalid_binding");
        }
        if (envelope == null || !CryptographicOperations.FixedTimeEquals(Sign(bindingKey, payloadBytes), FromBase64Url(envelope.Signature)))
            throw new EvidenceDenied("invalid_binding_signature");
        BindingPayload? payload;
        try { payload = JsonSerializer.Deserialize<BindingPayload>(payloadBytes, JsonOptions); }
        catch { throw new EvidenceDenied("invalid_binding_payload"); }
        if (payload == null || payload.Version != 1 || payload.IssueNumber != issueNumber
            || !IsAllowedRepository(payload.Repository)
            || !string.Equals(payload.Repository, repository, StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(payload.GuildId, out var guildId)
            || guildId is not (0 or CoflnetGuildId)
            || !ulong.TryParse(payload.ChannelId, out var channel) || channel == 0
            || !ulong.TryParse(payload.MessageId, out var message) || message == 0
            || !ulong.TryParse(payload.RecipientId ?? "0", out var recipient) || (guildId == 0) != (recipient != 0)
            || payload.SourceKind is not (null or "" or "bot-dm-mirror")
            || (payload.SourceKind == "bot-dm-mirror" && !(guildId == 0 && recipient != 0))
            || !DateTimeOffset.TryParse(payload.CreatedAt, out var created) || created > now().AddMinutes(1)
            || created < now().AddDays(-14) || FromBase64Url(payload.Nonce).Length != 16)
            throw new EvidenceDenied("binding_mismatch_or_expired");
        return payload;
    }

    public bool ValidateClientRequest(EvidenceRequest request, string path = "/internal/v1/agent/issue-evidence")
    {
        if (!IsConfigured || !DateTimeOffset.TryParse(request.Timestamp, out var sent) || Math.Abs((now() - sent).TotalSeconds) > 60)
            return false;
        byte[] nonceBytes;
        byte[] supplied;
        try { nonceBytes = FromBase64Url(request.Nonce); supplied = FromBase64Url(request.Signature); }
        catch { return false; }
        if (nonceBytes.Length != 16 || supplied.Length != 32)
            return false;
        var signed = Encoding.UTF8.GetBytes(string.Join("\n", "POST", path,
            request.Schema, request.Repository, request.IssueNumber, request.Binding, request.After, request.Timestamp, request.Nonce));
        if (!CryptographicOperations.FixedTimeEquals(Sign(clientKey, signed), supplied))
            return false;
        lock (nonceLock)
        {
            foreach (var expired in recentNonces.Where(entry => entry.Value < now().AddSeconds(-60)).Select(entry => entry.Key).ToList())
                recentNonces.Remove(expired);
            if (recentNonces.ContainsKey(request.Nonce) || recentNonces.Count >= 4096) return false;
            recentNonces[request.Nonce] = now();
        }
        return true;
    }

    public async Task<EvidenceResponse> Fetch(EvidenceRequest request, CancellationToken cancellationToken)
    {
        var binding = ValidateBinding(request.Binding, request.Repository, request.IssueNumber);
        var bindingDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Binding)));
        var channelId = ulong.Parse(binding.ChannelId);
        var sourceId = ulong.Parse(binding.MessageId);
        var guildId = ulong.Parse(binding.GuildId);
        var recipientId = ulong.Parse(binding.RecipientId ?? "0");
        var sourceKind = binding.SourceKind ?? "";
        ulong after = 0;
        var seen = 0;
        if (!string.IsNullOrEmpty(request.After)) (after, seen) = ValidateCursor(request.After, bindingDigest);
        var remaining = MaxMessages - seen;
        if (remaining == 0)
            return new("coflnet.discord.issue-evidence-raw/v1", request.Repository, request.IssueNumber,
                bindingDigest, [], [], request.After, true, new(0, 0, 0, 0));
        var rawMessages = await discord.GetExactEvidenceMessages(guildId, channelId, sourceId, recipientId, sourceKind, after, remaining);
        if (rawMessages.Count == 0)
            return new("coflnet.discord.issue-evidence-raw/v1", request.Repository, request.IssueNumber,
                bindingDigest, [], [], request.After, true, new(0, 0, 0, 0));

        var messages = new List<EvidenceMessage>();
        var images = new List<EvidenceImage>();
        var omittedMessages = 0;
        var omittedText = 0;
        var omittedImages = 0;
        var omittedImageBytes = 0;
        var totalText = 0;
        var totalImage = 0;
        var pageMessageIds = rawMessages.Select(message => message.Id).ToHashSet();
        foreach (var message in rawMessages.OrderBy(value => value.Id))
        {
            var content = SanitizeDiscordReferences(message.Content ?? "", bindingDigest);
            content = BoundUtf8(content, MaxMessageBytes, out var messageOmitted);
            if (totalText + Encoding.UTF8.GetByteCount(content) > MaxTextBytes)
            {
                omittedMessages++;
                omittedText += Encoding.UTF8.GetByteCount(content) + messageOmitted;
                continue;
            }
            totalText += Encoding.UTF8.GetByteCount(content);
            omittedText += messageOmitted;
            var reply = message.Reference?.MessageId;
            messages.Add(new(message.Id.ToString(), Reference("author", message.Author.Id.ToString(), bindingDigest),
                message.CreatedAt.UtcDateTime.ToString("O"), content,
                reply?.IsSpecified == true && pageMessageIds.Contains(reply.Value.Value) ? reply.Value.Value.ToString() : ""));
            foreach (var attachment in message.Attachments)
            {
                if (images.Count >= MaxImages) { omittedImages++; omittedImageBytes += (int)Math.Min(attachment.Size, int.MaxValue); continue; }
                var metadataRejection = GithubCommands.PublicIssueImageRejection(attachment.Size, attachment.ContentType, attachment.Url);
                if (metadataRejection != null)
                {
                    logger.LogInformation("Discord evidence image rejected: metadata_{Reason}", metadataRejection);
                    omittedImages++;
                    omittedImageBytes += (int)Math.Min(attachment.Size, int.MaxValue);
                    continue;
                }
                var data = await DownloadImage(attachment, cancellationToken);
                if (data == null || totalImage + data.Length > MaxTotalImageBytes)
                {
                    omittedImages++; omittedImageBytes += data?.Length ?? (int)Math.Min(attachment.Size, int.MaxValue); continue;
                }
                totalImage += data.Length;
                var extension = attachment.ContentType == "image/png" ? "png" : attachment.ContentType == "image/jpeg" ? "jpg"
                    : attachment.ContentType == "image/gif" ? "gif" : "webp";
                images.Add(new(message.Id.ToString(), attachment.Id.ToString(), $"discord-evidence-{images.Count + 1}.{extension}",
                    attachment.ContentType!, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), Convert.ToBase64String(data)));
            }
        }
        var last = rawMessages.Max(value => value.Id);
        var complete = rawMessages.Count < remaining || seen + rawMessages.Count == MaxMessages;
        var next = CreateCursor(bindingDigest, last, seen + rawMessages.Count);
        logger.LogInformation("Discord issue evidence {Repository}#{Issue} binding={Binding} messages={Messages} images={Images} complete={Complete}",
            request.Repository, request.IssueNumber, bindingDigest, messages.Count, images.Count, complete);
        return new("coflnet.discord.issue-evidence-raw/v1", request.Repository, request.IssueNumber,
            bindingDigest, messages, images, next, complete,
            new(omittedMessages, omittedText, omittedImages, omittedImageBytes));
    }

    internal string SanitizeDiscordReferences(string value, string scope)
    {
        value = UserMention.Replace(value, match => Reference("user", match.Groups["id"].Value, scope));
        value = RoleMention.Replace(value, match => Reference("role", match.Groups["id"].Value, scope));
        value = ChannelMention.Replace(value, match => Reference("channel", match.Groups["id"].Value, scope));
        return DiscordUrl.Replace(value, "[DISCORD_URL_WITHHELD]");
    }

    private string Reference(string kind, string value, string scope)
    {
        var digest = Sign(bindingKey, Encoding.UTF8.GetBytes(scope + "\0" + kind + "\0" + value));
        return $"[{kind.ToUpperInvariant()}:{Base64Url(digest[..8]).ToLowerInvariant()}]";
    }

    internal string CreateCursor(string bindingDigest, ulong after, int seen)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(1, bindingDigest, after.ToString(), seen), JsonOptions);
        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(new BindingEnvelope(Base64Url(payload), Base64Url(Sign(bindingKey, payload))), JsonOptions));
    }

    internal (ulong After, int Seen) ValidateCursor(string cursor, string bindingDigest)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<BindingEnvelope>(FromBase64Url(cursor), JsonOptions)!;
            var payloadBytes = FromBase64Url(envelope.Payload);
            if (!CryptographicOperations.FixedTimeEquals(Sign(bindingKey, payloadBytes), FromBase64Url(envelope.Signature)))
                throw new Exception();
            var payload = JsonSerializer.Deserialize<CursorPayload>(payloadBytes, JsonOptions)!;
            if (payload.Version != 1 || payload.BindingSha256 != bindingDigest || !ulong.TryParse(payload.After, out var after)
                || after == 0 || payload.Seen is < 1 or > MaxMessages)
                throw new Exception();
            return (after, payload.Seen);
        }
        catch { throw new EvidenceDenied("invalid_cursor"); }
    }

    // Bounded, content-type-verified download shared by the harvested-attachment path (which
    // additionally knows the exact expected size) and pasted image links (which don't).
    internal async Task<(byte[] Data, string MediaType)?> DownloadIssueImage(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "image/png,image/jpeg,image/gif,image/webp");
        request.Headers.TryAddWithoutValidation("User-Agent", "DiscordBot (https://github.com/Coflnet/DiscordBot, 1)");
        using var response = await httpClients.CreateClient("discord-evidence-images")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            logger.LogInformation("Discord evidence image rejected: status_{Status}", (int)response.StatusCode);
            return null;
        }
        if (response.Content.Headers.ContentLength > MaxImageBytes)
        {
            logger.LogInformation("Discord evidence image rejected: content_length");
            return null;
        }
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
        {
            logger.LogInformation("Discord evidence image rejected: content_type");
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxImageBytes)
            {
                logger.LogInformation("Discord evidence image rejected: body_length");
                return null;
            }
            output.Write(buffer, 0, read);
        }
        var data = output.ToArray();
        var detected = DetectedMediaType(data);
        if (detected != contentType)
        {
            logger.LogInformation("Discord evidence image rejected: detected_type");
            return null;
        }
        return (data, detected);
    }

    // Bound to the exact type Discord declared for this attachment, not just any allowed image
    // type - otherwise the served/detected bytes could be misdeclared in the evidence payload's
    // media_type (which is taken from attachment.ContentType, not from what was actually served).
    // Discord's upload size is advisory: its signed CDN may normalize image bytes. The bounded,
    // detected download length is the evidence receipt's authoritative size.
    internal async Task<byte[]?> DownloadImage(IAttachment attachment, CancellationToken cancellationToken)
    {
        var result = await DownloadIssueImage(attachment.Url, cancellationToken);
        if (result == null) return null;
        if (result.Value.MediaType != attachment.ContentType)
        {
            logger.LogInformation("Discord evidence image rejected: attachment_type");
            return null;
        }
        return result.Value.Data;
    }

    private static string BoundUtf8(string value, int limit, out int omitted)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= limit) { omitted = 0; return value; }
        var length = limit;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        omitted = bytes.Length - length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string DetectedMediaType(byte[] value)
    {
        if (value.Length >= 8 && value.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (value.Length >= 3 && value[0] == 0xff && value[1] == 0xd8 && value[2] == 0xff) return "image/jpeg";
        if (value.Length >= 6 && (Encoding.ASCII.GetString(value, 0, 6) is "GIF87a" or "GIF89a")) return "image/gif";
        if (value.Length >= 12 && Encoding.ASCII.GetString(value, 0, 4) == "RIFF" && Encoding.ASCII.GetString(value, 8, 4) == "WEBP") return "image/webp";
        return "";
    }

    private static byte[] ReadKey(string? encoded)
    {
        try { var key = Convert.FromBase64String(encoded ?? ""); return key.Length == 32 ? key : []; }
        catch { return []; }
    }
    private static byte[] Sign(byte[] key, byte[] value) => HMACSHA256.HashData(key, value);
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        if (value.Length == 0 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new FormatException();
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal sealed record BindingPayload(int Version, string Repository, long IssueNumber, string GuildId,
        string ChannelId, string MessageId, string? RecipientId, string? SourceKind, string CreatedAt, string Nonce);
    private sealed record BindingEnvelope(string Payload, string Signature);
    private sealed record CursorPayload(int Version, string BindingSha256, string After, int Seen);
}

public sealed record EvidenceRequest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("issue_number")] long IssueNumber,
    [property: JsonPropertyName("binding")] string Binding,
    [property: JsonPropertyName("after")] string After,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("signature")] string Signature);
public sealed record EvidenceMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("author_ref")] string AuthorRef,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("reply_to")] string ReplyTo);
public sealed record EvidenceImage(
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("attachment_id")] string AttachmentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("data")] string Data);
public sealed record EvidenceOmissions(
    [property: JsonPropertyName("messages")] int Messages,
    [property: JsonPropertyName("text_bytes")] int TextBytes,
    [property: JsonPropertyName("images")] int Images,
    [property: JsonPropertyName("image_bytes")] int ImageBytes);
public sealed record EvidenceResponse(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("issue_number")] long IssueNumber,
    [property: JsonPropertyName("binding_sha256")] string BindingSha256,
    [property: JsonPropertyName("messages")] IReadOnlyList<EvidenceMessage> Messages,
    [property: JsonPropertyName("images")] IReadOnlyList<EvidenceImage> Images,
    [property: JsonPropertyName("next")] string Next,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("omitted")] EvidenceOmissions Omitted);
public sealed class EvidenceDenied(string reason) : Exception(reason) { public string Reason { get; } = reason; }
