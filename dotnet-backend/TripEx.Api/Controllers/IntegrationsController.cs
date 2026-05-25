using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

/// <summary>
/// ניהול אינטגרציות — NetSuite ורשות המיסים
///
/// Endpoints:
///   GET    /api/integrations/status          — סטטוס כללי של האינטגרציות
///   GET    /api/integrations/netsuite/config  — קבלת הגדרות NetSuite
///   POST   /api/integrations/netsuite/config  — שמירת הגדרות NetSuite
///   POST   /api/integrations/netsuite/test    — בדיקת חיבור NetSuite
///   GET    /api/integrations/taxauthority/config — קבלת הגדרות רשות המיסים
///   POST   /api/integrations/taxauthority/config — שמירת הגדרות רשות המיסים
///   POST   /api/integrations/sync             — הפעלת סנכרון
///   GET    /api/integrations/sync/history     — היסטוריית סנכרון
/// </summary>
[ApiController]
[Route("api/integrations")]
[Authorize]
public class IntegrationsController : ControllerBase
{
    private readonly IntegrationSyncService _syncService;
    private readonly NetSuiteService _netSuiteService;

    public IntegrationsController(
        IntegrationSyncService syncService,
        NetSuiteService netSuiteService)
    {
        _syncService = syncService;
        _netSuiteService = netSuiteService;
    }

    // ─────────────────────────────────────────────────────────────────
    // Status
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/integrations/status
    /// סטטוס כללי — האם האינטגרציות מוגדרות ומופעלות
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var nsConfig = await _syncService.GetNetSuiteConfigAsync();
        var taConfig = await _syncService.GetTaxAuthorityConfigAsync();
        var history = await _syncService.GetSyncHistoryAsync(1);

        var lastSync = history.FirstOrDefault();

        return Ok(new IntegrationStatusResponse
        {
            NetSuiteConfigured = nsConfig != null && !string.IsNullOrWhiteSpace(nsConfig.AccountId),
            NetSuiteEnabled = nsConfig?.IsEnabled ?? false,
            TaxAuthorityConfigured = taConfig != null && !string.IsNullOrWhiteSpace(taConfig.BusinessId),
            TaxAuthorityEnabled = taConfig?.IsEnabled ?? false,
            LastSyncAt = lastSync?.StartedAt,
            LastSyncStatus = lastSync?.Status,
            AmountThreshold = nsConfig?.AmountThreshold ?? 5000,
            PendingAllocations = 0, // TODO: count from AllocationRecords where status = 'pending'
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // NetSuite Configuration
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/integrations/netsuite/config
    /// קבלת הגדרות NetSuite (ללא secrets)
    /// </summary>
    [HttpGet("netsuite/config")]
    public async Task<IActionResult> GetNetSuiteConfig()
    {
        var config = await _syncService.GetNetSuiteConfigAsync();
        if (config == null)
        {
            return Ok(new NetSuiteConfigDto
            {
                AmountThreshold = 5000,
                Currency = "ILS",
                IsEnabled = false,
                AutoSync = false,
                AutoSyncIntervalMinutes = 60
            });
        }

        // Mask secrets for security — only return whether they are set
        return Ok(new
        {
            config.Id,
            config.AccountId,
            ConsumerKey = config.ConsumerKey,
            ConsumerSecret = MaskSecret(config.ConsumerSecret),
            TokenKey = config.TokenKey,
            TokenSecret = MaskSecret(config.TokenSecret),
            config.AmountThreshold,
            config.Currency,
            config.IsEnabled,
            config.AutoSync,
            config.AutoSyncIntervalMinutes,
            config.LastSyncAt,
            config.UpdatedAt,
        });
    }

    /// <summary>
    /// POST /api/integrations/netsuite/config
    /// שמירת הגדרות NetSuite
    /// </summary>
    [HttpPost("netsuite/config")]
    public async Task<IActionResult> SaveNetSuiteConfig([FromBody] SaveNetSuiteConfigRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AccountId))
            return BadRequest(new { error = "AccountId הוא שדה חובה" });

        var saved = await _syncService.SaveNetSuiteConfigAsync(request);
        return Ok(new { success = true, config = new { saved.Id, saved.AccountId, saved.IsEnabled, saved.AmountThreshold } });
    }

    /// <summary>
    /// POST /api/integrations/netsuite/test
    /// בדיקת חיבור ל-NetSuite
    /// </summary>
    [HttpPost("netsuite/test")]
    public async Task<IActionResult> TestNetSuiteConnection([FromBody] SaveNetSuiteConfigRequest? request)
    {
        // Use saved config if no request body provided
        NetSuiteConfigDto config;
        if (request != null && !string.IsNullOrWhiteSpace(request.AccountId))
        {
            config = new NetSuiteConfigDto
            {
                AccountId = request.AccountId,
                ConsumerKey = request.ConsumerKey,
                ConsumerSecret = request.ConsumerSecret,
                TokenKey = request.TokenKey,
                TokenSecret = request.TokenSecret,
            };
        }
        else
        {
            var saved = await _syncService.GetNetSuiteConfigAsync();
            if (saved == null)
                return BadRequest(new { error = "אין הגדרות NetSuite — הגדר תחילה" });
            config = saved;
        }

        var result = await _netSuiteService.TestConnectionAsync(config);
        return Ok(result);
    }

    // ─────────────────────────────────────────────────────────────────
    // Tax Authority Configuration
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/integrations/taxauthority/config
    /// קבלת הגדרות רשות המיסים
    /// </summary>
    [HttpGet("taxauthority/config")]
    public async Task<IActionResult> GetTaxAuthorityConfig()
    {
        var config = await _syncService.GetTaxAuthorityConfigAsync();
        if (config == null)
        {
            return Ok(new TaxAuthorityConfigDto
            {
                ApiBaseUrl = "https://openapi.taxes.gov.il",
                IsEnabled = false,
                UseSandbox = true
            });
        }

        return Ok(new
        {
            config.Id,
            config.ApiBaseUrl,
            ClientId = config.ClientId,
            ClientSecret = MaskSecret(config.ClientSecret),
            config.Username,
            config.BusinessId,
            config.IsEnabled,
            config.UseSandbox,
            config.UpdatedAt,
        });
    }

    /// <summary>
    /// POST /api/integrations/taxauthority/config
    /// שמירת הגדרות רשות המיסים
    /// </summary>
    [HttpPost("taxauthority/config")]
    public async Task<IActionResult> SaveTaxAuthorityConfig([FromBody] SaveTaxAuthorityConfigRequest request)
    {
        var saved = await _syncService.SaveTaxAuthorityConfigAsync(request);
        return Ok(new { success = true, config = new { saved.Id, saved.BusinessId, saved.IsEnabled, saved.UseSandbox } });
    }

    // ─────────────────────────────────────────────────────────────────
    // Sync Operations
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/integrations/sync
    /// הפעלת סנכרון מלא:
    ///   1. שליפת הוצאות מ-NetSuite שמעל הסף
    ///   2. שליחה לרשות המיסים לקבלת מספר הקצאה
    ///   3. שמירת התוצאות
    ///
    /// Body (optional):
    /// {
    ///   "dryRun": true,          // בדיקה בלי לבצע שינויים
    ///   "dateFrom": "2025-01-01",
    ///   "dateTo": "2025-12-31",
    ///   "amountThresholdOverride": 10000  // עקיפת סף ברירת המחדל
    /// }
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> TriggerSync([FromBody] TriggerSyncRequest? request)
    {
        request ??= new TriggerSyncRequest();

        Console.WriteLine($"[API] Sync triggered — DryRun={request.DryRun}, " +
                          $"DateFrom={request.DateFrom}, DateTo={request.DateTo}");

        var result = await _syncService.RunSyncAsync(request);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/integrations/sync/history?limit=50
    /// היסטוריית הרצות הסנכרון
    /// </summary>
    [HttpGet("sync/history")]
    public async Task<IActionResult> GetSyncHistory([FromQuery] int limit = 50)
    {
        var history = await _syncService.GetSyncHistoryAsync(Math.Min(limit, 200));
        return Ok(history);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string? MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        if (secret.Length <= 4) return "****";
        return secret[..4] + new string('*', Math.Min(secret.Length - 4, 20));
    }
}
