using System.Text.Json;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// שירות אורקסטרציה לסנכרון NetSuite ↔ רשות המיסים
///
/// Flow:
///   1. שליפת הוצאות מ-NetSuite (Vendor Bills)
///   2. סינון הוצאות מעל הסף (ברירת מחדל: 5,000 ₪)
///   3. לכל הוצאה שעדיין לא קיבלה מספר הקצאה → שליחה לרשות המיסים
///   4. שמירת מספר ההקצאה חזרה ב-NetSuite + ב-DB המקומי
///   5. רישום לוג סנכרון מלא
/// </summary>
public class IntegrationSyncService
{
    private readonly NetSuiteService _netSuite;
    private readonly TaxAuthorityService _taxAuthority;
    private readonly TripExDbContext _db;

    public IntegrationSyncService(
        NetSuiteService netSuite,
        TaxAuthorityService taxAuthority,
        TripExDbContext db)
    {
        _netSuite = netSuite;
        _taxAuthority = taxAuthority;
        _db = db;
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// הפעלת סנכרון — שולף הוצאות מ-NetSuite ושולח לרשות המיסים
    /// Runs the full sync: fetch from NetSuite → request allocation → store results
    /// </summary>
    public async Task<SyncResult> RunSyncAsync(TriggerSyncRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var syncId = Guid.NewGuid();

        var result = new SyncResult
        {
            SyncId = syncId,
            IsDryRun = request.DryRun,
            StartedAt = DateTime.UtcNow
        };

        // Load configurations
        var nsConfig = await GetNetSuiteConfigAsync();
        var taConfig = await GetTaxAuthorityConfigAsync();

        if (nsConfig == null || !nsConfig.IsEnabled)
        {
            result.Success = false;
            result.Error = "חיבור NetSuite לא מוגדר או לא מופעל";
            await SaveSyncLogAsync(syncId, result, sw.ElapsedMilliseconds);
            return result;
        }

        var threshold = request.AmountThresholdOverride ?? nsConfig.AmountThreshold;

        try
        {
            // ── Step 1: Fetch expenses from NetSuite ──────────────────────
            Console.WriteLine($"[SYNC] {syncId} — Fetching expenses > {threshold} from NetSuite...");

            var expenses = await _netSuite.FetchExpensesAboveThresholdAsync(
                nsConfig,
                threshold,
                request.DateFrom,
                request.DateTo);

            result.TotalExpensesFetched = expenses.Count;
            Console.WriteLine($"[SYNC] {syncId} — Found {expenses.Count} expenses");

            // ── Step 2: Filter and process each expense ───────────────────
            foreach (var expense in expenses)
            {
                var detail = new SyncExpenseDetail
                {
                    NetSuiteId = expense.InternalId,
                    TranId = expense.TranId,
                    Amount = expense.Amount,
                    VendorName = expense.VendorName,
                };

                // Check if already processed
                var existing = await FindExistingAllocationAsync(expense.InternalId);
                if (existing != null)
                {
                    detail.Status = "already_processed";
                    detail.AllocationNumber = existing;
                    detail.Message = $"מספר הקצאה קיים: {existing}";
                    result.AlreadyProcessed++;
                    result.Details.Add(detail);
                    continue;
                }

                result.ExpensesAboveThreshold++;

                // DryRun — just report, don't request
                if (request.DryRun)
                {
                    detail.Status = "fetched";
                    detail.Message = $"[DryRun] יישלח לרשות המיסים: {expense.Amount:N2} {expense.Currency}";
                    result.Details.Add(detail);
                    continue;
                }

                // ── Step 3: Request allocation number ─────────────────────
                if (taConfig == null || !taConfig.IsEnabled)
                {
                    detail.Status = "skipped";
                    detail.Message = "רשות המיסים לא מוגדרת — דלג על בקשת הקצאה";
                    result.Details.Add(detail);
                    continue;
                }

                result.AllocationRequested++;
                var allocationReq = BuildAllocationRequest(expense, taConfig);
                var allocation = await _taxAuthority.RequestAllocationNumberAsync(taConfig, allocationReq);

                if (allocation.Success && !string.IsNullOrWhiteSpace(allocation.AllocationNumber))
                {
                    detail.Status = "approved";
                    detail.AllocationNumber = allocation.AllocationNumber;
                    detail.Message = $"מספר הקצאה התקבל: {allocation.AllocationNumber}";
                    result.AllocationApproved++;

                    // ── Step 4: Save back to NetSuite ─────────────────────
                    await _netSuite.UpdateAllocationNumberAsync(
                        nsConfig,
                        expense.InternalId,
                        allocation.AllocationNumber,
                        "approved");

                    // Save to local DB
                    await SaveAllocationAsync(expense, allocation.AllocationNumber, "approved");

                    Console.WriteLine($"[SYNC] {syncId} — Expense {expense.TranId}: " +
                                      $"Allocation {allocation.AllocationNumber} ✓");
                }
                else
                {
                    detail.Status = "failed";
                    detail.Message = allocation.Error ?? "הקצאה נכשלה ללא פרטים";
                    result.AllocationFailed++;

                    // Save failure
                    await SaveAllocationAsync(expense, null, "failed", allocation.Error);

                    Console.Error.WriteLine($"[SYNC] {syncId} — Expense {expense.TranId}: " +
                                            $"Allocation FAILED — {allocation.Error}");
                }

                result.Details.Add(detail);
            }

            result.Success = result.AllocationFailed == 0;
            result.CompletedAt = DateTime.UtcNow;
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;

            Console.WriteLine($"[SYNC] {syncId} — Completed in {sw.ElapsedMilliseconds}ms. " +
                              $"Approved: {result.AllocationApproved}, Failed: {result.AllocationFailed}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.Error = ex.Message;
            result.CompletedAt = DateTime.UtcNow;
            result.DurationMs = sw.ElapsedMilliseconds;
            Console.Error.WriteLine($"[SYNC] {syncId} — Fatal error: {ex.Message}");
        }

        await SaveSyncLogAsync(syncId, result, sw.ElapsedMilliseconds);

        // Update last sync time in NetSuite config
        await UpdateLastSyncAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Config loading
    // ─────────────────────────────────────────────────────────────────

    public async Task<NetSuiteConfigDto?> GetNetSuiteConfigAsync()
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        var entity = _db.NetSuiteConfigs.FirstOrDefault();
        if (entity == null) return null;

        return new NetSuiteConfigDto
        {
            Id = entity.Id,
            AccountId = entity.AccountId,
            ConsumerKey = entity.ConsumerKey,
            ConsumerSecret = entity.ConsumerSecret,
            TokenKey = entity.TokenKey,
            TokenSecret = entity.TokenSecret,
            AmountThreshold = entity.AmountThreshold,
            Currency = entity.Currency,
            IsEnabled = entity.IsEnabled,
            AutoSync = entity.AutoSync,
            AutoSyncIntervalMinutes = entity.AutoSyncIntervalMinutes,
            LastSyncAt = entity.LastSyncAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    public async Task<TaxAuthorityConfigDto?> GetTaxAuthorityConfigAsync()
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        var entity = _db.TaxAuthorityConfigs.FirstOrDefault();
        if (entity == null) return null;

        return new TaxAuthorityConfigDto
        {
            Id = entity.Id,
            ApiBaseUrl = entity.ApiBaseUrl,
            ClientId = entity.ClientId,
            ClientSecret = entity.ClientSecret,
            Username = entity.Username,
            BusinessId = entity.BusinessId,
            IsEnabled = entity.IsEnabled,
            UseSandbox = entity.UseSandbox,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    public async Task<NetSuiteConfigDto> SaveNetSuiteConfigAsync(SaveNetSuiteConfigRequest req)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        var entity = _db.NetSuiteConfigs.FirstOrDefault();
        if (entity == null)
        {
            entity = new NetSuiteConfigEntity { Id = Guid.NewGuid() };
            _db.NetSuiteConfigs.Add(entity);
        }

        entity.AccountId = req.AccountId;
        entity.ConsumerKey = req.ConsumerKey;
        entity.ConsumerSecret = req.ConsumerSecret;
        entity.TokenKey = req.TokenKey;
        entity.TokenSecret = req.TokenSecret;
        entity.AmountThreshold = req.AmountThreshold;
        entity.Currency = req.Currency;
        entity.IsEnabled = req.IsEnabled;
        entity.AutoSync = req.AutoSync;
        entity.AutoSyncIntervalMinutes = req.AutoSyncIntervalMinutes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return await GetNetSuiteConfigAsync() ?? new NetSuiteConfigDto();
    }

    public async Task<TaxAuthorityConfigDto> SaveTaxAuthorityConfigAsync(SaveTaxAuthorityConfigRequest req)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        var entity = _db.TaxAuthorityConfigs.FirstOrDefault();
        if (entity == null)
        {
            entity = new TaxAuthorityConfigEntity { Id = Guid.NewGuid() };
            _db.TaxAuthorityConfigs.Add(entity);
        }

        entity.ApiBaseUrl = req.ApiBaseUrl;
        entity.ClientId = req.ClientId;
        entity.ClientSecret = req.ClientSecret;
        entity.Username = req.Username;
        entity.BusinessId = req.BusinessId;
        entity.IsEnabled = req.IsEnabled;
        entity.UseSandbox = req.UseSandbox;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return await GetTaxAuthorityConfigAsync() ?? new TaxAuthorityConfigDto();
    }

    public async Task<List<SyncHistoryEntry>> GetSyncHistoryAsync(int limit = 50)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        return _db.IntegrationSyncLogs
            .OrderByDescending(l => l.StartedAt)
            .Take(limit)
            .Select(l => new SyncHistoryEntry
            {
                Id = l.Id,
                Status = l.Status,
                IsDryRun = l.IsDryRun,
                TotalFetched = l.TotalFetched,
                AboveThreshold = l.AboveThreshold,
                Approved = l.Approved,
                Failed = l.Failed,
                ErrorMessage = l.ErrorMessage,
                StartedAt = l.StartedAt,
                CompletedAt = l.CompletedAt,
                DurationMs = l.DurationMs
            })
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<string?> FindExistingAllocationAsync(string netSuiteId)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        var record = _db.AllocationRecords
            .Where(a => a.NetSuiteTransactionId == netSuiteId && a.Status == "approved")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        return record?.AllocationNumber;
    }

    private async Task SaveAllocationAsync(
        NetSuiteExpense expense,
        string? allocationNumber,
        string status,
        string? errorMessage = null)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        _db.AllocationRecords.Add(new AllocationRecordEntity
        {
            Id = Guid.NewGuid(),
            NetSuiteTransactionId = expense.InternalId,
            TranId = expense.TranId,
            VendorId = expense.VendorId,
            VendorName = expense.VendorName,
            Amount = expense.Amount,
            Currency = expense.Currency,
            InvoiceDate = expense.TranDate,
            AllocationNumber = allocationNumber,
            Status = status,
            ErrorMessage = errorMessage,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
    }

    private async Task SaveSyncLogAsync(Guid syncId, SyncResult result, long durationMs)
    {
        await SchemaGuard.EnsureIntegrationTablesAsync(_db);

        _db.IntegrationSyncLogs.Add(new IntegrationSyncLogEntity
        {
            Id = syncId,
            Status = result.Success ? "success" : (result.AllocationApproved > 0 ? "partial" : "failed"),
            IsDryRun = result.IsDryRun,
            TotalFetched = result.TotalExpensesFetched,
            AboveThreshold = result.ExpensesAboveThreshold,
            Approved = result.AllocationApproved,
            Failed = result.AllocationFailed,
            ErrorMessage = result.Error,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt ?? DateTime.UtcNow,
            DurationMs = durationMs,
            DetailsJson = JsonSerializer.Serialize(result.Details)
        });

        await _db.SaveChangesAsync();
    }

    private async Task UpdateLastSyncAsync()
    {
        var entity = _db.NetSuiteConfigs.FirstOrDefault();
        if (entity != null)
        {
            entity.LastSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    private static AllocationRequest BuildAllocationRequest(
        NetSuiteExpense expense,
        TaxAuthorityConfigDto taConfig)
    {
        var amountBeforeTax = expense.Amount - expense.TaxAmount;
        return new AllocationRequest
        {
            SupplierId = expense.VendorId ?? "",
            SupplierName = expense.VendorName ?? "",
            CustomerId = taConfig.BusinessId,
            InvoiceNumber = expense.TranId,
            InvoiceDate = expense.TranDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            TotalAmount = expense.Amount,
            TaxAmount = expense.TaxAmount,
            AmountBeforeTax = amountBeforeTax > 0 ? amountBeforeTax : expense.Amount,
            Currency = expense.Currency,
            DocumentType = "חשבונית מס",
            SourceSystem = "NetSuite",
            SourceTransactionId = expense.InternalId
        };
    }
}
