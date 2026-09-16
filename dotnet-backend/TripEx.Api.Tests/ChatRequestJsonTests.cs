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

    // ── Incoming message length ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_ordinary_message_is_passed_through_untouched()
    {
        // The cap must be invisible to every real question. The longest genuine message seen in
        // production is under 300 characters; this is 500 and must arrive byte-for-byte.
        var text = new string('א', 500);
        var request = new ChatRequest { Text = text };

        request.NormalizeWidgetShape();

        Assert.Equal(text, request.Text);
    }

    [Fact]
    public void An_oversized_message_is_trimmed_rather_than_rejected()
    {
        // Before this there was no cap at all: the whole thing went into the prompt (billed per
        // token), into chat_messages, and into the Zoho ticket transcript. Trimmed, not refused —
        // the first 8,000 characters still carry the question, so the customer gets an answer
        // instead of an error about their own message.
        var request = new ChatRequest { Text = new string('x', 50_000) };

        request.NormalizeWidgetShape();

        Assert.StartsWith(new string('x', ChatRequest.MaxTextLength), request.Text);
        Assert.EndsWith("[… message truncated]", request.Text);
        Assert.True(request.Text.Length < 50_000);
    }

    [Fact]
    public void Trimming_does_not_fire_exactly_at_the_limit()
    {
        // An off-by-one here would put the marker on a message that was never truncated, which
        // the model would then be told to treat as incomplete.
        var request = new ChatRequest { Text = new string('x', ChatRequest.MaxTextLength) };

        request.NormalizeWidgetShape();

        Assert.Equal(ChatRequest.MaxTextLength, request.Text.Length);
        Assert.DoesNotContain("truncated", request.Text);
    }
}
