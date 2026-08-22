using NUnit.Framework;

public class IssueDraftServiceTests
{
    [Test]
    public void DraftIsBoundToUserChannelAndConsumedOnce()
    {
        var now = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var next = 0;
        var service = new IssueDraftService(() => now, () => Enumerable.Repeat((byte)++next, 18).ToArray());
        var token = service.Create("title", "hypixel-react", "details", 7, 8, 9);

        Assert.Multiple(() =>
        {
            Assert.That(service.Peek(token, 7, 8, 9).Body, Is.EqualTo("details"));
            Assert.That(service.Peek(token, 7, 8, 9).Repository, Is.EqualTo("hypixel-react"));
            Assert.Throws<IssueDraftDenied>(() => service.Peek(token, 6, 8, 9));
            Assert.Throws<IssueDraftDenied>(() => service.Peek(token, 7, 10, 9));
            Assert.Throws<IssueDraftDenied>(() => service.Peek(token, 7, 8, 10));
            Assert.That(service.Take(token, 7, 8, 9).Title, Is.EqualTo("title"));
            Assert.Throws<IssueDraftDenied>(() => service.Take(token, 7, 8, 9));
        });
    }

    [Test]
    public void SourceUrlMustBeBlankOrAnExactMessageLink()
    {
        var now = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var next = 0;
        var service = new IssueDraftService(() => now, () => Enumerable.Repeat((byte)++next, 18).ToArray());
        const string link = "https://discord.com/channels/@me/1535522079699509299/1540607865847418932";

        var token = service.Create("title", "SkyModCommands", "details", 7, 8, 9, link);

        Assert.Multiple(() =>
        {
            Assert.That(service.Peek(token, 7, 8, 9).SourceUrl, Is.EqualTo(link));
            Assert.Throws<IssueDraftDenied>(() => service.Create("title", "SkyModCommands", "details", 7, 8, 9, "not-a-link"));
        });
    }

    [Test]
    public void AttachedImageUrlMustBeBlankOrAPublicIssueImageLink()
    {
        var now = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var next = 0;
        var service = new IssueDraftService(() => now, () => Enumerable.Repeat((byte)++next, 18).ToArray());
        const string image = "https://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/report.png";

        var token = service.Create("title", "SkyModCommands", "details", 7, 8, 9, attachedImageUrl: image);

        Assert.Multiple(() =>
        {
            Assert.That(service.Peek(token, 7, 8, 9).AttachedImageUrl, Is.EqualTo(image));
            Assert.Throws<IssueDraftDenied>(() => service.Create("title", "SkyModCommands", "details", 7, 8, 9, attachedImageUrl: "not-a-url"));
            Assert.Throws<IssueDraftDenied>(() => service.Create("title", "SkyModCommands", "details", 7, 8, 9,
                attachedImageUrl: "https://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/report.svg"));
        });
    }

    [Test]
    public void DraftExpiresAndCapacityIsBounded()
    {
        var now = DateTimeOffset.Parse("2026-08-22T00:00:00Z");
        var next = 0;
        var service = new IssueDraftService(() => now, () => BitConverter.GetBytes(++next).Concat(new byte[14]).ToArray());
        var expired = service.Create("title", "SkyModCommands", "", 7, 8, 9);
        now += IssueDraftService.Lifetime;
        Assert.Throws<IssueDraftDenied>(() => service.Peek(expired, 7, 8, 9));
        for (var index = 0; index < IssueDraftService.MaxPending; index++)
            service.Create("title", "SkyModCommands", "", 7, 8, (ulong)(10 + index));
        Assert.Throws<IssueDraftDenied>(() => service.Create("title", "SkyModCommands", "", 7, 8, 1000));
    }
}
