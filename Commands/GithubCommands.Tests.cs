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
}
