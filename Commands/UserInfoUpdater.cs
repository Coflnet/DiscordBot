

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Sky.Api.Client.Model;
using Coflnet.Sky.McConnect.Api;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System.Linq;

public class MinecraftAccountNotVerifiedException : Exception
{
    public string MinecraftName { get; }

    public MinecraftAccountNotVerifiedException(string minecraftName)
        : base($"Minecraft account {minecraftName} is not verified with a Coflnet account")
    {
        MinecraftName = minecraftName;
    }
}

public class UserInfoUpdater
{

    Persistence persistence;
    Coflnet.Payments.Client.Api.IUserApi userApi;
    ProfileClient profileClient;
    IConnectApi connectApi;
    private ILogger<UserInfoUpdater> logger;
    private readonly ulong guildId;
    private const ulong FlipperRoleId = 863804979529252864;
    private DiscordSocketClient? discordClient;
    private Dictionary<string, (Guid uuid, string)> ToAddLookup = new();

    public UserInfoUpdater(Persistence persistence, Coflnet.Payments.Client.Api.IUserApi userApi, ProfileClient profileClient, IConnectApi connectApi, ILogger<UserInfoUpdater> logger, IConfiguration configuration)
    {
        this.persistence = persistence;
        this.userApi = userApi;
        this.profileClient = profileClient;
        this.connectApi = connectApi;
        this.logger = logger;
        if (!ulong.TryParse(configuration["GUILD_ID"], out guildId))
        {
            guildId = 0;
            this.logger.LogWarning("GUILD_ID missing or invalid, flipper role assignment disabled");
        }
    }
    public async Task<string> UpdateuserDetails(ulong discordId, PlayerResult user, DiscordAccountInfo existing)
    {
        existing.DiscordId = discordId;
        existing.MinecraftUuids ??= new List<Guid>();
        existing.MinecraftUuid = Guid.Parse(user.Uuid);
        if (!existing.MinecraftUuids.Contains(existing.MinecraftUuid))
            existing.MinecraftUuids.Add(existing.MinecraftUuid);
        var ignName = user.Name;
        existing.MinecraftName = ignName;
        var connect = await connectApi.ConnectMinecraftMcUuidGetAsync(user.Uuid);
        if (connect == null || string.IsNullOrEmpty(connect.ExternalId))
            throw new MinecraftAccountNotVerifiedException(ignName);
        existing.UserId = connect.ExternalId;
        await UpdatePremiumTierAndSave(existing);
        return ignName;
    }

    public async Task UpdatePremiumTierAndSave(DiscordAccountInfo existing)
    {
        var owning = await userApi.UserUserIdOwnsUntilPostAsync(existing.UserId, requestBody: ["premium", "premium-plus"]);
        if (owning.TryGetValue("premium-plus", out var premPlus) && premPlus > DateTime.UtcNow)
        {
            existing.AccountTier = AccountTier.PREMIUM_PLUS;
            existing.ExpiresAt = premPlus;
        }
        else if (owning.TryGetValue("premium", out var prem) && prem > DateTime.UtcNow)
        {
            existing.AccountTier = AccountTier.PREMIUM;
            existing.ExpiresAt = prem;
        }
        else
        {
            existing.AccountTier = AccountTier.NONE;
            existing.ExpiresAt = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        }
        await persistence.SaveDiscordAccountInfo(existing);
        if (existing.ExpiresAt > DateTime.UtcNow + TimeSpan.FromDays(3))
            await EnsureFlipperRole(existing.DiscordId, existing.AccountTier);
    }

    internal async Task UpdateUserDetails(Discord.WebSocket.DiscordSocketClient client, string uuid, string name)
    {
        SetDiscordClient(client);
        var profile = await profileClient.GetLookup(uuid);
        var userName = profile.SocialMedia.Links.Where(l => l.Key == "discord").FirstOrDefault().Value;
        ToAddLookup.Add(userName, (Guid.Parse(uuid), name));
        foreach (var item in ToAddLookup)
        {
            var id = client.GetUser(item.Key);
            if (id == null)
                continue;
            var existing = await persistence.GetDiscordAccountInfo(id.Id);
            if (existing == null)
            {
                existing = new DiscordAccountInfo();
            }
            logger.LogInformation("Updating user details of {uuid} {discordId}", item.Value.Item2, id);
            try
            {
                await UpdateuserDetails(id.Id, new PlayerResult { Name = item.Value.Item2, Uuid = item.Value.uuid.ToString("n") }, existing);
            }
            catch (MinecraftAccountNotVerifiedException e)
            {
                logger.LogInformation("Skipping {name}, minecraft account is not verified with Coflnet", e.MinecraftName);
            }
        }
    }

    internal void SetDiscordClient(DiscordSocketClient client)
    {
        discordClient = client;
    }

    private async Task EnsureFlipperRole(ulong discordUserId, AccountTier accountTier)
    {
        if (discordUserId == 0)
            return;
        if (accountTier < AccountTier.PREMIUM)
            return;
        if (guildId == 0)
        {
            logger.LogDebug("Skipping flipper role assignment because guild id is not configured");
            return;
        }
        if (discordClient == null)
        {
            logger.LogDebug("Discord client unavailable, cannot assign flipper role to {discordUserId}", discordUserId);
            return;
        }
        var guild = discordClient.GetGuild(guildId);
        if (guild == null)
        {
            logger.LogWarning("Guild {guildId} not found when assigning flipper role", guildId);
            return;
        }
        var role = guild.GetRole(FlipperRoleId);
        if (role == null)
        {
            logger.LogWarning("Flipper role {roleId} not found in guild {guildId}", FlipperRoleId, guildId);
            return;
        }
        var member = guild.GetUser(discordUserId);
        if (member == null)
        {
            logger.LogDebug("Member {discordUserId} not found in guild {guildId} while assigning flipper role", discordUserId, guildId);
            return;
        }
        if (member.Roles.Any(r => r.Id == FlipperRoleId))
            return;
        logger.LogInformation("Assigning flipper role to user {discordUserId}", discordUserId);
        try
        {
            await member.AddRoleAsync(role);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to assign flipper role to user {discordUserId}", discordUserId);
        }
    }
}
