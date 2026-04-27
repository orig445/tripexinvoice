using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TripEx.Tests;

/// <summary>
/// Regression tests for the "second receipt returns zeros" bug.
/// Root cause: after the first scan, DbContext keeps tracked entities (state=Unchanged).
/// On the second SaveChangesAsync in SaveScanLog a conflict caused a silent failure,
/// which propagated to the outer catch and returned Success=false / zeroed fields.
/// Fix: detach all tracked entities before each SaveScanLog call.
/// </summary>
public class OcrDoubleReceiptTests
{
    private static readonly string _img = TestHelpers.FakeImageBase64;

    [Fact]
    public async Task AnalyzeAsync_TwiceOnSameDbContext_BothReturnSuccess()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        var result1 = await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());
        var result2 = await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());

        Assert.True(result1.Success, $"First scan failed: {result1.Error}");
        Assert.True(result2.Success, $"Second scan failed: {result2.Error}");
    }

    [Fact]
    public async Task AnalyzeAsync_TwiceOnSameDbContext_BothReturnCorrectFields()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        var result1 = await svc.AnalyzeAsync(_img, null, "IL");
        var result2 = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.Equal("117.00", result1.Fields?.Total);
        Assert.Equal("17.00", result1.Fields?.TotalVAT);
        Assert.Equal("117.00", result2.Fields?.Total);
        Assert.Equal("17.00", result2.Fields?.TotalVAT);
    }

    [Fact]
    public async Task AnalyzeAsync_TwiceOnSameDbContext_SavesBothLogs()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());
        await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());

        var logs = await db.InvoiceScanLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("Success", l.Status));
    }

    [Fact]
    public async Task AnalyzeAsync_SecondScan_FieldsAreNotZero()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        // Discard first scan
        await svc.AnalyzeAsync(_img, null, "IL");

        // Second scan — this was the one returning zeros before the fix
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.NotNull(result.Fields?.Total);
        Assert.NotEqual("0", result.Fields!.Total);
        Assert.NotEqual("0.00", result.Fields.Total);
        Assert.NotNull(result.Fields.MerchantName);
    }

    [Fact]
    public async Task AnalyzeAsync_AfterSecondScan_ChangeTrackerHasNoStaleEntries()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);

        await svc.AnalyzeAsync(_img, null, "IL");
        await svc.AnalyzeAsync(_img, null, "IL");

        var tracked = db.ChangeTracker.Entries()
            .Count(e => e.State != Microsoft.EntityFrameworkCore.EntityState.Detached);

        Assert.True(tracked <= 1,
            $"ChangeTracker has {tracked} tracked entries after two scans — expected ≤1");
    }
}
