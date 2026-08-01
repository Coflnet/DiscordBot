using Microsoft.Extensions.Options;
using Twilio.Security;

namespace Coflnet.DiscordBot.Phone;

public sealed class TwilioRequestValidator(
    IOptions<TwilioVoiceOptions> options,
    ILogger<TwilioRequestValidator> logger)
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
        {
            logger.LogWarning("Twilio request is missing its signature header");
            return false;
        }

        var validator = new RequestValidator(options.AuthToken);
        var parameters = fields.ToDictionary();
        if (validator.Validate(url, parameters, signature.ToString()))
            return true;

        var requestUrl = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        var requestUrlDiffers = !string.Equals(url, requestUrl, StringComparison.Ordinal);
        var requestUrlMatches = requestUrlDiffers
            && validator.Validate(requestUrl, parameters, signature.ToString());

        var decodedCallTokenMatches = false;
        if (fields.TryGetValue("CallToken", out var callToken))
        {
            var decodedCallToken = System.Net.WebUtility.UrlDecode(callToken);
            if (!string.Equals(callToken, decodedCallToken, StringComparison.Ordinal))
            {
                parameters["CallToken"] = decodedCallToken;
                decodedCallTokenMatches = validator.Validate(url, parameters, signature.ToString());
            }
        }

        parameters.Remove("CallToken");
        var omittedCallTokenMatches = parameters.Count != fields.Count
            && validator.Validate(url, parameters, signature.ToString());
        logger.LogWarning(
            "Twilio signature validation failed for {Path} with {FieldCount} form fields. "
            + "Request URL differs: {RequestUrlDiffers}; request URL matches: {RequestUrlMatches}; "
            + "decoded CallToken matches: {DecodedCallTokenMatches}; omitted CallToken matches: {OmittedCallTokenMatches}",
            request.Path.Value,
            fields.Count,
            requestUrlDiffers,
            requestUrlMatches,
            decodedCallTokenMatches,
            omittedCallTokenMatches);
        return false;
    }

    private string WebhookUrl(HttpRequest request)
    {
        return $"{options.PublicBaseUrl.TrimEnd('/')}{request.PathBase}{request.Path}{request.QueryString}";
    }
}
