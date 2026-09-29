using System.Globalization;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Milo's conversational answer runs at temperature 0.3, set in code: the value it has always
/// answered with, kept by Roi on 2026-09-29 after 0 was considered. Milo:Temperature changes it
/// with a config edit and a restart. The chatbot_config row's temperature column is no longer read.
///
/// These pin the parser behind that override, because every way it can go wrong is silent:
/// a default that drifted, a typo that clamps to 2 instead of landing on the default, or a
/// server with a decimal-comma locale that reads "0.1" as 1 — each one would change every answer
/// Milo writes without a single error in the log. The ProcessAsync wiring itself has no unit-test
/// seam (nothing here constructs ChatService); the once-per-process "[CHAT] temperature=" log line
/// is how a deployment confirms it.
/// </summary>
public class MiloTemperatureTests
{
    [Fact]
    public void Default_is_the_temperature_milo_has_always_used()
    {
        Assert.Equal(0.3, ChatService.DefaultConversationalTemperature);
        Assert.Equal(0.3, ChatService.ResolveConversationalTemperature(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]  // parses, but is out of range — must not reach the request as-is
    [InlineData("-0.1")]      // below the range: the default, not a clamp to the bound
    [InlineData("2.01")]      // above the range: again the default, never silently raised to 2
    [InlineData("0,1")]       // decimal comma: rejected, never read as 1 (or guessed as 0.1)
    public void Falls_back_to_the_default_on_missing_or_invalid(string configured)
    {
        Assert.Equal(0.3, ChatService.ResolveConversationalTemperature(configured));
    }

    [Theory]
    [InlineData("0", 0.0)]       // the extreme, still allowed
    [InlineData("0.1", 0.1)]     // the lower setting considered on 2026-09-29
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
        // decimal-comma machine (or, with looser number styles, read "0.1" as 1) — either way the
        // override would quietly not work on exactly the server it was needed on. "0.1" rather
        // than "0.3" so that a rejected value, which falls back to 0.3, cannot pass this test.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            Assert.Equal(0.1, ChatService.ResolveConversationalTemperature("0.1"), 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
