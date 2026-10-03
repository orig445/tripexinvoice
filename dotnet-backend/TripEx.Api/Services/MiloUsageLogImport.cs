using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>One model call found in Milo's own log, with who it was for as far as the log says.</summary>
public record LoggedCall(DateTime AtUtc, string Model, int PromptTokens, int CompletionTokens, int TotalTokens,
    Guid? SessionId, string Kind, string? CustomerName, string? Company);

/// <summary>
/// Real usage from before the /usage page existed, read back out of Milo's daily log files
/// (logs/tripex-yyyyMMdd.log, kept for 30 days). Every answer since the usage block was added logs
/// "[OCI] usage prompt=… completion=… total=…" — Oracle's own count of the tokens it bills — and
/// the lines around it say which conversation and which user it was for:
///
///   [CHAT-CONTINUITY] session=…            ← a new request starts
///   [WIDGET-CONTEXT] … customerName=… company=… … instance=…
///   [OCI] Request body length=…, model=…
///   [OCI] usage prompt=… completion=… total=…
///   [CHAT] session=… intent=… latency=…    ← the answer went out
///
/// The last call before the [CHAT] line is the answer; any earlier one in the same request is the
/// status-list classifier. Requests that overlap in time can interleave in the log, and then a
/// call may be credited to the neighbouring conversation — rare at Milo's traffic, and the token
/// totals stay right either way. The model is kept from the last "Request body" line seen, not
/// reset per request: it is the same for the whole deployment, and an overlap must not leave a
/// call with no model (which would price it at nothing).
/// </summary>
public static class MiloUsageLogImport
{
    private static readonly Regex LinePrefix = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<off>[+-]\d{2}:\d{2}) \[[A-Z]+\s*\] (?<msg>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex Usage = new(
        @"^\[OCI\] usage prompt=(?<p>-?\d+) completion=(?<c>-?\d+) total=(?<t>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex RequestBody = new(@"^\[OCI\] Request body length=\d+, model=(?<m>\S+)", RegexOptions.Compiled);
    private static readonly Regex Chat = new(@"^\[CHAT\] session=(?<s>[0-9a-fA-F-]{36}) ", RegexOptions.Compiled);
    private static readonly Regex FileDay = new(@"tripex-(?<d>\d{8})", RegexOptions.Compiled);
    private static readonly Regex WidgetContext = new(
        @"^\[WIDGET-CONTEXT\] .*?customerName=(?<n>.*?) (?:company=(?<c>.*?) )?role=.*?(?: instance=(?<i>\S+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// The calls in one file's lines. With fileDay (the date in the file's name), a call stamped
    /// more than a day away from it is dropped: the file holds only that day, so such a line was
    /// not written by the log itself.
    /// </summary>
    public static List<LoggedCall> Parse(IEnumerable<string> lines, DateOnly? fileDay = null)
    {
        var calls = new List<LoggedCall>();
        var pending = new List<(DateTime At, string Model, int P, int C, int T)>();
        string? name = null, company = null, model = null;

        void Flush(Guid? session)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                calls.Add(new LoggedCall(p.At, p.Model, p.P, p.C, p.T, session,
                    session != null && i == pending.Count - 1 ? MiloUsage.AnswerKind : MiloUsage.ClassifierKind,
                    name, company));
            }
            pending.Clear();
        }

        foreach (var raw in lines)
        {
            var m = LinePrefix.Match(raw);
            if (!m.Success) continue; // a continuation line (the Q:/A: text under [CHAT]) or noise
            var msg = m.Groups["msg"].Value;

            if (msg.StartsWith("[CHAT-CONTINUITY]", StringComparison.Ordinal))
            {
                // A new request: nothing from the one before may be credited to it.
                Flush(null);
                name = company = null;
                continue;
            }

            var w = WidgetContext.Match(msg);
            if (w.Success)
            {
                name = NullIfNone(w.Groups["n"].Value);
                company = ChatService.CompanyFromHostInstance(NullIfNone(w.Groups["i"].Value));
                continue;
            }

            var b = RequestBody.Match(msg);
            if (b.Success) { model = b.Groups["m"].Value; continue; }

            var u = Usage.Match(msg);
            if (u.Success)
            {
                if (!TryUtc(m.Groups["ts"].Value, m.Groups["off"].Value, out var at)) continue;
                if (fileDay is { } day && Math.Abs(DateOnly.FromDateTime(at).DayNumber - day.DayNumber) > 1) continue;
                // A number too big for an int is not something OCI wrote; one such line must not
                // stop the whole import.
                if (!int.TryParse(u.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var prompt)
                    || !int.TryParse(u.Groups["c"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var completion)
                    || !int.TryParse(u.Groups["t"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var total))
                    continue;
                pending.Add((at, model ?? "unknown", prompt, completion, total));
                continue;
            }

            var c = Chat.Match(msg);
            if (c.Success && Guid.TryParse(c.Groups["s"].Value, out var session))
                Flush(session);
        }

        Flush(null);
        return calls;
    }

    /// <summary>
    /// The same call always gets the same id, so importing the same log twice adds nothing.
    /// </summary>
    public static Guid StableId(LoggedCall c)
    {
        var key = string.Join("|", c.AtUtc.ToString("O", CultureInfo.InvariantCulture), c.Model,
            c.PromptTokens, c.CompletionTokens, c.TotalTokens);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    public record ImportResult(int Files, int CallsFound, int Imported, int AlreadyThere, int AfterLiveRecording,
        string? FirstDay, string? LastDay);

    /// <summary>The date in a log file's name (tripex-yyyyMMdd.log), when it has one.</summary>
    public static DateOnly? DayOfFile(string file)
    {
        var m = FileDay.Match(Path.GetFileName(file));
        return m.Success && DateOnly.TryParseExact(m.Groups["d"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// Whether a call from the log is one already stored live. The very first live calls are in the
    /// log too, stamped a moment BEFORE live recording began (the log line is written before the
    /// row, and the first row also waits for the table to be created), so the time cut-off alone
    /// would count them twice. Same tokens in the same conversation is the same call.
    /// </summary>
    public static bool IsLiveCall(LoggedCall c, IEnumerable<(Guid? SessionId, int Prompt, int Total)> live)
        => live.Any(l => l.Prompt == c.PromptTokens && l.Total == c.TotalTokens
                         && (c.SessionId == null || l.SessionId == c.SessionId));

    /// <summary>
    /// Reads every log file and stores the calls not stored yet. Only calls from before live
    /// recording began: from then on each call is already stored as it happens, and counting it a
    /// second time from the log would double the bill.
    /// </summary>
    public static async Task<ImportResult> ImportAsync(TripExDbContext db, string logsDir, ILogger logger, CancellationToken ct = default)
    {
        await SchemaGuard.EnsureChatUsageAsync(db);

        var liveStart = await db.ChatUsages.Where(u => u.Source == MiloUsage.LiveSource)
            .Select(u => (DateTime?)u.CreatedAt).MinAsync(ct) ?? DateTime.UtcNow;

        var files = Directory.Exists(logsDir)
            ? Directory.GetFiles(logsDir, "tripex-*.log").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

        var all = new List<LoggedCall>();
        foreach (var file in files)
        {
            // log4net holds today's file open for writing, so it is read with ReadWrite sharing.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(ct) is { } line) lines.Add(line);
            all.AddRange(Parse(lines, DayOfFile(file)));
        }

        var margin = TimeSpan.FromMinutes(10);
        var liveAtStart = (await db.ChatUsages.Where(u => u.Source == MiloUsage.LiveSource && u.CreatedAt < liveStart + margin)
                .Select(u => new { u.SessionId, u.PromptTokens, u.TotalTokens }).ToListAsync(ct))
            .Select(u => (u.SessionId, u.PromptTokens, u.TotalTokens)).ToList();

        int imported = 0, existing = 0, afterLive = 0;
        foreach (var c in all)
        {
            if (c.AtUtc >= liveStart) { afterLive++; continue; }
            if (c.AtUtc >= liveStart - margin && IsLiveCall(c, liveAtStart)) { afterLive++; continue; }
            var id = StableId(c);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[chat_usage] WHERE [id] = {id})
INSERT INTO [dbo].[chat_usage]
    ([id], [session_id], [created_at], [kind], [model], [prompt_tokens], [completion_tokens], [total_tokens],
     [company], [customer_email], [customer_name], [source])
VALUES
    ({id}, {c.SessionId}, {c.AtUtc}, {c.Kind}, {c.Model}, {Math.Max(c.PromptTokens, 0)}, {Math.Max(c.CompletionTokens, 0)},
     {Math.Max(c.TotalTokens, 0)}, {c.Company}, {(string?)null}, {c.CustomerName}, {MiloUsage.LogSource})", ct);
            if (inserted > 0) imported++; else existing++;
        }

        var result = new ImportResult(files.Count, all.Count, imported, existing, afterLive,
            all.Count > 0 ? all.Min(c => c.AtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            all.Count > 0 ? all.Max(c => c.AtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
        logger.LogInformation("[USAGE] log import: {Files} file(s), {Found} call(s) found, {Imported} imported, {Existing} already there, {AfterLive} after live recording began",
            result.Files, result.CallsFound, result.Imported, result.AlreadyThere, result.AfterLiveRecording);
        return result;
    }

    private static string? NullIfNone(string? v)
        => string.IsNullOrWhiteSpace(v) || v.Trim() == "(null)" ? null : v.Trim();

    private static bool TryUtc(string ts, string offset, out DateTime utc)
    {
        if (DateTimeOffset.TryParseExact(ts + " " + offset, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}
