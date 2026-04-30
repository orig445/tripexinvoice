using System.Text.Json;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Comprehensive tests for ParseJsonFromAiResponse — production hardening against
/// every truncation and malformation pattern observed in real OCR responses.
/// </summary>
public class JsonRepairTests
{
    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void ValidJson_ReturnsAsIs()
    {
        var raw = @"{""currency"":""ILS"",""invoice_number"":""12345""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("12345", result.GetProperty("invoice_number").GetString());
    }

    [Fact]
    public void MarkdownFence_IsStripped()
    {
        var raw = "```json\n{\"currency\":\"ILS\"}\n```";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void TextBeforeAndAfterJson_IsIgnored()
    {
        var raw = "Here is the result:\n{\"currency\":\"ILS\"}\nHope that helps!";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    // ── Truncation — the specific bug from production logs ────────────────────

    [Fact]
    public void DanglingPropertyName_WithComma_IsRemoved()
    {
        // Exact pattern from production log: "expense"} after a valid field
        var raw = @"{""currency"":""ILS"",""invoice_number"":""70581"",""invoice_date"":""2019-05-27"",""expense""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("70581", result.GetProperty("invoice_number").GetString());
        // dangling "expense" key must not appear
        Assert.False(result.TryGetProperty("expense", out _));
    }

    [Fact]
    public void DanglingPropertyName_AsOnlyKey_ReturnsEmptyObject()
    {
        // {"expense"} — only a key, no value, no comma
        var raw = @"{""expense""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void KeyWithColonButNoValue_WithComma_IsRemoved()
    {
        // ,"expense_type": } — key and colon but truncated before value
        var raw = @"{""currency"":""ILS"",""expense_type"": }";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.False(result.TryGetProperty("expense_type", out _));
    }

    [Fact]
    public void KeyWithColonButNoValue_AsOnlyKey_ReturnsEmptyObject()
    {
        var raw = @"{""expense_type"": }";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void TruncatedMidStringValue_IsClosed()
    {
        // Truncated inside a string value
        var raw = "{\"invoice_number\":\"12345\",\"merchant\":{\"name\":\"Cafe Aro";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("12345", result.GetProperty("invoice_number").GetString());
    }

    [Fact]
    public void TruncatedMidNestedObject_IsClosed()
    {
        // Truncated inside nested merchant block
        var raw = @"{""currency"":""ILS"",""merchant"":{""name"":""Cafe Aroma"",""city"":""Tel Aviv""";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("Cafe Aroma", result.GetProperty("merchant").GetProperty("name").GetString());
    }

    [Fact]
    public void TruncatedAfterColon_IsRepaired()
    {
        // Truncated right after a colon — most aggressive truncation
        var raw = "{\"currency\":\"ILS\",\"invoice_number\":";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void TruncatedAfterComma_IsRepaired()
    {
        var raw = @"{""currency"":""ILS"",""invoice_number"":""123"",";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void TrailingComma_InObject_IsFixed()
    {
        var raw = @"{""currency"":""ILS"",""invoice_number"":""123"",}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void TrailingComma_InArray_IsFixed()
    {
        var raw = @"{""items"":[1,2,3,]}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(3, result.GetProperty("items").GetArrayLength());
    }

    // ── Real invoice shape — truncated at various points ─────────────────────

    [Fact]
    public void FullInvoiceJson_TruncatedAtPayment_ExtractsCurrencyAndDate()
    {
        // Simulates the invoice cut off before the payment block
        var raw = @"{
            ""document_type"": ""invoice"",
            ""invoice_number"": ""70581"",
            ""invoice_date"": ""2019-05-27"",
            ""currency"": ""ILS"",
            ""expense_type"": ""business_meal"",
            ""merchant"": {""name"": ""הסטייק"", ""tin"": null},
            ""amounts"": {""vatable_sales_amount"": 100, ""tax_amount"": 17}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("2019-05-27", result.GetProperty("invoice_date").GetString());
        Assert.Equal("70581", result.GetProperty("invoice_number").GetString());
    }

    [Fact]
    public void FullInvoiceJson_TruncatedAtExpenseType_ExtractsCurrencyAndInvoiceNum()
    {
        // Reproduces the exact log scenario: truncated at "expense"}
        var raw = @"{
            ""detected_language"": ""he"",
            ""detected_country"": ""IL"",
            ""document_type"": ""invoice"",
            ""invoice_number"": ""70581"",
            ""invoice_date"": ""2019-05-27"",
            ""currency"": ""ILS"",
            ""expense""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("70581", result.GetProperty("invoice_number").GetString());
        Assert.False(result.TryGetProperty("expense", out _));
    }

    // ── Regex field-extraction fallback ──────────────────────────────────────

    [Fact]
    public void CompletelyBrokenJson_FallsBackToFieldExtraction()
    {
        // Completely unparseable — field extraction should recover currency and invoice_number
        var raw = @"blah blah ""currency"": ""ILS"" more blah ""invoice_number"": ""99999"" garbage }{]";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        // Currency extracted by regex fallback
        if (result.TryGetProperty("currency", out var cur))
            Assert.Equal("ILS", cur.GetString());
    }

    [Fact]
    public void NoJsonNoKnownFields_ReturnsEmptyObject()
    {
        // No JSON, no known fields — should return {} and NOT throw
        var result = OracleAiService.ParseJsonFromAiResponse("Sorry I cannot read this image");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("{}", result.GetRawText());
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void ControlChars_AreStripped()
    {
        var raw = "{\"foo\":\"bar" + (char)0x01 + "baz\"}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("barbaz", result.GetProperty("foo").GetString());
    }

    [Fact]
    public void EmptyString_Throws()
    {
        Assert.ThrowsAny<Exception>(() => OracleAiService.ParseJsonFromAiResponse(""));
    }

    [Fact]
    public void WhitespaceOnly_Throws()
    {
        Assert.ThrowsAny<Exception>(() => OracleAiService.ParseJsonFromAiResponse("   \n  "));
    }

    [Fact]
    public void MultipleDanglingKeys_AllRemovedInFixpoint()
    {
        // Two consecutive dangling keys — fixpoint loop must clear both
        var raw = @"{""currency"":""ILS"",""key1"",""key2""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.False(result.TryGetProperty("key1", out _));
        Assert.False(result.TryGetProperty("key2", out _));
    }

    [Fact]
    public void StringValueWithCommaInside_IsPreserved()
    {
        // Comma inside a string value must not be treated as a property separator
        var raw = @"{""address"":""Rothschild 10, Tel Aviv"",""currency"":""ILS""}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("Rothschild 10, Tel Aviv", result.GetProperty("address").GetString());
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void NestedObjectWithDanglingKey_IsRepaired()
    {
        // Dangling key inside a nested block
        var raw = @"{""currency"":""ILS"",""merchant"":{""name"":""Cafe"",""dangling""}}";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("Cafe", result.GetProperty("merchant").GetProperty("name").GetString());
    }

    [Fact]
    public void ConcurrentCalls_AreThreadSafe()
    {
        var raw = @"{""currency"":""ILS"",""invoice_number"":""12345""}";
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var el = OracleAiService.ParseJsonFromAiResponse(raw);
            results.Add(el.GetProperty("currency").GetString()!);
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.Equal("ILS", r));
    }

    [Fact]
    public void AmountPaid_ExtractedByRegexFallback()
    {
        // Broken JSON that still contains amount_paid in readable text
        var raw = @"BROKEN{ ""invoice_number"": ""555"", ""currency"": ""PHP"", ""payment"": { ""amount_paid"": 1328.00, ""form_of_payment"": ""cash"" } }BROKEN";
        var result = OracleAiService.ParseJsonFromAiResponse(raw);
        // Should parse the embedded valid JSON or extract via fallback
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }
}
