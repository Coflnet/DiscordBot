using Coflnet.Core;
using Coflnet.Core.Tracing;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Logging;

namespace Coflnet.DiscordBot.Telemetry;

/// <summary>Telemetry wiring on top of <c>AddCoflnetCore()</c>, shared by Program.cs and the tests.</summary>
public static class TelemetrySetup
{
    /// <summary>
    /// Registers the interaction source and sampler. Call after <c>AddCoflnetCore()</c> so the sampler wins.
    /// </summary>
    public static void AddBotTracing(this IServiceCollection services)
    {
        // Core 0.7.3 only calls AddOtlpExporter(), and with OpenTelemetry.Exporter 1.16 that ends up on
        // http://localhost:4317 even though OTEL_EXPORTER_OTLP_TRACES_ENDPOINT is set, so no span ever left the pod.
        services.AddOptions<OtlpExporterOptions>().Configure<IConfiguration>(ApplyTracesEndpoint);
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing
            .AddSource(BotTelemetry.SourceName)
            .SetSampler(new InteractionSampler(new TracingServiceExtention.RationOrTimeBasedSampler(0.03, 60))));
    }

    /// <summary>Applies the traces endpoint/protocol from configuration to the exporter options.</summary>
    public static void ApplyTracesEndpoint(OtlpExporterOptions options, IConfiguration config)
    {
        var endpoint = config["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
        // OTEL_EXPORTER_OTLP_ENDPOINT is handled by Core itself (HttpProtobuf), leave that path alone.
        if (string.IsNullOrEmpty(endpoint) || !string.IsNullOrEmpty(config["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return;
        options.Endpoint = uri;
        var protocol = config["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"] ?? config["OTEL_EXPORTER_OTLP_PROTOCOL"];
        options.Protocol = protocol == "http/protobuf" ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
    }

    /// <summary>
    /// Core 0.7.3 does not wire OTLP logging in AddCoflnetCore (0.7.4 does), so do it here. It clears all
    /// logger providers, hence the console logger is added afterwards to keep `kubectl logs` working.
    /// </summary>
    public static void AddBotLogging(this ILoggingBuilder logging, IConfiguration config)
    {
        logging.AddOpenTelemetryLogging(config, config["OTEL_SERVICE_NAME"] ?? "discord-bot");
        // Without an endpoint (or in DEV_LOGGING) Core already registered the console logger.
        if (!string.IsNullOrEmpty(config["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] ?? config["OTEL_EXPORTER_OTLP_ENDPOINT"])
            && config.GetValue<bool?>("DEV_LOGGING") != true)
            logging.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ");
    }
}
