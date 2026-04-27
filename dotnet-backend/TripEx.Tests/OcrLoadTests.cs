using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TripEx.Tests;

/// <summary>
/// Load and concurrency tests.
/// Sequential tests exercise the change-tracker fix on a single DbContext scope.
/// Concurrent tests simulate multiple parallel HTTP requests (each with its own scope).
/// </summary>
public class OcrLoadTests
{
    private static readonly string _img = TestHelpers.FakeImageBase64;

    // ── Sequential load on same DbContext ─────────────────────────────────────

    [Fact]
    public async Task Sequential_50Scans_AllReturnSuccess()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        for (int i = 0; i < 50; i++)
        {
            var result = await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());
            Assert.True(result.Success, $"Scan #{i + 1} failed: {result.Error}");
        }
    }

    [Fact]
    public async Task Sequential_50Scans_All50LogsSaved()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        for (int i = 0; i < 50; i++)
            await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());

        var count = await db.InvoiceScanLogs.CountAsync();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task Sequential_50Scans_AllFieldsNonZero()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        for (int i = 0; i < 50; i++)
        {
            var result = await svc.AnalyzeAsync(_img, null, "IL");
            Assert.True(result.Success);
            Assert.NotNull(result.Fields?.Total);
            Assert.NotEqual("0", result.Fields!.Total);
            Assert.NotEqual("0.00", result.Fields.Total);
        }
    }

    [Fact]
    public async Task Sequential_50Scans_ChangeTrackerSizeBounded()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        for (int i = 0; i < 50; i++)
            await svc.AnalyzeAsync(_img, null, "IL");

        // The detach-before-add fix ensures the tracker never accumulates
        var tracked = db.ChangeTracker.Entries()
            .Count(e => e.State != Microsoft.EntityFrameworkCore.EntityState.Detached);

        Assert.True(tracked <= 1,
            $"ChangeTracker has {tracked} entries after 50 scans — expected ≤1 (detach fix broken)");
    }

    // ── Concurrent load (separate DbContext per task — simulates parallel requests) ──

    [Fact]
    public async Task Concurrent_20IndependentScans_AllReturnSuccess()
    {
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            // Each task gets its own service stack — simulates a separate HTTP request scope
            var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
            return await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success, $"A concurrent scan failed: {r.Error}"));
    }

    [Fact]
    public async Task Concurrent_20IndependentScans_AllFieldsNonZero()
    {
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
            return await svc.AnalyzeAsync(_img, null, "IL");
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.True(r.Success);
            Assert.Equal("117.00", r.Fields?.Total);
            Assert.Equal("17.00", r.Fields?.TotalVAT);
        });
    }

    // ── Mixed error/success under load ────────────────────────────────────────

    [Fact]
    public async Task Mixed_SuccessAndErrorScans_SuccessOnesStillWork()
    {
        // Even when some scans fail (bad HTTP), others should still succeed
        var successTasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
            return await svc.AnalyzeAsync(_img, null, "IL");
        });

        var errorTasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            var (svc, _) = TestHelpers.ErrorStack(System.Net.HttpStatusCode.InternalServerError);
            return await svc.AnalyzeAsync(_img, null, "IL");
        });

        var allResults = await Task.WhenAll(successTasks.Concat(errorTasks));

        var successes = allResults.Where(r => r.Success).ToList();
        var failures = allResults.Where(r => !r.Success).ToList();

        Assert.Equal(10, successes.Count);
        Assert.Equal(5, failures.Count);
    }
}
