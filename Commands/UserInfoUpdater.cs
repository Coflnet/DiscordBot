

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Sky.Api.Client.Model;
using Coflnet.Sky.McConnect.Api;

public class UserInfoUpdater
{

    Persistence persistence;
    Coflnet.Payments.Client.Api.IUserApi userApi;
    ProfileClient profileClient;
    IConnectApi connectApi;
    private ILogger<UserInfoUpdater> logger;
    private Dictionary<string, (Guid uuid, string)> ToAddLookup = new();

    public UserInfoUpdater(Persistence persistence, Coflnet.Payments.Client.Api.IUserApi userApi, ProfileClient profileClient, IConnectApi connectApi, ILogger<UserInfoUpdater> logger)
    {
        this.persistence = persistence;
        this.userApi = userApi;
        this.profileClient = profileClient;
        this.connectApi = connectApi;
        this.logger = logger;
    }
    public async Task<string> UpdateuserDetails(ulong discordId, PlayerResult user, DiscordAccountInfo existing)
    {
        existing.DiscordId = discordId;
        existing.MinecraftUuid = Guid.Parse(user.Uuid);
        var ignName = user.Name;
        existing.MinecraftName = ignName;
        var connect = await connectApi.ConnectMinecraftMcUuidGetAsync(user.Uuid);
        existing.UserId = connect.ExternalId;
        await UpdatePremiumTierAndSave(existing);
        return ignName;
    }

    public async Task UpdatePremiumTierAndSave(DiscordAccountInfo existing)
    {
        var owning = await userApi.UserUserIdOwnsUntilPostAsync(existing.UserId, ["premium", "premium-plus"]);
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
    }

    internal async Task UpdateUserDetails(Discord.WebSocket.DiscordSocketClient client, string uuid, string name)
    {
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
            await UpdateuserDetails(id.Id, new PlayerResult { Name = item.Value.Item2, Uuid = item.Value.uuid.ToString("n") }, existing);
        }
    }
}
