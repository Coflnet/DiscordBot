using Microsoft.Extensions.Options;
using Twilio.Security;

namespace Coflnet.DiscordBot.Phone;

public sealed class TwilioRequestValidator(
    IOptionsMonitor<TwilioVoiceOptions> options,
    ILogger<TwilioRequestValidator> logger)
{
    public async Task<bool> IsValidWebhookAsync(HttpRequest request)
    {
        var fields = request.HasFormContentType
            ? (await request.ReadFormAsync(request.HttpContext.RequestAborted))
                .ToDictionary(field => field.Key, field => field.Value.ToString())
            : [];

        var current = options.CurrentValue;
        var url = $"{current.PublicBaseUrl.TrimEnd('/')}{request.PathBase}{request.Path}{request.QueryString}";
        return IsValid(request, url, fields, current.AuthToken);
    }

    public bool IsValidMediaRequest(HttpRequest request)
    {
        var current = options.CurrentValue;
        return IsValid(request, current.MediaStreamUrl, new Dictionary<string, string>(), current.AuthToken);
    }

    private bool IsValid(
        HttpRequest request,
        string url,
        IReadOnlyDictionary<string, string> fields,
        string authToken)
    {
        if (!request.Headers.TryGetValue("X-Twilio-Signature", out var signature)
            || string.IsNullOrWhiteSpace(signature))
        {
            logger.LogWarning("Twilio request is missing its signature header");
            return false;
        }

        var isValid = new RequestValidator(authToken)
            .Validate(url, fields.ToDictionary(), signature.ToString());
        if (!isValid)
            logger.LogWarning("Twilio signature validation failed for {Path}", request.Path.Value);
        return isValid;
    }
}
