

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Numerics;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

public class Persistence
{
    private Cassandra.ISession session;
    Table<DiscordAccountInfo> discordAccountInfo;

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
        var table = new Table<DiscordAccountInfo>(session, mapping);
        table.CreateIfNotExists();
        discordAccountInfo = table;
    }

    public async Task<DiscordAccountInfo> GetDiscordAccountInfo(ulong discordId)
    {
        BigInteger discordIdBigInt = new BigInteger(discordId);
        return await discordAccountInfo.Where(d => d.DiscordId == discordIdBigInt).First().ExecuteAsync();
    }

    public async Task SaveDiscordAccountInfo(DiscordAccountInfo info)
    {
        await discordAccountInfo.Insert(info).ExecuteAsync();
    }
}
