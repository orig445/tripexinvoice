using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Regression test for ChatService.CountTrailingConsecutiveClarifications — the counting
/// logic behind the "at most 2 clarifying questions in a row, then escalate" cap. Milo's own
/// judgment on when to ask "clarify" isn't perfectly reliable, so this cap is the hard,
/// code-level backstop; a counting mistake here would silently let the model interrogate the
/// user indefinitely (or, the opposite bug, escalate far too early) instead of enforcing the
/// intended limit. "clarify_status_trip" and "clarify_status_expense" (the fixed operations/
/// status-list follow-ups) are distinct intent values for logging purposes but must both count
/// toward the SAME streak as "clarify".
/// </summary>
public class ClarifyCapTests
{
    private static (string Role, string Content, string? Intent) User(string content = "u")
        => ("user", content, null);

    private static (string Role, string Content, string? Intent) Assistant(string intent)
        => ("assistant", "a", intent);

    [Fact]
    public void Empty_history_counts_as_zero()
    {
        var count = ChatService.CountTrailingConsecutiveClarifications(
            new List<(string, string, string?)>());
        Assert.Equal(0, count);
    }

    [Fact]
    public void Single_prior_clarify_counts_as_one()
    {
        var history = new List<(string, string, string?)>
        {
            User("original question"),
            Assistant("clarify"),
            User("current answer"),
        };
        Assert.Equal(1, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Fact]
    public void Two_consecutive_prior_clarifies_count_as_two()
    {
        var history = new List<(string, string, string?)>
        {
            User("q1"),
            Assistant("clarify"),
            User("a1"),
            Assistant("clarify"),
            User("a2"),
        };
        Assert.Equal(2, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Fact]
    public void A_real_answer_in_between_resets_the_streak()
    {
        // The exact scenario the cap must NOT punish: an old clarify from earlier in a
        // long-lived session that was already resolved by a real answer, followed by a
        // brand new, unrelated question that also happens to need a clarify.
        var history = new List<(string, string, string?)>
        {
            User("q1"),
            Assistant("clarify"),
            User("a1"),
            Assistant("help"), // real answer breaks the streak
            User("q2 - unrelated new topic"),
        };
        Assert.Equal(0, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Fact]
    public void Only_the_run_immediately_before_the_current_turn_counts()
    {
        // Two clarifies happened long ago but were followed by a real answer; only the one
        // clarify immediately preceding the current turn should count (not three).
        var history = new List<(string, string, string?)>
        {
            User("q1"), Assistant("clarify"), User("a1"), Assistant("clarify"), User("a2"),
            Assistant("help"), // resets
            User("q2"), Assistant("clarify"), User("a3"),
        };
        Assert.Equal(1, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Fact]
    public void Escalate_also_breaks_the_streak()
    {
        var history = new List<(string, string, string?)>
        {
            Assistant("clarify"),
            Assistant("escalate"), // a forced/legitimate escalation also resets
        };
        Assert.Equal(0, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Theory]
    [InlineData("clarify_status_trip")]
    [InlineData("clarify_status_expense")]
    public void Clarify_then_clarify_status_counts_as_two_and_hits_the_cap(string statusIntent)
    {
        // The designed operations-path flow: round 1 orientation ("clarify"), round 2 one of
        // the fixed status lists — by round 3 the count must already be at the 2-question cap
        // so a genuine third clarify-type turn (which shouldn't happen in this flow, but might
        // from a model mistake) is forced to escalate instead of asking again. Both the trip
        // and the expense-only variant must behave identically here.
        var history = new List<(string, string, string?)>
        {
            User("q1 - vague operations question"),
            Assistant("clarify"),       // fixed 3-way orientation
            User("1"),                  // picks "travel & expense operations"
            Assistant(statusIntent),    // fixed status list (trip or expense-only)
            User("Approved"),           // picks a status
        };
        Assert.Equal(2, ChatService.CountTrailingConsecutiveClarifications(history));
    }

    [Theory]
    [InlineData("clarify_status_trip")]
    [InlineData("clarify_status_expense")]
    public void Clarify_status_alone_still_counts(string statusIntent)
    {
        // Defensive case: even without a preceding plain "clarify" in view (e.g. history
        // trimmed, or a future flow that jumps straight to a status list), it must still be
        // recognized as a clarifying-type turn on its own.
        var history = new List<(string, string, string?)>
        {
            Assistant(statusIntent),
        };
        Assert.Equal(1, ChatService.CountTrailingConsecutiveClarifications(history));
    }
}
