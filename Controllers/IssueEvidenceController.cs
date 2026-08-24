using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public sealed class IssueEvidenceController(IssueEvidenceService evidence, ILogger<IssueEvidenceController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("/internal/v1/agent/issue-evidence")]
    public async Task<IActionResult> Fetch(CancellationToken cancellationToken)
    {
        var request = await ReadRequest(cancellationToken);
        if (request == null || request.Schema != "coflnet.discord.issue-evidence-request/v1")
            return BadRequest(new { error = "invalid_bounded_request" });
        if (!evidence.ValidateClientRequest(request))
        {
            logger.LogWarning("Denied unauthenticated Discord issue evidence request");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "request_authentication_denied" });
        }
        try { return Ok(await evidence.Fetch(request, cancellationToken)); }
        catch (EvidenceDenied denied)
        {
            logger.LogWarning("Denied Discord issue evidence request: {Reason}", denied.Reason);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = denied.Reason });
        }
        catch (Exception error)
        {
            logger.LogError("Discord issue evidence retrieval failed: {Category}", error is HttpRequestException ? "attachment_transport" : "internal_unavailable");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "evidence_unavailable" });
        }
    }

    [HttpPost("/internal/v1/agent/issue-evidence/probe")]
    [RequestSizeLimit(8 << 10)]
    public async Task<IActionResult> Probe(CancellationToken cancellationToken)
    {
        var request = await ReadRequest(cancellationToken);
        if (request == null || request.Schema != "coflnet.discord.issue-evidence-probe/v1"
            || request.Repository != "" || request.IssueNumber != 0 || request.Binding != "" || request.After != ""
            || !evidence.ValidateClientRequest(request, "/internal/v1/agent/issue-evidence/probe"))
        {
            logger.LogWarning("Denied Discord issue evidence key-match probe");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "request_authentication_denied" });
        }
        return Ok(new { schema = "coflnet.discord.issue-evidence-probe-response/v1", ok = true });
    }

    private async Task<EvidenceRequest?> ReadRequest(CancellationToken cancellationToken)
    {
        try
        {
            if (Request.ContentLength is > (8 << 10)) return null;
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > (8 << 10)) return null;
                output.Write(buffer, 0, read);
            }
            return output.Length == 0 ? null : JsonSerializer.Deserialize<EvidenceRequest>(output.ToArray(), RequestJsonOptions);
        }
        catch { return null; }
    }
}
