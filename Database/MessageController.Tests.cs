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

    private static DiscordMessage Msg(ulong id, string content, params string[] attachments) => new()
    {
        MessageId = id,
        Content = content,
        Attachments = attachments.Select((a, i) => (Key: (long)i + 1, Value: a)).ToDictionary(a => a.Key, a => a.Value),
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Test]
    public void BlankFetchedMessageDoesNotOverwriteStoredContent()
    {
        var stored = new[] { Msg(1, "Release ➡️ notes", "https://cdn/a.png") };
        var merged = MessageController.MergeForCache(stored, [Msg(1, "")]);

        Assert.That(merged, Has.Count.EqualTo(1));
        Assert.That(merged[0].Content, Is.EqualTo("Release ➡️ notes"));
        Assert.That(merged[0].Attachments.Values, Is.EqualTo(new[] { "https://cdn/a.png" }));
    }

    [Test]
    public void FetchedContentWinsAndUnfetchedStoredMessagesAreKept()
    {
        var merged = MessageController.MergeForCache([Msg(1, "old"), Msg(2, "kept")], [Msg(1, "new", "u"), Msg(3, "added")]);

        Assert.That(merged.Select(m => m.MessageId), Is.EqualTo(new ulong[] { 3, 2, 1 }));
        Assert.That(merged.Single(m => m.MessageId == 1).Content, Is.EqualTo("new"));
    }

    [Test]
    public void MonthWindowCoversOnlyThatUtcMonth()
    {
        var window = MessageController.MonthWindow.Of(new DateTime(2026, 2, 14, 12, 0, 0, DateTimeKind.Utc));

        Assert.That(window.Start, Is.EqualTo(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.That(window.End, Is.EqualTo(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.That(window.StartSnowflake, Is.LessThan(window.EndSnowflake));
        Assert.That(Discord.SnowflakeUtils.FromSnowflake(window.EndSnowflake), Is.EqualTo(window.End));
        Assert.That(window.Contains(new DiscordMessage { CreatedAt = window.Start }), Is.True);
        Assert.That(window.Contains(new DiscordMessage { CreatedAt = window.End }), Is.False);
        Assert.That(window.Contains(new DiscordMessage { CreatedAt = window.Start.AddTicks(-1) }), Is.False);
    }
}
