

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        logger.LogInformation(result.RawContent);
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
        _ = DeferAsync(ephemeral: true);
        (var user, var target) = await GetInstance();
        if (target == default)
            return;
        Embed? embed = await GetVpsInfoEmbed(1, user, target);
        if (embed == null)
        {
            return;
        }
        MessageComponent components = GetPageSwitch(1, target.PaidUntil - TimeSpan.FromDays(20) > DateTime.UtcNow);
        await FollowupAsync(embed: embed, components: components, ephemeral: true);
    }

    private static MessageComponent GetPageSwitch(int page, bool hideExtend = true)
    {
        var builder = new ComponentBuilder()
            .WithButton("Next page", "setting-page" + (page == 1 ? 2 : 1), ButtonStyle.Secondary);
        if (!hideExtend)
            builder = builder.WithButton("Renew (Costs CoflCoins)", "renew-vps", ButtonStyle.Primary);
        return builder.Build();
    }

    private async Task<Embed?> GetVpsInfoEmbed(int page, DiscordAccountInfo user, Instance target)
    {
        var settingsTask = vpsApi.VpsSettingsGetAsync();
        var result = await vpsApi.VpsUserInstanceIdSettingsGetAsync(user.UserId, target.Id!.Value);
        var settingsResult = await settingsTask;
        if (!settingsResult.TryOk(out var settings))
        {
            await FollowupAsync("Failed to get settings", ephemeral: true);
            return null;
        }
        if (!result.TryOk(out var instance))
        {
            logger.LogInformation("Failed to get instances {response}", result.RawContent);
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

    [ComponentInteraction("renew-vps", true)]
    public async Task RenewVps()
    {
        var originalContext = Context.Interaction as SocketMessageComponent;
        await originalContext!.UpdateAsync(a => { a.Embed = new EmbedBuilder().WithTitle("Renewing VPS").WithDescription("Please check your DMs").Build(); });
        var (user, instance) = await GetInstance();
        if (instance == default)
            return;
        var result = await vpsApi.VpsUserInstanceIdExtendPostAsync(user.UserId, instance.Id!.Value);
        if (!result.IsOk)
        {
            await PrintError(result);
            return;
        }
        await FollowupAsync("Renewed VPS", ephemeral: true);
    }

    [SlashCommand("start", "Start vps")]
    public async Task VpsStart()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var answer = await vpsApi.VpsUserInstanceIdTurnOnPostAsync(userId, target);
        if (!answer.IsOk)
        {
            await PrintError(answer);
            return;
        }
        await FollowupAsync("Starting instance", ephemeral: true);
    }

    [SlashCommand("import", "Import json vps settings, eg from TPM")]
    public async Task VpsImport(IAttachment settingsFile)
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var url = settingsFile.Url;
        var client = new RestClient(url);
        var request = new RestRequest("", Method.Get);
        var response = await client.ExecuteAsync(request);
        if (!response.IsSuccessful || response.Content == null)
        {
            await FollowupAsync("Failed to get settings file", ephemeral: true);
            return;
        }
        var content = response.Content;
        var result = await vpsApi.VpsUserInstanceIdImportPostAsync(userId, target, content);
        if (!result.IsOk)
        {
            await PrintError(result);
            return;
        }
        await FollowupAsync("Imported settings, take a look with /vps info", ephemeral: true);
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

    [SlashCommand("log-file", "Get logfile of vps")]
    public async Task GetLogFile()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (Dns.GetHostName().Contains("ekwav"))
            target = Guid.Parse("595e06b0-03bb-4add-a3f0-87575e716a42");
        if (target == default)
        {
            await FollowupAsync("You don't seem to have a vps yet", ephemeral: true);
            return;
        }
        var startTime = DateTimeOffset.UtcNow;
        var fullLog = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            var batch = (await GetVpsLog(target, startTime.AddHours(-i), startTime.AddHours(-i + 1), 5000)).ToList();
            if (batch.Count() == 0)
                continue;
            fullLog.InsertRange(0, batch);
            fullLog.Insert(0, "Log export time: " + startTime.AddHours(-i).ToString("yyyy-MM-dd HH:mm:ss"));
            if (batch.Any(b => b.Contains("Trying to log into"))) // logged on start
                break;
        }
        // Create a Discord file attachment with log contents
        var fileName = $"vps-log-{target}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm}.txt";
        var content = string.Join("\n", fullLog);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var attachment = new FileAttachment(stream, fileName);
        await FollowupWithFileAsync(attachment, "VPS logs exported", ephemeral: true);
        return;
    }

    [SlashCommand("log", "Retrieve log")]
    public async Task VpsLog()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (Dns.GetHostName().Contains("ekwav"))
            target = Guid.Parse("b702b3a5-fe82-4cb8-adb2-83bcc76919d9");
        if (target == default)
        {
            await FollowupAsync("You don't seem to have a vps yet", ephemeral: true);
            return;
        }
        var startTime = DateTimeOffset.UtcNow;

        var nanoSeconds = (startTime - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds() * 1_000_000;
        var url = configuration["LOKI_BASE_URL"].Replace("http:", "ws:") + "/loki/api/v1/tail";
        var query = $"{{container=\"tpm-manager\", instance_id=\"{target}\"}}";
        // Follow logs using WebSocket
        try
        {
            var ws = new ClientWebSocket();
            var cancellationToken = new CancellationTokenSource(TimeSpan.FromMinutes(14));

            var fullUrl = $"{url}?query={Uri.EscapeDataString(query)}&start={nanoSeconds}&limit=20";
            await ws.ConnectAsync(new Uri(fullUrl), cancellationToken.Token);

            // Button to stop following logs
            await ModifyOriginalResponseAsync(m =>
            {
                m.Components = new ComponentBuilder()
                    .WithButton("Stop Following", "stop-following", ButtonStyle.Danger)
                    .Build();
            });

            // Receive logs in a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("Connected to WebSocket: {url}", fullUrl);
                    await HandlePaket(ws, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Unknown Message"))
                        return; // following got canceled and its more efficient to ignore the error for updating that to check for each update
                    logger.LogError(ex, "Error in WebSocket stream");
                }
                finally
                {
                    if (ws.State != WebSocketState.Closed)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                }
                if (cancellationToken.IsCancellationRequested)
                    await ModifyOriginalResponseAsync(m => { m.Embed = new EmbedBuilder().WithTitle("Discord message can no longer be updated, please run command again").Build(); });
                else
                {
                    logger.LogInformation("WebSocket connection closed without cancel");
                    await ModifyOriginalResponseAsync(m => { m.Embed = new EmbedBuilder().WithTitle("Internal connection closed, you could use /vps log-file as alternative").Build(); });
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to WebSocket");
            await FollowupAsync("Failed to connect to WebSocket for log following. Falling back to polling.", ephemeral: true);
        }

        async Task HandlePaket(ClientWebSocket ws, CancellationTokenSource cancellationToken)
        {
            var buffer = new byte[4096 * 16];
            Queue<(long, string)> logReceived = new();
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by client", CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                var message = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                logger.LogInformation("Received message: {message}", message);
                var logEntry = JsonConvert.DeserializeObject<LogStreamResponse>(message);

                if (logEntry?.streams?.FirstOrDefault()?.values?.Any() == true)
                {
                    var logContent = logEntry.streams.SelectMany(s => s.values.Select(v => (long.Parse(v[0]), v[1]))).OrderBy(v => v.Item1).ToList();

                    foreach (var item in logContent)
                    {
                        logReceived.Enqueue(item);
                        if (logReceived.Count > 20)
                            logReceived.Dequeue();
                    }
                    var logEmbed = new EmbedBuilder()
                        .WithTitle($"VPS Logs (last received <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>)")
                        .WithDescription(FormatLog(logReceived.OrderBy(v => v.Item1).Select(v => v.Item2)))
                        .WithColor(Color.Blue)
                        .Build();

                    await ModifyOriginalResponseAsync(m => { m.Embed = logEmbed; });
                }
            }
            logger.LogInformation("WebSocket connection closed");
        }

        static string FormatLog(IEnumerable<string> logFollow)
        {
            if (logFollow.Count() == 0)
                return "No recent logs found, is the server on?";
            var primary = "```js\n" + string.Join("\n", logFollow) + "\n```";
            var linkMatch = Regex.Match(primary, @"https?://[^\s]+");
            if (linkMatch.Success)
            {
                primary += "\nFound link: " + linkMatch.Value;
            }
            return primary;
        }
    }

    // Add class to deserialize WebSocket responses
    public class LogStreamResponse
    {
        public List<LogStream> streams { get; set; } = new();
    }

    public class LogStream
    {
        public Dictionary<string, string> stream { get; set; } = new();
        public List<string[]> values { get; set; } = new();
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
            logger.LogInformation("Failed to get instances {response}", instances.RawContent);
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

    internal async Task<IEnumerable<string>> GetVpsLog(Guid instance, DateTimeOffset from, DateTimeOffset to, int limit = 20)
    {
        var query = $"{{container=\"tpm-manager\", instance_id=\"{instance}\"}}";
        var start = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        return await QueryLoki(query, start, end, limit);
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
