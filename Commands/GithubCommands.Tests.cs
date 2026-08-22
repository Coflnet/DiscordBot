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

    [TestCase(DiscordImage)]
    [TestCase(DiscordImage + "?ex=1234abcd&is=5678abcd&hm=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PastedImageLinksAreAcceptedWhenWellFormed(string url)
    {
        Assert.That(GithubCommands.IsPastedIssueImageUrl(url), Is.True);
    }

    [TestCase("http://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/report.png")]
    [TestCase("https://cdn.discordapp.com.evil.example/attachments/12345678901234567/23456789012345678/report.png")]
    [TestCase(DiscordImage + "?ex=1234abcd&hm=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&is=5678abcd")]
    public void PastedLinksAreRejectedWhenMalformed(string url)
    {
        Assert.That(GithubCommands.IsPastedIssueImageUrl(url), Is.False);
    }

    [Test]
    public void PastedSvgExtensionIsRejected()
    {
        Assert.That(GithubCommands.IsPastedIssueImageUrl(DiscordImage.Replace("report.png", "unsafe.svg")), Is.False);
    }

    [Test]
    public void ParsePastedImageUrlsKeepsValidDedupedAndCapped()
    {
        var text = string.Join("\n", DiscordImage, "not a url", DiscordImage,
            DiscordImage.Replace("report.png", "b.png"), DiscordImage.Replace("report.png", "c.png"), DiscordImage.Replace("report.png", "d.png"));

        var parsed = GithubCommands.ParsePastedImageUrls(text);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Count, Is.EqualTo(3));
            Assert.That(parsed, Does.Contain(DiscordImage));
            Assert.That(parsed, Does.Not.Contain(DiscordImage.Replace("report.png", "d.png")));
        });
    }

    [Test]
    public async Task TryDownloadIssueImageTreatsAThrowAsAFailedDownload()
    {
        Task<(byte[] Data, string MediaType)?> Throwing(string url, CancellationToken token) => throw new HttpRequestException("boom");

        var result = await GithubCommands.TryDownloadIssueImage(Throwing, "https://cdn.discordapp.com/x.png", CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryDownloadIssueImagePassesThroughASuccessfulResult()
    {
        Task<(byte[] Data, string MediaType)?> Succeeding(string url, CancellationToken token) =>
            Task.FromResult<(byte[] Data, string MediaType)?>((new byte[] { 1, 2, 3 }, "image/png"));

        var result = await GithubCommands.TryDownloadIssueImage(Succeeding, "https://cdn.discordapp.com/x.png", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.MediaType, Is.EqualTo("image/png"));
            Assert.That(result.Value.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
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
            (Id: 40UL, AuthorId: 7UL, IsBot: true, IsWebhook: false),
            (Id: 30UL, AuthorId: 7UL, IsBot: false, IsWebhook: true),
            (Id: 20UL, AuthorId: 8UL, IsBot: false, IsWebhook: false),
            (Id: 10UL, AuthorId: 7UL, IsBot: false, IsWebhook: false)
        };

        Assert.Multiple(() =>
        {
            Assert.That(GithubCommands.SelectReportMessageId(candidates, 7), Is.EqualTo(10UL));
            Assert.That(GithubCommands.SelectReportMessageId(candidates.Take(3), 7), Is.Null);
        });
    }

    [Test]
    public void FreeTextMessageIsIssueDetailWhileExactLinkSelectsSource()
    {
        const string link = "https://discord.com/channels/267680588666896385/1540465169019179128/1540479250002354246";
        var details = GithubCommands.ResolveMessageInput("existing", "Bazaar and AH prices should indicate live updates", 267680588666896385, 1540465169019179128);
        var exact = GithubCommands.ResolveMessageInput("existing", link, 267680588666896385, 1540465169019179128);

        Assert.Multiple(() =>
        {
            Assert.That(details.Body, Is.EqualTo("existing\n\nBazaar and AH prices should indicate live updates"));
            Assert.That(details.MessageId, Is.Null);
            Assert.That(details.DirectMessageChannelId, Is.Null);
            Assert.That(exact.Body, Is.EqualTo("existing"));
            Assert.That(exact.MessageId, Is.EqualTo(1540479250002354246));
            Assert.That(exact.DirectMessageChannelId, Is.Null);
        });
    }

    [Test]
    public void ExactDirectMessageLinkSelectsItsLinkedChannel()
    {
        const string link = "https://discord.com/channels/@me/1535522079699509299/1540607865847418932";
        var exact = GithubCommands.ResolveMessageInput("existing", link, null, 111111111111111111);

        Assert.Multiple(() =>
        {
            Assert.That(exact.Body, Is.EqualTo("existing"));
            Assert.That(exact.MessageId, Is.EqualTo(1540607865847418932));
            Assert.That(exact.DirectMessageChannelId, Is.EqualTo(1535522079699509299));
        });
    }

    [Test]
    public void CurrentDirectMessageLinkStillRequiresDmAuthorization()
    {
        const ulong channel = 1535522079699509299;
        var exact = GithubCommands.ResolveMessageInput("", "https://discord.com/channels/@me/1535522079699509299/1540607865847418932", null, channel);

        Assert.That(exact.DirectMessageChannelId, Is.EqualTo(channel));
    }

    [Test]
    public void DirectMessageSourceMustBeInvokingUsersExactChannel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GithubCommands.IsAuthorizedDirectMessage(10, 7, 10, 7), Is.True);
            Assert.That(GithubCommands.IsAuthorizedDirectMessage(10, 8, 10, 7), Is.False);
            Assert.That(GithubCommands.IsAuthorizedDirectMessage(11, 7, 10, 7), Is.False);
        });
    }

    [Test]
    public void CanBindEvidenceRequiresAMessageAndAnAllowedSource()
    {
        const ulong coflnet = IssueEvidenceService.CoflnetGuildId;
        Assert.Multiple(() =>
        {
            // No message at all - the "paste report content" path (unreadable source): never bindable.
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/SkyModCommands", 0, 0), Is.False);
            // guildId 0 with a real message only occurs when the source was verified as the invoking
            // user's own bot DM (Issue() only reaches canread=true with reportGuildId 0 in that case).
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/SkyModCommands", 0, 123), Is.True);
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/SkyModCommands", coflnet, 123), Is.True);
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/SkyModCommands", 999, 123), Is.False);
            // Every repo in the org counts; only targets outside it do not.
            Assert.That(GithubCommands.CanBindEvidence("Coflnet/OtherRepo", coflnet, 123), Is.True);
            Assert.That(GithubCommands.CanBindEvidence("Someone/SkyModCommands", coflnet, 123), Is.False);
            Assert.That(GithubCommands.EvidenceApplies("SkyModCommands"), Is.True);
            Assert.That(GithubCommands.EvidenceApplies("OtherRepo"), Is.True);
            Assert.That(GithubCommands.EvidenceApplies("Other/Repo"), Is.False);
        });
    }

    [Test]
    public void ResolveHarvestedImageUrlsKeepsOnlyReviewedImages()
    {
        var attachments = new[]
        {
            (1024L, (string?)"image/png", DiscordImage),
            (1024L, (string?)"image/svg+xml", DiscordImage.Replace("report.png", "unsafe.svg")),
        };

        Assert.That(GithubCommands.ResolveHarvestedImageUrls(attachments), Is.EqualTo(new[] { DiscordImage }));
    }

    [Test]
    public void ResolveAttachedImageUrlsRejectsMalformedLinks()
    {
        var urls = new[] { DiscordImage, DiscordImage.Replace("report.png", "unsafe.svg") };

        Assert.That(GithubCommands.ResolveAttachedImageUrls(urls), Is.EqualTo(new[] { DiscordImage }));
    }

    [Test]
    public void AppendIssueContextCapsAtThreeDistinctImagesAndNeverRendersThem()
    {
        var harvested = new[] { "img1", "img2", "img1", "img3", "img4" }.Select(name => DiscordImage.Replace("report.png", $"{name}.png"));

        var body = GithubCommands.AppendIssueContext("details", "SkyModCommands", "https://discord.com/channels/1/2/3", harvested, Enumerable.Empty<string>());
        var withoutImages = GithubCommands.AppendIssueContext("details", "OtherRepo", "https://discord.com/channels/1/2/3", Enumerable.Empty<string>(), Enumerable.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("context:https://discord.com/channels/1/2/3"));
            Assert.That(body, Does.Contain("3 screenshots attached as Discord evidence"));
            Assert.That(body, Does.Not.Contain("!["));
            Assert.That(withoutImages, Does.Not.Contain("screenshot"));
        });
    }

    [Test]
    public void AttachedImagesCountForAnyRepoAndSurviveTheHarvestedCap()
    {
        const string attachedUrl = "https://cdn.discordapp.com/attachments/12345678901234567/23456789012345678/attached.png";
        var harvested = new[] { "h1", "h2", "h3" }.Select(name => DiscordImage.Replace("report.png", $"{name}.png"));

        var attachedOnly = GithubCommands.AppendIssueContext("details", "OtherRepo", "https://discord.com/channels/1/2/3", Enumerable.Empty<string>(), new[] { attachedUrl });
        var capped = GithubCommands.AppendIssueContext("details", "SkyModCommands", "https://discord.com/channels/1/2/3", harvested, new[] { attachedUrl });

        Assert.Multiple(() =>
        {
            Assert.That(attachedOnly, Does.Contain("1 screenshot attached as Discord evidence"));
            Assert.That(capped, Does.Contain("3 screenshots attached as Discord evidence"));
            Assert.That(capped, Does.Not.Contain("!["));
            Assert.That(capped, Does.Not.Contain(attachedUrl));
        });
    }

    [Test]
    public void ContextLineEmbedsExactDirectMessageLinkVerbatimWithNoImageMarkdown()
    {
        const string link = "https://discord.com/channels/@me/1535522079699509299/1540607865847418932";
        var body = GithubCommands.AppendIssueContext("details", "OtherRepo", link, Enumerable.Empty<string>(), Enumerable.Empty<string>());
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("context:" + link));
            Assert.That(body, Does.Not.Contain("!["));
            Assert.That(body, Does.Not.Contain("screenshot"));
        });
    }

    [Test]
    public async Task EvidenceMarkerUpdateSuccessDoesNotReadOrClose()
    {
        var reads = 0;
        var closes = 0;

        await GithubCommands.FinalizeEvidenceMarker(
            () => Task.CompletedTask,
            () => { reads++; return Task.FromResult<string?>(null); },
            () => { closes++; return Task.CompletedTask; },
            "<!-- marker -->");

        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.Zero);
            Assert.That(closes, Is.Zero);
        });
    }

    [Test]
    public async Task AmbiguousUpdateWithExactMarkerIsAccepted()
    {
        var closes = 0;

        await GithubCommands.FinalizeEvidenceMarker(
            () => Task.FromException(new Exception("ambiguous")),
            () => Task.FromResult<string?>("body\n<!-- marker -->"),
            () => { closes++; return Task.CompletedTask; },
            "<!-- marker -->");

        Assert.That(closes, Is.Zero);
    }

    [Test]
    public void MissingEvidenceMarkerClosesExactIssueAndFails()
    {
        var closes = 0;

        Assert.That(async () => await GithubCommands.FinalizeEvidenceMarker(
            () => Task.FromException(new Exception("provider detail")),
            () => Task.FromResult<string?>("body\n<!-- marker -->suffix"),
            () => { closes++; return Task.CompletedTask; },
            "<!-- marker -->"), Throws.TypeOf<GithubCommands.IssueMarkerFinalizationException>());
        Assert.That(closes, Is.EqualTo(1));
    }

    [Test]
    public void FailedReadBackStillAttemptsCloseAndReturnsFixedError()
    {
        var closes = 0;

        var error = Assert.ThrowsAsync<GithubCommands.IssueMarkerFinalizationException>(() =>
            GithubCommands.FinalizeEvidenceMarker(
                () => Task.FromException(new Exception("update secret")),
                () => Task.FromException<string?>(new Exception("read secret")),
                () => { closes++; return Task.FromException(new Exception("close secret")); },
                "<!-- marker -->"));

        Assert.Multiple(() =>
        {
            Assert.That(closes, Is.EqualTo(1));
            Assert.That(error!.Message, Does.Not.Contain("secret"));
        });
    }
}
