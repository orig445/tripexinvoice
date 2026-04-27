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
        return await AnalyzeInternalAsync(imageBase64, imageUrl, country, userId, source: "analyze");
    }

    /// <summary>
    /// Internal scan with retry + detailed logging. Used by both AnalyzeAsync and BulkTrainAsync.
    /// </summary>
    private async Task<AnalyzeInvoiceResponse> AnalyzeInternalAsync(
        string? imageBase64, string? imageUrl, string? country, Guid? userId, string source)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        var stopwatch = Stopwatch.StartNew();
        string? rawResponse = null;
        var countryHint = country?.ToUpperInvariant() ?? "PH";
        var imageSize = imageBase64?.Length ?? 0;

        const int maxAttempts = 3;
        Exception? lastError = null;
        int? lastHttpStatus = null;
        string? lastOciBody = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var imageContent = imageBase64 ?? imageUrl!;

                Console.WriteLine($"[OCR][{source}] Attempt {attempt}/{maxAttempts} | country={countryHint} | imgSize={imageSize}");
                rawResponse = await _aiService.CallGeminiFlashAsync(imageContent, countryHint);
                Console.WriteLine($"[OCR][{source}] Raw response length: {rawResponse?.Length ?? 0}");

                var aiJson = OracleAiService.ParseJsonFromAiResponse(rawResponse!);
                aiJson = ValidateAndFixAmounts(aiJson);
                var detectedCurrency = aiJson.TryGetProperty("currency", out var curProp) ? curProp.GetString() : null;
                aiJson = ValidateDateByCurrency(aiJson, detectedCurrency, country);
                var fields = MapToAlgoTextFields(aiJson);

                stopwatch.Stop();

                // ── Sanity check: detect "empty result" (the classic 0-amount bug) ──
                bool isEmptyResult = (string.IsNullOrEmpty(fields.Total) || fields.Total == "0.00" || fields.Total == "0")
                                     && string.IsNullOrEmpty(fields.MerchantName)
                                     && string.IsNullOrEmpty(fields.InvoiceNumber);

                if (isEmptyResult)
                {
                    if (attempt < maxAttempts)
                    {
                        // OCI returned 200 but Gemini gave us garbage — retry
                        Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt}: OCI 200 but EMPTY result (Total=0, no merchant). Retrying...");
                        await SaveScanLog(new InvoiceScanLog
                        {
                            UserId = userId ?? Guid.Empty,
                            RawAiResponse = rawResponse,
                            CountryHint = countryHint,
                            Status = "EmptyResult",
                            DurationMs = stopwatch.ElapsedMilliseconds,
                            HttpStatusCode = 200,
                            ErrorMessage = "OCI returned success but extracted no data (Total=0, no merchant)",
                            ErrorType = "EmptyExtraction",
                            Source = source,
                            AttemptNumber = attempt,
                            ImageSizeBytes = imageSize
                        });
                        await Task.Delay(500);
                        stopwatch.Restart();
                        continue;
                    }
                    // All retries exhausted — hard failure, never return null fields silently
                    lastError = new InvalidOperationException(
                        $"OCR extracted no data after {maxAttempts} attempts (Total=0, no merchant, no invoice number). Image may be unreadable or unsupported.");
                    lastHttpStatus = 200;
                    break;
                }

                Console.WriteLine($"[OCR][{source}] OK in {stopwatch.ElapsedMilliseconds}ms | attempt={attempt} | Total={fields.Total} {fields.Currency} | Merchant={fields.MerchantName}");

                await SaveScanLog(new InvoiceScanLog
                {
                    UserId = userId ?? Guid.Empty,
                    RawAiResponse = rawResponse,
                    CountryHint = countryHint,
                    Status = "Success",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    HttpStatusCode = 200,
                    Source = source,
                    AttemptNumber = attempt,
                    ImageSizeBytes = imageSize
                });

                return new AnalyzeInvoiceResponse { Success = true, Fields = fields, RawResponse = rawResponse };
            }
            catch (OciApiException ex)
            {
                lastError = ex;
                lastHttpStatus = ex.StatusCode;
                lastOciBody = ex.ResponseBody;
                Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt} OCI error {ex.StatusCode}: {ex.Message}");

                // Retry only on rate-limit (429) or 5xx
                bool retriable = ex.StatusCode == 429 || ex.StatusCode >= 500;
                if (!retriable || attempt == maxAttempts) break;

                int delayMs = (int)Math.Pow(2, attempt) * 500; // 1s, 2s
                Console.WriteLine($"[OCR][{source}] Retriable, waiting {delayMs}ms before retry...");
                await Task.Delay(delayMs);
                stopwatch.Restart();
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt} network error: {ex.Message}");
                if (attempt == maxAttempts) break;
                await Task.Delay((int)Math.Pow(2, attempt) * 500);
                stopwatch.Restart();
            }
            catch (Exception ex)
            {
                // Non-retriable (parse error, validation, etc.) — fail immediately
                lastError = ex;
                Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt} non-retriable error: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        stopwatch.Stop();
        var errMsg = lastError?.Message ?? "Unknown error";
        Console.Error.WriteLine($"[OCR][{source}] FAILED after retries in {stopwatch.ElapsedMilliseconds}ms: {errMsg}");

        await SaveScanLog(new InvoiceScanLog
        {
            UserId = userId ?? Guid.Empty,
            RawAiResponse = rawResponse,
            CountryHint = countryHint,
            Status = "Failed",
            DurationMs = stopwatch.ElapsedMilliseconds,
            HttpStatusCode = lastHttpStatus,
            ErrorMessage = errMsg,
            ErrorType = lastError?.GetType().Name,
            OciResponseBody = lastOciBody,
            Source = source,
            AttemptNumber = maxAttempts,
            ImageSizeBytes = imageSize
        });

        return new AnalyzeInvoiceResponse { Success = false, Error = errMsg };
    }

    // ═══════════════════════════════════════
    // Logging — InvoiceScanLogs
    // ═══════════════════════════════════════

    private async Task SaveScanLog(InvoiceScanLog log)
    {
        try
        {
            _db.InvoiceScanLogs.Add(log);
            await _db.SaveChangesAsync();
            Console.WriteLine($"[OCR-LOG] Saved {log.Id} | status={log.Status} | http={log.HttpStatusCode} | duration={log.DurationMs}ms | source={log.Source}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OCR-LOG] Failed to save log: {ex.Message}");
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

            // Form of payment (credit/cash/bank)
            var formOfPayment = GetStringProp(pay, "form_of_payment")?.ToLowerInvariant()?.Trim();
            if (formOfPayment == "credit" || formOfPayment == "cash" || formOfPayment == "bank")
                fields.FormOfPayment = formOfPayment;
            else
            {
                // Infer from method field
                var method = (fields.PaymentMethod ?? "").ToLowerInvariant();
                if (method.Contains("credit") || method.Contains("card") || method.Contains("visa") || method.Contains("master") ||
                    method.Contains("amex") || method.Contains("emv") || method.Contains("contactless") || 
                    method.Contains("אשראי") || method.Contains("כרטיס") || method.Contains("סליקה") || method.Contains("סליקת"))
                    fields.FormOfPayment = "credit";
                else if (method.Contains("bank") || method.Contains("transfer") || method.Contains("העברה"))
                    fields.FormOfPayment = "bank";
                else
                    fields.FormOfPayment = "cash"; // default
            }

            // Card details (only relevant for credit)
            if (fields.FormOfPayment == "credit")
            {
                fields.CardLast4 = GetStringProp(pay, "card_last4");
                fields.CardType = GetStringProp(pay, "card_type")?.ToLowerInvariant();

                // Try to extract last 4 from method string if not found
                if (string.IsNullOrEmpty(fields.CardLast4) && !string.IsNullOrEmpty(fields.PaymentMethod))
                {
                    var match = Regex.Match(fields.PaymentMethod, @"(\d{4})\s*$");
                    if (!match.Success) match = Regex.Match(fields.PaymentMethod, @"[*xX]+[-\s]?(\d{4})");
                    if (match.Success) fields.CardLast4 = match.Groups[1].Value;
                }
            }
        }
        else
        {
            fields.FormOfPayment = "cash"; // default when no payment info
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

    // ═══════════════════════════════════════
    // OCR Training — Bulk Train
    // ═══════════════════════════════════════

    /// <summary>
    /// Process a single receipt for training: scan → save as training sample
    /// </summary>
    public async Task<BulkTrainResponse> BulkTrainAsync(string? imageBase64, string? country, Guid? userId)
    {
        try
        {
            if (string.IsNullOrEmpty(imageBase64))
                return new BulkTrainResponse { Success = false, Error = "imageBase64 is required" };

            var countryHint = country?.ToUpperInvariant() ?? "IL";

            // Reuse main scan path (with retry + logging)
            var scanResult = await AnalyzeInternalAsync(imageBase64, null, country, userId, source: "bulk-train");
            if (!scanResult.Success || scanResult.Fields == null)
            {
                Console.Error.WriteLine($"[OCR-TRAIN] Scan failed: {scanResult.Error}");
                return new BulkTrainResponse { Success = false, Error = scanResult.Error };
            }

            var fields = scanResult.Fields;
            JsonElement aiJson;
            try { aiJson = JsonDocument.Parse(scanResult.RawResponse ?? "{}").RootElement; }
            catch { aiJson = JsonDocument.Parse("{}").RootElement; }

            var sample = new OcrTrainingSample
            {
                VendorName = fields.MerchantName,
                Country = countryHint,
                Currency = fields.Currency,
                DocumentType = fields.Type,
                ExtractedFields = JsonSerializer.Serialize(fields),
                FieldPositions = BuildFieldPositions(aiJson),
                IsVerified = false,
                IsRejected = false
            };

            _db.OcrTrainingSamples.Add(sample);
            await _db.SaveChangesAsync();

            Console.WriteLine($"[OCR-TRAIN] Sample saved: {sample.Id} | Vendor={fields.MerchantName}");

            return new BulkTrainResponse { Success = true, SampleId = sample.Id, Fields = fields };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OCR-TRAIN] Error: {ex.GetType().Name}: {ex.Message}");
            return new BulkTrainResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get most recent scan logs (for debugging/admin UI)
    /// </summary>
    public async Task<List<InvoiceScanLog>> GetRecentLogsAsync(int limit = 50, Guid? userId = null)
    {
        var query = _db.InvoiceScanLogs.AsNoTracking().AsQueryable();
        if (userId.HasValue && userId.Value != Guid.Empty)
            query = query.Where(l => l.UserId == userId.Value);
        return await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();
    }

    /// <summary>
    /// Mark a training sample as verified or rejected
    /// </summary>
    public async Task<bool> VerifyTrainingSampleAsync(Guid sampleId, bool isCorrect, Dictionary<string, string>? corrections)
    {
        var sample = await _db.OcrTrainingSamples.FindAsync(sampleId);
        if (sample == null) return false;

        if (isCorrect)
        {
            sample.IsVerified = true;
            sample.IsRejected = false;
            if (corrections != null && corrections.Count > 0)
                sample.Corrections = JsonSerializer.Serialize(corrections);
        }
        else
        {
            sample.IsVerified = false;
            sample.IsRejected = true;
        }

        await _db.SaveChangesAsync();
        Console.WriteLine($"[OCR-TRAIN] Sample {sampleId} → verified={isCorrect}");
        return true;
    }

    /// <summary>
    /// Rebuild patterns from all verified training samples
    /// </summary>
    public async Task<RebuildPatternsResponse> RebuildPatternsAsync()
    {
        try
        {
            var verifiedSamples = await _db.OcrTrainingSamples
                .Where(s => s.IsVerified && !s.IsRejected)
                .ToListAsync();

            if (verifiedSamples.Count < 3)
                return new RebuildPatternsResponse { Success = false, Error = "Need at least 3 verified samples to build patterns" };

            // Group by country to analyze patterns per region
            var byCountry = verifiedSamples.GroupBy(s => s.Country ?? "UNKNOWN").ToList();

            // Clear existing patterns
            _db.OcrTrainingPatterns.RemoveRange(_db.OcrTrainingPatterns);

            var patternsCreated = 0;

            foreach (var group in byCountry)
            {
                var countryCode = group.Key;
                var samples = group.ToList();
                var sampleCount = samples.Count;

                // Analyze payment patterns
                var paymentPatterns = AnalyzePaymentPatterns(samples);
                foreach (var pattern in paymentPatterns)
                {
                    _db.OcrTrainingPatterns.Add(new OcrTrainingPattern
                    {
                        FieldName = "payment",
                        PatternRule = pattern.Rule,
                        Country = countryCode == "UNKNOWN" ? null : countryCode,
                        Confidence = pattern.Confidence,
                        SourceCount = pattern.Count
                    });
                    patternsCreated++;
                }

                // Analyze date format patterns
                var datePatterns = AnalyzeDatePatterns(samples);
                foreach (var pattern in datePatterns)
                {
                    _db.OcrTrainingPatterns.Add(new OcrTrainingPattern
                    {
                        FieldName = "date",
                        PatternRule = pattern.Rule,
                        Country = countryCode == "UNKNOWN" ? null : countryCode,
                        Confidence = pattern.Confidence,
                        SourceCount = pattern.Count
                    });
                    patternsCreated++;
                }

                // Analyze amount patterns
                var amountPatterns = AnalyzeAmountPatterns(samples);
                foreach (var pattern in amountPatterns)
                {
                    _db.OcrTrainingPatterns.Add(new OcrTrainingPattern
                    {
                        FieldName = "amount",
                        PatternRule = pattern.Rule,
                        Country = countryCode == "UNKNOWN" ? null : countryCode,
                        Confidence = pattern.Confidence,
                        SourceCount = pattern.Count
                    });
                    patternsCreated++;
                }

                // Analyze vendor patterns
                var vendorPatterns = AnalyzeVendorPatterns(samples);
                foreach (var pattern in vendorPatterns)
                {
                    _db.OcrTrainingPatterns.Add(new OcrTrainingPattern
                    {
                        FieldName = "vendor",
                        PatternRule = pattern.Rule,
                        Country = countryCode == "UNKNOWN" ? null : countryCode,
                        Confidence = pattern.Confidence,
                        SourceCount = pattern.Count
                    });
                    patternsCreated++;
                }

                // Analyze currency patterns
                var currencyPatterns = AnalyzeCurrencyPatterns(samples);
                foreach (var pattern in currencyPatterns)
                {
                    _db.OcrTrainingPatterns.Add(new OcrTrainingPattern
                    {
                        FieldName = "currency",
                        PatternRule = pattern.Rule,
                        Country = countryCode == "UNKNOWN" ? null : countryCode,
                        Confidence = pattern.Confidence,
                        SourceCount = pattern.Count
                    });
                    patternsCreated++;
                }
            }

            await _db.SaveChangesAsync();
            Console.WriteLine($"[OCR-TRAIN] Rebuilt {patternsCreated} patterns from {verifiedSamples.Count} samples");

            return new RebuildPatternsResponse
            {
                Success = true,
                PatternsCreated = patternsCreated,
                SamplesAnalyzed = verifiedSamples.Count
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OCR-TRAIN] Rebuild error: {ex.Message}");
            return new RebuildPatternsResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get training statistics
    /// </summary>
    public async Task<TrainingStatsResponse> GetTrainingStatsAsync()
    {
        var totalSamples = await _db.OcrTrainingSamples.CountAsync();
        var verifiedSamples = await _db.OcrTrainingSamples.CountAsync(s => s.IsVerified);
        var rejectedSamples = await _db.OcrTrainingSamples.CountAsync(s => s.IsRejected);
        var patterns = await _db.OcrTrainingPatterns.ToListAsync();

        return new TrainingStatsResponse
        {
            TotalSamples = totalSamples,
            VerifiedSamples = verifiedSamples,
            RejectedSamples = rejectedSamples,
            PatternsLearned = patterns.Count,
            Patterns = patterns.Select(p => new PatternInfo
            {
                FieldName = p.FieldName,
                Rule = p.PatternRule,
                Country = p.Country,
                Confidence = p.Confidence,
                SourceCount = p.SourceCount
            }).ToList()
        };
    }

    // ═══════════════════════════════════════
    // Pattern Analysis Helpers
    // ═══════════════════════════════════════

    private record PatternResult(string Rule, double Confidence, int Count);

    private static List<PatternResult> AnalyzePaymentPatterns(List<OcrTrainingSample> samples)
    {
        var results = new List<PatternResult>();
        var total = samples.Count;
        if (total == 0) return results;

        foreach (var sample in samples)
        {
            if (string.IsNullOrEmpty(sample.ExtractedFields)) continue;
            try
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sample.ExtractedFields);
                if (fields == null) continue;

                // Check if corrections changed payment method
                if (!string.IsNullOrEmpty(sample.Corrections))
                {
                    var corrections = JsonSerializer.Deserialize<Dictionary<string, string>>(sample.Corrections);
                    if (corrections?.ContainsKey("FormOfPayment") == true)
                    {
                        // Learn: the original was wrong, the corrected is right
                        var corrected = corrections["FormOfPayment"];
                        if (fields.TryGetValue("FormOfPayment", out var origEl))
                        {
                            var original = origEl.GetString() ?? "";
                            if (original != corrected)
                            {
                                results.Add(new PatternResult(
                                    $"When AI detects '{original}', verify carefully — users often correct to '{corrected}'",
                                    80, 1));
                            }
                        }
                    }
                }
            }
            catch { /* skip bad data */ }
        }

        // Aggregate similar rules
        return AggregatePatterns(results);
    }

    private static List<PatternResult> AnalyzeDatePatterns(List<OcrTrainingSample> samples)
    {
        var results = new List<PatternResult>();
        // Count how many have dates at all
        int withDate = 0;
        foreach (var s in samples)
        {
            if (string.IsNullOrEmpty(s.ExtractedFields)) continue;
            try
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.ExtractedFields);
                if (fields?.TryGetValue("InvoiceDate", out var dateEl) == true)
                {
                    var dateStr = dateEl.GetString();
                    if (!string.IsNullOrEmpty(dateStr)) withDate++;
                }
            }
            catch { }
        }

        if (withDate > 0)
        {
            var pct = (double)withDate / samples.Count * 100;
            results.Add(new PatternResult(
                $"Date is present in {pct:F0}% of receipts — always look for it carefully",
                pct, withDate));
        }

        return results;
    }

    private static List<PatternResult> AnalyzeAmountPatterns(List<OcrTrainingSample> samples)
    {
        var results = new List<PatternResult>();
        int correctedAmounts = 0;

        foreach (var s in samples)
        {
            if (string.IsNullOrEmpty(s.Corrections)) continue;
            try
            {
                var corrections = JsonSerializer.Deserialize<Dictionary<string, string>>(s.Corrections);
                if (corrections?.ContainsKey("Total") == true || corrections?.ContainsKey("TotalVAT") == true)
                    correctedAmounts++;
            }
            catch { }
        }

        if (correctedAmounts > 0)
        {
            var pct = (double)correctedAmounts / samples.Count * 100;
            results.Add(new PatternResult(
                $"Amount extraction was corrected in {pct:F0}% of cases — double-check amounts, especially VAT vs total",
                Math.Min(95, 50 + pct), correctedAmounts));
        }

        return results;
    }

    private static List<PatternResult> AnalyzeVendorPatterns(List<OcrTrainingSample> samples)
    {
        var results = new List<PatternResult>();
        // Find recurring vendor names
        var vendorCounts = samples
            .Where(s => !string.IsNullOrEmpty(s.VendorName))
            .GroupBy(s => s.VendorName!.Trim().ToLowerInvariant())
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(5);

        foreach (var g in vendorCounts)
        {
            results.Add(new PatternResult(
                $"Recurring vendor '{g.First().VendorName}' appears frequently — use exact name match",
                90, g.Count()));
        }

        return results;
    }

    private static List<PatternResult> AnalyzeCurrencyPatterns(List<OcrTrainingSample> samples)
    {
        var results = new List<PatternResult>();
        var currencyCounts = samples
            .Where(s => !string.IsNullOrEmpty(s.Currency))
            .GroupBy(s => s.Currency!)
            .OrderByDescending(g => g.Count());

        foreach (var g in currencyCounts)
        {
            var pct = (double)g.Count() / samples.Count * 100;
            if (pct >= 20)
            {
                results.Add(new PatternResult(
                    $"Currency {g.Key} is used in {pct:F0}% of receipts from this region",
                    pct, g.Count()));
            }
        }

        return results;
    }

    private static List<PatternResult> AggregatePatterns(List<PatternResult> patterns)
    {
        return patterns
            .GroupBy(p => p.Rule)
            .Select(g => new PatternResult(g.Key, g.Max(p => p.Confidence), g.Sum(p => p.Count)))
            .ToList();
    }

    /// <summary>
    /// Build field position metadata from AI response
    /// </summary>
    private static string BuildFieldPositions(JsonElement aiJson)
    {
        var positions = new Dictionary<string, string>();

        // Record which fields were successfully extracted
        if (aiJson.TryGetProperty("invoice_date", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(d.GetString()))
            positions["date"] = "present";
        if (aiJson.TryGetProperty("invoice_number", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString()))
            positions["invoice_number"] = "present";
        if (aiJson.TryGetProperty("merchant", out var m) && m.TryGetProperty("name", out var mn) && !string.IsNullOrEmpty(mn.GetString()))
            positions["vendor"] = "present";
        if (aiJson.TryGetProperty("amounts", out var a) && a.TryGetProperty("tax_amount", out var t) && t.ValueKind == JsonValueKind.Number)
            positions["tax"] = "present";
        if (aiJson.TryGetProperty("payment", out var p) && p.TryGetProperty("form_of_payment", out var f) && !string.IsNullOrEmpty(f.GetString()))
            positions["payment"] = f.GetString() ?? "unknown";

        return JsonSerializer.Serialize(positions);
    }
}
