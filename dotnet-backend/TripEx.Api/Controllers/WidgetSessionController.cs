using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Controllers;

/// <summary>
/// The route surface the TAS-embedded widget actually calls for conversation control.
///
/// Everything else in this API lives under "api/..." — but the widget's own script calls
/// GET {app}/AI/Message/ClearSession when the user presses "New chat" (observed in production
/// 2026-09-07: send-script.js -> app.js clearMessage()). That path matched no route here, so it
/// 404'd with an empty body, the widget's ApiClient threw on parsing the response, and the user
/// saw "That message didn't go through. Please try again." with no new chat ever starting.
///
/// This exists to answer that call rather than make the widget change: the widget is deployed
/// into TAS by hand and its source lives outside this repository, so the cheap, reliable fix is
/// on this side. Deliberately tolerant — it is a UI reset button, not a data operation:
///   * GET and POST both accepted (the widget uses GET).
///   * AllowAnonymous, because a 401 here would look identical to the 404 it replaces, and the
///     only state it can touch is a chat session's own status flag.
///   * Always returns 200, even with no session id and even if the database is unreachable, so
///     the widget can always clear its own UI.
/// </summary>
[ApiController]
[Route("AI/Message")]
[AllowAnonymous]
public class WidgetSessionController : ControllerBase
{
    private readonly TripExDbContext _db;
    private readonly ILogger<WidgetSessionController> _logger;

    public WidgetSessionController(TripExDbContext db, ILogger<WidgetSessionController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Ends the current conversation. The next message with no sessionToken starts a fresh
    /// session on its own (see ChatService.ProcessAsync), so this only has to mark the old one
    /// closed and confirm success.
    /// </summary>
    [HttpGet("ClearSession")]
    [HttpPost("ClearSession")]
    public async Task<ActionResult> ClearSession()
    {
        // Look for a chat session id anywhere the widget might plausibly put it. NOT the "Token"
        // header: that carries TAS's own per-login session GUID (see ApiKeyAuthenticationHandler),
        // which is a different thing entirely and must never be treated as one of our chat ids.
        var candidates = new[] { "sessionToken", "sessionId", "session", "chatSessionId" };
        Guid? sessionId = null;
        foreach (var name in candidates)
        {
            if (Request.Query.TryGetValue(name, out var qv) && Guid.TryParse(qv.FirstOrDefault(), out var fromQuery))
            {
                sessionId = fromQuery;
                break;
            }
            if (Request.Headers.TryGetValue(name, out var hv) && Guid.TryParse(hv.FirstOrDefault(), out var fromHeader))
            {
                sessionId = fromHeader;
                break;
            }
        }

        var closed = false;
        if (sessionId.HasValue)
        {
            // Hard time budget. Marking the old session closed is cosmetic — the next message
            // with no token starts a fresh session regardless — but an unreachable SQL Server
            // takes ~17s to fail its connection attempt (measured), which would leave the user
            // staring at a dead "New chat" button that long. A reset button must answer fast and
            // always; 2.5s is far more than a single-row update on the local network needs.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            try
            {
                var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId.Value, cts.Token);
                if (session != null)
                {
                    session.Status = "closed";
                    session.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(cts.Token);
                    closed = true;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("⚠️ [CLEAR-SESSION] Session not marked closed (DB slower than the 2.5s budget).");
            }
            catch (Exception ex)
            {
                // Best-effort, exactly like every other DB write on the chat path: the button must
                // still work when the database does not.
                Console.WriteLine($"⚠️ [CLEAR-SESSION] Session not marked closed (DB unavailable): {ex.Message}");
            }
        }

        _logger.LogInformation("[CLEAR-SESSION] requested session={SessionId} closed={Closed} method={Method}",
            sessionId?.ToString() ?? "-", closed, Request.Method);

        // sessionToken is returned empty on purpose: that is precisely what the widget should send
        // on its next message to have a brand-new session created for it server-side.
        return Ok(new
        {
            success = true,
            cleared = closed,
            sessionToken = "",
            sessionId = "",
        });
    }
}
