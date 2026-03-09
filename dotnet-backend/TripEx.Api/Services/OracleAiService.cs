using System.Text.Json;
using System.Text.RegularExpressions;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Service for calling Oracle Generative AI (Llama 4 Maverick)
/// </summary>
public class OracleAiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;

    public OracleAiService(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKey = config["Oracle:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ORACLE_API_KEY")
            ?? throw new InvalidOperationException("Oracle API key not configured");
        _endpoint = config["Oracle:Endpoint"]
            ?? "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
        _model = config["Oracle:Model"]
            ?? "meta.llama-4-maverick-17b-128e-instruct-fp8";
    }

    /// <summary>
    /// Send messages to Oracle AI and get response text
    /// </summary>
    public async Task<string> ChatAsync(
        List<OracleMessage> messages,
        int maxTokens = 1024,
        double temperature = 0.3)
    {
        var requestBody = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            max_tokens = maxTokens,
            temperature
        };

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
    /// Parse JSON from AI response (handles markdown wrappers, brace counting)
    /// </summary>
    public static JsonElement ParseJsonFromAiResponse(string raw)
    {
        var cleaned = Regex.Replace(raw, @"```json\n?", "");
        cleaned = Regex.Replace(cleaned, @"```\n?", "").Trim();

        // Find outermost { } using brace counting
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

        // Clean trailing commas
        cleaned = Regex.Replace(cleaned, @",\s*}", "}");
        cleaned = Regex.Replace(cleaned, @",\s*]", "]");

        return JsonDocument.Parse(cleaned).RootElement;
    }

    /// <summary>
    /// Decode unicode escape sequences like \u05e9\u05dc\u05d5\u05dd
    /// </summary>
    public static string DecodeUnicodeEscapes(string str)
    {
        return Regex.Replace(str, @"\\u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }
}
