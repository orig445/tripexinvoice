using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// After Milo hands a conversation to a person, it has to stop answering in it. Both ways of
/// getting this wrong are visible to the customer: Milo talking over the agent (two voices, one of
/// which has not read the ticket), or Milo going silent where no agent can ever answer — which is a
/// customer typing into a window nobody reads.
///
/// So most of these tests are about WHERE the silence must not apply.
/// </summary>
public class HandoverTests
{
    // ── Where a human can actually answer ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("web")]
    [InlineData("tas")]
    [InlineData("widget")]
    public void The_widget_with_the_relay_on_is_a_window_an_agent_answers_in(string? source)
    {
        Assert.True(ChatService.AgentAnswersHere(relayConfigured: true, source, isTasWidgetClient: true));
    }

    [Theory]
    [InlineData("web")]
    [InlineData(null)]
    public void With_the_relay_off_no_agent_answers_here(string? source)
    {
        // The reply has no way back into the window, so the escalation must name the email
        // address and Milo must keep answering.
        Assert.False(ChatService.AgentAnswersHere(relayConfigured: false, source, isTasWidgetClient: true));
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("Internal")]
    [InlineData(" internal ")]
    public void Internal_staff_chat_never_has_an_agent_answering(string source)
    {
        // The bug this closed: "internal" is never mirrored into Desk, so there is no ticket for an
        // agent to open — yet its escalation said "stay here, the reply will arrive in this chat".
        Assert.False(ChatService.AgentAnswersHere(relayConfigured: true, source, isTasWidgetClient: true));
    }

    [Theory]
    [InlineData("salesiq")]
    [InlineData("SalesIQ")]
    public void A_SalesIQ_relay_never_has_our_agent_answering(string source)
    {
        // SalesIQ raises its own ticket and never polls /api/chat/updates, so our relay cannot
        // reach it. Going silent there would strand the customer.
        Assert.False(ChatService.AgentAnswersHere(relayConfigured: true, source, isTasWidgetClient: true));
    }

    [Theory]
    [InlineData("web")]
    [InlineData(null)]
    public void A_client_that_is_not_the_widget_never_has_an_agent_answering(string? source)
    {
        // Only the TAS widget polls /api/chat/updates. This repo's own /chat page also arrives as
        // "web", so Source cannot tell them apart — and a client that never polls would be told to
        // wait for a reply it can never show, then silenced for the rest of the conversation.
        Assert.False(ChatService.AgentAnswersHere(relayConfigured: true, source, isTasWidgetClient: false));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("web", true)]
    [InlineData("widget", true)]
    [InlineData("internal", false)]
    [InlineData("INTERNAL", false)]
    [InlineData("salesiq", false)]
    [InlineData(" SalesIQ ", false)]
    public void Only_customer_sources_are_mirrored_into_Desk(string? source, bool mirrored)
    {
        // The one definition the enqueue, the recovery sweep and the worker all consult. Before it
        // existed the sweep had no source filter at all, so an escalated staff chat got a ticket.
        Assert.Equal(mirrored, ChatService.IsMirroredSource(source));
    }

    // ── When Milo goes quiet ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_escalated_conversation_where_an_agent_answers_is_handed_over()
    {
        Assert.True(ChatService.IsHandedOver(continuedSession: true, humanInvolved: true, agentAnswersHere: true));
    }

    [Fact]
    public void A_conversation_that_never_escalated_is_still_Milos()
    {
        Assert.False(ChatService.IsHandedOver(continuedSession: true, humanInvolved: false, agentAnswersHere: true));
    }

    [Fact]
    public void A_new_chat_is_always_Milos()
    {
        // How the customer gets Milo back: "New chat" starts a conversation with no history, and
        // nothing about the previous one's hand-off can follow it there.
        Assert.False(ChatService.IsHandedOver(continuedSession: false, humanInvolved: true, agentAnswersHere: true));
    }

    [Fact]
    public void An_escalation_nobody_can_answer_in_the_window_leaves_Milo_answering()
    {
        // Relay off, internal or SalesIQ: the customer was given an email address, not told to
        // wait. Going silent would mean every later question in that chat goes unanswered.
        Assert.False(ChatService.IsHandedOver(continuedSession: true, humanInvolved: true, agentAnswersHere: false));
    }

    // ── What the customer sees ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_receipt_speaks_the_customers_language()
    {
        Assert.Contains("נציג", ChatService.HandoverReceipt(hebrew: true));
        Assert.Contains("agent", ChatService.HandoverReceipt(hebrew: false));
    }

    [Fact]
    public void The_receipt_is_not_empty()
    {
        // The widget renders an empty reply as a stock "Here's where you can find more:" line.
        // A widget that has not been updated yet would show that under every message the customer
        // sends to the agent — so the text must never be blank.
        Assert.False(string.IsNullOrWhiteSpace(ChatService.HandoverReceipt(hebrew: true)));
        Assert.False(string.IsNullOrWhiteSpace(ChatService.HandoverReceipt(hebrew: false)));
    }

    [Theory]
    [InlineData("עדיין לא עובד לי", null, true)]
    [InlineData("still not working", "he-IL", false)]
    [InlineData("ok", "he-IL", true)]          // a short Latin fragment inside a Hebrew chat
    [InlineData("PDF?", "he", true)]
    [InlineData("TID 55123", "he-IL", true)]
    [InlineData("12345", "he-IL", true)]       // digits only: the locale decides
    [InlineData("12345", "en-US", false)]
    [InlineData("ok", "en-US", false)]
    [InlineData("ok", null, false)]
    public void The_receipt_language_follows_the_message_and_falls_back_to_the_locale(
        string text, string? locale, bool hebrew)
    {
        Assert.Equal(hebrew, ChatService.IsHebrewReceipt(text, locale));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Where_an_agent_answers_the_support_offer_connects_instead_of_naming_an_email(bool hebrew)
    {
        // Roi, 2026-09-24: sending someone to an inbox when a person could pick the chat up right
        // here makes them leave the one window the agent's reply would arrive in.
        var offer = ChatService.SupportOffer(hebrew, agentAnswersHere: true, "support@tripex.io");

        Assert.DoesNotContain("@", offer);
        Assert.Contains(hebrew ? "לחבר אותך לנציג" : "connect you to a support agent", offer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Where_no_agent_can_answer_the_support_offer_still_names_the_email(bool hebrew)
    {
        // Relay off, internal chat, SalesIQ: offering to "connect" them would be a promise nothing
        // keeps, so the address is still the honest way to a person.
        Assert.Contains("support@tripex.io",
            ChatService.SupportOffer(hebrew, agentAnswersHere: false, "support@tripex.io"));
    }

    [Fact]
    public void A_message_that_did_not_arrive_asks_to_be_sent_again()
    {
        Assert.Contains("שוב", ChatService.HandoverNotDelivered(hebrew: true));
        Assert.Contains("again", ChatService.HandoverNotDelivered(hebrew: false));
    }

    [Fact]
    public void HandedOver_defaults_to_false_so_every_other_reply_renders_as_before()
    {
        Assert.False(new ChatResponse().HandedOver);
    }

    [Fact]
    public void HandedOver_goes_over_the_wire_in_the_casing_the_widget_reads()
    {
        // The widget checks `response.handedOver`. ASP.NET's default web serializer is camelCase;
        // this pins the name so a rename on this side cannot silently turn the check off.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ChatResponse { HandedOver = true },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"handedOver\":true", json);
    }

    // ── Getting the message to a person ────────────────────────────────────────────────────

    private static ZohoTicketSyncWorker.TranscriptMessage Msg(string role, string? intent)
        => new(role, "text", intent, DateTime.UtcNow);

    [Fact]
    public void A_customer_line_sent_during_the_handover_is_marked_for_the_agent()
    {
        Assert.True(ZohoTicketSyncWorker.IsWrittenToTheAgent(Msg("user", ChatService.HandoverIntent)));
    }

    [Theory]
    [InlineData("user", null)]              // an ordinary question Milo answered
    [InlineData("assistant", "handover")]   // never Milo's own rows
    [InlineData("agent", "handover")]       // never the agent's own reply coming back
    [InlineData("user", "escalate")]
    public void Nothing_else_reopens_a_ticket(string role, string? intent)
    {
        // The reopen is only for the customer writing to a person who is not answering yet. A
        // relay-off conversation keeps talking to Milo after its escalation, and those lines must
        // not drag a ticket the agent closed back into their queue.
        Assert.False(ZohoTicketSyncWorker.IsWrittenToTheAgent(Msg(role, intent)));
    }

    [Theory]
    [InlineData(@"{""id"":""1"",""status"":""Closed"",""statusType"":""Closed""}", "Closed")]
    [InlineData(@"{""status"":""סגור"",""statusType"":""Closed""}", "Closed")]   // renamed status, same type
    [InlineData(@"{""status"":""Open"",""statusType"":""Open""}", "Open")]
    [InlineData(@"{""status"":""Waiting on supplier"",""statusType"":""On Hold""}", "On Hold")]
    [InlineData(@"{""status"":""Closed""}", "Closed")]                            // no type: built-in name only
    [InlineData(@"{""status"":""Resolved""}", null)]                               // unknown must not reopen
    [InlineData(@"[]", null)]
    [InlineData(@"not json", null)]
    [InlineData(@"", null)]
    public void The_ticket_status_is_read_by_type_not_by_name(string body, string? expected)
    {
        // Admins rename statuses; the three types are fixed. A reopen keyed on the name would stop
        // working the day someone calls "Closed" something else — or fire on a status that only
        // looks closed.
        Assert.Equal(expected, ZohoDeskService.ReadStatusType(body));
    }
}
