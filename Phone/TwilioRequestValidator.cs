using Microsoft.Extensions.Options;
using Twilio.Security;

namespace Coflnet.DiscordBot.Phone;

public sealed class TwilioRequestValidator(IOptions<TwilioVoiceOptions> options)
{
    private readonly TwilioVoiceOptions options = options.Value;

    public async Task<bool> IsValidWebhookAsync(HttpRequest request)
    {
        var fields = request.HasFormContentType
            ? (await request.ReadFormAsync(request.HttpContext.RequestAborted))
                .ToDictionary(field => field.Key, field => field.Value.ToString())
            : [];

        return IsValid(request, WebhookUrl(request), fields);
    }

    public bool IsValidMediaRequest(HttpRequest request)
    {
        return IsValid(request, options.MediaStreamUrl, new Dictionary<string, string>());
    }

    private bool IsValid(HttpRequest request, string url, IReadOnlyDictionary<string, string> fields)
    {
        if (!request.Headers.TryGetValue("X-Twilio-Signature", out var signature)
            || string.IsNullOrWhiteSpace(signature))
            return false;

        return new RequestValidator(options.AuthToken)
            .Validate(url, fields.ToDictionary(), signature.ToString());
    }

    private string WebhookUrl(HttpRequest request)
    {
        return $"{options.PublicBaseUrl.TrimEnd('/')}{request.PathBase}{request.Path}{request.QueryString}";
    }
}
