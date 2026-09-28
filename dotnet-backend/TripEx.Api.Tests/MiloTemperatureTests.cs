using System.Globalization;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Milo's conversational answer runs at temperature 0, set in code (owner decision 2026-09-28),
/// with Milo:Temperature as the no-deploy way back if greedy decoding turns out to make the
/// thinking model loop. The chatbot_config row's temperature column is no longer read.
///
/// These pin the parser behind that override, because every way it can go wrong is silent:
/// a default that drifted back to 0.3, a typo that clamps to 2 instead of landing on 0, or a
/// server with a decimal-comma locale that reads "0.3" as 3 — each one would change every answer
/// Milo writes without a single error in the log. The ProcessAsync wiring itself has no unit-test
/// seam (nothing here constructs ChatService); the once-per-process "[CHAT] temperature=" log line
/// is how a deployment confirms it.
/// </summary>
public class MiloTemperatureTests
{
    [Fact]
    public void Default_is_zero()
    {
        Assert.Equal(0.0, ChatService.DefaultConversationalTemperature);
        Assert.Equal(0.0, ChatService.ResolveConversationalTemperature(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]  // parses, but is out of range — must not reach the request as-is
    [InlineData("-0.1")]      // below the range: 0, the owner's value, not a clamp to the bound
    [InlineData("2.01")]      // above the range: again 0, never silently raised to 2
    [InlineData("0,3")]       // decimal comma: rejected, never read as 3 (or guessed as 0.3)
    public void Falls_back_to_zero_on_missing_or_invalid(string configured)
    {
        Assert.Equal(0.0, ChatService.ResolveConversationalTemperature(configured));
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.3", 0.3)]     // the old value — the rollback this override exists for
    [InlineData(" 0.7 ", 0.7)]   // whitespace around a hand-edited value
    [InlineData("1", 1.0)]
    [InlineData("2", 2.0)]       // the top of the range is still accepted
    public void Honours_a_valid_override(string configured, double expected)
    {
        Assert.Equal(expected, ChatService.ResolveConversationalTemperature(configured), 3);
    }

    [Theory]
    [InlineData("de-DE")]  // decimal comma, "." is the thousands separator
    [InlineData("he-IL")]  // the Hebrew locale most of Milo's conversations are in
    public void Parsing_ignores_the_thread_culture(string cultureName)
    {
        // The config value is always written with a decimal point, whatever the server's
        // regional settings say. Parsing it with the thread culture would reject "0.3" on a
        // decimal-comma machine (or, with looser number styles, read it as 3) — either way the
        // result is 0, and the rollback would quietly not work on exactly the server it was
        // needed on.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            Assert.Equal(0.3, ChatService.ResolveConversationalTemperature("0.3"), 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
