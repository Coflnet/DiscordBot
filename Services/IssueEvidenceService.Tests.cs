using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

public sealed class IssueEvidenceServiceTests
{
    private static readonly byte[] BindingKey = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private static readonly byte[] ClientKey = Enumerable.Repeat((byte)0x52, 32).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static IssueEvidenceService Service(DateTimeOffset? now = null) => new(
        BindingKey, ClientKey, null!, null!, NullLogger<IssueEvidenceService>.Instance, () => now ?? Now);

    [Test]
    public void BindingIsExactIssueScopedTamperEvidentAndExpiring()
    {
        var service = Service();
        var binding = service.CreateBinding("Coflnet/SkyModCommands", 434,
            IssueEvidenceService.CoflnetGuildId, 1540465169019179128, 1540479250002354246);
        var payload = service.ValidateBinding(binding, "Coflnet/SkyModCommands", 434);
        Assert.Multiple(() =>
        {
            Assert.That(payload.ChannelId, Is.EqualTo("1540465169019179128"));
            Assert.That(payload.MessageId, Is.EqualTo("1540479250002354246"));
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding, "Coflnet/SkyModCommands", 435));
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding, "Coflnet/SkyApi", 434));
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding[..20] + (binding[20] == 'a' ? "b" : "a") + binding[21..], "Coflnet/SkyModCommands", 434));
            Assert.Throws<EvidenceDenied>(() => Service(Now.AddDays(15)).ValidateBinding(binding, "Coflnet/SkyModCommands", 434));
        });
    }

    [Test]
    public void CursorCannotCrossBindingAndIsStable()
    {
        var service = Service();
        var cursor = service.CreateCursor(new string('a', 64), 1540479250002354246, 17);
        Assert.Multiple(() =>
        {
            Assert.That(service.ValidateCursor(cursor, new string('a', 64)), Is.EqualTo((1540479250002354246UL, 17)));
            Assert.Throws<EvidenceDenied>(() => service.ValidateCursor(cursor, new string('b', 64)));
            Assert.Throws<EvidenceDenied>(() => service.ValidateCursor(cursor + "a", new string('a', 64)));
        });
    }

    [Test]
    public void ClientHmacIsExactFreshAndReplayProtected()
    {
        var service = Service();
        var timestamp = Now.ToString("O");
        var nonce = Base64Url(Enumerable.Repeat((byte)0x33, 16).ToArray());
        var unsigned = new EvidenceRequest("coflnet.discord.issue-evidence-request/v1", "Coflnet/SkyModCommands", 434,
            new string('a', 96), "", timestamp, nonce, "");
        var signed = Encoding.UTF8.GetBytes(string.Join("\n", "POST", "/internal/v1/agent/issue-evidence",
            unsigned.Schema, unsigned.Repository, unsigned.IssueNumber, unsigned.Binding, unsigned.After, unsigned.Timestamp, unsigned.Nonce));
        var signature = Base64Url(HMACSHA256.HashData(ClientKey, signed));
        var request = unsigned with { Signature = signature };
        Assert.Multiple(() =>
        {
            Assert.That(service.ValidateClientRequest(request), Is.True);
            Assert.That(service.ValidateClientRequest(request), Is.False);
            Assert.That(Service().ValidateClientRequest(request with { Repository = "Coflnet/SkyApi" }), Is.False);
            Assert.That(Service(Now.AddMinutes(2)).ValidateClientRequest(request), Is.False);
        });
    }

    [Test]
    public void DiscordIdentifiersBecomeScopedOpaqueReferences()
    {
        var service = Service();
        const string input = "<@12345678901234567> <@!12345678901234567> <@&22345678901234567> <#32345678901234567> https://discord.com/api/webhooks/1/secret http://discord.gg/invite https://images-ext-1.discordapp.net/external/token";
        var first = service.SanitizeDiscordReferences(input, new string('a', 64));
        var second = service.SanitizeDiscordReferences(input, new string('b', 64));
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Not.Contain("12345678901234567"));
            Assert.That(first, Does.Not.Contain("discordapp.com"));
            Assert.That(first, Does.Not.Contain("discord.com").And.Not.Contain("discord.gg").And.Not.Contain("discordapp.net"));
            Assert.That(first, Does.Contain("[USER:").And.Contain("[ROLE:").And.Contain("[CHANNEL:"));
            Assert.That(second, Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void ProbeHmacIsBoundToItsExactPath()
    {
        var service = Service();
        var timestamp = Now.ToString("O");
        var nonce = Base64Url(Enumerable.Repeat((byte)0x44, 16).ToArray());
        var request = new EvidenceRequest("coflnet.discord.issue-evidence-probe/v1", "", 0, "", "", timestamp, nonce, "");
        var signed = Encoding.UTF8.GetBytes(string.Join("\n", "POST", "/internal/v1/agent/issue-evidence/probe",
            request.Schema, request.Repository, request.IssueNumber, request.Binding, request.After, request.Timestamp, request.Nonce));
        request = request with { Signature = Base64Url(HMACSHA256.HashData(ClientKey, signed)) };
        Assert.Multiple(() =>
        {
            Assert.That(service.ValidateClientRequest(request, "/internal/v1/agent/issue-evidence/probe"), Is.True);
            Assert.That(Service().ValidateClientRequest(request), Is.False);
        });
    }

    [Test]
    public async Task ProbeAcceptsKubernetesServiceProxyOctetStream()
    {
        var service = Service();
        var timestamp = Now.ToString("O");
        var nonce = Base64Url(Enumerable.Repeat((byte)0x45, 16).ToArray());
        var request = new EvidenceRequest("coflnet.discord.issue-evidence-probe/v1", "", 0, "", "", timestamp, nonce, "");
        var signed = Encoding.UTF8.GetBytes(string.Join("\n", "POST", "/internal/v1/agent/issue-evidence/probe",
            request.Schema, request.Repository, request.IssueNumber, request.Binding, request.After, request.Timestamp, request.Nonce));
        request = request with { Signature = Base64Url(HMACSHA256.HashData(ClientKey, signed)) };
        var controller = new IssueEvidenceController(service, NullLogger<IssueEvidenceController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.ContentType = "application/octet-stream";
        controller.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

        var response = await controller.Probe(CancellationToken.None);

        Assert.That(response, Is.TypeOf<OkObjectResult>());
    }

    [Test]
    public void IssueSourcesAndAttachedThreadsStayExact()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/SkyModCommands", IssueEvidenceService.CoflnetGuildId), Is.True);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/SkyModCommands", 0), Is.True);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/SkyModCommands", 999), Is.False);
            // Any Coflnet repo is a valid target; anything outside the org (or a name that is not a
            // plain repo name) is not.
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/AnyOtherRepo", IssueEvidenceService.CoflnetGuildId), Is.True);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Someone/SkyModCommands", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/Sky/Mod", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/..", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/Sky Mod", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 10, 20), Is.True);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 11, 20), Is.False);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 10, 21), Is.False);
            Assert.That(DiscordHandler.EvidenceThreadWindowLimits(10, true), Is.EqualTo((9, 0)));
            Assert.That(DiscordHandler.EvidenceThreadWindowLimits(10, false), Is.EqualTo((0, 9)));
            Assert.That(DiscordHandler.EvidenceThreadWindowLimits(IssueEvidenceService.MaxMessages, true), Is.EqualTo((199, 0)));
        });
    }

    // Regression: /issue repo:skymodcommands created issue #435 with no evidence binding, because
    // the allow-list was matched ordinally while GitHub repository identity is case-insensitive.
    // Any casing must bind, and must still resolve the binding on fetch.
    [Test]
    public void IssueSourceRepositoryCasingDoesNotBreakBinding()
    {
        var service = Service();
        var binding = service.CreateBinding("Coflnet/skymodcommands", 435,
            IssueEvidenceService.CoflnetGuildId, 1540537726070300805, 1540726822130557128);
        Assert.Multiple(() =>
        {
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/skymodcommands", IssueEvidenceService.CoflnetGuildId), Is.True);
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/skymodcommands", IssueEvidenceService.CoflnetGuildId, 1540726822130557128), Is.True);
            Assert.That(service.ValidateBinding(binding, "Coflnet/SkyModCommands", 435).MessageId, Is.EqualTo("1540726822130557128"));
            Assert.That(service.ValidateBinding(binding, "Coflnet/skymodcommands", 435).MessageId, Is.EqualTo("1540726822130557128"));
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding, "Coflnet/SkyApi", 435));
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding, "Coflnet/Other", 435));
        });
    }

    [Test]
    public void DirectMessageBindingRequiresAndBindsRecipient()
    {
        var service = Service();
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateBinding("Coflnet/SkyModCommands", 434, 0, 1535522079699509299, 1540607865847418932));
        Assert.Throws<InvalidOperationException>(() => service.CreateBinding("Coflnet/SkyModCommands", 434,
            IssueEvidenceService.CoflnetGuildId, 1535522079699509299, 1540607865847418932, 267680402594988033));

        var binding = service.CreateBinding("Coflnet/SkyModCommands", 434, 0, 1535522079699509299,
            1540607865847418932, 267680402594988033);
        var payload = service.ValidateBinding(binding, "Coflnet/SkyModCommands", 434);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GuildId, Is.EqualTo("0"));
            Assert.That(payload.ChannelId, Is.EqualTo("1535522079699509299"));
            Assert.That(payload.MessageId, Is.EqualTo("1540607865847418932"));
            Assert.That(payload.RecipientId, Is.EqualTo("267680402594988033"));
        });
    }

    [Test]
    public void DirectMessageEvidenceMustMatchChannelRecipientSourceAndAuthor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DiscordHandler.IsExactDirectMessageEvidence(10, 7, 20, 7, 10, 7, 20), Is.True);
            Assert.That(DiscordHandler.IsExactDirectMessageEvidence(11, 7, 20, 7, 10, 7, 20), Is.False);
            Assert.That(DiscordHandler.IsExactDirectMessageEvidence(10, 8, 20, 8, 10, 7, 20), Is.False);
            Assert.That(DiscordHandler.IsExactDirectMessageEvidence(10, 7, 21, 7, 10, 7, 20), Is.False);
            Assert.That(DiscordHandler.IsExactDirectMessageEvidence(10, 7, 20, 8, 10, 7, 20), Is.False);
        });
    }

    [Test]
    public void DirectMessageMirrorMustMatchChannelRecipientSourceAndBotAuthor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DiscordHandler.IsExactDirectMessageMirror(10, 7, 20, 99, 99, 10, 7, 20), Is.True);
            Assert.That(DiscordHandler.IsExactDirectMessageMirror(11, 7, 20, 99, 99, 10, 7, 20), Is.False); // wrong channel
            Assert.That(DiscordHandler.IsExactDirectMessageMirror(10, 8, 20, 99, 99, 10, 7, 20), Is.False); // wrong recipient
            Assert.That(DiscordHandler.IsExactDirectMessageMirror(10, 7, 21, 99, 99, 10, 7, 20), Is.False); // wrong source
            Assert.That(DiscordHandler.IsExactDirectMessageMirror(10, 7, 20, 7, 99, 10, 7, 20), Is.False); // author is the operator, not the bot
        });
    }

    [Test]
    public void BotDmMirrorSourceKindOnlyAcceptedForADirectMessageWithARecipient()
    {
        var service = Service();
        // Wrong pairing: bot-dm-mirror requires guildId 0 and a non-zero recipient.
        Assert.Throws<InvalidOperationException>(() => service.CreateBinding("Coflnet/SkyModCommands", 434,
            IssueEvidenceService.CoflnetGuildId, 1535522079699509299, 1540607865847418932, 0, "bot-dm-mirror"));
        // Unknown source kinds are rejected outright.
        Assert.Throws<InvalidOperationException>(() => service.CreateBinding("Coflnet/SkyModCommands", 434,
            0, 1535522079699509299, 1540607865847418932, 267680402594988033, "something-else"));

        var binding = service.CreateBinding("Coflnet/SkyModCommands", 434, 0, 1535522079699509299,
            1540607865847418932, 267680402594988033, "bot-dm-mirror");
        var payload = service.ValidateBinding(binding, "Coflnet/SkyModCommands", 434);

        Assert.Multiple(() =>
        {
            Assert.That(payload.SourceKind, Is.EqualTo("bot-dm-mirror"));
            Assert.That(payload.RecipientId, Is.EqualTo("267680402594988033"));
        });
    }

    [Test]
    public async Task DownloadIssueImageRejectsContentTypeMismatch()
    {
        var service = ServiceWithHttpResponse(StubResponse("image/svg+xml", Png));

        Assert.That(await service.DownloadIssueImage("https://cdn.discordapp.com/x.png", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task DownloadIssueImageRejectsOversizeBody()
    {
        var response = StubResponse("image/png", Png);
        response.Content.Headers.ContentLength = (10 << 20) + 1;
        var service = ServiceWithHttpResponse(response);

        Assert.That(await service.DownloadIssueImage("https://cdn.discordapp.com/x.png", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task DownloadIssueImageRejectsMagicByteMismatch()
    {
        var service = ServiceWithHttpResponse(StubResponse("image/png", new byte[] { 1, 2, 3, 4 }));

        Assert.That(await service.DownloadIssueImage("https://cdn.discordapp.com/x.png", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task DownloadIssueImageAcceptsAMatchingImage()
    {
        var service = ServiceWithHttpResponse(StubResponse("image/png", Png));

        var result = await service.DownloadIssueImage("https://cdn.discordapp.com/x.png", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.MediaType, Is.EqualTo("image/png"));
            Assert.That(result.Value.Data, Is.EqualTo(Png));
        });
    }

    [Test]
    public async Task DownloadIssueImageIdentifiesTheBotToDiscord()
    {
        var factory = new StubHttpClientFactory(StubResponse("image/png", Png));
        var service = new IssueEvidenceService(BindingKey, ClientKey, null!, factory,
            NullLogger<IssueEvidenceService>.Instance, () => Now);

        Assert.That(await service.DownloadIssueImage("https://cdn.discordapp.com/x.png", CancellationToken.None), Is.Not.Null);
        Assert.That(factory.UserAgent, Is.EqualTo("DiscordBot (https://github.com/Coflnet/DiscordBot, 1)"));
    }

    [Test]
    public async Task DownloadImageRejectsServedTypeThatDiffersFromTheDeclaredAttachmentType()
    {
        // Served bytes are a valid, correctly-detected PNG - but Discord declared this attachment
        // as a JPEG. Fetch reports attachment.ContentType (not what was actually served) as the
        // payload's media_type, so accepting this would let the evidence payload misdeclare its
        // own bytes - exactly the provenance property DownloadImage exists to guarantee.
        var service = ServiceWithHttpResponse(StubResponse("image/png", Png));
        var attachment = new StubAttachment { ContentType = "image/jpeg", Size = Png.Length, Url = "https://cdn.discordapp.com/x.png" };

        Assert.That(await service.DownloadImage(attachment, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task DownloadImageAcceptsAMatchingDeclaredType()
    {
        var service = ServiceWithHttpResponse(StubResponse("image/png", Png));
        var attachment = new StubAttachment { ContentType = "image/png", Size = Png.Length, Url = "https://cdn.discordapp.com/x.png" };

        Assert.That(await service.DownloadImage(attachment, CancellationToken.None), Is.EqualTo(Png));
    }

    private static readonly byte[] Png = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 };

    private static HttpResponseMessage StubResponse(string contentType, byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static IssueEvidenceService ServiceWithHttpResponse(HttpResponseMessage response) => new(
        BindingKey, ClientKey, null!, new StubHttpClientFactory(response), NullLogger<IssueEvidenceService>.Instance, () => Now);

    private sealed class StubHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public string? UserAgent { get; private set; }
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(response,
            request => UserAgent = request.Headers.UserAgent.ToString()));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response, Action<HttpRequestMessage> observe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe(request);
            return Task.FromResult(response);
        }
    }

    // Minimal IAttachment stub - only DownloadImage's own fields (Url, Size, ContentType) matter here.
    private sealed class StubAttachment : IAttachment
    {
        public ulong Id => 0;
        public DateTimeOffset CreatedAt => default;
        public string Filename { get; init; } = "image.png";
        public required string Url { get; init; }
        public string ProxyUrl => "";
        public required int Size { get; init; }
        public int? Height => null;
        public int? Width => null;
        public bool Ephemeral => false;
        public string Description => "";
        public required string ContentType { get; init; }
        public double? Duration => null;
        public string Waveform => "";
        public byte[] WaveformBytes => Array.Empty<byte>();
        public AttachmentFlags Flags => AttachmentFlags.None;
        public IReadOnlyCollection<IUser> ClipParticipants => Array.Empty<IUser>();
        public string Title => "";
        public DateTimeOffset? ClipCreatedAt => null;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
