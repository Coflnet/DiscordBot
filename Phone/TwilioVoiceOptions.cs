namespace Coflnet.DiscordBot.Phone;

public sealed class TwilioVoiceOptions
{
    public const string SectionName = "TwilioVoice";

    public bool Enabled { get; set; }
    public ulong TargetUserId { get; set; } = 267680402594988033;
    public ulong VoiceChannelId { get; set; } = 939528464455835719;
    public ulong PrivateVoiceChannelId { get; set; } = 1041286026259345449;
    public ulong VoicemailNotificationChannelId { get; set; } = 907954426290008094;
    public int MaxCallsPer24Hours { get; set; } = 3;
    public int MaxCallMinutes { get; set; } = 30;
    public int VoicemailMaxSeconds { get; set; } = 120;
    public int VoicemailRetentionDays { get; set; } = 30;
    public string Region { get; set; } = "ie1";
    public string PublicBaseUrl { get; set; } = "";
    public string MediaStreamUrl { get; set; } = "";
    public string EnglishPressOneAudioUrl { get; set; } = "";
    public string GermanPressOneAudioUrl { get; set; } = "";
    public string EnglishUnavailableAudioUrl { get; set; } = "";
    public string GermanUnavailableAudioUrl { get; set; } = "";
    public string EnglishDiscordNoticeAudioUrl { get; set; } = "";
    public string GermanDiscordNoticeAudioUrl { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string ApiKeySid { get; set; } = "";
    public string ApiKeySecret { get; set; } = "";
    public string CallerHashKey { get; set; } = "";
    public string StreamSigningKey { get; set; } = "";

    public static bool IsValid(TwilioVoiceOptions options)
    {
        if (!options.Enabled)
            return true;

        return options.TargetUserId != 0
            && options.VoiceChannelId != 0
            && options.PrivateVoiceChannelId != 0
            && options.VoicemailNotificationChannelId != 0
            && options.PrivateVoiceChannelId != options.VoiceChannelId
            && options.MaxCallsPer24Hours > 0
            && options.MaxCallMinutes is > 0 and <= 120
            && options.VoicemailMaxSeconds is >= 10 and <= 300
            && options.VoicemailRetentionDays is >= 1 and <= 365
            && options.Region.ToLowerInvariant() is "us1" or "ie1" or "au1"
            && IsAbsoluteUrl(options.PublicBaseUrl, "https")
            && IsAbsoluteUrl(options.MediaStreamUrl, "wss")
            && IsAbsoluteUrl(options.EnglishPressOneAudioUrl, "https")
            && IsAbsoluteUrl(options.GermanPressOneAudioUrl, "https")
            && IsAbsoluteUrl(options.EnglishUnavailableAudioUrl, "https")
            && IsAbsoluteUrl(options.GermanUnavailableAudioUrl, "https")
            && IsAbsoluteUrl(options.EnglishDiscordNoticeAudioUrl, "https")
            && IsAbsoluteUrl(options.GermanDiscordNoticeAudioUrl, "https")
            && options.AuthToken.Length >= 16
            && ((options.ApiKeySid.Length == 0 && options.ApiKeySecret.Length == 0)
                || (options.ApiKeySid.StartsWith("SK", StringComparison.Ordinal)
                    && options.ApiKeySid.Length == 34
                    && options.ApiKeySecret.Length >= 16))
            && options.CallerHashKey.Length >= 32
            && options.StreamSigningKey.Length >= 32;
    }

    public static PhoneLanguage LanguageFromCaller(string? caller)
        => caller?.StartsWith("+49", StringComparison.Ordinal) == true
            ? PhoneLanguage.German
            : PhoneLanguage.English;

    public static string LanguageCode(PhoneLanguage language)
        => language == PhoneLanguage.German ? "de" : "en";

    public static bool TryParseLanguageCode(string? code, out PhoneLanguage language)
    {
        language = code == "de" ? PhoneLanguage.German : PhoneLanguage.English;
        return code is "de" or "en";
    }

    public (string PressOne, string Unavailable, string DiscordNotice) Prompts(PhoneLanguage language)
        => language == PhoneLanguage.German
            ? new(GermanPressOneAudioUrl, GermanUnavailableAudioUrl, GermanDiscordNoticeAudioUrl)
            : new(EnglishPressOneAudioUrl, EnglishUnavailableAudioUrl, EnglishDiscordNoticeAudioUrl);

    private static bool IsAbsoluteUrl(string value, string scheme)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == scheme
            && !uri.IsLoopback;
    }
}

public enum PhoneLanguage
{
    English,
    German
}
