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
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetDecimal();
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

        // Already in YYYY-MM-DD format — validate day/month aren't swapped
        var match = Regex.Match(dateStr, @"^(\d{4})-(\d{2})-(\d{2})$");
        if (!match.Success) return json;

        int year = int.Parse(match.Groups[1].Value);
        int month = int.Parse(match.Groups[2].Value);
        int day = int.Parse(match.Groups[3].Value);

        bool needsSwap = false;
        var effectiveCurrency = currency?.ToUpperInvariant();
        var effectiveCountry = country?.ToUpperInvariant();

        // If month > 12, it's definitely wrong — swap
        if (month > 12 && day <= 12)
        {
            needsSwap = true;
            Console.WriteLine($"[OCR-DATE] Month={month} > 12, swapping with day={day}");
        }
        // Philippines (MM/DD) — if the AI read DD/MM but country is PH, and day ≤ 12, we can't be sure
        // Israel (DD/MM) — if the AI read MM/DD but country is IL, and month ≤ 12, ambiguous
        // Only swap when we're confident (month > 12)
        else if (month <= 12 && day <= 12)
        {
            // Ambiguous case — trust the AI's output but log it
            Console.WriteLine($"[OCR-DATE] Ambiguous date {dateStr} for currency={effectiveCurrency}, country={effectiveCountry} — keeping as-is");
        }

        if (needsSwap)
        {
            var corrected = $"{year:D4}-{day:D2}-{month:D2}";
            // Validate the corrected date is real
            if (DateTime.TryParseExact(corrected, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                Console.WriteLine($"[OCR-DATE] Corrected: {dateStr} → {corrected}");
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText()) ?? new();
                dict["invoice_date"] = corrected;
                var rebuilt = JsonSerializer.SerializeToUtf8Bytes(dict);
                return JsonDocument.Parse(rebuilt).RootElement.Clone();
            }
        }

        return json;
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

        if (json.TryGetProperty("payment", out var payment) &&
            payment.TryGetProperty("amount_paid", out var amountPaid) &&
            amountPaid.ValueKind == JsonValueKind.Number)
        {
            totalValue = amountPaid.GetDecimal();
        }

        // Fallback: calculate from amounts
        if (!totalValue.HasValue || totalValue.Value == 0)
        {
            if (json.TryGetProperty("amounts", out var amounts))
            {
                decimal total = 0;
                if (amounts.TryGetProperty("vatable_sales_amount", out var vat) && vat.ValueKind == JsonValueKind.Number)
                    total += vat.GetDecimal();
                if (amounts.TryGetProperty("non_vatable_sales_amount", out var nonVat) && nonVat.ValueKind == JsonValueKind.Number)
                    total += nonVat.GetDecimal();
                if (amounts.TryGetProperty("service_charge_amount", out var sc) && sc.ValueKind == JsonValueKind.Number)
                    total += sc.GetDecimal();
                if (amounts.TryGetProperty("tax_amount", out var tax) && tax.ValueKind == JsonValueKind.Number)
                    total += tax.GetDecimal();
                if (total > 0) totalValue = total;
            }
        }

        if (totalValue.HasValue)
        {
            fields.Total = totalValue.Value.ToString("F2");
            fields.TotalAmount = totalValue.Value.ToString("F2");
        }

        // TotalVAT = amounts.tax_amount
        if (json.TryGetProperty("amounts", out var amt) &&
            amt.TryGetProperty("tax_amount", out var taxAmt) &&
            taxAmt.ValueKind == JsonValueKind.Number)
        {
            fields.TotalVAT = taxAmt.GetDecimal().ToString("F2");
        }

        // Direct fields
        fields.Currency = json.TryGetProperty("currency", out var cur) ? cur.GetString() : null;
        fields.InvoiceNumber = json.TryGetProperty("invoice_number", out var inv) ? inv.GetString() : null;
        fields.InvoiceDate = json.TryGetProperty("invoice_date", out var dt) ? dt.GetString() : null;
        fields.Type = json.TryGetProperty("document_type", out var docType) ? docType.GetString() : null;

        // Expense Type
        if (json.TryGetProperty("expense_type", out var expType))
        {
            var expTypeStr = expType.GetString();
            fields.ExpenseType = ValidExpenseTypes.Contains(expTypeStr ?? "") ? expTypeStr : "other";
        }

        // Merchant info — with name cleaning
        if (json.TryGetProperty("merchant", out var merchant))
        {
            fields.MerchantName = CleanMerchantName(merchant.TryGetProperty("name", out var name) ? name.GetString() : null);
            fields.MerchantTin = merchant.TryGetProperty("tin", out var tin) ? tin.GetString() : null;
            fields.MerchantAddress = merchant.TryGetProperty("address", out var addr) ? addr.GetString() : null;
            fields.MerchantCity = merchant.TryGetProperty("city", out var city) ? city.GetString() : null;
        }

        // Payment
        if (json.TryGetProperty("payment", out var pay))
        {
            fields.PaymentMethod = pay.TryGetProperty("method", out var method) ? method.GetString() : null;
            if (pay.TryGetProperty("amount_paid", out var paid) && paid.ValueKind == JsonValueKind.Number)
                fields.AmountPaid = paid.GetDecimal().ToString("F2");
        }

        // Extra Details — store the entire raw JSON for support/debugging
        fields.ExtraDetails = json.GetRawText();

        return fields;
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

═══ STEP-BY-STEP EXTRACTION PROCESS ═══

STEP 1 — VENDOR/MERCHANT NAME:
- Look at the LARGEST text at the TOP of the document
- Read it CHARACTER BY CHARACTER — do NOT guess or autocomplete
- Do NOT confuse product names with the store/business name
- Do NOT invent letters that aren't there

STEP 2 — FIND THE TOTAL (LARGEST AMOUNT):
- Look for labels: ""AMOUNT DUE"", ""TOTAL"", ""סה""כ לתשלום"", ""Grand Total""
- This should be the LARGEST monetary value on the document
- IGNORE: Cash given, Change amounts
- If you see ""Amount Paid"" or ""Cash"" — that's what the customer gave, NOT the total

STEP 3 — FIND TAX/VAT (SMALLER THAN SUBTOTAL):
- Look for: ""VAT"", ""Tax"", ""מע""מ"", ""Output Tax""
- ⚠️ CRITICAL: tax_amount MUST BE SMALLER than vatable_sales_amount
- If tax seems larger than subtotal, you have them SWAPPED — switch them!

STEP 4 — DATE:
- Read the EXACT DIGITS on the document
- Look for the TRANSACTION date, NOT accreditation/permit/certification dates
- Output as YYYY-MM-DD

STEP 5 — INVOICE NUMBER:
- Look for: ""Invoice No"", ""SI#"", ""OR NO."", ""Receipt #"", ""מספר קבלה"", ""אסמכתא""
- ⚠️ This is NOT the TIN/tax ID! Invoice numbers are usually shorter and numeric

STEP 6 — EXPENSE CATEGORY:
- Classify the purchase as ONE of these exact values:
  ""business_meal"" — restaurant, cafe, catering for business
  ""vehicle"" — gas, car rental, car maintenance
  ""entertainment"" — events, shows, recreation
  ""hotel"" — accommodation, lodging
  ""internet"" — internet service, phone bills, telecom
  ""parking"" — parking fees
  ""meal"" — food/drink purchase (non-business)
  ""taxi"" — taxi, ride-sharing, transportation
  ""other"" — anything that doesn't fit above

═══ NEGATIVE EXAMPLES (DO NOT DO THIS) ═══
❌ Do NOT put the subtotal value in tax_amount
❌ Do NOT put the tax value in vatable_sales_amount  
❌ Do NOT put TIN in invoice_number
❌ Do NOT put invoice_number in TIN
❌ Do NOT invent or autocomplete merchant names

DOCUMENT TYPE: Classify as one of: ""sales_invoice"", ""official_receipt"", ""charge_invoice"", ""delivery_receipt"", ""credit_memo"", ""debit_memo"", ""purchase_order"", ""quotation"", ""receipt"", ""other""

CURRENCY: Philippine/₱ = PHP. Hebrew/₪ = ILS. Thai/฿ = THB. Dollar/$ = USD (unless context says otherwise).

TIN: Philippine format: XXX-XXX-XXX-XXXXX. Non-Philippine = null.

AMOUNTS: Extract exactly as printed:
1. ""vatable_sales_amount"" - base amount BEFORE tax
2. ""non_vatable_sales_amount"" - VAT-exempt (default 0)
3. ""service_charge_amount"" - service charge (default 0)  
4. ""tax_amount"" - VAT/tax portion (MUST be SMALLER than vatable_sales)
LOGIC CHECK: vatable_sales_amount + tax_amount ≈ amount_paid

PAYMENT: ""method"" (Cash/Card/GCash etc), ""amount_paid"" (the total the customer paid).

OUTPUT FORMAT (JSON ONLY):
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
