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

    // ── Concurrency throttle: max 3 simultaneous OCI calls process-wide ──
    // Prevents overwhelming OCI when user uploads many receipts at once.
    private static readonly SemaphoreSlim _ociThrottle = new(3, 3);

    // ── Image size limits (raw base64 length) ──
    // 10MB raw bytes ≈ 13.3MB base64 chars
    private const int MaxImageBase64Length = 14_000_000;
    private const int MinImageBase64Length = 100;

    public OracleAiService(IHttpClientFactory httpClientFactory, IConfiguration config, TripExDbContext db)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(60); // hard cap — never hang on OCI
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

        return $@"You are a UNIVERSAL receipt/invoice OCR expert. You handle receipts from ANY country, ANY language, ANY currency.
Return STRICT JSON only — no markdown, no explanation.
{(locale != "Unknown locale" ? $"User country hint: {locale}" : "No country hint — auto-detect from receipt content.")}

━━━ STEP 1: AUTO-DETECT COUNTRY ━━━
Read these clues to identify the country/region:
• Hebrew text / ₪ / ""מע""מ"" → ISRAEL (ILS)
• Hangul (Korean) / ₩ / Seoul / +82 → KOREA (KRW)
• Japanese Kanji+Hiragana / ¥ / +81 → JAPAN (JPY)
• Chinese characters (mainland address) / 元 / +86 → CHINA (CNY)
• Thai script / ฿ / +66 → THAILAND (THB)
• German words (Betrag/Datum/Uhr/MwSt) / Berlin/Wien/Zürich → GERMANY/AUSTRIA/SWITZERLAND (EUR/CHF)
• French words (Total TTC/TVA/Montant) → FRANCE/BELGIUM/SWITZERLAND (EUR)
• Spanish (IVA/Factura/Importe) → SPAIN/LATIN AMERICA (EUR/local)
• Italian (Totale/IVA/Scontrino) → ITALY (EUR)
• Arabic text / SAR/AED/EGP → GULF/MIDDLE EAST
• Indian script or INR/Rs → INDIA (INR)
• Philippine address/TIN (XXX-XXX-XXX) / ₱ → PHILIPPINES (PHP)
• AUD + Australian address → AUSTRALIA (AUD)
• English + US address + $ → USA (USD)
• English + GBP / UK address → UK (GBP)
• SGD / Singapore → SINGAPORE (SGD)
• MYR / Ringgit / Malaysia → MALAYSIA (MYR)
• IDR / Rp / Indonesia → INDONESIA (IDR)
• BRL / R$ / Brazil → BRAZIL (BRL)
• ZAR / R / South Africa → SOUTH AFRICA (ZAR)
• TRY / ₺ / Turkish text → TURKEY (TRY)
• PLN / Poland → POLAND (PLN)
• CZK / Czech Republic → CZECH (CZK)
• SEK/NOK/DKK + Scandinavian text → SCANDINAVIA

━━━ STEP 2: DECIMAL FORMAT ━━━
EUROPEAN countries (DE/AT/FR/IT/ES/NL/BE/CH/PL/SE/NO/DK/FI/CZ/HU/PT/GR/RO/BG/HR/SK/SI):
  COMMA = decimal, DOT = thousands: ""12,00"" = 12.00 · ""1.234,56"" = 1234.56
US / UK / Philippines / Israel / Australia / India / most of Asia:
  DOT = decimal, COMMA = thousands: ""1,234.56"" = 1234.56
⚠ Always OUTPUT amounts as plain dot-decimal: 12.00 · 1234.56 · 78500.00

━━━ STEP 3: FIND TOTAL AMOUNT → payment.amount_paid ━━━
Look for the FINAL/LARGEST amount on the receipt. This is ALWAYS payment.amount_paid.
Labels by language (scan ALL of these):
• English:  TOTAL · GRAND TOTAL · AMOUNT DUE · TOTAL AMOUNT · SALE AMOUNT · NET AMOUNT
• German:   Betrag · Betrag EUR · Summe · Gesamt · Endbetrag · Rechnungsbetrag · Zahlungsbetrag · Zu zahlen
• French:   Total TTC · Montant Total · Net à payer · À payer · Total à régler
• Spanish:  Total · Importe Total · Total a pagar · Monto
• Italian:  Totale · Importo Totale · Totale a pagare · Da pagare
• Hebrew:   סה""כ · לתשלום · סכום לתשלום · סה""כ לתשלום · סכום
• Korean:   합계 · 결제금액 · 청구금액 · 총금액 · 지불금액
• Japanese: 合計 · お会計 · ご請求金額 · 合計金額 · 税込合計
• Thai:     ยอดรวม · ยอดชำระ · ราคารวม · จำนวนเงิน
• Chinese:  合计 · 总金额 · 应付金额 · 实付金额 · 金额合计
• Arabic:   المجموع · الإجمالي · المبلغ الإجمالي
• Hindi:    कुल · कुल राशि
• Portuguese: Total · Valor Total · Total a Pagar
• Turkish:  Toplam · Ödenecek Tutar
• Polish:   Razem · Do zapłaty · Suma
• Russian:  Итого · Сумма к оплате

⚠ RULE: If ANY total/amount line is visible → NEVER return null for payment.amount_paid.
⚠ PAYMENT TERMINAL RECEIPTS (Kundenbeleg, Lightspeed, SumUp, Square, Verifone, Ingenico, etc.):
  Often show only one amount. ""Betrag EUR 12,00"" = payment.amount_paid = 12.00
  ""Zahlung erfolgt / Approved"" = it was paid — extract the amount shown before it.

━━━ STEP 4: FIND VAT / TAX → amounts.tax_amount ━━━
If no explicit tax line: set tax_amount = 0 (not null).
Labels:
• English:       VAT · Tax · Sales Tax · GST · HST · PST
• German:        MwSt. · MwSt · USt · Mehrwertsteuer · inkl. 19% MwSt · inkl. 7% MwSt
• French:        TVA · T.V.A. · taxe
• Spanish:       IVA · Impuesto
• Italian:       IVA
• Hebrew:        מע""מ
• Korean:        부가세 · 부가가치세 (10%)
• Japanese:      消費税 · 税 (10%)
• Thai:          ภาษีมูลค่าเพิ่ม · VAT (7%)
• Chinese:       增值税 · 税
• Australian:    GST (10%)
• Indian:        GST · CGST · SGST · IGST
• Turkish:       KDV
• Polish:        VAT · PTU
Standard rates (use only if explicitly printed): DE 19%/7%, FR 20%, IL 17%, PH 12%, TH 7%, JP 10%, KR 10%, AU 10%

━━━ STEP 5: DATE FORMAT BY COUNTRY ━━━
Output always as YYYY-MM-DD. Find the TRANSACTION date (not permit/accreditation date).
• EAST ASIAN (KRW/JPY/CNY/TWD, Korean/Japanese/Chinese text): YY/MM/DD or YYYY/MM/DD
  ""26/02/13"" (KRW) = 2026-02-13 (Feb 13 2026). Year comes FIRST.
• USA (USD + US address): MM/DD/YYYY → ""02/13/2026"" = 2026-02-13
• EUROPE/ISRAEL/PHILIPPINES/most of world: DD/MM/YYYY → ""13/02/2026"" = 2026-02-13
• German format: ""16.01.2025"" = 2026-01-16 (DD.MM.YYYY with dots)
• 2-digit year: <50 → 20YY, ≥50 → 19YY
• Sanity: result must be ≤ today and > year 2000 (unless clearly historical)
• IGNORE: expiry dates, permit dates, accreditation dates

━━━ STEP 6: CURRENCY DETECTION ━━━
Symbols: ₱=PHP · ₪=ILS · ฿=THB · $=USD · ₩=KRW · ¥=JPY (or CNY by context) · €=EUR · £=GBP
         ₹=INR · ₺=TRY · R$=BRL · R=ZAR · RM=MYR · Rp=IDR · ฿=THB · ₫=VND · ₴=UAH · ₸=KZT
Text: EUR/USD/GBP/ILS/KRW/JPY/CNY/THB/PHP/AED/SAR/AUD/SGD/MYR/IDR/INR/BRL/ZAR/TRY/PLN/CZK/SEK/NOK/DKK
NEVER default to USD if other currency evidence exists.
{learnedPatternsSection}
━━━ OUTPUT FORMAT (JSON ONLY) ━━━
{{
  ""document_type"": ""receipt|invoice|payment_terminal|other"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""ISO-4217 code e.g. EUR/USD/ILS/KRW"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|meal|taxi|other"",
  ""merchant"": {{ ""name"": ""string or null"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number or null, ""non_vatable_sales_amount"": number or null, ""service_charge_amount"": number or null, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number, ""form_of_payment"": ""credit|cash|bank"", ""card_last4"": ""string or null"", ""card_type"": ""visa|mastercard|amex|diners|isracart|other or null"" }},
  ""item_count"": number or null
}}

PAYMENT METHOD DETECTION:
• credit → EMV · Contactless · Chip · Swipe · Visa · Mastercard · Amex · Diners · card number visible
           אשראי · כרטיס · סליקה · אשראי · Kreditkarte · Carte bancaire · Tarjeta · Pagamento con carta
• bank   → transfer · wire · IBAN · bank · העברה · Überweisung · Virement · Transferencia
• cash   → cash · מזומן · Bargeld · Espèces · Efectivo · Contanti · 現金 · 현금 · เงินสด · default if unclear
• card_last4: look for ****1234 · XXXX-5678 · ########6814 · masked digits followed by 4 clear digits
• card_type from BIN: Visa (4xxx) · Mastercard (5xxx/2xxx) · Amex (34xx/37xx) · Diners (36xx) · Isracard/Isracart";
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
