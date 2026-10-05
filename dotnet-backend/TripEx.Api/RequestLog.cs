using System.Text.RegularExpressions;

namespace TripEx.Api;

/// <summary>
/// What a request is called in the log (Roi, 2026-10-05). ASP.NET's own "Request starting /
/// Request finished" lines (Microsoft.AspNetCore.Hosting.Diagnostics) wrote the full URL, query
/// string included, for every request: the widget's conversation id
/// (/api/chat/updates?sessionToken=…, every 6 seconds) and the Zoho webhook secret, which is a
/// path segment of /api/zoho/desk/thread/{secret}. That logger is now at WARN (log4net.config) and
/// Program.cs writes one "[HTTP] GET /DEV_AI/api/chat/updates 200 12ms" line per request instead,
/// with the path from here: never a query string, and the webhook secret as "***".
/// </summary>
public static class RequestLog
{
    public const string Redacted = "***";

    // The segment after .../zoho/desk/thread/, whatever the path base or the casing.
    private static readonly Regex WebhookSecret = new(
        @"(?<=/api/zoho/desk/thread/)[^/]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The path as it may be logged. Callers pass PathBase + Path, which never holds the query; anything
    /// from a "?" on is dropped anyway, so a caller that passes a whole URL still logs no query.
    /// </summary>
    public static string SafePath(string? path)
    {
        var query = path?.IndexOf('?') ?? -1;
        if (query >= 0) path = path![..query];
        return string.IsNullOrEmpty(path) ? "/" : WebhookSecret.Replace(path, Redacted);
    }
}
