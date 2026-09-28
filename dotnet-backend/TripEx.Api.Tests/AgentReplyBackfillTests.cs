using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The backfill picks threads out of a Desk list on its own judgement and puts what it finds in
/// front of a customer, so the judgement is what is pinned here: how the list is read, which
/// threads count as an agent's public reply, which conversations are looked at, and when the whole
/// thing is switched on. Everything is static and offline — no database is opened, no Desk call is
/// made.
///
/// The response shapes below are Desk's documented ones, not captured traffic; the LogOnly stage
/// exists to confirm them against production before any recovered reply is shown.
/// </summary>
public class AgentReplyBackfillTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A thread that should be relayed, with one field changed per test.</summary>
    private static ZohoDeskService.ThreadSummary Reply(
        string? direction = "out",
        string? visibility = "public",
        bool isDescription = false,
        bool isForward = false,
        string? authorType = "AGENT",
        string? status = "SUCCESS",
        double? minutesOld = 10)
        => new("31138000012000001", direction, visibility, isDescription, isForward, authorType, status, "EMAIL",
            minutesOld == null ? null : Now.AddMinutes(-minutesOld.Value));

    private static ZohoAgentReplyBackfillWorker.BackfillVerdict Verdict(ZohoDeskService.ThreadSummary t)
        => ZohoAgentReplyBackfillWorker.Classify(t, Now);

    // ── Reading the list ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_thread_list_maps_every_field()
    {
        var threads = ZohoDeskService.ParseThreadList("""
            {"data":[
              {"id":"31138000012000001","channel":"EMAIL","direction":"out","visibility":"public",
               "isDescriptionThread":false,"isForward":false,"status":"SUCCESS",
               "createdTime":"2026-09-28T10:15:30.000Z","summary":"Hi, your refund is on its way",
               "author":{"name":"Dana","type":"AGENT"}},
              {"id":"31138000012000002","channel":"WEB","direction":"in","visibility":"private",
               "isDescriptionThread":true,"isForward":true,"status":"DRAFT",
               "createdTime":"2026-09-27T08:00:00.000Z",
               "author":{"name":"Customer","type":"END_USER"}}
            ]}
            """);

        Assert.Equal(2, threads.Count);

        var a = threads[0];
        Assert.Equal("31138000012000001", a.Id);
        Assert.Equal("out", a.Direction);
        Assert.Equal("public", a.Visibility);
        Assert.False(a.IsDescriptionThread);
        Assert.False(a.IsForward);
        Assert.Equal("AGENT", a.AuthorType);
        Assert.Equal("SUCCESS", a.Status);
        Assert.Equal("EMAIL", a.Channel);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 15, 30, DateTimeKind.Utc), a.CreatedTimeUtc);
        Assert.Equal(DateTimeKind.Utc, a.CreatedTimeUtc!.Value.Kind);

        var b = threads[1];
        Assert.Equal("in", b.Direction);
        Assert.Equal("private", b.Visibility);
        Assert.True(b.IsDescriptionThread);
        Assert.True(b.IsForward);
        Assert.Equal("END_USER", b.AuthorType);
        Assert.Equal("DRAFT", b.Status);
        Assert.Equal("WEB", b.Channel);
    }

    [Fact]
    public void A_numeric_id_is_kept_and_a_thread_without_one_is_dropped()
    {
        var threads = ZohoDeskService.ParseThreadList("""
            {"data":[
              {"id":31138000012000003,"direction":"out"},
              {"direction":"out","visibility":"public"},
              {"id":"","direction":"out"},
              {"id":null,"direction":"out"}
            ]}
            """);

        Assert.Equal("31138000012000003", Assert.Single(threads).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]                                   // Desk's 204: an empty list has no body
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":\"x\"}")]
    [InlineData("{\"data\":[{\"id\":\"1\",\"direction\":\"out\"")]   // truncated
    [InlineData("not json at all")]
    [InlineData("{\"data\":[1, \"two\", null, []]}")]
    public void A_list_it_cannot_read_is_empty_rather_than_a_crash(string? body)
    {
        // The sweep runs every ten minutes; a parser that throws on one odd body kills that sweep
        // every time, and no reply is recovered from any ticket until someone notices.
        Assert.Empty(ZohoDeskService.ParseThreadList(body));
    }

    [Fact]
    public void Flags_are_only_true_when_Desk_says_a_literal_true()
    {
        var threads = ZohoDeskService.ParseThreadList("""
            {"data":[
              {"id":"1","isForward":"true","isDescriptionThread":"true"},
              {"id":"2"}
            ]}
            """);

        Assert.All(threads, t =>
        {
            Assert.False(t.IsForward);
            Assert.False(t.IsDescriptionThread);
        });
    }

    [Fact]
    public void An_author_that_is_not_an_object_has_no_type()
    {
        var t = Assert.Single(ZohoDeskService.ParseThreadList("""{"data":[{"id":"1","author":"AGENT"}]}"""));

        Assert.Null(t.AuthorType);
    }

    [Fact]
    public void Fields_of_the_wrong_type_are_read_as_missing_not_thrown_on()
    {
        // JsonElement.GetString throws on a number. A field that changed type must cost the
        // thread, not the sweep.
        var t = Assert.Single(ZohoDeskService.ParseThreadList("""
            {"data":[{"id":"1","direction":5,"visibility":true,"status":[],"channel":{},
                      "createdTime":1727518530000,"author":{"type":7}}]}
            """));

        Assert.Null(t.Direction);
        Assert.Null(t.Visibility);
        Assert.Null(t.Status);
        Assert.Null(t.Channel);
        Assert.Null(t.CreatedTimeUtc);
        Assert.Null(t.AuthorType);
    }

    [Fact]
    public void An_unparseable_time_is_no_time()
    {
        var t = Assert.Single(ZohoDeskService.ParseThreadList("""{"data":[{"id":"1","createdTime":"yesterday-ish"}]}"""));

        Assert.Null(t.CreatedTimeUtc);
    }

    // ── Which threads count ──────────────────────────────────────────────────────────────────

    [Fact]
    public void An_agents_public_reply_ten_minutes_old_is_eligible()
    {
        Assert.Equal(ZohoAgentReplyBackfillWorker.BackfillVerdict.Eligible, Verdict(Reply()));
    }

    [Fact]
    public void The_words_are_compared_case_blind()
    {
        Assert.Equal(ZohoAgentReplyBackfillWorker.BackfillVerdict.Eligible,
            Verdict(Reply(direction: "OUT", visibility: "Public", authorType: "agent")));
    }

    [Fact]
    public void An_internal_note_by_an_agent_is_never_eligible()
    {
        // The single most damaging thing this could do, and the mirror of the webhook's own
        // An_internal_note_never_reaches_the_customer: agents write notes to each other on the same
        // ticket, outgoing and by an AGENT, believing the customer cannot see them.
        Assert.Equal(ZohoAgentReplyBackfillWorker.BackfillVerdict.NotPublic,
            Verdict(Reply(direction: "out", visibility: "private", authorType: "AGENT")));
    }

    public static IEnumerable<object[]> Rejections() => new[]
    {
        new object[] { Reply(direction: "in"), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotOutgoing },
        new object[] { Reply(direction: null), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotOutgoing },
        new object[] { Reply(visibility: "private"), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotPublic },
        new object[] { Reply(visibility: null), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotPublic },
        new object[] { Reply(isDescription: true), ZohoAgentReplyBackfillWorker.BackfillVerdict.Description },
        new object[] { Reply(isForward: true), ZohoAgentReplyBackfillWorker.BackfillVerdict.Forward },
        new object[] { Reply(authorType: "END_USER"), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotAgent },
        new object[] { Reply(authorType: null), ZohoAgentReplyBackfillWorker.BackfillVerdict.NotAgent },
        new object[] { Reply(status: "DRAFT"), ZohoAgentReplyBackfillWorker.BackfillVerdict.Draft },
        new object[] { Reply(minutesOld: null), ZohoAgentReplyBackfillWorker.BackfillVerdict.NoTime },
        new object[] { Reply(minutesOld: 2), ZohoAgentReplyBackfillWorker.BackfillVerdict.TooNew },
        new object[] { Reply(minutesOld: 25 * 60), ZohoAgentReplyBackfillWorker.BackfillVerdict.TooOld },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Anything_that_is_not_plainly_an_agents_public_reply_is_skipped(
        ZohoDeskService.ThreadSummary thread, ZohoAgentReplyBackfillWorker.BackfillVerdict expected)
    {
        // Every rule fails closed: a missing direction, visibility, author type or time is a reason
        // to skip. The webhook lets a missing direction or visibility through to its re-read; this
        // chooses threads from a list by itself, so it is the stricter of the two.
        Assert.Equal(expected, Verdict(thread));
    }

    [Fact]
    public void A_bot_or_auto_acknowledgement_is_not_an_agent()
    {
        // Outgoing and public, like a real reply — only the author type tells them apart.
        Assert.Equal(ZohoAgentReplyBackfillWorker.BackfillVerdict.NotAgent, Verdict(Reply(authorType: "SYSTEM")));
        Assert.Equal(ZohoAgentReplyBackfillWorker.BackfillVerdict.NotAgent, Verdict(Reply(authorType: "BOT")));
    }

    // ── Which conversations are looked at ────────────────────────────────────────────────────

    [Fact]
    public void The_candidate_query_translates_and_keeps_its_filters()
    {
        // Same approach as HandoverQueryTranslationTests: ToQueryString builds the SQL without
        // opening the connection, so an untranslatable query fails here instead of every sweep.
        using var db = new TripExDbContext(new DbContextOptionsBuilder<TripExDbContext>()
            .UseSqlServer("Server=localhost;Database=never-opened;Trusted_Connection=True")
            .Options);

        var sql = ZohoAgentReplyBackfillWorker.CandidateQuery(db, DateTime.UtcNow).Take(25).ToQueryString();

        Assert.Contains("N'internal'", sql);    // staff chat is never swept
        // The human-involved test: escalated, or an agent row as a subquery. Without it the sweep
        // would look at conversations Milo answers alone, and a recovered row would silence Milo.
        Assert.Contains("[escalated]", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains("N'agent'", sql);
        Assert.Contains("DESC", sql);           // newest activity first when the cap bites
    }

    // ── The switch ───────────────────────────────────────────────────────────────────────────

    private static ZohoDeskOptions RelayOn() => new()
    {
        Enabled = true,
        ClientId = "id", ClientSecret = "secret", RefreshToken = "refresh",
        OrgId = "1", DepartmentId = "2", FallbackContactEmail = "milo@tripex.io",
        AgentRelayEnabled = true,
        WebhookSecret = "6f1c2b9e4a7d8350bc1e2f4a9d77",
    };

    [Fact]
    public void The_backfill_is_off_by_default()
    {
        Assert.Equal(AgentReplyBackfillMode.Off, new ZohoDeskOptions().BackfillMode);

        // Relay fully on, key never written: still off. Turning the relay on is not a decision to
        // start sweeping Desk.
        var o = RelayOn();
        Assert.True(o.IsRelayConfigured);
        Assert.Equal(AgentReplyBackfillMode.Off, o.BackfillMode);
        Assert.False(o.IsBackfillActive);
    }

    [Theory]
    [InlineData("On", AgentReplyBackfillMode.On)]
    [InlineData(" on ", AgentReplyBackfillMode.On)]
    [InlineData("LogOnly", AgentReplyBackfillMode.LogOnly)]
    [InlineData("logonly", AgentReplyBackfillMode.LogOnly)]
    [InlineData("log", AgentReplyBackfillMode.LogOnly)]
    [InlineData("Off", AgentReplyBackfillMode.Off)]
    [InlineData("banana", AgentReplyBackfillMode.Off)]
    [InlineData("true", AgentReplyBackfillMode.Off)]
    [InlineData("", AgentReplyBackfillMode.Off)]
    [InlineData(null, AgentReplyBackfillMode.Off)]
    public void Only_the_words_themselves_turn_it_on(string? value, AgentReplyBackfillMode expected)
    {
        Assert.Equal(expected, ZohoDeskOptions.ParseBackfillMode(value));
    }

    [Fact]
    public void It_never_runs_without_the_relay()
    {
        // AgentRelayEnabled=false has to stay the one rollback for agent text reaching customers.
        var o = RelayOn();
        o.AgentReplyBackfill = "On";
        Assert.True(o.IsBackfillActive);

        o.AgentRelayEnabled = false;
        Assert.False(o.IsRelayConfigured);
        Assert.False(o.IsBackfillActive);

        o = RelayOn();
        o.AgentReplyBackfill = "On";
        o.WebhookSecret = "too-short";
        Assert.False(o.IsBackfillActive);

        o = RelayOn();
        o.AgentReplyBackfill = "LogOnly";
        o.Enabled = false;
        Assert.False(o.IsBackfillActive);
    }

    [Fact]
    public void LogOnly_counts_as_active()
    {
        // LogOnly still runs the sweep — that is what it is for — it just stores nothing.
        var o = RelayOn();
        o.AgentReplyBackfill = "LogOnly";

        Assert.True(o.IsBackfillActive);
        Assert.Equal(AgentReplyBackfillMode.LogOnly, o.BackfillMode);
    }
}
