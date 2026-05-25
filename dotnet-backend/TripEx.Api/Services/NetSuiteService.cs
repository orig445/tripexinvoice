using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// שירות לתקשורת עם NetSuite ERP
///
/// אימות: Token-Based Authentication (TBA) — OAuth 1.0 HMAC-SHA256
///
/// כל בקשה חייבת לכלול Authorization header עם חתימה OAuth.
/// </summary>
public class NetSuiteService
{
    private readonly IHttpClientFactory _http;
    private readonly TripExDbContext _db;

    public NetSuiteService(IHttpClientFactory http, TripExDbContext db)
    {
        _http = http;
        _db = db;
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// בדיקת חיבור ל-NetSuite
    /// Tests connectivity and credentials
    /// </summary>
    public async Task<NetSuiteConnectionTestResponse> TestConnectionAsync(NetSuiteConfigDto config)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Use SuiteQL to get account info — lightweight query
            var url = GetSuiteQlUrl(config.AccountId);
            var body = new { q = "SELECT id, companyname FROM subsidiary WHERE iselimination = 'F' ORDER BY id OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY" };

            var client = _http.CreateClient("NetSuite");
            var request = BuildRequest(HttpMethod.Post, url, config, body);

            var response = await client.SendAsync(request);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return new NetSuiteConnectionTestResponse
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode}: {errorBody.Truncate(200)}",
                    ResponseTimeMs = sw.ElapsedMilliseconds
                };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<NetSuiteSuiteQlResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var accountName = result?.Items?.FirstOrDefault()?
                .GetValueOrDefault("companyname")?.ToString() ?? "חשבון NetSuite";

            return new NetSuiteConnectionTestResponse
            {
                Success = true,
                AccountId = config.AccountId,
                AccountName = accountName,
                Message = $"חיבור תקין — {accountName}",
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new NetSuiteConnectionTestResponse
            {
                Success = false,
                Error = ex.Message,
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// שליפת הוצאות מ-NetSuite העומדות בקריטריונים
    /// Fetches vendor bills / expenses above the threshold from NetSuite
    /// </summary>
    public async Task<List<NetSuiteExpense>> FetchExpensesAboveThresholdAsync(
        NetSuiteConfigDto config,
        decimal threshold,
        string? dateFrom = null,
        string? dateTo = null,
        int maxResults = 500)
    {
        // Build date filters
        var dateFilter = BuildDateFilter(dateFrom, dateTo);

        // SuiteQL query for Vendor Bills above threshold
        var query = $@"
SELECT
    t.id,
    t.tranid,
    TO_CHAR(t.trandate, 'YYYY-MM-DD') AS trandate,
    t.foreignamount AS amount,
    t.foreigntaxamount AS taxamount,
    t.currency,
    t.memo,
    e.id AS vendorid,
    e.entityid AS vendorexternalid,
    e.companyname AS vendorname,
    t.status,
    t.subsidiary
FROM transaction t
LEFT JOIN entity e ON t.entity = e.id
WHERE t.recordtype = 'vendorbill'
  AND t.foreignamount > {threshold}
  AND t.voided = 'F'
  {dateFilter}
ORDER BY t.trandate DESC
OFFSET 0 ROWS FETCH NEXT {maxResults} ROWS ONLY";

        var expenses = await RunSuiteQlAsync<NetSuiteExpense>(config, query, MapExpenseRow);
        return expenses;
    }

    /// <summary>
    /// עדכון מספר ההקצאה ב-NetSuite (כ-custom field)
    /// Updates the allocation number on the vendor bill in NetSuite
    /// </summary>
    public async Task<bool> UpdateAllocationNumberAsync(
        NetSuiteConfigDto config,
        string internalId,
        string allocationNumber,
        string status)
    {
        try
        {
            // Update custom fields on the VendorBill record
            // Fields: custbody_il_allocation_number, custbody_il_allocation_status
            var url = $"{GetRecordBaseUrl(config.AccountId)}/vendorbill/{internalId}";
            var patchBody = new
            {
                custbody_il_allocation_number = allocationNumber,
                custbody_il_allocation_status = status,
                custbody_il_allocation_date = DateTime.UtcNow.ToString("yyyy-MM-dd")
            };

            var client = _http.CreateClient("NetSuite");
            var request = BuildRequest(new HttpMethod("PATCH"), url, config, patchBody);

            var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NETSUITE] UpdateAllocationNumber failed for {internalId}: {ex.Message}");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<List<T>> RunSuiteQlAsync<T>(
        NetSuiteConfigDto config,
        string query,
        Func<Dictionary<string, object?>, T> mapper)
    {
        var url = GetSuiteQlUrl(config.AccountId);
        var body = new { q = query.Trim() };

        var client = _http.CreateClient("NetSuite");
        var request = BuildRequest(HttpMethod.Post, url, config, body);

        var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"NetSuite SuiteQL failed [{(int)response.StatusCode}]: {err.Truncate(500)}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<NetSuiteSuiteQlResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result?.Items?.Select(mapper).ToList() ?? new List<T>();
    }

    /// <summary>
    /// ממפה שורת SuiteQL למודל NetSuiteExpense
    /// </summary>
    private static NetSuiteExpense MapExpenseRow(Dictionary<string, object?> row)
    {
        return new NetSuiteExpense
        {
            InternalId = row.GetString("id"),
            TranId = row.GetString("tranid"),
            TranDate = row.GetString("trandate"),
            Amount = row.GetDecimal("amount"),
            TaxAmount = row.GetDecimal("taxamount"),
            Currency = row.GetString("currency") is { Length: > 0 } c ? c : "ILS",
            VendorId = row.GetString("vendorexternalid"),
            VendorName = row.GetString("vendorname"),
            Memo = row.GetString("memo"),
            Subsidiary = row.GetString("subsidiary"),
            RecordType = "vendorbill",
            Status = row.GetString("status"),
        };
    }

    private static string BuildDateFilter(string? dateFrom, string? dateTo)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(dateFrom))
        {
            if (DateTime.TryParse(dateFrom, out var from))
                parts.Add($"AND t.trandate >= TO_DATE('{from:yyyy-MM-dd}', 'YYYY-MM-DD')");
        }
        else
        {
            // Default: last 30 days
            var from = DateTime.UtcNow.AddDays(-30);
            parts.Add($"AND t.trandate >= TO_DATE('{from:yyyy-MM-dd}', 'YYYY-MM-DD')");
        }

        if (!string.IsNullOrWhiteSpace(dateTo))
        {
            if (DateTime.TryParse(dateTo, out var to))
                parts.Add($"AND t.trandate <= TO_DATE('{to:yyyy-MM-dd}', 'YYYY-MM-DD')");
        }

        return string.Join("\n  ", parts);
    }

    private static string GetSuiteQlUrl(string accountId) =>
        $"https://{NormalizeAccountId(accountId)}.suitetalk.api.netsuite.com/services/rest/query/v1/suiteql";

    private static string GetRecordBaseUrl(string accountId) =>
        $"https://{NormalizeAccountId(accountId)}.suitetalk.api.netsuite.com/services/rest/record/v1";

    /// <summary>
    /// NetSuite account IDs with underscores need dashes in URLs
    /// e.g. "1234567_SB1" → "1234567-sb1"
    /// </summary>
    private static string NormalizeAccountId(string accountId) =>
        accountId.Replace("_", "-").ToLowerInvariant();

    // ─────────────────────────────────────────────────────────────────
    // OAuth 1.0 TBA Signature Generation
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// בונה HttpRequestMessage עם Authorization header של OAuth 1.0 TBA
    /// Builds an HttpRequestMessage with the OAuth 1.0 TBA Authorization header
    /// </summary>
    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        string url,
        NetSuiteConfigDto config,
        object? body = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = GenerateNonce();

        var authHeader = BuildOAuthHeader(
            method: method.Method.ToUpperInvariant(),
            url: url,
            accountId: config.AccountId,
            consumerKey: config.ConsumerKey,
            consumerSecret: config.ConsumerSecret,
            tokenKey: config.TokenKey,
            tokenSecret: config.TokenSecret,
            timestamp: timestamp,
            nonce: nonce
        );

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", authHeader);
        request.Headers.Add("Prefer", "transient");

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// מייצר Authorization header מלא ל-OAuth 1.0 TBA
    ///
    /// OAuth specification: https://tools.ietf.org/html/rfc5849
    /// NetSuite TBA docs: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4637721744.html
    /// </summary>
    private static string BuildOAuthHeader(
        string method,
        string url,
        string accountId,
        string consumerKey,
        string consumerSecret,
        string tokenKey,
        string tokenSecret,
        string timestamp,
        string nonce)
    {
        // Step 1 — Collect OAuth parameters (sorted by key for signature)
        var oauthParams = new SortedDictionary<string, string>
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA256",
            ["oauth_timestamp"] = timestamp,
            ["oauth_token"] = tokenKey,
            ["oauth_version"] = "1.0"
        };

        // Step 2 — Build the normalized parameter string
        var normalizedParams = string.Join("&",
            oauthParams.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        // Step 3 — Build the signature base string
        var baseUri = new Uri(url);
        var baseUrl = $"{baseUri.Scheme}://{baseUri.Host}{baseUri.AbsolutePath}"; // no query string
        var signatureBase = string.Join("&",
            method,
            Uri.EscapeDataString(baseUrl),
            Uri.EscapeDataString(normalizedParams));

        // Step 4 — Build the signing key
        var signingKey = $"{Uri.EscapeDataString(consumerSecret)}&{Uri.EscapeDataString(tokenSecret)}";

        // Step 5 — Compute HMAC-SHA256 signature
        var keyBytes = Encoding.ASCII.GetBytes(signingKey);
        var dataBytes = Encoding.ASCII.GetBytes(signatureBase);
        var signature = Convert.ToBase64String(new HMACSHA256(keyBytes).ComputeHash(dataBytes));

        // Step 6 — Build the Authorization header value
        return $"realm=\"{Uri.EscapeDataString(accountId)}\", " +
               $"oauth_consumer_key=\"{consumerKey}\", " +
               $"oauth_nonce=\"{nonce}\", " +
               $"oauth_signature=\"{Uri.EscapeDataString(signature)}\", " +
               $"oauth_signature_method=\"HMAC-SHA256\", " +
               $"oauth_timestamp=\"{timestamp}\", " +
               $"oauth_token=\"{tokenKey}\", " +
               $"oauth_version=\"1.0\"";
    }

    private static string GenerateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
               .Replace("+", "A").Replace("/", "B").Replace("=", "C");
}

// ─────────────────────────────────────────────────────────────────
// Extension helpers for Dictionary row parsing
// ─────────────────────────────────────────────────────────────────

internal static class DictionaryExtensions
{
    public static string GetString(this Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    public static decimal GetDecimal(this Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return 0;
        return decimal.TryParse(v.ToString(), out var dec) ? dec : 0;
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "…";
}
