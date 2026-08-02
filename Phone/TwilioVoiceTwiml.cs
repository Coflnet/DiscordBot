using System.Xml.Linq;

namespace Coflnet.DiscordBot.Phone;

public static class TwilioVoiceTwiml
{
    public static string Reject()
        => Document(new XElement("Reject", new XAttribute("reason", "busy")));

    public static string PlayAndHangup(string audioUrl)
        => Document(new XElement("Play", audioUrl), new XElement("Hangup"));

    public static string Voicemail(
        string actionUrl,
        string statusUrl,
        string audioUrl,
        PhoneLanguage language,
        int maxSeconds)
        => Document(
            new XElement("Play", audioUrl),
            new XElement(
                "Say",
                new XAttribute("language", language == PhoneLanguage.German ? "de-DE" : "en-GB"),
                language == PhoneLanguage.German
                    ? "Sie können nach dem Signalton eine Nachricht hinterlassen. Ihre Nachricht wird aufgezeichnet. Drücken Sie zum Beenden die Raute-Taste."
                    : "You can leave a voicemail after the tone. Your message will be recorded. Press hash when finished."),
            new XElement(
                "Record",
                new XAttribute("action", actionUrl),
                new XAttribute("method", "POST"),
                new XAttribute("recordingStatusCallback", statusUrl),
                new XAttribute("recordingStatusCallbackMethod", "POST"),
                new XAttribute("recordingStatusCallbackEvent", "completed absent"),
                new XAttribute("maxLength", maxSeconds),
                new XAttribute("timeout", "5"),
                new XAttribute("finishOnKey", "#"),
                new XAttribute("playBeep", "true"),
                new XAttribute("trim", "trim-silence")));

    public static string VoicemailFinished(PhoneLanguage language)
        => Document(
            new XElement(
                "Say",
                new XAttribute("language", language == PhoneLanguage.German ? "de-DE" : "en-GB"),
                language == PhoneLanguage.German
                    ? "Vielen Dank. Ihre Nachricht wurde gespeichert."
                    : "Thank you. Your message has been saved."),
            new XElement("Hangup"));

    public static string Prompt(string actionUrl, string audioUrl)
        => Document(
            new XElement(
                "Gather",
                new XAttribute("input", "dtmf"),
                new XAttribute("numDigits", "1"),
                new XAttribute("timeout", "7"),
                new XAttribute("action", actionUrl),
                new XAttribute("method", "POST"),
                new XElement("Play", audioUrl)),
            new XElement("Hangup"));

    public static string Connect(
        string streamUrl,
        string callSid,
        string token,
        string afterUrl,
        PhoneLanguage language)
        => Document(
            new XElement(
                "Say",
                new XAttribute("language", language == PhoneLanguage.German ? "de-DE" : "en-GB"),
                language == PhoneLanguage.German
                    ? "Einen Moment bitte. Ihr Anruf wird jetzt verbunden."
                    : "One moment please. Your call is now being connected."),
            new XElement(
                "Connect",
                new XElement(
                    "Stream",
                    new XAttribute("url", streamUrl),
                    new XElement("Parameter", new XAttribute("name", "CallSid"), new XAttribute("value", callSid)),
                    new XElement("Parameter", new XAttribute("name", "Token"), new XAttribute("value", token)),
                    new XElement(
                        "Parameter",
                        new XAttribute("name", "Language"),
                        new XAttribute("value", TwilioVoiceOptions.LanguageCode(language))))),
            new XElement("Redirect", new XAttribute("method", "POST"), afterUrl));

    public static string Hangup() => Document(new XElement("Hangup"));

    private static string Document(params XElement[] verbs)
    {
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Response", verbs))
            .ToString(SaveOptions.DisableFormatting);
    }
}
