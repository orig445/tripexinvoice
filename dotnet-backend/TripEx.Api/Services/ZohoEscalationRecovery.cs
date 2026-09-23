using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>
/// Makes sure a conversation that asked for a human actually reaches one.
///
/// The mirror is driven by an in-memory queue that ChatService writes to at the end of a turn,
/// and every failure inside it logs "will retry on the next turn". That recovery story held while
/// escalating meant printing an email address: the customer had somewhere else to go, and if they
/// came back and typed again, the next turn retried the sync.
///
/// It stopped holding the moment escalation started saying "stay here, their reply will arrive in
/// this chat". That sentence removes the next turn — which was the only retry trigger — and the
/// fallback at the same time. So a single Zoho 500 on the transcript comment, or an app-pool
/// recycle in the seconds between the enqueue and the worker draining it (the queue is in memory
/// and its contents simply vanish), leaves the ticket sitting at Closed/Low where nobody is
/// looking, while the customer waits in a chat window for a person who was never told.
///
/// Nothing errors in that state. That is what makes it worth a background sweep: the evidence is
/// already in the database — chat_sessions.escalated is true and chat_session_tickets says the
/// escalation was never pushed — so the fix is to read it back and try again, rather than to hope
/// the customer disobeys the instruction they were just given.
/// </summary>
public class ZohoEscalationRecoveryWorker : BackgroundService
{
    /// <summary>
    /// How often to look. Minutes rather than seconds because this is a safety net, not the
    /// primary path: the happy case is already handled by the enqueue on the escalating turn, and
    /// this only ever finds the cases that failed.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A short wait before the first sweep. An app-pool recycle is the single likeliest way to
    /// lose a queued escalation, so the sweep that runs just after startup is the most valuable
    /// one — but it must not race the rest of the app coming up.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How far back to look. An escalation nobody acted on for a week is not something to push
    /// into a support queue now; it also stops the query growing without bound as the table does.
    /// </summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(7);

    /// <summary>Most sessions to re-enqueue in one sweep, so a bad day cannot flood the queue.</summary>
    private const int BatchLimit = 50;

    private readonly ZohoTicketSyncQueue _queue;
    private readonly ZohoDeskService _zoho;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZohoEscalationRecoveryWorker> _logger;

    public ZohoEscalationRecoveryWorker(
        ZohoTicketSyncQueue queue,
        ZohoDeskService zoho,
        IServiceScopeFactory scopeFactory,
        ILogger<ZohoEscalationRecoveryWorker> logger)
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
            _logger.LogInformation("[ZOHO-RECOVERY] Not configured — sweeper idle.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("[ZOHO-RECOVERY] Sweeper started — checking every {Minutes} minutes for " +
                               "escalations that never reached the helpdesk.", SweepInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A sweeper that dies is worse than one that fails a round: the next round is
                // what recovers whatever this one could not.
                _logger.LogError(ex, "[ZOHO-RECOVERY] sweep failed — will try again next interval");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Finds escalated conversations whose escalation never made it to Zoho and re-enqueues them.
    ///
    /// "Never made it" is deliberately two different situations, both of which leave a customer
    /// waiting: the ticket was never created at all (no mapping row), or it exists but the status
    /// and priority push never landed (EscalationSynced false). Re-enqueuing covers both, because
    /// SyncOneAsync creates the ticket when the mapping is missing and pushes the escalation when
    /// it is not yet synced — it is already idempotent, which is what makes this safe to repeat.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();

        var cutoff = DateTime.UtcNow - LookBack;

        // Left join in LINQ: sessions that escalated, minus the ones already fully pushed.
        var stuck = await (
            from s in db.ChatSessions
            where s.Escalated && s.UpdatedAt >= cutoff
            join t in db.ChatSessionTickets on s.Id equals t.SessionId into map
            from t in map.DefaultIfEmpty()
            where t == null || !t.EscalationSynced
            orderby s.UpdatedAt
            select s.Id).Take(BatchLimit).ToListAsync(ct);

        if (stuck.Count == 0) return;

        var queued = 0;
        foreach (var sessionId in stuck)
        {
            // Null identity on purpose. The customer's name and email were only ever carried to
            // create the Desk contact, and ZohoDeskOptions already has a fallback for exactly
            // that; the transcript itself is read from the database, so nothing is lost by not
            // having them here. Inventing values would be worse than using the documented default.
            if (_queue.Enqueue(new ZohoSyncRequest(sessionId, null, null, null))) queued++;
        }

        // Logged at warning because every line here is a customer who was told a human was coming
        // and, until this moment, was wrong. If this is not silent in production, something in the
        // primary path is failing and deserves looking at rather than being quietly papered over.
        _logger.LogWarning(
            "[ZOHO-RECOVERY] re-queued {Queued} of {Found} escalation(s) that never reached the helpdesk",
            queued, stuck.Count);
    }
}
