using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// שירות לתקשורת עם רשות המיסים הישראלית
/// Service for Israeli Tax Authority (מסים) API communication
///
/// ═══════════════════════════════════════════════════════════════
/// מספר הקצאה (Allocation Number):
/// ════════════════════════════
/// על-פי תיקון לחוק מע"מ, חשבוניות מס מעל סכום מסוים (5,000 ₪ ומעלה)
/// חייבות לקבל מספר הקצאה מרשות המיסים לפני שניתן לנכות מס תשומות.
///
/// נקודת קצה בסביבת sandbox:
///   https://openapi.taxes.gov.il/shaam/tsandbox/longtail/mas-hashlama/v1/
///
/// נקודת קצה בייצור:
///   https://openapi.taxes.gov.il/shaam/production/longtail/mas-hashlama/v1/
///
/// ═══════════════════════════════════════════════════════════════
/// מצב נוכחי: בסיס ומבנה כללי קיים — ה-API מחכה להשלמה.
/// Current state: base structure ready — API integration pending.
/// ═══════════════════════════════════════════════════════════════
/// </summary>
public class TaxAuthorityService
{
    private readonly IHttpClientFactory _http;
    private readonly TripExDbContext _db;

    // API Endpoints
    private const string SandboxBaseUrl = "https://openapi.taxes.gov.il/shaam/tsandbox/longtail/mas-hashlama/v1";
    private const string ProductionBaseUrl = "https://openapi.taxes.gov.il/shaam/production/longtail/mas-hashlama/v1";

    public TaxAuthorityService(IHttpClientFactory http, TripExDbContext db)
    {
        _http = http;
        _db = db;
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// בקשת מספר הקצאה לחשבונית מ-NetSuite
    /// Requests an allocation number for an invoice from the Tax Authority
    ///
    /// [!] יש להשלים את פרטי ה-API כשיהיו זמינים מרשות המיסים
    /// [!] Complete API details when available from Tax Authority
    /// </summary>
    public async Task<AllocationResponse> RequestAllocationNumberAsync(
        TaxAuthorityConfigDto config,
        AllocationRequest request)
    {
        if (!config.IsEnabled)
        {
            return new AllocationResponse
            {
                Success = false,
                Error = "חיבור לרשות המיסים לא מופעל. הפעל בהגדרות האינטגרציה.",
                Status = "disabled"
            };
        }

        try
        {
            // Get access token
            var token = await GetAccessTokenAsync(config);
            if (token == null)
            {
                return new AllocationResponse
                {
                    Success = false,
                    Error = "אימות מול רשות המיסים נכשל — בדוק פרטי גישה",
                    Status = "auth_failed"
                };
            }

            var baseUrl = config.UseSandbox ? SandboxBaseUrl : ProductionBaseUrl;
            var endpoint = $"{baseUrl}/allocate";

            // Build the API request payload
            // ════════════════════════════════════════════════════════
            // TODO: מבנה ה-payload הסופי יש להתאים ל-API הרשמי של רשות המיסים
            // TODO: Adjust payload structure to match official Tax Authority API spec
            // ════════════════════════════════════════════════════════
            var payload = BuildAllocationPayload(config, request);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var client = _http.CreateClient("TaxAuthority");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(httpRequest);
            var rawBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"[TAX-AUTH] Allocation request for {request.InvoiceNumber}: " +
                              $"HTTP {(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                return new AllocationResponse
                {
                    Success = false,
                    Status = "api_error",
                    Error = $"HTTP {(int)response.StatusCode}: {rawBody.Truncate(300)}",
                    RawResponse = rawBody
                };
            }

            return ParseAllocationResponse(rawBody);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TAX-AUTH] RequestAllocationNumber exception: {ex.Message}");
            return new AllocationResponse
            {
                Success = false,
                Error = $"שגיאה בתקשורת עם רשות המיסים: {ex.Message}",
                Status = "exception"
            };
        }
    }

    /// <summary>
    /// בדיקת תקינות מספר הקצאה קיים
    /// Validates an existing allocation number
    /// </summary>
    public async Task<bool> ValidateAllocationNumberAsync(
        TaxAuthorityConfigDto config,
        string allocationNumber)
    {
        // TODO: Implement validation call to Tax Authority
        // Placeholder — returns true for now
        await Task.CompletedTask;
        Console.WriteLine($"[TAX-AUTH] ValidateAllocationNumber [{allocationNumber}] — STUB");
        return !string.IsNullOrWhiteSpace(allocationNumber);
    }

    // ─────────────────────────────────────────────────────────────────
    // Private: Authentication
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// קבלת OAuth 2.0 access token מרשות המיסים
    /// Gets an OAuth 2.0 access token from the Tax Authority
    ///
    /// [!] יש להשלים את ה-URL ופרטי האימות הסופיים
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(TaxAuthorityConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) ||
            string.IsNullOrWhiteSpace(config.ClientSecret))
        {
            Console.Error.WriteLine("[TAX-AUTH] Missing ClientId or ClientSecret");
            return null;
        }

        try
        {
            var tokenUrl = config.UseSandbox
                ? "https://openapi.taxes.gov.il/shaam/tsandbox/oauth2/token"
                : "https://openapi.taxes.gov.il/shaam/production/oauth2/token";

            var client = _http.CreateClient("TaxAuthority");
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", config.ClientId),
                new KeyValuePair<string, string>("client_secret", config.ClientSecret),
                new KeyValuePair<string, string>("scope", "mas_hashlama"),
            });

            var response = await client.PostAsync(tokenUrl, form);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"[TAX-AUTH] Token request failed: {err.Truncate(200)}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var tok)
                ? tok.GetString()
                : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TAX-AUTH] GetAccessToken exception: {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Private: Request/Response Mapping
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// בניית payload לבקשת הקצאה
    ///
    /// [!] מבנה זה הוא הערכה של ה-API — יש להתאים לפי התיעוד הרשמי
    /// [!] This structure is an estimate — adjust per official API docs
    /// </summary>
    private static object BuildAllocationPayload(TaxAuthorityConfigDto config, AllocationRequest req)
    {
        return new
        {
            // פרטי מוציא החשבונית
            maasikId = config.BusinessId,

            // פרטי החשבונית
            invoiceReferenceNumber = req.InvoiceNumber,
            invoiceDate = req.InvoiceDate,
            documentType = MapDocumentType(req.DocumentType),

            // סכומים
            totalAmount = req.TotalAmount,
            vatAmount = req.TaxAmount,
            amountExcludingVat = req.AmountBeforeTax,
            currency = req.Currency,

            // פרטי הספק
            supplierVatId = req.SupplierId,
            supplierName = req.SupplierName,

            // פרטי הלקוח
            clientVatId = req.CustomerId,
            clientName = req.CustomerName,

            // מזהה חיצוני
            externalId = $"NS-{req.SourceTransactionId}",
        };
    }

    /// <summary>
    /// פירוש תגובת ה-API
    /// </summary>
    private static AllocationResponse ParseAllocationResponse(string rawBody)
    {
        try
        {
            var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            // ════════════════════════════════════════════════════════
            // TODO: התאם את המאפיינים לפי ה-API הרשמי של רשות המיסים
            // TODO: Adjust property names per official Tax Authority API spec
            // ════════════════════════════════════════════════════════

            var allocationNumber = root.TryGetProperty("allocationNumber", out var an) ? an.GetString()
                : root.TryGetProperty("misparHaktzaa", out var mh) ? mh.GetString()
                : root.TryGetProperty("allocation_number", out var aln) ? aln.GetString()
                : null;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : "approved";

            if (!string.IsNullOrWhiteSpace(allocationNumber))
            {
                return new AllocationResponse
                {
                    Success = true,
                    AllocationNumber = allocationNumber,
                    Status = status ?? "approved",
                    RawResponse = rawBody
                };
            }

            // Check for error / rejection
            var errorMsg = root.TryGetProperty("errorMessage", out var em) ? em.GetString()
                : root.TryGetProperty("message", out var msg) ? msg.GetString()
                : "תגובה לא צפויה מרשות המיסים";

            return new AllocationResponse
            {
                Success = false,
                Status = status ?? "rejected",
                Error = errorMsg,
                RawResponse = rawBody
            };
        }
        catch (Exception ex)
        {
            return new AllocationResponse
            {
                Success = false,
                Error = $"שגיאה בפירוש תגובת רשות המיסים: {ex.Message}",
                RawResponse = rawBody
            };
        }
    }

    private static int MapDocumentType(string type) => type switch
    {
        "חשבונית מס" => 305,
        "חשבונית מס קבלה" => 320,
        "קבלה" => 400,
        "חשבון עסקה" => 330,
        _ => 305 // ברירת מחדל: חשבונית מס
    };
}
