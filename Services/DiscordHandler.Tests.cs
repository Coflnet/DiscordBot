using NUnit.Framework;

public class DiscordHandlerTests
{
    [TestCase("https://sky.coflnet.com/item/STING?range=year&ultimate_chimera=4-4&looting=5-5&divine_gift=3-3", false)]
    [TestCase("Claim your gift https://example.com", true)]
    [TestCase("https://example.com claim your gift", true)]
    public void NitroScamLinksRequireSpaceBeforeGift(string content, bool expected)
    {
        Assert.That(DiscordHandler.IsNitroScamLink(content), Is.EqualTo(expected));
    }
}
