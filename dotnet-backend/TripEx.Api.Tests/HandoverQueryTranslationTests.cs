using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// A LINQ query EF cannot translate compiles cleanly and throws only when it runs. These two run on
/// the chat path (every message in a continued conversation) and in the background sweep, so a
/// translation failure would surface as Milo quietly answering handed-over customers, or as a
/// sweep that errors every two minutes. ToQueryString produces the SQL without opening a connection,
/// so this needs no database — and, as with every test here, touches nothing on the network.
/// </summary>
public class HandoverQueryTranslationTests
{
    private static TripExDbContext Db() => new(new DbContextOptionsBuilder<TripExDbContext>()
        .UseSqlServer("Server=localhost;Database=never-opened;Trusted_Connection=True")
        .Options);

    [Fact]
    public void The_human_involved_check_translates_to_one_SQL_statement()
    {
        using var db = Db();

        var sql = ChatService.HumanInvolvedQuery(db, Guid.NewGuid()).ToQueryString();

        Assert.Contains("[escalated]", sql);
        Assert.Contains("EXISTS", sql);          // the agent-reply half, as a subquery, not a second round trip
        Assert.Contains("[role]", sql);
    }

    [Fact]
    public void The_recovery_sweep_translates_and_keeps_its_filters()
    {
        using var db = Db();

        var sql = ZohoEscalationRecoveryWorker.StuckQuery(db, DateTime.UtcNow).Take(50).ToQueryString();

        // The source filter is the fix for tickets opened for staff chat — it must reach the SQL.
        Assert.Contains("N'internal'", sql);
        Assert.Contains("N'salesiq'", sql);
        // The handover leg: unsent customer messages written to the agent.
        Assert.Contains("N'handover'", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains("DESC", sql);

        // The handover leg must not sit under the escalation filter: a conversation is also handed
        // over when an agent replied on a ticket Milo never escalated, and the second review found
        // exactly this — escalated=1 ANDed above all three legs, so those were never recovered.
        Assert.DoesNotContain("WHERE [c].[escalated] = CAST(1 AS bit) AND [c].[updated_at]", sql);
    }
}
