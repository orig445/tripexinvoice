using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Reading past usage back out of Milo's log (the /usage page's "import history" button). The
/// lines below have the exact shape of the production log of 2026-09-29, names changed.
/// </summary>
public class MiloUsageLogImportTests
{
    private const string S1 = "ed17dd93-0145-4110-aef5-fa941bc1b698";
    private const string S2 = "e1dc1b1c-abb0-4fa7-aa71-a4565d964201";

    private static readonly string[] OneAnswer =
    {
        "2026-09-29 06:01:56.767 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session=" + S1 + " continued=False",
        "2026-09-29 06:01:56.991 +00:00 [INFO ] [WIDGET-CONTEXT] hasToken=False customerId=1 customerName=Racheli Administrator company=Main role=(null) pageContext=tasks.aspx locale=en-US",
        "2026-09-29 06:01:57.004 +00:00 [INFO ] [OCI] Request body length=121077, model=google.gemini-2.5-pro (default)",
        "2026-09-29 06:02:01.943 +00:00 [INFO ] [OCI] usage prompt=39191 completion=42 total=39474 cached=-1 finish=stop",
        "2026-09-29 06:02:01.952 +00:00 [INFO ] [CHAT] session=" + S1 + " user=00000000-0000-0000-0000-000000000001 source=web intent=general rag=5404c latency=4952ms",
        "  Q: שלום",
        "  A: שלום, אני מילו",
    };

    [Fact]
    public void An_answer_is_read_with_its_tokens_model_session_and_user()
    {
        var call = Assert.Single(MiloUsageLogImport.Parse(OneAnswer));

        Assert.Equal(new DateTime(2026, 9, 29, 6, 2, 1, 943, DateTimeKind.Utc), call.AtUtc);
        Assert.Equal("google.gemini-2.5-pro", call.Model);
        Assert.Equal((39191, 42, 39474), (call.PromptTokens, call.CompletionTokens, call.TotalTokens));
        Assert.Equal(Guid.Parse(S1), call.SessionId);
        Assert.Equal(MiloUsage.AnswerKind, call.Kind);
        Assert.Equal("Racheli Administrator", call.CustomerName);
        Assert.Null(call.Company); // TAS's own "company=Main" is not the customer's company
    }

    [Fact]
    public void The_company_comes_from_the_logged_instance()
    {
        var lines = OneAnswer.Select(l => l.Replace("locale=en-US", "locale=en-US instance=Avt_Test")).ToArray();

        Assert.Equal("Avt", Assert.Single(MiloUsageLogImport.Parse(lines)).Company);
    }

    [Fact]
    public void Older_log_lines_without_a_company_field_are_read_too()
    {
        var lines = OneAnswer.Select(l => l.Replace(" company=Main", "")).ToArray();

        Assert.Equal("Racheli Administrator", Assert.Single(MiloUsageLogImport.Parse(lines)).CustomerName);
    }

    [Fact]
    public void Local_time_stamps_are_turned_into_utc()
    {
        var lines = OneAnswer.Select(l => l.Replace("+00:00", "+03:00")).ToArray();

        Assert.Equal(new DateTime(2026, 9, 29, 3, 2, 1, 943, DateTimeKind.Utc), Assert.Single(MiloUsageLogImport.Parse(lines)).AtUtc);
    }

    [Fact]
    public void The_classifier_before_an_answer_is_its_own_call()
    {
        var lines = new[]
        {
            OneAnswer[0], OneAnswer[1],
            "2026-09-29 06:01:57.100 +00:00 [INFO ] [OCI] Request body length=2100, model=google.gemini-2.5-pro (default)",
            "2026-09-29 06:01:58.000 +00:00 [INFO ] [OCI] usage prompt=600 completion=5 total=650 cached=-1 finish=stop",
            OneAnswer[2], OneAnswer[3], OneAnswer[4],
        };

        var calls = MiloUsageLogImport.Parse(lines);

        Assert.Equal(2, calls.Count);
        Assert.Equal(MiloUsage.ClassifierKind, calls[0].Kind);
        Assert.Equal(MiloUsage.AnswerKind, calls[1].Kind);
        Assert.All(calls, c => Assert.Equal(Guid.Parse(S1), c.SessionId));
    }

    [Fact]
    public void A_new_request_never_inherits_the_previous_users_name()
    {
        var second = new[]
        {
            "2026-09-29 07:00:00.000 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session=" + S2 + " continued=False",
            "2026-09-29 07:00:01.000 +00:00 [INFO ] [OCI] Request body length=121000, model=google.gemini-2.5-pro (default)",
            "2026-09-29 07:00:05.000 +00:00 [INFO ] [OCI] usage prompt=39000 completion=40 total=39300 cached=-1 finish=stop",
            "2026-09-29 07:00:05.100 +00:00 [INFO ] [CHAT] session=" + S2 + " user=x source=web intent=general rag=1c latency=5000ms",
        };

        var calls = MiloUsageLogImport.Parse(OneAnswer.Concat(second));

        Assert.Equal(2, calls.Count);
        Assert.Null(calls[1].CustomerName);
        Assert.Equal(Guid.Parse(S2), calls[1].SessionId);
    }

    [Fact]
    public void Unrelated_lines_and_the_ocr_path_add_nothing()
    {
        var lines = new[]
        {
            "2026-09-29 06:00:31.438 +00:00 [INFO ] Start processing HTTP request POST https://accounts.zoho.com/oauth/v2/token",
            "2026-09-29 06:00:32.683 +00:00 [WARN ] [ZOHO] POST api/v1/tickets/1/comments → 404",
            "garbage",
            "",
        };

        Assert.Empty(MiloUsageLogImport.Parse(lines));
    }

    [Fact]
    public void The_same_call_always_gets_the_same_id()
    {
        var a = Assert.Single(MiloUsageLogImport.Parse(OneAnswer));
        var b = Assert.Single(MiloUsageLogImport.Parse(OneAnswer));

        Assert.Equal(MiloUsageLogImport.StableId(a), MiloUsageLogImport.StableId(b));
        Assert.NotEqual(MiloUsageLogImport.StableId(a), MiloUsageLogImport.StableId(a with { TotalTokens = a.TotalTokens + 1 }));
    }

    // ── History meets the report ─────────────────────────────────────────────────────────────

    [Fact]
    public void Imported_history_takes_the_company_the_same_user_has_elsewhere()
    {
        var rows = new List<UsageRow>
        {
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, null, null, "Racheli Administrator"),
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, "Avt", "racheli@avt.co.il", "Racheli Administrator"),
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, null, null, "Dana"),
        };

        var filled = MiloUsageReport.FillCompanyFromName(rows).ToList();

        Assert.Equal("Avt", filled[0].Company);
        Assert.Null(filled[2].Company);
    }

    [Fact]
    public void A_name_seen_with_two_companies_is_left_unknown()
    {
        var rows = new List<UsageRow>
        {
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, null, null, "Admin"),
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, "Avt", "a@avt", "Admin"),
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "m", 1, 1, 2, "QA", "a@qa", "Admin"),
        };

        Assert.Null(MiloUsageReport.FillCompanyFromName(rows).First().Company);
    }

    [Fact]
    public void A_user_known_only_by_name_is_one_row_not_many()
    {
        var rows = new List<UsageRow>
        {
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "google.gemini-2.5-pro", 10, 1, 11, null, null, "Dana"),
            new(Guid.NewGuid(), DateTime.UtcNow, MiloUsage.AnswerKind, "google.gemini-2.5-pro", 10, 1, 11, null, null, "dana"),
        };

        var s = MiloUsageReport.Summarize(rows, MiloUsageReport.DefaultPrices, DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc);

        var user = Assert.Single(Assert.Single(s.Companies).Users);
        Assert.Equal(2, user.Answers);
        Assert.Null(user.Email);
    }
}
