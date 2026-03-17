using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Invoice OCR analysis — calls Gemini 3 Flash via OCI, logs results, returns AlgoText-compatible fields.
/// </summary>
public class InvoiceService
{
    private readonly TripExDbContext _db;
    private readonly OracleAiService _aiService;

    private static readonly HashSet<string> ValidExpenseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "business_meal", "vehicle", "entertainment", "hotel",
        "internet", "parking", "other", "meal", "taxi"
    };

    public InvoiceService(TripExDbContext db, OracleAiService aiService)
    {
        _db = db;
        _aiService = aiService;
    }

    /// <summary>
    /// Main entry point — called by InvoiceController and ChatService
    /// </summary>
    public async Task<AnalyzeInvoiceResponse> AnalyzeAsync(string? imageBase64, string? imageUrl, string? country, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        var stopwatch = Stopwatch.StartNew();
        string? rawResponse = null;
        string status = "Failed";

        try
        {
            // Prepare image content
            var imageContent = imageBase64 ?? imageUrl!;
            if (!string.IsNullOrEmpty(imageBase64) && !imageBase64.StartsWith("data:"))
                imageContent = imageBase64;

            var countryHint = country?.ToUpperInvariant() ?? "PH";

            // ── Single Gemini call ──
            Console.WriteLine($"[OCR] Calling Gemini 3 Flash (country={countryHint})...");
            rawResponse = await _aiService.CallGeminiFlashAsync(imageContent, countryHint);
            Console.WriteLine($"[OCR] Raw response length: {rawResponse?.Length ?? 0}");

            // ── Parse AI JSON response ──
            var aiJson = OracleAiService.ParseJsonFromAiResponse(rawResponse);

            // ── Post-processing: validate amounts ──
            aiJson = ValidateAndFixAmounts(aiJson);

            // ── Post-processing: validate date by currency/country ──
            var detectedCurrency = aiJson.TryGetProperty("currency", out var curProp) ? curProp.GetString() : null;
            aiJson = ValidateDateByCurrency(aiJson, detectedCurrency, country);

            // ── Map to AlgoText-compatible flat fields ──
            var fields = MapToAlgoTextFields(aiJson);

            stopwatch.Stop();
            status = "Success";
            Console.WriteLine($"[OCR] Done in {stopwatch.ElapsedMilliseconds}ms | Total={fields.Total}, Currency={fields.Currency}, ExpenseType={fields.ExpenseType}, Merchant={fields.MerchantName}");

            // ── Save log ──
            await SaveScanLog(userId, rawResponse, countryHint, status);

            return new AnalyzeInvoiceResponse
            {
                Success = true,
                Fields = fields,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.WriteLine($"[OCR] FATAL ERROR: {ex.Message}");

            // Log even failed scans
            await SaveScanLog(userId, rawResponse, country, "Failed");

            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message };
        }
    }

    // ═══════════════════════════════════════
    // Logging — InvoiceScanLogs
    // ═══════════════════════════════════════

    private async Task SaveScanLog(Guid? userId, string? rawResponse, string? countryHint, string status)
    {
        try
        {
            var log = new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                RawAiResponse = rawResponse,
                CountryHint = countryHint,
                Status = status
            };
            _db.InvoiceScanLogs.Add(log);
            await _db.SaveChangesAsync();
            Console.WriteLine($"[OCR] Log saved: {log.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Failed to save log: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    // Amount Validation
    // ═══════════════════════════════════════

    private static JsonElement ValidateAndFixAmounts(JsonElement json)
    {
        if (!json.TryGetProperty("amounts", out var amounts)) return json;

        decimal? vatableSales = GetDecimalProp(amounts, "vatable_sales_amount");
        decimal? tax = GetDecimalProp(amounts, "tax_amount");
        decimal? amountPaid = null;

        if (json.TryGetProperty("payment", out var payment))
            amountPaid = GetDecimalProp(payment, "amount_paid");

        // Rule 1: If tax > subtotal, they are swapped
        if (tax.HasValue && vatableSales.HasValue && tax.Value > vatableSales.Value)
        {
            Console.WriteLine($"[OCR-VALIDATE] Tax ({tax}) > Subtotal ({vatableSales}) — SWAPPING");
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText()) ?? new();
            if (dict.ContainsKey("amounts"))
            {
                var amountsDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(dict["amounts"])) ?? new();
                amountsDict["vatable_sales_amount"] = tax.Value;
                amountsDict["tax_amount"] = vatableSales.Value;
                dict["amounts"] = amountsDict;
                var rebuilt = JsonSerializer.SerializeToUtf8Bytes(dict);
                return JsonDocument.Parse(rebuilt).RootElement.Clone();
            }
        }

        // Rule 2: If total exists and subtotal+tax don't add up, recalculate tax
        if (amountPaid.HasValue && vatableSales.HasValue && tax.HasValue)
        {
            var expectedTotal = vatableSales.Value + tax.Value;
            var tolerance = amountPaid.Value * 0.05m;
            if (Math.Abs(expectedTotal - amountPaid.Value) > tolerance && amountPaid.Value > 0)
            {
                var correctedTax = amountPaid.Value - vatableSales.Value;
                if (correctedTax >= 0)
                {
                    Console.WriteLine($"[OCR-VALIDATE] Subtotal+Tax ({expectedTotal}) != Total ({amountPaid}) — Recalculating tax to {correctedTax}");
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText()) ?? new();
                    if (dict.ContainsKey("amounts"))
                    {
                        var amountsDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(dict["amounts"])) ?? new();
                        amountsDict["tax_amount"] = correctedTax;
                        dict["amounts"] = amountsDict;
                        var rebuilt = JsonSerializer.SerializeToUtf8Bytes(dict);
                        return JsonDocument.Parse(rebuilt).RootElement.Clone();
                    }
                }
            }
        }

        return json;
    }

    private static decimal? GetDecimalProp(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number) return val.GetDecimal();
        if (val.ValueKind == JsonValueKind.String)
        {
            var str = val.GetString();
            if (!string.IsNullOrEmpty(str))
            {
                str = Regex.Replace(str, @"[₱₪$€£¥,\s]", "");
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════
    // Date Validation
    // ═══════════════════════════════════════

    private static JsonElement ValidateDateByCurrency(JsonElement json, string? currency, string? country)
    {
        if (!json.TryGetProperty("invoice_date", out var dateProp)) return json;
        var dateStr = dateProp.GetString();
        if (string.IsNullOrEmpty(dateStr)) return json;

        var effectiveCurrency = currency?.ToUpperInvariant();
        var effectiveCountry = country?.ToUpperInvariant();

        string? normalizedDate = TryNormalizeDate(dateStr, effectiveCurrency, effectiveCountry);

        if (normalizedDate != null && normalizedDate != dateStr)
        {
            Console.WriteLine($"[OCR-DATE] Normalized: {dateStr} → {normalizedDate}");
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText()) ?? new();
            dict["invoice_date"] = normalizedDate;
            var rebuilt = JsonSerializer.SerializeToUtf8Bytes(dict);
            return JsonDocument.Parse(rebuilt).RootElement.Clone();
        }

        return json;
    }

    private static string? TryNormalizeDate(string dateStr, string? currency, string? country)
    {
        dateStr = dateStr.Trim();

        // YYYY-MM-DD or YYYY/MM/DD
        var matchYMD = Regex.Match(dateStr, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})");
        if (matchYMD.Success)
        {
            int year = int.Parse(matchYMD.Groups[1].Value);
            int g2 = int.Parse(matchYMD.Groups[2].Value);
            int g3 = int.Parse(matchYMD.Groups[3].Value);

            if (g2 <= 12 && g3 >= 1 && g3 <= 31)
            {
                var result = $"{year:D4}-{g2:D2}-{g3:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
            if (g2 > 12 && g3 <= 12)
            {
                var result = $"{year:D4}-{g3:D2}-{g2:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
        }

        // DD/MM/YYYY or MM/DD/YYYY
        var matchDMY = Regex.Match(dateStr, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{4})");
        if (matchDMY.Success)
        {
            int g1 = int.Parse(matchDMY.Groups[1].Value);
            int g2 = int.Parse(matchDMY.Groups[2].Value);
            int year = int.Parse(matchDMY.Groups[3].Value);

            if (g1 > 12 && g2 <= 12)
                return TryBuildDate(year, g2, g1);
            if (g2 > 12 && g1 <= 12)
                return TryBuildDate(year, g1, g2);

            // Ambiguous — use country hint
            if (g1 <= 12 && g2 <= 12)
            {
                bool useDDMM = currency == "ILS" || country == "IL";
                if (useDDMM)
                    return TryBuildDate(year, g2, g1); // DD/MM
                else
                    return TryBuildDate(year, g1, g2); // MM/DD
            }
        }

        // DD/MM/YY or MM/DD/YY
        var matchShort = Regex.Match(dateStr, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{2})$");
        if (matchShort.Success)
        {
            int year = 2000 + int.Parse(matchShort.Groups[3].Value);
            return TryNormalizeDate($"{matchShort.Groups[1].Value}/{matchShort.Groups[2].Value}/{year}", currency, country);
        }

        return null;
    }

    private static string? TryBuildDate(int year, int month, int day)
    {
        var result = $"{year:D4}-{month:D2}-{day:D2}";
        if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return result;
        return null;
    }

    // ═══════════════════════════════════════
    // Field Mapping (AlgoText-compatible)
    // ═══════════════════════════════════════

    private static InvoiceFields MapToAlgoTextFields(JsonElement json)
    {
        var fields = new InvoiceFields();

        // Total = payment.amount_paid or sum of amounts
        decimal? totalValue = null;

        if (json.TryGetProperty("payment", out var payment))
            totalValue = GetDecimalProp(payment, "amount_paid");

        // Fallback: calculate from amounts
        if (!totalValue.HasValue || totalValue.Value == 0)
        {
            if (json.TryGetProperty("amounts", out var amounts))
            {
                decimal total = 0;
                foreach (var prop in new[] { "vatable_sales_amount", "non_vatable_sales_amount", "service_charge_amount", "tax_amount" })
                {
                    var val = GetDecimalProp(amounts, prop);
                    if (val.HasValue) total += val.Value;
                }
                if (total > 0) totalValue = total;
            }
        }

        // Fallback: top-level total
        if (!totalValue.HasValue || totalValue.Value == 0)
            totalValue = GetDecimalProp(json, "total") ?? GetDecimalProp(json, "total_amount") ?? GetDecimalProp(json, "amount");

        if (totalValue.HasValue)
        {
            fields.Total = totalValue.Value.ToString("F2");
            fields.TotalAmount = totalValue.Value.ToString("F2");
        }

        // VAT
        if (json.TryGetProperty("amounts", out var amt))
        {
            var taxVal = GetDecimalProp(amt, "tax_amount");
            if (taxVal.HasValue) fields.TotalVAT = taxVal.Value.ToString("F2");

            var vatableVal = GetDecimalProp(amt, "vatable_sales_amount");
            if (vatableVal.HasValue) fields.SubCategory = vatableVal.Value.ToString("F2");
        }

        // Direct fields
        fields.Currency = GetStringProp(json, "currency");
        fields.InvoiceNumber = GetStringProp(json, "invoice_number");
        fields.InvoiceDate = GetStringProp(json, "invoice_date");
        fields.Type = GetStringProp(json, "document_type");

        // Expense Type
        var expType = GetStringProp(json, "expense_type") ?? GetStringProp(json, "category") ?? "other";
        expType = expType.Trim().ToLowerInvariant().Replace(" ", "_");
        fields.ExpenseType = ValidExpenseTypes.Contains(expType) ? expType : "other";

        // Merchant
        if (json.TryGetProperty("merchant", out var merchant))
        {
            fields.MerchantName = CleanMerchantName(GetStringProp(merchant, "name"));
            fields.MerchantTin = GetStringProp(merchant, "tin");
            fields.MerchantAddress = GetStringProp(merchant, "address");
            fields.MerchantCity = GetStringProp(merchant, "city");
        }
        // Fallback: top-level vendor
        if (string.IsNullOrEmpty(fields.MerchantName))
            fields.MerchantName = CleanMerchantName(GetStringProp(json, "vendor"));

        // Payment
        if (json.TryGetProperty("payment", out var pay))
        {
            fields.PaymentMethod = GetStringProp(pay, "method");
            var paidVal = GetDecimalProp(pay, "amount_paid");
            if (paidVal.HasValue) fields.AmountPaid = paidVal.Value.ToString("F2");
        }

        // Extra Details — full raw JSON
        fields.ExtraDetails = json.GetRawText();

        return fields;
    }

    private static string? GetStringProp(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.ValueKind != JsonValueKind.Null && val.ValueKind != JsonValueKind.Undefined)
                return val.GetRawText().Trim('"');
        }
        return null;
    }

    private static string? CleanMerchantName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var cleaned = Regex.Replace(name, @"[\r\n\t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim();
    }
}
