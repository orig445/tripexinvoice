using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The "Other" choice under every clarifying question (Roi, 2026-09-28). Two halves, both pinned
/// here: it has to be ADDED to every option set without breaking the widget's button rules, and
/// pressing it has to be RECOGNISED on the next turn so the reply is the fixed "tell me in your
/// own words" line instead of a full model turn.
/// </summary>
public class OtherOptionTests
{
    private static (string Role, string Content, string? Intent) Assistant(string intent, string text = "…")
        => ("assistant", text, intent);

    private static (string Role, string Content, string? Intent) User(string text)
        => ("user", text, null);

    // ── Adding it ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Appends_the_label_in_the_language_of_the_question()
    {
        var options = new List<string> { "דוח א", "דוח ב" };

        Assert.Equal(new[] { "דוח א", "דוח ב", "אחר" }, ChatService.WithOtherOption(options, hebrew: true));
        Assert.Equal(new[] { "דוח א", "דוח ב", "Other" }, ChatService.WithOtherOption(options, hebrew: false));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("other")]
    [InlineData("Other.")]
    [InlineData("אחר")]
    [InlineData("‏אחר")] // with an RTL mark
    [InlineData("None of these")]
    [InlineData("Something else")]
    [InlineData("משהו אחר")]
    [InlineData("אף אחד מאלה")]
    public void A_catch_all_the_model_wrote_becomes_the_one_canonical_other(string existing)
    {
        // One way out, never two — and in the canonical wording, because only that wording is
        // recognised on the next turn and gets the instant reply.
        var options = new List<string> { "Budget by Division", "Budget by Cost Center", existing };

        var english = ChatService.WithOtherOption(options, hebrew: false);
        var hebrew = ChatService.WithOtherOption(options, hebrew: true);

        Assert.Equal(new[] { "Budget by Division", "Budget by Cost Center", "Other" }, english);
        Assert.Equal(new[] { "Budget by Division", "Budget by Cost Center", "אחר" }, hebrew);
    }

    [Theory]
    [InlineData("Other reports")]
    [InlineData("דוח אחר")]
    [InlineData("Another trip")]
    public void A_real_choice_that_merely_contains_the_word_is_kept(string choice)
    {
        var options = new List<string> { "Budget by Division", choice };

        var result = ChatService.WithOtherOption(options, hebrew: false);

        Assert.Equal(new[] { "Budget by Division", choice, "Other" }, result);
    }

    // ── Which language it goes out in ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("האם התכוונת ל-Budget by Division או Budget by Cost Center?", "איך מוציאים דוח תקציב?", true)]
    [InlineData("האם הדוח ב-Expense Approval או ב-Expense Approved?", "Budget", true)] // Latin-heavy Hebrew question
    [InlineData("Which of the two budget reports do you mean?", "איך מוציאים דוח תקציב?", true)] // Hebrew customer
    [InlineData("Which of the two budget reports do you mean?", "How do I export a budget report?", false)]
    public void The_label_follows_the_conversation_not_a_letter_count(string question, string userText, bool hebrew)
    {
        Assert.Equal(hebrew, ChatService.OtherLabelIsHebrew(question, userText));
    }

    [Fact]
    public void A_hebrew_customer_who_pressed_an_english_label_is_answered_in_hebrew()
    {
        var history = new List<(string, string, string?)>
        {
            User("איך מוציאים דוח?"),
            Assistant("clarify", "האם התכוונת ל-Budget by Division או Budget by Cost Center?"),
        };

        Assert.True(ChatService.OtherReplyIsHebrew(history, "Other"));
        Assert.True(ChatService.OtherReplyIsHebrew(history, "אחר"));
    }

    [Fact]
    public void An_english_conversation_is_answered_in_english()
    {
        var history = new List<(string, string, string?)>
        {
            User("How do I export a report?"),
            Assistant("clarify", "Which of the two budget reports do you mean?"),
        };

        Assert.False(ChatService.OtherReplyIsHebrew(history, "Other"));
    }

    [Fact]
    public void An_empty_set_stays_empty()
    {
        // No choices means a free-text question — a lone "Other" button would be a choice of one.
        Assert.Empty(ChatService.WithOtherOption(new List<string>(), hebrew: true));
    }

    [Fact]
    public void Never_changes_the_shipped_lists()
    {
        var before = ChatService.TripStatusOptionsForTrip.Count;

        ChatService.WithOtherOption(ChatService.TripStatusOptionsForTrip, hebrew: true);

        Assert.Equal(before, ChatService.TripStatusOptionsForTrip.Count);
    }

    [Fact]
    public void The_status_lists_carry_no_other_of_their_own()
    {
        // Each used to end in its own "Other" — a status bucket on the trip list, a catch-all on
        // the expense list. Next to the generic one that would be two buttons both called Other.
        Assert.DoesNotContain(ChatService.TripStatusOptionsForTrip, o => o.StartsWith("Other", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ChatService.TripStatusOptionsForExpenseOnly, o => o.StartsWith("Other", StringComparison.OrdinalIgnoreCase));
        // The four statuses the trip bucket stood for are still offered.
        Assert.Contains("Matched / Closed / Pending for Cancel / Cancelled", ChatService.TripStatusOptionsForTrip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_real_option_set_still_renders_as_buttons_with_other_added(bool hebrew)
    {
        // A comma or "TID" anywhere in the set turns every button back into a numbered list, so
        // the added label has to pass the same rules as the rest, on every set it joins.
        var sets = new[]
        {
            ChatService.TripStatusOptionsForTrip,
            ChatService.TripStatusOptionsForExpenseOnly,
            hebrew ? ChatService.OrientationOptionsHe : ChatService.OrientationOptionsEn,
        };

        foreach (var set in sets)
        {
            var withOther = ChatService.WithOtherOption(set, hebrew);
            Assert.True(ChatService.OptionsRenderAsButtons(withOther, clientRendersParamerter: true),
                string.Join(" | ", withOther));
        }
    }

    // ── Recognising the click ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("clarify")]
    [InlineData("clarify_status_trip")]
    [InlineData("clarify_status_expense")]
    public void Fires_after_any_question_that_offered_it(string intent)
    {
        var history = new List<(string, string, string?)>
        {
            User("איך מוציאים דוח?"),
            Assistant(intent),
            User("אחר"),
        };

        Assert.True(ChatService.IsOtherOptionPick(history, "אחר"));
        Assert.True(ChatService.IsOtherOptionPick(history, "Other"));
    }

    [Theory]
    [InlineData("  אחר ")]
    [InlineData("‏אחר")]
    [InlineData("Other.")]
    [InlineData("4. Other")] // typed with the list number still on it
    public void Survives_the_ways_a_label_comes_back_altered(string text)
    {
        var history = new List<(string, string, string?)> { User("?"), Assistant("clarify") };

        Assert.True(ChatService.IsOtherOptionPick(history, text));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("escalate")]
    [InlineData("general")]
    [InlineData(ChatService.OtherPickIntent)] // its own reply offers no options
    public void Does_not_fire_after_a_turn_that_offered_no_other(string intent)
    {
        var history = new List<(string, string, string?)> { User("?"), Assistant(intent) };

        Assert.False(ChatService.IsOtherOptionPick(history, "אחר"));
    }

    [Fact]
    public void Does_not_fire_at_the_start_of_a_conversation()
    {
        Assert.False(ChatService.IsOtherOptionPick(new List<(string, string, string?)>(), "Other"));
        Assert.False(ChatService.IsOtherOptionPick(new List<(string, string, string?)> { User("Other") }, "Other"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("other reports")]
    [InlineData("משהו אחר לגמרי")]
    [InlineData("Draft")]
    public void Does_not_fire_on_anything_that_is_not_the_label(string? text)
    {
        var history = new List<(string, string, string?)> { User("?"), Assistant("clarify") };

        Assert.False(ChatService.IsOtherOptionPick(history, text));
    }

    // ── What the reply does to the rest of the flow ──────────────────────────────────────────

    [Fact]
    public void The_reply_counts_as_a_question_in_the_run()
    {
        // So the every-fourth support offer keeps counting through an "Other" pick, and a clarify
        // after it is a later round rather than the fixed orientation question all over again.
        var history = new List<(string, string, string?)>
        {
            User("?"), Assistant("clarify"),
            User("אחר"), Assistant(ChatService.OtherPickIntent),
        };

        Assert.Equal(2, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Fact]
    public void An_orientation_label_typed_after_it_is_not_a_status_list_turn()
    {
        // The status-list shortcut may only follow the fixed orientation question itself.
        var history = new List<(string, string, string?)>
        {
            User("?"), Assistant("clarify"),
            User("אחר"), Assistant(ChatService.OtherPickIntent),
        };

        Assert.False(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsHe[0]));
    }

    [Fact]
    public void The_reply_is_in_the_language_of_the_click()
    {
        Assert.Contains("במילים שלך", ChatService.OtherOptionReply(hebrew: true));
        Assert.Contains("in your own words", ChatService.OtherOptionReply(hebrew: false));
    }
}
