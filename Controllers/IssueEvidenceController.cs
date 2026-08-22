using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public sealed class IssueEvidenceController(IssueEvidenceService evidence, ILogger<IssueEvidenceController> logger) : ControllerBase
{
    [HttpPost("/internal/v1/agent/issue-evidence")]
    public async Task<IActionResult> Fetch(CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            if (Request.ContentLength is > (8 << 10)) return BadRequest(new { error = "invalid_bounded_request" });
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > (8 << 10)) return BadRequest(new { error = "invalid_bounded_request" });
                output.Write(buffer, 0, read);
            }
            if (output.Length < 1) return BadRequest(new { error = "invalid_bounded_request" });
            body = output.ToArray();
        }
        catch { return BadRequest(new { error = "invalid_bounded_request" }); }
        EvidenceRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EvidenceRequest>(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
        }
        catch { return BadRequest(new { error = "invalid_bounded_request" }); }
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
    public IActionResult Probe([FromBody] EvidenceRequest? request)
    {
        if (request == null || request.Schema != "coflnet.discord.issue-evidence-probe/v1"
            || request.Repository != "" || request.IssueNumber != 0 || request.Binding != "" || request.After != ""
            || !evidence.ValidateClientRequest(request, "/internal/v1/agent/issue-evidence/probe"))
        {
            logger.LogWarning("Denied Discord issue evidence key-match probe");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "request_authentication_denied" });
        }
        return Ok(new { schema = "coflnet.discord.issue-evidence-probe-response/v1", ok = true });
    }
}
