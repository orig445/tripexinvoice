using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Full-catalog regression test for Milo's page-link resolution (ChatService.ResolvePageOverride).
/// Runs every time via `dotnet test` — this is the permanent, repeatable version of the
/// one-off collision analysis from 2026-08-17 that found "Travel Status" alone could hijack
/// 26 other entries' links, and "ManageTAS" hijacking a Travel Status question in production.
///
/// For every non-hub, non-excluded page in Data/page-links.json, builds an adversarial
/// simulated AI answer (its own Description FIRST, its own name only afterward, in a step) —
/// the exact ordering that caused the real bugs — and asserts the resolved page still matches
/// the AI's own correctly-stated "page" field. If this ever regresses (e.g. someone reverts the
/// "trust an already-specific page" guard, or a new page's Description happens to embed another
/// page's exact name), this test fails immediately instead of surfacing as a live wrong-link bug.
/// </summary>
public class MiloLinkResolutionTests
{
    public static IEnumerable<object[]> AllCatalogEntries()
    {
        foreach (var kv in ChatService.AllPageLinks)
        {
            if (kv.Value.Category == "Navigation") continue; // hubs are the fallback being overridden, not a target
            var name = !string.IsNullOrWhiteSpace(kv.Value.LabelEn) ? kv.Value.LabelEn : kv.Value.Label;
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return new object[] { kv.Key, kv.Value.Category, name, kv.Value.Description };
        }
    }

    [Fact]
    public void Catalog_has_entries_loaded()
    {
        // Guards against the whole suite below silently passing with zero entries if
        // page-links.json failed to load (e.g. the file wasn't copied to test output).
        Assert.True(ChatService.AllPageLinks.Count > 300,
            $"Expected 300+ page-links.json entries, found {ChatService.AllPageLinks.Count} — " +
            "is Data/page-links.json missing from the test output directory?");
    }

    [Theory]
    [MemberData(nameof(AllCatalogEntries))]
    public void Every_specific_page_survives_an_adversarial_description_first_answer(
        string key, string category, string name, string description)
    {
        // Description BEFORE the entry's own name is stated — the ordering that actually
        // caused the ManageTAS and Travel-Status-family bugs (a generic/explanatory opening
        // line mentions a DIFFERENT page's name before this page's own name appears).
        var simulatedText =
            $"{description}\n\nTo use this feature, follow these steps:\n" +
            $"1. Go to Settings -> {category} -> {name}.\n" +
            "2. Configure the settings as needed.\n3. Save your changes.";

        var resolved = ChatService.ResolvePageOverride(key, simulatedText);

        Assert.Equal(key, resolved);
    }

    [Fact]
    public void ManageTAS_question_resolves_to_travel_status_not_the_generic_hub()
    {
        // The exact live bug found 2026-08-17: asking to manage TAS record status codes
        // returned the excluded generic "ManageTAS" page instead of the real Travel Status
        // settings page, because the AI's own paraphrase of the question ("To manage TAS
        // record status codes...") happened to contain the excluded entry's label text.
        var simulatedText =
            "To manage TAS record status codes, follow these steps:\n" +
            "1. Go to Settings -> Master Files.\n2. Click on 'Travel Status'.\n\n" +
            "The 'Travel Status' page allows you to define and manage the different status " +
            "codes used for TAS records in the system.";

        var resolved = ChatService.ResolvePageOverride("tbl_glob_travel_status", simulatedText);

        Assert.Equal("tbl_glob_travel_status", resolved);
    }

    [Fact]
    public void Missing_page_field_still_falls_back_to_the_specific_report_named_in_text()
    {
        // The ORIGINAL failure mode this override exists to fix must keep working: when the
        // AI's own "page" is empty/missing, the specific report actually named in the text
        // should still be picked up as a fallback.
        var simulatedText =
            "This shows trip approval status alongside payment details.\n\n" +
            "Select Travel Status with Paid Data - 01006";

        var resolved = ChatService.ResolvePageOverride(page: null, simulatedText);

        Assert.Equal("TASR_01006_TravelStatusWithPaidData", resolved);
    }
}
