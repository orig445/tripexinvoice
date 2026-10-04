using System.Security.Cryptography;
using System.Text;
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
/// and the instance. A field counts only when both the stored owner and the current request have
/// it — TAS may send its fields one at a time, and a conversation from before this existed has no
/// owner at all — so only a real contradiction starts a new conversation.
/// </summary>
public record SessionOwner(string? EmailHash, string? CustomerId, string? Instance)
{
    public bool IsEmpty => EmailHash == null && CustomerId == null && Instance == null;

    public static SessionOwner From(WidgetIdentityContext? widget) => new(
        HashEmail(widget?.Email),
        Clean(widget?.CustomerId),
        Clean(widget?.HostInstance)?.ToLowerInvariant());

    /// <summary>SHA-256 of the trimmed, lower-cased email, as hex. Null for no email.</summary>
    public static string? HashEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e))).ToLowerInvariant();
    }

    /// <summary>True when a field both sides know differs: a different TAS user.</summary>
    public bool BelongsToSomeoneElseThan(SessionOwner current) =>
        Differs(EmailHash, current.EmailHash) || Differs(CustomerId, current.CustomerId) || Differs(Instance, current.Instance);

    private static bool Differs(string? stored, string? current)
        => stored != null && current != null && !string.Equals(stored, current, StringComparison.Ordinal);

    // Trimmed, and capped at the column's 64 characters; null when blank.
    private static string? Clean(string? v)
    {
        var s = v?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= 64 ? s : s[..64];
    }
}

/// <summary>
/// Reading and recording SessionOwner in chat_session_owners. Both are best-effort: a database
/// that cannot answer must not stop a customer getting an answer, so a failed read means "no
/// owner known" (the conversation carries on, as it always did) and a failed write is logged.
/// </summary>
public static class MiloSessionOwners
{
    public static async Task<SessionOwner?> ReadAsync(TripExDbContext db, Guid sessionId, ILogger logger)
    {
        try
        {
            await SchemaGuard.EnsureChatSessionOwnersAsync(db);
            var row = await db.ChatSessionOwners.AsNoTracking().FirstOrDefaultAsync(o => o.SessionId == sessionId);
            return row == null ? null : new SessionOwner(row.EmailHash, row.CustomerId, row.Instance);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[SESSION-OWNER] session={SessionId} owner not read: {Message}", sessionId, ex.Message);
            return null;
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
}
