using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>
/// One model call's token count, exactly as OCI reported it. Handed back by
/// OracleAiService.ChatAsync through its onUsage callback, so only the callers that ask for it —
/// the Milo chat path — are recorded.
/// </summary>
public readonly record struct OciUsage(string Model, int PromptTokens, int CompletionTokens, int TotalTokens)
{
    /// <summary>
    /// The output that is billed. A thinking model spends tokens on reasoning that never appears
    /// in the reply: they are in total_tokens but not in completion_tokens, and they are billed
    /// as output. Seen in production on 2026-09-29: prompt=39534 completion=86 total=42053, so 2,519
    /// output tokens were billed for an 86-token reply. When total is missing or smaller than the
    /// prompt, fall back to completion.
    /// </summary>
    public int BilledOutputTokens => TotalTokens >= PromptTokens && PromptTokens >= 0
        ? Math.Max(TotalTokens - PromptTokens, Math.Max(CompletionTokens, 0))
        : Math.Max(CompletionTokens, 0);
}

/// <summary>
/// Records what each Milo answer cost in tokens, and who it was for, so usage and cost can be
/// shown per company and per user (the /usage page, Roi 2026-09-29). Company is the one read
/// from the TAS address (ChatService.CompanyFromHostInstance); the user is the email TAS sends.
/// </summary>
public static class MiloUsage
{
    public const string AnswerKind = "answer";
    public const string ClassifierKind = "classifier";
    public const string LiveSource = "live";
    public const string LogSource = "log";

    /// <summary>
    /// Stores one call. Never throws, and runs as its own statement rather than through the
    /// change tracker: a failure here — the table not there yet, the database down — must not be
    /// able to take the conversation's own save down with it.
    /// </summary>
    public static async Task TryRecordAsync(TripExDbContext db, ILogger logger, Guid sessionId, string kind,
        OciUsage usage, string? company, string? email, string? name)
    {
        try
        {
            await SchemaGuard.EnsureChatUsageAsync(db);
            var model = Clip(usage.Model, 100) ?? "";
            var co = Clip(company, 64);
            var em = Clip(email?.Trim().ToLowerInvariant(), 256);
            var nm = Clip(name, 200);
            var prompt = Math.Max(usage.PromptTokens, 0);
            var completion = Math.Max(usage.CompletionTokens, 0);
            var total = Math.Max(usage.TotalTokens, 0);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO [dbo].[chat_usage]
    ([id], [session_id], [created_at], [kind], [model], [prompt_tokens], [completion_tokens], [total_tokens],
     [company], [customer_email], [customer_name], [source])
VALUES
    ({Guid.NewGuid()}, {sessionId}, {DateTime.UtcNow}, {kind}, {model}, {prompt}, {completion}, {total},
     {co}, {em}, {nm}, {LiveSource})");

            // One line per recorded call, so a deployment can be checked from the log alone: the
            // company it was credited to, and whether it has a user. Not the email itself — the log
            // is kept for 30 days and read by more people than the page is.
            logger.LogInformation(
                "[USAGE] session={SessionId} recorded {Kind} model={Model} in={Prompt} out={Out} company={Company} hasEmail={HasEmail}",
                sessionId, kind, model, prompt, usage.BilledOutputTokens, co ?? "(none)", em != null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[USAGE] session={SessionId} usage not recorded: {Message}", sessionId, ex.Message);
        }
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>A model's price, in US dollars per million tokens.</summary>
public class ModelPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

/// <summary>One stored call, as the usage report reads it.</summary>
public record UsageRow(Guid? SessionId, DateTime CreatedAtUtc, string Kind, string Model,
    int PromptTokens, int CompletionTokens, int TotalTokens, string? Company, string? Email, string? Name)
{
    public int BilledOutputTokens => new OciUsage(Model, PromptTokens, CompletionTokens, TotalTokens).BilledOutputTokens;
}

public record UsageTotals(int Conversations, int Answers, long InputTokens, long OutputTokens, decimal CostUsd);
public record UsageUser(string? Email, string? Name, int Conversations, int Answers, long InputTokens, long OutputTokens, decimal CostUsd);
public record UsageCompany(string? Company, int Conversations, int Answers, long InputTokens, long OutputTokens, decimal CostUsd,
    List<UsageUser> Users);
public record UsageDay(string Date, int Answers, decimal CostUsd);
public record UsagePrice(string Model, decimal InputPerMillion, decimal OutputPerMillion);
public record UsageSummary(string From, string To, string TimeZone, string Currency, UsageTotals Totals,
    List<UsageCompany> Companies, List<UsageDay> Days, List<UsagePrice> Prices, List<string> UnpricedModels);

/// <summary>A name as stored next to a company or an email, the raw material of UsageNameMaps.</summary>
public record UsageIdentity(string? Name, string? Company, string? Email);

/// <summary>
/// What a customer's name alone says about them, learned from the calls that carry more. Calls
/// imported from the log have a name and nothing else; these maps give them the company the same
/// name has on its live calls when it has exactly one, and then the email that name has AT THAT
/// company when it has exactly one there — a generic name like "System Administrator" at two
/// customers must not lend one customer's email to the other. Built from every stored call rather
/// than the dates on screen, so a call's company does not change with the range picked.
/// EmailByNameAndCompany is keyed by NameCompanyKey.
/// </summary>
public record UsageNameMaps(IReadOnlyDictionary<string, string> CompanyByName, IReadOnlyDictionary<string, string> EmailByNameAndCompany)
{
    public static string NameCompanyKey(string name, string? company) => name.Trim() + "\u0001" + (company?.Trim() ?? "");
}

/// <summary>
/// Turns stored calls into the report: totals, per company, per user within each company, per
/// day. Pure, so every number on the page can be pinned by a test.
/// </summary>
public static class MiloUsageReport
{
    /// <summary>
    /// Prices used when the config names none. OCI's on-demand price for these models, per million
    /// tokens, as published on 2026-09-29 (prompts up to 200K tokens, which every Milo prompt is).
    /// They are an estimate of the bill, not the bill: correct them under Milo:Pricing if Oracle's
    /// price list or your contract says otherwise. The real invoice is in the OCI console.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ModelPrice> DefaultPrices =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["google.gemini-2.5-pro"] = new() { InputPerMillion = 1.25m, OutputPerMillion = 10.00m },
            ["google.gemini-2.5-flash"] = new() { InputPerMillion = 0.30m, OutputPerMillion = 2.50m },
            ["google.gemini-2.5-flash-lite"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.40m },
        };

    /// <summary>The defaults, overridden model by model by Milo:Pricing in the config.</summary>
    public static Dictionary<string, ModelPrice> LoadPrices(IConfiguration configuration)
    {
        var prices = new Dictionary<string, ModelPrice>(DefaultPrices, StringComparer.OrdinalIgnoreCase);
        foreach (var model in configuration.GetSection("Milo:Pricing").GetChildren())
        {
            var p = new ModelPrice();
            if (decimal.TryParse(model["InputPerMillion"], NumberStyles.Number, CultureInfo.InvariantCulture, out var i)) p.InputPerMillion = i;
            else if (prices.TryGetValue(model.Key, out var d1)) p.InputPerMillion = d1.InputPerMillion;
            if (decimal.TryParse(model["OutputPerMillion"], NumberStyles.Number, CultureInfo.InvariantCulture, out var o)) p.OutputPerMillion = o;
            else if (prices.TryGetValue(model.Key, out var d2)) p.OutputPerMillion = d2.OutputPerMillion;
            prices[model.Key] = p;
        }
        return prices;
    }

    public static decimal? Cost(UsageRow r, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        if (!prices.TryGetValue(r.Model, out var p)) return null;
        return (r.PromptTokens * p.InputPerMillion + r.BilledOutputTokens * p.OutputPerMillion) / 1_000_000m;
    }

    /// <summary>Israel time, which is how the days on the page are cut. UTC if the server has no such zone.</summary>
    public static TimeZoneInfo ReportZone()
    {
        foreach (var id in new[] { "Israel Standard Time", "Asia/Jerusalem" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { /* try the next name */ }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// The UTC window for a range of local dates, both inclusive: from local midnight of the first
    /// day to local midnight after the last.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) UtcWindow(DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var start = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(start, zone), TimeZoneInfo.ConvertTimeToUtc(end, zone));
    }

    public static UsageSummary Summarize(IReadOnlyCollection<UsageRow> rows, IReadOnlyDictionary<string, ModelPrice> prices,
        DateOnly from, DateOnly to, TimeZoneInfo zone, UsageNameMaps? names = null)
    {
        names ??= BuildNameMaps(rows.Select(r => new UsageIdentity(r.Name, r.Company, r.Email)));
        rows = FillFromName(rows, names);
        decimal CostOf(IEnumerable<UsageRow> rs) => rs.Sum(r => Cost(r, prices) ?? 0m);
        int Conversations(IEnumerable<UsageRow> rs) => rs.Where(r => r.SessionId != null).Select(r => r.SessionId).Distinct().Count();
        int Answers(IEnumerable<UsageRow> rs) => rs.Count(r => r.Kind == MiloUsage.AnswerKind);

        var totals = new UsageTotals(Conversations(rows), Answers(rows),
            rows.Sum(r => (long)r.PromptTokens), rows.Sum(r => (long)r.BilledOutputTokens), CostOf(rows));

        var companies = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Company) ? null : r.Company!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageCompany(
                g.Key, Conversations(g), Answers(g), g.Sum(r => (long)r.PromptTokens), g.Sum(r => (long)r.BilledOutputTokens), CostOf(g),
                g.GroupBy(UserKey, StringComparer.OrdinalIgnoreCase)
                 .Select(u => new UsageUser(
                     u.Select(r => r.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))?.Trim().ToLowerInvariant(),
                     u.Select(r => r.Name).LastOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                     Conversations(u), Answers(u), u.Sum(r => (long)r.PromptTokens), u.Sum(r => (long)r.BilledOutputTokens), CostOf(u)))
                 .OrderByDescending(u => u.CostUsd).ThenBy(u => u.Email)
                 .ToList()))
            .OrderByDescending(c => c.CostUsd).ThenBy(c => c.Company)
            .ToList();

        var byDay = rows
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc), zone)))
            .ToDictionary(g => g.Key, g => g.ToList());
        var days = new List<UsageDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var rs = byDay.TryGetValue(d, out var list) ? list : new List<UsageRow>();
            days.Add(new UsageDay(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Answers(rs), CostOf(rs)));
        }

        var used = rows.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var priceList = used.Where(prices.ContainsKey)
            .Select(m => new UsagePrice(m, prices[m].InputPerMillion, prices[m].OutputPerMillion)).ToList();
        var unpriced = used.Where(m => !prices.ContainsKey(m)).ToList();

        return new UsageSummary(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            zone.Id, "USD", totals, companies, days, priceList, unpriced);
    }

    // A user is their email when TAS sent one, otherwise their name. FillFromName has already given
    // a name-only call the email its name has elsewhere, so the same person is one row.
    private static string? UserKey(UsageRow r)
        => !string.IsNullOrWhiteSpace(r.Email) ? r.Email!.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(r.Name) ? "name:" + r.Name!.Trim().ToLowerInvariant()
            : null;

    /// <summary>
    /// Names that stand for nobody in particular. The widget sends "Guest" when TAS gave no name,
    /// so a Guest seen at one company says nothing about a Guest anywhere else.
    /// </summary>
    public static bool IsPlaceholderName(string? name)
        => string.IsNullOrWhiteSpace(name) || name.Trim().Equals("Guest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For each real name, the one company and the one email it has been seen with. A name seen
    /// with two companies (or two emails) is left out of that map: it is two people, or a person who
    /// moved, and guessing would put calls in the wrong place.
    /// </summary>
    public static UsageNameMaps BuildNameMaps(IEnumerable<UsageIdentity> identities)
    {
        var known = identities.Where(i => !IsPlaceholderName(i.Name)).ToList();

        static Dictionary<string, string> OnlyOne(IEnumerable<(string Name, string Value)> pairs)
            => pairs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Values: g.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .Where(x => x.Values.Count == 1)
                .ToDictionary(x => x.Name, x => x.Values[0], StringComparer.OrdinalIgnoreCase);

        return new UsageNameMaps(
            OnlyOne(known.Where(i => !string.IsNullOrWhiteSpace(i.Company)).Select(i => (i.Name!.Trim(), i.Company!.Trim()))),
            OnlyOne(known.Where(i => !string.IsNullOrWhiteSpace(i.Email))
                .Select(i => (UsageNameMaps.NameCompanyKey(i.Name!, i.Company), i.Email!.Trim().ToLowerInvariant()))));
    }

    /// <summary>
    /// A call with no company takes the company its customer name has on other calls, when that
    /// name has exactly one — calls from before the widget sent the TAS address, and every call
    /// imported from the log, have a name but no company. Then a call with no email takes the one
    /// email that name has at the call's company, so the history and the live calls of one person
    /// are one row.
    /// </summary>
    public static IReadOnlyCollection<UsageRow> FillFromName(IReadOnlyCollection<UsageRow> rows, UsageNameMaps names)
    {
        if (names.CompanyByName.Count == 0 && names.EmailByNameAndCompany.Count == 0) return rows;
        return rows.Select(r =>
        {
            if (IsPlaceholderName(r.Name)) return r;
            var name = r.Name!.Trim();
            if (string.IsNullOrWhiteSpace(r.Company) && names.CompanyByName.TryGetValue(name, out var company))
                r = r with { Company = company };
            if (string.IsNullOrWhiteSpace(r.Email)
                && names.EmailByNameAndCompany.TryGetValue(UsageNameMaps.NameCompanyKey(name, r.Company), out var email))
                r = r with { Email = email };
            return r;
        }).ToList();
    }

    /// <summary>FillFromName with the maps taken from these rows alone.</summary>
    public static IReadOnlyCollection<UsageRow> FillCompanyFromName(IReadOnlyCollection<UsageRow> rows)
        => FillFromName(rows, BuildNameMaps(rows.Select(r => new UsageIdentity(r.Name, r.Company, r.Email))));

    /// <summary>
    /// The per-user table as CSV for Excel: a BOM so Hebrew names open correctly, and every field
    /// quoted so a comma in a name cannot shift the columns. A text field that Excel would run as a
    /// formula (it starts with = + - @, a tab or a carriage return) gets a leading ' — names and
    /// emails come from the customer's side, and opening the file must not run them.
    /// </summary>
    public static string ToCsv(UsageSummary s)
    {
        static string Q(string? v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        static string N(decimal v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("﻿");
        sb.AppendLine("Company,User email,User name,Conversations,Answers,Input tokens,Output tokens,Cost (USD)");
        foreach (var c in s.Companies)
            foreach (var u in c.Users)
                sb.AppendLine(string.Join(",", Q(c.Company ?? "Unknown"), Q(u.Email ?? ""), Q(u.Name ?? ""),
                    u.Conversations, u.Answers, u.InputTokens, u.OutputTokens, N(u.CostUsd)));
        sb.AppendLine(string.Join(",", Q("Total"), Q(""), Q(""), s.Totals.Conversations, s.Totals.Answers,
            s.Totals.InputTokens, s.Totals.OutputTokens, N(s.Totals.CostUsd)));
        return sb.ToString();
    }
}
