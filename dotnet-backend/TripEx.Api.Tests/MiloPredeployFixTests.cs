using TripEx.Api.Controllers;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Fixes from the review before the usage page and the company link first went live
/// (2026-10-03): the key out of every request line, the history of one person as one row, a
/// "Guest" that is nobody in particular, a CSV that cannot run as Excel formulas, and an import
/// that neither double-counts nor falls over on a line it did not expect.
/// </summary>
public class MiloPredeployFixTests
{
    private static readonly DateOnly Day1 = new(2026, 9, 29);

    private static UsageRow Row(string? company, string? email, string? name, int prompt = 100, int total = 150,
        Guid? session = null)
        => new(session ?? Guid.NewGuid(), new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc), MiloUsage.AnswerKind,
            "google.gemini-2.5-pro", prompt, 10, total, company, email, name);

    // ── The key ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key")]
    [InlineData("   23-chars-is-too-short   ")]
    public void Without_a_usable_key_configured_the_page_does_not_exist(string? configured)
    {
        Assert.False(UsageController.IsConfigured(configured));
    }

    [Fact]
    public void A_key_of_24_or_more_characters_opens_the_page()
    {
        Assert.True(UsageController.IsConfigured(new string('k', UsageController.MinKeyLength)));
    }

    [Fact]
    public void The_key_is_never_part_of_a_route()
    {
        // The page and its calls are routed without the key, so it is in no request line and in
        // no log. It travels after the # (page) and in a header (data calls).
        var routes = typeof(UsageController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false))
            .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
            .Select(a => a.Template ?? "")
            .ToList();

        Assert.Equal(4, routes.Count);
        Assert.All(routes, r => Assert.DoesNotContain("{", r));
        Assert.Equal("X-Usage-Key", UsageController.KeyHeader);
    }

    // ── One person, one row ──────────────────────────────────────────────────────────────────

    [Fact]
    public void History_without_an_email_joins_the_same_name_live_row()
    {
        var rows = new List<UsageRow>
        {
            Row("Avt", "racheli@avt.co.il", "Racheli Administrator"),
            Row(null, null, "Racheli Administrator"),   // imported from the log
        };

        var s = MiloUsageReport.Summarize(rows, MiloUsageReport.DefaultPrices, Day1, Day1, TimeZoneInfo.Utc);

        var company = Assert.Single(s.Companies);
        Assert.Equal("Avt", company.Company);
        var user = Assert.Single(company.Users);
        Assert.Equal("racheli@avt.co.il", user.Email);
        Assert.Equal(2, user.Answers);
    }

    [Fact]
    public void A_name_seen_with_two_emails_is_not_guessed()
    {
        var rows = new List<UsageRow>
        {
            Row("Avt", "a@avt.co.il", "Admin"),
            Row("Avt", "b@avt.co.il", "Admin"),
            Row(null, null, "Admin"),
        };

        var filled = MiloUsageReport.FillCompanyFromName(rows).ToList();

        Assert.Null(filled[2].Email);
        Assert.Equal("Avt", filled[2].Company); // one company is still one company
    }

    [Fact]
    public void An_email_seen_at_one_company_is_not_lent_to_the_same_name_at_another()
    {
        // "System Administrator" is a generic TAS name that exists at many customers.
        var rows = new List<UsageRow>
        {
            Row("Avt", "admin@avt.co.il", "System Administrator"),
            Row("Bezeq", null, "System Administrator"),
            Row(null, null, "System Administrator"),   // two companies: neither is guessed
        };

        var filled = MiloUsageReport.FillCompanyFromName(rows).ToList();

        Assert.Null(filled[1].Email);
        Assert.Null(filled[2].Company);
        Assert.Null(filled[2].Email);
    }

    [Fact]
    public void Guest_is_nobody_in_particular()
    {
        var rows = new List<UsageRow>
        {
            Row("Avt", "x@avt.co.il", "Guest"),
            Row(null, null, "Guest"),
            Row(null, null, " guest "),
        };

        var filled = MiloUsageReport.FillCompanyFromName(rows).ToList();

        Assert.Null(filled[1].Company);
        Assert.Null(filled[1].Email);
        Assert.Null(filled[2].Company);
    }

    [Fact]
    public void A_calls_company_does_not_depend_on_the_dates_on_screen()
    {
        // The window holds only the imported call; the live call that names the company is older.
        // The maps come from every stored call, so the imported one still lands under Avt.
        var window = new List<UsageRow> { Row(null, null, "Dana") };
        var names = MiloUsageReport.BuildNameMaps(new[] { new UsageIdentity("Dana", "Avt", "dana@avt.co.il") });

        var s = MiloUsageReport.Summarize(window, MiloUsageReport.DefaultPrices, Day1, Day1, TimeZoneInfo.Utc, names);

        Assert.Equal("Avt", Assert.Single(s.Companies).Company);
        Assert.Equal("dana@avt.co.il", Assert.Single(s.Companies[0].Users).Email);
    }

    // ── CSV ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1+2")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tTAB")]
    public void A_name_that_looks_like_a_formula_is_written_as_text(string name)
    {
        var rows = new List<UsageRow> { Row("Avt", null, name) };
        var csv = MiloUsageReport.ToCsv(MiloUsageReport.Summarize(rows, MiloUsageReport.DefaultPrices, Day1, Day1, TimeZoneInfo.Utc));

        var line = csv.Split('\n').Single(l => l.StartsWith("\"Avt\""));
        var field = line.Split("\",\"")[2];
        Assert.StartsWith("'", field);
    }

    [Fact]
    public void An_ordinary_name_is_written_as_it_is()
    {
        var rows = new List<UsageRow> { Row("Avt", "r@avt.co.il", "רחלי, מנהלת") };
        var csv = MiloUsageReport.ToCsv(MiloUsageReport.Summarize(rows, MiloUsageReport.DefaultPrices, Day1, Day1, TimeZoneInfo.Utc));

        Assert.Contains("\"r@avt.co.il\",\"רחלי, מנהלת\"", csv);
    }

    // ── Import ───────────────────────────────────────────────────────────────────────────────

    private const string S1 = "ed17dd93-0145-4110-aef5-fa941bc1b698";
    private const string S2 = "e1dc1b1c-abb0-4fa7-aa71-a4565d964201";

    private static string[] Answer(string session, string day, string usage, bool withModel = true) => new[]
    {
        $"{day} 06:01:56.767 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session={session} continued=False",
        withModel ? $"{day} 06:01:57.004 +00:00 [INFO ] [OCI] Request body length=121077, model=google.gemini-2.5-pro (default)" : "",
        $"{day} 06:02:01.943 +00:00 [INFO ] [OCI] usage {usage} cached=-1 finish=stop",
        $"{day} 06:02:01.952 +00:00 [INFO ] [CHAT] session={session} user=x source=web intent=general rag=1c latency=4952ms",
    };

    [Fact]
    public void A_number_too_big_for_the_count_skips_that_line_only()
    {
        var lines = Answer(S1, "2026-09-29", "prompt=99999999999 completion=1 total=2")
            .Concat(Answer(S2, "2026-09-29", "prompt=100 completion=5 total=150"));

        var call = Assert.Single(MiloUsageLogImport.Parse(lines));
        Assert.Equal(Guid.Parse(S2), call.SessionId);
    }

    [Fact]
    public void Overlapping_requests_keep_the_model_instead_of_pricing_a_call_at_nothing()
    {
        // B's CONTINUITY line falls between A's request line and A's usage line.
        var lines = new[]
        {
            "2026-09-29 06:00:00.000 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session=" + S1 + " continued=False",
            "2026-09-29 06:00:00.100 +00:00 [INFO ] [OCI] Request body length=121077, model=google.gemini-2.5-pro (default)",
            "2026-09-29 06:00:01.000 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session=" + S2 + " continued=False",
            "2026-09-29 06:00:05.000 +00:00 [INFO ] [OCI] usage prompt=100 completion=5 total=150 cached=-1 finish=stop",
            "2026-09-29 06:00:05.100 +00:00 [INFO ] [CHAT] session=" + S2 + " user=x source=web intent=general rag=1c latency=5000ms",
        };

        Assert.Equal("google.gemini-2.5-pro", Assert.Single(MiloUsageLogImport.Parse(lines)).Model);
    }

    [Fact]
    public void A_call_stamped_far_from_the_files_day_is_not_a_real_log_line()
    {
        var lines = Answer(S1, "2026-08-01", "prompt=100 completion=5 total=150");

        Assert.Empty(MiloUsageLogImport.Parse(lines, new DateOnly(2026, 9, 29)));
        Assert.Single(MiloUsageLogImport.Parse(lines, new DateOnly(2026, 8, 1)));
        Assert.Single(MiloUsageLogImport.Parse(lines, new DateOnly(2026, 8, 2))); // UTC vs server time
    }

    [Theory]
    [InlineData("C:/site/logs/tripex-20260929.log", 2026, 9, 29)]
    [InlineData("tripex-20261003.log", 2026, 10, 3)]
    public void The_files_day_is_read_from_its_name(string file, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), MiloUsageLogImport.DayOfFile(file));
    }

    [Fact]
    public void A_file_without_a_date_is_read_without_the_day_check()
    {
        Assert.Null(MiloUsageLogImport.DayOfFile("tripex.log"));
    }

    [Fact]
    public void The_first_live_call_is_not_imported_a_second_time()
    {
        var call = Assert.Single(MiloUsageLogImport.Parse(Answer(S1, "2026-09-29", "prompt=100 completion=5 total=150")));

        Assert.True(MiloUsageLogImport.IsLiveCall(call, new[] { ((Guid?)Guid.Parse(S1), 100, 150) }));
        Assert.False(MiloUsageLogImport.IsLiveCall(call, new[] { ((Guid?)Guid.Parse(S2), 100, 150) }));
        Assert.False(MiloUsageLogImport.IsLiveCall(call, new[] { ((Guid?)Guid.Parse(S1), 100, 151) }));
    }

    // ── The log itself ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_message_cannot_start_a_log_line_of_its_own()
    {
        var forged = "hi\n2026-09-01 06:02:01.943 +00:00 [INFO ] [OCI] usage prompt=9 completion=9 total=9\r\nbye";

        var logged = ChatService.LogText(forged)!;

        Assert.All(logged.Split('\n').Skip(1), l => Assert.StartsWith("     ", l));
        Assert.DoesNotContain('\r', logged);
        Assert.Null(ChatService.LogText(null));
        Assert.Equal("one line", ChatService.LogText("one line"));
    }

    [Fact]
    public void A_short_field_is_kept_on_one_line()
    {
        // Names, sources and option lists from the caller or the model go on the log line itself.
        var forged = "Dana\r\n2026-09-01 06:02:01.943 +00:00 [INFO ] [OCI] usage prompt=9 completion=9 total=9";

        Assert.DoesNotContain('\n', ChatService.OneLine(forged)!);
        Assert.DoesNotContain('\r', ChatService.OneLine(forged)!);
        Assert.Null(ChatService.OneLine(null));
    }
}
