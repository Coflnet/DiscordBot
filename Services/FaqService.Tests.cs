using Coflnet.Discord;
using Microsoft.Extensions.Logging.Abstractions;
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

    [TestCase("")]
    [TestCase("   ")]
    public void EmptyAnswerIsNeverReturned(string answer)
    {
        var service = new FaqService(NullLogger<FaqService>.Instance);
        service.SetEntries(new() { new FaqEntry { Question = new() { "help" }, Answer = answer } });

        Assert.That(service.GetResponse("need help"), Is.Null);
    }

    [Test]
    public void EmptyAnswerFallsThroughToNextMatchingEntry()
    {
        var service = new FaqService(NullLogger<FaqService>.Instance);
        service.SetEntries(new()
        {
            new FaqEntry { Question = new() { "help" }, Answer = "" },
            new FaqEntry { Question = new() { "help" }, Answer = "read the docs" }
        });

        Assert.That(service.GetResponse("need help"), Is.EqualTo("read the docs"));
    }
}
