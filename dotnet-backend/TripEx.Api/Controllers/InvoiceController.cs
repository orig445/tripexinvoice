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
    /// Direct invoice analysis (standalone OCR without chat context).
    /// Returns fields with multiple naming conventions (PascalCase + camelCase + snake_case)
    /// to ensure compatibility with any client mapping (AlgoText / TripEx / QA combtas).
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult> Analyze([FromBody] AnalyzeInvoiceRequest request)
    {
        try
        {
            Guid? userId = null;
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdClaim, out var uid)) userId = uid;

            var result = await _invoiceService.AnalyzeAsync(request.ImageBase64, request.ImageUrl, request.Country, userId);
            var fieldsDict = BuildMultiCaseFields(result.Fields);

            return Ok(new
            {
                success = result.Success,
                Success = result.Success,
                error = result.Error,
                Error = result.Error,
                rawResponse = result.RawResponse,
                RawResponse = result.RawResponse,
                fields = fieldsDict,
                Fields = fieldsDict
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invoice analysis error: {ex}");
            return Ok(new { success = false, Success = false, error = ex.Message, Error = ex.Message });
        }
    }

    /// <summary>
    /// Build a dictionary that contains every field name in multiple casings:
    /// PascalCase + camelCase + snake_case + common AlgoText aliases.
    /// This guarantees the QA client finds the value regardless of naming.
    /// </summary>
    private static Dictionary<string, object?> BuildMultiCaseFields(InvoiceFields? f)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (f == null) return d;

        void Set(object? value, params string[] keys)
        {
            foreach (var k in keys) d[k] = value;
        }

        Set(f.Total, "Total", "total");
        Set(f.TotalAmount, "TotalAmount", "totalAmount", "total_amount");
        Set(f.TotalVAT, "TotalVAT", "totalVAT", "totalVat", "total_vat", "VAT", "vat", "Tax", "tax");
        Set(f.Currency, "Currency", "currency", "currency_code", "currencyCode");
        Set(f.InvoiceNumber, "InvoiceNumber", "invoiceNumber", "invoice_number",
            "ReceiptNumber", "receiptNumber", "receipt_number",
            "DocumentNumber", "documentNumber", "document_number");
        Set(f.InvoiceDate, "InvoiceDate", "invoiceDate", "invoice_date", "Date", "date");
        Set(f.Type, "Type", "type", "DocumentType", "documentType", "document_type");
        Set(f.SubCategory, "SubCategory", "subCategory", "sub_category", "Subtotal", "subtotal", "sub_total");
        Set(f.MerchantName, "MerchantName", "merchantName", "merchant_name",
            "Vendor", "vendor", "VendorName", "vendorName", "vendor_name");
        Set(f.MerchantTin, "MerchantTin", "merchantTin", "merchant_tin",
            "TIN", "Tin", "tin", "VendorId", "vendorId", "vendor_id");
        Set(f.MerchantAddress, "MerchantAddress", "merchantAddress", "merchant_address",
            "Address", "address", "vendor_address");
        Set(f.MerchantCity, "MerchantCity", "merchantCity", "merchant_city", "City", "city");
        Set(f.PaymentMethod, "PaymentMethod", "paymentMethod", "payment_method");
        Set(f.AmountPaid, "AmountPaid", "amountPaid", "amount_paid");
        Set(f.ExpenseType, "ExpenseType", "expenseType", "expense_type", "Category", "category");
        Set(f.FormOfPayment, "FormOfPayment", "formOfPayment", "form_of_payment");
        Set(f.CardLast4, "CardLast4", "cardLast4", "card_last4");
        Set(f.CardType, "CardType", "cardType", "card_type");
        Set(f.ExtraDetails, "ExtraDetails", "extraDetails", "extra_details");

        return d;
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
    /// Get recent OCR scan logs (admin/debug). Pass ?limit=N&onlyMine=true.
    /// </summary>
    [HttpGet("logs")]
    public async Task<ActionResult> GetRecentLogs([FromQuery] int limit = 50, [FromQuery] bool onlyMine = false)
    {
        Guid? userId = null;
        if (onlyMine)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(userIdClaim, out var uid)) userId = uid;
        }
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
