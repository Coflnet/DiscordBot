using System.Text.Json;
using NUnit.Framework;

public class DeveloperUpdatePersistenceTests
{
    [Test]
    public void OnlyConfiguredChannelsAreCacheable()
    {
        Assert.That(MessageController.IsConfiguredCacheChannel(MessageController.DeveloperUpdateChannelId), Is.True);
        Assert.That(MessageController.IsConfiguredCacheChannel(MessageController.TestChannelId), Is.True);
        Assert.That(MessageController.IsConfiguredCacheChannel(1), Is.False);
    }

    [Test]
    public void PublicUpdateKeepsDeveloperAttributionWithoutDiscordAuthorId()
    {
        var json = JsonSerializer.Serialize(new DiscordMessage
        {
            ChannelId = MessageController.DeveloperUpdateChannelId,
            MessageId = 1,
            Content = "Release notes",
            AuthorName = "ProudDeveloper",
            CreatedAt = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(json, Does.Contain("\"authorName\":\"ProudDeveloper\""));
        Assert.That(json, Does.Not.Contain("authorId").IgnoreCase);
        Assert.That(typeof(DiscordMessage).GetProperty("AuthorId"), Is.Null);
    }

    [TestCase("Thanks <@12345>", "Thanks @Finder")]
    [TestCase("Thanks <@!12345>", "Thanks @Finder")]
    [TestCase("No user mention", "No user mention")]
    public void DiscordUserMentionsKeepCreditWithoutExposingIds(string content, string expected)
    {
        var userNames = new Dictionary<ulong, string> { [12345] = "Finder" };

        Assert.That(MessageController.ReplaceDiscordUserMentions(content, userNames), Is.EqualTo(expected));
    }
}
