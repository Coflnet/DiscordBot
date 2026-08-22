
using Coflnet.Core;
using Coflnet.Discord;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Octokit;
using StackExchange.Redis;
using Coflnet.Sky.ModCommands.Client.Extensions;
using Coflnet.Sky.ModCommands.Client.Client;
using Coflnet.Core.Tracing;
using Coflnet.Security.OpenBao;
using Coflnet.DiscordBot.Phone;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddOpenBaoFromEnvironment();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<DiscordHandler>();
builder.Services.AddHostedService(di=> di.GetRequiredService<DiscordHandler>());
builder.Services.AddSingleton<IssueEvidenceService>();
builder.Services.AddSingleton<IssueDraftService>();
builder.Services.AddHttpClient("discord-evidence-images")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IPlayerNameApi, PlayerNameApi>(di => new PlayerNameApi(builder.Configuration["PLAYERNAME_BASE_URL"]));
builder.Services.AddSingleton<IConnectApi, ConnectApi>(di => new ConnectApi(builder.Configuration["MCCONNECT_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Payments.Client.Api.IUserApi, Coflnet.Payments.Client.Api.UserApi>(di => new Coflnet.Payments.Client.Api.UserApi(builder.Configuration["PAYMENTS_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Payments.Client.Api.ITransactionApi, Coflnet.Payments.Client.Api.TransactionApi>(di => new Coflnet.Payments.Client.Api.TransactionApi(builder.Configuration["PAYMENTS_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Payments.Client.Api.ITopUpApi, Coflnet.Payments.Client.Api.TopUpApi>(di => new Coflnet.Payments.Client.Api.TopUpApi(builder.Configuration["PAYMENTS_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Payments.Client.Api.IProductsApi, Coflnet.Payments.Client.Api.ProductsApi>(di => new Coflnet.Payments.Client.Api.ProductsApi(builder.Configuration["PAYMENTS_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Sky.Settings.Client.Api.ISettingsApi, Coflnet.Sky.Settings.Client.Api.SettingsApi>(di => new Coflnet.Sky.Settings.Client.Api.SettingsApi(builder.Configuration["SETTINGS_BASE_URL"]));
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<LokiQuery>();
builder.Services.AddSingleton<Persistence>();
builder.Services.AddSingleton<FaqService>();
builder.Host.ConfigureApi((context, s, options) =>
{
    options.AddApiHttpClients(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["MOD_BASE_URL"]);
    });
});
builder.Services.AddSingleton<ProfileClient>();
builder.Services.AddSingleton<UserInfoUpdater>();
builder.Services.AddSingleton<GitHubClient>(di =>
{
    var github = new GitHubClient(new ProductHeaderValue("CoflnetBot"))
    {
        Credentials = new Credentials(builder.Configuration["GitHubToken"])
    };
    return github;
});
builder.Services.AddResponseCaching();
builder.Services.AddSingleton<Octokit.GraphQL.Connection>(di =>
{
    var productInformation = new Octokit.GraphQL.ProductHeaderValue("CoflnetBot", "1");
    var connection = new Octokit.GraphQL.Connection(productInformation,builder.Configuration["GitHubToken"]);
    return connection;
});
builder.Services.AddCoflnetCore();
builder.Services.AddControllers();
builder.Services.AddSingleton<ISearchApi>(di => new SearchApi(builder.Configuration["API_BASE_URL"]));
builder.Services.AddSingleton<IConnectionMultiplexer>(s => ConnectionMultiplexer.Connect(builder.Configuration["CHAT_REDIS_HOST"]));
builder.Services.AddOptions<TwilioVoiceOptions>()
    .Bind(builder.Configuration.GetSection(TwilioVoiceOptions.SectionName))
    .Validate(TwilioVoiceOptions.IsValid, "Enabled Twilio voice configuration is incomplete or contains non-public URLs")
    .ValidateOnStart();
builder.Services.AddSingleton<TwilioRequestValidator>();
builder.Services.AddSingleton<TwilioCallGate>();
builder.Services.AddSingleton<TwilioMediaBridge>();
builder.Services.AddSingleton<TwilioVoicemailService>();
builder.Services.AddHostedService(services => services.GetRequiredService<TwilioVoicemailService>());
builder.Services.AddHttpClient<DiscordCallHandoff>();

var app = builder.Build();

app.UseCoflnetCore();

app.UseResponseCaching();
app.UseHttpsRedirection();
app.UseWebSockets();

app.MapControllers();

app.Run();
