using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Coflnet.DiscordBot.Phone;

[ApiController]
[Route("api/twilio/voice")]
public sealed class TwilioVoiceController(
    IOptions<TwilioVoiceOptions> options,
    TwilioRequestValidator requestValidator,
    TwilioCallGate callGate,
    TwilioVoicemailService voicemail,
    DiscordHandler discord,
    TwilioMediaBridge mediaBridge) : ControllerBase
{
    private readonly TwilioVoiceOptions options = options.Value;

    [HttpPost("incoming")]
    public async Task<IActionResult> Incoming()
    {
        if (!options.Enabled)
            return NotFound();
        if (!await requestValidator.IsValidWebhookAsync(Request))
            return StatusCode(StatusCodes.Status403Forbidden);

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var callSid = form["CallSid"].ToString();
        var language = TwilioVoiceOptions.LanguageFromCaller(form["From"].ToString());
        var prompts = options.Prompts(language);
        var admission = await callGate.AdmitCallerAsync(form["From"].ToString(), callSid);
        if (admission != CallAdmission.Allowed)
            return Twiml(TwilioVoiceTwiml.Reject());

        if (!discord.IsUserInVoiceChannel(options.TargetUserId, options.VoiceChannelId))
            return await VoicemailAsync(form["From"].ToString(), language, MissedCallReason.TargetUnavailable);

        var continueUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/api/twilio/voice/continue"
            + $"?language={TwilioVoiceOptions.LanguageCode(language)}";
        return Twiml(TwilioVoiceTwiml.Prompt(continueUrl, prompts.PressOne));
    }

    [HttpPost("continue")]
    public async Task<IActionResult> Continue()
    {
        if (!options.Enabled)
            return NotFound();
        if (!await requestValidator.IsValidWebhookAsync(Request))
            return StatusCode(StatusCodes.Status403Forbidden);
        if (!TwilioVoiceOptions.TryParseLanguageCode(Request.Query["language"], out var language))
            return StatusCode(StatusCodes.Status403Forbidden);

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        if (form["Digits"] != "1")
            return Twiml(TwilioVoiceTwiml.Hangup());

        if (!discord.IsUserInVoiceChannel(options.TargetUserId, options.VoiceChannelId))
            return await VoicemailAsync(form["From"].ToString(), language, MissedCallReason.TargetUnavailable);

        var callSid = form["CallSid"].ToString();
        if (string.IsNullOrWhiteSpace(callSid))
            return Twiml(TwilioVoiceTwiml.PlayAndHangup(options.Prompts(language).Unavailable));
        if (!await callGate.TryReserveAsync(callSid))
            return await VoicemailAsync(form["From"].ToString(), language, MissedCallReason.LineBusy);

        mediaBridge.BeginHandoff(callSid, language);
        var afterUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/api/twilio/voice/after"
            + $"?language={TwilioVoiceOptions.LanguageCode(language)}";
        return Twiml(TwilioVoiceTwiml.Connect(
            options.MediaStreamUrl,
            callSid,
            callGate.CreateStreamToken(callSid),
            afterUrl,
            language));
    }

    [HttpPost("after")]
    public async Task<IActionResult> AfterStream()
    {
        if (!options.Enabled)
            return NotFound();
        if (!await requestValidator.IsValidWebhookAsync(Request))
            return StatusCode(StatusCodes.Status403Forbidden);
        if (!TwilioVoiceOptions.TryParseLanguageCode(Request.Query["language"], out var language))
            return StatusCode(StatusCodes.Status403Forbidden);

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        if (!await callGate.ConsumeHandoffUnavailableAsync(form["CallSid"].ToString()))
            return Twiml(TwilioVoiceTwiml.Hangup());

        return await VoicemailAsync(
            form["From"].ToString(),
            language,
            MissedCallReason.TargetUnavailable);
    }

    [HttpPost("voicemail/finished")]
    public async Task<IActionResult> VoicemailFinished()
    {
        if (!options.Enabled)
            return NotFound();
        if (!await requestValidator.IsValidWebhookAsync(Request))
            return StatusCode(StatusCodes.Status403Forbidden);
        if (!TwilioVoiceOptions.TryParseLanguageCode(Request.Query["language"], out var language))
            return StatusCode(StatusCodes.Status403Forbidden);

        return Twiml(TwilioVoiceTwiml.VoicemailFinished(language));
    }

    [HttpPost("voicemail/status")]
    public async Task<IActionResult> VoicemailStatus()
    {
        if (!options.Enabled)
            return NotFound();
        if (!await requestValidator.IsValidWebhookAsync(Request))
            return StatusCode(StatusCodes.Status403Forbidden);
        var callerReference = Request.Query["caller"].ToString();
        if (!int.TryParse(Request.Query["reason"], out var reasonValue)
            || !Enum.IsDefined(typeof(MissedCallReason), reasonValue)
            || callerReference.Length != 8
            || !callerReference.All(char.IsAsciiHexDigit))
            return StatusCode(StatusCodes.Status403Forbidden);

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        if (form["RecordingStatus"] == "completed")
        {
            _ = int.TryParse(form["RecordingDuration"], out var duration);
            await voicemail.StoreAsync(
                form["AccountSid"].ToString(),
                form["RecordingSid"].ToString(),
                callerReference,
                (MissedCallReason)reasonValue,
                duration);
        }
        return NoContent();
    }

    [HttpGet("media")]
    public async Task Media()
    {
        if (!options.Enabled
            || !HttpContext.WebSockets.IsWebSocketRequest
            || !requestValidator.IsValidMediaRequest(Request))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await mediaBridge.HandleAsync(HttpContext);
    }

    private async Task<IActionResult> VoicemailAsync(
        string caller,
        PhoneLanguage language,
        MissedCallReason reason)
    {
        await callGate.RecordMissedCallAsync(caller, reason);
        var baseUrl = options.PublicBaseUrl.TrimEnd('/');
        var languageCode = TwilioVoiceOptions.LanguageCode(language);
        var callerReference = callGate.CallerReference(caller);
        return Twiml(TwilioVoiceTwiml.Voicemail(
            $"{baseUrl}/api/twilio/voice/voicemail/finished?language={languageCode}",
            $"{baseUrl}/api/twilio/voice/voicemail/status?reason={(int)reason}&caller={callerReference}",
            options.Prompts(language).Unavailable,
            language,
            options.VoicemailMaxSeconds));
    }

    private ContentResult Twiml(string xml) => Content(xml, "application/xml");
}
