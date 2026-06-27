FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY DiscordBot.csproj DiscordBot.csproj
RUN dotnet restore
COPY . .
RUN dotnet test
RUN dotnet publish -c release -o /app

# -extra includes ICU + tzdata. Without it (plain -chiseled) .NET runs in
# globalization-invariant mode, and Discord.Net throws CultureNotFoundException
# while handling GUILD_AVAILABLE, so guild channels are never cached and inbound
# (Discord->Minecraft) message forwarding silently breaks.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8000

ENTRYPOINT ["dotnet", "DiscordBot.dll", "--hostBuilder:reloadConfigOnChange=false"]
