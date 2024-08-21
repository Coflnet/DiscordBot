

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Numerics;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

public class Persistence
{
    private Cassandra.ISession session;
    Table<DiscordAccountInfo> discordAccountInfo;
    Table<DiscordAccountInfo> byMcUuid;

    public Persistence(Cassandra.ISession session)
    {
        this.session = session;

        var mapping = new MappingConfiguration().Define(
            new Map<DiscordAccountInfo>()
                .TableName("account_info")
                .PartitionKey(u => u.DiscordId)
                .Column(u => u.AccountTier, cm => cm.WithDbType<int>())
                .Column(u => u.DiscordId, cm => cm.WithDbType<BigInteger>())
        );
        var byUuidMapping = new MappingConfiguration().Define(
            new Map<DiscordAccountInfo>()
                .TableName("account_info_mc")
                .PartitionKey(u => u.MinecraftUuid)
                .Column(u => u.AccountTier, cm => cm.WithDbType<int>())
                .Column(u => u.DiscordId, cm => cm.WithDbType<BigInteger>())
        );
        var table = new Table<DiscordAccountInfo>(session, mapping);
        byMcUuid = new Table<DiscordAccountInfo>(session, byUuidMapping);
        table.CreateIfNotExists();
        byMcUuid.CreateIfNotExists();
        discordAccountInfo = table;
    }

    public async Task<DiscordAccountInfo> GetDiscordAccountInfo(ulong discordId)
    {
        BigInteger discordIdBigInt = new BigInteger(discordId);
        return await discordAccountInfo.Where(d => d.DiscordId == discordIdBigInt).First().ExecuteAsync();
    }
    public async Task<DiscordAccountInfo> GetDiscordAccountInfoByMcUuid(Guid mcUuid)
    {
        return await byMcUuid.Where(d => d.MinecraftUuid == mcUuid).First().ExecuteAsync();
    }

    public async Task SaveDiscordAccountInfo(DiscordAccountInfo info)
    {
        await discordAccountInfo.Insert(info).ExecuteAsync();
        await byMcUuid.Insert(info).ExecuteAsync();
    }
}
