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

public class MainIntentsTests
{
    [Test]
    public void MainClientRequestsMessageContentButNotScheduledEvents()
    {
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.MessageContent), Is.True);
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.GuildMessages), Is.True);
        Assert.That(DiscordHandler.MainIntents.HasFlag(Discord.GatewayIntents.GuildScheduledEvents), Is.False);
    }
}
