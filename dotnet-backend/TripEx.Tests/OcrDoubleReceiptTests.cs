using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TripEx.Api.Data;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Tests;

/// <summary>
/// Reproduces the "second receipt returns zeros" bug and verifies the fix.
///
/// Bug: After the first scan, the DbContext keeps tracked entities (InvoiceScanLog)
/// in state "Unchanged". On the second SaveChangesAsync call within the same scope
/// a conflict causes a silent failure → AnalyzeAsync outer catch fires → returns zeros.
///
/// Fix: Detach all tracked entities at the start of SaveScanLog before adding the new log.
/// </summary>
public class OcrDoubleReceiptTests
{
    // ── Minimal valid Gemini response that ParseJsonFromAiResponse can handle ──
    private const string FakeGeminiContent =
        """
        {
          "document_type": "receipt",
          "invoice_number": "RCP-001",
          "invoice_date": "2024-01-15",
          "currency": "ILS",
          "expense_type": "business_meal",
          "merchant": { "name": "Test Cafe", "tin": null, "address": "Tel Aviv", "city": "Tel Aviv" },
          "amounts": {
            "vatable_sales_amount": 100.00,
            "non_vatable_sales_amount": 0,
            "service_charge_amount": 0,
            "tax_amount": 17.00
          },
          "payment": {
            "method": "cash",
            "amount_paid": 117.00,
            "form_of_payment": "cash",
            "card_last4": null,
            "card_type": null
          },
          "item_count": 2
        }
        """;

    // Wraps the content inside the OCI response envelope
    private static string BuildOciResponse(string content)
    {
        var escaped = content
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");
        return $$"""{"choices":[{"message":{"content":"{{escaped}}"},"finish_reason":"stop"}]}""";
    }

    private static TripExDbContext BuildInMemoryDb(string dbName)
    {
        var opts = new DbContextOptionsBuilder<TripExDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TripExDbContext(opts);
    }

    private static OracleAiService BuildFakeOracleService(
        TripExDbContext db,
        string httpResponseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(httpResponseBody, statusCode);
        var httpClient = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oracle:ApiKey"]       = "fake-key",
                ["Oracle:Endpoint"]     = "https://fake-oci/chat",
                ["Oracle:Model"]        = "fake-model",
                ["Oracle:CompartmentId"] = "fake-compartment"
            })
            .Build();

        return new OracleAiService(factory, config, db);
    }

    // ── Test 1: Two consecutive AnalyzeAsync calls on the SAME DbContext scope ──
    // This reproduces the session-level bug where the scoped DbContext accumulates
    // tracked entities across two receipt scans.
    [Fact]
    public async Task AnalyzeAsync_TwiceOnSameDbContext_BothReturnSuccess()
    {
        var db = BuildInMemoryDb("db_double_scan_" + Guid.NewGuid());
        var oracleService = BuildFakeOracleService(db, BuildOciResponse(FakeGeminiContent));
        var invoiceService = new InvoiceService(db, oracleService);

        var userId = Guid.NewGuid();
        var fakeImage = "data:image/jpeg;base64," + Convert.ToBase64String(new byte[200]);

        // ── First scan ──
        var result1 = await invoiceService.AnalyzeAsync(fakeImage, null, "IL", userId);

        Assert.True(result1.Success, $"First scan failed: {result1.Error}");
        Assert.NotNull(result1.Fields);
        Assert.Equal("117.00", result1.Fields!.Total);
        Assert.Equal("17.00", result1.Fields.TotalVAT);
        Assert.Equal("ILS", result1.Fields.Currency);

        // After first scan: DbContext tracker holds the InvoiceScanLog as "Unchanged"
        // (without the fix, the second SaveChangesAsync would conflict and throw silently,
        //  causing the outer catch to return Success=false / zeroed fields)

        // ── Second scan — same DbContext instance, same InvoiceService ──
        var result2 = await invoiceService.AnalyzeAsync(fakeImage, null, "IL", userId);

        Assert.True(result2.Success, $"Second scan failed: {result2.Error}");
        Assert.NotNull(result2.Fields);
        Assert.Equal("117.00", result2.Fields!.Total);
        Assert.Equal("17.00", result2.Fields.TotalVAT);
        Assert.Equal("ILS", result2.Fields.Currency);
    }

    // ── Test 2: Both scan logs are actually persisted ──
    [Fact]
    public async Task AnalyzeAsync_TwiceOnSameDbContext_SavesBothLogs()
    {
        var db = BuildInMemoryDb("db_logs_" + Guid.NewGuid());
        var oracleService = BuildFakeOracleService(db, BuildOciResponse(FakeGeminiContent));
        var invoiceService = new InvoiceService(db, oracleService);

        var userId = Guid.NewGuid();
        var fakeImage = "data:image/jpeg;base64," + Convert.ToBase64String(new byte[200]);

        await invoiceService.AnalyzeAsync(fakeImage, null, "IL", userId);
        await invoiceService.AnalyzeAsync(fakeImage, null, "IL", userId);

        var logs = await db.InvoiceScanLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("Success", l.Status));
    }

    // ── Test 3: Change tracker is clean after each SaveScanLog ──
    // Directly verifies the detach logic leaves no stale tracked entities.
    [Fact]
    public async Task AnalyzeAsync_AfterSecondScan_ChangeTrackerHasNoStaleEntries()
    {
        var db = BuildInMemoryDb("db_tracker_" + Guid.NewGuid());
        var oracleService = BuildFakeOracleService(db, BuildOciResponse(FakeGeminiContent));
        var invoiceService = new InvoiceService(db, oracleService);

        var fakeImage = "data:image/jpeg;base64," + Convert.ToBase64String(new byte[200]);

        await invoiceService.AnalyzeAsync(fakeImage, null, "IL", Guid.NewGuid());
        await invoiceService.AnalyzeAsync(fakeImage, null, "IL", Guid.NewGuid());

        // After both scans, the tracker should have at most the last newly-added entity
        // (which becomes Unchanged after SaveChanges), never accumulate unboundedly
        var trackedCount = db.ChangeTracker.Entries()
            .Count(e => e.State != EntityState.Detached);

        // With the fix: detach runs before each Add, so only the last saved log remains (0 or 1)
        Assert.True(trackedCount <= 1,
            $"ChangeTracker has {trackedCount} tracked entities after two scans — expected ≤1");
    }

    // ── Test 4: Fields contain non-zero values (regression guard) ──
    [Fact]
    public async Task AnalyzeAsync_SecondScan_FieldsAreNotZero()
    {
        var db = BuildInMemoryDb("db_nonzero_" + Guid.NewGuid());
        var oracleService = BuildFakeOracleService(db, BuildOciResponse(FakeGeminiContent));
        var invoiceService = new InvoiceService(db, oracleService);

        var fakeImage = "data:image/jpeg;base64," + Convert.ToBase64String(new byte[200]);

        // First scan — discard
        await invoiceService.AnalyzeAsync(fakeImage, null, "IL", Guid.NewGuid());

        // Second scan — this was the one returning zeros
        var result = await invoiceService.AnalyzeAsync(fakeImage, null, "IL", Guid.NewGuid());

        Assert.True(result.Success);
        Assert.NotEqual("0", result.Fields?.Total);
        Assert.NotEqual("0.00", result.Fields?.Total);
        Assert.NotNull(result.Fields?.MerchantName);
        Assert.NotEmpty(result.Fields!.MerchantName!);
    }
}

// ═══════════════════════════════════════
// Test helpers
// ═══════════════════════════════════════

internal class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;

    public FakeHttpHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

internal class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public FakeHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}
