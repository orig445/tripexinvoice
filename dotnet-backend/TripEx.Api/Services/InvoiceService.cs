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
    /// Main entry point — called by InvoiceController and ChatService.
    /// Optionally accepts caller-supplied expense-type and form-of-payment lookup lists
    /// so the AI (and server-side fallback) can return the matching IDs.
    /// </summary>
    public async Task<AnalyzeInvoiceResponse> AnalyzeAsync(
        string? imageBase64,
        string? imageUrl,
        string? country,
        Guid? userId = null,
        IList<ExpenseTypeOption>? expenseTypes = null,
        IList<FormOfPaymentOption>? formOfPayments = null)
    {
        return await AnalyzeInternalAsync(imageBase64, imageUrl, country, userId,
            source: "analyze", expenseTypes: expenseTypes, formOfPayments: formOfPayments);
    }

    /// <summary>
    /// Internal scan with retry + detailed logging. Used by both AnalyzeAsync and BulkTrainAsync.
    /// </summary>
    private async Task<AnalyzeInvoiceResponse> AnalyzeInternalAsync(
        string? imageBase64, string? imageUrl, string? country, Guid? userId, string source,
        IList<ExpenseTypeOption>? expenseTypes = null,
        IList<FormOfPaymentOption>? formOfPayments = null)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        var stopwatch = Stopwatch.StartNew();
        string? rawResponse = null;
        var countryHint = country?.ToUpperInvariant() ?? "";
        var imageSize = imageBase64?.Length ?? 0;
        ImageInspectionResult? imageInspection = null;

        try
        {
            var imageContent = imageBase64 ?? imageUrl!;

            // Inspect image before sending: detect real MIME type, hash, save debug copy
            if (!string.IsNullOrEmpty(imageBase64))
            {
                imageInspection = _aiService.InspectImage(imageBase64);
                if (!imageInspection.IsValid)
                    Console.Error.WriteLine($"[OCR][{source}] ⚠️ Image validation failed: {imageInspection.InvalidReason}");
            }

            Console.WriteLine(
                $"[OCR][{source}] Scanning | country={countryHint} | imgSize={imageSize} | " +
                $"mime={imageInspection?.MimeType ?? "url"} | " +
                $"decoded={imageInspection?.DecodedBytes.ToString("N0") ?? "n/a"}B | " +
                $"sha256={imageInspection?.Sha256Hash[..16] ?? "n/a"}...");

            rawResponse = await _aiService.CallGeminiFlashAsync(imageContent, countryHint, expenseTypes, formOfPayments);
            Console.WriteLine($"[OCR][{source}] Raw response length: {rawResponse?.Length ?? 0}");

            var aiJson = OracleAiService.ParseJsonFromAiResponse(rawResponse!);
            ValidateJsonCompleteness(aiJson, source, 1);
            aiJson = ValidateAndFixAmounts(aiJson);
            var detectedCurrency = aiJson.TryGetProperty("currency", out var curProp) ? curProp.GetString() : null;
            aiJson = ValidateDateByCurrency(aiJson, detectedCurrency, country);
            var fields = MapToAlgoTextFields(aiJson);

            // ── Resolve expenseTypeId and formOfPaymentId ──
            ResolveOptionIds(fields, aiJson, expenseTypes, formOfPayments);

            stopwatch.Stop();

            bool missingTotal = IsMissingOrZeroAmount(fields.Total ?? fields.TotalAmount);
            bool missingDate = string.IsNullOrEmpty(fields.InvoiceDate);
            bool missingInvoiceNum = string.IsNullOrEmpty(fields.InvoiceNumber);
            bool missingVat = IsMissingOrZeroAmount(fields.TotalVAT);

            if (missingTotal)
            {
                fields.Total = "0";
                fields.TotalAmount = "0";
            }

            bool needsTraining = missingTotal || missingDate || missingInvoiceNum || missingVat;
            if (needsTraining)
            {
                Console.WriteLine($"[OCR][{source}] Missing fields (Total:{missingTotal} Date:{missingDate} InvNum:{missingInvoiceNum} VAT:{missingVat}) — saving to training");
                await SaveTrainingSample(fields, aiJson, countryHint, imageUrl);
            }

            Console.WriteLine($"[OCR][{source}] Done in {stopwatch.ElapsedMilliseconds}ms | Total={fields.Total} {fields.Currency} | Merchant={fields.MerchantName}");

            await SaveScanLog(new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                RawAiResponse = rawResponse,
                CountryHint = countryHint,
                Status = needsTraining ? "PartialResult" : "Success",
                DurationMs = stopwatch.ElapsedMilliseconds,
                HttpStatusCode = 200,
                ErrorMessage = needsTraining ? $"Missing: Total:{missingTotal} Date:{missingDate} InvNum:{missingInvoiceNum} VAT:{missingVat}" : null,
                Source = source,
                AttemptNumber = 1,
                ImageSizeBytes = imageInspection?.DecodedBytes ?? imageSize,
                ImageMimeType = imageInspection?.MimeType,
                ImageHash = imageInspection?.Sha256Hash,
                ImageDebugPath = imageInspection?.DebugFilePath,
            });

            return new AnalyzeInvoiceResponse { Success = true, Fields = fields, RawResponse = rawResponse };
        }
        catch (OciApiException ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"[OCR][{source}] OCI error {ex.StatusCode}: {ex.Message}");
            await SaveScanLog(new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                RawAiResponse = rawResponse,
                CountryHint = countryHint,
                Status = "Failed",
                DurationMs = stopwatch.ElapsedMilliseconds,
                HttpStatusCode = ex.StatusCode,
                ErrorMessage = ex.Message,
                ErrorType = nameof(OciApiException),
                OciResponseBody = ex.ResponseBody,
                Source = source,
                AttemptNumber = 1,
                ImageSizeBytes = imageInspection?.DecodedBytes ?? imageSize,
                ImageMimeType = imageInspection?.MimeType,
                ImageHash = imageInspection?.Sha256Hash,
                ImageDebugPath = imageInspection?.DebugFilePath,
            });
            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message, RawResponse = rawResponse ?? "", Fields = CreateFailureFields(countryHint) };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"[OCR][{source}] Network error: {ex.Message}");
            await SaveScanLog(new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                CountryHint = countryHint,
                Status = "Failed",
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                ErrorType = nameof(HttpRequestException),
                Source = source,
                AttemptNumber = 1,
                ImageSizeBytes = imageInspection?.DecodedBytes ?? imageSize,
                ImageMimeType = imageInspection?.MimeType,
                ImageHash = imageInspection?.Sha256Hash,
                ImageDebugPath = imageInspection?.DebugFilePath,
            });
            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message, Fields = CreateFailureFields(countryHint) };
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"[OCR][{source}] Timed out after 60s: {ex.Message}");
            await SaveScanLog(new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                CountryHint = countryHint,
                Status = "Failed",
                DurationMs = stopwatch.ElapsedMilliseconds,
                HttpStatusCode = 504,
                ErrorMessage = "Request timed out",
                ErrorType = nameof(TaskCanceledException),
                Source = source,
                AttemptNumber = 1,
                ImageSizeBytes = imageInspection?.DecodedBytes ?? imageSize,
                ImageMimeType = imageInspection?.MimeType,
                ImageHash = imageInspection?.Sha256Hash,
                ImageDebugPath = imageInspection?.DebugFilePath,
            });
            return new AnalyzeInvoiceResponse { Success = false, Error = "OCR request timed out.", Fields = CreateFailureFields(countryHint) };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"[OCR][{source}] Error: {ex.GetType().Name}: {ex.Message}");
            await SaveScanLog(new InvoiceScanLog
            {
                UserId = userId ?? Guid.Empty,
                CountryHint = countryHint,
                Status = "Failed",
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                ErrorType = ex.GetType().Name,
                Source = source,
                AttemptNumber = 1,
                ImageSizeBytes = imageInspection?.DecodedBytes ?? imageSize,
                ImageMimeType = imageInspection?.MimeType,
                ImageHash = imageInspection?.Sha256Hash,
                ImageDebugPath = imageInspection?.DebugFilePath,
            });
            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message, Fields = CreateFailureFields(countryHint) };
        }
    }

    private async Task SaveTrainingSample(InvoiceFields fields, JsonElement aiJson, string countryHint, string? imageUrl)
    {
        try
        {
            await SchemaGuard.EnsureOcrTrainingSamplesAsync(_db);
            var sample = new OcrTrainingSample
            {
                VendorName = fields.MerchantName,
                Country = countryHint,
                Currency = fields.Currency,
                DocumentType = fields.Type,
                ExtractedFields = JsonSerializer.Serialize(fields),
                FieldPositions = BuildFieldPositions(aiJson),
                ImageUrl = imageUrl,
                IsVerified = false,
                IsRejected = false
            };
            _db.OcrTrainingSamples.Add(sample);
            await _db.SaveChangesAsync();
            Console.WriteLine($"[OCR-TRAIN] Saved incomplete scan for training: {sample.Id}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OCR-TRAIN] Failed to save training sample: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    // Option ID Resolution (expense type & form of payment)
    // ═══════════════════════════════════════

    /// <summary>
    /// Resolves ExpenseTypeId and FormOfPaymentId from the caller-supplied option lists.
    /// Strategy:
    ///   1. If the AI returned a numeric expense_type_id / form_of_payment_id that exists in the list → use it.
    ///   2. Server-side fuzzy fallback: compare the AI-detected string value against option names
    ///      (case-insensitive, underscore-normalised) and pick the best match.
    /// </summary>
    private static void ResolveOptionIds(
        InvoiceFields fields,
        JsonElement aiJson,
        IList<ExpenseTypeOption>? expenseTypes,
        IList<FormOfPaymentOption>? formOfPayments)
    {
        // ── Expense Type ID ──
        if (expenseTypes != null && expenseTypes.Count > 0)
        {
            // 1. AI returned a numeric id
            if (aiJson.TryGetProperty("expense_type_id", out var etIdProp))
            {
                int? aiId = etIdProp.ValueKind == JsonValueKind.Number
                    ? etIdProp.GetInt32()
                    : int.TryParse(etIdProp.GetString(), out var p) ? p : (int?)null;

                if (aiId.HasValue && expenseTypes.Any(e => e.Id == aiId.Value))
                {
                    fields.ExpenseTypeId = aiId.Value;
                    Console.WriteLine($"[OCR-IDS] expense_type_id={aiId.Value} (AI-selected)");
                }
            }

            // 2. Server-side fallback: match ExpenseType string against option names
            if (fields.ExpenseTypeId == null && !string.IsNullOrEmpty(fields.ExpenseType))
            {
                var normalised = fields.ExpenseType.ToLowerInvariant().Replace("_", " ").Trim();
                var match = expenseTypes.FirstOrDefault(e =>
                    e.Name.ToLowerInvariant().Replace("_", " ").Trim() == normalised);
                if (match != null)
                {
                    fields.ExpenseTypeId = match.Id;
                    Console.WriteLine($"[OCR-IDS] expense_type_id={match.Id} (server-side name match: '{match.Name}')");
                }
            }

            if (fields.ExpenseTypeId == null)
                Console.WriteLine($"[OCR-IDS] expense_type_id=null — no match for '{fields.ExpenseType}' in {expenseTypes.Count} options");
        }

        // ── Form of Payment ID ──
        if (formOfPayments != null && formOfPayments.Count > 0)
        {
            // 1. AI returned a numeric id
            if (aiJson.TryGetProperty("payment", out var paymentEl) &&
                paymentEl.TryGetProperty("form_of_payment_id", out var fopIdProp))
            {
                int? aiId = fopIdProp.ValueKind == JsonValueKind.Number
                    ? fopIdProp.GetInt32()
                    : int.TryParse(fopIdProp.GetString(), out var p) ? p : (int?)null;

                if (aiId.HasValue && formOfPayments.Any(f => f.Id == aiId.Value))
                {
                    fields.FormOfPaymentId = aiId.Value;
                    Console.WriteLine($"[OCR-IDS] form_of_payment_id={aiId.Value} (AI-selected)");
                }
            }

            // 2. Server-side fallback: match FormOfPayment string against option names
            if (fields.FormOfPaymentId == null && !string.IsNullOrEmpty(fields.FormOfPayment))
            {
                var normalised = fields.FormOfPayment.ToLowerInvariant().Replace("_", " ").Trim();
                var match = formOfPayments.FirstOrDefault(f =>
                    f.Name.ToLowerInvariant().Replace("_", " ").Trim() == normalised);
                if (match != null)
                {
                    fields.FormOfPaymentId = match.Id;
                    Console.WriteLine($"[OCR-IDS] form_of_payment_id={match.Id} (server-side name match: '{match.Name}')");
                }
            }

            if (fields.FormOfPaymentId == null)
                Console.WriteLine($"[OCR-IDS] form_of_payment_id=null — no match for '{fields.FormOfPayment}' in {formOfPayments.Count} options");
        }
    }

    private static InvoiceFields CreateFailureFields(string countryHint) => new()
    {
        Total = "",
        TotalAmount = "",
        TotalVAT = "",
        Currency = DefaultCurrencyForCountry(countryHint) ?? "",
        InvoiceNumber = "",
        InvoiceDate = "",
        Type = "other",
        ExpenseType = "other",
        FormOfPayment = "cash",
        ExtraDetails = "{}"
    };

    private static bool IsMissingOrZeroAmount(string? value)
    {
        var amount = ParseAmountSmart(value);
        return !amount.HasValue || amount.Value <= 0;
    }

    /// <summary>
    /// Locale-aware amount parsing — handles "25,00" (EU), "1,234.56" (US), "1.234,56" (EU thousands),
    /// currency symbols, and whitespace. Returns null if not a valid positive number.
    /// </summary>
    public static decimal? ParseAmountSmart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var str = Regex.Replace(value, @"[₱₪$€£¥₩\s]", "").Trim();
        if (string.IsNullOrEmpty(str)) return null;

        int lastDot = str.LastIndexOf('.');
        int lastComma = str.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
            normalized = lastComma > lastDot
                ? str.Replace(".", "").Replace(",", ".")  // EU: 1.234,56
                : str.Replace(",", "");                    // US: 1,234.56
        else if (lastComma >= 0)
        {
            var afterComma = str.Length - lastComma - 1;
            normalized = (afterComma <= 2 && str.Count(c => c == ',') == 1)
                ? str.Replace(",", ".")  // "25,00" → "25.00"
                : str.Replace(",", "");  // thousands
        }
        else normalized = str;

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>
    /// Last-resort regex extraction: scan raw AI response text for ALL monetary numbers,
    /// return the LARGEST one (which is overwhelmingly the receipt total).
    /// Filters out IDs, dates, and unrealistic numbers.
    /// </summary>
    private static string? ExtractLargestMonetaryFromText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // Match decimal numbers like 1234.56, 1,234.56, 12 345,67, 999, etc.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            rawText,
            @"(?<![\d\.])(\d{1,3}(?:[,\s]\d{3})+(?:\.\d{1,2})?|\d+\.\d{1,2}|\d{2,6})(?![\d])");

        decimal best = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var s = m.Value.Replace(" ", "").Replace(",", "");
            if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;
            // Filter unrealistic values (likely IDs, years, phone numbers)
            if (v <= 0 || v > 10_000_000) continue;
            // Skip 4-digit integers that look like years (1900-2099)
            if (!s.Contains('.') && v >= 1900 && v <= 2099) continue;
            if (v > best) best = v;
        }

        return best > 0 ? best.ToString("0.##", CultureInfo.InvariantCulture) : null;
    }

    private static string DefaultCurrencyForCountry(string? country) => country?.ToUpperInvariant() switch
    {
        "IL" => "ILS",
        "PH" => "PHP",
        "US" => "USD",
        "CA" => "CAD",
        "AU" => "AUD",
        "NZ" => "NZD",
        "GB" or "UK" => "GBP",
        "CH" => "CHF",
        "SE" => "SEK",
        "NO" => "NOK",
        "DK" => "DKK",
        "PL" => "PLN",
        "CZ" => "CZK",
        "HU" => "HUF",
        "RO" => "RON",
        "RU" => "RUB",
        "TR" => "TRY",
        "TH" => "THB",
        "SG" => "SGD",
        "MY" => "MYR",
        "ID" => "IDR",
        "VN" => "VND",
        "JP" => "JPY",
        "KR" => "KRW",
        "CN" => "CNY",
        "TW" => "TWD",
        "HK" => "HKD",
        "IN" => "INR",
        "AE" => "AED",
        "SA" => "SAR",
        "EG" => "EGP",
        "ZA" => "ZAR",
        "NG" => "NGN",
        "KE" => "KES",
        "BR" => "BRL",
        "AR" => "ARS",
        "MX" => "MXN",
        "CO" => "COP",
        "CL" => "CLP",
        // Eurozone
        "DE" or "FR" or "IT" or "ES" or "PT" or "NL" or "BE" or "AT" or
        "FI" or "IE" or "GR" or "EU" => "EUR",
        _ => null  // unknown country — don't guess, let the AI-detected currency stand
    };

    // ═══════════════════════════════════════
    // Logging — InvoiceScanLogs
    // ═══════════════════════════════════════

    private async Task SaveScanLog(InvoiceScanLog log)
    {
        try
        {
            // Defensive: ensure table exists if background DB-init hasn't finished yet
            await SchemaGuard.EnsureInvoiceScanLogsAsync(_db);

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
    // JSON Completeness Validation
    // ═══════════════════════════════════════

    /// <summary>
    /// Logs warnings for any required top-level fields missing from the parsed AI JSON.
    /// Missing fields indicate truncation or a malformed response — callers use this for observability.
    /// </summary>
    private static void ValidateJsonCompleteness(JsonElement json, string source, int attempt)
    {
        var requiredTopLevel = new[] { "document_type", "invoice_number", "invoice_date", "currency", "expense_type", "merchant", "amounts", "payment" };
        var missing = requiredTopLevel.Where(f => !json.TryGetProperty(f, out _)).ToList();
        if (missing.Count > 0)
            Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt}: JSON missing fields [{string.Join(", ", missing)}] — likely truncated response");

        // Specifically check payment.amount_paid since this is the total shown to the user
        if (json.TryGetProperty("payment", out var pay))
        {
            if (!pay.TryGetProperty("amount_paid", out var amtProp) || amtProp.ValueKind == JsonValueKind.Null)
                Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt}: payment.amount_paid is missing or null — total will be empty");
            else
            {
                var rawAmt = amtProp.ValueKind == JsonValueKind.Number ? amtProp.GetRawText() : amtProp.GetString();
                Console.WriteLine($"[OCR][{source}] Attempt {attempt}: payment.amount_paid={rawAmt}");
            }
        }
        else
        {
            Console.Error.WriteLine($"[OCR][{source}] Attempt {attempt}: 'payment' block missing — amount_paid cannot be extracted");
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
            if (string.IsNullOrEmpty(str)) return null;

            // Strip currency symbols and whitespace (keep digits, commas, dots, minus)
            str = Regex.Replace(str, @"[₱₪$€£¥₩\s]", "").Trim();
            if (string.IsNullOrEmpty(str)) return null;

            // Smart number parsing: handle BOTH "1,234.56" (US/UK) and "1.234,56" (EU)
            // Rule: the LAST separator (',' or '.') is the decimal separator
            int lastDot = str.LastIndexOf('.');
            int lastComma = str.LastIndexOf(',');

            string normalized;
            if (lastDot >= 0 && lastComma >= 0)
            {
                // Both present → the rightmost is decimal, the other is thousands
                if (lastComma > lastDot)
                {
                    // EU format: "1.234,56" → remove dots, replace comma with dot
                    normalized = str.Replace(".", "").Replace(",", ".");
                }
                else
                {
                    // US format: "1,234.56" → remove commas
                    normalized = str.Replace(",", "");
                }
            }
            else if (lastComma >= 0)
            {
                // Only comma present. If it looks like decimal (≤2 digits after), treat as decimal
                var afterComma = str.Length - lastComma - 1;
                if (afterComma <= 2 && str.Count(c => c == ',') == 1)
                    normalized = str.Replace(",", ".");
                else
                    normalized = str.Replace(",", ""); // thousands separator
            }
            else
            {
                normalized = str; // only dots or plain number
            }

            if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
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

        // East Asian currencies use YY/MM/DD or YYYY/MM/DD format
        bool isEastAsian = currency == "KRW" || currency == "JPY" || currency == "CNY" || currency == "TWD"
                           || country == "KR" || country == "JP" || country == "CN" || country == "TW";
        // US uses MM/DD/YY
        bool isUS = currency == "USD" && (country == "US" || country == null);

        // YYYY-MM-DD or YYYY/MM/DD
        var matchYMD = Regex.Match(dateStr, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})");
        if (matchYMD.Success)
        {
            int year = int.Parse(matchYMD.Groups[1].Value);
            int g2 = int.Parse(matchYMD.Groups[2].Value);
            int g3 = int.Parse(matchYMD.Groups[3].Value);

            // 🩹 SAFETY NET: AI may have parsed Korean "26/02/13" as "2013-02-26".
            // If currency is East Asian and the year looks suspiciously old (>5 years ago),
            // and the day-part (g3) looks like a plausible 2-digit year (e.g. 26 = 2026),
            // flip it: treat YYYY as DD, day as YY.
            if (isEastAsian && year < DateTime.UtcNow.Year - 5 && g3 <= 31 && g3 >= 13)
            {
                int flippedYear = 2000 + g3;
                int flippedDay = year % 100; // last 2 digits of misread year become day
                if (g2 <= 12 && flippedDay >= 1 && flippedDay <= 31 && flippedYear <= DateTime.UtcNow.Year + 1)
                {
                    var flipped = TryBuildDate(flippedYear, g2, flippedDay);
                    if (flipped != null)
                    {
                        Console.WriteLine($"[DateNorm] East Asian flip: '{dateStr}' → '{flipped}' (currency={currency})");
                        return flipped;
                    }
                }
            }

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
                bool useDDMM = currency == "ILS" || country == "IL" || (!isUS && !isEastAsian);
                if (useDDMM)
                    return TryBuildDate(year, g2, g1); // DD/MM
                else
                    return TryBuildDate(year, g1, g2); // MM/DD
            }
        }

        // YY/MM/DD (East Asian) or DD/MM/YY / MM/DD/YY
        var matchShort = Regex.Match(dateStr, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{2})$");
        if (matchShort.Success)
        {
            int p1 = int.Parse(matchShort.Groups[1].Value);
            int p2 = int.Parse(matchShort.Groups[2].Value);
            int p3 = int.Parse(matchShort.Groups[3].Value);

            // East Asian: first part is year (YY/MM/DD)
            if (isEastAsian && p2 >= 1 && p2 <= 12 && p3 >= 1 && p3 <= 31)
            {
                int year = (p1 < 50 ? 2000 : 1900) + p1;
                var built = TryBuildDate(year, p2, p3);
                if (built != null)
                {
                    Console.WriteLine($"[DateNorm] East Asian YY/MM/DD: '{dateStr}' → '{built}' (currency={currency})");
                    return built;
                }
            }

            // Default: treat last part as year
            int fullYear = 2000 + p3;
            return TryNormalizeDate($"{p1}/{p2}/{fullYear}", currency, country);
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
            totalValue = GetDecimalProp(payment, "amount_paid")
                      ?? GetDecimalProp(payment, "total")
                      ?? GetDecimalProp(payment, "amount")
                      ?? GetDecimalProp(payment, "paid");

        // Fallback: calculate from amounts
        if (!totalValue.HasValue || totalValue.Value == 0)
        {
            if (json.TryGetProperty("amounts", out var amounts))
            {
                // Try direct total fields first (some AIs include them)
                totalValue = GetDecimalProp(amounts, "total")
                          ?? GetDecimalProp(amounts, "total_amount")
                          ?? GetDecimalProp(amounts, "grand_total");

                if (!totalValue.HasValue || totalValue.Value == 0)
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
        }

        // Fallback: top-level total (many naming variations)
        if (!totalValue.HasValue || totalValue.Value == 0)
            totalValue = GetDecimalProp(json, "total")
                      ?? GetDecimalProp(json, "total_amount")
                      ?? GetDecimalProp(json, "totalAmount")
                      ?? GetDecimalProp(json, "amount")
                      ?? GetDecimalProp(json, "grand_total")
                      ?? GetDecimalProp(json, "grandTotal");

        // Always populate Total — combtas requires the field.
        // If still no total but we have subtotal+tax, calculate it (last resort)
        if ((!totalValue.HasValue || totalValue.Value == 0) && json.TryGetProperty("amounts", out var amtFinal))
        {
            var st = GetDecimalProp(amtFinal, "vatable_sales_amount") ?? GetDecimalProp(amtFinal, "subtotal") ?? 0;
            var tx = GetDecimalProp(amtFinal, "tax_amount") ?? GetDecimalProp(amtFinal, "vat_amount") ?? 0;
            if (st + tx > 0) totalValue = st + tx;
        }

        if (totalValue.HasValue && totalValue.Value > 0)
        {
            fields.Total = totalValue.Value.ToString("F2", CultureInfo.InvariantCulture);
            fields.TotalAmount = totalValue.Value.ToString("F2", CultureInfo.InvariantCulture);
        }

        // VAT — check amounts object AND top-level (multiple naming conventions)
        decimal? taxValue = null;
        decimal? vatableValue = null;
        if (json.TryGetProperty("amounts", out var amt))
        {
            taxValue = GetDecimalProp(amt, "tax_amount")
                    ?? GetDecimalProp(amt, "vat_amount")
                    ?? GetDecimalProp(amt, "vat")
                    ?? GetDecimalProp(amt, "tax");
            vatableValue = GetDecimalProp(amt, "vatable_sales_amount")
                        ?? GetDecimalProp(amt, "subtotal")
                        ?? GetDecimalProp(amt, "net_amount")
                        ?? GetDecimalProp(amt, "sub_total");
        }
        // Top-level fallbacks for VAT
        taxValue ??= GetDecimalProp(json, "tax_amount")
                  ?? GetDecimalProp(json, "vat_amount")
                  ?? GetDecimalProp(json, "vat")
                  ?? GetDecimalProp(json, "tax")
                  ?? GetDecimalProp(json, "totalVAT")
                  ?? GetDecimalProp(json, "total_vat");
        vatableValue ??= GetDecimalProp(json, "subtotal")
                      ?? GetDecimalProp(json, "sub_total")
                      ?? GetDecimalProp(json, "net_amount");

        if (taxValue.HasValue && taxValue.Value > 0) fields.TotalVAT = taxValue.Value.ToString("F2", CultureInfo.InvariantCulture);
        if (vatableValue.HasValue && vatableValue.Value > 0) fields.SubCategory = vatableValue.Value.ToString("F2", CultureInfo.InvariantCulture);

        // Direct fields (with multiple naming fallbacks)
        fields.Currency = GetStringProp(json, "currency")
                       ?? GetStringProp(json, "currency_code")
                       ?? GetStringProp(json, "currencyCode");
        fields.InvoiceNumber = GetStringProp(json, "invoice_number")
                            ?? GetStringProp(json, "invoiceNumber")
                            ?? GetStringProp(json, "receipt_number")
                            ?? GetStringProp(json, "receiptNumber")
                            ?? GetStringProp(json, "transaction_id")
                            ?? GetStringProp(json, "transactionId")
                            ?? GetStringProp(json, "document_number")
                            ?? GetStringProp(json, "doc_number")
                            ?? GetStringProp(json, "ref_number")
                            ?? GetStringProp(json, "reference");
        fields.InvoiceDate = GetStringProp(json, "invoice_date")
                          ?? GetStringProp(json, "invoiceDate")
                          ?? GetStringProp(json, "date")
                          ?? GetStringProp(json, "transaction_date")
                          ?? GetStringProp(json, "receipt_date");
        fields.Type = GetStringProp(json, "document_type")
                   ?? GetStringProp(json, "documentType")
                   ?? GetStringProp(json, "type");

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

            var countryHint = country?.ToUpperInvariant() ?? "";

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
        await SchemaGuard.EnsureInvoiceScanLogsAsync(_db);
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
            await SchemaGuard.EnsureOcrTrainingPatternsAsync(_db);
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
        await SchemaGuard.EnsureOcrTrainingPatternsAsync(_db);
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
