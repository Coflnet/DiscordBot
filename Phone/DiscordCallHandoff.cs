using Discord.Audio;
using Microsoft.Extensions.Options;

namespace Coflnet.DiscordBot.Phone;

public sealed class DiscordCallHandoff(
    HttpClient httpClient,
    IOptions<TwilioVoiceOptions> options,
    DiscordHandler discord)
{
    private const int MaximumNoticeBytes = 5 * 1024 * 1024;
    private readonly TwilioVoiceOptions options = options.Value;

    public async Task<DiscordVoiceSession?> PrepareAsync(
        PhoneLanguage language,
        CancellationToken cancellationToken)
    {
        if (!discord.IsUserInVoiceChannel(options.TargetUserId, options.VoiceChannelId))
            return null;

        var url = options.Prompts(language).DiscordNotice;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var noticeUri) || noticeUri.Scheme != "https")
            throw new InvalidOperationException("The selected Discord notice URL is not configured");

        using var response = await httpClient.GetAsync(
            noticeUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumNoticeBytes)
            throw new InvalidDataException("Discord notice exceeds 5 MiB");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var wave = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (wave.Length + read > MaximumNoticeBytes)
                throw new InvalidDataException("Discord notice exceeds 5 MiB");
            await wave.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return await discord.AnnounceAndMoveAsync(
            options.TargetUserId,
            options.VoiceChannelId,
            options.PrivateVoiceChannelId,
            WavePcmReader.Read48KhzStereo16Bit(wave.GetBuffer().AsSpan(0, (int)wave.Length)),
            cancellationToken);
    }

    public Task EndAsync(DiscordVoiceSession session)
        => discord.EndCallAsync(
            options.TargetUserId,
            options.PrivateVoiceChannelId,
            options.VoiceChannelId,
            session);
}

public sealed record DiscordVoiceSession(IAudioClient AudioClient, ulong BotUserId);
