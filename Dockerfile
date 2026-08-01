FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY DiscordBot.csproj DiscordBot.csproj
RUN dotnet restore
COPY . .
RUN dotnet test
RUN dotnet publish -c release -o /app

FROM ubuntu:noble AS voice-native
ARG TARGETARCH
ARG LIBDAVE_VERSION=1.1.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl libgcc-s1 libopus-dev libsodium-dev libstdc++6 unzip \
    && rm -rf /var/lib/apt/lists/*
RUN case "${TARGETARCH}" in \
      amd64) libdave_arch=X64; libdave_sha=2470a131dbf39a820d893ba3d6f01373fc597868caffd6a30a8746e441ed5ede ;; \
      arm64) libdave_arch=ARM64; libdave_sha=2848ca62da5c8303626cfa410827f37735455f614256311fc320b35c7c6a1975 ;; \
      *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && curl -fsSL "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}/cpp/libdave-Linux-${libdave_arch}-boringssl.zip" -o /tmp/libdave.zip \
    && echo "${libdave_sha}  /tmp/libdave.zip" | sha256sum -c - \
    && unzip -q /tmp/libdave.zip -d /opt/libdave \
    && rm /tmp/libdave.zip

# -extra includes ICU + tzdata. Without it (plain -chiseled) .NET runs in
# globalization-invariant mode, and Discord.Net throws CultureNotFoundException
# while handling GUILD_AVAILABLE, so guild channels are never cached and inbound
# (Discord->Minecraft) message forwarding silently breaks.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app

COPY --from=build /app .
COPY --from=voice-native /opt/libdave/lib/libdave.so /app/libdave.so
COPY --from=voice-native /opt/libdave/licenses /usr/share/licenses/libdave
COPY --from=voice-native /usr/lib/*/libgcc_s.so* /usr/lib/
COPY --from=voice-native /usr/lib/*/libopus.so* /usr/lib/
COPY --from=voice-native /usr/lib/*/libsodium.so* /usr/lib/
COPY --from=voice-native /usr/lib/*/libstdc++.so* /usr/lib/

ENV ASPNETCORE_URLS=http://+:8000

ENTRYPOINT ["dotnet", "DiscordBot.dll", "--hostBuilder:reloadConfigOnChange=false"]
