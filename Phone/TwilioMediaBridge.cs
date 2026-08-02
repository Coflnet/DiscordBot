using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Discord;
using Discord.Audio;
using Microsoft.Extensions.Options;

namespace Coflnet.DiscordBot.Phone;

public sealed class TwilioMediaBridge(
    IOptions<TwilioVoiceOptions> options,
    TwilioCallGate callGate,
    DiscordHandler discord,
    DiscordCallHandoff handoff,
    ILogger<TwilioMediaBridge> logger)
{
    private const int RealtimeBufferMilliseconds = 100;
    private static readonly TimeSpan MediaInactivityTimeout = TimeSpan.FromSeconds(2);
    private readonly TwilioVoiceOptions options = options.Value;

    public async Task HandleAsync(HttpContext context)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        string? callSid = null;
        DiscordVoiceSession? session = null;
        AudioOutStream? callerAudio = null;

        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        callCancellation.CancelAfter(TimeSpan.FromMinutes(options.MaxCallMinutes));

        try
        {
            var start = await ReceiveAsync(webSocket, callCancellation.Token);
            if (start?.Event == "connected")
                start = await ReceiveAsync(webSocket, callCancellation.Token);
            callSid = start?.Start?.CallSid;
            var parameters = start?.Start?.CustomParameters;
            if (start?.Event != "start"
                || string.IsNullOrWhiteSpace(callSid)
                || parameters is null
                || !parameters.TryGetValue("CallSid", out var signedCallSid)
                || signedCallSid != callSid
                || !parameters.TryGetValue("Token", out var token)
                || !callGate.IsValidStreamToken(callSid, token)
                || !parameters.TryGetValue("Language", out var languageCode)
                || !TwilioVoiceOptions.TryParseLanguageCode(languageCode, out var language)
                || !await callGate.ActivateAsync(callSid))
            {
                await CloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Invalid stream", context.RequestAborted);
                return;
            }

            var preparation = handoff.PrepareAsync(language, callCancellation.Token);
            // Do not replay audio collected while Discord plays the notice and switches channels.
            while (!preparation.IsCompleted)
            {
                var message = await ReceiveAsync(webSocket, callCancellation.Token);
                if (message is null || message.Event == "stop")
                {
                    callCancellation.Cancel();
                    break;
                }
            }

            session = await preparation;
            if (callCancellation.IsCancellationRequested)
                return;
            if (session is null)
            {
                await callGate.MarkHandoffUnavailableAsync(callSid);
                await CloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Unavailable", context.RequestAborted);
                return;
            }

            callerAudio = session.AudioClient.CreatePCMStream(
                AudioApplication.Voice,
                bufferMillis: RealtimeBufferMilliseconds);
            await BridgeAsync(webSocket, start.Start!.StreamSid, session, callerAudio, callCancellation);
            session = null;
        }
        catch (OperationCanceledException) when (callCancellation.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "Twilio media stream ended");
        }
        catch (Exception exception)
        {
            if (callSid is not null && session is null)
                await callGate.MarkHandoffUnavailableAsync(callSid);
            logger.LogError(exception, "Twilio/Discord voice bridge failed");
        }
        finally
        {
            callCancellation.Cancel();
            if (session is not null)
            {
                try
                {
                    await handoff.EndAsync(session);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not return the Discord user after the phone call");
                }
            }
            if (callerAudio is not null)
            {
                try
                {
                    await callerAudio.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not cleanly close Discord caller audio");
                }
            }
            if (callSid is not null)
            {
                try
                {
                    await callGate.ReleaseAsync(callSid);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not release the phone-call lease");
                }
            }
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "Call ended", CancellationToken.None);
        }
    }

    private async Task BridgeAsync(
        WebSocket webSocket,
        string streamSid,
        DiscordVoiceSession session,
        AudioOutStream callerAudio,
        CancellationTokenSource cancellation)
    {
        var discordFrames = Channel.CreateBounded<short[]>(new BoundedChannelOptions(25)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var pumps = new ConcurrentDictionary<AudioInStream, Task>();

        Task StartPump(ulong userId, AudioInStream stream)
        {
            if (userId != options.TargetUserId)
                return Task.CompletedTask;
            return pumps.GetOrAdd(stream, _ => PumpDiscordUserAsync(
                stream,
                discordFrames.Writer,
                cancellation.Token));
        }

        Task StreamCreated(ulong userId, AudioInStream stream)
        {
            _ = StartPump(userId, stream);
            return Task.CompletedTask;
        }

        session.AudioClient.StreamCreated += StreamCreated;
        foreach (var stream in session.AudioClient.GetStreams())
            _ = StartPump(stream.Key, stream.Value);

        var outbound = SendDiscordAudioAsync(webSocket, streamSid, discordFrames.Reader, cancellation.Token);
        var presence = MonitorPresenceAsync(cancellation);

        try
        {
            while (!cancellation.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                receiveCancellation.CancelAfter(MediaInactivityTimeout);
                TwilioStreamMessage? message;
                try
                {
                    message = await ReceiveAsync(webSocket, receiveCancellation.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    logger.LogInformation("Twilio media stream became inactive; ending the call");
                    break;
                }
                if (message is null || message.Event == "stop")
                    break;
                if (message.Event != "media" || string.IsNullOrWhiteSpace(message.Media?.Payload))
                    continue;

                byte[] payload;
                try
                {
                    payload = Convert.FromBase64String(message.Media.Payload);
                }
                catch (FormatException)
                {
                    throw new WebSocketException("Twilio sent an invalid media payload");
                }
                await callerAudio.WriteAsync(PcmuCodec.DecodeToDiscordPcm(payload), cancellation.Token);
            }
        }
        finally
        {
            session.AudioClient.StreamCreated -= StreamCreated;
            cancellation.Cancel();
            discordFrames.Writer.TryComplete();
            await handoff.EndAsync(session);
            await IgnoreCancellation(outbound);
            await IgnoreCancellation(presence);
            await Task.WhenAll(pumps.Values.Select(IgnoreCancellation));
        }
    }

    private async Task PumpDiscordUserAsync(
        AudioInStream stream,
        ChannelWriter<short[]> writer,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[3840];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(total, buffer.Length - total),
                        cancellationToken);
                    if (read == 0)
                        return;
                    total += read;
                }
                await writer.WriteAsync(PcmuCodec.DownsampleDiscordPcm(buffer), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendDiscordAudioAsync(
        WebSocket webSocket,
        string streamSid,
        ChannelReader<short[]> reader,
        CancellationToken cancellationToken)
    {
        var started = false;
        await foreach (var samples in reader.ReadAllAsync(cancellationToken))
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "media",
                streamSid,
                media = new { payload = Convert.ToBase64String(PcmuCodec.Encode(samples)) }
            });
            await webSocket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
            if (!started)
            {
                logger.LogInformation("Started forwarding Discord audio to Twilio");
                started = true;
            }
        }
    }

    private async Task MonitorPresenceAsync(CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                if (!discord.IsUserInVoiceChannel(options.TargetUserId, options.PrivateVoiceChannelId))
                {
                    cancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task<TwilioStreamMessage?> ReceiveAsync(
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var total = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer.AsMemory(total), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            total += result.Count;
            if (total == buffer.Length && !result.EndOfMessage)
                throw new WebSocketException("Twilio media message exceeded 64 KiB");
        } while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<TwilioStreamMessage>(buffer.AsSpan(0, total));
    }

    private static async Task CloseAsync(
        WebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            await webSocket.CloseAsync(status, description, cancellationToken);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class TwilioStreamMessage
    {
        [JsonPropertyName("event")]
        public string? Event { get; init; }

        [JsonPropertyName("start")]
        public TwilioStart? Start { get; init; }

        [JsonPropertyName("media")]
        public TwilioMedia? Media { get; init; }
    }

    private sealed class TwilioStart
    {
        [JsonPropertyName("streamSid")]
        public string StreamSid { get; init; } = "";

        [JsonPropertyName("callSid")]
        public string? CallSid { get; init; }

        [JsonPropertyName("customParameters")]
        public Dictionary<string, string>? CustomParameters { get; init; }
    }

    private sealed class TwilioMedia
    {
        [JsonPropertyName("payload")]
        public string? Payload { get; init; }
    }
}
