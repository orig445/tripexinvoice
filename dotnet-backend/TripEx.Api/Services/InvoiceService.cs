using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Invoice OCR analysis using Oracle AI Vision.
/// Returns data in AlgoText-compatible flat format ($.fields.*)
/// so TripEx client code doesn't need changes.
/// </summary>
public class InvoiceService
{
    private readonly TripExDbContext _db;
    private readonly OracleAiService _oracle;

    // Valid expense types for classification
    private static readonly HashSet<string> ValidExpenseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "business_meal", "vehicle", "entertainment", "hotel",
        "internet", "parking", "other", "meal", "taxi"
    };

    public InvoiceService(TripExDbContext db, OracleAiService oracle)
    {
        _db = db;
        _oracle = oracle;
    }

    public async Task<AnalyzeInvoiceResponse> AnalyzeAsync(string? imageBase64, string? imageUrl, string? country, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        var stopwatch = Stopwatch.StartNew();
        string? scan1Raw = null, scan2Raw = null;
        string? errorLog = null;

        try
        {
            var correctionsContext = await BuildCorrectionsContext();

            var imageContentUrl = imageBase64 != null
                ? (imageBase64.StartsWith("data:") ? imageBase64 : $"data:image/jpeg;base64,{imageBase64}")
                : imageUrl!;

            var systemPrompt = GetExtractionPrompt(correctionsContext, country);

            // ── SCAN 1: Extract ──
            Console.WriteLine("[OCR] Starting Scan 1 - Extraction...");
            var scan1Messages = new List<OracleMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new()
                {
                    Role = "user",
                    Content = new object[]
                    {
                        new { type = "text", text = "Please analyze this invoice and extract all information as JSON:" },
                        new { type = "image_url", image_url = new { url = imageContentUrl } }
                    }
                }
            };

            scan1Raw = await _oracle.ChatAsync(scan1Messages, 1024, 0.1);
            Console.WriteLine($"[OCR] Scan 1 raw response length: {scan1Raw?.Length ?? 0}");
            var scan1Data = OracleAiService.ParseJsonFromAiResponse(scan1Raw);

            // ── SCAN 2: Verify ──
            JsonElement finalData = scan1Data;
            try
            {
                Console.WriteLine("[OCR] Starting Scan 2 - Verification...");
                var verifyPrompt = GetVerificationPrompt(scan1Data, country);

                var scan2Messages = new List<OracleMessage>
                {
                    new() { Role = "system", Content = verifyPrompt },
                    new()
                    {
                        Role = "user",
                        Content = new object[]
                        {
                            new { type = "text", text = "Verify this extracted invoice data against the image:" },
                            new { type = "image_url", image_url = new { url = imageContentUrl } }
                        }
                    }
                };

                scan2Raw = await _oracle.ChatAsync(scan2Messages, 1024, 0.1);
                Console.WriteLine($"[OCR] Scan 2 raw response length: {scan2Raw?.Length ?? 0}");
                finalData = OracleAiService.ParseJsonFromAiResponse(scan2Raw);
            }
            catch (Exception ex)
            {
                errorLog = $"Scan 2 failed: {ex.Message}";
                Console.WriteLine($"[OCR] {errorLog}");
            }

            // ── Post-processing: Validate amounts ──
            finalData = ValidateAndFixAmounts(finalData);

            // ── Post-processing: Validate date by currency ──
            var detectedCurrency = finalData.TryGetProperty("currency", out var curProp) ? curProp.GetString() : null;
            finalData = ValidateDateByCurrency(finalData, detectedCurrency, country);

            // Convert AI response to AlgoText-compatible flat fields format
            var fields = MapToAlgoTextFields(finalData);

            stopwatch.Stop();
            Console.WriteLine($"[OCR] Total processing time: {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"[OCR] Final fields: Total={fields.Total}, VAT={fields.TotalVAT}, Currency={fields.Currency}, ExpenseType={fields.ExpenseType}, Merchant={fields.MerchantName}");

            // ── Save OCR log ──
            await SaveOcrLog(userId, scan1Raw, scan2Raw, fields, country, detectedCurrency, (int)stopwatch.ElapsedMilliseconds, errorLog);

            return new AnalyzeInvoiceResponse
            {
                Success = true,
                Fields = fields,
                RawResponse = scan1Raw
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorLog = ex.Message;
            Console.WriteLine($"[OCR] FATAL ERROR: {ex.Message}");

            // Log even failed scans
            await SaveOcrLog(userId, scan1Raw, scan2Raw, null, country, null, (int)stopwatch.ElapsedMilliseconds, errorLog);

            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message };
        }
    }

    // ═══════════════════════════════════════
    // Verification Prompt (Scan 2) — Targeted
    // ═══════════════════════════════════════

    private static string GetVerificationPrompt(JsonElement scan1Data, string? country)
    {
        return $@"You are a receipt/invoice verification expert. I extracted the following data from a receipt image. 
Please look at the SAME image and verify each field with these SPECIFIC checks:

EXTRACTED DATA:
{scan1Data}

VERIFICATION CHECKLIST:
1. MERCHANT NAME: Is this the business name printed large at the TOP of the document? Not a product name or header text?
2. AMOUNTS: Is tax_amount SMALLER than vatable_sales_amount? If tax > subtotal, they are SWAPPED — fix it!
3. TOTAL: Does amount_paid match the sum of vatable + non_vatable + service_charge + tax? 
4. DATE: Is this the TRANSACTION date, NOT an accreditation/permit date? Read digits exactly.
5. INVOICE NUMBER: Is this a document number, NOT a TIN or permit number?
6. TIN: Is this the merchant's tax identification, NOT an invoice number?
7. EXPENSE TYPE: Does the category make sense for what was purchased?

RULES:
- Fix ANY field that is wrong
- Return the FULL corrected JSON (same structure), no explanation
- If everything is correct, return the SAME JSON unchanged";
    }

    // ═══════════════════════════════════════
    // Mathematical Validation
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
            // Rebuild JSON with swapped values
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

        // Rule 2: If total (amount_paid) exists and subtotal+tax don't add up, recalculate tax
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
        
        if (val.ValueKind == JsonValueKind.Number)
            return val.GetDecimal();
        
        // AI often returns amounts as strings like "13,328.00" or "13328.00"
        if (val.ValueKind == JsonValueKind.String)
        {
            var str = val.GetString();
            if (!string.IsNullOrEmpty(str))
            {
                // Remove currency symbols, commas, spaces
                str = Regex.Replace(str, @"[₱₪$€£¥,\s]", "");
                if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        }
        
        return null;
    }

    // ═══════════════════════════════════════
    // Date Validation by Currency/Country
    // ═══════════════════════════════════════

    private static JsonElement ValidateDateByCurrency(JsonElement json, string? currency, string? country)
    {
        if (!json.TryGetProperty("invoice_date", out var dateProp)) return json;
        var dateStr = dateProp.GetString();
        if (string.IsNullOrEmpty(dateStr)) return json;

        var effectiveCurrency = currency?.ToUpperInvariant();
        var effectiveCountry = country?.ToUpperInvariant();

        // Try to parse various date formats the AI might return
        string? normalizedDate = TryNormalizeDate(dateStr, effectiveCurrency, effectiveCountry);
        
        if (normalizedDate != null && normalizedDate != dateStr)
        {
            Console.WriteLine($"[OCR-DATE] Normalized: {dateStr} → {normalizedDate} (currency={effectiveCurrency}, country={effectiveCountry})");
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText()) ?? new();
            dict["invoice_date"] = normalizedDate;
            var rebuilt = JsonSerializer.SerializeToUtf8Bytes(dict);
            return JsonDocument.Parse(rebuilt).RootElement.Clone();
        }

        return json;
    }

    /// <summary>
    /// Normalize various date formats to YYYY-MM-DD, considering currency/country conventions
    /// </summary>
    private static string? TryNormalizeDate(string dateStr, string? currency, string? country)
    {
        // Clean the date string
        dateStr = dateStr.Trim();
        
        // Try YYYY-MM-DD (already correct format)
        var matchYMD = Regex.Match(dateStr, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})");
        if (matchYMD.Success)
        {
            int year = int.Parse(matchYMD.Groups[1].Value);
            int g2 = int.Parse(matchYMD.Groups[2].Value);
            int g3 = int.Parse(matchYMD.Groups[3].Value);
            
            // YYYY/MM/DD format (common in payment terminals like Maya)
            if (g2 <= 12 && g3 >= 1 && g3 <= 31)
            {
                var result = $"{year:D4}-{g2:D2}-{g3:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
            // YYYY/DD/MM — month and day might be swapped
            if (g2 > 12 && g3 <= 12)
            {
                var result = $"{year:D4}-{g3:D2}-{g2:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
        }

        // Try DD/MM/YYYY or MM/DD/YYYY
        var matchDMY = Regex.Match(dateStr, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{4})");
        if (matchDMY.Success)
        {
            int g1 = int.Parse(matchDMY.Groups[1].Value);
            int g2 = int.Parse(matchDMY.Groups[2].Value);
            int year = int.Parse(matchDMY.Groups[3].Value);

            // If first number > 12, it MUST be day (DD/MM/YYYY)
            if (g1 > 12 && g2 <= 12)
            {
                var result = $"{year:D4}-{g2:D2}-{g1:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
            // If second number > 12, it MUST be day (MM/DD/YYYY)
            if (g2 > 12 && g1 <= 12)
            {
                var result = $"{year:D4}-{g1:D2}-{g2:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
            // Ambiguous — use currency/country hint
            if (g1 <= 12 && g2 <= 12)
            {
                bool useDDMM = currency == "ILS" || country == "IL"; // Israel = DD/MM
                bool useMMDD = currency == "PHP" || currency == "USD" || country == "PH" || country == "US"; // PH/US = MM/DD
                
                int month, day;
                if (useDDMM) { day = g1; month = g2; }
                else if (useMMDD) { month = g1; day = g2; }
                else { month = g1; day = g2; } // default MM/DD
                
                var result = $"{year:D4}-{month:D2}-{day:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
        }

        // Try DD/MM/YY or MM/DD/YY
        var matchShort = Regex.Match(dateStr, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{2})$");
        if (matchShort.Success)
        {
            int g1 = int.Parse(matchShort.Groups[1].Value);
            int g2 = int.Parse(matchShort.Groups[2].Value);
            int year = 2000 + int.Parse(matchShort.Groups[3].Value);
            // Recurse with full year
            return TryNormalizeDate($"{g1}/{g2}/{year}", currency, country);
        }

        // Already YYYY-MM-DD — validate month > 12 swap
        var matchISO = Regex.Match(dateStr, @"^(\d{4})-(\d{2})-(\d{2})$");
        if (matchISO.Success)
        {
            int year = int.Parse(matchISO.Groups[1].Value);
            int month = int.Parse(matchISO.Groups[2].Value);
            int day = int.Parse(matchISO.Groups[3].Value);
            
            if (month > 12 && day <= 12)
            {
                var result = $"{year:D4}-{day:D2}-{month:D2}";
                if (DateTime.TryParseExact(result, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return result;
            }
        }

        return null; // couldn't normalize, keep original
    }

    // ═══════════════════════════════════════
    // Field Mapping (AlgoText-compatible)
    // ═══════════════════════════════════════

    /// <summary>
    /// Maps the AI JSON response to AlgoText-compatible flat fields.
    /// TripEx client reads: fields.total, fields.totalVAT, fields.currency,
    /// fields.invoiceNumber, fields.invoiceDate, fields.type
    /// </summary>
    private static InvoiceFields MapToAlgoTextFields(JsonElement json)
    {
        var fields = new InvoiceFields();

        // Total = payment.amount_paid or sum of amounts
        decimal? totalValue = null;

        if (json.TryGetProperty("payment", out var payment))
        {
            totalValue = GetDecimalFromElement(payment, "amount_paid");
        }

        // Fallback: calculate from amounts
        if (!totalValue.HasValue || totalValue.Value == 0)
        {
            if (json.TryGetProperty("amounts", out var amounts))
            {
                decimal total = 0;
                var vat = GetDecimalFromElement(amounts, "vatable_sales_amount");
                var nonVat = GetDecimalFromElement(amounts, "non_vatable_sales_amount");
                var sc = GetDecimalFromElement(amounts, "service_charge_amount");
                var tax = GetDecimalFromElement(amounts, "tax_amount");
                if (vat.HasValue) total += vat.Value;
                if (nonVat.HasValue) total += nonVat.Value;
                if (sc.HasValue) total += sc.Value;
                if (tax.HasValue) total += tax.Value;
                if (total > 0) totalValue = total;
            }
        }

        // Fallback: look for top-level "total" or "total_amount"
        if (!totalValue.HasValue || totalValue.Value == 0)
        {
            totalValue = GetDecimalFromElement(json, "total") 
                      ?? GetDecimalFromElement(json, "total_amount")
                      ?? GetDecimalFromElement(json, "sale_amount");
        }

        if (totalValue.HasValue)
        {
            fields.Total = totalValue.Value.ToString("F2");
            fields.TotalAmount = totalValue.Value.ToString("F2");
        }

        // TotalVAT = amounts.tax_amount
        if (json.TryGetProperty("amounts", out var amt))
        {
            var taxVal = GetDecimalFromElement(amt, "tax_amount");
            if (taxVal.HasValue)
                fields.TotalVAT = taxVal.Value.ToString("F2");
        }

        // SubCategory — store vatable_sales_amount as sub_category info
        if (json.TryGetProperty("amounts", out var amtSub))
        {
            var vatableVal = GetDecimalFromElement(amtSub, "vatable_sales_amount");
            if (vatableVal.HasValue)
                fields.SubCategory = vatableVal.Value.ToString("F2");
        }

        // Direct fields
        fields.Currency = GetStringProp(json, "currency");
        fields.InvoiceNumber = GetStringProp(json, "invoice_number");
        fields.InvoiceDate = GetStringProp(json, "invoice_date");
        fields.Type = GetStringProp(json, "document_type");

        // Expense Type — check multiple possible locations
        var expTypeStr = GetStringProp(json, "expense_type") 
                      ?? GetStringProp(json, "category")
                      ?? GetStringProp(json, "expense_category");
        if (!string.IsNullOrEmpty(expTypeStr))
        {
            // Normalize: lowercase, trim, replace spaces with underscore
            expTypeStr = expTypeStr.Trim().ToLowerInvariant().Replace(" ", "_");
            fields.ExpenseType = ValidExpenseTypes.Contains(expTypeStr) ? expTypeStr : "other";
        }
        else
        {
            fields.ExpenseType = "other";
        }

        // Merchant info — with name cleaning
        if (json.TryGetProperty("merchant", out var merchant))
        {
            fields.MerchantName = CleanMerchantName(GetStringProp(merchant, "name"));
            fields.MerchantTin = GetStringProp(merchant, "tin");
            fields.MerchantAddress = GetStringProp(merchant, "address");
            fields.MerchantCity = GetStringProp(merchant, "city");
        }

        // Payment
        if (json.TryGetProperty("payment", out var pay))
        {
            fields.PaymentMethod = GetStringProp(pay, "method");
            var paidVal = GetDecimalFromElement(pay, "amount_paid");
            if (paidVal.HasValue)
                fields.AmountPaid = paidVal.Value.ToString("F2");
        }

        // Extra Details — store the entire raw JSON for support/debugging
        fields.ExtraDetails = json.GetRawText();

        return fields;
    }

    /// <summary>
    /// Get a decimal value from a JSON element property, handling both Number and String types
    /// </summary>
    private static decimal? GetDecimalFromElement(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        
        if (val.ValueKind == JsonValueKind.Number)
            return val.GetDecimal();
        
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

    /// <summary>
    /// Safely get string property from JSON element
    /// </summary>
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

    /// <summary>
    /// Clean merchant name: remove newlines, extra spaces, trim
    /// </summary>
    private static string? CleanMerchantName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Remove newlines, tabs, collapse multiple spaces
        var cleaned = Regex.Replace(name, @"[\r\n\t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim();
    }

    // ═══════════════════════════════════════
    // OCR Logging
    // ═══════════════════════════════════════

    private async Task SaveOcrLog(Guid? userId, string? scan1Raw, string? scan2Raw,
        InvoiceFields? fields, string? country, string? currency, int processingTimeMs, string? errors)
    {
        try
        {
            var log = new OcrScanLog
            {
                UserId = userId,
                Scan1Raw = scan1Raw,
                Scan2Raw = scan2Raw,
                FinalFields = fields != null ? JsonSerializer.Serialize(fields) : null,
                Country = country,
                CurrencyDetected = currency,
                ProcessingTimeMs = processingTimeMs,
                Errors = errors
            };
            _db.OcrScanLogs.Add(log);
            await _db.SaveChangesAsync();
            Console.WriteLine($"[OCR] Log saved: {log.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Failed to save log: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    // Corrections Context
    // ═══════════════════════════════════════

    private async Task<string> BuildCorrectionsContext()
    {
        var corrections = await _db.InvoiceCorrections
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync();

        if (corrections.Count == 0) return "";

        var lines = corrections.Select(c =>
            $"- Field \"{c.FieldName}\": AI extracted \"{c.OriginalValue}\" but correct value was \"{c.CorrectedValue}\""
            + (c.Context != null ? $" (context: {c.Context})" : ""));

        return $"\n\nLEARNING FROM PAST MISTAKES — Apply these corrections patterns:\n{string.Join("\n", lines)}\n";
    }

    // ═══════════════════════════════════════
    // Extraction Prompt (Enhanced)
    // ═══════════════════════════════════════

    private static string GetExtractionPrompt(string correctionsContext, string? country)
    {
        var countryHint = "";
        if (!string.IsNullOrEmpty(country))
        {
            countryHint = country.ToUpperInvariant() switch
            {
                "IL" => @"
COUNTRY CONTEXT: Israel. Currency is likely ILS (₪). Date format DD/MM/YYYY → output YYYY-MM-DD. 
Look for Hebrew text. מספר חשבונית = Invoice Number. סה""כ = Total. מע""מ = VAT.
WARNING: Do NOT assume ILS just because of Hebrew text — check currency symbols carefully.",
                "PH" => @"
COUNTRY CONTEXT: Philippines. Currency is likely PHP (₱). Date format MM/DD/YYYY → output YYYY-MM-DD. 
Look for TIN format XXX-XXX-XXX-XXXXX. Look for 'Official Receipt' or 'Sales Invoice' headers.",
                "TH" => "\nCOUNTRY CONTEXT: Thailand. Currency is likely THB (฿).",
                "US" => "\nCOUNTRY CONTEXT: United States. Currency is likely USD ($). Date format MM/DD/YYYY → output YYYY-MM-DD.",
                _ => $"\nCOUNTRY CONTEXT: {country}."
            };
        }

        return $@"You are an expert invoice/receipt OCR analyzer. Your job is to extract ONLY what is physically printed on the document. NEVER guess, calculate, or invent data.

GOLDEN RULE: If you cannot clearly read a value, return null. Wrong data is worse than no data.
{countryHint}

═══ DOCUMENT TYPE RECOGNITION ═══
This could be ANY of these document types — adapt your extraction accordingly:
- Standard invoice/receipt (paper)
- Payment TERMINAL receipt (Maya, GCash, BPI, etc.) — look for ""SALE AMOUNT"", ""TRANSACTION NO."", ""DATE/TIME""
- Digital/email receipt
- Handwritten receipt

═══ STEP-BY-STEP EXTRACTION PROCESS ═══

STEP 1 — VENDOR/MERCHANT NAME:
- Look at the LARGEST text at the TOP of the document
- For payment terminal receipts: the terminal brand (e.g. ""maya"") is NOT the merchant — look for the business name printed ABOVE or BELOW the terminal brand
- Read it CHARACTER BY CHARACTER — do NOT guess or autocomplete
- Do NOT confuse product names with the store/business name

STEP 2 — FIND THE TOTAL (THE AMOUNT THE CUSTOMER PAID):
- Look for labels: ""SALE AMOUNT"", ""AMOUNT DUE"", ""TOTAL"", ""סה""כ לתשלום"", ""Grand Total"", ""Total Amount""
- For payment terminals: ""SALE AMOUNT"" is the total — use this value!
- Read the number EXACTLY as printed including decimals
- ⚠️ Remove currency symbols (₱, ₪, $) but keep the number exactly
- IGNORE: Cash given, Change amounts
- ⚠️ CRITICAL: Return amounts as NUMBERS, not strings! Example: 13328.00, not ""13,328.00""

STEP 3 — FIND TAX/VAT (SMALLER THAN SUBTOTAL):
- Look for: ""VAT"", ""Tax"", ""מע""מ"", ""Output Tax"", ""VATable Sales""
- ⚠️ CRITICAL: tax_amount MUST BE SMALLER than vatable_sales_amount
- If tax seems larger than subtotal, you have them SWAPPED — switch them!
- If no tax/VAT is visible on the document, set tax_amount to 0

STEP 4 — DATE:
- Read the EXACT DIGITS printed on the document
- For payment terminals: look for ""DATE/TIME"" field — extract ONLY the date part
- Common formats you may see:
  * ""2026/02/08 11:01:31"" → extract as ""2026-02-08""
  * ""02/08/2026"" (MM/DD/YYYY for PH/US) → extract as ""2026-02-08""  
  * ""08/02/2026"" (DD/MM/YYYY for IL) → extract as ""2026-02-08""
- Look for the TRANSACTION date, NOT accreditation/permit/certification dates
- ⚠️ ALWAYS output as YYYY-MM-DD format

STEP 5 — INVOICE/TRANSACTION NUMBER:
- Look for: ""TRANSACTION NO."", ""Invoice No"", ""SI#"", ""OR NO."", ""Receipt #"", ""REFERENCE NO."", ""BATCH NO."", ""מספר קבלה"", ""אסמכתא""
- ⚠️ This is NOT the TIN/tax ID! Invoice numbers are usually shorter and numeric
- For payment terminals: use TRANSACTION NO. or REFERENCE NO.

STEP 6 — EXPENSE CATEGORY:
- You MUST classify the purchase as ONE of these exact values:
  ""business_meal"" — restaurant, cafe, catering for business purposes
  ""vehicle"" — gas station, car rental, car maintenance, fuel
  ""entertainment"" — events, shows, recreation, activities
  ""hotel"" — accommodation, lodging, resort, hotel
  ""internet"" — internet service, phone bills, telecom, data plans
  ""parking"" — parking fees, parking lot
  ""meal"" — food/drink purchase (non-business context)
  ""taxi"" — taxi, ride-sharing (Grab, Uber), transportation
  ""other"" — anything that doesn't fit above
- ⚠️ You MUST always provide this field! Default to ""other"" if unsure.

═══ NEGATIVE EXAMPLES (DO NOT DO THIS) ═══
❌ Do NOT put the subtotal value in tax_amount
❌ Do NOT put the tax value in vatable_sales_amount  
❌ Do NOT put TIN in invoice_number
❌ Do NOT put invoice_number in TIN
❌ Do NOT invent or autocomplete merchant names
❌ Do NOT return amounts as strings — they must be numbers
❌ Do NOT leave expense_type empty — always classify

DOCUMENT TYPE: Classify as one of: ""sales_invoice"", ""official_receipt"", ""charge_invoice"", ""delivery_receipt"", ""credit_memo"", ""debit_memo"", ""purchase_order"", ""quotation"", ""receipt"", ""payment_terminal"", ""other""

CURRENCY: Philippine/₱ = PHP. Hebrew/₪ = ILS. Thai/฿ = THB. Dollar/$ = USD (unless context says otherwise).

TIN: Philippine format: XXX-XXX-XXX-XXXXX. Non-Philippine = null.

AMOUNTS: Extract exactly as printed. All values MUST be numbers (not strings):
1. ""vatable_sales_amount"" - base amount BEFORE tax (number)
2. ""non_vatable_sales_amount"" - VAT-exempt, default 0 (number)
3. ""service_charge_amount"" - service charge, default 0 (number)
4. ""tax_amount"" - VAT/tax portion, MUST be SMALLER than vatable_sales (number)
LOGIC CHECK: vatable_sales_amount + tax_amount ≈ amount_paid

PAYMENT: ""method"" (Cash/Card/GCash/Mastercard/Visa etc), ""amount_paid"" (the total the customer paid, as number).

OUTPUT FORMAT (JSON ONLY — no explanation, no markdown):
{{
  ""document_type"": ""string"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""string"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|other|meal|taxi"",
  ""merchant"": {{ ""name"": ""string"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number, ""non_vatable_sales_amount"": number, ""service_charge_amount"": number, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number or null }},
  ""item_count"": number
}}{correctionsContext}";
    }
}
