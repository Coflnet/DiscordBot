using Coflnet.Discord;
using Discord;
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

public class InviteScamTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [TestCase("➡ ASK here Please 0P£N-A-TlCk£T > http://discordapp.com/invite/complaint", 1.0, true)]
    [TestCase("go to https://discord.com/invite/complaint", 1.0, true)]
    [TestCase("HTTP://DISCORD.COM/INVITE/COMPLAINT", 1.0, true)]
    [TestCase("http://discordapp.com/invite/complaint", 2.0, false)]
    [TestCase("http://discordapp.com/invite/complaint", 3.0, false)]
    [TestCase("https://discord.com/invite/abcdef", 1.0, false)]
    [TestCase("https://discord.com/invite/complaints", 1.0, false)]
    [TestCase("https://discord.com/invite/complaint-x", 1.0, false)]
    [TestCase("", 1.0, false)]
    public void BansOnlyFreshAccountsPostingTheScamLink(string content, double joinedDaysAgo, bool expected)
    {
        var joinedAt = Now.AddDays(-joinedDaysAgo);
        Assert.That(DiscordHandler.ShouldBanInviteScam(content, joinedAt, Now), Is.EqualTo(expected));
    }

    [Test]
    public void UnknownJoinDateIsNeverBanned()
    {
        Assert.That(DiscordHandler.ShouldBanInviteScam("http://discordapp.com/invite/complaint", null, Now), Is.False);
    }
}

public class InviteScamDecisionTests
{
    private const ulong Owner = 1, User = 2, ExemptRole = 669258959495888907;
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Scam = "http://discordapp.com/invite/complaint";

    private static bool Protected(ulong user = User, ulong[]? roles = null, GuildPermissions? perms = null)
        => DiscordHandler.IsProtectedMember(Owner, user, roles ?? [], perms ?? GuildPermissions.None);

    [Test]
    public void PlainMemberIsNotProtected() => Assert.That(Protected(), Is.False);

    [Test]
    public void OwnerIsProtected() => Assert.That(Protected(user: Owner), Is.True);

    [Test]
    public void ExemptRoleIsProtected() => Assert.That(Protected(roles: [ExemptRole]), Is.True);

    [TestCase(nameof(GuildPermissions.Administrator))]
    [TestCase(nameof(GuildPermissions.ManageGuild))]
    [TestCase(nameof(GuildPermissions.ManageMessages))]
    [TestCase(nameof(GuildPermissions.BanMembers))]
    [TestCase(nameof(GuildPermissions.KickMembers))]
    [TestCase(nameof(GuildPermissions.ModerateMembers))]
    [TestCase(nameof(GuildPermissions.ManageRoles))]
    [TestCase(nameof(GuildPermissions.ManageChannels))]
    public void StaffPermissionIsProtected(string permission)
    {
        var perms = new GuildPermissions(administrator: permission == "Administrator",
            manageGuild: permission == "ManageGuild", manageMessages: permission == "ManageMessages",
            banMembers: permission == "BanMembers", kickMembers: permission == "KickMembers",
            moderateMembers: permission == "ModerateMembers", manageRoles: permission == "ManageRoles",
            manageChannels: permission == "ManageChannels");
        Assert.That(Protected(perms: perms), Is.True);
    }

    [Test]
    public void FreshRecentJoinDateBansEvenWhenCachedDateWasOld()
    {
        var staleCached = Now.AddDays(-400);
        var fresh = Now.AddHours(-3);
        Assert.That(DiscordHandler.ShouldBanInviteScam(Scam, staleCached, Now), Is.False);
        Assert.That(DiscordHandler.ShouldBanInviteScam(Scam, fresh, Now), Is.True);
    }

    [Test]
    public void MissingFreshMemberDoesNotBan() => Assert.That(DiscordHandler.ShouldBanInviteScam(Scam, null, Now), Is.False);

    [Test]
    public void OnlyCoflnetGuildQualifies()
    {
        Assert.That(DiscordHandler.IsInviteScamGuild(IssueEvidenceService.CoflnetGuildId), Is.True);
        Assert.That(DiscordHandler.IsInviteScamGuild(123), Is.False);
    }
}

public class InviteScamGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void BlocksRepeatWithinTtlAndAllowsAfter()
    {
        var guard = new InviteScamGuard();
        Assert.That(guard.TryEnter(1, 2, T0), Is.True);
        Assert.That(guard.TryEnter(1, 2, T0.AddMinutes(4)), Is.False);
        Assert.That(guard.TryEnter(1, 2, T0.AddMinutes(5)), Is.True);
    }

    [Test]
    public void ClearAllowsImmediateReevaluation()
    {
        var guard = new InviteScamGuard();
        guard.TryEnter(1, 2, T0);
        guard.Clear(1, 2);
        Assert.That(guard.TryEnter(1, 2, T0.AddSeconds(1)), Is.True);
    }

    [Test]
    public void UsersAndGuildsAreIndependent()
    {
        var guard = new InviteScamGuard();
        guard.TryEnter(1, 2, T0);
        Assert.That(guard.TryEnter(1, 3, T0), Is.True);
        Assert.That(guard.TryEnter(9, 2, T0), Is.True);
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
