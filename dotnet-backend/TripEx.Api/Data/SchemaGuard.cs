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

    public static async Task EnsureOcrTrainingSamplesAsync(TripExDbContext db)
    {
        await EnsureAsync(db, "OcrTrainingSamples", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingSamples')
CREATE TABLE [dbo].[OcrTrainingSamples] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [VendorName] NVARCHAR(255) NULL,
    [Country] NVARCHAR(10) NULL,
    [Currency] NVARCHAR(10) NULL,
    [DocumentType] NVARCHAR(100) NULL,
    [ExtractedFields] NVARCHAR(MAX) NULL,
    [FieldPositions] NVARCHAR(MAX) NULL,
    [Corrections] NVARCHAR(MAX) NULL,
    [ImageUrl] NVARCHAR(2048) NULL,
    [IsVerified] BIT NOT NULL DEFAULT 0,
    [IsRejected] BIT NOT NULL DEFAULT 0,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);
IF COL_LENGTH('dbo.OcrTrainingSamples', 'ImageUrl') IS NULL
    ALTER TABLE [dbo].[OcrTrainingSamples] ADD [ImageUrl] NVARCHAR(2048) NULL;");
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
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [AttemptNumber] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageMimeType') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageMimeType] NVARCHAR(50) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageHash') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageHash] NVARCHAR(64) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageDebugPath') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageDebugPath] NVARCHAR(1024) NULL;");
    }

    /// <summary>
    /// מבטיח שכל טבלאות האינטגרציה קיימות (NetSuite + רשות המיסים)
    /// Ensures all integration tables exist (idempotent)
    /// </summary>
    public static async Task EnsureIntegrationTablesAsync(TripExDbContext db)
    {
        await EnsureAsync(db, "NetSuiteConfigs", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'NetSuiteConfigs')
CREATE TABLE [dbo].[NetSuiteConfigs] (
    [Id]                        UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [AccountId]                 NVARCHAR(100) NOT NULL DEFAULT '',
    [ConsumerKey]               NVARCHAR(255) NOT NULL DEFAULT '',
    [ConsumerSecret]            NVARCHAR(MAX) NOT NULL DEFAULT '',
    [TokenKey]                  NVARCHAR(255) NOT NULL DEFAULT '',
    [TokenSecret]               NVARCHAR(MAX) NOT NULL DEFAULT '',
    [AmountThreshold]           DECIMAL(18,2) NOT NULL DEFAULT 5000,
    [Currency]                  NVARCHAR(10) NOT NULL DEFAULT 'ILS',
    [IsEnabled]                 BIT NOT NULL DEFAULT 0,
    [AutoSync]                  BIT NOT NULL DEFAULT 0,
    [AutoSyncIntervalMinutes]   INT NOT NULL DEFAULT 60,
    [LastSyncAt]                DATETIME NULL,
    [CreatedAt]                 DATETIME NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]                 DATETIME NOT NULL DEFAULT GETUTCDATE()
);");

        await EnsureAsync(db, "TaxAuthorityConfigs", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxAuthorityConfigs')
CREATE TABLE [dbo].[TaxAuthorityConfigs] (
    [Id]            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [ApiBaseUrl]    NVARCHAR(500) NOT NULL DEFAULT 'https://openapi.taxes.gov.il',
    [ClientId]      NVARCHAR(255) NULL,
    [ClientSecret]  NVARCHAR(MAX) NULL,
    [Username]      NVARCHAR(255) NULL,
    [BusinessId]    NVARCHAR(50) NOT NULL DEFAULT '',
    [IsEnabled]     BIT NOT NULL DEFAULT 0,
    [UseSandbox]    BIT NOT NULL DEFAULT 1,
    [CreatedAt]     DATETIME NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]     DATETIME NOT NULL DEFAULT GETUTCDATE()
);");

        await EnsureAsync(db, "IntegrationSyncLogs", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'IntegrationSyncLogs')
CREATE TABLE [dbo].[IntegrationSyncLogs] (
    [Id]             UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [Status]         NVARCHAR(50) NOT NULL DEFAULT '',
    [IsDryRun]       BIT NOT NULL DEFAULT 0,
    [TotalFetched]   INT NOT NULL DEFAULT 0,
    [AboveThreshold] INT NOT NULL DEFAULT 0,
    [Approved]       INT NOT NULL DEFAULT 0,
    [Failed]         INT NOT NULL DEFAULT 0,
    [ErrorMessage]   NVARCHAR(MAX) NULL,
    [StartedAt]      DATETIME NOT NULL DEFAULT GETUTCDATE(),
    [CompletedAt]    DATETIME NULL,
    [DurationMs]     BIGINT NOT NULL DEFAULT 0,
    [DetailsJson]    NVARCHAR(MAX) NULL
);");

        await EnsureAsync(db, "AllocationRecords", @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AllocationRecords')
CREATE TABLE [dbo].[AllocationRecords] (
    [Id]                      UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [NetSuiteTransactionId]   NVARCHAR(100) NOT NULL DEFAULT '',
    [TranId]                  NVARCHAR(100) NOT NULL DEFAULT '',
    [VendorId]                NVARCHAR(100) NULL,
    [VendorName]              NVARCHAR(255) NULL,
    [Amount]                  DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Currency]                NVARCHAR(10) NOT NULL DEFAULT 'ILS',
    [InvoiceDate]             NVARCHAR(20) NULL,
    [AllocationNumber]        NVARCHAR(100) NULL,
    [Status]                  NVARCHAR(50) NOT NULL DEFAULT 'pending',
    [ErrorMessage]            NVARCHAR(MAX) NULL,
    [CreatedAt]               DATETIME NOT NULL DEFAULT GETUTCDATE()
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_AllocationRecords_NetSuiteTransactionId')
    CREATE INDEX IX_AllocationRecords_NetSuiteTransactionId
        ON [dbo].[AllocationRecords] ([NetSuiteTransactionId]);");
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
