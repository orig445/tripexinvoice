using System.Text.Json;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Reproduces the bug: "first receipt scans fine, second returns 0 on everything".
/// Tests the PURE static logic (no DB / no HTTP) that runs for every scan.
/// If these pass, the bug is in the Gemini/OCI call layer, NOT in the parsing layer.
/// </summary>
public class RepeatedScanTests
{
    private const string SampleAiResponse = @"{
        ""document_type"": ""receipt"",
        ""invoice_number"": ""12345"",
        ""invoice_date"": ""2025-01-15"",
        ""currency"": ""ILS"",
        ""expense_type"": ""business_meal"",
        ""merchant"": { ""name"": ""Cafe Aroma"", ""tin"": ""514123456"", ""address"": ""Rothschild 10"", ""city"": ""Tel Aviv"" },
        ""amounts"": { ""vatable_sales_amount"": 100.00, ""non_vatable_sales_amount"": 0, ""service_charge_amount"": 0, ""tax_amount"": 17.00 },
        ""payment"": { ""method"": ""Visa ****1234"", ""amount_paid"": 117.00, ""form_of_payment"": ""credit"", ""card_last4"": ""1234"", ""card_type"": ""visa"" },
        ""item_count"": 3
    }";

    private const string MarkdownWrappedResponse = @"```json
{
  ""document_type"": ""invoice"",
  ""invoice_number"": ""INV-001"",
  ""invoice_date"": ""15/01/2025"",
  ""currency"": ""ILS"",
  ""merchant"": { ""name"": ""Test Vendor"" },
  ""amounts"": { ""vatable_sales_amount"": 200, ""tax_amount"": 34 },
  ""payment"": { ""amount_paid"": 234, ""form_of_payment"": ""cash"" }
}
```";

    private const string TruncatedResponse = @"{
        ""document_type"": ""receipt"",
        ""currency"": ""ILS"",
        ""amounts"": { ""vatable_sales_amount"": 50, ""tax_amount"": 8.5 },
        ""payment"": { ""amount_paid"": 58.5";

    [Fact]
    public void ParseJsonFromAiResponse_RepeatedCalls_ReturnsConsistentResults()
    {
        // ── Reproduces the bug pattern: same input scanned 5 times in a row ──
        for (int i = 0; i < 5; i++)
        {
            var parsed = OracleAiService.ParseJsonFromAiResponse(SampleAiResponse);
            Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
            Assert.True(parsed.TryGetProperty("currency", out var cur));
            Assert.Equal("ILS", cur.GetString());
            Assert.True(parsed.TryGetProperty("amounts", out var amounts));
            Assert.True(amounts.TryGetProperty("tax_amount", out var tax));
            Assert.Equal(17.00m, tax.GetDecimal());
        }
    }

    [Fact]
    public void ParseJsonFromAiResponse_HandlesMarkdownWrapper()
    {
        var parsed = OracleAiService.ParseJsonFromAiResponse(MarkdownWrappedResponse);
        Assert.Equal("Test Vendor",
            parsed.GetProperty("merchant").GetProperty("name").GetString());
    }

    [Fact]
    public void ParseJsonFromAiResponse_RepairsTruncatedJson()
    {
        // Truncated AI response — should not throw, should auto-close braces
        var parsed = OracleAiService.ParseJsonFromAiResponse(TruncatedResponse);
        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
        Assert.Equal("ILS", parsed.GetProperty("currency").GetString());
    }

    [Fact]
    public void ParseJsonFromAiResponse_EmptyString_Throws()
    {
        Assert.ThrowsAny<Exception>(() => OracleAiService.ParseJsonFromAiResponse(""));
    }

    [Fact]
    public void ParseJsonFromAiResponse_NoJsonAtAll_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            OracleAiService.ParseJsonFromAiResponse("Sorry I cannot read this image"));
    }

    [Fact]
    public async Task ParseJsonFromAiResponse_ConcurrentCalls_AreThreadSafe()
    {
        // Simulates the bulk-train scenario (parallel uploads from UI)
        var results = new List<JsonElement>();
        var lockObj = new object();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var parsed = OracleAiService.ParseJsonFromAiResponse(SampleAiResponse);
            lock (lockObj) { results.Add(parsed); }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(10, results.Count);
        foreach (var r in results)
        {
            Assert.Equal("ILS", r.GetProperty("currency").GetString());
            Assert.Equal(17.00m,
                r.GetProperty("amounts").GetProperty("tax_amount").GetDecimal());
        }
    }

    [Fact]
    public void DecodeUnicodeEscapes_HandlesHebrew()
    {
        var input = @"\u05e7\u05e4\u05d4"; // קפה
        var decoded = OracleAiService.DecodeUnicodeEscapes(input);
        Assert.Equal("קפה", decoded);
    }

    [Fact]
    public void DecodeUnicodeEscapes_RepeatedCalls_AreIdempotent()
    {
        var input = @"\u05e7\u05e4\u05d4";
        var first = OracleAiService.DecodeUnicodeEscapes(input);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, OracleAiService.DecodeUnicodeEscapes(input));
        }
    }

    [Theory]
    [InlineData("Visa ****1234", "1234")]
    [InlineData("MASTERCARD-XXXX-5678", "5678")]
    public void ParseJsonFromAiResponse_Repeated_ProducesSameOutput(string method, string expectedLast4)
    {
        // Confirm parsing is deterministic across multiple calls
        var json = $@"{{ ""payment"": {{ ""method"": ""{method}"", ""card_last4"": ""{expectedLast4}"" }} }}";
        var first = OracleAiService.ParseJsonFromAiResponse(json).GetRawText();
        var second = OracleAiService.ParseJsonFromAiResponse(json).GetRawText();
        var third = OracleAiService.ParseJsonFromAiResponse(json).GetRawText();
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }
}
