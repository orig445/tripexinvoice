using System.Text.Json;
using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Regression tests for how a fixed clarifying question's options are rendered — the fix for
/// "the options are written twice" (reported 2026-09-09).
///
/// The TAS widget draws the reply as `&lt;div&gt;${response.text}&lt;/div&gt;` followed by one button per
/// `paramerter` entry, so a question that also carried its own numbered list showed every option
/// twice: once as text, once as a button. The options must be rendered ONCE — as buttons where
/// the client draws them, as the numbered list everywhere else, and never as neither.
///
/// "Never as neither" is the failure this file mostly guards: dropping the list is only safe when
/// buttons are actually going to appear, and there are three separate ways for them not to.
/// </summary>
public class ClarifyOptionRenderingTests
{
    private const string Question = "באיזה סטטוס נמצאת הנסיעה או דוח ההוצאות?";

    private static List<string> CleanOptions() =>
        new() { "Draft", "TR Approval", "Coordinator Approval" };

    // ── Which rendering applies ──────────────────────────────────────────────────────────────

    [Fact]
    public void Widget_client_with_clean_options_gets_buttons()
    {
        Assert.True(ChatService.OptionsRenderAsButtons(CleanOptions(), clientRendersParamerter: true));
    }

    [Fact]
    public void Non_widget_client_never_gets_buttons()
    {
        // This repo's own /chat page reads neither Paramerter nor QuickReplies, so for it the
        // numbered list in "text" is the only way the options reach the user at all.
        Assert.False(ChatService.OptionsRenderAsButtons(CleanOptions(), clientRendersParamerter: false));
    }

    [Fact]
    public void An_option_containing_a_comma_falls_back_to_the_list()
    {
        // The widget does paramerter.split(","), so this option cannot survive as a button —
        // BuildWidgetParamerter refuses the whole set, and no buttons render.
        var options = new List<string>
        {
            "Expense Approved",
            "Other (Matched / Closed / Pending for Cancel / Cancelled)",
            "Draft, revised",
        };
        Assert.False(ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true));
        Assert.Contains("3. Draft, revised", ChatService.ComposeClarifyText(Question, options, false));
    }

    [Fact]
    public void TID_in_the_options_falls_back_to_the_list()
    {
        // "TID" switches the widget to its trip-link rendering (and auto-clicks the first
        // button), which is not what a status question wants.
        var options = new List<string> { "Draft", "TID Approval" };
        Assert.False(ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true));
    }

    [Fact]
    public void No_options_means_no_buttons()
    {
        // The second, model-authored clarifying round has no fixed options at all.
        Assert.False(ChatService.OptionsRenderAsButtons(new List<string>(), clientRendersParamerter: true));
    }

    // ── The composition itself ───────────────────────────────────────────────────────────────

    [Fact]
    public void Buttons_rendering_leaves_the_question_alone()
    {
        var text = ChatService.ComposeClarifyText(Question, CleanOptions(), optionsRenderAsButtons: true);

        Assert.Equal(Question, text);
        Assert.DoesNotContain("1.", text);
        Assert.DoesNotContain("Coordinator Approval", text);
    }

    [Fact]
    public void No_buttons_appends_the_numbered_list()
    {
        var text = ChatService.ComposeClarifyText(Question, CleanOptions(), optionsRenderAsButtons: false);

        Assert.Equal($"{Question}\n1. Draft\n2. TR Approval\n3. Coordinator Approval", text);
    }

    [Fact]
    public void Every_option_survives_the_list_verbatim()
    {
        // The chosen option is sent back as the next user message and matched against these
        // strings, so the numbering may be added but the option text itself must not be touched.
        var options = ChatService.ComposeClarifyText(Question, CleanOptions(), false);
        foreach (var option in CleanOptions())
            Assert.Contains(option, options);
    }

    [Fact]
    public void An_empty_option_set_never_leaves_a_dangling_newline()
    {
        Assert.Equal(Question, ChatService.ComposeClarifyText(Question, new List<string>(), false));
    }

    // ── The real shipping option sets ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_status_lists_do_render_as_buttons(bool tripPath)
    {
        // The whole point of the fix. If a status is ever added that holds a comma, or one whose
        // text contains "TID", BuildWidgetParamerter refuses the set, the widget draws no buttons,
        // and the status question quietly goes back to being a wall of numbered text. That would
        // be invisible in production, so it fails here instead.
        var options = (tripPath
            ? ChatService.TripStatusOptionsForTrip
            : ChatService.TripStatusOptionsForExpenseOnly).ToList();

        Assert.True(ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true),
            "A status option now breaks the widget's paramerter rules (comma, or \"TID\").");
        Assert.Equal(Question, ChatService.ComposeClarifyText(Question, options, true));
    }

    [Fact]
    public void The_real_orientation_options_do_render_as_buttons()
    {
        // Same guard for the fixed three-way opening question, in both languages. Kept as
        // literals because ChatService builds these inline per reply language.
        var hebrew = new List<string>
        {
            "תפעול שוטף של נסיעות והוצאות", "ניתוח נתונים ודוחות במערכת", "ניהול ושינוי הגדרות במערכת",
        };
        var english = new List<string>
        {
            "Travel & expense operations", "Data analysis & reports", "System management & settings",
        };

        Assert.True(ChatService.OptionsRenderAsButtons(hebrew, clientRendersParamerter: true));
        Assert.True(ChatService.OptionsRenderAsButtons(english, clientRendersParamerter: true));
    }

    // ── Telling the clients apart ────────────────────────────────────────────────────────────

    [Fact]
    public void The_live_widget_payload_is_recognised_as_the_widget()
    {
        // Captured verbatim from the live widget on 2026-09-08 by hooking its fetch
        // (DEV_AI_2/assets/script/app.js). Note it sends NO "source" — which is exactly why
        // Source cannot be used to tell it apart from this repo's own /chat page.
        var request = Deserialize("""
        {"companyName":null,"customerName":"Guest","customerId":null,"role":null,
         "sentAt":"2026-09-08T08:12:25.165Z","messageId":"3f2b","locale":"en-US",
         "timezone":"Asia/Jerusalem","platform":"web","appVersion":"1.0.0","pageContext":null,
         "conversationId":"413494f3-0000-4000-8000-000000000000",
         "sessionId":"413494f3-0000-4000-8000-000000000000","module":"web","scope":"webpage",
         "trid":0,"request":"where do I see my reports","text":"where do I see my reports",
         "tts":false}
        """);

        request.NormalizeWidgetShape();

        Assert.True(request.IsTasWidgetClient);
        Assert.Equal("web", request.Source); // the default, NOT something the widget sent
    }

    [Fact]
    public void This_repos_own_chat_page_is_not_the_widget()
    {
        // src/lib/api-service.ts sendChatMessage() — the canonical documented shape.
        var request = Deserialize("""
        {"text":"where do I see my reports","type":"text","source":"web","sessionToken":null,
         "userDate":"","userTime":"","userTimezone":"","audience":"external"}
        """);

        request.NormalizeWidgetShape();

        Assert.False(request.IsTasWidgetClient);
    }

    [Fact]
    public void A_session_id_alone_is_enough_to_recognise_the_widget()
    {
        // customerName has a "Guest" fallback today, but a future widget build could drop the
        // identity fields entirely — the conversation id it always round-trips still identifies it.
        var request = Deserialize("""
        {"sessionId":"413494f3-0000-4000-8000-000000000000","text":"hi"}
        """);

        request.NormalizeWidgetShape();

        Assert.True(request.IsTasWidgetClient);
    }

    [Fact]
    public void IsTasWidgetClient_is_never_taken_from_the_request_body()
    {
        // It is a derived flag, so a caller must not be able to set it — least of all to true,
        // which would suppress the only rendering of the options that caller can display.
        var request = Deserialize("""
        {"text":"hi","isTasWidgetClient":true}
        """);

        Assert.False(request.IsTasWidgetClient);
        request.NormalizeWidgetShape();
        Assert.False(request.IsTasWidgetClient);
    }

    private static ChatRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<ChatRequest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
}
