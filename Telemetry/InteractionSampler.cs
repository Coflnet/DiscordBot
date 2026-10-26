using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Coflnet.DiscordBot.Telemetry;

/// <summary>
/// Coflnet.Core samples by span name (3% plus the first span of each name every 10s) and does not
/// honour the parent decision, so child HTTP spans of a command could be dropped. Interaction spans are
/// rare admin/support actions, keep all of them and every child of a recorded span.
/// </summary>
public sealed class InteractionSampler(Sampler fallback) : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters parameters)
    {
        if (parameters.Name.StartsWith(BotTelemetry.SpanPrefix, StringComparison.Ordinal)
            || (parameters.ParentContext.TraceId != default && parameters.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)))
            return new SamplingResult(SamplingDecision.RecordAndSample);
        return fallback.ShouldSample(in parameters);
    }
}
