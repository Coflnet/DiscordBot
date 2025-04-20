

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text.Json.Serialization;
using Coflnet.Core;
using Coflnet.Sky.ModCommands.Client.Api;
using Coflnet.Sky.ModCommands.Client.Model;
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
    public async Task VpsCommand([Autocomplete] string setting, string? value = null)
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var result = await vpsApi.VpsUserInstanceIdSetPostAsync(userId, target, new(new()
        {
            Setting = setting,
            Value = value?.Replace("\\\\", "\\").Replace("\\n", "\n")
        }));
        if (!result.IsOk)
        {
            logger.LogInformation("Failed to set {setting} to {value} {response}", setting, value, result.RawContent);
            await PrintError(result);
            return;
        }
        if (value == null)
        {
            await FollowupAsync($"Toggled {setting}", ephemeral: true);
            return;
        }
        await FollowupAsync($"Set {setting} to {value}", ephemeral: true);
    }

    private async Task PrintError(Coflnet.Sky.ModCommands.Client.Client.IApiResponse result)
    {
        var deserialized = JsonConvert.DeserializeObject<ApiException>(result.RawContent);
        await FollowupAsync(deserialized.Message, ephemeral: true);
    }

    [AutocompleteCommand("setting", "set")]
    public async Task Autocomplete()
    {
        var interaction = (Context.Interaction as SocketAutocompleteInteraction) ?? throw new InvalidOperationException("Interaction is not an autocomplete interaction");
        string userInput = interaction.Data.Current.Value.ToString();
        var response = await vpsApi.VpsSettingsGetAsync();
        if (!response.TryOk(out var options))
        {
            await interaction.RespondAsync(new[] { new AutocompleteResult("Failed to get settings", "error") });
            return;
        }
        var transformed = options.Where(o => !o.Value.Hide!.Value).Select(o => new AutocompleteResult($"{o.Value.Prefix}{o.Value.RealName} - {FormatDescription(o)}", o.Key));
        if (string.IsNullOrEmpty(userInput))
        {
            await interaction.RespondAsync(transformed.Take(25));
            return;
        }

        await interaction.RespondAsync(transformed.Where(o => o.Name.Contains(userInput, StringComparison.OrdinalIgnoreCase)).Take(25));

        static string FormatDescription(KeyValuePair<string, Coflnet.Sky.ModCommands.Client.Model.SettingDoc> v)
        {
            var description = v.Value.Info == null ? "" : $"{v.Value.Info} -";
            var typeHint = v.Value.Type switch
            {
                "String[]" => "Separate options with commas",
                "Object[]" => "Separate options with commas",
                "Boolean" => "`true` or `false`",
                "Dictionary`2" => "Separate key and value with space, not adding a space will remove the key",
                "Int32" => "Number input",
                _ => "Text input"
            };
            return description + $" {typeHint}";
        }
    }

    [SlashCommand("info", "Get vps info")]
    public async Task VpsInfo()
    {
         _ =DeferAsync(ephemeral: true);
        (var user, var target) = await GetInstance();
        if (target == default)
            return;
        Embed? embed = await GetVpsInfoEmbed(1, user, target);
        if (embed == null)
        {
            return;
        }
        MessageComponent components = GetPageSwitch(1);
        await FollowupAsync(embed: embed, components: components, ephemeral: true);
    }

    private static MessageComponent GetPageSwitch(int page)
    {
        return new ComponentBuilder()
            .WithButton("Next page", "setting-page" + (page == 1 ? 2 : 1), ButtonStyle.Secondary)
            .Build();
    }

    private async Task<Embed?> GetVpsInfoEmbed(int page, DiscordAccountInfo user, Instance target)
    {
        var settingsTask = vpsApi.VpsSettingsGetAsync();
        var result = await vpsApi.VpsUserInstanceIdsettingsGetAsync(user.UserId, target.Id!.Value);
        var settingsResult = await settingsTask;
        if (!settingsResult.TryOk(out var settings))
        {
            await FollowupAsync("Failed to get settings", ephemeral: true);
            return null;
        }
        if (!result.TryOk(out var instance))
        {
            await FollowupAsync("Failed to get instance", ephemeral: true);
            return null;
        }
        var combined = instance.Select(i => (i, settings[i.Key])).ToList();
        var timestamp = new DateTimeOffset(target.PaidUntil!.Value).ToUnixTimeSeconds();
        var desc = $"Instance id: `{target.Id.ToString()?.TakeLast(3).Aggregate("", (s, c) => s + c)}`\n" +
                   $"Expires: <t:{timestamp}> (in <t:{timestamp}:R>)\n" +
                   $"Kind: `{target.AppKind}`\n"; Console.WriteLine(desc);
        return new EmbedBuilder()
            .WithTitle("VPS Info (page " + page + ")")
            .WithDescription(desc)
            .WithFields(combined.Select(i =>
            {
                var setting = i.Item2;
                var value = i.i.Value;
                return new EmbedFieldBuilder()
                    .WithName($"{setting.Prefix}{setting.RealName}")
                    .WithValue(string.IsNullOrWhiteSpace(value) ? "Not set" : value)
                    .WithIsInline(true);
            }).Skip(25 * (page - 1)).Take(25))
            .WithColor(Color.Blue)
            .Build();
    }

    [ComponentInteraction("setting-page*", true)]
    public async Task SettingsUpdate()
    {
        var originalContext = Context.Interaction as SocketMessageComponent;
        (var user, var instance) = await GetInstance();
        var page = int.Parse(originalContext!.Data.CustomId[12..]);
        var emded = await GetVpsInfoEmbed(page, user, instance);
        var component = GetPageSwitch(page);
        await originalContext!.UpdateAsync(a => { a.Embed = emded; a.Components = component; });
    }

    [SlashCommand("start", "Start vps")]
    public async Task VpsStart()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var answer = await vpsApi.VpsUserInstanceIdTurnOnPostAsync(userId, target);
        if(!answer.IsOk)
        {
            await PrintError(answer);
            return;
        }
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

    private async Task<(string, Guid)> GetInstanceId(bool defer = true)
    {
        if (defer)
            await DeferAsync(ephemeral: true);
        (var profile, var instance) = await GetInstance();
        if (profile == default)
            return default;
        return (profile.UserId, instance.Id!.Value);
    }

    private async Task<(DiscordAccountInfo, Instance)> GetInstance()
    {
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

        return (profile, instance.First());
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
        var request = new RestRequest("loki/api/v1/query_range", RestSharp.Method.Get);
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
