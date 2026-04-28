using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Service for calling Oracle Generative AI — Gemini 2.5 Flash via OCI API
/// </summary>
public class OracleAiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _compartmentId;
    private readonly TripExDbContext _db;

    // ── Concurrency throttle: max 10 simultaneous OCI calls process-wide ──
    // Higher limit prevents request queueing under bursts of bulk uploads.
    private static readonly SemaphoreSlim _ociThrottle = new(10, 10);

    // ── Image size limits (raw base64 length) ──
    // 10MB raw bytes ≈ 13.3MB base64 chars
    private const int MaxImageBase64Length = 14_000_000;
    private const int MinImageBase64Length = 100;

    public OracleAiService(IHttpClientFactory httpClientFactory, IConfiguration config, TripExDbContext db)
    {
        _httpClient = httpClientFactory.CreateClient();
        // Gemini Vision can take 30-50s on heavier images. 25s was too aggressive and caused
        // 504s in QA → triggering retries → exceeding host (IIS/Nginx) timeout → Thread Abort.
        // 60s gives the model room to finish on a single attempt for typical receipts.
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _db = db;
        _apiKey = config["Oracle:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ORACLE_API_KEY")
            ?? throw new InvalidOperationException("Oracle API key not configured");
        _endpoint = config["Oracle:Endpoint"]
            ?? "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
        _model = config["Oracle:Model"]
            ?? "google.gemini-2.5-flash";
        _compartmentId = config["Oracle:CompartmentId"];
    }

    /// <summary>
    /// Invoice-specific call: sends image + country hint to Gemini 2.5 Flash
    /// </summary>
    public async Task<string> CallGeminiFlashAsync(string imageBase64, string countryHint)
    {
        // ── Validate input ──
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("imageBase64 cannot be empty");

        // Ensure proper data URI prefix
        var imageUrl = imageBase64.StartsWith("data:")
            ? imageBase64
            : $"data:image/jpeg;base64,{imageBase64}";

        // Validate base64 content (strip prefix and check)
        var base64Part = imageUrl.Contains(",") ? imageUrl[(imageUrl.IndexOf(',') + 1)..] : imageUrl;
        if (string.IsNullOrWhiteSpace(base64Part) || base64Part.Length < MinImageBase64Length)
            throw new ArgumentException("Image data is too small or empty — likely corrupted");
        if (base64Part.Length > MaxImageBase64Length)
            throw new ArgumentException(
                $"Image too large ({base64Part.Length / 1_000_000}MB base64). Max ~10MB. Please compress on the client side.");

        var prompt = await PrepareSystemPromptAsync(countryHint);

        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = prompt },
            new()
            {
                Role = "user",
                Content = new object[]
                {
                    new { type = "image_url", image_url = new { url = imageUrl } },
                    new { type = "text", text = "Extract data from this invoice." }
                }
            }
        };

        return await ChatAsync(messages, 2048, 0.1);
    }

    /// <summary>
    /// FALLBACK: Focused single-purpose call — extract ONLY the final total amount.
    /// Used when the main scan returns 0/missing total. Aggressive prompt + higher temp for variation.
    /// Returns plain decimal string (e.g. "123.45") or empty if truly nothing found.
    /// </summary>
    public async Task<string> CallGeminiTotalOnlyAsync(string imageBase64, string countryHint, int retryRound = 1)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("imageBase64 cannot be empty");

        var imageUrl = imageBase64.StartsWith("data:")
            ? imageBase64
            : $"data:image/jpeg;base64,{imageBase64}";

        // Round-specific instructions to force different reasoning paths
        var roundHint = retryRound switch
        {
            1 => "Look at the BOTTOM of the receipt for the FINAL grand total (the largest number, usually labeled Total, Sum, סה\"כ, Итого, 合計, 总计, 합계, 总额, Suma, Gesamt, Totale, Total à payer, รวม, Kabuuan).",
            2 => "Find the LARGEST monetary number on the receipt — that is almost always the final amount paid. Ignore subtotals, item prices, change, tip, and tax-only lines.",
            _ => "Scan EVERY number on this receipt. Output the highest monetary value that represents what the customer paid. If there are multiple candidates, pick the one closest to a 'TOTAL' or 'PAID' label in any language."
        };

        var prompt = $@"You are an OCR expert. Your ONLY job is to extract the FINAL TOTAL AMOUNT from this receipt/invoice.
{roundHint}

Country hint: {countryHint}

CRITICAL RULES:
- Output ONLY a JSON object: {{""total"": ""<number>"", ""currency"": ""<3-letter code>""}}
- The number must be a positive decimal (e.g. ""123.45"" or ""1234"").
- NEVER output 0, null, or empty. If you cannot see a clear total, output the largest monetary number visible.
- Do NOT include currency symbols in the total field — only digits and one decimal point.
- No explanations, no markdown, no extra fields.";

        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = prompt },
            new()
            {
                Role = "user",
                Content = new object[]
                {
                    new { type = "image_url", image_url = new { url = imageUrl } },
                    new { type = "text", text = "Extract the final total amount only." }
                }
            }
        };

        // Higher temperature on later rounds to break out of stuck patterns
        double temp = retryRound switch { 1 => 0.2, 2 => 0.4, _ => 0.6 };
        var raw = await ChatAsync(messages, 256, temp);

        // Parse the focused JSON response
        try
        {
            var json = ParseJsonFromAiResponse(raw);
            if (json.TryGetProperty("total", out var totalProp))
            {
                var totalStr = totalProp.ValueKind == JsonValueKind.Number
                    ? totalProp.GetRawText()
                    : (totalProp.GetString() ?? "");
                return totalStr.Trim();
            }
        }
        catch { /* fall through to regex */ }

        // Last resort: regex-extract first decimal — use ParseAmountSmart for locale-aware normalization
        var m = Regex.Match(raw ?? "", @"(\d{1,3}(?:[,\.\s]\d{3})*(?:[\.,]\d{1,2})?|\d+(?:[\.,]\d{1,2})?)");
        if (!m.Success) return "";
        var parsed = InvoiceService.ParseAmountSmart(m.Value);
        return parsed.HasValue ? parsed.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    /// <summary>
    /// General-purpose chat (used by ChatService and others)
    /// </summary>
    public async Task<string> ChatAsync(
        List<OracleMessage> messages,
        int maxTokens = 1024,
        double temperature = 0.3)
    {
        // ── Validate messages ──
        if (messages == null || messages.Count == 0)
            throw new ArgumentException("Messages list cannot be empty");

        var requestDict = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature
        };

        // OCI requires compartmentId in the request body
        if (!string.IsNullOrEmpty(_compartmentId))
            requestDict["compartmentId"] = _compartmentId;

        var serializedBody = JsonSerializer.Serialize(requestDict);

        // ── Validate serialized JSON before sending ──
        try
        {
            using var testDoc = JsonDocument.Parse(serializedBody);
            Console.WriteLine($"[OCI] Request body valid JSON, length={serializedBody.Length}, model={_model}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Request body serialization produced invalid JSON: {ex.Message}");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            serializedBody,
            System.Text.Encoding.UTF8,
            "application/json");

        // ── Throttle: wait for an OCI slot (max 3 concurrent process-wide) ──
        var throttleStart = DateTime.UtcNow;
        await _ociThrottle.WaitAsync();
        var waitedMs = (DateTime.UtcNow - throttleStart).TotalMilliseconds;
        if (waitedMs > 100)
            Console.WriteLine($"[OCI] Throttle wait: {waitedMs:F0}ms (queue depth)");

        HttpResponseMessage response;
        string responseBody;
        try
        {
            Console.WriteLine($"[OCI] Sending request to: {_endpoint}");
            try
            {
                response = await _httpClient.SendAsync(request);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ex.CancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout reached — surface as 504 so retry layer treats it as transient
                throw new OciApiException(
                    $"OCI request timed out after {_httpClient.Timeout.TotalSeconds}s",
                    504, null);
            }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Failed to connect to OCI endpoint: {ex.Message}", ex);
            }

            responseBody = await response.Content.ReadAsStringAsync();
        }
        finally
        {
            _ociThrottle.Release();
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[OCI] Error {(int)response.StatusCode}: {responseBody}");
            throw new OciApiException(
                $"Oracle AI error: {(int)response.StatusCode} - {responseBody}",
                (int)response.StatusCode,
                responseBody);
        }

        // ── Validate response is valid JSON ──
        if (string.IsNullOrWhiteSpace(responseBody))
            throw new InvalidOperationException("OCI returned empty response");

        Console.WriteLine($"[OCI] Response length={responseBody.Length}, status={response.StatusCode}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[OCI] Invalid JSON response: {responseBody[..Math.Min(500, responseBody.Length)]}");
            throw new InvalidOperationException(
                $"OCI returned invalid JSON (length={responseBody.Length}): {ex.Message}. First 200 chars: {responseBody[..Math.Min(200, responseBody.Length)]}");
        }

        using (doc)
        {
            // ── Extract content with safe navigation ──
            if (!doc.RootElement.TryGetProperty("choices", out var choices))
                throw new InvalidOperationException($"OCI response missing 'choices'. Keys: {string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))}");

            if (choices.GetArrayLength() == 0)
                throw new InvalidOperationException("OCI response 'choices' array is empty");

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("message", out var message))
                throw new InvalidOperationException($"OCI choice[0] missing 'message'. Keys: {string.Join(", ", firstChoice.EnumerateObject().Select(p => p.Name))}");

            if (!message.TryGetProperty("content", out var contentProp))
                throw new InvalidOperationException($"OCI message missing 'content'. Keys: {string.Join(", ", message.EnumerateObject().Select(p => p.Name))}");

            var content = contentProp.GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                // Check for finish_reason to understand why content is empty
                var finishReason = firstChoice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "unknown";
                Console.WriteLine($"[OCI] Warning: Empty content, finish_reason={finishReason}");
                throw new InvalidOperationException($"OCI returned empty content (finish_reason={finishReason})");
            }

            Console.WriteLine($"[OCI] Content extracted, length={content.Length}");
            return content;
        }
    }

    /// <summary>
    /// Prepare system prompt based on country hint + learned patterns from DB
    /// </summary>
    private async Task<string> PrepareSystemPromptAsync(string? countryHint)
    {
        string locale = countryHint?.ToUpperInvariant() switch
        {
            "IL" => "Israel (DD/MM/YYYY, ILS ₪)",
            "PH" => "Philippines (MM/DD/YYYY, PHP ₱)",
            "US" => "United States (MM/DD/YYYY, USD $)",
            "TH" => "Thailand (DD/MM/YYYY, THB ฿)",
            _ => "Unknown locale"
        };

        // ── Load learned patterns from DB ──
        string learnedPatternsSection = "";
        try
        {
            var patterns = await _db.OcrTrainingPatterns
                .Where(p => p.Country == null || p.Country == "" || p.Country == (countryHint ?? "").ToUpper())
                .OrderByDescending(p => p.Confidence)
                .Take(20)
                .ToListAsync();

            if (patterns.Count > 0)
            {
                var lines = patterns.Select(p =>
                    $"- {p.FieldName.ToUpper()}: {p.PatternRule} ({p.Confidence:F0}% confidence, from {p.SourceCount} receipts)");
                learnedPatternsSection = $@"

LEARNED PATTERNS (from analyzed receipts — use these to improve accuracy):
{string.Join("\n", lines)}
";
                Console.WriteLine($"[OCI] Injected {patterns.Count} learned patterns into prompt");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCI] Warning: Could not load training patterns: {ex.Message}");
        }

        return $@"You are an expert invoice/receipt OCR analyzer. Extract data for {locale}.
Return STRICT JSON only — no markdown, no explanation.

🔴🔴🔴 ABSOLUTE RULES — VIOLATING THESE IS A CRITICAL FAILURE:

STEP 1 — DETECT first: identify the receipt's LANGUAGE and COUNTRY from the script/text/currency symbol BEFORE extracting fields. This determines which label dictionary to use.

STEP 2 — Use the matching language dictionary below to find each MANDATORY field.

📋 MULTI-LANGUAGE LABEL DICTIONARY (use the row matching the receipt's language):

═══ TOTAL / AMOUNT PAID labels (the FINAL amount the customer paid, including tax) ═══
  English:    TOTAL, GRAND TOTAL, AMOUNT DUE, AMOUNT PAID, BALANCE DUE, TOTAL DUE, SALE AMOUNT, NET AMOUNT
  Hebrew:     סה""כ לתשלום, סה""כ, לתשלום, סך הכל, סכום, סך לתשלום, חשבון
  Spanish:    TOTAL, IMPORTE TOTAL, TOTAL A PAGAR, MONTO TOTAL, IMPORTE
  Filipino/Tagalog: KABUUAN, TOTAL, BAYARAN, BAYAD, KABUUANG HALAGA
  Korean:     합계, 총액, 결제금액, 청구금액, 총합계, 받을금액
  Japanese:   合計, 総計, 請求金額, お支払い, お支払金額, ご請求額
  Thai:       รวมทั้งสิ้น, ยอดรวม, จำนวนเงินรวม, ยอดสุทธิ
  French:     TOTAL, MONTANT TOTAL, NET À PAYER, TOTAL TTC, À PAYER
  German:     GESAMT, GESAMTBETRAG, SUMME, ZU ZAHLEN, ENDBETRAG
  Italian:    TOTALE, IMPORTO TOTALE, TOTALE DA PAGARE, NETTO A PAGARE
  Portuguese: TOTAL, VALOR TOTAL, TOTAL A PAGAR, MONTANTE
  Arabic:     المجموع, الإجمالي, المبلغ المستحق, الإجمالي الكلي
  Russian:    ИТОГО, ВСЕГО, СУММА К ОПЛАТЕ, ОБЩАЯ СУММА
  Chinese:    总计, 合计, 应付金额, 总额, 实付金额

═══ TAX / VAT labels ═══
  English:    VAT, TAX, GST, SALES TAX, HST
  Hebrew:     מע""מ, מס
  Spanish:    IVA, IMPUESTO
  Filipino:   VAT, BUWIS
  Korean:     부가세, 세금, 부가가치세
  Japanese:   消費税, 税
  Thai:       ภาษี, ภาษีมูลค่าเพิ่ม, VAT
  French:     TVA, TAXE
  German:     MWST, MEHRWERTSTEUER, STEUER, USt
  Italian:    IVA, IMPOSTA
  Portuguese: IVA, IMPOSTO
  Arabic:     ضريبة, ضريبة القيمة المضافة
  Russian:    НДС, НАЛОГ
  Chinese:    税, 增值税

═══ INVOICE / RECEIPT NUMBER labels ═══
  English:    Invoice #, Receipt #, Order #, Transaction, Ref #, Doc #, Bill No, OR No, SI No
  Hebrew:     מספר חשבונית, מס' חשבונית, חשבונית מס, מספר קבלה, מס' קבלה, הזמנה, מס. עסקה
  Spanish:    Factura N°, Recibo N°, Núm. de factura, Folio
  Filipino:   Resibo Numero, OR No, SI No, Bilang ng resibo
  Korean:     영수증번호, 거래번호, 청구번호
  Japanese:   領収書番号, 請求書番号, 取引番号, 伝票番号
  Thai:       เลขที่ใบเสร็จ, เลขที่ใบกำกับภาษี, เลขที่
  French:     N° facture, N° reçu, Numéro de facture
  German:     Rechnungsnr, Belegnr, Quittungsnr, Rechnungsnummer
  Italian:    N° fattura, N° ricevuta, Numero fattura
  Portuguese: Nº fatura, Nº recibo, Número da fatura
  Arabic:     رقم الفاتورة, رقم الإيصال
  Russian:    Номер счета, Номер чека, Чек №
  Chinese:    发票号, 收据号, 发票号码, 单据号

═══ DATE labels & formats by country ═══
  Date labels: DATE, תאריך, FECHA, PETSA, 날짜, 日付, วันที่, DATE, DATUM, DATA, التاريخ, ДАТА, 日期
  Formats:
    - Israel/EU/UK/Philippines/most-of-world: DD/MM/YYYY or DD/MM/YY
    - USA: MM/DD/YYYY or MM/DD/YY
    - Korea/Japan/China/Taiwan: YYYY/MM/DD or YY/MM/DD (East Asian convention)
    - ISO (anywhere): YYYY-MM-DD

═══ CURRENCY DETECTION ═══
  Symbols: ₪=ILS, $=USD (or PHP/AUD/CAD by context), €=EUR, £=GBP, ¥=JPY/CNY, ₩=KRW, ₱=PHP, ฿=THB, ₹=INR, ₽=RUB
  Country fallback: IL=ILS, US=USD, PH=PHP, GB=GBP, JP=JPY, KR=KRW, TH=THB, EU=EUR, FR=EUR, DE=EUR, IT=EUR, ES=EUR, PT=EUR, CN=CNY, RU=RUB, IN=INR, AU=AUD, CA=CAD

🔴 MANDATORY FIELDS — NEVER return 0 or null unless receipt is genuinely blank:
   1. payment.amount_paid → MUST be a NUMBER > 0. Use the largest amount near a TOTAL label from the dictionary above.
      • Pick the FINAL/LARGEST total (after tax), not subtotal.
      • If multiple totals visible, the FINAL TOTAL is at the bottom of the receipt.
      • Strip currency symbols, commas, decimals → return as plain number (e.g. 13328.00 not ""$13,328.00"").

   2. invoice_date → REQUIRED. Find ANY date pattern. Convert to YYYY-MM-DD using the country/format rules above.

   3. invoice_number → REQUIRED. Find using the language dictionary above.
      • DO NOT confuse with: TIN, ח.פ., ע.מ., RUT, NIF, tax-ID, vendor-ID, phone numbers, dates, postal codes.
      • If multiple candidates, prefer the one explicitly labeled (Invoice/Receipt/Order/Ref).

   4. currency → ALWAYS detect using the currency table above. NEVER return null.

GOLDEN RULE: Inspect EVERY visible number on the image before giving up. NEVER return 0/null on a non-blank receipt.

OUTPUT also include these top-level fields for diagnostics:
   • detected_language: ""he|en|es|fil|ko|ja|th|fr|de|it|pt|ar|ru|zh|other""
   • detected_country: ISO-2 code (best guess from receipt content)


DOCUMENT TYPES to recognize:
- Standard invoice/receipt, Payment terminal (Maya, GCash, BPI), Digital receipt, Handwritten receipt

EXTRACTION RULES:
1. VENDOR: Largest text at TOP. For terminals: business name, NOT terminal brand.
2. AMOUNT: Look for ""SALE AMOUNT"", ""TOTAL"", ""סה""כ"". Return as NUMBER (13328.00 not ""13,328.00"").
3. TAX: Must be SMALLER than subtotal. If tax > subtotal, they are SWAPPED. If no tax visible, use 0.
4. DATE: Transaction date only (not permit/accreditation). Output as YYYY-MM-DD.
   ⚠️ CRITICAL — DATE FORMAT BY COUNTRY/CURRENCY (the receipt's printed format depends on origin):
   - KOREA (KRW, ""THE PLAZA"", Seoul, +82, Hangul): format is **YY/MM/DD** (East Asian convention).
     Example: ""26/02/13"" = **2026-02-13** (Feb 13, 2026), NOT 2013-02-26.
   - JAPAN (JPY, +81), CHINA (CNY, +86), TAIWAN (TWD): also **YY/MM/DD** or **YYYY/MM/DD**.
   - USA (USD, English receipts from US merchants): **MM/DD/YY** or **MM/DD/YYYY**.
     Example: ""02/13/26"" = 2026-02-13.
   - ISRAEL (ILS, ₪, Hebrew), EUROPE (EUR), PHILIPPINES (PHP), UK (GBP), most of world: **DD/MM/YY** or **DD/MM/YYYY**.
     Example: ""13/02/26"" = 2026-02-13.
   - If the year value is ≤ 31 and appears FIRST with two digits, and currency is KRW/JPY/CNY/TWD → it's the YEAR (YY/MM/DD).
   - If a 2-digit year is < 50 → assume 20YY. If ≥ 50 → assume 19YY.
   - Sanity check: the resulting date should be in the past or near-present, NOT 10+ years old unless clearly historical.
   - When uncertain, prefer the format matching the receipt's currency/country over the default DD/MM/YY.
5. INVOICE NUMBER: Document/transaction number, NOT TIN/tax ID.
6. CATEGORY: Must be one of: business_meal, vehicle, entertainment, hotel, internet, parking, meal, taxi, other.

CURRENCY: ₱=PHP, ₪=ILS, ฿=THB, $=USD, ₩=KRW, ¥=JPY/CNY (unless context says otherwise).
{learnedPatternsSection}
OUTPUT FORMAT:
{{
  ""document_type"": ""string"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""string"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|other|meal|taxi"",
  ""merchant"": {{ ""name"": ""string"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number, ""non_vatable_sales_amount"": 0, ""service_charge_amount"": 0, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number, ""form_of_payment"": ""credit|cash|bank"", ""card_last4"": ""string or null"", ""card_type"": ""visa|mastercard|amex|diners|isracart|other or null"" }},
  ""item_count"": number
}}

PAYMENT FORM RULES:
- form_of_payment: Identify if payment was by credit card, cash, or bank transfer.
  Look for: ""אשראי"", ""credit"", ""כרטיס"", ""card"", ""visa"", ""mastercard"", ""מזומן"", ""cash"", ""העברה"", ""bank"", ""EMV"", ""Contactless"".
  If EMV, Contactless, chip, swipe, or card number visible → ""credit"".
  If no payment method found → default to ""cash"".
- card_last4: If credit card, extract last 4 digits (look for ****1234, XXXX-1234, etc.)
- card_type: Identify card network from text or BIN:
  Visa (starts with 4), Mastercard (starts with 5), Amex (starts with 3), Isracard/Isracart, Diners, etc.";
    }

    /// <summary>
    /// Parse JSON from AI response (handles markdown wrappers, brace counting, truncation repair)
    /// </summary>
    public static JsonElement ParseJsonFromAiResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("AI response is empty");

        // Strip markdown wrappers
        var cleaned = Regex.Replace(raw, @"```json\s*\n?", "");
        cleaned = Regex.Replace(cleaned, @"```\s*\n?", "").Trim();

        // Remove control characters (except normal whitespace)
        cleaned = Regex.Replace(cleaned, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

        var startIdx = cleaned.IndexOf('{');
        if (startIdx < 0)
            throw new InvalidOperationException($"No JSON object found in AI response. First 200 chars: {cleaned[..Math.Min(200, cleaned.Length)]}");

        // Brace-counting extraction
        int depth = 0, endIdx = -1;
        bool inString = false, escapeNext = false;
        for (int i = startIdx; i < cleaned.Length; i++)
        {
            char ch = cleaned[i];
            if (escapeNext) { escapeNext = false; continue; }
            if (ch == '\\') { escapeNext = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
        }

        if (endIdx >= 0)
        {
            cleaned = cleaned[startIdx..(endIdx + 1)];
        }
        else
        {
            // Truncated JSON — attempt repair by closing open braces/brackets
            Console.WriteLine($"[OCI-PARSE] Detected truncated JSON (depth={depth}), attempting repair...");
            cleaned = cleaned[startIdx..];

            // Close any open string
            if (inString) cleaned += "\"";

            // Remove trailing comma or colon
            cleaned = Regex.Replace(cleaned, @"[,:\s]+$", "");

            // Close open braces/brackets
            // Recount
            int openBraces = 0, openBrackets = 0;
            bool inStr = false; bool esc = false;
            for (int i = 0; i < cleaned.Length; i++)
            {
                char c = cleaned[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') openBraces++;
                else if (c == '}') openBraces--;
                else if (c == '[') openBrackets++;
                else if (c == ']') openBrackets--;
            }

            for (int i = 0; i < openBrackets; i++) cleaned += "]";
            for (int i = 0; i < openBraces; i++) cleaned += "}";

            Console.WriteLine($"[OCI-PARSE] Repaired JSON, added {openBrackets} ] and {openBraces} }}");
        }

        // Fix trailing commas
        cleaned = Regex.Replace(cleaned, @",\s*}", "}");
        cleaned = Regex.Replace(cleaned, @",\s*]", "]");

        try
        {
            return JsonDocument.Parse(cleaned).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[OCI-PARSE] Final parse failed: {ex.Message}");
            Console.Error.WriteLine($"[OCI-PARSE] Cleaned content: {cleaned[..Math.Min(500, cleaned.Length)]}");
            throw new InvalidOperationException(
                $"Failed to parse AI response as JSON: {ex.Message}. Content length={cleaned.Length}");
        }
    }

    /// <summary>
    /// Decode unicode escape sequences
    /// </summary>
    public static string DecodeUnicodeEscapes(string str)
    {
        return Regex.Replace(str, @"\\u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }
}

/// <summary>
/// Exception carrying OCI HTTP status + raw response body for diagnostics.
/// </summary>
public class OciApiException : Exception
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public OciApiException(string message, int statusCode, string? responseBody) : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
