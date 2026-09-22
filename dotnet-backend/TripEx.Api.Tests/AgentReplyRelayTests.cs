using System.Text.Json;
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

    private static string Event(
        string ticketId = "31138000011972149",
        string direction = "out",
        string visibility = "public",
        bool isDescriptionThread = false)
        => $@"{{
                ""payload"": {{
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
        var ids = ZohoWebhookController.ExtractTicketIds(Json($"[{Event()}]"));

        Assert.Equal(new[] { "31138000011972149" }, ids);
    }

    [Fact]
    public void The_customers_own_message_coming_back_in_is_ignored()
    {
        // Desk fires Ticket_Thread_Add for inbound threads too. Relaying one would show customers
        // their own words rendered as if support had said them.
        Assert.Empty(ZohoWebhookController.ExtractTicketIds(Json($"[{Event(direction: "in")}]")));
    }

    [Fact]
    public void An_internal_note_never_reaches_the_customer()
    {
        // The single most damaging thing this code could do: agents write notes to each other on
        // the same ticket, in the belief that the customer cannot see them.
        Assert.Empty(ZohoWebhookController.ExtractTicketIds(Json($"[{Event(visibility: "private")}]")));
    }

    [Fact]
    public void The_tickets_opening_description_is_not_a_reply()
    {
        Assert.Empty(ZohoWebhookController.ExtractTicketIds(Json($"[{Event(isDescriptionThread: true)}]")));
    }

    [Fact]
    public void A_batch_is_read_whole_and_deduped()
    {
        // Desk batches events into one array, and two threads on one ticket only need one read.
        var body = Json($"[{Event(ticketId: "111")},{Event(ticketId: "222")},{Event(ticketId: "111")}]");

        Assert.Equal(new[] { "111", "222" }, ZohoWebhookController.ExtractTicketIds(body));
    }

    [Fact]
    public void A_single_object_is_accepted_as_well_as_an_array()
    {
        // Documented as an array. Accepting a bare object too means a payload shape that changes
        // under us degrades to working rather than to silence.
        Assert.Single(ZohoWebhookController.ExtractTicketIds(Json(Event())));
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
        Assert.Empty(ZohoWebhookController.ExtractTicketIds(Json(body)));
    }

    [Fact]
    public void A_numeric_ticket_id_is_not_lost()
    {
        // Zoho sends ids as strings today. A future payload that sends a number should not
        // silently stop the relay.
        var ids = ZohoWebhookController.ExtractTicketIds(
            Json(@"[{""payload"": {""ticketId"": 31138000011972149, ""direction"": ""out""}}]"));

        Assert.Equal(new[] { "31138000011972149" }, ids);
    }

    [Fact]
    public void An_event_that_says_nothing_about_direction_is_still_read()
    {
        // Absent is not the same as "in". An unknown shape earns a re-read from Desk, which is
        // where the real decision is made — the parsing here is only a cheap first pass.
        var ids = ZohoWebhookController.ExtractTicketIds(Json(@"[{""payload"": {""ticketId"": ""999""}}]"));

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
}
