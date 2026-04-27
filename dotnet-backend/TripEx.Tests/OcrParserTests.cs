using System.Text.Json;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Tests;

/// <summary>
/// Unit tests for OracleAiService.ParseJsonFromAiResponse.
/// This is the entry point for every AI response — if it breaks, everything breaks.
/// </summary>
public class OcrParserTests
{
    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidJson_ReturnsCorrectFields()
    {
        var json = """{"currency": "ILS", "invoice_number": "INV-001"}""";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
        Assert.Equal("INV-001", result.GetProperty("invoice_number").GetString());
    }

    [Fact]
    public void Parse_NestedObjects_ParsedCorrectly()
    {
        var json = """
            {
              "merchant": {"name": "Test Shop", "city": "Tel Aviv"},
              "amounts": {"tax_amount": 17.5}
            }
            """;
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("Test Shop", result.GetProperty("merchant").GetProperty("name").GetString());
        Assert.Equal(17.5m, result.GetProperty("amounts").GetProperty("tax_amount").GetDecimal());
    }

    [Fact]
    public void Parse_NullFieldValues_ReturnedAsNull()
    {
        var json = """{"invoice_number": null, "currency": "PHP"}""";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("invoice_number").ValueKind);
    }

    // ── Markdown wrapper stripping ────────────────────────────────────────────

    [Fact]
    public void Parse_MarkdownJsonBlock_Stripped()
    {
        var json = "```json\n{\"currency\": \"PHP\"}\n```";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("PHP", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void Parse_MarkdownBlockNoKeyword_Stripped()
    {
        var json = "```\n{\"currency\": \"USD\"}\n```";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("USD", result.GetProperty("currency").GetString());
    }

    // ── Text before/after JSON ────────────────────────────────────────────────

    [Fact]
    public void Parse_TextBeforeJson_JsonExtracted()
    {
        var json = "Here is the extracted data:\n{\"currency\": \"THB\"}";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("THB", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void Parse_TextAfterJson_ExtraIgnored()
    {
        var json = "{\"currency\": \"ILS\"}\nHope this helps!";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void Parse_TextBeforeAndAfterJson_OnlyJsonTaken()
    {
        var json = "Processing...\n```json\n{\"currency\": \"PHP\"}\n```\nDone.";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("PHP", result.GetProperty("currency").GetString());
    }

    // ── Repair ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TruncatedJson_AutoRepaired()
    {
        // Missing closing braces — should be repaired
        var truncated = """{"amounts": {"tax_amount": 17""";
        var result = OracleAiService.ParseJsonFromAiResponse(truncated);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(
            17m,
            result.GetProperty("amounts").GetProperty("tax_amount").GetDecimal());
    }

    [Fact]
    public void Parse_TrailingCommaInObject_AutoFixed()
    {
        var json = """{"currency": "ILS", "tax": 17,}""";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal("ILS", result.GetProperty("currency").GetString());
    }

    [Fact]
    public void Parse_ControlCharactersInJson_Stripped()
    {
        // ASCII control chars injected inside key name
        var json = "{\"cur\x01rency\": \"ILS\"}";
        var result = OracleAiService.ParseJsonFromAiResponse(json);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => OracleAiService.ParseJsonFromAiResponse(""));
    }

    [Fact]
    public void Parse_WhitespaceOnly_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => OracleAiService.ParseJsonFromAiResponse("   \n  "));
    }

    [Fact]
    public void Parse_PlainTextNoJson_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(
            () => OracleAiService.ParseJsonFromAiResponse("This is not JSON at all"));
    }

    // ── DecodeUnicodeEscapes (used in ChatService for Hebrew text) ─────────────

    [Fact]
    public void DecodeUnicode_HebrewShalom_DecodedCorrectly()
    {
        var encoded = "\\u05E9\\u05DC\\u05D5\\u05DD";
        var result = OracleAiService.DecodeUnicodeEscapes(encoded);
        Assert.Equal("שלום", result);
    }

    [Fact]
    public void DecodeUnicode_MixedAsciiAndHebrew_DecodedCorrectly()
    {
        var encoded = "Hello \\u05E9\\u05DC\\u05D5\\u05DD World";
        var result = OracleAiService.DecodeUnicodeEscapes(encoded);
        Assert.Equal("Hello שלום World", result);
    }

    [Fact]
    public void DecodeUnicode_NoEscapes_ReturnedUnchanged()
    {
        var plain = "No unicode here";
        Assert.Equal(plain, OracleAiService.DecodeUnicodeEscapes(plain));
    }
}
