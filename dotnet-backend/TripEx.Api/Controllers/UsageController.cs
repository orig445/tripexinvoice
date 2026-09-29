using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

/// <summary>
/// Milo's usage and its estimated OCI cost, per company and per user (Roi, 2026-09-29).
///
/// A page Roi bookmarks: /usage/{key}. There is no login here on purpose — the page is for one
/// person, and the key is its lock, the same way Zoho's webhook URL carries its secret. The key
/// lives only in appsettings.Production.json (Milo:UsagePageKey). Without a key of at least 24
/// characters the page does not exist, and a wrong key gets the same 404 as a URL that was never
/// routed, so the address cannot be probed.
///
/// The numbers are an estimate: tokens as OCI reported them, times the price in Milo:Pricing (or
/// MiloUsageReport.DefaultPrices). The bill itself is in the OCI console.
/// </summary>
[ApiController]
[AllowAnonymous]
public class UsageController : ControllerBase
{
    public const int MinKeyLength = 24;
    private const int MaxRangeDays = 400;

    private readonly TripExDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UsageController> _logger;

    public UsageController(TripExDbContext db, IConfiguration configuration, ILogger<UsageController> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Constant-time, and false whenever no usable key is configured.</summary>
    public static bool KeyMatches(string? configured, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.Trim().Length < MinKeyLength) return false;
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = Encoding.UTF8.GetBytes(configured.Trim());
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The date range asked for, as local (Israel) dates. Missing means this month so far; a range
    /// the wrong way round or longer than MaxRangeDays is refused rather than silently trimmed.
    /// </summary>
    public static bool TryParseRange(string? from, string? to, DateOnly today, out DateOnly start, out DateOnly end)
    {
        start = new DateOnly(today.Year, today.Month, 1);
        end = today;
        if (!string.IsNullOrWhiteSpace(from)
            && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
            return false;
        if (!string.IsNullOrWhiteSpace(to)
            && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
            return false;
        return end >= start && end.DayNumber - start.DayNumber <= MaxRangeDays;
    }

    private bool Authorized(string key) => KeyMatches(_configuration["Milo:UsagePageKey"], key);

    [HttpGet("usage/{key}")]
    public IActionResult Page(string key)
    {
        if (!Authorized(key)) return NotFound();

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TripEx.Api.Data.usage-page.html");
        if (stream == null) return NotFound();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Never cached, never indexed, never sent on as a referrer: the key is in this URL.
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    [HttpGet("api/usage/{key}/summary")]
    public async Task<IActionResult> Summary(string key, [FromQuery] string? from, [FromQuery] string? to)
    {
        if (!Authorized(key)) return NotFound();
        var summary = await BuildAsync(from, to);
        if (summary == null) return BadRequest(new { error = "Dates must be yyyy-MM-dd, the start before the end, at most 400 days apart." });
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(summary);
    }

    [HttpGet("api/usage/{key}/export")]
    public async Task<IActionResult> Export(string key, [FromQuery] string? from, [FromQuery] string? to)
    {
        if (!Authorized(key)) return NotFound();
        var summary = await BuildAsync(from, to);
        if (summary == null) return BadRequest();
        Response.Headers["Cache-Control"] = "no-store";
        return File(Encoding.UTF8.GetBytes(MiloUsageReport.ToCsv(summary)), "text/csv; charset=utf-8",
            $"milo-usage-{summary.From}-to-{summary.To}.csv");
    }

    /// <summary>
    /// Reads Milo's log files and stores every past call not stored yet — the real usage from
    /// before this page existed. Safe to press again: nothing is counted twice.
    /// </summary>
    [HttpPost("api/usage/{key}/import-logs")]
    public async Task<IActionResult> ImportLogs(string key, CancellationToken ct)
    {
        if (!Authorized(key)) return NotFound();
        var result = await MiloUsageLogImport.ImportAsync(_db, Path.Combine(AppContext.BaseDirectory, "logs"), _logger, ct);
        return Ok(result);
    }

    private async Task<UsageSummary?> BuildAsync(string? from, string? to)
    {
        var zone = MiloUsageReport.ReportZone();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        if (!TryParseRange(from, to, today, out var start, out var end)) return null;

        var (fromUtc, toUtc) = MiloUsageReport.UtcWindow(start, end, zone);
        await SchemaGuard.EnsureChatUsageAsync(_db);
        var rows = await _db.ChatUsages.AsNoTracking()
            .Where(u => u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            .Select(u => new UsageRow(u.SessionId, u.CreatedAt, u.Kind, u.Model, u.PromptTokens, u.CompletionTokens,
                u.TotalTokens, u.Company, u.CustomerEmail, u.CustomerName))
            .ToListAsync();

        _logger.LogInformation("[USAGE] report {From}..{To}: {Rows} call(s)", start, end, rows.Count);
        return MiloUsageReport.Summarize(rows, MiloUsageReport.LoadPrices(_configuration), start, end, zone);
    }
}
