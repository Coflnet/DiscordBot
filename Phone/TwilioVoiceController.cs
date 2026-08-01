using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Coflnet.DiscordBot.Phone;

[ApiController]
[Route("api/twilio/voice")]
public sealed class TwilioVoiceController(
    IOptions<TwilioVoiceOptions> options,
    TwilioRequestValidator requestValidator,
    TwilioCallGate callGate,
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
            return Twiml(TwilioVoiceTwiml.PlayAndHangup(prompts.Unavailable));

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
            return Twiml(TwilioVoiceTwiml.PlayAndHangup(options.Prompts(language).Unavailable));

        var callSid = form["CallSid"].ToString();
        if (string.IsNullOrWhiteSpace(callSid) || !await callGate.TryReserveAsync(callSid))
            return Twiml(TwilioVoiceTwiml.PlayAndHangup(options.Prompts(language).Unavailable));

        return Twiml(TwilioVoiceTwiml.Connect(
            options.MediaStreamUrl,
            callSid,
            callGate.CreateStreamToken(callSid),
            TwilioVoiceOptions.LanguageCode(language)));
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

    private ContentResult Twiml(string xml) => Content(xml, "application/xml");
}
