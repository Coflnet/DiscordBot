

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text.Json.Serialization;
using Coflnet.Sky.ModCommands.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Newtonsoft.Json;
using RestSharp;

[Group("vps", "commands for Vps")]
public class VpsCommands : InteractionModuleBase
{
    private readonly IVpsApi vpsApi;
    private readonly ILogger<VpsCommands> logger;
    private readonly Persistence persistence;
    private readonly IConfiguration configuration;

    public VpsCommands(IVpsApi vpsApi, ILogger<VpsCommands> logger, Persistence persistence, IConfiguration configuration)
    {
        this.vpsApi = vpsApi;
        this.logger = logger;
        this.persistence = persistence;
        this.configuration = configuration;
    }

    [SlashCommand("set", "Update a vps setting")]
    public async Task VpsCommand(string setting, string value)
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        await vpsApi.VpsUserInstanceIdSetPostAsync(userId, target, new(new()
        {
            Setting = setting,
            Value = value
        }));
        await FollowupAsync($"Set {setting} to {value}", ephemeral: true);
    }

    [SlashCommand("start", "Start vps")]
    public async Task VpsStart()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        await vpsApi.VpsUserInstanceIdTurnOnPostAsync(userId, target);
        await FollowupAsync("Starting instance", ephemeral: true);
    }

    [SlashCommand("stop", "Stop vps")]
    public async Task VpsStop()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        await vpsApi.VpsUserInstanceIdTurnOffPostAsync(userId, target);
        await FollowupAsync("Stopping instance", ephemeral: true);
    }

    [ComponentInteraction("stop-following", true)]
    public async Task StopFollowing()
    {
        await DeferAsync(ephemeral: true);
        Console.WriteLine("Aborting follow " + Context.Interaction.GetType().Name);
        var originalContext = Context.Interaction as SocketMessageComponent;
        await originalContext!.DeleteOriginalResponseAsync();
        await FollowupAsync("Stopped following", ephemeral: true);
    }

    [SlashCommand("log", "Retrieve log")]
    public async Task VpsLog(bool follow = false)
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var log = await GetVpsLog(target, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        var embed = new EmbedBuilder()
            .WithTitle("VPS Logs")
            .WithDescription(FormatLog(log))
            .WithColor(Color.Blue)
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);

        if (follow)
        {
            var iterations = 50;
            for (int i = 0; i < iterations; i++)
            {
                await Task.Delay(5000);
                var isLast = iterations - 1 == i;
                var logFollow = await GetVpsLog(target, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
                var logEmbed = new EmbedBuilder()
                    .WithTitle("VPS Logs" + (isLast ? " (stopped following)" : $" (following {DateTime.UtcNow:mm:ss})"))
                    .WithDescription(FormatLog(logFollow))
                    .WithColor(Color.Blue)
                    .Build();
                try
                {
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = logEmbed;
                        if (isLast)
                            m.Components = null;
                        else
                            m.Components = new ComponentBuilder()
                                .WithButton("Stop Following", "stop-following", ButtonStyle.Danger)
                                .Build();
                    });
                }
                catch (InteractionException)
                {
                    logger.LogInformation("Log follow was aborted");
                    break;
                }
            }
        }

        static string FormatLog(IEnumerable<string> logFollow)
        {
            return logFollow.Count() == 0 ? "No logs found" : "```js\n" + string.Join("\n", logFollow) + "\n```";
        }
    }

    private async Task<(string, Guid)> GetInstanceId()
    {
        await DeferAsync(ephemeral: true);
        var profile = await persistence.GetDiscordAccountInfo(Context.User.Id);
        if (profile == null)
        {
            await FollowupAsync("You don't seem to have verified a minecraft account, use `/update-mc-user`");
            return default;
        }
        var instances = await vpsApi.VpsInstancesGetAsync(new(profile.UserId));
        if (!instances.TryOk(out var instance))
        {
            await FollowupAsync("Failed to get instances");
            return default;
        }
        if (instance.Count == 0)
        {
            await FollowupAsync("You don't seem to have any instance running");
            return default;
        }
        return (profile.UserId, instance.First().Id!.Value);
    }

    internal async Task<IEnumerable<string>> GetVpsLog(Guid instance, DateTimeOffset from, DateTimeOffset to)
    {
        var query = $"{{container=\"tpm-manager\", instance_id=\"{instance}\"}}";
        var start = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        return await QueryLoki(query, start, end);
    }

    private async Task<IEnumerable<string>> QueryLoki(string query, long start, long end, int limit = 20)
    {
        var client = new RestClient(configuration["LOKI_BASE_URL"]);
        var request = new RestRequest("loki/api/v1/query_range", Method.Get);
        request.AddQueryParameter("query", query);
        request.AddQueryParameter("start", start);
        request.AddQueryParameter("end", end);
        request.AddQueryParameter("limit", limit);
        var response = await client.ExecuteAsync(request);
        logger.LogInformation($"Querying loki with {client.BuildUri(request)}");
        if (!response.IsSuccessful)
        {
            logger.LogError($"Failed to query loki: {response.Content}");
            await FollowupAsync($"Failed to get logs, sorry please let Äkwav know", ephemeral: true);
            return Enumerable.Empty<string>();
        }
        var root = JsonConvert.DeserializeObject<Root>(response.Content);
        return root.data.result.SelectMany(r => r.values).Select(v => v[1]).Reverse();
    }

    public class Root
    {
        [JsonPropertyName("status")]
        public string status { get; set; }

        [JsonPropertyName("data")]
        public Data data { get; set; }
    }

    public class Data
    {
        [JsonPropertyName("result")]
        public Result[] result { get; set; }
    }

    public class Result
    {
        [JsonPropertyName("stream")]
        public Stream stream { get; set; }

        [JsonPropertyName("values")]
        public string[][] values { get; set; }
    }

    public class Stream
    {
        [JsonPropertyName("container")]
        public string container { get; set; }

        [JsonPropertyName("instance_id")]
        public string instance_id { get; set; }
        [JsonPropertyName("user_id")]
        public string user_id { get; set; }
    }
}
