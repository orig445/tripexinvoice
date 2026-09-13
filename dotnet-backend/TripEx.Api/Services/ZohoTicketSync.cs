using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>One conversation's worth of identity, captured at request time so the background
/// worker does not have to go looking for it (the widget's customer details are not persisted).</summary>
public record ZohoSyncRequest(Guid SessionId, string? CustomerName, string? CompanyName, string? Email);

/// <summary>
/// Hand-off point between answering the customer and mirroring the conversation into Zoho Desk.
///
/// ChatService drops a session id here and returns immediately — it never waits, never retries
/// and never sees an error. Milo already takes 11-39 seconds to answer; a helpdesk write must
/// not add to that, and must certainly not be able to fail the reply.
///
/// The channel is bounded and DROPS THE OLDEST item when full rather than blocking the writer.
/// A dropped item is not lost work: the sync is driven by what our own chat_messages table says
/// is unsynced, so the next turn in that conversation picks up whatever the dropped one missed.
/// </summary>
public class ZohoTicketSyncQueue
{
    private readonly Channel<ZohoSyncRequest> _channel = Channel.CreateBounded<ZohoSyncRequest>(
        new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public ChannelReader<ZohoSyncRequest> Reader => _channel.Reader;

    /// <summary>Never blocks and never throws. Returns false if the item could not be queued.</summary>
    public bool Enqueue(ZohoSyncRequest request) => _channel.Writer.TryWrite(request);
}

/// <summary>
/// Drains the queue and mirrors each conversation into one Zoho Desk ticket.
///
/// SINGLE reader, on purpose and not as an oversight:
///   * Desk gives no control over comment order — comments sort by a server-assigned timestamp
///     that cannot be set by the client — so the only way to keep a transcript readable is to
///     post the turns one after another rather than in parallel.
///   * Desk's real ceiling is concurrent calls (15-25 by edition), not calls per minute. One
///     reader can never breach it.
///
/// The design is deliberately stateless about what it has already done: on every pass it asks
/// our own database which messages are not yet mirrored. That makes it self-healing — if Zoho
/// was unreachable, or the process restarted mid-queue, the next turn in that conversation
/// sends everything that was missed. There is no outbox table and nothing to replay by hand.
/// </summary>
public class ZohoTicketSyncWorker : BackgroundService
{
    private readonly ZohoTicketSyncQueue _queue;
    private readonly ZohoDeskService _zoho;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZohoTicketSyncWorker> _logger;

    public ZohoTicketSyncWorker(ZohoTicketSyncQueue queue, ZohoDeskService zoho,
        IServiceScopeFactory scopeFactory, ILogger<ZohoTicketSyncWorker> logger)
    {
        _queue = queue;
        _zoho = zoho;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_zoho.Options.IsConfigured)
        {
            // Nothing is ever enqueued in this state either — this is just the second half of
            // the same "do nothing at all when it is off" guarantee.
            _logger.LogInformation("[ZOHO-SYNC] Not configured — worker idle.");
            return;
        }

        _logger.LogInformation("[ZOHO-SYNC] Worker started.");

        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SyncOneAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad conversation must never take the worker down for every other one.
                _logger.LogError(ex, "[ZOHO-SYNC] session={SessionId} failed", request.SessionId);
            }
        }
    }

    private async Task SyncOneAsync(ZohoSyncRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();

        // The mapping table is created lazily and only on this path. Deliberately NOT a column on
        // chat_sessions: init-db.sql runs in a background task at startup, so a column added to
        // that entity would make EVERY chat_sessions query fail with "Invalid column name" until
        // the migration lands. Here the blast radius of a missing table is this feature alone.
        await SchemaGuard.EnsureChatSessionTicketsAsync(db);

        var map = await db.ChatSessionTickets
            .FirstOrDefaultAsync(t => t.SessionId == request.SessionId, ct);

        // Everything this conversation has said that Zoho has not been told about yet.
        var since = map?.SyncedThrough ?? DateTime.MinValue;
        var pending = await db.ChatMessages
            .Where(m => m.SessionId == request.SessionId && m.CreatedAt > since)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new TranscriptMessage(m.Role, m.Content, m.Intent, m.CreatedAt))
            .ToListAsync(ct);

        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
        var escalated = session?.Escalated ?? false;

        // Nothing new to say, and no status change to make.
        if (pending.Count == 0 && (map == null || !escalated || map.EscalationSynced)) return;

        // ── Token discipline from here down ──────────────────────────────────────────────────
        // The reads above take `ct` and may be abandoned freely. Everything below either has an
        // irreversible effect at Zoho or RECORDS one, so none of it may be cut short by an
        // app-pool recycle: a ticket that exists at Zoho but whose id we failed to write down
        // becomes a SECOND ticket on the next turn, and this row is the only thing standing
        // between us and that — Zoho has no idempotency key to fall back on.
        var batches = BuildTranscriptBatches(pending, ZohoDeskService.MaxCommentLength);

        if (map == null)
        {
            if (batches.Count == 0) return; // nothing to open a ticket about yet

            var firstQuestion = pending.FirstOrDefault(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;

            var draft = new ZohoTicketDraft(
                Subject: ZohoDeskService.Truncate(
                    OneLine(firstQuestion) is { Length: > 0 } s ? s : "Milo conversation", 200),
                Description: batches[0].Content,
                ContactLastName: FirstNonBlank(request.CustomerName, request.CompanyName)
                                 ?? _zoho.Options.FallbackContactLastName,
                ContactEmail: FirstNonBlank(request.Email) ?? _zoho.Options.FallbackContactEmail,
                SessionId: request.SessionId,
                Escalated: escalated);

            var ticketId = await _zoho.CreateTicketAsync(draft, CancellationToken.None);
            if (ticketId == null)
            {
                // Left unmapped on purpose: the next turn retries the create and carries these
                // same messages with it, because SyncedThrough was never advanced.
                _logger.LogWarning("[ZOHO-SYNC] session={SessionId} ticket not created — will retry on the next turn",
                    request.SessionId);
                return;
            }

            map = new ChatSessionTicket
            {
                SessionId = request.SessionId,
                ZohoTicketId = ticketId,
                SyncedThrough = batches[0].Through,
                // Creating an already-escalated ticket sets the right status up front, so there
                // is no follow-up PATCH to make.
                EscalationSynced = escalated,
            };
            db.ChatSessionTickets.Add(map);

            // Logged BEFORE the save, deliberately: if writing this row fails, this line is the
            // only remaining trace of a ticket that really does exist in Zoho.
            _logger.LogInformation("[ZOHO-SYNC] session={SessionId} → ticket={TicketId} ({Count} message(s), escalated={Escalated})",
                request.SessionId, ticketId, pending.Count, escalated);

            await db.SaveChangesAsync(CancellationToken.None);
            batches.RemoveAt(0);
        }

        // Whatever did not fit in the ticket body goes on as comments, one per batch, each one
        // advancing the watermark as it lands. Per batch rather than once at the end, so a
        // failure half-way through does not re-send the batches that already arrived.
        foreach (var batch in batches)
        {
            if (!await _zoho.AddCommentAsync(map.ZohoTicketId, batch.Content, CancellationToken.None))
            {
                _logger.LogWarning("[ZOHO-SYNC] session={SessionId} ticket={TicketId} comment failed — will retry on the next turn",
                    request.SessionId, map.ZohoTicketId);
                return;
            }

            map.SyncedThrough = batch.Through;
            map.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        if (pending.Count > 0)
            _logger.LogInformation("[ZOHO-SYNC] session={SessionId} ticket={TicketId} synced {Count} message(s)",
                request.SessionId, map.ZohoTicketId, pending.Count);

        // The conversation started as something Milo handled and has now been handed to a human:
        // reopen the ticket so it lands in the queue as real work. Done last, so a failure here
        // leaves the transcript already saved and only the status to retry.
        if (escalated && !map.EscalationSynced)
        {
            if (await _zoho.UpdateStatusAsync(map.ZohoTicketId, _zoho.Options.EscalatedStatus, CancellationToken.None))
            {
                map.EscalationSynced = true;
                map.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogInformation("[ZOHO-SYNC] session={SessionId} ticket={TicketId} reopened — escalated to a human",
                    request.SessionId, map.ZohoTicketId);
            }
            else
            {
                _logger.LogWarning("[ZOHO-SYNC] session={SessionId} ticket={TicketId} status not updated — will retry",
                    request.SessionId, map.ZohoTicketId);
            }
        }
    }

    /// <summary>One chat message, as the transcript builder sees it.</summary>
    public record TranscriptMessage(string Role, string Content, string? Intent, DateTime CreatedAt);

    /// <summary>A chunk of transcript that fits one Zoho write, and the point it syncs up to.</summary>
    public record TranscriptBatch(string Content, DateTime Through);

    /// <summary>
    /// Renders messages into chunks that each fit inside Zoho's per-write character limit.
    ///
    /// Splitting matters more than it looks. The usual batch is one question and one answer, but
    /// the FIRST sync after an outage carries the whole backlog, and nothing caps how much a
    /// customer can paste into a single message. Truncating to the limit and then advancing the
    /// watermark — which is what this did before — would drop those turns from the ticket
    /// permanently while reporting success. Each batch carries the timestamp it syncs up to, so
    /// the watermark moves one batch at a time and a half-delivered backlog never re-sends what
    /// already arrived.
    ///
    /// A single message too large to fit on its own is truncated and marked, because re-sending
    /// it would fail identically forever — and the untouched original always remains in
    /// chat_messages, which is the actual system of record.
    /// </summary>
    public static List<TranscriptBatch> BuildTranscriptBatches(
        IReadOnlyList<TranscriptMessage> messages, int maxChars)
    {
        var batches = new List<TranscriptBatch>();
        if (messages.Count == 0 || maxChars <= 0) return batches;

        var current = new StringBuilder();
        var through = default(DateTime);

        foreach (var message in messages)
        {
            var rendered = ZohoDeskService.Truncate(Render(message), maxChars);

            if (current.Length > 0 && current.Length + rendered.Length > maxChars)
            {
                batches.Add(new TranscriptBatch(current.ToString().TrimEnd(), through));
                current.Clear();
            }

            current.Append(rendered);
            through = message.CreatedAt;
        }

        if (current.Length > 0)
            batches.Add(new TranscriptBatch(current.ToString().TrimEnd(), through));

        return batches;
    }

    private static string Render(TranscriptMessage message)
    {
        var who = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Customer" : "Milo";
        var intent = string.IsNullOrWhiteSpace(message.Intent) ? "" : $" [{message.Intent}]";
        return $"── {who} · {message.CreatedAt:yyyy-MM-dd HH:mm} UTC{intent}\n{message.Content}\n\n";
    }

    /// <summary>First line only, whitespace collapsed — a ticket subject is one line, not a paragraph.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return collapsed.Trim();
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
