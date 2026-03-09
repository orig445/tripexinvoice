using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Invoice OCR analysis using Oracle AI Vision
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

    public async Task<AnalyzeInvoiceResponse> AnalyzeAsync(string? imageBase64, string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageBase64) && string.IsNullOrEmpty(imageUrl))
            return new AnalyzeInvoiceResponse { Success = false, Error = "Either imageBase64 or imageUrl must be provided" };

        try
        {
            // Build corrections context from past user corrections
            var correctionsContext = await BuildCorrectionsContext();

            // Build image content for Oracle Vision
            var imageContentUrl = imageBase64 != null
                ? (imageBase64.StartsWith("data:") ? imageBase64 : $"data:image/jpeg;base64,{imageBase64}")
                : imageUrl!;

            var systemPrompt = GetExtractionPrompt(correctionsContext);

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

            return new AnalyzeInvoiceResponse
            {
                Success = true,
                Data = DeserializeInvoiceData(finalData),
                RawResponse = scan1Raw
            };
        }
        catch (Exception ex)
        {
            return new AnalyzeInvoiceResponse { Success = false, Error = ex.Message };
        }
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

    private static InvoiceData DeserializeInvoiceData(JsonElement json)
    {
        var data = new InvoiceData
        {
            DocumentType = json.TryGetProperty("document_type", out var dt) ? dt.GetString() : null,
            InvoiceNumber = json.TryGetProperty("invoice_number", out var inv) ? inv.GetString() : null,
            InvoiceDate = json.TryGetProperty("invoice_date", out var id) ? id.GetString() : null,
            Currency = json.TryGetProperty("currency", out var cur) ? cur.GetString() : null,
        };

        if (json.TryGetProperty("merchant", out var m))
        {
            data.Merchant = new MerchantInfo
            {
                Name = m.TryGetProperty("name", out var n) ? n.GetString() : null,
                Tin = m.TryGetProperty("tin", out var t) ? t.GetString() : null,
                Address = m.TryGetProperty("address", out var a) ? a.GetString() : null,
                City = m.TryGetProperty("city", out var c) ? c.GetString() : null,
            };
        }

        if (json.TryGetProperty("amounts", out var am))
        {
            data.Amounts = new AmountsInfo
            {
                VatableSalesAmount = am.TryGetProperty("vatable_sales_amount", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null,
                NonVatableSalesAmount = am.TryGetProperty("non_vatable_sales_amount", out var nv) && nv.ValueKind == JsonValueKind.Number ? nv.GetDecimal() : null,
                ServiceChargeAmount = am.TryGetProperty("service_charge_amount", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDecimal() : null,
                TaxAmount = am.TryGetProperty("tax_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDecimal() : null,
            };
        }

        if (json.TryGetProperty("payment", out var p))
        {
            data.Payment = new PaymentInfo
            {
                Method = p.TryGetProperty("method", out var pm) ? pm.GetString() : null,
                AmountPaid = p.TryGetProperty("amount_paid", out var pa) && pa.ValueKind == JsonValueKind.Number ? pa.GetDecimal() : null,
            };
        }

        return data;
    }

    private static string GetExtractionPrompt(string correctionsContext)
    {
        return $@"You are an expert invoice/receipt OCR analyzer. Your job is to extract ONLY what is physically printed on the document. NEVER guess, calculate, or invent data.

GOLDEN RULE: If you cannot clearly read a value, return null. Wrong data is worse than no data.

DOCUMENT TYPE: Classify as one of: ""sales_invoice"", ""official_receipt"", ""charge_invoice"", ""delivery_receipt"", ""credit_memo"", ""debit_memo"", ""purchase_order"", ""quotation"", ""receipt"", ""other""

VENDOR NAME: The vendor/store name is usually the LARGEST text at the TOP. Read it CHARACTER BY CHARACTER.

INVOICE NUMBER: Look for labels: ""Invoice No"", ""SI#"", ""OR NO."", ""Receipt #"". For Hebrew: ""מספר קבלה"", ""אסמכתא"".

DATE: READ THE EXACT DIGITS. Philippine = MM/DD/YYYY → output YYYY-MM-DD. Hebrew = DD/MM/YYYY → output YYYY-MM-DD.

CURRENCY: Philippine/₱ = PHP. Hebrew/₪ = ILS. Thai/฿ = THB.

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
