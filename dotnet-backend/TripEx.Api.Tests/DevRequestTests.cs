using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TripEx.Api.Data;
using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Development requests from the widget's opening menu (Roi, 2026-10-04). The customer picks
/// "Development request", describes the change in one message, and that message arrives with
/// menuChoice="development_request". Milo answers with a fixed receipt — no model — and the
/// conversation becomes a Zoho ticket for the product team: Open, "Feature", Medium.
///
/// Three things are pinned here: WHEN a turn is one (first message only, everything configured,
/// never over a hand-off), WHAT it leaves behind (the intents every later reader keys on), and
/// the TICKET it becomes, including the one retry that must never lose it.
///
/// The ProcessAsync tests run the real ChatService against a database and HTTP that never leave
/// the process: EF's interceptors answer every query from a script and every HTTP call goes to a
/// stub. Nothing here touches a network, a database or the model.
/// </summary>
public class DevRequestTests
{
    private static (string Role, string Content, string? Intent) Assistant(string? intent, string text = "…")
        => ("assistant", text, intent);

    private static (string Role, string Content, string? Intent) User(string text, string? intent = null)
        => ("user", text, intent);

    private const string Request = "Let approvers sign off on a whole trip at once instead of line by line";

    // ── The request field ────────────────────────────────────────────────────────────────────

    [Fact]
    public void MenuChoice_binds_from_the_widgets_flat_payload_and_survives_normalisation()
    {
        // The widget posts its identity fields flat at the top level, and menuChoice beside them.
        var json = @"{""customerName"":""Racheli"",""sessionId"":""413494f3-0000-0000-0000-000000000001"",""scope"":""webpage"",""trid"":0," +
                   @"""text"":""" + Request + @""",""menuChoice"":""development_request"",""tts"":false}";

        var request = JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        request.NormalizeWidgetShape();

        Assert.Equal(ChatService.DevRequestMenuChoice, request.MenuChoice);
        Assert.True(request.IsTasWidgetClient);
        Assert.Equal(Request, request.Text);
    }

    [Fact]
    public void MenuChoice_binds_with_the_web_defaults_aspnet_uses()
    {
        var request = JsonSerializer.Deserialize<ChatRequest>(
            @"{""text"":""hi"",""menuChoice"":""development_request""}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("development_request", request.MenuChoice);
    }

    [Fact]
    public void A_message_without_a_menu_choice_carries_none()
    {
        var request = JsonSerializer.Deserialize<ChatRequest>(@"{""text"":""hi""}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        request.NormalizeWidgetShape();

        Assert.Null(request.MenuChoice);
    }

    // ── When a turn is a development request ─────────────────────────────────────────────────

    [Theory]
    [InlineData("development_request")]
    [InlineData(" Development_Request ")]
    public void The_first_message_with_the_choice_is_submitted(string choice)
    {
        var history = new List<(string, string, string?)> { User(Request) };

        Assert.Equal(ChatService.DevRequestTurn.Submit,
            ChatService.ClassifyDevRequestTurn(available: true, choice, history, Request));
    }

    [Fact]
    public void A_choice_after_the_first_message_is_ignored()
    {
        // The widget sends the choice only on the message after the pick, and shows the menu only in
        // a fresh conversation — so a choice anywhere later is not one to act on.
        var history = new List<(string, string, string?)>
        {
            User("How do I export a report?"), Assistant("general"), User(Request),
        };

        Assert.Equal(ChatService.DevRequestTurn.None,
            ChatService.ClassifyDevRequestTurn(true, ChatService.DevRequestMenuChoice, history, Request));
    }

    [Fact]
    public void A_choice_whose_history_could_not_be_read_is_ignored()
    {
        // An outage: nobody can say it was the first message, or that the message was stored at all.
        Assert.Equal(ChatService.DevRequestTurn.None,
            ChatService.ClassifyDevRequestTurn(true, ChatService.DevRequestMenuChoice, new List<(string, string, string?)>(), Request));
    }

    [Fact]
    public void A_lone_stored_message_that_is_not_this_one_is_not_a_first_message()
    {
        // The current message failed to save, and the one row read back is an earlier, unanswered one.
        var history = new List<(string, string, string?)> { User("an earlier question") };

        Assert.Equal(ChatService.DevRequestTurn.None,
            ChatService.ClassifyDevRequestTurn(true, ChatService.DevRequestMenuChoice, history, Request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("problem_in_the_system")]
    [InlineData("guidance")]
    [InlineData("Development request")] // the button's label is not the value
    public void Anything_but_the_choice_itself_is_an_ordinary_first_message(string? choice)
    {
        var history = new List<(string, string, string?)> { User(Request) };

        Assert.Equal(ChatService.DevRequestTurn.None, ChatService.ClassifyDevRequestTurn(true, choice, history, Request));
    }

    [Fact]
    public void Nothing_applies_while_the_feature_is_unavailable()
    {
        // available folds in the switch, Zoho being configured and the source being mirrored —
        // without a ticket the receipt would be a promise nobody keeps. Follow-ups too.
        var first = new List<(string, string, string?)> { User(Request) };
        var later = new List<(string, string, string?)>
        {
            User(Request, ChatService.DevRequestIntent), Assistant(ChatService.DevRequestSubmittedIntent), User("one more thing"),
        };

        Assert.Equal(ChatService.DevRequestTurn.None,
            ChatService.ClassifyDevRequestTurn(false, ChatService.DevRequestMenuChoice, first, Request));
        Assert.Equal(ChatService.DevRequestTurn.None,
            ChatService.ClassifyDevRequestTurn(false, null, later, "one more thing"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("development_request")]
    public void Everything_after_the_receipt_is_a_follow_up_whatever_the_request_says(string? choice)
    {
        var history = new List<(string, string, string?)>
        {
            User(Request, ChatService.DevRequestIntent), Assistant(ChatService.DevRequestSubmittedIntent),
            User("Also for expense reports"),
        };

        Assert.Equal(ChatService.DevRequestTurn.FollowUp,
            ChatService.ClassifyDevRequestTurn(true, choice, history, "Also for expense reports"));
    }

    [Fact]
    public void A_follow_up_is_recognised_after_the_receipt_has_left_the_history_window()
    {
        // History is the newest 50 messages. Every follow-up reply carries an intent of its own, so a
        // long conversation is still recognised once the receipt has scrolled out.
        var history = new List<(string, string, string?)>
        {
            User("more"), Assistant(ChatService.DevRequestFollowUpIntent), User("and more"),
        };

        Assert.True(ChatService.IsDevRequestConversation(history));
        Assert.Equal(ChatService.DevRequestTurn.FollowUp, ChatService.ClassifyDevRequestTurn(true, null, history, "and more"));
    }

    [Fact]
    public void An_ordinary_conversation_is_not_one()
    {
        var history = new List<(string, string, string?)>
        {
            User("?"), Assistant("clarify"), User("Other"), Assistant(ChatService.OtherPickIntent), User("x"),
        };

        Assert.False(ChatService.IsDevRequestConversation(history));
    }

    // ── The intents it leaves behind disturb nothing else ───────────────────────────────────

    [Fact]
    public void The_intents_are_their_own_and_fit_the_column()
    {
        var intents = new[] { ChatService.DevRequestIntent, ChatService.DevRequestSubmittedIntent, ChatService.DevRequestFollowUpIntent };

        Assert.Equal(3, intents.Distinct().Count());
        Assert.DoesNotContain(ChatService.HandoverIntent, intents);
        Assert.DoesNotContain(ChatService.OtherPickIntent, intents);
        Assert.All(intents, i => Assert.True(i.Length <= 50)); // chat_messages.intent is NVARCHAR(50)
    }

    [Fact]
    public void They_are_not_clarifying_turns()
    {
        // Not counted towards the every-fourth support offer, and nothing after them is read as an
        // answer to a clarifying question.
        var history = new List<(string, string, string?)>
        {
            User(Request, ChatService.DevRequestIntent), Assistant(ChatService.DevRequestSubmittedIntent),
            User("x"), Assistant(ChatService.DevRequestFollowUpIntent),
        };

        Assert.Equal(0, ChatService.CountTrailingConsecutiveClarifications(history));
        Assert.False(ChatService.IsOtherOptionPick(history, "Other"));
        Assert.False(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsEn[0]));
    }

    [Fact]
    public void The_request_line_is_not_a_message_to_an_agent()
    {
        // Only HandoverIntent makes the worker reopen a ticket; a request must not.
        var line = new ZohoTicketSyncWorker.TranscriptMessage("user", Request, ChatService.DevRequestIntent, DateTime.UtcNow);

        Assert.False(ZohoTicketSyncWorker.IsWrittenToTheAgent(line));
    }

    [Fact]
    public void The_replies_are_the_approved_wording()
    {
        Assert.Equal(
            "Thanks! I've passed your development request to our product team for review. This isn't a commitment " +
            "to build it, and the team may contact you if they need more details. If something is broken or blocking " +
            "your work, or for anything else, please start a New chat.",
            ChatService.DevRequestSubmittedReply);
        Assert.Equal("Added to your development request. For anything else, please start a New chat.",
            ChatService.DevRequestFollowUpReply);
    }

    [Fact]
    public void The_reply_flag_goes_over_the_wire_in_the_casing_the_widget_reads()
    {
        // The widget checks `response.devRequest` to keep its New chat button up — both replies send
        // the customer there. ASP.NET's web defaults are camelCase; this pins the name. Off by default,
        // so every other reply leaves the button to its usual rules.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Contains("\"devRequest\":true", JsonSerializer.Serialize(new ChatResponse { DevRequest = true }, web));
        Assert.Contains("\"devRequest\":false", JsonSerializer.Serialize(new ChatResponse(), web));
    }

    // ── ProcessAsync, end to end, offline ────────────────────────────────────────────────────

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_submitted_request_gets_the_fixed_receipt_without_the_model()
    {
        var h = new Harness { History = { User(Request) } };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(Request, ChatService.DevRequestMenuChoice), UserId, "203.0.113.7");

        Assert.Equal(ChatService.DevRequestSubmittedReply, response.Text);
        Assert.Equal(SessionId.ToString(), response.SessionId);
        Assert.True(response.DevRequest);
        Assert.Empty(response.QuickReplies);
        Assert.Null(response.Paramerter);
        Assert.Equal("", response.RedirectPage);
        Assert.False(response.HandedOver);
        Assert.False(response.Escalated);
        Assert.False(response.AgentWillAnswer);
        Assert.Null(response.TicketNumber);

        // No model, no geolocation (the address above would have been looked up), no usage row — and
        // not even the config read, which comes before the geolocation, the knowledge search and the
        // prompt.
        Assert.Empty(h.Http.Calls);
        Assert.DoesNotContain(h.Db.Commands, c => c.Contains("chat_usage") || c.Contains("chatbot_config"));

        // What the save carries: the customer's line tagged as the request, the receipt as submitted.
        var rows = h.Messages();
        Assert.Contains(rows, m => m.Role == "user" && m.Content == Request && m.Intent == ChatService.DevRequestIntent);
        Assert.Contains(rows, m => m.Role == "assistant" && m.Content == ChatService.DevRequestSubmittedReply
                                   && m.Intent == ChatService.DevRequestSubmittedIntent);

        // And the conversation goes to Zoho like any answered turn.
        Assert.True(h.Queue.Reader.TryRead(out var queued));
        Assert.Equal(SessionId, queued!.SessionId);
        Assert.Equal("Racheli", queued.CustomerName);
    }

    [Fact]
    public async Task Anything_added_afterwards_gets_the_follow_up_line_and_reaches_the_ticket()
    {
        const string more = "It should work for expense reports too";
        var h = new Harness
        {
            History = { User(Request, ChatService.DevRequestIntent), Assistant(ChatService.DevRequestSubmittedIntent, ChatService.DevRequestSubmittedReply), User(more) },
        };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(more, menuChoice: null), UserId, "203.0.113.7");

        Assert.Equal(ChatService.DevRequestFollowUpReply, response.Text);
        Assert.True(response.DevRequest);
        Assert.False(response.HandedOver);
        Assert.Empty(h.Http.Calls);

        var rows = h.Messages();
        Assert.Contains(rows, m => m.Role == "user" && m.Content == more && m.Intent == null);
        Assert.Contains(rows, m => m.Role == "assistant" && m.Intent == ChatService.DevRequestFollowUpIntent);
        Assert.True(h.Queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_receipt_that_could_not_be_saved_promises_nothing_and_queues_nothing()
    {
        // The customer's line saves; the save carrying its tag and the receipt does not.
        var h = new Harness { History = { User(Request) }, FailMessageWritesAfter = 1 };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(Request, ChatService.DevRequestMenuChoice), UserId, "203.0.113.7");

        Assert.Equal(ChatService.DevRequestNotRecordedReply, response.Text);
        Assert.Equal(SessionId.ToString(), response.SessionId);
        Assert.True(response.DevRequest);   // it sends the customer to New chat
        Assert.Empty(h.Http.Calls);
        Assert.False(h.Queue.Reader.TryRead(out _));
        // The receipt was tried, and is not among the rows that saved.
        Assert.Contains(h.Attempted(), m => m.Intent == ChatService.DevRequestSubmittedIntent);
        Assert.DoesNotContain(h.Messages(), m => m.Intent == ChatService.DevRequestSubmittedIntent);
    }

    [Fact]
    public async Task A_follow_up_whose_reply_could_not_be_saved_still_reaches_the_ticket()
    {
        const string more = "It should work for expense reports too";
        var h = new Harness
        {
            History = { User(Request, ChatService.DevRequestIntent), Assistant(ChatService.DevRequestSubmittedIntent, ChatService.DevRequestSubmittedReply), User(more) },
            FailMessageWritesAfter = 1,
        };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(more, menuChoice: null), UserId, "203.0.113.7");

        Assert.Equal(ChatService.DevRequestFollowUpReply, response.Text);
        Assert.True(h.Queue.Reader.TryRead(out _));
    }

    public enum Ignored { NotTheFirstMessage, SwitchedOff, ZohoNotConfigured, InternalSource }

    [Theory]
    [InlineData(Ignored.NotTheFirstMessage)]
    [InlineData(Ignored.SwitchedOff)]
    [InlineData(Ignored.ZohoNotConfigured)]
    [InlineData(Ignored.InternalSource)]
    public async Task Where_it_does_not_apply_the_choice_is_ignored_and_the_model_answers(Ignored why)
    {
        var h = new Harness
        {
            DevRequestsSwitch = why == Ignored.SwitchedOff ? "false" : "true",
            ZohoEnabled = why != Ignored.ZohoNotConfigured,
        };
        if (why == Ignored.NotTheFirstMessage)
            h.History.AddRange(new[] { User("How do I add a trip?"), Assistant("general") });
        h.History.Add(User(Request));

        var request = h.WidgetRequest(Request, ChatService.DevRequestMenuChoice);
        if (why == Ignored.InternalSource) request.Source = "internal";

        var response = await h.Service.ProcessAsync(request, UserId, "203.0.113.7");

        Assert.Equal(StubHttp.ModelAnswer, response.Text);
        Assert.False(response.DevRequest);
        Assert.Contains(h.Http.Calls, c => c.StartsWith("POST https://oci.example.test/"));
        Assert.DoesNotContain(h.Attempted(), m => m.Intent is ChatService.DevRequestIntent
            or ChatService.DevRequestSubmittedIntent or ChatService.DevRequestFollowUpIntent);
    }

    [Theory]
    [InlineData(null, false)]      // a server that never mentions it: off
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("on", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public async Task Off_unless_milo_dev_requests_is_true(string? setting, bool on)
    {
        // The opening menu was dropped (Roi, 2026-10-05): the code stays, inert unless switched on.
        var h = new Harness { DevRequestsSwitch = setting, History = { User(Request) } };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(Request, ChatService.DevRequestMenuChoice), UserId, "203.0.113.7");

        Assert.Equal(on, response.DevRequest);
        Assert.Equal(on ? ChatService.DevRequestSubmittedReply : StubHttp.ModelAnswer, response.Text);
    }

    [Fact]
    public async Task A_handed_over_conversation_goes_to_the_agent_not_to_the_product_team()
    {
        // The hand-off check runs first, unchanged: a person on the ticket is who the customer is
        // writing to, menu choice or not.
        var h = new Harness { RelayOn = true, HumanInvolved = true, History = { User(Request) } };

        var response = await h.Service.ProcessAsync(h.WidgetRequest(Request, ChatService.DevRequestMenuChoice), UserId, "203.0.113.7");

        Assert.True(response.HandedOver);
        Assert.False(response.DevRequest);
        Assert.NotEqual(ChatService.DevRequestSubmittedReply, response.Text);
        Assert.Empty(h.Http.Calls);
        Assert.Contains(h.Messages(), m => m.Role == "user" && m.Intent == ChatService.HandoverIntent);
        Assert.DoesNotContain(h.Attempted(), m => m.Role == "assistant");
    }

    // ── The ticket ───────────────────────────────────────────────────────────────────────────

    private static ZohoTicketSyncWorker.TranscriptMessage Line(string role, string content, string? intent, int minute)
        => new(role, content, intent, new DateTime(2026, 10, 4, 9, minute, 0, DateTimeKind.Utc));

    [Fact]
    public void A_request_conversation_opens_a_request_ticket_named_after_the_request()
    {
        // Even when Zoho was down at the time and the customer added more before the create landed.
        var pending = new List<ZohoTicketSyncWorker.TranscriptMessage>
        {
            Line("user", Request, ChatService.DevRequestIntent, 0),
            Line("assistant", ChatService.DevRequestSubmittedReply, ChatService.DevRequestSubmittedIntent, 0),
            Line("user", "And for expense reports", null, 1),
            Line("assistant", ChatService.DevRequestFollowUpReply, ChatService.DevRequestFollowUpIntent, 1),
        };

        var draft = ZohoTicketSyncWorker.NewTicketDraft(
            new ZohoSyncRequest(SessionId, "Racheli", "Avt", "racheli@avt.example.test"), pending, "<b>transcript</b>",
            escalated: false, new ZohoDeskOptions { FallbackContactEmail = "milo@example.test" });

        Assert.True(draft.DevRequest);
        Assert.Equal(Request, draft.Subject);
        Assert.DoesNotContain("Development request", draft.Subject); // the label is added once, by the create
        Assert.False(draft.Escalated);
        Assert.Equal("Racheli", draft.ContactLastName);
        Assert.Equal("racheli@avt.example.test", draft.ContactEmail);
    }

    [Fact]
    public void An_ordinary_conversation_still_opens_an_ordinary_ticket()
    {
        var pending = new List<ZohoTicketSyncWorker.TranscriptMessage>
        {
            Line("user", "How do I\nexport a report?", null, 0),
            Line("assistant", "Like this.", "general", 0),
        };

        var draft = ZohoTicketSyncWorker.NewTicketDraft(new ZohoSyncRequest(SessionId, null, null, null), pending, "t",
            escalated: true, new ZohoDeskOptions { FallbackContactEmail = "milo@example.test" });

        Assert.False(draft.DevRequest);
        Assert.True(draft.Escalated);
        Assert.Equal("How do I export a report?", draft.Subject);
        Assert.Equal("Milo", draft.ContactLastName);
        Assert.Equal("milo@example.test", draft.ContactEmail);
    }

    [Fact]
    public async Task A_request_ticket_is_open_classified_as_a_feature_at_medium_with_the_label()
    {
        var stub = new StubDesk();
        var desk = Desk(stub);

        var created = await desk.CreateTicketAsync(Draft(devRequest: true));

        Assert.NotNull(created);
        var body = Assert.Single(stub.TicketCreates);
        Assert.Equal("[Milo] [Development request] " + Request, body.GetProperty("subject").GetString());
        Assert.Equal("Open", body.GetProperty("status").GetString());
        Assert.Equal("Feature", body.GetProperty("classification").GetString());
        Assert.Equal("Medium", body.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task An_ordinary_ticket_is_unchanged()
    {
        var stub = new StubDesk();
        var desk = Desk(stub);

        await desk.CreateTicketAsync(Draft(devRequest: false));

        var body = Assert.Single(stub.TicketCreates);
        Assert.Equal("[Milo] " + Request, body.GetProperty("subject").GetString());
        Assert.Equal("Closed", body.GetProperty("status").GetString());
        Assert.Equal("AI Answer", body.GetProperty("classification").GetString());
        Assert.Equal("Low", body.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_refused_request_ticket_is_retried_once_with_the_ordinary_classification_and_no_priority()
    {
        // A portal whose classification list has no "Feature" must not cost the customer their request.
        var stub = new StubDesk { CreateStatuses = { HttpStatusCode.UnprocessableEntity } };
        var desk = Desk(stub);

        var created = await desk.CreateTicketAsync(Draft(devRequest: true));

        Assert.NotNull(created);
        Assert.Equal(2, stub.TicketCreates.Count);
        var retry = stub.TicketCreates[1];
        Assert.Equal("AI Answer", retry.GetProperty("classification").GetString());
        Assert.False(retry.TryGetProperty("priority", out _));
        // The rest of what makes it a request ticket stays.
        Assert.Equal("Open", retry.GetProperty("status").GetString());
        Assert.StartsWith("[Milo] [Development request] ", retry.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task There_is_never_more_than_one_retry()
    {
        var stub = new StubDesk { CreateStatuses = { HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity } };
        var desk = Desk(stub);

        Assert.Null(await desk.CreateTicketAsync(Draft(devRequest: true)));
        Assert.Equal(2, stub.TicketCreates.Count);
    }

    [Fact]
    public async Task A_request_ticket_with_nothing_to_drop_is_not_retried()
    {
        // Its classification is the ordinary one and it has no priority, so a retry would send the
        // same thing again — the same rule an ordinary ticket with no priority has always followed.
        var stub = new StubDesk { CreateStatuses = { HttpStatusCode.UnprocessableEntity } };
        var desk = Desk(stub, new() { ["Zoho:DevRequestClassification"] = "AI Answer", ["Zoho:DevRequestPriority"] = "" });

        Assert.Null(await desk.CreateTicketAsync(Draft(devRequest: true)));
        Assert.Single(stub.TicketCreates);
    }

    [Fact]
    public async Task The_four_settings_can_be_changed_or_blanked_in_config()
    {
        var stub = new StubDesk();
        var desk = Desk(stub, new()
        {
            ["Zoho:DevRequestStatus"] = " ",                // blank → EscalatedStatus
            ["Zoho:DevRequestClassification"] = " Idea ",   // trimmed: Desk matches picklists exactly
            ["Zoho:DevRequestPriority"] = "",               // blank → left off
            ["Zoho:DevRequestSubjectLabel"] = "",           // blank → no label
        });

        await desk.CreateTicketAsync(Draft(devRequest: true));

        var body = Assert.Single(stub.TicketCreates);
        Assert.Equal("[Milo] " + Request, body.GetProperty("subject").GetString());
        Assert.Equal("Open", body.GetProperty("status").GetString());
        Assert.Equal("Idea", body.GetProperty("classification").GetString());
        Assert.False(body.TryGetProperty("priority", out _));
    }

    [Fact]
    public void The_defaults_are_the_approved_ones_and_need_no_config()
    {
        var options = new ZohoDeskOptions();

        Assert.Equal("Open", options.DevRequestStatus);
        Assert.Equal("Feature", options.DevRequestClassification);
        Assert.Equal("Medium", options.DevRequestPriority);
        Assert.Equal("[Development request]", options.DevRequestSubjectLabel);
    }

    [Fact]
    public void A_closed_request_ticket_is_reopened_but_never_raised_to_high()
    {
        // A customer answering an agent on a closed request ticket: Medium is not the AI-handled
        // priority, so the reopen leaves the priority the request was filed with.
        var options = new ZohoDeskOptions();

        Assert.NotEqual(options.AiHandledPriority, options.DevRequestPriority);
        Assert.Equal(ZohoTicketSyncWorker.ReopenAction.ReopenStatusOnly,
            ZohoTicketSyncWorker.PlanReopen("Closed", options.DevRequestPriority, escalationSynced: false, options));
    }

    // ── The recovery sweep ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_recovery_sweep_also_finds_a_request_whose_ticket_was_never_created()
    {
        // Zoho down when the request was submitted, and the customer was told to start a New chat
        // for anything else: no next turn will ever retry the create, so the sweep must.
        using var db = new TripExDbContext(new DbContextOptionsBuilder<TripExDbContext>()
            .UseSqlServer("Server=localhost;Database=never-opened;Trusted_Connection=True")
            .Options);

        var sql = ZohoEscalationRecoveryWorker.StuckQuery(db, DateTime.UtcNow).Take(50).ToQueryString();

        Assert.Contains("N'" + ChatService.DevRequestSubmittedIntent + "'", sql);
        Assert.Contains("N'assistant'", sql);
        // Still only mirrored sources, and still the handover leg beside it.
        Assert.Contains("N'internal'", sql);
        Assert.Contains("N'handover'", sql);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static ZohoTicketDraft Draft(bool devRequest)
        => new(Request, "<b>transcript</b>", "Racheli", "racheli@avt.example.test", SessionId, Escalated: false, DevRequest: devRequest);

    private static ZohoDeskService Desk(StubDesk stub, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Zoho:Enabled"] = "true",
            ["Zoho:ClientId"] = "test-client",
            ["Zoho:ClientSecret"] = "test-secret",
            ["Zoho:RefreshToken"] = "test-refresh",
            ["Zoho:OrgId"] = "1",
            ["Zoho:DepartmentId"] = "2",
            ["Zoho:FallbackContactEmail"] = "milo@example.test",
            ["Zoho:AccountsBaseUrl"] = "https://accounts.example.test",
            ["Zoho:ApiBaseUrl"] = "https://desk.example.test",
        };
        foreach (var kv in extra ?? new()) values[kv.Key] = kv.Value;
        return new ZohoDeskService(new StubFactory(stub),
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), NullLogger<ZohoDeskService>.Instance);
    }

    /// <summary>Desk, answered in-process: tokens always, ticket creates from a script of statuses.</summary>
    private sealed class StubDesk : HttpMessageHandler
    {
        public readonly List<HttpStatusCode> CreateStatuses = new();
        public readonly List<JsonElement> TicketCreates = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth/v2/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\",\"expires_in\":3600}");

            if (request.Method == HttpMethod.Post && path == "/api/v1/tickets")
            {
                TicketCreates.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
                var status = TicketCreates.Count <= CreateStatuses.Count ? CreateStatuses[TicketCreates.Count - 1] : HttpStatusCode.OK;
                return status == HttpStatusCode.OK
                    ? Json(status, "{\"id\":\"9001\",\"ticketNumber\":\"104\"}")
                    : Json(status, "{\"errorCode\":\"INVALID_DATA\"}");
            }

            return Json(HttpStatusCode.OK, "{}");
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Every HTTP call ChatService could make — the model, the geolocation lookup — answered here. A
    /// model call gets an ordinary answer, so a turn that should NOT have been a development request
    /// runs to the end the normal way.
    /// </summary>
    private sealed class StubHttp : HttpMessageHandler
    {
        public const string ModelAnswer = "An answer from the model";
        public readonly List<string> Calls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri}");
            if (request.RequestUri!.Host == "oci.example.test")
            {
                var content = JsonSerializer.Serialize(new { intent = "general", text = ModelAnswer, page = "" });
                var body = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
                });
                return Task.FromResult(Json(HttpStatusCode.OK, body));
            }
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"fail\"}"));
        }
    }

    /// <summary>
    /// The connection is never opened: EF is told it is. Raw ADO.NET use (the knowledge search) gets a
    /// connection with no connection string at all, which fails on the spot without trying anything.
    /// </summary>
    private sealed class NeverOpened : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
            => new(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
            => new(InterceptionResult.Suppress());
    }

    /// <summary>
    /// Answers the reads ChatService makes on the way to the turn from a script, accepts every write,
    /// and records every command. What a turn saved is then read back from the change tracker: an
    /// entity that is Unchanged after the turn was written with the values it holds.
    /// </summary>
    private sealed class ScriptedDb : DbCommandInterceptor
    {
        public readonly List<string> Commands = new();
        public Guid Owner;
        public bool HumanInvolved;
        public List<(string Role, string Content, string? Intent)> History = new();
        /// <summary>How many chat_messages writes succeed before the rest fail, as when the database goes away.</summary>
        public int FailMessageWritesAfter = int.MaxValue;
        private int _messageWrites;

        private void FailIfDue(string sql)
        {
            if (!sql.Contains("[chat_messages]", StringComparison.Ordinal)) return;
            if (!sql.Contains("INSERT ", StringComparison.Ordinal) && !sql.Contains("UPDATE ", StringComparison.Ordinal)) return;
            if (++_messageWrites > FailMessageWritesAfter) throw new InvalidOperationException("database unavailable (test)");
        }

        private DbDataReader Answer(DbCommand command)
        {
            var sql = command.CommandText;
            Commands.Add(sql);
            FailIfDue(sql);

            var table = new DataTable();
            if (sql.Contains("UPDATE ", StringComparison.Ordinal))
            {
                // EF checks an UPDATE by the row count the batch selects after it.
                table.Columns.Add("rowcount", typeof(int));
                table.Rows.Add(1);
            }
            else if (sql.Contains("INSERT ", StringComparison.Ordinal) || sql.Contains("DELETE ", StringComparison.Ordinal))
            {
                // Nothing generated on the server, so nothing to read back.
            }
            else if (sql.Contains("[escalated]", StringComparison.Ordinal))
            {
                table.Columns.Add("v", typeof(bool));
                table.Rows.Add(HumanInvolved);
            }
            else if (sql.Contains("FROM [chat_sessions]", StringComparison.Ordinal) && sql.Contains("[user_id]", StringComparison.Ordinal))
            {
                table.Columns.Add("user_id", typeof(Guid));
                table.Rows.Add(Owner);
            }
            else if (sql.Contains("FROM [chat_messages]", StringComparison.Ordinal) && sql.Contains("[c].[role]", StringComparison.Ordinal))
            {
                table.Columns.Add("role", typeof(string));
                table.Columns.Add("content", typeof(string));
                table.Columns.Add("intent", typeof(string));
                // Newest first, as the query orders it.
                for (var i = History.Count - 1; i >= 0; i--)
                    table.Rows.Add(History[i].Role, History[i].Content, (object?)History[i].Intent ?? DBNull.Value);
            }
            return table.CreateDataReader();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
            => InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => new(InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command)));

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            FailIfDue(command.CommandText);
            return new(InterceptionResult<int>.SuppressWithResult(1));
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return new(InterceptionResult<object>.SuppressWithResult(DBNull.Value));
        }
    }

    /// <summary>A real ChatService with a scripted database, stubbed HTTP and an in-memory Zoho queue.</summary>
    private sealed class Harness
    {
        // On in these tests: the feature is off by default (the menu was dropped) and only a literal
        // "true" turns it on — see Off_unless_milo_dev_requests_is_true.
        public string? DevRequestsSwitch = "true";
        public bool ZohoEnabled = true;
        public bool RelayOn;
        public bool HumanInvolved;
        public int FailMessageWritesAfter = int.MaxValue;
        public List<(string Role, string Content, string? Intent)> History { get; } = new();

        public readonly StubHttp Http = new();
        public readonly ZohoTicketSyncQueue Queue = new();
        public ScriptedDb Db { get; private set; } = new();
        private TripExDbContext? _context;
        private ChatService? _service;

        public ChatService Service => _service ??= Build();

        public ChatRequest WidgetRequest(string text, string? menuChoice) => new()
        {
            Text = text,
            CustomerName = "Racheli",
            SessionId = SessionId.ToString(),
            MenuChoice = menuChoice,
        };

        /// <summary>The chat_messages rows this turn wrote: tracked, and Unchanged once a save succeeded.</summary>
        public List<ChatMessage> Messages()
            => _context!.ChangeTracker.Entries<ChatMessage>()
                .Where(e => e.State == EntityState.Unchanged)
                .Select(e => e.Entity).ToList();

        /// <summary>Every chat_messages row this turn wrote or tried to write.</summary>
        public List<ChatMessage> Attempted()
            => _context!.ChangeTracker.Entries<ChatMessage>().Select(e => e.Entity).ToList();

        private ChatService Build()
        {
            Db = new ScriptedDb { Owner = UserId, HumanInvolved = HumanInvolved, History = History, FailMessageWritesAfter = FailMessageWritesAfter };
            // One statement per command and no transaction: the scripted connection is never really
            // open, so it can take neither a transaction nor a batch whose results it would have to fake.
            _context = new TripExDbContext(new DbContextOptionsBuilder<TripExDbContext>()
                .UseSqlServer(new SqlConnection(), o => o.MaxBatchSize(1))
                .AddInterceptors(new NeverOpened(), Db)
                .Options);
            _context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

            var config = new Dictionary<string, string?>
            {
                ["Oracle:ApiKey"] = "test-key",
                ["Oracle:Endpoint"] = "https://oci.example.test/chat",
                ["Oracle:Model"] = "test-model",
                ["Milo:DevRequests"] = DevRequestsSwitch,
                ["Zoho:Enabled"] = ZohoEnabled ? "true" : "false",
                ["Zoho:ClientId"] = "test-client",
                ["Zoho:ClientSecret"] = "test-secret",
                ["Zoho:RefreshToken"] = "test-refresh",
                ["Zoho:OrgId"] = "1",
                ["Zoho:DepartmentId"] = "2",
                ["Zoho:FallbackContactEmail"] = "milo@example.test",
                ["Zoho:AccountsBaseUrl"] = "https://accounts.example.test",
                ["Zoho:ApiBaseUrl"] = "https://desk.example.test",
                ["Zoho:AgentRelayEnabled"] = RelayOn ? "true" : "false",
                ["Zoho:WebhookSecret"] = RelayOn ? new string('s', 32) : "",
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            var factory = new StubFactory(Http);

            return new ChatService(
                _context,
                new OracleAiService(factory, configuration, _context),
                invoiceService: null!, // only the image flow uses it
                new GeolocationService(factory),
                NullLogger<ChatService>.Instance,
                configuration,
                new ZohoDeskService(factory, configuration, NullLogger<ZohoDeskService>.Instance),
                Queue);
        }
    }
}
