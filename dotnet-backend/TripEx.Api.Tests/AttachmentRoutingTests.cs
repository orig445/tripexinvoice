using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Tests for AttachmentRouting — the decision that sends a file to the invoice scanner or to the
/// conversation, and for the request normalization that feeds it.
///
/// The behaviour being pinned down here: an attachment used to mean exactly one thing. Type="image"
/// went straight to OCR, whatever the file was and whatever the user said, so a screenshot of a TAS
/// screen sent with "why is this empty?" came back as a list of merchant/VAT/total fields read off a
/// page that has none. Now only a purchase document is scanned — while the camera button, the TAS
/// widget and Milo:ImageAutoRoute=false all still get the old behaviour exactly.
/// </summary>
public class AttachmentRoutingTests
{
    // ── The decision ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("scan")]
    [InlineData("SCAN")]
    [InlineData("  scan  ")]
    public void An_explicit_scan_intent_scans_whatever_the_classifier_thinks(string intent)
    {
        // The camera button is "scan this receipt" and nothing else. A model's opinion must not
        // be able to override what the user pressed.
        var route = AttachmentRouting.Decide(intent, autoRouteEnabled: true, hasQuestionText: true, classifierAnswer: "ask");

        Assert.Equal(AttachmentRoute.Scan, route);
    }

    [Fact]
    public void An_explicit_ask_intent_never_scans()
    {
        var route = AttachmentRouting.Decide("ask", autoRouteEnabled: true, hasQuestionText: false, classifierAnswer: "scan");

        Assert.Equal(AttachmentRoute.Ask, route);
    }

    [Fact]
    public void With_auto_routing_off_everything_is_scanned_as_before()
    {
        // The rollback path: a config edit and a restart restore the pre-change behaviour with no
        // deploy, so it has to hold even for a turn that clearly asked a question.
        var route = AttachmentRouting.Decide("auto", autoRouteEnabled: false, hasQuestionText: true, classifierAnswer: null);

        Assert.Equal(AttachmentRoute.Scan, route);
        Assert.False(AttachmentRouting.ShouldClassify("auto", autoRouteEnabled: false));
    }

    [Fact]
    public void A_receipt_the_classifier_recognised_is_still_scanned()
    {
        var route = AttachmentRouting.Decide(null, autoRouteEnabled: true, hasQuestionText: false, classifierAnswer: "scan");

        Assert.Equal(AttachmentRoute.Scan, route);
    }

    [Fact]
    public void Anything_the_classifier_calls_context_goes_to_the_conversation()
    {
        // The whole point: a screenshot sent with no words at all must not be OCR'd just because
        // nothing was typed.
        var route = AttachmentRouting.Decide(null, autoRouteEnabled: true, hasQuestionText: false, classifierAnswer: "ask");

        Assert.Equal(AttachmentRoute.Ask, route);
    }

    [Theory]
    [InlineData(null)]        // the classifier was skipped, timed out, or threw
    [InlineData("")]
    [InlineData("maybe?")]    // a reply nothing could be made of
    public void Without_a_verdict_a_question_is_answered_and_a_bare_file_is_scanned(string? verdict)
    {
        // A failure must not cost the user their question — and must not change what a bare
        // attachment has always done, which is scan.
        Assert.Equal(
            AttachmentRoute.Ask,
            AttachmentRouting.Decide(null, autoRouteEnabled: true, hasQuestionText: true, classifierAnswer: verdict));

        Assert.Equal(
            AttachmentRoute.Scan,
            AttachmentRouting.Decide(null, autoRouteEnabled: true, hasQuestionText: false, classifierAnswer: verdict));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("nonsense")]
    public void Only_an_auto_intent_is_worth_a_model_call(string? intent)
    {
        // Anything unrecognised is "auto" — a future client's typo must not silently pick a side.
        Assert.Equal(AttachmentIntents.Auto, AttachmentIntents.Normalize(intent));
        Assert.True(AttachmentRouting.ShouldClassify(intent, autoRouteEnabled: true));
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("ask")]
    public void An_intent_that_already_decides_costs_nothing(string intent)
    {
        // Nobody should be billed for a verdict that is discarded.
        Assert.False(AttachmentRouting.ShouldClassify(intent, autoRouteEnabled: true));
    }

    // ── The request shape ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_old_image_shape_becomes_a_scan_attachment()
    {
        // Every caller we don't control — the TAS widget above all — still sends Type="image"
        // with the payload in Text, and must keep scanning exactly as it does today.
        var payload = new string('A', 40_000);
        var request = new ChatRequest { Type = "image", Text = payload };

        request.NormalizeWidgetShape();

        Assert.Equal(new[] { payload }, request.Images);
        Assert.Equal(AttachmentIntents.Scan, request.AttachmentIntent);
        Assert.Equal("", request.Text);
        Assert.True(request.HasAttachments);
    }

    [Fact]
    public void The_old_image_shape_is_moved_out_before_the_text_cap_can_eat_it()
    {
        // This is the bug that made the old shape unusable in the first place: Text is capped at
        // MaxTextLength (8,000) and silently truncated above it, and the smallest real receipt
        // runs to tens of thousands of base64 characters — so what reached the scanner was the
        // first 8,000 of them plus a truncation marker.
        var payload = new string('A', 120_000);
        var request = new ChatRequest { Type = "image", Text = payload };

        request.NormalizeWidgetShape();

        Assert.Equal(payload, request.Images![0]);
        Assert.DoesNotContain("truncated", request.Images![0]);
    }

    [Fact]
    public void A_caller_that_sends_both_shapes_keeps_its_own_images_and_intent()
    {
        var request = new ChatRequest
        {
            Type = "image",
            Text = "why was this rejected?",
            Images = new List<string> { "AAAA" },
            AttachmentIntent = AttachmentIntents.Ask,
        };

        request.NormalizeWidgetShape();

        Assert.Equal(new[] { "AAAA" }, request.Images);
        Assert.Equal(AttachmentIntents.Ask, request.AttachmentIntent);
        Assert.Equal("why was this rejected?", request.Text);
    }

    [Fact]
    public void Blank_attachments_are_dropped_and_the_count_is_capped()
    {
        var request = new ChatRequest
        {
            Text = "look at these",
            Images = new List<string> { "a", "", "   ", "b", "c", "d", "e", "f", "g" },
        };

        request.NormalizeWidgetShape();

        Assert.Equal(ChatRequest.MaxAttachments, request.Images!.Count);
        Assert.DoesNotContain("", request.Images);
        Assert.Equal("a", request.Images[0]);
    }

    [Fact]
    public void A_plain_text_turn_is_left_alone()
    {
        var request = new ChatRequest { Text = "how do I export an expense report?" };

        request.NormalizeWidgetShape();

        Assert.False(request.HasAttachments);
        Assert.Null(request.Images);
        Assert.Equal("how do I export an expense report?", request.Text);
    }

    // ── What the transcript records ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_attachment_leaves_a_note_in_the_conversations_own_text()
    {
        // The images last one request; the text is what is saved to chat_messages, replayed as
        // history and mirrored onto the Zoho ticket. Without the note, "why is this empty?" is
        // stored with nothing it could refer to.
        Assert.Equal("why is this empty?\n[1 file attached]", ChatService.WithAttachmentNote("why is this empty?", 1));
        Assert.Equal("[1 file attached]", ChatService.WithAttachmentNote("", 1));
        Assert.Equal("[1 file attached]", ChatService.WithAttachmentNote(null, 1));
        Assert.Equal("[3 files attached]", ChatService.WithAttachmentNote("   ", 3));
        Assert.Equal("see these\n[2 files attached]", ChatService.WithAttachmentNote("see these  ", 2));
    }
}
