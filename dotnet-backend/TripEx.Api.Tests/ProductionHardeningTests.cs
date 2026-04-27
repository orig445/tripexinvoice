using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Tests for production hardening: oversized images, custom OCI exception, etc.
/// These run without a real OCI/DB connection.
/// </summary>
public class ProductionHardeningTests
{
    [Fact]
    public void OciApiException_CarriesStatusAndBody()
    {
        var ex = new OciApiException("rate limited", 429, "{\"error\":\"too many requests\"}");
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("too many requests", ex.ResponseBody);
        Assert.Equal("rate limited", ex.Message);
    }

    [Fact]
    public void ParseJsonFromAiResponse_StripsControlCharsAndRecovers()
    {
        // Control char \x01 between valid chars — stripped, parse succeeds
        var raw = "{\"foo\":\"bar" + (char)0x01 + "baz\"}";
        var parsed = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal("barbaz", parsed.GetProperty("foo").GetString());
    }

    [Fact]
    public void DecodeUnicodeEscapes_HandlesEmptyAndPlainText()
    {
        Assert.Equal("", OracleAiService.DecodeUnicodeEscapes(""));
        Assert.Equal("hello world", OracleAiService.DecodeUnicodeEscapes("hello world"));
    }

    [Fact]
    public void ParseJsonFromAiResponse_HandlesNestedObjectsAndArrays()
    {
        var raw = @"{ ""items"": [{""name"":""a"",""qty"":1},{""name"":""b"",""qty"":2}], ""total"": 30 }";
        var parsed = OracleAiService.ParseJsonFromAiResponse(raw);
        Assert.Equal(2, parsed.GetProperty("items").GetArrayLength());
        Assert.Equal(30, parsed.GetProperty("total").GetInt32());
    }
}
