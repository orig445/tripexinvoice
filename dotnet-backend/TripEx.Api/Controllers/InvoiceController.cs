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
}
