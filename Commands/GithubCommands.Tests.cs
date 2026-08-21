using NUnit.Framework;

public class GitRepoAutocompleteHandlerTests
{
    private const string DiscordImage = "https://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/report.png";

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void BlankSearchTermDefaultsToSky(string? value)
    {
        Assert.That(GitRepoAutocompleteHandler.GetSearchTerm(value), Is.EqualTo("sky"));
    }

    [Test]
    public void EnteredSearchTermIsPreserved()
    {
        Assert.That(GitRepoAutocompleteHandler.GetSearchTerm("discord"), Is.EqualTo("discord"));
    }

    [TestCase(DiscordImage)]
    [TestCase(DiscordImage + "?ex=1234abcd&is=5678abcd&hm=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ReviewedDiscordImagesAreAccepted(string url)
    {
        Assert.That(GithubCommands.IsPublicIssueImage(1024, "image/png", url), Is.True);
    }

    [TestCase("http://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/report.png")]
    [TestCase("https://cdn.discordapp.com.evil.example/attachments/12345678901234567/23456789012345678/report.png")]
    [TestCase(DiscordImage + "?ex=1234abcd&hm=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&is=5678abcd")]
    [TestCase("https://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/a%2fb.png")]
    public void UnreviewedDiscordUrlsAreRejected(string url)
    {
        Assert.That(GithubCommands.IsPublicIssueImage(1024, "image/png", url), Is.False);
    }

    [Test]
    public void DiscordImageMetadataIsBounded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GithubCommands.IsPublicIssueImage(0, "image/png", DiscordImage), Is.False);
            Assert.That(GithubCommands.IsPublicIssueImage((10 << 20) + 1, "image/png", DiscordImage), Is.False);
            Assert.That(GithubCommands.IsPublicIssueImage(1024, "image/svg+xml", DiscordImage), Is.False);
        });
    }

    [Test]
    public void ExactReportLinkMustTargetCurrentChannel()
    {
        const string link = "https://discord.com/channels/267680588666896385/1540465169019179128/1540479250002354246";
        Assert.Multiple(() =>
        {
            Assert.That(GithubCommands.ExactMessageId(link, 267680588666896385, 1540465169019179128), Is.EqualTo(1540479250002354246));
            Assert.That(GithubCommands.ExactMessageId(link, 267680588666896385, 1540465169019179129), Is.Null);
            Assert.That(GithubCommands.ExactMessageId(link, 267680588666896386, 1540465169019179128), Is.Null);
            Assert.That(GithubCommands.ExactMessageId(link + "?redirect=1", 267680588666896385, 1540465169019179128), Is.Null);
        });
    }

    [Test]
    public void DefaultReportSelectionSkipsBotPromptAndWebhook()
    {
        var candidates = new[]
        {
            (Id: 30UL, IsBot: true, IsWebhook: false),
            (Id: 20UL, IsBot: false, IsWebhook: true),
            (Id: 10UL, IsBot: false, IsWebhook: false)
        };

        Assert.Multiple(() =>
        {
            Assert.That(GithubCommands.SelectReportMessageId(candidates), Is.EqualTo(10UL));
            Assert.That(GithubCommands.SelectReportMessageId(candidates.Take(2)), Is.Null);
        });
    }

    [Test]
    public void IssueContextIncludesAtMostThreeDistinctReviewedImages()
    {
        var images = Enumerable.Range(1, 5)
            .Select(index => (1024L, (string?)"image/png", DiscordImage.Replace("report.png", $"report-{index}.png")))
            .Append((1024L, (string?)"image/png", DiscordImage.Replace("report.png", "report-1.png")))
            .Append((1024L, (string?)"image/svg+xml", DiscordImage.Replace("report.png", "unsafe.svg")));

        var body = GithubCommands.AppendIssueContext("details", "SkyModCommands", "https://discord.com/channels/1/2/3", images);
        var unenrolled = GithubCommands.AppendIssueContext("details", "OtherRepo", "https://discord.com/channels/1/2/3", images);

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("context:https://discord.com/channels/1/2/3"));
            Assert.That(body.Split("![Discord issue image ").Length - 1, Is.EqualTo(3));
            Assert.That(body, Does.Not.Contain("report-4.png"));
            Assert.That(body, Does.Not.Contain("unsafe.svg"));
            Assert.That(unenrolled, Does.Not.Contain("![Discord issue image"));
        });
    }
}
