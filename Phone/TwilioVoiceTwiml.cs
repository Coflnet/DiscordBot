using System.Xml.Linq;

namespace Coflnet.DiscordBot.Phone;

public static class TwilioVoiceTwiml
{
    public static string Reject()
        => Document(new XElement("Reject", new XAttribute("reason", "busy")));

    public static string PlayAndHangup(string audioUrl)
        => Document(new XElement("Play", audioUrl), new XElement("Hangup"));

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

    public static string Connect(string streamUrl, string callSid, string token, string language)
        => Document(
            new XElement(
                "Connect",
                new XElement(
                    "Stream",
                    new XAttribute("url", streamUrl),
                    new XElement("Parameter", new XAttribute("name", "CallSid"), new XAttribute("value", callSid)),
                    new XElement("Parameter", new XAttribute("name", "Token"), new XAttribute("value", token)),
                    new XElement("Parameter", new XAttribute("name", "Language"), new XAttribute("value", language)))),
            new XElement("Hangup"));

    public static string Hangup() => Document(new XElement("Hangup"));

    private static string Document(params XElement[] verbs)
    {
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Response", verbs))
            .ToString(SaveOptions.DisableFormatting);
    }
}
