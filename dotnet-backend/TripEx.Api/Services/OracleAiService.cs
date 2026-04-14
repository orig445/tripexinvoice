using System.Text.Json;
using System.Text.RegularExpressions;
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

    public OracleAiService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClient = httpClientFactory.CreateClient();
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
        if (string.IsNullOrWhiteSpace(base64Part) || base64Part.Length < 100)
            throw new ArgumentException("Image data is too small or empty — likely corrupted");

        var prompt = PrepareSystemPrompt(countryHint);

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

        Console.WriteLine($"[OCI] Sending request to: {_endpoint}");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Failed to connect to OCI endpoint: {ex.Message}", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[OCI] Error {(int)response.StatusCode}: {responseBody}");
            throw new HttpRequestException(
                $"Oracle AI error: {(int)response.StatusCode} - {responseBody}",
                null,
                response.StatusCode);
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
    /// Prepare system prompt based on country hint
    /// </summary>
    private string PrepareSystemPrompt(string? countryHint)
    {
        string locale = countryHint?.ToUpperInvariant() switch
        {
            "IL" => "Israel (DD/MM/YYYY, ILS ₪)",
            "PH" => "Philippines (MM/DD/YYYY, PHP ₱)",
            "US" => "United States (MM/DD/YYYY, USD $)",
            "TH" => "Thailand (DD/MM/YYYY, THB ฿)",
            _ => "Unknown locale"
        };

        return $@"You are an expert invoice/receipt OCR analyzer. Extract data for {locale}.
Return STRICT JSON only — no markdown, no explanation.

GOLDEN RULE: If you cannot clearly read a value, return null. Wrong data is worse than no data.

DOCUMENT TYPES to recognize:
- Standard invoice/receipt, Payment terminal (Maya, GCash, BPI), Digital receipt, Handwritten receipt

EXTRACTION RULES:
1. VENDOR: Largest text at TOP. For terminals: business name, NOT terminal brand.
2. AMOUNT: This is the FINAL TOTAL amount the customer pays — INCLUDING VAT, taxes, service charges, and all fees. Look for ""TOTAL"", ""GRAND TOTAL"", ""AMOUNT DUE"", ""SALE AMOUNT"", ""סה""כ לתשלום"", ""סה""כ"". Do NOT use subtotal or pre-tax amount. Return as NUMBER (13328.00 not ""13,328.00"").
3. TAX: The VAT/tax portion only. Must be SMALLER than the total. If tax > total, they are SWAPPED. If no tax visible, use 0.
4. DATE: Transaction date only (not permit/accreditation). Output as YYYY-MM-DD.
5. INVOICE NUMBER: Document/transaction number, NOT TIN/tax ID.
6. CATEGORY: Must be one of: business_meal, vehicle, entertainment, hotel, internet, parking, meal, taxi, other.

CURRENCY: ₱=PHP, ₪=ILS, ฿=THB, $=USD (unless context says otherwise).

OUTPUT FORMAT:
{{
  ""document_type"": ""string"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""string"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|other|meal|taxi"",
  ""merchant"": {{ ""name"": ""string"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number, ""non_vatable_sales_amount"": 0, ""service_charge_amount"": 0, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number }},
  ""item_count"": number
}}";
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
