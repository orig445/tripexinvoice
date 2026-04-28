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

        return $@"You are a UNIVERSAL receipt/invoice OCR engine. Every receipt in the world — any language, any country, any currency.
Return STRICT JSON only — no markdown, no explanation.
{(locale != "Unknown locale" ? $"User country hint: {locale}" : "Auto-detect country from receipt content.")}

━━━ UNIVERSAL TRUTH: ALL RECEIPTS SHARE THE SAME STRUCTURE ━━━
Regardless of language, every receipt follows this layout:
  1. HEADER  — merchant name (largest text at top), address, tax ID
  2. ITEMS   — list of purchased items with prices
  3. SUMMARY — subtotal, tax/VAT line, then a SEPARATOR (line of dashes/equals/stars)
  4. TOTAL   — the FINAL PROMINENT NUMBER after the separator = what the customer paid
  5. PAYMENT — card/cash details, approval code
  6. FOOTER  — thank you message, reference numbers, QR/barcode

Use this structure to extract data. You do NOT need to know the language — the position and visual weight of numbers tells you what they are.

━━━ RULE 1: TOTAL AMOUNT (payment.amount_paid) ━━━
THE TOTAL IS ALWAYS:
  → The last/bottom prominent number before ""thank you / approved / paid"" messages
  → The number that appears after the final separator line on the receipt
  → The number with the largest font or bold formatting at the bottom of the summary
  → On payment terminal slips: the ONLY or LARGEST printed amount

FALLBACK — if no separator line exists:
  → Take the largest prominent number that is NOT a date, phone, tax ID, or reference code
  → It will always be at the BOTTOM of the items/summary section

You are multilingual — you already know what ""total"" means in every language.
Use that knowledge freely. Never return null if any amount is visible.

━━━ RULE 2: TAX / VAT (amounts.tax_amount) ━━━
THE TAX LINE IS ALWAYS:
  → A line ABOVE the total, BELOW the subtotal
  → Smaller than the subtotal (never larger)
  → Often shows a percentage: 23%, 19%, 17%, 12%, 10%, 7%, 5%...
  → Labeled in the receipt's language — you know what ""VAT/tax/IVA/MwSt/TVA/PTU/מע""מ/НДС/부가세/消費税/KDV/BTW/Moms..."" means in every language

If no tax line exists → set tax_amount = 0. Never null.
If tax appears larger than subtotal → they are swapped, correct them.

━━━ RULE 3: DECIMAL FORMAT ━━━
Determine the decimal format FROM THE COUNTRY you identified:
  EUROPEAN (DE/AT/FR/IT/ES/NL/BE/CH/PL/CZ/HU/RO/BG/HR/SK/SI/SE/NO/DK/FI/PT/GR...):
    COMMA = decimal · DOT = thousands · ""30,00""=30.00 · ""1.234,56""=1234.56
  REST OF WORLD (US/UK/IL/PH/AU/IN/SG/MY/ID/JP/KR/CN/TH/AE/BH/BR/ZA/MX/CA...):
    DOT = decimal · COMMA = thousands · ""1,234.56""=1234.56
  Always OUTPUT as plain dot-decimal: 30.00 · 1234.56

━━━ RULE 4: DATE ━━━
Output: YYYY-MM-DD. Use transaction date only (not expiry/accreditation dates).
  EAST ASIAN (KR/JP/CN/TW — Hangul/Kanji/CJK script): YYYY/MM/DD or YY/MM/DD — year is FIRST
  USA/CANADA: MM/DD/YYYY — month is first
  EUROPE with dots (DE/PL/CZ/AT/CH...): DD.MM.YYYY
  MOST OF WORLD: DD/MM/YYYY
  ISO already: keep as-is
  2-digit year: <50→20YY · ≥50→19YY · result must be ≤ today

━━━ RULE 5: CURRENCY ━━━
Priority: (1) ISO code printed on receipt → (2) currency symbol → (3) country's standard currency
All symbols: ₪=ILS · ₱=PHP · ฿=THB · ₩=KRW · ¥=JPY/CNY · €=EUR · £=GBP · ₹=INR · ₺=TRY
             ₫=VND · ₴=UAH · ₸=KZT · ₾=GEL · ৳=BDT · ₨=PKR · ₦=NGN · R$=BRL · R=ZAR · Rp=IDR · RM=MYR
NEVER output USD unless you see $ or USD or a US merchant address.
{learnedPatternsSection}
━━━ OUTPUT JSON ━━━
{{
  ""document_type"": ""receipt|invoice|payment_terminal|other"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""ISO-4217 code"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|meal|taxi|other"",
  ""merchant"": {{ ""name"": ""string or null"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number or null, ""non_vatable_sales_amount"": number or null, ""service_charge_amount"": number or null, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number, ""form_of_payment"": ""credit|cash|bank"", ""card_last4"": ""string or null"", ""card_type"": ""visa|mastercard|amex|diners|isracart|unionpay|jcb|other or null"" }},
  ""item_count"": number or null
}}

━━━ PAYMENT METHOD ━━━
• credit  → any card network name (Visa/MC/Amex/Diners/UnionPay/JCB/Eftpos/Interac/Isracard/Isracart...)
            EMV · Contactless · Chip · Swipe · any masked card number (****1234)
            any language term meaning ""credit/debit card"" — you know them all
• bank    → IBAN · SWIFT · BIC · any language term for ""bank transfer""
• cash    → default when no card/bank evidence found
• card_last4: find 4 consecutive digits after masking (****1234 · XXXX-5678 · ########6814)
• card_type from BIN: Visa=4xxx · Mastercard=5xxx/2xxx · Amex=34xx/37xx · Diners=36xx · UnionPay=62xx";
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
