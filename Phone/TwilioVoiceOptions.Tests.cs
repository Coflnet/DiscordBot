using NUnit.Framework;

namespace Coflnet.DiscordBot.Phone;

public class TwilioVoiceOptionsTests
{
    [Test]
    public void DisabledConfigurationDoesNotRequireSecrets()
    {
        Assert.That(TwilioVoiceOptions.IsValid(new TwilioVoiceOptions()), Is.True);
    }

    [Test]
    public void EnabledConfigurationRejectsPrivateOrInsecureEndpoints()
    {
        var options = CompleteOptions();
        options.PublicBaseUrl = "http://localhost:5000";
        Assert.That(TwilioVoiceOptions.IsValid(options), Is.False);
    }

    [Test]
    public void EnabledConfigurationAcceptsPublicTlsEndpoints()
    {
        Assert.That(TwilioVoiceOptions.IsValid(CompleteOptions()), Is.True);
    }

    [Test]
    public void ApiKeySecretRequiresSid()
    {
        var options = CompleteOptions();
        options.ApiKeySecret = "not-a-real-secret";
        Assert.That(TwilioVoiceOptions.IsValid(options), Is.False);
    }

    private static TwilioVoiceOptions CompleteOptions() => new()
    {
        Enabled = true,
        PublicBaseUrl = "https://bot.example",
        MediaStreamUrl = "wss://bot.example/api/twilio/voice/media",
        EnglishPressOneAudioUrl = "https://bot.example/audio/press-one-en.wav",
        GermanPressOneAudioUrl = "https://bot.example/audio/press-one-de.wav",
        EnglishUnavailableAudioUrl = "https://bot.example/audio/unavailable-en.wav",
        GermanUnavailableAudioUrl = "https://bot.example/audio/unavailable-de.wav",
        EnglishDiscordNoticeAudioUrl = "https://bot.example/audio/notice-en.wav",
        GermanDiscordNoticeAudioUrl = "https://bot.example/audio/notice-de.wav",
        AuthToken = "0123456789abcdef",
        CallerHashKey = "0123456789abcdef0123456789abcdef",
        StreamSigningKey = "abcdef0123456789abcdef0123456789"
    };

    [TestCase("+491701234567", PhoneLanguage.German)]
    [TestCase("+441234567890", PhoneLanguage.English)]
    [TestCase("+12025550123", PhoneLanguage.English)]
    public void CallerCountryCodeSelectsLanguage(string caller, PhoneLanguage expected)
    {
        Assert.That(TwilioVoiceOptions.LanguageFromCaller(caller), Is.EqualTo(expected));
    }
}
