using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>
/// The way back. Milo already mirrors every conversation into one Zoho Desk ticket; this is what
/// closes the loop, so that when a human agent answers that ticket the customer reads the answer
/// in Milo's own widget instead of being handed an email address and told to start again.
///
/// The design decision worth stating up front: a webhook from Desk is treated as a SIGNAL ONLY.
/// It says "ticket 1234 has a new outgoing thread" and nothing it contains is believed. The reply
/// itself is then read back from Desk over our own authenticated connection. Three problems
/// dissolve at once — Desk requires the webhook endpoint to be publicly reachable with no
/// authentication at all, so its body is a claim rather than a fact; the body carries the reply
/// as a raw HTML email complete with signature, survey and quoted history; and it can arrive
/// truncated. Re-reading costs one API call per agent reply and answers all three.
/// </summary>
public class ZohoAgentReplyService
{
    /// <summary>
    /// The role an agent's message is stored under in chat_messages. Deliberately distinct from
    /// "assistant": the transcript has to be able to say which answers were Milo's and which were
    /// a person's, long after the fact — for the widget's own styling, and because a conversation
    /// where a human stepped in is the one worth reviewing.
    /// </summary>
    public const string AgentRole = "agent";

    /// <summary>Hard ceiling on a relayed reply, so one pasted document cannot fill a chat bubble.</summary>
    public const int MaxRelayedReplyLength = 4000;

    private readonly TripExDbContext _db;
    private readonly ZohoDeskService _zoho;
    private readonly ILogger<ZohoAgentReplyService> _logger;

    public ZohoAgentReplyService(
        TripExDbContext db, ZohoDeskService zoho, ILogger<ZohoAgentReplyService> logger)
    {
        _db = db;
        _zoho = zoho;
        _logger = logger;
    }

    /// <summary>What happened to one signalled ticket. Every outcome is a normal, expected one.</summary>
    public enum RelayOutcome
    {
        /// <summary>Stored and now visible to the customer.</summary>
        Delivered,
        /// <summary>This exact thread was already relayed. Desk retries, so this is routine.</summary>
        AlreadySeen,
        /// <summary>The ticket is not one of ours, or its conversation is gone.</summary>
        NotOurs,
        /// <summary>Nothing worth showing — an internal note, an inbound thread, or empty text.</summary>
        NothingToSay,
        /// <summary>Desk could not be read. The next signal, or the customer's next turn, retries.</summary>
        Unavailable,
    }

    /// <summary>
    /// Relays the latest public agent reply on one ticket into its conversation.
    ///
    /// Safe to call repeatedly for the same ticket: the thread id is recorded with the message and
    /// checked before inserting, which matters because Zoho's webhook retry policy is undocumented
    /// and a duplicate would appear to the customer as the agent saying the same thing twice.
    /// </summary>
    public async Task<RelayOutcome> RelayLatestReplyAsync(string ticketId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticketId)) return RelayOutcome.NotOurs;

        // Which conversation is this ticket? chat_session_tickets is the only mapping, and it is
        // written when the ticket is created, so a ticket with no row here was raised by somebody
        // else in the same department and is none of our business.
        var map = await _db.ChatSessionTickets
            .FirstOrDefaultAsync(t => t.ZohoTicketId == ticketId, ct);

        if (map == null)
        {
            _logger.LogInformation("[AGENT-REPLY] ticket={TicketId} is not a Milo conversation — ignored", ticketId);
            return RelayOutcome.NotOurs;
        }

        var reply = await _zoho.GetLatestPublicReplyAsync(ticketId, ct);
        if (reply == null)
        {
            // Either Desk was unreachable or the latest thread was not a public outgoing one.
            // Both are ordinary; neither is worth alarming about, and neither is retried here —
            // a failed read leaves the ticket unchanged, so the next signal sees the same thread.
            _logger.LogInformation("[AGENT-REPLY] ticket={TicketId} had no public agent reply to relay", ticketId);
            return RelayOutcome.Unavailable;
        }

        var threadId = reply.Value.ThreadId;

        // Dedupe on the thread id rather than on the text: an agent who deliberately sends the
        // same short line twice ("Any luck?") must not have the second one swallowed.
        var alreadyStored = await _db.ChatMessages.AnyAsync(
            m => m.SessionId == map.SessionId
                 && m.Role == AgentRole
                 && m.Metadata != null
                 && m.Metadata.Contains(threadId), ct);

        if (alreadyStored) return RelayOutcome.AlreadySeen;

        var text = ZohoDeskService.Truncate(reply.Value.Text, MaxRelayedReplyLength);
        if (string.IsNullOrWhiteSpace(text)) return RelayOutcome.NothingToSay;

        _db.ChatMessages.Add(new ChatMessage
        {
            SessionId = map.SessionId,
            Role = AgentRole,
            Content = text,
            // The thread id is the dedupe key and the audit trail back to Desk; the name is what
            // the widget can put on the bubble so the customer knows a person has taken over.
            Metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                zohoThreadId = threadId,
                zohoTicketId = ticketId,
                agentName = reply.Value.AuthorName,
            }),
        });

        // SyncedThrough is deliberately NOT touched here. Moving it to "now" would indeed stop
        // the mirror echoing this reply back into its own ticket, but it is a watermark — a
        // position in the conversation, not a flag — and every customer or Milo message written
        // before this instant and not yet mirrored would fall behind it and never be sent. A
        // customer whose question arrives while an agent is typing would simply vanish from the
        // ticket the agent is reading. The echo is stopped where it belongs instead, by excluding
        // the agent role from the mirror's own query (see ZohoTicketSync).
        map.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[AGENT-REPLY] ticket={TicketId} thread={ThreadId} session={SessionId} relayed {Chars} chars from {Agent}",
            ticketId, threadId, map.SessionId, text.Length, reply.Value.AuthorName ?? "an agent");

        return RelayOutcome.Delivered;
    }

    /// <summary>
    /// Agent messages in a conversation that the caller has not seen yet — the read side of the
    /// relay, which the widget polls.
    ///
    /// Strictly greater-than on the timestamp, so a client that passes back the last value it
    /// received never re-reads the same message; and only the agent role, because the widget
    /// already has its own turns and Milo's.
    /// </summary>
    public async Task<List<AgentMessage>> GetAgentMessagesSinceAsync(
        Guid sessionId, DateTime? afterUtc, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty) return new List<AgentMessage>();

        var since = afterUtc ?? DateTime.MinValue;

        var rows = await _db.ChatMessages
            .Where(m => m.SessionId == sessionId && m.Role == AgentRole && m.CreatedAt > since)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Content, m.CreatedAt, m.Metadata })
            .ToListAsync(ct);

        return rows.Select(r => new AgentMessage(r.Content, r.CreatedAt, ReadAgentName(r.Metadata))).ToList();
    }

    /// <summary>One relayed reply, shaped for the widget.</summary>
    public readonly record struct AgentMessage(string Text, DateTime CreatedAtUtc, string? AgentName);

    /// <summary>
    /// This conversation's customer-facing ticket number, or null if there is not one to show.
    ///
    /// Null has several ordinary causes — no ticket opened yet, a row written before the column
    /// existed, Zoho switched off — and none of them is worth failing a poll over. The caller
    /// treats it as "nothing to display", and a client that has already been given a number keeps
    /// showing it rather than clearing on a null.
    /// </summary>
    public async Task<string?> GetTicketNumberAsync(Guid sessionId, CancellationToken ct = default)
    {
        // The IsConfigured check is not just an optimisation. zoho_ticket_number is added to
        // chat_session_tickets by SchemaGuard, which only ever runs on the sync path — so on a
        // deployment where Zoho has never been switched on, the column does not exist and this
        // query would throw "Invalid column name". The catch below would swallow it, but the
        // widget polls every few seconds, so it would swallow it into a warning every few seconds
        // forever. With Zoho off there is no ticket to name anyway.
        if (sessionId == Guid.Empty || !_zoho.Options.IsConfigured) return null;

        try
        {
            return await _db.ChatSessionTickets
                .Where(t => t.SessionId == sessionId)
                .Select(t => t.ZohoTicketNumber)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[AGENT-REPLY] ticket number not read for session {SessionId}: {Message}",
                sessionId, ex.Message);
            return null;
        }
    }

    private static string? ReadAgentName(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadata);
            return doc.RootElement.TryGetProperty("agentName", out var n) ? n.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Metadata is written by us, but it is also the kind of column that accumulates
            // history. A name is decoration; failing to read one must never cost the message.
            return null;
        }
    }
}
