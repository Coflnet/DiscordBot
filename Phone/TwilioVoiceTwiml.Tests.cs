using System.Xml.Linq;
using NUnit.Framework;

namespace Coflnet.DiscordBot.Phone;

public class TwilioVoiceTwimlTests
{
    [Test]
    public void PromptOnlyContinuesAfterOneDigit()
    {
        var response = XDocument.Parse(TwilioVoiceTwiml.Prompt(
            "https://bot.example/api/twilio/voice/continue?language=de",
            "https://bot.example/press-one.wav"));
        var gather = response.Root!.Element("Gather")!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)gather.Attribute("input"), Is.EqualTo("dtmf"));
            Assert.That((string?)gather.Attribute("numDigits"), Is.EqualTo("1"));
            Assert.That((string?)gather.Attribute("method"), Is.EqualTo("POST"));
            Assert.That((string?)gather.Attribute("action"), Does.EndWith("?language=de"));
            Assert.That(gather.Element("Play")?.Value, Is.EqualTo("https://bot.example/press-one.wav"));
            Assert.That(response.Root.Elements().Last().Name.LocalName, Is.EqualTo("Hangup"));
        });
    }

    [Test]
    public void StreamCarriesSignedParametersWithoutQueryString()
    {
        var response = XDocument.Parse(TwilioVoiceTwiml.Connect(
            "wss://bot.example/api/twilio/voice/media",
            "CA123",
            "signed-token",
            "de"));
        var stream = response.Descendants("Stream").Single();

        Assert.Multiple(() =>
        {
            Assert.That((string?)stream.Attribute("url"), Does.Not.Contain("?"));
            Assert.That(
                stream.Elements("Parameter").Single(node => (string?)node.Attribute("name") == "CallSid")
                    .Attribute("value")?.Value,
                Is.EqualTo("CA123"));
            Assert.That(
                stream.Elements("Parameter").Single(node => (string?)node.Attribute("name") == "Token")
                    .Attribute("value")?.Value,
                Is.EqualTo("signed-token"));
            Assert.That(
                stream.Elements("Parameter").Single(node => (string?)node.Attribute("name") == "Language")
                    .Attribute("value")?.Value,
                Is.EqualTo("de"));
        });
    }

    [Test]
    public void RateLimitedCallsAreRejectedBeforeAnswer()
    {
        var response = XDocument.Parse(TwilioVoiceTwiml.Reject());
        var reject = response.Root!.Element("Reject")!;
        Assert.That((string?)reject.Attribute("reason"), Is.EqualTo("busy"));
    }
}
