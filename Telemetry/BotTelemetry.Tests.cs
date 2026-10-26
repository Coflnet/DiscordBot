using System.Diagnostics;
using Coflnet.Sky.Api.Client.Model;
using Discord.Interactions;
using NUnit.Framework;
using OpenTelemetry.Trace;

namespace Coflnet.DiscordBot.Telemetry;

public class BotTelemetryTests
{
    private static InteractionInfo Info(params (string, string?)[] options) => new(
        42, "slash", "revert", 111, 222, 333,
        options.Select(o => new KeyValuePair<string, string?>(o.Item1, o.Item2)).ToList());

    [Test]
    public void TagsContainCommandUserAndOptions()
    {
        var tags = BotTelemetry.BuildTags(Info(("user", "Ekwav"), ("transactionid", "7"))).ToDictionary(t => t.Key, t => t.Value);
        Assert.Multiple(() =>
        {
            Assert.That(tags["discord.command.name"], Is.EqualTo("revert"));
            Assert.That(tags["discord.user.id"], Is.EqualTo("111"));
            Assert.That(tags["discord.guild.id"], Is.EqualTo("222"));
            Assert.That(tags["discord.channel.id"], Is.EqualTo("333"));
            Assert.That(tags["discord.option.user"], Is.EqualTo("Ekwav"));
            Assert.That(tags["discord.option.transactionid"], Is.EqualTo("7"));
        });
    }

    [TestCase("token")]
    [TestCase("ReviewToken")]
    [TestCase("api_key")]
    [TestCase("key")]
    [TestCase("password")]
    [TestCase("client_secret")]
    public void SensitiveOptionsAreRedacted(string name)
    {
        var tags = BotTelemetry.BuildTags(Info((name, "hunter2")));
        Assert.That(tags.Single(t => t.Key == "discord.option." + name).Value, Is.EqualTo("[redacted]"));
        Assert.That(tags.Select(t => t.Value?.ToString()), Does.Not.Contain("hunter2"));
    }

    [Test]
    public void LongValuesAreTruncatedAndKeywordIsNotRedacted()
    {
        var tags = BotTelemetry.BuildTags(Info(("keyword", new string('a', 1000))));
        var value = (string)tags.Single(t => t.Key == "discord.option.keyword").Value!;
        Assert.That(value.Length, Is.LessThanOrEqualTo(BotTelemetry.MaxValueLength + 3));
        Assert.That(value, Does.StartWith("aaa"));
    }

    [Test]
    public void PreconditionFailureIsMappedWithReason()
    {
        var outcome = BotTelemetry.Describe(PreconditionResult.FromError("User requires guild role 842102236024930304"));
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Status, Is.EqualTo("UnmetPrecondition"));
            Assert.That(outcome.Reason, Does.Contain("guild role"));
        });
    }

    [Test]
    public void SuccessIsMapped()
    {
        var outcome = BotTelemetry.Describe(ExecuteResult.FromSuccess());
        Assert.That(outcome, Is.EqualTo(new InteractionOutcome(true, "ok", null)));
    }

    [Test]
    public void ExecutionExceptionIsMapped()
    {
        var outcome = BotTelemetry.Describe(ExecuteResult.FromError(new InvalidOperationException("boom")));
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Status, Is.EqualTo("Exception"));
            Assert.That(outcome.Reason, Does.Contain("boom"));
        });
    }

    [Test]
    public void InteractionSpanIsExportedWithErrorStatusAndExceptionEvent()
    {
        var exported = new List<Activity>();
        using var provider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddSource(BotTelemetry.SourceName)
            .SetSampler(new InteractionSampler(new AlwaysOffSampler()))
            .AddProcessor(new CollectingProcessor(exported))
            .Build();

        var activity = BotTelemetry.StartInteraction(Info(("user", "x")));
        Assert.That(activity, Is.Not.Null, "span must be created once the source is registered");
        BotTelemetry.Complete(activity, BotTelemetry.Describe(ExecuteResult.FromError(new InvalidOperationException("boom"))), new InvalidOperationException("boom"));
        provider.ForceFlush();

        var span = exported.Single();
        Assert.Multiple(() =>
        {
            Assert.That(span.DisplayName, Is.EqualTo("discord.slash revert"));
            Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(span.Events.Select(e => e.Name), Does.Contain("exception"));
            Assert.That(span.GetTagItem("discord.result.success"), Is.EqualTo(false));
        });
    }

    [Test]
    public void SamplerKeepsInteractionSpansAndChildrenOfRecordedSpans()
    {
        var sampler = new InteractionSampler(new AlwaysOffSampler());
        var root = new SamplingParameters(default, default, "discord.slash revert", ActivityKind.Server, null, null);
        Assert.That(sampler.ShouldSample(root).Decision, Is.EqualTo(SamplingDecision.RecordAndSample));

        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var child = new SamplingParameters(parent, parent.TraceId, "DELETE", ActivityKind.Client, null, null);
        Assert.That(sampler.ShouldSample(child).Decision, Is.EqualTo(SamplingDecision.RecordAndSample));

        var unrelated = new SamplingParameters(default, ActivityTraceId.CreateRandom(), "GET", ActivityKind.Client, null, null);
        Assert.That(sampler.ShouldSample(unrelated).Decision, Is.EqualTo(SamplingDecision.Drop));
    }
}

internal sealed class CollectingProcessor(List<Activity> target) : OpenTelemetry.BaseProcessor<Activity>
{
    public override void OnEnd(Activity data) => target.Add(data);
}

public class RevertLookupTests
{
    [Test]
    public void NoSearchResultYieldsNullInsteadOfThrowing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(global::Commands.FirstPlayerUuid(new List<PlayerResult>()), Is.Null);
            Assert.That(global::Commands.FirstPlayerUuid(null), Is.Null);
        });
    }

    [Test]
    public void FirstResultUuidIsReturned()
    {
        var results = new List<PlayerResult> { new("x", "abc", 1), new("y", "def", 1) };
        Assert.That(global::Commands.FirstPlayerUuid(results), Is.EqualTo("abc"));
    }
}
