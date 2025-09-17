

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Coflnet.Core;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.ModCommands.Client.Api;
using Coflnet.Sky.ModCommands.Client.Model;
using Coflnet.Sky.Settings.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Newtonsoft.Json;

public partial class VpsCommands
{
    [Group("create", "Create a new vps")]
    public class Create : InteractionModuleBase
    {
        private readonly ISearchApi searchApi;
        private readonly IVpsApi vpsApi;
        private readonly Persistence persistence;
        private readonly ISettingsApi settingsApi;
        private readonly ILogger<Create> logger;
        private readonly LokiQuery lokiQuery;

        public Create(IVpsApi vpsApi, Persistence persistence, ISettingsApi settingsApi, ILogger<Create> logger, ISearchApi searchApi, LokiQuery lokiQuery)
        {
            this.vpsApi = vpsApi;
            this.persistence = persistence;
            this.settingsApi = settingsApi;
            this.logger = logger;
            this.searchApi = searchApi;
            this.lokiQuery = lokiQuery;
        }

        [SlashCommand("tpm_plus", "Create managed tpm+ instance on fast servers")]
        [DefaultMemberPermissions(GuildPermission.SendMessages)]
        public async Task VpsCreatePlus([Autocomplete(typeof(McNameAutocompleteHandler))] string ign, ITextChannel? webHookChannel = null)
        {
            await VpsCreate("tpm+", ign, webHookChannel);
        }
        [SlashCommand("tpm", "Create managed tpm instance on fast servers")]
        [DefaultMemberPermissions(GuildPermission.SendMessages)]
        public async Task VpsCreate([Autocomplete(typeof(McNameAutocompleteHandler))] string ign, ITextChannel? webHookChannel = null)
        {
            await VpsCreate("tpm", ign, webHookChannel);
        }
        public async Task VpsCreate(string kind, string ign, ITextChannel? webHookChannel = null)
        {
            await DeferAsync(ephemeral: true);
            var playerSearch = await searchApi.ApiSearchPlayerPlayerNameGetAsync(ign);
            ign = playerSearch.First().Name; // also important for correct casing
            var profile = await persistence.GetDiscordAccountInfo(Context.User.Id);
            if (profile?.UserId.Contains('\\') ?? false)
                profile = null;// ignore broken accounts
            IUserMessage message = null;
            if (profile == null)
            {
                (_, var targetId) = ComputeConnectionId(Context.User.Id.ToString(), Guid.NewGuid().ToString());
                var link = GetAuthLink(targetId);
                logger.LogInformation("User {user} not found, sending auth link {link} with id {id}", Context.User.Id, link, targetId);
                var button = new ComponentBuilder()
                    .WithButton("Click to login via website", style: ButtonStyle.Link, url: link).Build();
                message = await FollowupAsync($"You don't seem to have verified a minecraft account, use `/update-mc-user` or click [here to login via the website]({link})", ephemeral: true, components: button);
                for (int i = 0; i < 180 / 5; i++)
                {
                    await Task.Delay(5000);
                    var settingsId = await settingsApi.SettingsGetSettingAsync(targetId, "userId");
                    if (string.IsNullOrEmpty(settingsId?.Trim('"')))
                        continue;
                    var accountInfo = new DiscordAccountInfo()
                    {
                        DiscordId = Context.User.Id,
                        UserId = settingsId.Trim('"', '\\'),
                        MinecraftUuid = Guid.Empty,
                        MinecraftName = null,
                        Attributes = new Dictionary<string, string>(),
                        AccountTier = AccountTier.NONE
                    };
                    logger.LogInformation("User {user} found, saving account info {info}", Context.User.Id, JsonConvert.SerializeObject(accountInfo));
                    await persistence.SaveDiscordAccountInfo(accountInfo);
                    profile = accountInfo;
                    break;
                }
                if (profile == null)
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = "Request timed out, please rerun the create command again");
                    return;
                }
            }
            else
                message = await FollowupAsync("Preparing setup");
            var instances = await vpsApi.VpsInstancesGetAsync(new(profile.UserId));
            if (!instances.TryOk(out var instance))
            {
                logger.LogInformation("Failed to get instances {response}", instances.RawContent);
                await ModifyOriginalResponseAsync(msg => msg.Content = "Failed to get instances");
                return;
            }
            if (instance.Count > 0)
            {
                if (!profile.Attributes.ContainsKey("vpsId"))
                {
                    await HandleLogin(ign, instance.First());
                    return;
                }
                if (instance.First().PaidUntil > DateTime.UtcNow)
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = "You already have an instance running";
                        msg.Embed = new EmbedBuilder()
                        {
                            Title = "Instance already running",
                            Description = $"You already have an instance running, having multiple is not supported\n"
                            + $"Use the other `/vps` commands to manage your instance",
                            Color = Color.Green
                        }.Build();
                    });
                else
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = "Your vps has expired, please extend your instance with `/vps info`";
                        msg.Components = new ComponentBuilder()
                            .WithButton("Extend 30 days (Costs CoflCoins)", "renew-vps", ButtonStyle.Primary)
                            .Build();
                    });
                return;
            }
            await ModifyOriginalResponseAsync(msg => msg.Content = "Creating instance, please wait");
            var options = System.Text.Json.JsonSerializer.Deserialize<VpsCreateRequest>(JsonConvert.SerializeObject(new
            {
                appKind = kind,
                mcName = ign,
            }), new JsonSerializerOptions()
            {
                Converters =
                {
                    new VpsCreateRequestJsonConverter()
                }
            });
            var result = await vpsApi.VpsPostAsync(profile.UserId, new(options!));
            if (!result.TryOk(out var newInstance))
            {
                logger.LogInformation("Failed to create instance {response} for {options}", result.RawContent, System.Text.Json.JsonSerializer.Serialize(options));
                await PrintError(result);
                return;
            }
            await ModifyOriginalResponseAsync(msg => msg.Content = "Created an instance");
            if (kind == "tpm+")
                await vpsApi.VpsUserInstanceIdSetPostAsync(newInstance.OwnerId, newInstance.Id ?? default, new(new()
                {
                    Setting = "skipalways",
                    Value = $"true"
                }));
            await vpsApi.VpsUserInstanceIdSetPostAsync(newInstance.OwnerId, newInstance.Id ?? default, new(new()
            {
                Setting = "discordID",
                Value = Context.User.Id.ToString()
            }));
            if (webHookChannel != null)
            {
                try
                {
                    await TryCreateWebhook(ign, webHookChannel, newInstance);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to create webhook for {channel}", webHookChannel.Id);
                    await ModifyOriginalResponseAsync(msg => msg.Content = "Failed to create webhook for specified channel, please create it manually");
                }
            }
            await HandleLogin(ign, newInstance);
        }

        private async Task TryCreateWebhook(string ign, ITextChannel webHookChannel, Instance newInstance)
        {
            var existingWebhooks = await webHookChannel.GetWebhooksAsync();
            var existingWebhook = existingWebhooks.FirstOrDefault(w => w.Name == "CoflVps");
            if (existingWebhook == null)
            {
                if (existingWebhooks.Count >= 10)
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = "You have too many webhooks in this channel, please remove some");
                    return;
                }
                var currentUser = Context.User as SocketGuildUser;
                var permissions = currentUser?.GetPermissions(webHookChannel);
                if (!permissions.HasValue || !permissions.Value.ManageWebhooks)
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = "I (bot) or you (user) lack the permission to create a webhook in the specified channel. Please grant me the 'Manage Webhooks' permission.");
                    return;
                }
                existingWebhook = await webHookChannel.CreateWebhookAsync("CoflVps");
            }
            await vpsApi.VpsUserInstanceIdSetPostAsync(newInstance.OwnerId, newInstance.Id ?? default, new(new()
            {
                Setting = "webhooks",
                Value = $"https://discord.com/api/webhooks/{existingWebhook.Id}/{existingWebhook.Token}"
            }));
            var webhook = await webHookChannel.SendMessageAsync($"New instance created for {ign}, webhooks will be sent here");
        }

        private async Task<bool> HandleLogin(string ign, Instance newInstance)
        {
            if (newInstance.PaidUntil < DateTime.UtcNow)
            {
                await ModifyOriginalResponseAsync(msg =>
                {
                    var timeStamp = new DateTimeOffset(newInstance.PaidUntil ?? DateTime.UtcNow).ToUnixTimeSeconds();
                    msg.Content = $"Your vps has expired at <t:{timeStamp}>. You can check details with `/vps info`";
                    msg.Components = new ComponentBuilder()
                        .WithButton("Get another 30 days", "renew-vps", ButtonStyle.Primary)
                        .Build();
                });
                return false;
            }
            await vpsApi.VpsUserInstanceIdSetPostAsync(newInstance.OwnerId, newInstance.Id ?? default, new(new()
            {
                Setting = "igns",
                Value = ign
            }));
            await vpsApi.VpsUserInstanceIdTurnOnPostAsync(newInstance.OwnerId, newInstance.Id ?? default);
            await Task.Delay(3000);
            await ModifyOriginalResponseAsync(msg => msg.Content = "Configuring instance");
            for (int i = 0; i < 20; i++)
            {
                var lines = await lokiQuery.GetVpsLog(newInstance.Id ?? default, DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now, 100);
                foreach (var item in lines)
                {
                    var match = Regex.Match(item, @".*(http://microsoft.com/link\?.*)");
                    if (item.Contains(" logged in!"))
                    {
                        // seemingly already logged in in the past, continue to next step
                        i = 20;
                        break;
                    }
                    if (!match.Success)
                        continue;
                    var link = match.Groups[1].Value;
                    var button = new ComponentBuilder()
                        .WithButton("Click here to login with microsoft", style: ButtonStyle.Link, url: link);
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = "Started your instance";
                        msg.Components = button.Build();
                    });
                    i = 20;
                }
                await Task.Delay(6000);
                if (i == 19)
                {
                    var referenceId = Guid.NewGuid();
                    logger.LogInformation("Failed to get login link {lines} for {instance}, referenceId: {refernceId}", string.Join("\n", lines), newInstance.Id, referenceId);
                    await ModifyOriginalResponseAsync(msg => msg.Content = $"Failed to get login link, please check the logs, try again or ask Äkwav to help and give him `{referenceId}`");
                    return false;
                }
                else
                    await ModifyOriginalResponseAsync(msg => msg.Content = (i >= 10 ? "still " : "") + "Waiting for login link");
            }
            for (int i = 0; i < 20; i++)
            {
                var lines = await lokiQuery.GetVpsLog(newInstance.Id ?? default, DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now, 100);
                foreach (var item in lines)
                {
                    var match = Regex.Match(item, $@"^(.*) logged in!$");
                    if (!match.Success)
                        continue;
                    var foundIgn = match.Groups[1].Value;
                    if (foundIgn != ign)
                    {
                        await ModifyOriginalResponseAsync(msg =>
                        {
                            msg.Content = $"Logged in with another account (`{foundIgn}`), please re-run the command with that username";
                            msg.Embed = new EmbedBuilder()
                            {
                                Title = "Logged in with another account",
                                Description = $"Your instance is ready to use, but you logged in with {foundIgn} instead of **{ign}**, please re-run the command with that username.\n"
                                + $"`/vps create tpm_plus ign:{foundIgn}`",
                                Color = Color.Red
                            }.Build();
                        });
                        return false;
                    }
                    var button = new ComponentBuilder()
                        .WithButton("Edit filters", style: ButtonStyle.Link, url: "https://sky.coflnet.com/flipper")
                        .WithButton("Extend 30 days (Costs CoflCoins)", "renew-vps", ButtonStyle.Primary);
                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = "Setup completed!";
                        msg.Embed = new EmbedBuilder()
                        {
                            Title = "Setup completed",
                            Description = $"Your instance is ready to use, you can now use `/vps info` and `/vps log`",
                            Color = Color.Green
                        }.Build();
                        msg.Components = button.Build();
                    });
                    var userInfo = await persistence.GetDiscordAccountInfo(Context.User.Id);
                    userInfo.MinecraftName = ign;
                    var search = await searchApi.ApiSearchPlayerPlayerNameGetAsync(ign);
                    userInfo.MinecraftUuid = Guid.Parse(search.First().Uuid);
                    userInfo.Attributes ??= new();
                    userInfo.Attributes["vpsId"] = newInstance.Id.ToString();
                    userInfo.MinecraftUuids ??= new();
                    userInfo.MinecraftUuids.Add(Guid.Parse(search.First().Uuid));
                    await persistence.SaveDiscordAccountInfo(userInfo);
                    return true;
                }
                await Task.Delay(6000);
                if (i == 19)
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = "Failed to get login link, please check the logs");
                    return false;
                }
                else
                    await ModifyOriginalResponseAsync(msg => msg.Content = (i >= 10 ? "still " : "") + "Waiting for login confirmation");
            }

            return false;
        }

        public virtual string GetAuthLink(string stringId)
        {
            var decoded = Convert.FromBase64String(stringId);
            var sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += decoded[i];
            }
            var newid = Convert.ToBase64String(decoded.Append((byte)(sum % 256)).ToArray());

            return $"https://sky.coflnet.com/authmod?mcid=none-Discord&conId={HttpUtility.UrlEncode(newid)}";
        }
        public (long, string) ComputeConnectionId(string playerId, string sessionId)
        {
            var bytes = Encoding.UTF8.GetBytes(playerId.ToLower() + sessionId + "fancySalt");
            var hash = System.Security.Cryptography.SHA512.Create();
            var hashed = hash.ComputeHash(bytes);
            return (BitConverter.ToInt64(hashed), Convert.ToBase64String(hashed, 0, 16));
        }


        private async Task PrintError(Coflnet.Sky.ModCommands.Client.Client.IApiResponse result)
        {
            var deserialized = JsonConvert.DeserializeObject<ApiException>(result.RawContent);
            logger.LogInformation(result.RawContent);
            await FollowupAsync(deserialized.Message, ephemeral: true);
        }
    }
}
