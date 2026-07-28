using Coflnet.Discord;
using NUnit.Framework;

public class FaqServiceTests
{
    [Test]
    public void LogSampleRemovesMentionIdsAndLineBreaks()
    {
        Assert.That(FaqService.CreateLogSample("Thanks <@!12345>\nfor the report"),
            Is.EqualTo("Thanks @user for the report"));
    }

    [Test]
    public void LogSampleIsLimitedToOneHundredCharacters()
    {
        Assert.That(FaqService.CreateLogSample(new string('a', 101)),
            Is.EqualTo(new string('a', 100)));
    }
}
