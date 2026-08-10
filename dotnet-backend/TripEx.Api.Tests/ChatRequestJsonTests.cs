using System.Text.Json;
using TripEx.Api.Models;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// TAS's widget sends "trid" as a bare JSON number, not a string — verifies
/// ChatRequest deserializes that shape (and the more common string/absent shapes)
/// without throwing, since a JsonException here surfaces as an automatic 400
/// before the request ever reaches the controller.
/// </summary>
public class ChatRequestJsonTests
{
    [Fact]
    public void NumericTrid_DeserializesToString()
    {
        // Exact payload observed from the TAS widget (module/tts are extra fields
        // with no matching property — must be ignored, not throw).
        var json = @"{""module"":""web"",""scope"":""webpage"",""trid"":0,""text"":""hey"",""tts"":false}";
        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(request);
        Assert.Equal("0", request!.Trid);
        Assert.Equal("hey", request.Text);
        Assert.Equal("webpage", request.Scope);
    }

    [Fact]
    public void NumericTrid_NonZero_DeserializesToString()
    {
        var json = @"{""trid"":42,""text"":""hi""}";
        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("42", request!.Trid);
    }

    [Fact]
    public void StringTrid_StillWorks()
    {
        var json = @"{""trid"":""TAS12345"",""text"":""hi""}";
        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("TAS12345", request!.Trid);
    }

    [Fact]
    public void NullTrid_DeserializesToNull()
    {
        var json = @"{""trid"":null,""text"":""hi""}";
        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(request!.Trid);
    }

    [Fact]
    public void MissingTrid_DeserializesToNull()
    {
        var json = @"{""text"":""hi""}";
        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(request!.Trid);
    }
}
