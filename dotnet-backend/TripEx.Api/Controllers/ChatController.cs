using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    // The identity GET /updates is checked against. Shared with the widget — the names are the
    // contract, so they are spelled out once here.
    public const string EmailHeader = "X-Milo-Email";
    public const string CustomerIdHeader = "X-Milo-Customer-Id";
    public const string InstanceHeader = "X-Milo-Instance";

    private readonly ChatService _chatService;
    private readonly ZohoAgentReplyService _agentReplies;

    public ChatController(ChatService chatService, ZohoAgentReplyService agentReplies)
    {
        _chatService = chatService;
        _agentReplies = agentReplies;
    }

    /// <summary>
    /// Has a human answered since I last looked?
    ///
    /// Milo's chat has always been strictly request/response: the widget asks, the widget is
    /// answered, nothing else ever arrives. A reply written by a support agent in Zoho Desk is
    /// the first message that appears without the customer having asked for it, so the widget
    /// needs somewhere to look. This is that place, and it is deliberately a poll rather than a
    /// socket — the widget opens no sockets today, and a conversation waiting on a person is
    /// measured in minutes, not milliseconds.
    ///
    /// `since` is the CreatedAtUtc of the last agent message the caller already has; pass it back
    /// unchanged and nothing repeats. Omit it and the whole conversation's agent messages come
    /// back, which is what a reloaded page needs.
    ///
    /// Each createdAt is UTC and ends in "Z" (see FormatCursor). `since` accepts that value, the
    /// older Z-less values that widgets already hold in localStorage from before the Z was added
    /// (read as the same UTC instant, tick for tick, so nothing repeats or is skipped across the
    /// deploy), or a value with an explicit offset, which is converted to UTC.
    ///
    /// Keep `since` a model-bound DateTime?. ASP.NET's DateTime binder parses with
    /// AdjustToUniversal, so a "Z" value stays in UTC. DateTime.Parse without styles,
    /// Convert.ToDateTime or a TypeConverter all move a "Z" value into server-local time instead —
    /// two or three hours later on a server set to Israel time — and the `CreatedAt > since` filter
    /// then silently skips every agent reply written in that gap. Nothing errors; the customer just
    /// never sees them.
    ///
    /// Returns an empty list — never 404 — for a token that resolves to nothing. Whether a given
    /// conversation exists is not something an unrelated caller should be able to find out.
    ///
    /// The same empty answer goes to a caller the conversation's owner rule says is someone else.
    /// Who is asking comes from the X-Milo-Email / X-Milo-Customer-Id / X-Milo-Instance headers (a
    /// GET has no body to carry the identity a message carries); see
    /// ChatService.ResolveOwnedSessionAsync for what happens when they are missing.
    /// </summary>
    [HttpGet("updates")]
    public async Task<ActionResult> Updates(
        [FromQuery] string? sessionToken, [FromQuery] DateTime? since)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var caller = SessionOwner.FromHeaders(
            Request.Headers[EmailHeader].FirstOrDefault(),
            Request.Headers[CustomerIdHeader].FirstOrDefault(),
            Request.Headers[InstanceHeader].FirstOrDefault());
        var sessionId = await _chatService.ResolveOwnedSessionAsync(sessionToken, userId.Value, caller);
        if (sessionId == Guid.Empty) return Ok(new { messages = Array.Empty<object>() });

        var messages = await _agentReplies.GetAgentMessagesSinceAsync(sessionId, since, HttpContext.RequestAborted);

        // Returned on every poll, not only when there are messages. The ticket is opened by a
        // background worker, so a conversation that escalated on its very first turn had no number
        // to quote in the reply itself; this is where the widget picks it up a few seconds later,
        // without the customer having to send anything to trigger it.
        var ticketNumber = await _agentReplies.GetTicketNumberAsync(sessionId, HttpContext.RequestAborted);

        return Ok(new
        {
            ticketNumber,
            messages = messages.Select(m => new
            {
                text = m.Text,
                agentName = m.AgentName,
                // Round-trip format ("o"), and the precision is the whole point rather than a
                // detail. This value comes straight back as the next `since`, and the filter is
                // CreatedAt > since. created_at is datetime2(7) holding all 7 fractional digits,
                // so emitting only 3 — as "yyyy-MM-ddTHH:mm:ss.fffZ" does, truncating rather than
                // rounding — hands the client a cursor that sits BEFORE the row it is meant to
                // mark. The row then matches again on the next poll, and the one after, and the
                // cursor never advances past it: the customer watches the agent's reply reappear
                // every few seconds forever. "o" keeps all 7 digits, binds back exactly, and is
                // parsed fine by both the ASP.NET binder and JS Date.
                //
                // The value must also carry "Z": EF reads datetime2 back as Kind=Unspecified,
                // "o" on an Unspecified value writes no designator, and without one the browser
                // reads the string as local time — the agent's bubble showed 2-3 hours early.
                createdAt = FormatCursor(m.CreatedAtUtc),
            }),
        });
    }

    /// <summary>
    /// created_at as the widget receives it: UTC, marked with "Z", all 7 fractional digits.
    ///
    /// EF reads datetime2 back as Kind=Unspecified, and "o" on an Unspecified value writes no
    /// designator, so JS Date read it as local time and the bubble showed 2-3 hours early. The
    /// column only ever holds DateTime.UtcNow, so SpecifyKind is a relabel, not a conversion: the
    /// ticks, and therefore the 7 digits, are exactly the ones emitted before the fix, which is
    /// what lets a widget holding an old Z-less cursor carry on without skipping or repeating a
    /// reply. A Local value is converted rather than relabelled, because relabelling it would move
    /// the instant.
    ///
    /// Idempotent — a value that is already Utc comes out unchanged — so it is safe whether or not
    /// the caller has already marked the Kind. Public, not private, because the test project has no
    /// InternalsVisibleTo and this is the format the whole cursor contract hangs on.
    /// </summary>
    public static string FormatCursor(DateTime storedUtc) =>
        (storedUtc.Kind == DateTimeKind.Local
            ? storedUtc.ToUniversalTime()
            : DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc))
        .ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Main chat endpoint — handles text messages and image scanning
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0]?.Trim();

        var userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "user";

        try
        {
            var response = await _chatService.ProcessAsync(request, userId.Value, ipAddress, userRole);
            return Ok(response);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return StatusCode(429, new { error = "Rate limit exceeded" });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Chat error: {ex}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// List support tickets (= chat sessions) for review and learning. Admin only.
    /// Use ?escalatedOnly=true to see only tickets Milo handed to a human.
    /// </summary>
    [HttpGet("tickets")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult> ListTickets([FromQuery] bool escalatedOnly = false, [FromQuery] int take = 100)
    {
        var tickets = await _chatService.ListTicketsAsync(escalatedOnly, Math.Clamp(take, 1, 500));
        return Ok(tickets);
    }

    // Stable id used for server-to-server (X-Api-Key) callers such as TAS, which
    // authenticate as a system principal without a real per-user GUID.
    private static readonly Guid ApiKeySystemUserId = new("00000000-0000-0000-0000-000000000001");

    private Guid? GetUserId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var id)) return id;

        // X-Api-Key auth sets NameIdentifier = "api-key-user" (not a GUID). Map that
        // to a stable system user id so the chat endpoint works for TAS, instead of
        // rejecting it with 401 (this is why OCR worked via API key but chat did not).
        if (User.FindFirst("auth_type")?.Value == "api_key")
            return ApiKeySystemUserId;

        return null;
    }
}
