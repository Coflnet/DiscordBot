

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Coflnet.Core;
using Coflnet.Payments.Client.Api;
using Coflnet.Sky.ModCommands.Client.Api;
using Coflnet.Sky.ModCommands.Client.Model;
using Coflnet.Sky.Settings.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Newtonsoft.Json;
using RestSharp;

[Group("vps", "commands for Vps")]
public partial class VpsCommands : InteractionModuleBase
{
    private readonly ILogger<VpsCommands> logger;
    private readonly IConfiguration configuration;
    private readonly IVpsApi vpsApi;
    private readonly Persistence persistence;
    private readonly ISettingsApi settingsApi;
    private readonly LokiQuery lokiQuery;
    private readonly ITopUpApi topUpApi;

    public VpsCommands(IVpsApi vpsApi, ILogger<VpsCommands> logger, Persistence persistence, IConfiguration configuration, LokiQuery lokiQuery, ISettingsApi settingsApi, ITopUpApi topUpApi)
    {
        this.vpsApi = vpsApi;
        this.logger = logger;
        this.persistence = persistence;
        this.configuration = configuration;
        this.lokiQuery = lokiQuery;
        this.settingsApi = settingsApi;
        this.topUpApi = topUpApi;
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
        var transformed = options.Where(o => !o.Value.Hide!.Value).Select(o => new AutocompleteResult($"{o.Value.Prefix}{o.Value.RealName} - {FormatDescription(o).Truncate(50)}", o.Key));
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

    [SlashCommand("help", "Get help about vps commands or a specific setting")]
    public async Task VpsHelp([Autocomplete] string? setting = null)
    {
        _ = DeferAsync(ephemeral: true);

        if (string.IsNullOrEmpty(setting))
        {
            // No setting specified - show general help
            var helpEmbed = new EmbedBuilder()
                .WithTitle("VPS Commands Help")
                .WithDescription("Here's how to use the VPS commands:")
                .WithColor(Color.Blue)
                .AddField("/vps start", "Start your VPS instance. You'll be guided through the login process if needed.", inline: false)
                .AddField("/vps stop", "Stop your VPS instance.", inline: false)
                .AddField("/vps info", "View your current VPS settings and instance information.", inline: false)
                .AddField("/vps set <setting> [value]", "Change a VPS setting. Use autocomplete to see available settings. If no value is provided, boolean settings will be toggled.", inline: false)
                .AddField("/vps help [setting]", "Show this help message, or get detailed info about a specific setting.", inline: false)
                .AddField("/vps log", "View recent logs from your VPS.", inline: false)
                .AddField("/vps log-file", "Download a full log file from your VPS.", inline: false)
                .AddField("/vps reset", "Reset your VPS settings to default. Can optionally reset login info or switch instance type.", inline: false)
                .AddField("/vps import", "Import VPS settings from a JSON file (e.g., exported from TPM).", inline: false)
                .AddField("/vps export", "Export your VPS settings as a JSON file.", inline: false)
                .AddField("💡 Tip", "Use `/vps help <setting>` to get detailed information about a specific setting, including its type and valid values.", inline: false)
                .Build();

            await FollowupAsync(embed: helpEmbed, ephemeral: true);
            return;
        }

        var settingsResponse = await vpsApi.VpsSettingsGetAsync();
        if (!settingsResponse.TryOk(out var settings))
        {
            await FollowupAsync("Failed to get settings information", ephemeral: true);
            return;
        }

        if (!settings.TryGetValue(setting, out var settingDoc))
        {
            KeyValuePair<string, SettingDoc> match = FindPartialMatch(setting, settings);
            if (match.Value == null)
            {
                await FollowupAsync($"Setting `{setting}` not found. Use `/vps help` without a parameter to see available commands, or check the autocomplete for valid settings.", ephemeral: true);
                return;
            }
            setting = match.Key;
            settingDoc = match.Value;
        }

        var typeDescription = settingDoc.Type switch
        {
            "String[]" => "**Array of strings** - Separate multiple values with commas\nExample: `/vps set {0} value1,value2,value3`",
            "Object[]" => "**Array of objects** - Separate multiple values with commas\nExample: `/vps set {0} value1,value2`",
            "Boolean" => "**Boolean** - Use `true` or `false`\nExample: `/vps set {0} true`\nOr omit the value to toggle: `/vps set {0}`",
            "Dictionary`2" => "**Key-Value pairs** - Separate key and value with space\nTo add/update: `/vps set {0} key value`\nTo remove a key: `/vps set {0} key` (no value)",
            "Int32" or "Int64" => "**Number** - Enter a whole number\nExample: `/vps set {0} 42`",
            "Double" => "**Decimal number** - Enter a number\nExample: `/vps set {0} 3.14`",
            _ => "**Text** - Enter any text value\nExample: `/vps set {0} your_value`"
        };

        var settingEmbed = new EmbedBuilder()
            .WithTitle($"{settingDoc.Prefix}{settingDoc.RealName}")
            .WithColor(Color.Green);

        if (!string.IsNullOrWhiteSpace(settingDoc.Info))
        {
            settingEmbed.WithDescription(settingDoc.Info);
        }

        settingEmbed.AddField("Setting Key", $"`{setting}`", inline: true);
        settingEmbed.AddField("Type", settingDoc.Type ?? "Text", inline: true);
        settingEmbed.AddField("Usage", string.Format(typeDescription, setting), inline: false);

        await FollowupAsync(embed: settingEmbed.Build(), ephemeral: true);

        static KeyValuePair<string, SettingDoc> FindPartialMatch(string setting, Dictionary<string, SettingDoc> settings)
        {
            // Try to find a partial match
            return settings.FirstOrDefault(s => s.Key.Equals(setting, StringComparison.OrdinalIgnoreCase) ||
                                                      s.Value.RealName?.Equals(setting, StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [AutocompleteCommand("setting", "help")]
    public async Task HelpAutocomplete()
    {
        var interaction = (Context.Interaction as SocketAutocompleteInteraction) ?? throw new InvalidOperationException("Interaction is not an autocomplete interaction");
        string userInput = interaction.Data.Current.Value.ToString() ?? "";
        var response = await vpsApi.VpsSettingsGetAsync();
        if (!response.TryOk(out var options))
        {
            await interaction.RespondAsync(new[] { new AutocompleteResult("Failed to get settings", "error") });
            return;
        }
        var transformed = options.Where(o => !o.Value.Hide!.Value).Select(o => new AutocompleteResult($"{o.Value.Prefix}{o.Value.RealName}", o.Key));
        if (string.IsNullOrEmpty(userInput))
        {
            await interaction.RespondAsync(transformed.Take(25));
            return;
        }

        await interaction.RespondAsync(transformed.Where(o => o.Name.Contains(userInput, StringComparison.OrdinalIgnoreCase)).Take(25));
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
        if(string.IsNullOrEmpty(instance.GetValueOrDefault("discordID")))
        {
            await vpsApi.VpsUserInstanceIdSetPostAsync(user.UserId, target.Id!.Value, new(new()
            {
                Setting = "discordId",
                Value = user.DiscordId.ToString()
            }));
        }
        var combined = instance.Select(i => (i, settings[i.Key])).ToList();
        var timestamp = new DateTimeOffset(target.PaidUntil!.Value).ToUnixTimeSeconds();
        var desc = $"Instance id: `{target.Id.ToString()?.TakeLast(3).Aggregate("", (s, c) => s + c)}`\n" +
                   $"Expires: <t:{timestamp}> (in <t:{timestamp}:R>)\n" +
                   $"Kind: `{target.AppKind}`\n"; Console.WriteLine(desc);
        if (target.PublicIp != null)
            desc += $"Public IP (proxy): ||`{target.PublicIp.Split(':').First()}`||\n";
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
        var (user, instance) = await GetInstance();
        var confirmButton = new ComponentBuilder()
            .WithButton("Confirm Renewal", "confirm-renew-vps", ButtonStyle.Success)
            .WithButton("Cancel", "cancel-renew-vps", ButtonStyle.Danger)
            .Build();
        await originalContext!.UpdateAsync(a =>
        {
            var price = instance.AppKind switch
            {
                "tpm+" => 5100,
                _ => 2700
            };
            a.Embed = new EmbedBuilder()
                .WithTitle("Renew VPS")
                .WithDescription($"Are you sure you want to renew the VPS{(string.IsNullOrWhiteSpace(instance.AppKind) ? "" : $" running `{instance.AppKind}`")}? This will cost `{price:N0}` CoflCoins.")
                .WithColor(Color.Blue)
                .Build();
            a.Components = confirmButton;
        });

    }
    [ComponentInteraction("confirm-renew-vps", true)]
    public async Task ConfirmRenewVps()
    {
        var originalContext = Context.Interaction as SocketMessageComponent;
        await originalContext!.UpdateAsync(a => { a.Embed = new EmbedBuilder().WithTitle("Trying to renew/extend VPS").Build(); });
        var (user, instance) = await GetInstance();
        if (instance == default)
            return;
        var result = await vpsApi.VpsUserInstanceIdExtendPostAsync(user.UserId, instance.Id!.Value);
        if (!result.TryOk(out var content))
        {
            await PrintError(result);
            return;
        }
        var time = content.PaidUntil!.Value;
        var timestamp = new DateTimeOffset(time).ToUnixTimeSeconds();
        await originalContext.UpdateAsync(a =>
        {
            a.Embed = new EmbedBuilder()
                .WithTitle("Renewed VPS")
                .WithDescription($"VPS is now paid for until <t:{timestamp}> (in <t:{timestamp}:R>)")
                .WithColor(Color.Green)
                .Build();
            a.Components = new ComponentBuilder()
                .WithButton("Show info page", "setting-page1", ButtonStyle.Secondary)
                .Build();
        });
    }
    [ComponentInteraction("set-igns", true)]
    public async Task SetIgns()
    {
        var originalContext = Context.Interaction as SocketMessageComponent;
        await originalContext.RespondWithModalAsync<SetIgnsModal>("set-igns-modal");
    }

    [ModalInteraction("set-igns-modal", true)]
    public async Task SetIgnsModalOpen(SetIgnsModal modal)
    {
        await DeferAsync(ephemeral: true);
        (string userId, Guid target) = await GetInstanceId(false);
        if (target == default)
            return;
        var igns = modal.IGNs;
        var result = await vpsApi.VpsUserInstanceIdSetPostAsync(userId, target, new(new()
        {
            Setting = "igns",
            Value = igns
        }));
        if (!result.IsOk)
        {
            logger.LogInformation("Failed to set igns to {value} {response}", igns, result.RawContent);
            await PrintError(result);
            return;
        }
        await FollowupAsync($"Set IGNs to {igns}", ephemeral: true);
    }


    public class SetIgnsModal : IModal
    {
        [ModalTextInput("igns", TextInputStyle.Short, "Enter your IGNs, separated by commas")]
        public string IGNs { get; set; }
        public string Title => "Input your ign";
    }

    [ComponentInteraction("cancel-renew-vps", true)]
    public async Task CancelRenewVps()
    {
        var originalContext = Context.Interaction as SocketMessageComponent;
        var component = new ComponentBuilder()
            .WithButton("Extend/Renew", "renew-vps", ButtonStyle.Primary)
            .Build();
        await originalContext!.UpdateAsync(a =>
        {
            a.Embed = new EmbedBuilder().WithTitle("Canceled renewal").Build();
            a.Components = component;
        });
    }

    [SlashCommand("reset", "Resets VPS settings to default, optionally preserving specific data.")]
    public async Task VpsReset(
        [Summary("reset-login", "Reset the minecraft login info (e.g., Minecraft login, IGNs)")]
        bool resetLogin = false,
        [Summary("reset-config", "Reset the vps settings (e.g., webhook format)")]
        bool resetConfig = true,
        [Summary("instance-type", "Switch the type of instance you have, will try to migrate settings")]
        [Choice("TPM (normal)", "tpm")]
        [Choice("TPM+", "tpm+")]
        string? instanceType = null)
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
        {
            // User has no VPS
            await FollowupAsync("You do not have a VPS instance to reset.", ephemeral: true);
            return;
        }
        await vpsApi.VpsUserInstanceIdTurnOffPostAsync(userId, target);
        if (instanceType != null)
        {
            var instanceData = await vpsApi.VpsInstancesGetAsync(userId);
            if (!instanceData.TryOk(out var instances))
            {
                logger.LogError("Failed to get instances for user {UserId}. Response: {RawContent}", userId, instanceData.RawContent);
                await FollowupAsync("Failed to get instances", ephemeral: true);
                return;
            }
            var instance = instances.FirstOrDefault(i => i.Id == target);
            var resetResult = await vpsApi.VpsUserInstanceIdResetPostAsync(userId, target, new VpsCreateRequest()
            {
                AppKind = instanceType
            });
            if (!resetResult.IsOk)
            {
                await PrintError(resetResult);
                logger.LogError("Failed to reset instance {InstanceId} for user {UserId}. Response: {RawContent}", target, userId, resetResult.RawContent);
                return;
            }
            resetLogin = true; // reset login details when changing instance type
        }

        logger.LogInformation("Attempting to reset VPS instance {InstanceId} for user {UserId}. PreserveGameState: {PreserveGameState}, PreserveConfig: {PreserveConfig}", target, userId, resetLogin, resetConfig);
        if (resetLogin)
        {
            await ResetUserLogin(userId);
        }
        if (resetConfig)
        {
            var settingsResponse = await vpsApi.VpsUserInstanceIdSettingsGetAsync(userId, target);
            if (!settingsResponse.TryOk(out var settings))
            {
                logger.LogError("Failed to get settings for instance {InstanceId}. Response: {RawContent}", target, settingsResponse.RawContent);
                settings = [];
            }
            await settingsApi.SettingsUpdateSettingAsync(userId, "tpm_config", JsonConvert.SerializeObject(null));
            await vpsApi.VpsUserInstanceIdSetPostAsync(userId, target, new(new()
            {
                Setting = "webhooks",
                Value = settings.GetValueOrDefault("webhooks") ?? ""
            }));
            var igns = settings.GetValueOrDefault("igns") ?? "";
            if (igns != null && !igns.Contains(":"))
                await vpsApi.VpsUserInstanceIdSetPostAsync(userId, target, new(new()
                {
                    Setting = "igns",
                    Value = igns
                }));
            var message = "Reset general config, copied over ign and webhooks";
            if (!resetLogin)
                message += ", login details (selected account) preserved";
            await FollowupAsync(message, ephemeral: true);
        }
        await vpsApi.VpsUserInstanceIdTurnOnPostAsync(userId, target);
    }

    private async Task ResetUserLogin(string userId)
    {
        await UpdateSetting<string?>(userId, "tpm_extra_config", null);
        await FollowupAsync("Reset login details", ephemeral: true);
    }

    [SlashCommand("start", "Start vps")]
    public async Task VpsStart()
    {
            await DeferAsync(ephemeral: true);
        (var profile, var instance) = await GetInstance();
        var userId = profile.UserId;
        var target = instance.Id!.Value;
        if (target == default)
            return;
        if(instance.PaidUntil < DateTime.UtcNow)
        {
            var message = $"Your VPS instance has expired at <t:{new DateTimeOffset(instance.PaidUntil?? DateTime.UtcNow).ToUnixTimeSeconds()}>. Please extend to start the instance.";
            var component = new ComponentBuilder()
                .WithButton("Extend", "renew-vps", ButtonStyle.Primary)
                .Build();
            await FollowupAsync(message, ephemeral: true, components: component);
            return;
        }
        var answer = await vpsApi.VpsUserInstanceIdTurnOnPostAsync(userId, target);
        if (!answer.IsOk)
        {
            await PrintError(answer);
            return;
        }
        await FollowupAsync("Starting instance", ephemeral: true);
        var result = await vpsApi.VpsUserInstanceIdSettingsGetAsync(userId, target);
        if (!result.TryOk(out var settings))
        {
            logger.LogInformation("Failed to get settings {response}", result.RawContent);
            await FollowupAsync("Failed to get current settings, check `/vps info` please", ephemeral: true);
            return;
        }
        if (settings.TryGetValue("igns", out var igns) && string.IsNullOrWhiteSpace(igns))
        {
            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Content = "You have not set any IGNs, please do so with `/vps set igns <ign1>,<ign2>`";
                msg.Components = new ComponentBuilder()
                    .WithButton("Set IGNs", "set-igns", ButtonStyle.Primary)
                    .Build();
            });
            return;
        }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(3500);
            var lines = await lokiQuery.GetVpsLog(target, DateTimeOffset.UtcNow.AddMinutes(i == 0 ? -65 : -2), DateTimeOffset.UtcNow, 30);
            foreach (var item in lines)
            {
                if (item.Contains("https://www.microsoft.com/link"))
                {
                    var loginMatch = Regex.Match(item, @"http:\/\/microsoft.com\/link\?otc=[a-zA-Z0-9]+");
                    if (loginMatch.Success)
                    {
                        await ModifyOriginalResponseAsync(msg =>
                        {
                            msg.Content = $"Please login to your microsoft account by clicking the link: {loginMatch.Groups[0].Value}";
                            msg.Components = new ComponentBuilder()
                                .WithButton("Login with Microsoft", url: loginMatch.Groups[0].Value, style: ButtonStyle.Link)
                                .Build();
                        });
                        await Task.Delay(10000);
                        continue;
                    }
                }
                var match = Regex.Match(item, $@"^(.*) logged in!$");
                if (!match.Success)
                    continue;
                await ModifyOriginalResponseAsync(msg =>
                        {
                            msg.Content = $"Started and logged in as `{match.Groups[1].Value}`";
                            msg.Components = new ComponentBuilder()
                                .WithButton("Show Log", "show-log", ButtonStyle.Primary)
                                .Build();
                        });
                await Task.Delay(10000);
                lines = await lokiQuery.GetVpsLog(target, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 30);
                foreach (var line in lines) // make sure user is logged in
                    await CheckForLoginLink(line ?? "");
                return;
            }
        }
        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = $"Instance could not be started, please check logs and contact support, your instance id is `{target}`";
            msg.Components = new ComponentBuilder()
                .Build();
        });
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

    [SlashCommand("export", "Export vps settings as json")]
    public async Task VpsExport()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
            return;
        var result = await vpsApi.VpsUserInstanceIdExportGetAsync(userId, target);
        if (!result.TryOk(out var content))
        {
            await PrintError(result);
            return;
        }
        var userName = Context.User.Username;
        var fileName = $"vps-settings-{userName}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm}.json";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var attachment = new FileAttachment(stream, fileName);
        await FollowupWithFileAsync(attachment, "VPS settings exported", ephemeral: true);
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

    [ComponentInteraction("reset-login", true)]
    public async Task ResetLogin()
    {
        (string userId, Guid target) = await GetInstanceId();

        await ResetUserLogin(userId);
        var originalContext = Context.Interaction as SocketMessageComponent;

        await originalContext!.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = new EmbedBuilder()
                .WithTitle("Reset Login")
                .WithDescription("Reset login details, you can now login again with `/vps start`")
                .WithColor(Color.Green)
                .Build();
            m.Components = new ComponentBuilder()
                .WithButton("Stop Following", "stop-following", ButtonStyle.Danger)
                .Build();
        });
    }

    [ComponentInteraction("show-log")]
    public async Task ShowLog()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (target == default)
        {
            await FollowupAsync("You don't seem to have a vps yet", ephemeral: true);
            return;
        }
        await DisplayLog(target);
    }

    [SlashCommand("log-file", "Get logfile of vps")]
    public async Task GetLogFile()
    {
        (string userId, Guid target) = await GetInstanceId();
        if (Dns.GetHostName().Contains("ekwav"))
            target = Guid.Parse("a7341578-674a-4139-90e0-1d0b225b4663");
        if (target == default)
        {
            await FollowupAsync("You don't seem to have a vps yet", ephemeral: true);
            return;
        }
        var startTime = DateTimeOffset.UtcNow;
        var fullLog = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            var batch = (await lokiQuery.GetVpsLog(target, startTime.AddHours(-i), startTime.AddHours(-i + 1), 5000, true)).ToList();
            if (batch.Count() == 0)
                continue;
            fullLog.InsertRange(0, batch);
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
        await DisplayLog(target);
    }

    private async Task DisplayLog(Guid target)
    {
        var startTime = DateTimeOffset.UtcNow;

        var nanoSeconds = (startTime - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds() * 1_000_000;
        var url = configuration["LOKI_BASE_URL"].Replace("http:", "ws:") + "/loki/api/v1/tail";
        var query = $"{{container=\"tpm-manager\", instance_id=\"{target}\"}}";
        // Follow logs using WebSocket
        try
        {
            var ws = new ClientWebSocket();
            var cancellationToken = new CancellationTokenSource(TimeSpan.FromMinutes(14));

            var fullUrl = $"{url}?query={Uri.EscapeDataString(query)}&start={nanoSeconds}&limit=60";
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
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = new EmbedBuilder()
                        .WithTitle("Discord message can no longer be updated, please run command again").Build();
                    });
                else if (ws.CloseStatus == WebSocketCloseStatus.InternalServerError)
                {
                    var log = await lokiQuery.GetVpsLog(target, startTime.AddHours(-1), startTime, 40);
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = new EmbedBuilder()
                        .WithTitle("Could not follow logs, please run command again to update")
                        .WithColor(Color.DarkOrange)
                        .WithDescription(FormatLog(log)).Build();
                    });
                }
                else
                {
                    logger.LogInformation("WebSocket connection closed without cancel");
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = new EmbedBuilder()
                        .WithTitle("Internal connection closed, you could use /vps log-file as alternative").Build();
                    });
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
            var buffer = new byte[4096 * 64];
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

                if ((logEntry?.streams?.FirstOrDefault()?.values?.Any()) != true)
                {
                    continue;
                }
                var logContent = logEntry.streams.SelectMany(s => s.values.Select(v => (long.Parse(v[0]), v[1]))).OrderBy(v => v.Item1).ToList();

                var displayReset = false;
                foreach (var line in logContent)
                {
                    logReceived.Enqueue(line);
                    if (logReceived.Count > 100)
                        logReceived.Dequeue();
                    await CheckForLoginLink(line.Item2);
                    await CheckForBan(line.Item2);
                    if (await CheckForLoginFail(line.Item2))
                    {
                        displayReset = true;
                    }
                }
                var newest20 = logReceived.OrderByDescending(v => v.Item1).Take(20).OrderBy(v => v.Item1).Select(v => v.Item2);
                var logEmbed = new EmbedBuilder()
                    .WithTitle($"VPS Logs (last received <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>)")
                    .WithDescription(FormatLog(newest20))
                    .WithColor(Color.Blue);

                await ModifyOriginalResponseAsync(m =>
                {
                    if (!displayReset)

                        m.Embed = logEmbed.Build();
                    else
                    {
                        m.Components = new ComponentBuilder()
                            .WithButton("Reset Login", "reset-login", ButtonStyle.Danger)
                            .WithButton("Stop Following", "stop-following", ButtonStyle.Danger)
                            .Build();
                        m.Embed = logEmbed.WithFooter("It looks like you logged in with a minecraft account not owning Minecraft, click the Reset Login button to logout").Build();
                    }
                });
            }
            logger.LogInformation("WebSocket connection closed reason: {reason} {httpResponseStatus}", ws.CloseStatus, ws.HttpStatusCode);
        }

        static string FormatLog(IEnumerable<string> logFollow)
        {
            if (logFollow.Count() == 0)
                return "No recent logs found, is the server on?";
            var primary = "```js\n" + string.Join("\n", logFollow) + "\n```";
            var linkMatch = Regex.Matches(primary, @"https?://[^\s]+");
            foreach (var item in linkMatch.ToList())
            {
                primary += "\nFound link: " + item.Value;
            }
            return primary;
        }
    }

    private async Task CheckForBan(string line)
    {
        if (!line.StartsWith("[TPM] ") || !line.Contains("kicked because You are temporarily banned for ,29d 23h 59m"))
        {
            return;
        }

        (string userId, Guid target) = await GetInstanceId();
        try
        {
            await topUpApi.TopUpCustomPostAsync(userId, new()
            {
                Amount = 2100,
                ProductId = "compensation",
                Reference = "being banned using vps " + DateTime.UtcNow.ToString("yyyy-MM")
            });
            await FollowupAsync("We are sorry to inform you but hypixel banned you. To offset the issue compensated you 2100 CoflCoins", ephemeral: true);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to compensate user for ban, instance {instanceId}", target);
            if (!e.Message.Contains("exists"))
                await FollowupAsync("We are sorry to inform you but hypixel banned you. We tried to compensate you but failed, please contact support", ephemeral: true);
        }
    }
    public async Task<bool> CheckForLoginFail(string line)
    {
        // 	Failed to log into RenaAvali after 120 seconds
        if (!line.StartsWith("[TPM] ") || !line.Contains("Failed to log into"))
        {
            return false;
        }
        var match = Regex.Match(line, @"Failed to log into ([^ ]+) after \d+ seconds");
        return match.Success;

    }

    private async Task CheckForLoginLink(string line)
    {
        var hasLoginLink = Regex.Match(line, @"^\[Coflnet\]:  ?Please click (https?:\/\/[^\s]+) to login ?$");
        if (!hasLoginLink.Success)
        {
            return;
        }
        try
        {
            await LoginImplicitly(hasLoginLink);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to login implicitly with connection id");
        }
    }

    private async Task LoginImplicitly(Match hasLoginLink)
    {
        var loginId = hasLoginLink.Groups[1].Value.Split("conId=").Last();
        var urldecoded = WebUtility.UrlDecode(loginId);
        byte[] idBytes;
        try
        {
            idBytes = Convert.FromBase64String(urldecoded);
        }
        catch (Exception e)
        {
            try
            {
                idBytes = Convert.FromBase64String(urldecoded + "=");
            }
            catch (Exception)
            {
                logger.LogError(e, "Failed to decode connection id");
                return;
            }
        }
        if (idBytes.Length < 16)
        {
            logger.LogError("Invalid connection id length: {length}", idBytes.Length);
            return;
        }
        if (idBytes.Length == 17)
        {
            // check checksum
            var checksum = idBytes[16];
            var sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += idBytes[i];
            }
            if (sum % 256 != checksum)
                throw new ApiException("invalid_id", "The passed connection id is invalid, please get the link from minecraft again");
            urldecoded = Convert.ToBase64String(idBytes, 0, 16);
        }

        var profile = await persistence.GetDiscordAccountInfo(Context.User.Id);
        if (profile?.UserId == null)
        {
            await FollowupAsync("Failed to log you in automatically", ephemeral: true);
            return;
        }
        logger.LogInformation("Logging in user {userId} with connection id {connectionId}", profile.UserId, urldecoded);
        await UpdateSetting(urldecoded, "userId", profile.UserId.ToString());
    }

    private async Task UpdateSetting<T>(string userId, string key, T data)
    {
        await settingsApi.SettingsUpdateSettingAsync(userId, key, JsonConvert.SerializeObject(JsonConvert.SerializeObject(data)));
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
            await FollowupAsync("You don't seem to have verified a minecraft account, use `/update-mc-user`", ephemeral: true);
            return default;
        }
        var instances = await vpsApi.VpsInstancesGetAsync(new(profile.UserId));
        if (!instances.TryOk(out var instance))
        {
            logger.LogInformation("Failed to get instances {response}", instances.RawContent);
            await FollowupAsync("Failed to get instances", ephemeral: true);
            return default;
        }
        if (instance.Count == 0)
        {
            await FollowupAsync("You don't seem to have any instance running", ephemeral: true);
            return default;
        }

        return (profile, instance.First());
    }
}
