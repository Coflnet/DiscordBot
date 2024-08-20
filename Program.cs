
using Coflnet.Core;
using Coflnet.Discord;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<DiscordHandler>();
builder.Services.AddSingleton<IPlayerNameApi, PlayerNameApi>(di => new PlayerNameApi(builder.Configuration["PLAYERNAME_BASE_URL"]));
builder.Services.AddSingleton<IConnectApi, ConnectApi>(di => new ConnectApi(builder.Configuration["MCCONNECT_BASE_URL"]));
builder.Services.AddSingleton<Coflnet.Payments.Client.Api.IUserApi, Coflnet.Payments.Client.Api.UserApi>(di => new Coflnet.Payments.Client.Api.UserApi(builder.Configuration["PAYMENTS_BASE_URL"]));
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<Persistence>();
builder.Services.AddSingleton<ProfileClient>();
builder.Services.AddCoflnetCore();
builder.Services.AddSingleton<ISearchApi>(di => new SearchApi(builder.Configuration["API_BASE_URL"]));
builder.Services.AddSingleton<IConnectionMultiplexer>(s => ConnectionMultiplexer.Connect(builder.Configuration["CHAT_REDIS_HOST"]));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();



app.MapGet("/weatherforecast", () =>
{
    return "cool";
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();
