using System.Text.Json;
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

    public InvoiceService(TripExDbContext db, OracleAiService oracle)
    {
        _db = db;
        _oracle = oracle;
    }

    public async Task<AnalyzeInvoiceResponse> AnalyzeAsync(string? imageBase64, string? imageUrl, string? country)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        try
        {
            var correctionsContext = await BuildCorrectionsContext();

            var imageContentUrl = imageBase64 != null
                ? (imageBase64.StartsWith("data:") ? imageBase64 : $"data:image/jpeg;base64,{imageBase64}")
                : imageUrl!;

            var systemPrompt = GetExtractionPrompt(correctionsContext, country);

            // ── SCAN 1: Extract ──
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

            var scan1Raw = await _oracle.ChatAsync(scan1Messages, 1024, 0.1);
            var scan1Data = OracleAiService.ParseJsonFromAiResponse(scan1Raw);

            // ── SCAN 2: Verify ──
            JsonElement finalData = scan1Data;
            try
            {
                var verifyPrompt = $@"You are a receipt/invoice verification expert. I extracted the following data from a receipt image. Please look at the SAME image and verify each field. If any value is WRONG, return the corrected JSON. If everything is correct, return the SAME JSON unchanged.

EXTRACTED DATA:
{scan1Data}

RULES:
- Compare each field against what you see in the image
- Pay special attention to: amounts, dates, merchant name, TIN, invoice number
- If VAT amount seems wrong, fix it
- Return ONLY the corrected/verified JSON, no explanation";

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

                var scan2Raw = await _oracle.ChatAsync(scan2Messages, 1024, 0.1);
                finalData = OracleAiService.ParseJsonFromAiResponse(scan2Raw);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Verification scan failed, using scan 1: {ex.Message}");
            }

            // Convert AI response to AlgoText-compatible flat fields format
            var fields = MapToAlgoTextFields(finalData);

            return new AnalyzeInvoiceResponse
            {
                Success = true,
                Fields = fields,
                RawResponse = scan1Raw
            };
        }
        catch (Exception ex)
        {
            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Maps the AI JSON response to AlgoText-compatible flat fields.
    /// TripEx client reads: fields.total, fields.totalVAT, fields.currency,
    /// fields.invoiceNumber, fields.invoiceDate, fields.type
    /// </summary>
    private static InvoiceFields MapToAlgoTextFields(JsonElement json)
    {
        var fields = new InvoiceFields();

        // Total = payment.amount_paid or sum of amounts
        if (json.TryGetProperty("payment", out var payment) &&
            payment.TryGetProperty("amount_paid", out var amountPaid) &&
            amountPaid.ValueKind == JsonValueKind.Number)
        {
            fields.Total = amountPaid.GetDecimal().ToString("F2");
        }
        else if (json.TryGetProperty("amounts", out var amounts))
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
            fields.Total = total.ToString("F2");
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

        // Merchant info
        if (json.TryGetProperty("merchant", out var merchant))
        {
            fields.MerchantName = merchant.TryGetProperty("name", out var name) ? name.GetString() : null;
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

        return fields;
    }

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

    private static string GetExtractionPrompt(string correctionsContext, string? country)
    {
        var countryHint = "";
        if (!string.IsNullOrEmpty(country))
        {
            countryHint = country.ToUpperInvariant() switch
            {
                "IL" => "\nCOUNTRY CONTEXT: Israel. Currency is likely ILS (₪). Date format DD/MM/YYYY → output YYYY-MM-DD. Look for Hebrew text. מספר חשבונית = Invoice Number. סה\"כ = Total. מע\"מ = VAT.",
                "PH" => "\nCOUNTRY CONTEXT: Philippines. Currency is likely PHP (₱). Date format MM/DD/YYYY → output YYYY-MM-DD. Look for TIN format XXX-XXX-XXX-XXXXX.",
                "TH" => "\nCOUNTRY CONTEXT: Thailand. Currency is likely THB (฿).",
                "US" => "\nCOUNTRY CONTEXT: United States. Currency is likely USD ($). Date format MM/DD/YYYY → output YYYY-MM-DD.",
                _ => $"\nCOUNTRY CONTEXT: {country}."
            };
        }

        return $@"You are an expert invoice/receipt OCR analyzer. Your job is to extract ONLY what is physically printed on the document. NEVER guess, calculate, or invent data.

GOLDEN RULE: If you cannot clearly read a value, return null. Wrong data is worse than no data.
{countryHint}

DOCUMENT TYPE: Classify as one of: ""sales_invoice"", ""official_receipt"", ""charge_invoice"", ""delivery_receipt"", ""credit_memo"", ""debit_memo"", ""purchase_order"", ""quotation"", ""receipt"", ""other""

VENDOR NAME: The vendor/store name is usually the LARGEST text at the TOP. Read it CHARACTER BY CHARACTER.

INVOICE NUMBER: Look for labels: ""Invoice No"", ""SI#"", ""OR NO."", ""Receipt #"". For Hebrew: ""מספר קבלה"", ""אסמכתא"", ""מספר חשבונית"".

DATE: READ THE EXACT DIGITS. Output format: YYYY-MM-DD.

CURRENCY: Philippine/₱ = PHP. Hebrew/₪ = ILS. Thai/฿ = THB. Dollar/$ = USD (unless context says otherwise).

TIN: Philippine format: XXX-XXX-XXX-XXXXX. Non-Philippine = null.

AMOUNTS: Extract exactly as printed:
1. ""vatable_sales_amount"" - base amount BEFORE tax
2. ""non_vatable_sales_amount"" - VAT-exempt (default 0)
3. ""service_charge_amount"" - service charge (default 0)  
4. ""tax_amount"" - VAT/tax portion (must be SMALLER than vatable_sales)
IGNORE: Cash/Change amounts.

PAYMENT: ""method"" (Cash/Card/GCash etc), ""amount_paid"" (what customer gave).

OUTPUT FORMAT (JSON ONLY):
{{
  ""document_type"": ""string"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""string"",
  ""merchant"": {{ ""name"": ""string"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number, ""non_vatable_sales_amount"": number, ""service_charge_amount"": number, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number or null }}
}}{correctionsContext}";
    }
}
