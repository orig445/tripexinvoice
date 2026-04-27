using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TripEx.Api.Data;
using TripEx.Api.Services;

namespace TripEx.Tests;

internal static class TestHelpers
{
    internal static string FakeImageBase64 =>
        "data:image/jpeg;base64," + Convert.ToBase64String(new byte[200]);

    internal static string RawBase64 =>
        Convert.ToBase64String(new byte[200]);

    internal static TripExDbContext BuildInMemoryDb() =>
        new(new DbContextOptionsBuilder<TripExDbContext>()
            .UseInMemoryDatabase("test_" + Guid.NewGuid())
            .Options);

    // Wraps invoice JSON inside the OCI response envelope
    internal static string WrapInOci(string invoiceJson)
    {
        var escaped = invoiceJson
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");
        return $$"""{"choices":[{"message":{"content":"{{escaped}}"},"finish_reason":"stop"}]}""";
    }

    internal static OracleAiService BuildOracle(
        TripExDbContext db,
        string rawHttpBody,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oracle:ApiKey"]        = "fake-key",
                ["Oracle:Endpoint"]      = "https://fake.oci/chat",
                ["Oracle:Model"]         = "fake-model",
                ["Oracle:CompartmentId"] = "fake-comp"
            })
            .Build();
        var factory = new FakeHttpClientFactory(
            new HttpClient(new FakeHttpHandler(rawHttpBody, status)));
        return new OracleAiService(factory, config, db);
    }

    // Convenience: builds a full stack for a happy-path scan
    internal static (InvoiceService Svc, TripExDbContext Db) SuccessStack(string invoiceJson)
    {
        var db = BuildInMemoryDb();
        return (new InvoiceService(db, BuildOracle(db, WrapInOci(invoiceJson))), db);
    }

    // Convenience: builds a stack where the HTTP call returns an error
    internal static (InvoiceService Svc, TripExDbContext Db) ErrorStack(
        HttpStatusCode status, string errorBody = "error")
    {
        var db = BuildInMemoryDb();
        return (new InvoiceService(db, BuildOracle(db, errorBody, status)), db);
    }

    // ── Standard receipt templates ──────────────────────────────────────────

    internal const string StandardReceipt = """
        {
          "document_type": "receipt",
          "invoice_number": "RCP-001",
          "invoice_date": "2024-01-15",
          "currency": "ILS",
          "expense_type": "business_meal",
          "merchant": {"name": "Test Cafe", "tin": "123456", "address": "Test St 1", "city": "Tel Aviv"},
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
}

internal class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    internal FakeHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        => (_body, _status) = (body, status);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        });
}

internal class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    internal FakeHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}
