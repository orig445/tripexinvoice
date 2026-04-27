using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

[ApiController]
[Route("api/invoice")]
[Authorize]
public class InvoiceController : ControllerBase
{
    private readonly InvoiceService _invoiceService;

    public InvoiceController(InvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    /// <summary>
    /// Direct invoice analysis (standalone OCR without chat context)
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult<AnalyzeInvoiceResponse>> Analyze([FromBody] AnalyzeInvoiceRequest request)
    {
        try
        {
            Guid? userId = null;
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdClaim, out var uid)) userId = uid;

            var result = await _invoiceService.AnalyzeAsync(request.ImageBase64, request.ImageUrl, request.Country, userId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invoice analysis error: {ex}");
            return Ok(new AnalyzeInvoiceResponse { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Bulk train: scan a receipt and save as training sample
    /// </summary>
    [HttpPost("bulk-train")]
    public async Task<ActionResult<BulkTrainResponse>> BulkTrain([FromBody] BulkTrainRequest request)
    {
        try
        {
            Guid? userId = null;
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdClaim, out var uid)) userId = uid;

            var result = await _invoiceService.BulkTrainAsync(request.ImageBase64, request.Country, userId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bulk train error: {ex}");
            return Ok(new BulkTrainResponse { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Verify or reject a training sample
    /// </summary>
    [HttpPost("verify-sample")]
    public async Task<ActionResult> VerifySample([FromBody] VerifyTrainingSampleRequest request)
    {
        var success = await _invoiceService.VerifyTrainingSampleAsync(
            request.SampleId, request.IsCorrect, request.Corrections);
        return Ok(new { success });
    }

    /// <summary>
    /// Rebuild patterns from verified samples
    /// </summary>
    [HttpPost("rebuild-patterns")]
    public async Task<ActionResult<RebuildPatternsResponse>> RebuildPatterns()
    {
        var result = await _invoiceService.RebuildPatternsAsync();
        return Ok(result);
    }

    /// <summary>
    /// Get training statistics
    /// </summary>
    [HttpGet("training-stats")]
    public async Task<ActionResult<TrainingStatsResponse>> GetTrainingStats()
    {
        var stats = await _invoiceService.GetTrainingStatsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Get recent OCR scan logs for the authenticated user. Pass ?limit=N.
    /// </summary>
    [HttpGet("logs")]
    public async Task<ActionResult> GetRecentLogs([FromQuery] int limit = 50)
    {
        // Always scope to the authenticated user — never expose other users' scan data
        Guid? userId = null;
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var uid)) userId = uid;
        var logs = await _invoiceService.GetRecentLogsAsync(limit, userId);
        return Ok(new
        {
            success = true,
            count = logs.Count,
            logs = logs.Select(l => new
            {
                l.Id,
                l.CreatedAt,
                l.UserId,
                l.Source,
                l.Status,
                l.HttpStatusCode,
                l.DurationMs,
                l.AttemptNumber,
                l.CountryHint,
                l.ImageSizeBytes,
                l.ErrorType,
                l.ErrorMessage,
                OciResponseBodyPreview = l.OciResponseBody?.Length > 500
                    ? l.OciResponseBody.Substring(0, 500) + "..."
                    : l.OciResponseBody,
                RawAiResponsePreview = l.RawAiResponse?.Length > 500
                    ? l.RawAiResponse.Substring(0, 500) + "..."
                    : l.RawAiResponse
            })
        });
    }
}
