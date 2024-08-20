FROM mcr.microsoft.com/dotnet/sdk:8.0 as build
WORKDIR /build
COPY DiscordBot.csproj DiscordBot.csproj
RUN dotnet restore
COPY . .
RUN dotnet test
RUN dotnet publish -c release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8000

RUN useradd --uid $(shuf -i 2000-65000 -n 1) app-user
USER app-user

ENTRYPOINT ["dotnet", "DiscordBot.dll", "--hostBuilder:reloadConfigOnChange=false"]
