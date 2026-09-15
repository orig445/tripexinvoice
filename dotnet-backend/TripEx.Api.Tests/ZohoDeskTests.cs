using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The Zoho Desk mirror writes into a real helpdesk, so the parts that decide WHETHER to call it
/// and WHAT to send are pinned here. The HTTP calls themselves are not exercised — what these
/// cover is the logic that can silently do the wrong thing: a half-filled config that would fire
/// doomed requests, a transcript that overflows Zoho's limit, and a subject that is not one line.
/// </summary>
public class ZohoDeskTests
{
    private static ZohoDeskOptions FullyConfigured() => new()
    {
        Enabled = true,
        ClientId = "id",
        ClientSecret = "secret",
        RefreshToken = "refresh",
        OrgId = "123",
        DepartmentId = "456",
        FallbackContactEmail = "milo@tripex.io",
    };

    // ── The off switch ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_by_default()
    {
        // The whole feature has to be inert until someone deliberately turns it on: a fresh
        // deployment that never touches the Zoho section must not call anything.
        Assert.False(new ZohoDeskOptions().Enabled);
        Assert.False(new ZohoDeskOptions().IsConfigured);
    }

    [Fact]
    public void Enabled_alone_is_not_enough()
    {
        Assert.False(new ZohoDeskOptions { Enabled = true }.IsConfigured);
    }

    [Fact]
    public void A_complete_configuration_is_usable()
    {
        Assert.True(FullyConfigured().IsConfigured);
    }

    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("RefreshToken")]
    [InlineData("OrgId")]
    [InlineData("DepartmentId")]
    [InlineData("FallbackContactEmail")]
    public void Any_single_missing_value_keeps_it_off(string missing)
    {
        // Half-configured is the dangerous state — it would fire requests that can only fail, on
        // every single chat turn. Each of these is individually load-bearing: Zoho rejects a
        // ticket with no department, and cannot create one without a contact email at all.
        var options = FullyConfigured();
        typeof(ZohoDeskOptions).GetProperty(missing)!.SetValue(options, "");

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Blank_is_treated_as_missing()
    {
        var options = FullyConfigured();
        options.RefreshToken = "   ";
        Assert.False(options.IsConfigured);
    }

    // ── Assignment (the trial-run setting) ───────────────────────────────────────────────────

    [Fact]
    public void Assignment_is_optional()
    {
        // Empty is the end state, not a broken state: tickets then follow the department's own
        // rules. Clearing this value is how the trial run ends, so it must never be required.
        var options = FullyConfigured();
        Assert.Equal("", options.AssigneeId);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void Setting_an_assignee_does_not_change_whether_the_feature_is_usable()
    {
        var options = FullyConfigured();
        options.AssigneeId = "1892000000056007";
        Assert.True(options.IsConfigured);
    }

    // ── Truncation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Short_content_is_untouched()
    {
        Assert.Equal("hello", ZohoDeskService.Truncate("hello", 32000));
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
        Assert.Equal("", ZohoDeskService.Truncate(null, 100));
        Assert.Equal("", ZohoDeskService.Truncate("", 100));
    }

    [Fact]
    public void Oversized_content_is_cut_to_the_limit_and_marked()
    {
        // Zoho rejects a comment over 32,000 characters outright, so a long conversation must be
        // trimmed rather than lost — and the cut has to be visible, or someone reads a clipped
        // transcript as the whole conversation. The full record stays in chat_messages.
        var huge = new string('x', ZohoDeskService.MaxCommentLength + 500);
        var result = ZohoDeskService.Truncate(huge, ZohoDeskService.MaxCommentLength);

        Assert.Equal(ZohoDeskService.MaxCommentLength, result.Length);
        Assert.EndsWith("… [truncated]", result);
    }

    [Fact]
    public void Exactly_at_the_limit_is_not_marked()
    {
        var exact = new string('x', 100);
        var result = ZohoDeskService.Truncate(exact, 100);

        Assert.Equal(exact, result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void A_limit_shorter_than_the_marker_still_returns_that_many_characters()
    {
        // Degenerate, but it must not throw or return something longer than asked for.
        var result = ZohoDeskService.Truncate("abcdefghij", 4);
        Assert.Equal(4, result.Length);
    }

    // ── Subject line ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_multiline_question_becomes_one_subject_line()
    {
        // The subject is a single line in every Desk view. A raw first message can carry newlines
        // (the widget lets a user paste), and a subject with a newline renders as truncated junk.
        var subject = ZohoTicketSyncWorker.OneLine("איך אני מוציא דוח\n\nהוצאות לרבעון?");
        Assert.Equal("איך אני מוציא דוח הוצאות לרבעון?", subject);
    }

    [Fact]
    public void Blank_questions_do_not_produce_a_subject()
    {
        Assert.Equal("", ZohoTicketSyncWorker.OneLine(null));
        Assert.Equal("", ZohoTicketSyncWorker.OneLine("   \n\t "));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("where are my reports", ZohoTicketSyncWorker.OneLine("  where are   my reports \n"));
    }

    // ── Batching: no transcript may be lost to the character limit ───────────────────────────

    private static ZohoTicketSyncWorker.TranscriptMessage Msg(string role, string content, int minute) =>
        new(role, content, null, new DateTime(2026, 9, 13, 10, minute, 0, DateTimeKind.Utc));

    [Fact]
    public void An_ordinary_turn_is_a_single_batch()
    {
        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(
            new[] { Msg("user", "where are my reports", 1), Msg("assistant", "In Analysis Reports.", 1) },
            ZohoDeskService.MaxCommentLength);

        Assert.Single(batches);
        Assert.Contains("where are my reports", batches[0].Content);
        Assert.Contains("In Analysis Reports.", batches[0].Content);
    }

    [Fact]
    public void A_backlog_too_big_for_one_write_is_split_and_nothing_is_dropped()
    {
        // The case that used to lose data: truncate to the limit, report success, advance the
        // watermark past the turns that were cut. A long conversation — or the first sync after
        // a Zoho outage — has to arrive in full, across as many comments as it takes.
        var big = new string('x', 12000);
        var messages = Enumerable.Range(1, 10).Select(i => Msg("user", big, i)).ToList();

        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(messages, ZohoDeskService.MaxCommentLength);

        Assert.True(batches.Count > 1);
        Assert.All(batches, b => Assert.True(b.Content.Length <= ZohoDeskService.MaxCommentLength));
        // Every message present exactly once across the batches.
        Assert.Equal(10, batches.Sum(b => b.Content.Split(big).Length - 1));
    }

    [Fact]
    public void Each_batch_reports_the_point_it_syncs_up_to()
    {
        // This is what lets the watermark advance one batch at a time, so a failure half-way
        // through a backlog does not re-send the batches that already landed.
        var big = new string('y', 20000);
        var messages = new[] { Msg("user", big, 1), Msg("assistant", big, 5) };

        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(messages, ZohoDeskService.MaxCommentLength);

        Assert.Equal(2, batches.Count);
        Assert.Equal(new DateTime(2026, 9, 13, 10, 1, 0, DateTimeKind.Utc), batches[0].Through);
        Assert.Equal(new DateTime(2026, 9, 13, 10, 5, 0, DateTimeKind.Utc), batches[1].Through);
    }

    [Fact]
    public void A_single_message_too_large_to_fit_is_truncated_rather_than_wedging_the_sync()
    {
        // Nothing caps what a customer can paste. Such a message can never fit, so retrying it
        // forever would stall the whole conversation's sync — it is cut, marked, and the
        // untouched original stays in chat_messages.
        var enormous = new string('z', ZohoDeskService.MaxCommentLength * 2);
        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(new[] { Msg("user", enormous, 1) },
            ZohoDeskService.MaxCommentLength);

        Assert.Single(batches);
        Assert.True(batches[0].Content.Length <= ZohoDeskService.MaxCommentLength);
        Assert.Contains("truncated", batches[0].Content);
    }

    [Fact]
    public void No_messages_means_no_batches()
    {
        Assert.Empty(ZohoTicketSyncWorker.BuildTranscriptBatches(
            Array.Empty<ZohoTicketSyncWorker.TranscriptMessage>(), ZohoDeskService.MaxCommentLength));
    }

    [Fact]
    public void The_transcript_says_who_said_what()
    {
        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(
            new[] { Msg("user", "שאלה", 1), Msg("assistant", "תשובה", 1) },
            ZohoDeskService.MaxCommentLength);

        Assert.Contains("Customer", batches[0].Content);
        Assert.Contains("Milo", batches[0].Content);
    }

    [Fact]
    public void Line_breaks_survive_as_markup()
    {
        // Zoho's ticket description is an HTML field with no plainText option, so a transcript
        // sent with bare newlines arrives as one run-on line — which is exactly what the first
        // real ticket looked like. Every break has to be markup.
        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(
            new[] { Msg("user", "line one\nline two", 1) },
            ZohoDeskService.MaxCommentLength);

        Assert.Contains("line one<br>line two", batches[0].Content);
        Assert.DoesNotContain("\n", batches[0].Content);
    }

    [Fact]
    public void Customer_text_cannot_inject_markup_into_the_helpdesk()
    {
        // The transcript carries the customer's own words and the model's output. Now that it is
        // rendered as HTML, neither may be able to put live markup in front of a support agent.
        var batches = ZohoTicketSyncWorker.BuildTranscriptBatches(
            new[] { Msg("user", "<img src=x onerror=alert(1)> & <b>bold</b>", 1) },
            ZohoDeskService.MaxCommentLength);

        Assert.DoesNotContain("<img", batches[0].Content);
        Assert.DoesNotContain("<b>bold", batches[0].Content);
        Assert.Contains("&lt;img", batches[0].Content);
        Assert.Contains("&amp;", batches[0].Content);
    }

    [Fact]
    public void Truncation_never_leaves_half_an_html_entity()
    {
        // A plain cut can land inside "&quot;", and the tail then shows as literal "&qu".
        var html = "abcdefgh&quot;ijkl";
        for (var cut = 1; cut <= html.Length; cut++)
        {
            var result = ZohoTicketSyncWorker.TruncateHtml(html, cut);
            var lastAmp = result.LastIndexOf('&');
            Assert.True(lastAmp < 0 || lastAmp < result.LastIndexOf(';'),
                $"dangling entity at cut={cut}: {result}");
        }
    }

    // ── The hand-off queue ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Enqueue_accepts_work_without_blocking()
    {
        var queue = new ZohoTicketSyncQueue();
        Assert.True(queue.Enqueue(new ZohoSyncRequest(Guid.NewGuid(), "Roi", "TripEx", null)));
    }

    [Fact]
    public void A_backlog_never_blocks_or_throws_on_the_chat_path()
    {
        // This is the property that matters: the chat request thread writes here and must return
        // immediately, whatever state the queue is in. With no reader draining it, the bound is
        // reached and old items are dropped — which is safe, because the worker derives what to
        // send from chat_messages, so the next turn re-sends anything a dropped item missed.
        var queue = new ZohoTicketSyncQueue();

        for (var i = 0; i < 5000; i++)
            Assert.True(queue.Enqueue(new ZohoSyncRequest(Guid.NewGuid(), null, null, null)));
    }

    [Fact]
    public void Queued_work_is_readable_by_the_worker()
    {
        var queue = new ZohoTicketSyncQueue();
        var id = Guid.NewGuid();
        queue.Enqueue(new ZohoSyncRequest(id, "Roi", "TripEx", "roi@tripex.io"));

        Assert.True(queue.Reader.TryRead(out var item));
        Assert.Equal(id, item!.SessionId);
        Assert.Equal("roi@tripex.io", item.Email);
    }
}
