using System.Diagnostics;
using Coflnet.Core;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Coflnet.DiscordBot.Telemetry;

public class TelemetrySetupTests
{
    private sealed class Collector : BaseProcessor<Activity>
    {
        public List<Activity> Ended { get; } = new();
        public override void OnEnd(Activity data) { lock (Ended) Ended.Add(data); }
    }

    private static WebApplication BuildLikeProgram(Dictionary<string, string?> settings, Collector? collector = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddCoflnetCore();
        builder.Logging.AddBotLogging(builder.Configuration);
        builder.Services.AddBotTracing();
        if (collector != null)
            builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(collector));
        return builder.Build();
    }

    [Test]
    public async Task TracesEndpointFromConfigurationReachesTheExporterAndSpansAreRecorded()
    {
        var collector = new Collector();
        await using var app = BuildLikeProgram(new()
        {
            ["OTEL_SERVICE_NAME"] = "discord-bot",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://jaeger-collector.observability:4317",
        }, collector);
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        try
        {
            var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OtlpExporterOptions>>().CurrentValue;
            Assert.That(options.Endpoint, Is.EqualTo(new Uri("http://jaeger-collector.observability:4317")));
            Assert.That(options.Protocol, Is.EqualTo(OtlpExportProtocol.Grpc));

            using var http = new HttpClient();
            using (var interaction = BotTelemetry.Source.StartActivity("discord.slash test", ActivityKind.Server))
            {
                Assert.That(interaction, Is.Not.Null, "interaction source is not listened to");
                await http.GetStringAsync(app.Urls.First() + "/ping");
            }
            app.Services.GetRequiredService<TracerProvider>().ForceFlush(5000);
            var names = collector.Ended.Select(a => a.OperationName + "|" + a.Source.Name).ToList();
            Assert.That(names, Does.Contain("discord.slash test|" + BotTelemetry.SourceName));
            Assert.That(collector.Ended.Any(a => a.Source.Name == "System.Net.Http" || a.Source.Name.Contains("Http")), Is.True, string.Join(", ", names));
            var child = collector.Ended.First(a => a.OperationName == "System.Net.Http.HttpRequestOut");
            Assert.That(child.TraceId, Is.EqualTo(collector.Ended.First(a => a.Source.Name == BotTelemetry.SourceName).TraceId));
        }
        finally { await app.StopAsync(); }
    }

    [Test]
    public void BaseEndpointKeepsCoresHttpProtobufPath()
    {
        var options = new OtlpExporterOptions();
        TelemetrySetup.ApplyTracesEndpoint(options, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4318",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://other:4317",
        }).Build());
        Assert.That(options.Endpoint, Is.EqualTo(new OtlpExporterOptions().Endpoint));
    }

    private static List<ILoggerProvider> Providers(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(l => { l.AddConsole(); l.AddBotLogging(config); });
        return services.BuildServiceProvider().GetServices<ILoggerProvider>().ToList();
    }

    [Test]
    public void OtlpAndConsoleLoggingAreBothRegisteredWhenLogsEndpointIsSet()
    {
        var providers = Providers(new() { ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://loki-scalable-write.loki:3100/otlp/v1/logs" });
        Assert.That(providers.OfType<OpenTelemetryLoggerProvider>().Count(), Is.EqualTo(1));
        Assert.That(providers.Count(p => p.GetType().Name == "ConsoleLoggerProvider"), Is.EqualTo(1));
    }

    [Test]
    public void OnlyOneConsoleLoggerWithoutLogsEndpoint()
    {
        var providers = Providers(new());
        Assert.That(providers.OfType<OpenTelemetryLoggerProvider>(), Is.Empty);
        Assert.That(providers.Count(p => p.GetType().Name == "ConsoleLoggerProvider"), Is.EqualTo(1));
    }
}
