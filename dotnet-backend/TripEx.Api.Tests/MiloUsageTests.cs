using Microsoft.Extensions.Configuration;
using TripEx.Api.Controllers;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The /usage page (Roi, 2026-09-29): usage and estimated OCI cost per company and per user.
/// These pin every number the page shows, because each way it can go wrong is silent — a cost
/// that leaves out the thinking tokens, a user counted under the wrong company, a day cut at the
/// wrong hour — and the page would just show a plausible, wrong figure.
/// </summary>
public class MiloUsageTests
{
    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices = MiloUsageReport.DefaultPrices;
    private static readonly DateOnly Day1 = new(2026, 9, 29);

    private static UsageRow Row(string? company, string? email, int prompt, int completion, int total,
        Guid? session = null, string kind = MiloUsage.AnswerKind, string model = "google.gemini-2.5-pro",
        DateTime? at = null, string? name = null)
        => new(session ?? Guid.NewGuid(), at ?? new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc), kind, model,
            prompt, completion, total, company, email, name);

    // ── Tokens and cost ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Thinking_tokens_are_billed_as_output()
    {
        // The real call from the 2026-09-29 log: an 86-token reply billed as 2,519 output tokens.
        var usage = new OciUsage("google.gemini-2.5-pro", 39534, 86, 42053);
        Assert.Equal(2519, usage.BilledOutputTokens);
    }

    [Theory]
    [InlineData(100, 20, -1, 20)]    // total not reported: completion is all we know
    [InlineData(100, 20, 50, 20)]    // total smaller than the prompt: unreadable, fall back
    [InlineData(-1, 20, 500, 20)]    // prompt not reported
    [InlineData(100, 20, 120, 20)]   // no thinking at all
    public void Missing_or_odd_counts_fall_back_to_completion(int prompt, int completion, int total, int expected)
    {
        Assert.Equal(expected, new OciUsage("m", prompt, completion, total).BilledOutputTokens);
    }

    [Fact]
    public void Cost_is_input_and_billed_output_at_the_model_price()
    {
        // 39,534 × $1.25/M + 2,519 × $10/M = $0.0494175 + $0.02519
        var cost = MiloUsageReport.Cost(Row("Avt", "a@b.c", 39534, 86, 42053), Prices);
        Assert.Equal(0.0746075m, cost);
    }

    [Fact]
    public void A_model_with_no_price_costs_nothing_and_is_named_on_the_page()
    {
        var rows = new List<UsageRow> { Row("Avt", "a@b.c", 1000, 10, 1010, model: "cohere.command-r") };

        var s = MiloUsageReport.Summarize(rows, Prices, Day1, Day1, TimeZoneInfo.Utc);

        Assert.Null(MiloUsageReport.Cost(rows[0], Prices));
        Assert.Equal(0m, s.Totals.CostUsd);
        Assert.Contains("cohere.command-r", s.UnpricedModels);
    }

    [Fact]
    public void Config_prices_override_the_defaults_model_by_model()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Milo:Pricing:google.gemini-2.5-pro:OutputPerMillion"] = "12.5",
            ["Milo:Pricing:my.model:InputPerMillion"] = "2",
            ["Milo:Pricing:my.model:OutputPerMillion"] = "4",
        }).Build();

        var prices = MiloUsageReport.LoadPrices(cfg);

        Assert.Equal(1.25m, prices["google.gemini-2.5-pro"].InputPerMillion);   // kept from the default
        Assert.Equal(12.5m, prices["google.gemini-2.5-pro"].OutputPerMillion);  // overridden
        Assert.Equal(2m, prices["my.model"].InputPerMillion);
        Assert.Equal(0.30m, prices["google.gemini-2.5-flash"].InputPerMillion); // untouched default
    }

    // ── The breakdown ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Usage_is_split_by_company_and_then_by_user()
    {
        var s1 = Guid.NewGuid();
        var rows = new List<UsageRow>
        {
            Row("Avt", "racheli@avt.co.il", 1000, 10, 1010, s1, name: "Racheli"),
            Row("Avt", "RACHELI@avt.co.il", 1000, 10, 1010, s1),                 // same user, other casing
            Row("Avt", "dana@avt.co.il", 1000, 10, 1010),
            Row("QA", "qauser@tripex.io", 1000, 10, 1010),
            Row(null, null, 1000, 10, 1010),                                     // before the update
        };

        var s = MiloUsageReport.Summarize(rows, Prices, Day1, Day1, TimeZoneInfo.Utc);

        Assert.Equal(3, s.Companies.Count);
        var avt = s.Companies.Single(c => c.Company == "Avt");
        Assert.Equal(3, avt.Answers);
        Assert.Equal(2, avt.Conversations);
        Assert.Equal(2, avt.Users.Count);
        var racheli = avt.Users.Single(u => u.Email == "racheli@avt.co.il");
        Assert.Equal(2, racheli.Answers);
        Assert.Equal(1, racheli.Conversations);
        Assert.Equal("Racheli", racheli.Name);
        Assert.Contains(s.Companies, c => c.Company == null);
        Assert.Equal(5, s.Totals.Answers);
        Assert.Equal(4, s.Totals.Conversations);
    }

    [Fact]
    public void A_classifier_call_costs_money_but_is_not_an_answer()
    {
        var session = Guid.NewGuid();
        var rows = new List<UsageRow>
        {
            Row("Avt", "a@avt.co.il", 300, 5, 305, session, MiloUsage.ClassifierKind),
            Row("Avt", "a@avt.co.il", 1000, 10, 1010, session),
        };

        var s = MiloUsageReport.Summarize(rows, Prices, Day1, Day1, TimeZoneInfo.Utc);

        Assert.Equal(1, s.Totals.Answers);
        Assert.Equal(1, s.Totals.Conversations);
        Assert.Equal(rows.Sum(r => MiloUsageReport.Cost(r, Prices)!.Value), s.Totals.CostUsd);
    }

    [Fact]
    public void Companies_are_ordered_by_cost_and_every_day_in_the_range_is_listed()
    {
        var rows = new List<UsageRow>
        {
            Row("Small", "a@s.c", 100, 1, 101),
            Row("Big", "b@b.c", 100000, 1000, 101000),
        };

        var s = MiloUsageReport.Summarize(rows, Prices, new DateOnly(2026, 9, 27), Day1, TimeZoneInfo.Utc);

        Assert.Equal("Big", s.Companies[0].Company);
        Assert.Equal(new[] { "2026-09-27", "2026-09-28", "2026-09-29" }, s.Days.Select(d => d.Date));
        Assert.Equal(0, s.Days[0].Answers);
        Assert.Equal(2, s.Days[2].Answers);
    }

    [Fact]
    public void Days_are_cut_at_israel_midnight_not_utc()
    {
        var israel = MiloUsageReport.ReportZone();
        if (israel == TimeZoneInfo.Utc) return; // a machine without the zone cannot show the difference

        // 22:30 UTC on 28.9 is 01:30 on 29.9 in Israel (summer time, UTC+3).
        var rows = new List<UsageRow> { Row("Avt", "a@b.c", 100, 1, 101, at: new DateTime(2026, 9, 28, 22, 30, 0, DateTimeKind.Utc)) };

        var s = MiloUsageReport.Summarize(rows, Prices, new DateOnly(2026, 9, 28), Day1, israel);

        Assert.Equal(0, s.Days.Single(d => d.Date == "2026-09-28").Answers);
        Assert.Equal(1, s.Days.Single(d => d.Date == "2026-09-29").Answers);
    }

    [Fact]
    public void The_window_runs_from_local_midnight_to_the_midnight_after_the_last_day()
    {
        var israel = MiloUsageReport.ReportZone();
        if (israel == TimeZoneInfo.Utc) return;

        var (fromUtc, toUtc) = MiloUsageReport.UtcWindow(Day1, Day1, israel);

        Assert.Equal(new DateTime(2026, 9, 28, 21, 0, 0), fromUtc);
        Assert.Equal(new DateTime(2026, 9, 29, 21, 0, 0), toUtc);
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_export_opens_in_excel_with_hebrew_and_commas_intact()
    {
        var rows = new List<UsageRow> { Row("Avt", "a@avt.co.il", 1000, 10, 1010, name: "רחלי, מנהלת \"ראשית\"") };
        var csv = MiloUsageReport.ToCsv(MiloUsageReport.Summarize(rows, Prices, Day1, Day1, TimeZoneInfo.Utc));

        Assert.StartsWith("﻿", csv);
        Assert.Contains("\"רחלי, מנהלת \"\"ראשית\"\"\"", csv);
        Assert.Contains("\"Total\"", csv);
    }

    // ── The page's lock ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_page_opens_only_with_the_configured_key()
    {
        const string key = "0123456789abcdef0123456789abcdef";
        Assert.True(UsageController.KeyMatches(key, key));
        Assert.True(UsageController.KeyMatches(" " + key + " ", key));
        Assert.False(UsageController.KeyMatches(key, key + "x"));
        Assert.False(UsageController.KeyMatches(key, key.ToUpperInvariant()));
        Assert.False(UsageController.KeyMatches(key, ""));
        Assert.False(UsageController.KeyMatches(key, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key")]   // under 24 characters: the page does not exist at all
    public void Without_a_real_key_configured_there_is_no_page(string? configured)
    {
        Assert.False(UsageController.KeyMatches(configured, configured ?? ""));
    }

    [Fact]
    public void No_dates_means_this_month_so_far()
    {
        Assert.True(UsageController.TryParseRange(null, null, Day1, out var from, out var to));
        Assert.Equal(new DateOnly(2026, 9, 1), from);
        Assert.Equal(Day1, to);
    }

    [Theory]
    [InlineData("2026-09-30", "2026-09-01")]  // the wrong way round
    [InlineData("2024-01-01", "2026-09-29")]  // far longer than a year
    [InlineData("29/09/2026", null)]          // not yyyy-MM-dd
    public void A_bad_range_is_refused_not_trimmed(string? from, string? to)
    {
        Assert.False(UsageController.TryParseRange(from, to, Day1, out _, out _));
    }
}
