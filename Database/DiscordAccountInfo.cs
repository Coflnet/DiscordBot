// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
public class DiscordAccountInfo
{
    public ulong DiscordId { get; set; }
    public string UserId { get; set; }
    public Guid MinecraftUuid { get; set; }
    public List<Guid> MinecraftUuids { get; set; }
    public string? MinecraftName { get; set; }
    public Dictionary<string, string> Attributes { get; set; }
    public AccountTier AccountTier { get; set; }
    public DateTime ExpiresAt { get; set; }
}
