using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The status-list turn is the one turn whose reply is fully decided before the model is asked:
/// the user has picked "travel &amp; expense operations" from the fixed orientation question, so
/// the answer is the fixed status list and only the choice between the two lists is open.
/// Detecting it is what lets ChatService skip the 60,000-character prompt there — measured at
/// 39.3 s and 3,561 thinking tokens in production on 2026-09-08, the slowest turn in the sample.
///
/// These pin the detection, because both of its failure directions are silent: miss it and the
/// slow turn quietly comes back, fire it wrongly and a real question gets answered with a status
/// list instead.
/// </summary>
public class StatusListShortcutTests
{
    private static (string Role, string Content, string? Intent) Assistant(string intent, string text = "…")
        => ("assistant", text, intent);

    private static (string Role, string Content, string? Intent) User(string text)
        => ("user", text, null);

    // ── The round trip that the whole shortcut rests on ───────────────────────────────────────

    [Theory]
    [InlineData(0, true)]   // travel & expense operations — the branch that ends in a status list
    [InlineData(1, false)]  // data analysis & reports
    [InlineData(2, false)]  // system management & settings
    public void Recognises_exactly_the_operations_option_it_ships(int index, bool expected)
    {
        // The options are WRITTEN from these constants and READ back from them one turn later,
        // because the widget re-sends a clicked button's label verbatim. Comparing against the
        // shipping arrays rather than against copies is the point: a reworded option that broke
        // the recognition would leave the flow working and merely slow again, which no other
        // test would catch.
        Assert.Equal(expected, ChatService.IsOperationsOrientationAnswer(ChatService.OrientationOptionsHe[index]));
        Assert.Equal(expected, ChatService.IsOperationsOrientationAnswer(ChatService.OrientationOptionsEn[index]));
    }

    [Theory]
    [InlineData("תפעול שוטף של נסיעות והוצאות.")]      // a trailing period the model added
    [InlineData("  תפעול שוטף של נסיעות והוצאות  ")]   // whitespace around a pasted label
    [InlineData("‏ תפעול שוטף של נסיעות והוצאות")] // an RTL mark from the browser
    [InlineData("\"Travel & expense operations\"")]      // quoted back
    [InlineData("1. Travel & expense operations")]       // typed with the list number still on it
    public void Survives_the_ways_a_label_comes_back_altered(string text)
    {
        Assert.True(ChatService.IsOperationsOrientationAnswer(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("לא")]
    [InlineData("איך מגישים דוח הוצאות?")]
    [InlineData("travel")]  // a word FROM the option is not the option
    public void Does_not_fire_on_anything_that_is_not_that_option(string? text)
    {
        Assert.False(ChatService.IsOperationsOrientationAnswer(text));
    }

    // ── The turn itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fires_right_after_the_fixed_orientation_question()
    {
        var history = new List<(string, string, string?)>
        {
            User("איך אני יודע משהו על נסיעה מסוימת?"),
            Assistant("clarify", "כדי שאוכל לכוון אותך…"),
            User(ChatService.OrientationOptionsHe[0]),
        };

        Assert.True(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsHe[0]));
    }

    [Fact]
    public void Does_not_fire_a_second_time_once_the_list_was_already_shown()
    {
        // The user is now answering the STATUS list, not the orientation question. Its reply is
        // a real answer that needs the full prompt — firing here would replace that answer with
        // the same list all over again.
        var history = new List<(string, string, string?)>
        {
            User("איך אני יודע משהו על נסיעה מסוימת?"),
            Assistant("clarify"),
            User(ChatService.OrientationOptionsHe[0]),
            Assistant("clarify_status_trip", "באיזה סטטוס נמצאת הנסיעה?"),
            User("Draft"),
        };

        Assert.False(ChatService.IsStatusListTurn(history, "Draft"));
    }

    [Fact]
    public void Does_not_fire_when_the_previous_turn_was_an_ordinary_answer()
    {
        // Same words, no orientation question behind them: someone who simply types the phrase
        // mid-conversation is asking a question, and must get the full prompt.
        var history = new List<(string, string, string?)>
        {
            User("שלום"),
            Assistant("help", "שלום! איך אוכל לעזור?"),
            User(ChatService.OrientationOptionsHe[0]),
        };

        Assert.False(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsHe[0]));
    }

    [Fact]
    public void Does_not_fire_for_the_other_two_areas()
    {
        var history = new List<(string, string, string?)>
        {
            User("איפה רואים דוחות?"),
            Assistant("clarify"),
            User(ChatService.OrientationOptionsHe[1]),
        };

        // Reports and settings both continue to a real page answer, which needs the catalog.
        Assert.False(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsHe[1]));
        Assert.False(ChatService.IsStatusListTurn(history, ChatService.OrientationOptionsHe[2]));
    }

    [Fact]
    public void Is_safe_on_an_empty_history()
    {
        // History loading is best-effort — it comes back empty whenever the DB is unavailable.
        // With no orientation question on record the shortcut must decline, not throw.
        Assert.False(ChatService.IsStatusListTurn(new List<(string, string, string?)>(),
            ChatService.OrientationOptionsHe[0]));
    }

    // ── Which question the trip-vs-expense choice is made against ────────────────────────────

    [Fact]
    public void Finds_the_question_that_triggered_the_orientation_round()
    {
        // Deciding trip-vs-expense from the button the user just clicked is impossible — it
        // names an area and says nothing about either. The original question is the input.
        var history = new List<(string, string, string?)>
        {
            User("שלום"),
            Assistant("general", "שלום!"),
            User("מה קורה עם דוח ההוצאות שלי?"),
            Assistant("clarify", "כדי שאוכל לכוון אותך…"),
            User(ChatService.OrientationOptionsHe[0]),
        };

        Assert.Equal("מה קורה עם דוח ההוצאות שלי?", ChatService.FindQuestionBeforeOrientation(history));
    }

    [Fact]
    public void Returns_null_rather_than_guessing_when_there_is_no_earlier_question()
    {
        var history = new List<(string, string, string?)>
        {
            Assistant("clarify", "כדי שאוכל לכוון אותך…"),
            User(ChatService.OrientationOptionsHe[0]),
        };

        // Null is the caller's signal to take the safe default (the full trip list) instead of
        // sending an empty question to the model and trusting whatever comes back.
        Assert.Null(ChatService.FindQuestionBeforeOrientation(history));
        Assert.Null(ChatService.FindQuestionBeforeOrientation(new List<(string, string, string?)>()));
    }
}
