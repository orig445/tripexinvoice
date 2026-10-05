using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Whose conversation it is (Roi, 2026-10-04). Every TAS widget user reaches this API as the same
/// system user, and the widget keeps one conversation per browser — so when a second TAS user
/// signed in on the same computer, their first message resumed the first user's conversation:
/// its history went into Milo's prompt, its ticket and its agent came with it.
///
/// The owner is what TAS says about the user: the email (stored only as a hash), the customerId
/// and the instance. The rule for "the same user" is shared with the widget, which applies it to
/// the conversation it keeps in the browser (Roi, 2026-10-05) — see BelongsToSomeoneElseThan.
/// </summary>
public record SessionOwner(string? EmailHash, string? CustomerId, string? Instance)
{
    /// <summary>A request that says nothing about who it is.</summary>
    public static readonly SessionOwner None = new(null, null, null);

    public bool IsEmpty => EmailHash == null && CustomerId == null && Instance == null;

    public static SessionOwner From(WidgetIdentityContext? widget) => new(
        HashEmail(widget?.Email),
        Clean(widget?.CustomerId),
        Clean(widget?.HostInstance)?.ToLowerInvariant());

    /// <summary>
    /// The identity GET /api/chat/updates carries in its X-Milo-Email / X-Milo-Customer-Id /
    /// X-Milo-Instance headers, read exactly like the same fields in a chat message. A value the
    /// widget percent-encoded is decoded first, so the email hashes the same either way.
    /// </summary>
    public static SessionOwner FromHeaders(string? email, string? customerId, string? instance) => From(
        new WidgetIdentityContext { Email = Decode(email), CustomerId = Decode(customerId), HostInstance = Decode(instance) });

    /// <summary>SHA-256 of the trimmed, lower-cased email, as hex. Null for no email.</summary>
    public static string? HashEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e))).ToLowerInvariant();
    }

    /// <summary>
    /// True when the current request is NOT the user this stored owner records:
    ///   * The email is the one per-user key — TAS's customerId is the company (two users of one
    ///     company both send customerId=1). Both sides have one: the same user exactly when they
    ///     match, whatever the customerId says.
    ///   * A recorded email and a request without one: not the same user.
    ///   * No email recorded: the customerId and the instance, each counted only when both sides
    ///     have it, as before.
    ///   * Two different instances (both known) are two different users in every case, and a
    ///     request with no identity at all is never the owner of a conversation that has one.
    /// An ownerless conversation contradicts nothing here; whether a request may take it over is
    /// the caller's decision (see ChatService.CheckSessionOwnerAsync).
    /// </summary>
    public bool BelongsToSomeoneElseThan(SessionOwner current)
    {
        if (IsEmpty) return false;
        if (current.IsEmpty) return true;
        if (Differs(Instance, current.Instance)) return true;
        if (EmailHash != null) return !string.Equals(EmailHash, current.EmailHash, StringComparison.Ordinal);
        return Differs(CustomerId, current.CustomerId);
    }

    private static bool Differs(string? stored, string? current)
        => stored != null && current != null && !string.Equals(stored, current, StringComparison.Ordinal);

    // Trimmed, and capped at the column's 64 characters; null when blank.
    private static string? Clean(string? v)
    {
        var s = v?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= 64 ? s : s[..64];
    }

    private static string? Decode(string? v)
    {
        if (string.IsNullOrEmpty(v) || !v.Contains('%')) return v;
        try { return Uri.UnescapeDataString(v); }
        catch (UriFormatException) { return v; }
    }
}

/// <summary>
/// What reading a conversation's owner found. Known=false means the database could not say, which
/// is not the same as "nobody owns it": callers treat it as someone else's (fail closed).
/// </summary>
public readonly record struct SessionOwnerRead(bool Known, SessionOwner? Owner)
{
    public static readonly SessionOwnerRead Unknown = new(false, null);
}

/// <summary>
/// Reading and recording SessionOwner in chat_session_owners. Writes are best-effort: a failed one
/// is logged and the turn carries on. A failed READ is not — see SessionOwnerRead.
/// </summary>
public static class MiloSessionOwners
{
    public static async Task<SessionOwnerRead> ReadAsync(TripExDbContext db, Guid sessionId, ILogger logger)
    {
        try
        {
            await SchemaGuard.EnsureChatSessionOwnersAsync(db);
            var row = await db.ChatSessionOwners.AsNoTracking().FirstOrDefaultAsync(o => o.SessionId == sessionId);
            return new SessionOwnerRead(true, row == null ? null : new SessionOwner(row.EmailHash, row.CustomerId, row.Instance));
        }
        catch (Exception ex) when (IsMissingTable(ex))
        {
            // The table does not exist (SchemaGuard could not create it): then no conversation can
            // have an owner, which is a real answer, not an unknown one. Treating it as unknown
            // would restart every conversation on every turn until someone created the table.
            logger.LogWarning("[SESSION-OWNER] chat_session_owners is missing — no conversation has an owner");
            return new SessionOwnerRead(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[SESSION-OWNER] session={SessionId} owner not read: {Message}", sessionId, ex.Message);
            return SessionOwnerRead.Unknown;
        }
    }

    /// <summary>
    /// Records the owner of a conversation that has none yet. Its own statement rather than the
    /// change tracker, like MiloUsage.TryRecordAsync: two first turns of one conversation can
    /// race here, and the loser's duplicate key must not ride along into the turn's other saves.
    /// </summary>
    public static async Task TryClaimAsync(TripExDbContext db, Guid sessionId, SessionOwner owner, ILogger logger)
    {
        if (owner.IsEmpty) return;
        try
        {
            await SchemaGuard.EnsureChatSessionOwnersAsync(db);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[chat_session_owners] WHERE [session_id] = {sessionId})
INSERT INTO [dbo].[chat_session_owners] ([session_id], [email_hash], [customer_id], [instance], [created_at])
VALUES ({sessionId}, {owner.EmailHash}, {owner.CustomerId}, {owner.Instance}, {DateTime.UtcNow})");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[SESSION-OWNER] session={SessionId} owner not recorded: {Message}", sessionId, ex.Message);
        }
    }

    /// <summary>
    /// Adds what the owner row was missing — TAS may send its fields one at a time, and the first
    /// turn may have had only some of them — once a request from the same user carries them. Each
    /// column is written only WHERE it IS NULL, so a value already recorded is never replaced, even
    /// by a request that raced this one.
    /// </summary>
    public static async Task TryFillInAsync(TripExDbContext db, Guid sessionId, SessionOwner stored, SessionOwner current, ILogger logger)
    {
        try
        {
            if (stored.EmailHash == null && current.EmailHash != null)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [dbo].[chat_session_owners] SET [email_hash] = {current.EmailHash} WHERE [session_id] = {sessionId} AND [email_hash] IS NULL");
            if (stored.CustomerId == null && current.CustomerId != null)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [dbo].[chat_session_owners] SET [customer_id] = {current.CustomerId} WHERE [session_id] = {sessionId} AND [customer_id] IS NULL");
            if (stored.Instance == null && current.Instance != null)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [dbo].[chat_session_owners] SET [instance] = {current.Instance} WHERE [session_id] = {sessionId} AND [instance] IS NULL");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[SESSION-OWNER] session={SessionId} owner not completed: {Message}", sessionId, ex.Message);
        }
    }

    // SQL Server 208, "Invalid object name", anywhere in the chain (EF may wrap it).
    private static bool IsMissingTable(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SqlException { Number: 208 }) return true;
        return false;
    }
}
