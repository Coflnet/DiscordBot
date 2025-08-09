using System.Numerics;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

public class Persistence
{
    private Cassandra.ISession session;
    Table<DiscordAccountInfo> discordAccountInfo;
    Table<DiscordAccountInfo> byMcUuid;
    Table<DiscordMessage> messages;

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
        var messageMapping = new MappingConfiguration().Define(
            new Map<DiscordMessage>()
                .TableName("messages")
                .PartitionKey(m => m.ChannelId, m => m.Month)
                .ClusteringKey(m => m.MessageId)
                .Column(m => m.MessageId, cm => cm.WithDbType<BigInteger>())
                .Column(m => m.AuthorId, cm => cm.WithDbType<BigInteger>())
                .Column(m => m.ChannelId, cm => cm.WithDbType<BigInteger>())
                .Column(m=>m.Attachments, cm => cm.WithDbType<Dictionary<long, string>>())
        );
        var table = new Table<DiscordAccountInfo>(session, mapping);
        byMcUuid = new Table<DiscordAccountInfo>(session, byUuidMapping);
        messages = new Table<DiscordMessage>(session, messageMapping, "discord_messages");

        // Create tables if they do not exist
        messages.CreateIfNotExists();
        table.CreateIfNotExists();
        byMcUuid.CreateIfNotExists();
        discordAccountInfo = table;
    }

    public async Task<DiscordAccountInfo?> GetDiscordAccountInfo(ulong discordId)
    {
        BigInteger discordIdBigInt = new BigInteger(discordId);
        return await discordAccountInfo.Where(d => d.DiscordId == discordIdBigInt).FirstOrDefault().ExecuteAsync();
    }
    public async Task<DiscordAccountInfo> GetDiscordAccountInfoByMcUuid(Guid mcUuid)
    {
        return await byMcUuid.Where(d => d.MinecraftUuid == mcUuid).First().ExecuteAsync();
    }

    public async Task SaveDiscordAccountInfo(DiscordAccountInfo info)
    {
        await discordAccountInfo.Insert(info).ExecuteAsync();
        if (info.MinecraftUuid != Guid.Empty)
            await byMcUuid.Insert(info).ExecuteAsync();
    }

    public async Task<IEnumerable<DiscordMessage>> GetDiscordMessages(BigInteger channelId, DateTime time)
    {
        int month = GetMonthofDate(time);
        var messages = await this.messages.Where(m => m.ChannelId == channelId && m.Month == month).ExecuteAsync();
        return messages;
    }

    public async Task SaveDiscordMessage(DiscordMessage message)
    {
        var date = message.CreatedAt;
        message.Month = GetMonthofDate(date); // Calculate month as Year * 12 + Month
        await messages.Insert(message).ExecuteAsync();
    }

    private static int GetMonthofDate(DateTimeOffset date)
    {
        return date.Month + (date.Year - 2020) * 12;
    }
}
