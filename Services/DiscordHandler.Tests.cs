using NUnit.Framework;

public class DiscordHandlerTests
{
    [TestCase("https://sky.coflnet.com/item/STING?range=year&ultimate_chimera=4-4&looting=5-5&divine_gift=3-3", false)]
    [TestCase("Claim your gift https://example.com", true)]
    [TestCase("https://example.com claim your gift", true)]
    public void NitroScamLinksRequireSpaceBeforeGift(string content, bool expected)
    {
        Assert.That(DiscordHandler.IsNitroScamLink(content), Is.EqualTo(expected));
    }
}

public class FallbackMessageSourceTests
{
    [Test]
    public void MainClientDoesNotRequestMessageContent()
    {
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.MessageContent), Is.False);
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.GuildMessages), Is.True);
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.GuildScheduledEvents), Is.False);
    }

    [Test]
    public void FallbackClientOnlyReadsMessages()
    {
        Assert.That(FallbackMessageSource.Intents, Is.EqualTo(
            Discord.GatewayIntents.Guilds | Discord.GatewayIntents.GuildMessages | Discord.GatewayIntents.MessageContent));
    }

    [TestCase(true, true, MessageSource.Fallback)]
    [TestCase(true, false, MessageSource.Main)]
    [TestCase(false, true, MessageSource.Main)]
    [TestCase(false, false, MessageSource.Main)]
    public void EvidenceReadPrefersFallbackWhenConnectedAndInGuild(bool connected, bool inGuild, MessageSource expected)
    {
        Assert.That(FallbackMessageSource.ChooseEvidenceSource(connected, inGuild), Is.EqualTo(expected));
    }

    [TestCase(MessageSource.Fallback, false, false, true)]
    [TestCase(MessageSource.Main, false, false, false)]
    [TestCase(MessageSource.Main, true, false, true)]
    [TestCase(MessageSource.Main, false, true, true)]
    public void CacheContentIsOnlyTrustedWhenActuallyDelivered(MessageSource source, bool byMain, bool mentionsMain, bool expected)
    {
        Assert.That(FallbackMessageSource.IsCacheContentTrusted(source, byMain, mentionsMain), Is.EqualTo(expected));
    }

    [Test]
    public void MainClientEvidenceWithBlankUserMessagesIsNotAvailable()
    {
        // bot-authored and bot-mentioning messages carry content via main; ordinary user messages do not
        Assert.That(FallbackMessageSource.AllContentAvailable([(true, false), (false, true)]), Is.True);
        Assert.That(FallbackMessageSource.AllContentAvailable([(true, false), (false, false)]), Is.False);
        Assert.That(FallbackMessageSource.AllContentAvailable([]), Is.True);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    [TestCase("token", true)]
    public void DisabledWithoutToken(string? token, bool expected)
    {
        Assert.That(FallbackMessageSource.IsEnabled(token), Is.EqualTo(expected));
    }

    // source, guildId, fallbackInGuild, mainInGuild, expected
    [TestCase(MessageSource.Fallback, 1UL, true, true, true, Description = "shared guild: fallback serves")]
    [TestCase(MessageSource.Main, 1UL, true, true, false, Description = "shared guild: main ignored")]
    [TestCase(MessageSource.Main, 1UL, false, true, true, Description = "main-only guild: main serves")]
    [TestCase(MessageSource.Fallback, 1UL, true, false, false, Description = "fallback-only guild: ignored")]
    [TestCase(MessageSource.Fallback, null, false, false, false, Description = "DM to fallback bot ignored")]
    [TestCase(MessageSource.Main, null, false, false, true, Description = "DM to main bot kept")]
    [TestCase(MessageSource.Main, 1UL, false, true, true, Description = "fallback disabled: main serves")]
    public void RoutesMessagesToExactlyOneSource(MessageSource source, ulong? guildId, bool fallbackInGuild, bool mainInGuild, bool expected)
    {
        Assert.That(FallbackMessageSource.ShouldProcess(source, guildId, fallbackInGuild, mainInGuild), Is.EqualTo(expected));
    }

    // Regression: without Message Content the main client sees empty text for normal guild messages,
    // which must not trigger the "no text + 4 images" compromised-account rule.
    [TestCase(MessageSource.Fallback, false, false, true)]
    [TestCase(MessageSource.Main, false, false, false)]
    [TestCase(MessageSource.Main, true, false, true)]
    [TestCase(MessageSource.Main, false, true, true)]
    public void ContentOnlyTrustedWhenDelivered(MessageSource source, bool isDm, bool mentionsMainBot, bool expected)
    {
        Assert.That(FallbackMessageSource.IsContentAvailable(source, isDm, mentionsMainBot), Is.EqualTo(expected));
    }
}
