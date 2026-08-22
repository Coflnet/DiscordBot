using System.Security.Cryptography;
using System.Text;
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
            Assert.Throws<EvidenceDenied>(() => service.ValidateBinding(binding[..^1] + (binding[^1] == 'a' ? "b" : "a"), "Coflnet/SkyModCommands", 434));
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
    public void IssueSourcesAndAttachedThreadsStayExact()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/SkyModCommands", IssueEvidenceService.CoflnetGuildId), Is.True);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/SkyModCommands", 0), Is.False);
            Assert.That(IssueEvidenceService.IsAllowedIssueSource("Coflnet/Other", IssueEvidenceService.CoflnetGuildId), Is.False);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 10, 20), Is.True);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 11, 20), Is.False);
            Assert.That(DiscordHandler.IsExactAttachedThread(10, 20, 10, 21), Is.False);
            Assert.That(DiscordHandler.EvidenceThreadWindowLimits(10, true), Is.EqualTo((4, 5)));
            Assert.That(DiscordHandler.EvidenceThreadWindowLimits(10, false), Is.EqualTo((0, 9)));
        });
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
