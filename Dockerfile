FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY DiscordBot.csproj DiscordBot.csproj
RUN dotnet restore
COPY . .
RUN dotnet test
RUN dotnet publish -c release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8000

ENTRYPOINT ["dotnet", "DiscordBot.dll", "--hostBuilder:reloadConfigOnChange=false"]
