using Microsoft.EntityFrameworkCore;

namespace TripEx.Api.Data;

/// <summary>
/// Lazy schema guard — ensures specific tables exist before they are queried.
/// 
/// Why this exists:
/// The main DB initialization (init-db.sql) runs in a background Task at startup
/// (see Program.cs) so that IIS doesn't kill the process during cold-start
/// SQL Server connections (>120s timeout). If a request lands on the API before
/// the background init has finished — or if the init silently failed for one
/// specific batch — queries against tables like [OcrTrainingPatterns] or
/// [InvoiceScanLogs] would throw "Invalid object name".
/// 
/// This guard runs idempotent CREATE TABLE IF NOT EXISTS statements lazily,
/// once per table per process lifetime. It is intentionally narrow: it only
/// covers the two tables that produced "Invalid object name" errors in
/// production logs and does NOT change any business logic.
/// </summary>
internal static class SchemaGuard
{
    private static readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task EnsureOcrTrainingPatternsAsync(TripExDbContext db)
    {
        await EnsureAsync(db, "OcrTrainingPatterns", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingPatterns')
CREATE TABLE [dbo].[OcrTrainingPatterns] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [FieldName] NVARCHAR(100) NOT NULL DEFAULT '',
    [PatternRule] NVARCHAR(MAX) NOT NULL DEFAULT '',
    [Country] NVARCHAR(10) NULL,
    [Currency] NVARCHAR(10) NULL,
    [Confidence] FLOAT NOT NULL DEFAULT 0,
    [SourceCount] INT NOT NULL DEFAULT 0,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    [UpdatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);");
    }

    public static async Task EnsureInvoiceScanLogsAsync(TripExDbContext db)
    {
        await EnsureAsync(db, "InvoiceScanLogs", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'InvoiceScanLogs')
CREATE TABLE [dbo].[InvoiceScanLogs] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [RawAiResponse] NVARCHAR(MAX) NULL,
    [CountryHint] NVARCHAR(10) NULL,
    [Status] NVARCHAR(50) NULL,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);
IF COL_LENGTH('dbo.InvoiceScanLogs', 'DurationMs') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [DurationMs] BIGINT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'HttpStatusCode') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [HttpStatusCode] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ErrorMessage') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ErrorMessage] NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ErrorType') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ErrorType] NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'OciResponseBody') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [OciResponseBody] NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageSizeBytes') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageSizeBytes] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'Source') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [Source] NVARCHAR(50) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'AttemptNumber') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [AttemptNumber] INT NULL;");
    }

    private static async Task EnsureAsync(TripExDbContext db, string tableName, string ddl)
    {
        // Fast path — already verified in this process
        if (_ensured.Contains(tableName)) return;

        await _lock.WaitAsync();
        try
        {
            if (_ensured.Contains(tableName)) return;
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl);
                _ensured.Add(tableName);
                Console.WriteLine($"[SCHEMA-GUARD] Ensured table [{tableName}] exists");
            }
            catch (Exception ex)
            {
                // Don't poison the cache — let the next call retry — but don't crash the request either.
                Console.Error.WriteLine($"[SCHEMA-GUARD] Could not ensure [{tableName}]: {ex.Message}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
