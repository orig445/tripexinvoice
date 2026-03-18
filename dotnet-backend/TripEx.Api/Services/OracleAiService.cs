using System.Text.Json;
using System.Text.RegularExpressions;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Service for calling Oracle Generative AI — Gemini 3 Flash via OCI API
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
    /// Invoice-specific call: sends image + country hint to Gemini 3 Flash
    /// </summary>
    public async Task<string> CallGeminiFlashAsync(string imageBase64, string countryHint)
    {
        var prompt = PrepareSystemPrompt(countryHint);

        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = prompt },
            new()
            {
                Role = "user",
                Content = new object[]
                {
                    new { type = "image_url", image_url = new { url = imageBase64.StartsWith("data:") ? imageBase64 : $"data:image/jpeg;base64,{imageBase64}" } },
                    new { type = "text", text = "Extract data from this invoice." }
                }
            }
        };

        return await ChatAsync(messages, 1000, 0.1);
    }

    /// <summary>
    /// General-purpose chat (used by ChatService and others)
    /// </summary>
    public async Task<string> ChatAsync(
        List<OracleMessage> messages,
        int maxTokens = 1024,
        double temperature = 0.3)
    {
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

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Oracle AI error: {(int)response.StatusCode} - {errText}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? "";
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
2. AMOUNT: Look for ""SALE AMOUNT"", ""TOTAL"", ""סה""כ"". Return as NUMBER (13328.00 not ""13,328.00"").
3. TAX: Must be SMALLER than subtotal. If tax > subtotal, they are SWAPPED. If no tax visible, use 0.
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
    /// Parse JSON from AI response (handles markdown wrappers, brace counting)
    /// </summary>
    public static JsonElement ParseJsonFromAiResponse(string raw)
    {
        var cleaned = Regex.Replace(raw, @"```json\n?", "");
        cleaned = Regex.Replace(cleaned, @"```\n?", "").Trim();

        var startIdx = cleaned.IndexOf('{');
        if (startIdx >= 0)
        {
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
                cleaned = cleaned[startIdx..(endIdx + 1)];
        }

        cleaned = Regex.Replace(cleaned, @",\s*}", "}");
        cleaned = Regex.Replace(cleaned, @",\s*]", "]");

        return JsonDocument.Parse(cleaned).RootElement;
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
