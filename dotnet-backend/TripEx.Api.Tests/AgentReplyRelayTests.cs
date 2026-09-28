using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TripEx.Api.Controllers;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The relay carries text written by a human support agent into a customer's chat window. Both of
/// its failure directions are quiet: pass something through that should not have been passed and
/// a customer reads an internal note or their own words in an agent's voice; drop something that
/// should have gone and the customer sits watching a conversation nobody ever answered.
///
/// The parsing here runs on a body from an endpoint Zoho requires to be unauthenticated, so every
/// test that feeds it a malformed payload is testing a real input, not a hypothetical one.
/// </summary>
public class AgentReplyRelayTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>The ticket ids of a batch, in order — what most of these tests care about.</summary>
    private static List<string> Ids(JsonElement body) =>
        ZohoWebhookController.ExtractThreadEvents(body).Select(e => e.TicketId).ToList();

    private static string Event(
        string ticketId = "31138000011972149",
        string direction = "out",
        string visibility = "public",
        bool isDescriptionThread = false,
        string threadId = "31138000011974105")
        => $@"{{
                ""payload"": {{
                    ""id"": ""{threadId}"",
                    ""ticketId"": ""{ticketId}"",
                    ""direction"": ""{direction}"",
                    ""visibility"": ""{visibility}"",
                    ""isDescriptionThread"": {isDescriptionThread.ToString().ToLowerInvariant()},
                    ""content"": ""<div>hello</div>""
                }},
                ""eventType"": ""Ticket_Thread_Add"",
                ""orgId"": ""54983163""
             }}";

    // ── Which events are even worth a read ───────────────────────────────────────────────────

    [Fact]
    public void An_agents_public_reply_is_picked_up()
    {
        var ids = Ids(Json($"[{Event()}]"));

        Assert.Equal(new[] { "31138000011972149" }, ids);
    }

    [Fact]
    public void The_customers_own_message_coming_back_in_is_ignored()
    {
        // Desk fires Ticket_Thread_Add for inbound threads too. Relaying one would show customers
        // their own words rendered as if support had said them.
        Assert.Empty(Ids(Json($"[{Event(direction: "in")}]")));
    }

    [Fact]
    public void An_internal_note_never_reaches_the_customer()
    {
        // The single most damaging thing this code could do: agents write notes to each other on
        // the same ticket, in the belief that the customer cannot see them.
        Assert.Empty(Ids(Json($"[{Event(visibility: "private")}]")));
    }

    [Fact]
    public void The_tickets_opening_description_is_not_a_reply()
    {
        Assert.Empty(Ids(Json($"[{Event(isDescriptionThread: true)}]")));
    }

    [Fact]
    public void A_batch_is_read_whole()
    {
        var body = Json($"[{Event(ticketId: "111")},{Event(ticketId: "222")}]");

        Assert.Equal(new[] { "111", "222" }, Ids(body));
    }

    [Fact]
    public void Two_replies_on_one_ticket_are_two_events_not_one()
    {
        // The bug this replaced: collapsing a batch per TICKET. Desk batches thread events, so an
        // agent who writes "let me check" and then the actual answer produces two events in one
        // POST — and keeping only one of them meant the customer only ever saw the later reply,
        // with the earlier one unreachable forever. No timing race was needed, just a batch.
        var body = Json($"[{Event(ticketId: "111", threadId: "T1")},{Event(ticketId: "111", threadId: "T2")}]");

        var events = ZohoWebhookController.ExtractThreadEvents(body);

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "T1", "T2" }, events.Select(e => e.ThreadId));
        Assert.All(events, e => Assert.Equal("111", e.TicketId));
    }

    [Fact]
    public void The_same_thread_twice_is_still_one_event()
    {
        // Genuinely the same notification — Zoho's retry policy is undocumented, so a duplicate
        // delivery is expected. Deduping on the PAIR keeps this collapsed while leaving two
        // different threads on one ticket alone.
        var body = Json($"[{Event(ticketId: "111", threadId: "T1")},{Event(ticketId: "111", threadId: "T1")}]");

        Assert.Single(ZohoWebhookController.ExtractThreadEvents(body));
    }

    [Fact]
    public void A_payload_with_no_thread_id_still_produces_an_event()
    {
        // It falls back to "the latest thread on that ticket", which is what the relay always did
        // and is still the right answer when the payload gives nothing better to aim at.
        var body = Json(@"[{""payload"": {""ticketId"": ""111"", ""direction"": ""out""}}]");

        var events = ZohoWebhookController.ExtractThreadEvents(body);

        Assert.Single(events);
        Assert.Null(events[0].ThreadId);
    }

    [Fact]
    public void A_single_object_is_accepted_as_well_as_an_array()
    {
        // Documented as an array. Accepting a bare object too means a payload shape that changes
        // under us degrades to working rather than to silence.
        Assert.Single(Ids(Json(Event())));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"hello\"")]
    [InlineData("[1,2,3]")]
    [InlineData("[{}]")]
    [InlineData(@"[{""payload"": null}]")]
    [InlineData(@"[{""payload"": {}}]")]
    [InlineData(@"[{""payload"": {""ticketId"": null}}]")]
    [InlineData(@"[{""payload"": {""ticketId"": """"}}]")]
    public void Nothing_in_a_malformed_body_throws(string body)
    {
        // This parses input from an endpoint that cannot require authentication, so a crash here
        // is reachable by anyone who finds the URL. Every one of these must be a quiet no-op.
        Assert.Empty(Ids(Json(body)));
    }

    [Fact]
    public void A_numeric_ticket_id_is_not_lost()
    {
        // Zoho sends ids as strings today. A future payload that sends a number should not
        // silently stop the relay.
        var ids = Ids(
            Json(@"[{""payload"": {""ticketId"": 31138000011972149, ""direction"": ""out""}}]"));

        Assert.Equal(new[] { "31138000011972149" }, ids);
    }

    [Fact]
    public void An_event_that_says_nothing_about_direction_is_still_read()
    {
        // Absent is not the same as "in". An unknown shape earns a re-read from Desk, which is
        // where the real decision is made — the parsing here is only a cheap first pass.
        var ids = Ids(Json(@"[{""payload"": {""ticketId"": ""999""}}]"));

        Assert.Equal(new[] { "999" }, ids);
    }

    // ── Turning a helpdesk reply into something a chat bubble can show ───────────────────────

    [Fact]
    public void The_quoted_history_below_a_reply_is_cut_away()
    {
        // Zoho appends the entire prior conversation under every reply. Left in, the customer
        // gets their whole chat history repeated back inside one bubble.
        var raw = "Sure — go to Settings and press Export.\n\n"
                + "---- On Thu, 15 Apr 2021 14:26:49 +0300 support@tripex.io wrote ----\n"
                + "> How do I export?";

        Assert.Equal("Sure — go to Settings and press Export.", ZohoDeskService.TidyReply(raw));
    }

    [Fact]
    public void Zohos_own_satisfaction_survey_is_cut_away()
    {
        var raw = "Fixed it for you.\n\nHow would you rate our customer service?\nGood  Okay  Bad";

        Assert.Equal("Fixed it for you.", ZohoDeskService.TidyReply(raw));
    }

    [Fact]
    public void A_reply_with_nothing_but_boilerplate_comes_back_empty()
    {
        // "" is the caller's signal not to relay at all — better than an empty bubble appearing
        // as if a person had sent one.
        Assert.Equal("", ZohoDeskService.TidyReply("---- On Thu, 15 Apr 2021 ... wrote ----\n> hi"));
        Assert.Equal("", ZohoDeskService.TidyReply("   "));
        Assert.Equal("", ZohoDeskService.TidyReply(null));
    }

    [Fact]
    public void A_paragraph_break_the_agent_typed_survives()
    {
        // Only the runs a stripped signature leaves behind are collapsed. Two paragraphs written
        // by a person must still read as two paragraphs.
        var raw = "First line.\n\nSecond line.\n\n\n\nThird after a signature was removed.";

        Assert.Equal("First line.\n\nSecond line.\n\nThird after a signature was removed.",
            ZohoDeskService.TidyReply(raw));
    }

    [Fact]
    public void Html_is_reduced_to_readable_text_when_plain_text_is_missing()
    {
        var html = "<div>Hello there.</div><p>Second line &amp; an entity.</p>"
                 + "<style>.x{color:red}</style><script>alert(1)</script>";

        var text = ZohoDeskService.TidyReply(ZohoDeskService.StripHtml(html));

        Assert.Contains("Hello there.", text);
        Assert.Contains("Second line & an entity.", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
    }

    // ── The switches ─────────────────────────────────────────────────────────────────────────

    private static ZohoDeskOptions FullyConfigured() => new()
    {
        Enabled = true,
        ClientId = "id", ClientSecret = "secret", RefreshToken = "refresh",
        OrgId = "1", DepartmentId = "2", FallbackContactEmail = "milo@tripex.io",
    };

    [Fact]
    public void The_relay_is_off_until_it_is_switched_on_and_given_a_secret()
    {
        // Three separate things must all be true. Mirroring conversations into tickets and
        // putting an agent's words in front of a customer are different risks, so turning the
        // first on must not quietly turn the second on too.
        var o = FullyConfigured();

        Assert.True(o.IsConfigured);
        Assert.False(o.IsRelayConfigured);            // ticket mirroring on, relay still off

        o.AgentRelayEnabled = true;
        Assert.False(o.IsRelayConfigured);            // no secret

        o.WebhookSecret = "too-short";
        Assert.False(o.IsRelayConfigured);            // a guessable secret is no secret

        o.WebhookSecret = "6f1c2b9e4a7d8350bc1e2f4a9d77";
        Assert.True(o.IsRelayConfigured);
    }

    [Fact]
    public void Turning_the_whole_Zoho_integration_off_takes_the_relay_with_it()
    {
        var o = FullyConfigured();
        o.AgentRelayEnabled = true;
        o.WebhookSecret = "6f1c2b9e4a7d8350bc1e2f4a9d77";
        Assert.True(o.IsRelayConfigured);

        o.Enabled = false;
        Assert.False(o.IsRelayConfigured);
    }
    // ── Languages ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_non_english_portal_still_gets_its_quoted_history_cut()
    {
        // Every worded marker is English, and Zoho translates both the quoted-history header and
        // the survey. On a Hebrew portal the worded list matches nothing, so the cut has to come
        // from the punctuation Zoho fences those blocks with — which survives translation.
        var raw = "אישרתי את הנסיעה, תנסה שוב.\n\n"
                + "---- ביום חמישי, 15 באפריל 2021, support@tripex.io כתב ----\n"
                + "> למה הכפתור אפור?";

        Assert.Equal("אישרתי את הנסיעה, תנסה שוב.", ZohoDeskService.TidyReply(raw));
    }

    [Theory]
    [InlineData("__________________________________")]   // the Outlook-style rule
    [InlineData("----------------------------------")]
    [InlineData("==================================")]
    public void The_separators_that_survive_translation_all_cut(string separator)
    {
        var raw = "Real answer.\n\n" + separator + "\nolder conversation";

        Assert.Equal("Real answer.", ZohoDeskService.TidyReply(raw));
    }

    [Fact]
    public void An_agents_own_text_is_passed_through_in_whatever_language_they_wrote_it()
    {
        // The relay never translates and never inspects the language of the reply. Whatever the
        // agent typed is what the customer reads — Hebrew, English, Russian, Arabic or mixed.
        foreach (var written in new[]
                 {
                     "אישרתי את הנסיעה, תנסה שוב.",
                     "Approved the trip, please try again.",
                     "Одобрил поездку, попробуйте снова.",
                     "تمت الموافقة على الرحلة.",
                     "Approved — הנסיעה אושרה, try again.",
                 })
        {
            Assert.Equal(written, ZohoDeskService.TidyReply(written));
        }
    }

    [Fact]
    public void A_dash_inside_a_sentence_is_not_mistaken_for_a_separator()
    {
        // The structural cut only fires on a LINE that is a rule. A hyphen or dash used mid
        // sentence must never truncate the agent halfway through a thought.
        var raw = "The export is locked - the trip is still pending approval.\nTry again after 14:00.";

        Assert.Equal(raw, ZohoDeskService.TidyReply(raw));
    }
    // ── The role the model is allowed to see ─────────────────────────────────────────────────

    [Fact]
    public void An_agents_reply_reaches_the_model_as_an_assistant_turn()
    {
        // chat_messages now stores a third role. The chat completions API accepts only
        // system/user/assistant, and an agent row stays in history forever — so passing "agent"
        // through would not break one turn, it would break every later turn in that conversation.
        // The feature that lets a human help would be the thing that stops Milo working.
        Assert.Equal("assistant", ChatService.ToModelRole(ZohoAgentReplyService.AgentRole));
    }

    [Theory]
    [InlineData("user", "user")]
    [InlineData("User", "user")]
    [InlineData("  user  ", "user")]
    [InlineData("assistant", "assistant")]
    [InlineData("agent", "assistant")]
    [InlineData("system", "assistant")]
    [InlineData("", "assistant")]
    [InlineData(null, "assistant")]
    [InlineData("something_added_in_2027", "assistant")]
    public void Every_stored_role_maps_to_one_the_api_accepts(string? stored, string expected)
    {
        // Deliberately a whitelist of one: anything that is not the user was said back TO the
        // user. A role invented later fails closed into a valid request rather than a rejected one.
        Assert.Equal(expected, ChatService.ToModelRole(stored));
    }

    [Fact]
    public void The_customers_own_turn_is_never_relabelled()
    {
        // The mirror image of the bug above, and just as bad: map a user turn to assistant and
        // the model reads the customer's question as its own answer.
        Assert.Equal("user", ChatService.ToModelRole("user"));
        Assert.NotEqual("user", ChatService.ToModelRole("assistant"));
    }
    // ── Losing a reply is worse than showing one extra line ──────────────────────────────────

    [Fact]
    public void A_reply_that_opens_by_quoting_the_customer_is_never_erased()
    {
        // Quoting the question before answering it is an ordinary thing for an agent to do.
        // Cutting at the first "> " line turned that reply into an empty string, which the relay
        // read as "nothing to say" — so it was never stored, the thread id was never recorded,
        // and no later poll retried it. The customer waited for an answer that had been written
        // and thrown away. Showing the quoted line back is noise; losing the answer is not.
        var raw = "> למה הכפתור אפור?\n\nכי הנסיעה עדיין ממתינה לאישור. אישרתי אותה עכשיו.";

        var tidied = ZohoDeskService.TidyReply(raw);

        Assert.Contains("אישרתי אותה עכשיו.", tidied);
        Assert.NotEqual("", tidied);
    }

    [Fact]
    public void Quoted_history_that_runs_to_the_end_is_still_cut()
    {
        // The other half of the same rule: a quote block with nothing after it IS quoted history.
        var raw = "אישרתי את הנסיעה.\n\n> למה הכפתור אפור?\n> ניסיתי פעמיים";

        Assert.Equal("אישרתי את הנסיעה.", ZohoDeskService.TidyReply(raw));
    }

    [Fact]
    public void A_reply_that_is_only_a_quote_still_comes_back_empty()
    {
        // Nothing was written, so there is nothing to relay.
        Assert.Equal("", ZohoDeskService.TidyReply("> just the quote\n> and another line"));
    }
    // ── The poll cursor ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A row's created_at exactly as EF hands it back: datetime2 carries no zone, so the Kind is
    /// Unspecified even though the column only ever holds DateTime.UtcNow. Building the test value
    /// with DateTimeKind.Utc, as these tests once did, is what let the missing "Z" go unnoticed.
    /// The sub-millisecond ticks are deliberate — see the first test below.
    /// </summary>
    private static DateTime StoredAsEfReadsIt() =>
        new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Unspecified).AddTicks(1234567);

    /// <summary>
    /// Binds a raw `since` query value through the real ASP.NET model-binding pipeline, the same
    /// one that fills ChatController.Updates's `[FromQuery] DateTime? since`.
    ///
    /// DateTime.Parse is not a stand-in for it. The binder parses with AdjustToUniversal, which is
    /// what keeps a "Z" cursor in UTC; a plain Parse (or RoundtripKind, or a TypeConverter) behaves
    /// differently, and a test that parses by hand is testing a pipeline the request never takes.
    /// Building the pipeline here costs a service provider per call and nothing else.
    /// </summary>
    private static DateTime? BindSince(string raw)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        using var sp = services.BuildServiceProvider();

        var md = sp.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(DateTime?));
        var binder = sp.GetRequiredService<IModelBinderFactory>()
            .CreateBinder(new ModelBinderFactoryContext { Metadata = md });

        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.QueryString = new QueryString("?since=" + Uri.EscapeDataString(raw));

        var ctx = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new QueryStringValueProvider(BindingSource.Query, http.Request.Query, CultureInfo.InvariantCulture),
            md, bindingInfo: null, modelName: "since");

        binder.BindModelAsync(ctx).GetAwaiter().GetResult();

        Assert.True(ctx.Result.IsModelSet, $"the binder did not accept since={raw}");
        return (DateTime?)ctx.Result.Model;
    }

    [Fact]
    public void The_cursor_the_widget_gets_back_is_not_earlier_than_the_row_it_marks()
    {
        // This is the whole contract of /api/chat/updates: the client returns the last createdAt
        // it saw as `since`, and the filter is `CreatedAt > since`. created_at is datetime2(7),
        // so the emitted value must carry all 7 fractional digits.
        //
        // With "yyyy-MM-ddTHH:mm:ss.fffZ" it carried 3, TRUNCATED rather than rounded — so the
        // cursor landed BEFORE the row it was supposed to mark, that row matched again on the
        // next poll, and the cursor never advanced past it. The customer watched the agent's
        // reply reappear every few seconds, forever. It is invisible in any test that uses a
        // whole-millisecond timestamp, which is why this one deliberately does not.
        //
        // Both ends are the real ones: the value is Unspecified, as EF returns it, and it comes
        // back through the ASP.NET binder rather than a hand-picked DateTime.Parse overload.
        var stored = StoredAsEfReadsIt();

        Assert.NotEqual(0, stored.Ticks % TimeSpan.TicksPerMillisecond);   // the case that bit us

        var emitted = ChatController.FormatCursor(stored);
        var roundTripped = BindSince(emitted)!.Value;

        Assert.Equal(stored.Ticks, roundTripped.Ticks);
        Assert.False(stored > roundTripped, "the row must not still match its own cursor");
    }

    [Fact]
    public void The_cursor_says_UTC_even_though_EF_reads_it_back_Unspecified()
    {
        // The bubble-time bug. created_at holds UTC, but EF returns it Unspecified, and "o" on an
        // Unspecified value writes no designator — so the widget's `new Date(createdAt)` read the
        // string as local time and an agent's reply at 13:15 Israel time showed as 10:15.
        var stored = StoredAsEfReadsIt();

        Assert.Equal("2026-09-22T10:00:00.1234567Z", ChatController.FormatCursor(stored));

        // The counter-example, recorded so nobody "simplifies" FormatCursor back to ToString("o").
        Assert.False(stored.ToString("o").EndsWith("Z"), "plain \"o\" on an EF value carries no Z");
    }

    [Fact]
    public void A_cursor_that_is_already_UTC_is_emitted_unchanged()
    {
        // ZohoAgentReplyService now marks CreatedAtUtc as Utc itself, so FormatCursor receives
        // both shapes depending on the caller. Relabelling must not shift anything either way.
        var unspecified = StoredAsEfReadsIt();
        var utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);

        Assert.Equal(ChatController.FormatCursor(unspecified), ChatController.FormatCursor(utc));
    }

    [Fact]
    public void The_cursor_always_carries_seven_fractional_digits()
    {
        // "o" pads to 7 digits even for a whole second. Worth pinning, because a format that
        // trimmed trailing zeros would still round-trip — until a later change assumed a fixed
        // width, or went back to a custom format string that did not pad.
        var wholeSecond = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Unspecified);

        var emitted = ChatController.FormatCursor(wholeSecond);

        Assert.Equal("2026-09-22T10:00:00.0000000Z", emitted);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$", emitted);
    }

    [Theory]
    [InlineData("2026-09-22T10:00:00.1234567")]         // Z-less, in localStorage from before the fix
    [InlineData("2026-09-22T10:00:00.1234567Z")]        // what the endpoint emits now
    [InlineData("2026-09-22T13:00:00.1234567+03:00")]   // an explicit offset
    public void Every_shape_of_cursor_a_widget_may_hold_binds_to_the_same_instant(string raw)
    {
        // A widget stores the last createdAt verbatim in localStorage and sends it back as-is, so
        // after the deploy some widgets hold a Z-less cursor and some a Z one. All of them must
        // land on exactly the row they mark: no earlier (that row repeats), no later (the next
        // reply is skipped).
        var stored = StoredAsEfReadsIt();

        var bound = BindSince(raw)!.Value;

        Assert.Equal(stored.Ticks, bound.Ticks);

        // Local would mean something turned a "Z" into server time. On an Israel machine that
        // also breaks the ticks assert above; on a UTC CI machine the ticks happen to survive, so
        // this is the assert that still catches a hand-parse or TypeConverter regression there.
        Assert.NotEqual(DateTimeKind.Local, bound.Kind);

        Assert.False(stored > bound, "the marked row must not repeat");
        Assert.True(stored.AddTicks(1) > bound, "a reply one tick later must not be skipped");
    }

    [Fact]
    public void Old_and_new_cursors_for_the_same_row_are_interchangeable()
    {
        // The deploy case: a widget kept the Z-less cursor it received before the fix and gets
        // its next reply after it. Both strings must bind to the same instant, so the switch
        // neither repeats the last reply nor skips the next one.
        var stored = StoredAsEfReadsIt();

        var before = BindSince(stored.ToString("o"))!.Value;
        var after = BindSince(ChatController.FormatCursor(stored))!.Value;

        Assert.Equal(before.Ticks, after.Ticks);
    }

    [Fact]
    public void A_cursor_parsed_by_hand_is_moved_into_server_local_time()
    {
        // Kept as the counter-example, like the millisecond one below: this is why `since` stays
        // a model-bound DateTime? rather than a string parsed in the controller. DateTime.Parse
        // without styles converts a "Z" value to server-local time — +2 or +3 hours in Israel —
        // and `CreatedAt > since` then skips every reply in that gap without any error.
        var parsed = DateTime.Parse("2026-09-22T10:00:00.1234567Z", CultureInfo.InvariantCulture);

        Assert.Equal(DateTimeKind.Local, parsed.Kind);
    }

    [Fact]
    public void The_old_millisecond_format_is_shown_to_lose_the_row()
    {
        // Kept as the counter-example, so nobody "simplifies" the format back and reintroduces a
        // bug that no ordinary test would catch.
        var stored = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

        var lossy = DateTime.Parse(stored.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), null,
            System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.True(stored > lossy, "truncating to milliseconds puts the cursor before the row");
    }
}
