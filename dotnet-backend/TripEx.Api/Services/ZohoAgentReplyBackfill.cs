using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;

namespace TripEx.Api.Services;

/// <summary>
/// Finds agent replies whose webhook never arrived, and relays them.
///
/// The relay has exactly one way in: Desk POSTs Ticket_Thread_Add and ZohoWebhookController hands
/// the thread to ZohoAgentReplyService. Nothing else ever reads an agent's reply back. So if that
/// one HTTP call is lost — the API is mid-publish (every publish recycles the app pool), a 5xx, a
/// timeout, Desk quietly disabling a subscription it thinks is failing — the reply exists in Desk
/// and nowhere else. Nothing errors and nothing retries, and the customer, who was told "stay here,
/// the agent's reply will arrive in this chat", sits watching a conversation nobody answered while
/// the agent believes they did.
///
/// This sweep closes that gap from the other end: for each conversation a person is already
/// answering, it lists the ticket's threads, picks out the agent's public replies that are not in
/// chat_messages, and relays them through RelayReplyAsync — the webhook's own entry point. A
/// recovered reply therefore passes the very same authenticated re-read, the same direction and
/// visibility checks, the same TidyReply and the same thread-id dedupe as one that arrived on time.
/// This class only decides WHICH threads to ask about, and it is stricter about that than the
/// webhook is (see Classify): when Desk's fields are not what was expected, the worst case is
/// "recovers nothing", never "shows the wrong text".
///
/// Three stages, from Zoho:AgentReplyBackfill. Off is the default and does nothing at all. LogOnly
/// runs the whole sweep and logs what it WOULD relay without storing anything — it exists because
/// the shape of Desk's thread list could not be inspected when this was written, and the filters
/// should be seen matching real threads before any recovered text reaches a customer. On relays.
/// All three sit behind the relay's own switch: with AgentRelayEnabled off this never runs.
/// </summary>
public class ZohoAgentReplyBackfillWorker : BackgroundService
{
    /// <summary>
    /// A short wait before the first sweep, staggered after the escalation recovery's 45 seconds so
    /// the two safety nets do not both hit the database and Desk in the same moment a recycled app
    /// pool is coming up.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often to look. Ten minutes because this is a net under the webhook, not a second way in:
    /// the webhook delivers in seconds and this only ever finds what it dropped. It is also the
    /// dominant cost — one Desk list call per handed-over conversation per sweep — so halving it
    /// doubles the API spend for replies that are, at most, a few minutes less late.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Which conversations are worth listing: ones with any activity in the last day. An agent who
    /// is actively answering keeps a ticket inside this window by answering (every relayed reply
    /// moves chat_session_tickets.updated_at), so this bounds the cost without cutting off a
    /// conversation that is still going.
    /// </summary>
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The oldest thread worth recovering. A reply a day late is still worth showing; one from last
    /// week, surfacing out of nowhere, would read to the customer as a message from the past — and
    /// the window also keeps a relay switched on today from dredging up everything written while it
    /// was off.
    /// </summary>
    private static readonly TimeSpan ThreadMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// The youngest thread the sweep will touch. The webhook normally lands within seconds, and any
    /// retry Desk makes lands soon after; five minutes gives both the first chance. That keeps the
    /// sweep out of the webhook's way — the store lock stops a double insert, but not a wasted Desk
    /// read for a reply that was about to arrive anyway.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Most tickets to list in one sweep, newest activity first. The hard ceiling on the API budget:
    /// 25 list calls × 144 sweeps a day = 3,600. Hitting it is logged, because on that day the
    /// oldest candidates are the ones not being looked at.
    /// </summary>
    private const int MaxTicketsPerSweep = 25;

    /// <summary>Most replies to fetch and relay in one sweep, so a bad day upstream cannot turn one
    /// sweep into a burst of Desk reads — whatever is left is still there ten minutes later.</summary>
    private const int MaxRelaysPerSweep = 20;

    /// <summary>
    /// How many times one thread is tried before the sweep leaves it alone. A thread that keeps
    /// coming back NothingToSay or Unavailable is usually one the relay deliberately refuses (the
    /// named-thread read found it private, or TidyReply left nothing), and asking every ten minutes
    /// for a day would only burn API credits on the same answer.
    /// </summary>
    private const int MaxAttemptsPerThread = 3;

    /// <summary>
    /// Consecutive failed list calls after which a sweep gives up. Three in a row means Desk is
    /// down or refusing our token, and the remaining tickets would only add the same failure to the
    /// log and the same calls to the bill. The next sweep starts afresh.
    /// </summary>
    private const int AbortAfterConsecutiveFailures = 3;

    /// <summary>How long a thread's attempt count is remembered. Twice ThreadMaxAge, so a thread is
    /// always older than the age filter before its count is forgotten and could be tried again.</summary>
    private static readonly TimeSpan AttemptMemory = TimeSpan.FromHours(48);

    private readonly ZohoDeskService _zoho;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZohoAgentReplyBackfillWorker> _logger;

    /// <summary>
    /// Attempts per Desk thread id, kept in memory only. Losing it on a recycle costs at most
    /// MaxAttemptsPerThread more reads of a thread that was failing anyway, which is not worth a
    /// table. No lock: ExecuteAsync is the only reader and writer, and it runs one sweep at a time.
    /// </summary>
    private readonly Dictionary<string, (int Attempts, DateTime FirstSeenUtc)> _attempts = new();

    // LogOnly: threads already reported with a "would relay" line, so a reply the webhook never
    // delivered is named once rather than every sweep until it ages out. The summary still
    // counts it each time. Pruned with the attempt memory.
    private readonly Dictionary<string, DateTime> _reportedWouldRelay = new();

    public ZohoAgentReplyBackfillWorker(
        ZohoDeskService zoho,
        IServiceScopeFactory scopeFactory,
        ILogger<ZohoAgentReplyBackfillWorker> logger)
    {
        // Nothing here may throw: AddHostedService resolves this on the startup path, and a
        // constructor failure would take the whole API down with it.
        _zoho = zoho;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _zoho.Options;
        if (!options.IsBackfillActive)
        {
            _logger.LogInformation("[ZOHO-BACKFILL] off — idle (relay={Relay}, mode={Mode})",
                options.IsRelayConfigured, options.BackfillMode);
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "[ZOHO-BACKFILL] started mode={Mode} every {Minutes} min for handed-over tickets active in the last {Hours} h{Note}",
            options.BackfillMode, SweepInterval.TotalMinutes, ActivityWindow.TotalHours,
            options.BackfillMode == AgentReplyBackfillMode.LogOnly ? " — LogOnly: nothing will be shown to customers" : "");

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
                _logger.LogError(ex, "[ZOHO-BACKFILL] sweep failed — will try again next interval");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One conversation worth listing, and the Desk ticket that mirrors it.</summary>
    public readonly record struct BackfillCandidate(Guid SessionId, string TicketId);

    /// <summary>
    /// The conversations the sweep lists threads for. A static builder so a test can prove it
    /// translates to SQL without a database.
    ///
    /// Only conversations where a person is ALREADY involved — escalated, or holding an agent row —
    /// which is the same test as ChatService.HumanInvolvedQuery. That is a safety property and not
    /// just a cost saving: a relayed agent row is what puts a conversation into handed-over mode and
    /// makes Milo go quiet, so if this looked at conversations Milo was answering alone, recovering a
    /// reply could switch Milo off in one of them. Here, the conversation was handed over before the
    /// sweep ever touched it.
    ///
    /// Active in the last day on EITHER side: chat_sessions.updated_at moves with the customer's own
    /// turns, and chat_session_tickets.updated_at with every sync and every relayed reply, so an
    /// agent still answering keeps the ticket in the window even if the customer has gone quiet.
    ///
    /// Not "internal", matching ChatService.IsMirroredSource — staff chat never gets a ticket, and
    /// this says so rather than relying on it. SQL Server's default collation compares
    /// case-insensitively, which matches IsMirroredSource.
    ///
    /// Newest first, so on a day that hits MaxTicketsPerSweep the conversations being lost are the
    /// ones longest idle.
    /// </summary>
    public static IQueryable<BackfillCandidate> CandidateQuery(TripExDbContext db, DateTime nowUtc)
    {
        var cutoff = nowUtc - ActivityWindow;

        return
            from t in db.ChatSessionTickets
            join s in db.ChatSessions on t.SessionId equals s.Id
            where s.Source != "internal"
                  && t.ZohoTicketId != ""
                  && (s.UpdatedAt >= cutoff || t.UpdatedAt >= cutoff)
                  && (s.Escalated
                      || db.ChatMessages.Any(m => m.SessionId == s.Id
                                                  && m.Role == ZohoAgentReplyService.AgentRole))
            orderby s.UpdatedAt descending
            select new BackfillCandidate(s.Id, t.ZohoTicketId);
    }

    /// <summary>Why a listed thread was, or was not, worth asking about. Each skip is counted by
    /// reason in the sweep summary, which is how LogOnly shows whether the filters are sane.</summary>
    public enum BackfillVerdict
    {
        Eligible,
        NotOutgoing,
        NotPublic,
        Description,
        Forward,
        NotAgent,
        Draft,
        NoTime,
        TooNew,
        TooOld,
    }

    /// <summary>
    /// Is this thread an agent's public reply that the sweep should ask the relay about?
    ///
    /// Every rule fails closed — a missing field is a reason to skip, never to pass — and every rule
    /// is at least as strict as the webhook's own pre-filter, which lets a missing direction or
    /// visibility through to the read. That asymmetry is deliberate. The webhook is told about a
    /// specific thread by Desk the moment it is written; this reads a list and picks threads out of
    /// it on its own judgement, so it has to be the more careful of the two.
    ///
    ///   out        — "in" is the customer's own message; relaying it shows them their own words.
    ///   public     — an internal note is written for colleagues. The most damaging thing to relay.
    ///   not the description, not a forward — neither is a reply to this customer.
    ///   AGENT      — Desk's auto-acknowledgements, bots and system threads are outgoing and public
    ///                too; only a person's reply belongs in the chat.
    ///   not DRAFT  — an agent's unsent draft is not a reply yet.
    ///   5 min–24 h — younger is the webhook's to deliver; older is not worth surfacing now.
    ///
    /// Milo's own transcript never appears here: it goes to Desk as private comments, which are not
    /// threads at all.
    /// </summary>
    public static BackfillVerdict Classify(ZohoDeskService.ThreadSummary t, DateTime nowUtc)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        if (!string.Equals(t.Direction, "out", ci)) return BackfillVerdict.NotOutgoing;
        if (!string.Equals(t.Visibility, "public", ci)) return BackfillVerdict.NotPublic;
        if (t.IsDescriptionThread) return BackfillVerdict.Description;
        if (t.IsForward) return BackfillVerdict.Forward;
        if (!string.Equals(t.AuthorType, "AGENT", ci)) return BackfillVerdict.NotAgent;
        if (string.Equals(t.Status, "DRAFT", ci)) return BackfillVerdict.Draft;
        if (t.CreatedTimeUtc == null) return BackfillVerdict.NoTime;
        if (t.CreatedTimeUtc.Value > nowUtc - SettleDelay) return BackfillVerdict.TooNew;
        if (t.CreatedTimeUtc.Value < nowUtc - ThreadMaxAge) return BackfillVerdict.TooOld;
        return BackfillVerdict.Eligible;
    }

    /// <summary>
    /// One pass: list each candidate's threads, and relay (or, in LogOnly, report) every eligible
    /// one that is not already stored.
    ///
    /// Sequential on purpose — one Desk call at a time, so the sweep adds no concurrency pressure to
    /// the API budget the live chat path shares, and so the in-process store lock is only ever
    /// contended by the webhook.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        var mode = _zoho.Options.BackfillMode;
        var now = DateTime.UtcNow;

        foreach (var stale in _attempts.Where(a => now - a.Value.FirstSeenUtc > AttemptMemory)
                     .Select(a => a.Key).ToList())
            _attempts.Remove(stale);
        foreach (var stale in _reportedWouldRelay.Where(r => now - r.Value > AttemptMemory)
                     .Select(r => r.Key).ToList())
            _reportedWouldRelay.Remove(stale);

        using var scope = _scopeFactory.CreateScope();

        // From the SAME scope, so both share one DbContext: the sweep's "already stored?" question
        // and the relay's insert then read the same connection's view of chat_messages, and a failed
        // relay's half-added row can be cleared from the one change tracker it lives in (below).
        var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();
        var relay = scope.ServiceProvider.GetRequiredService<ZohoAgentReplyService>();

        // Defensive only. The sync path creates the table the first time Zoho runs, and this cannot
        // be the first time — the relay is on — but a sweep that errors every ten minutes on a
        // missing table would be a poor way to find that out.
        await SchemaGuard.EnsureChatSessionTicketsAsync(db);

        var candidates = await CandidateQuery(db, now).Take(MaxTicketsPerSweep + 1).ToListAsync(ct);

        // Silent when there is nothing to look at, like the escalation recovery: an idle night
        // should not fill the log with a line every ten minutes saying so.
        if (candidates.Count == 0) return;

        if (candidates.Count > MaxTicketsPerSweep)
        {
            _logger.LogWarning(
                "[ZOHO-BACKFILL] ticket cap hit — more than {Cap} handed-over tickets active in the last {Hours} h; " +
                "only the most recent are checked this sweep", MaxTicketsPerSweep, ActivityWindow.TotalHours);
            candidates = candidates.Take(MaxTicketsPerSweep).ToList();
        }

        var skipped = new Dictionary<BackfillVerdict, int>();
        int tickets = 0, threads = 0, eligible = 0, stored = 0, delivered = 0, would = 0, gaveUp = 0, refused = 0;
        var relayCalls = 0;
        var consecutiveFailures = 0;
        var stop = false;

        foreach (var c in candidates)
        {
            var (list, readOutcome) = await _zoho.ListThreadsAsync(c.TicketId, ct);
            if (list == null && readOutcome == ZohoDeskService.ZohoCallOutcome.Rejected)
            {
                // A refusal is about this ticket alone — deleted, merged, or moved where the token
                // cannot read it — so it neither counts toward "Desk is down" nor resets that
                // count. Not counted as a ticket checked; GetAsync has already logged the status.
                refused++;
                continue;
            }
            if (list == null)
            {
                // Not counted as a ticket checked. GetAsync has already logged the status.
                if (++consecutiveFailures >= AbortAfterConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "[ZOHO-BACKFILL] Desk unreachable — sweep abandoned after {Failures} failed reads",
                        consecutiveFailures);
                    break;
                }
                continue;
            }

            consecutiveFailures = 0;
            tickets++;
            threads += list.Count;

            foreach (var t in list)
            {
                var verdict = Classify(t, now);
                if (verdict != BackfillVerdict.Eligible)
                {
                    skipped[verdict] = skipped.GetValueOrDefault(verdict) + 1;
                    continue;
                }

                eligible++;

                if (_attempts.TryGetValue(t.Id, out var tried) && tried.Attempts >= MaxAttemptsPerThread)
                {
                    gaveUp++;
                    continue;
                }

                // Asked before the relay is, so a reply the webhook delivered — the normal case, and
                // nearly every eligible thread — costs a database lookup and no Desk read at all.
                if (await relay.IsThreadStoredAsync(c.SessionId, t.Id, ct))
                {
                    stored++;
                    continue;
                }

                var ageMinutes = (int)Math.Round((now - t.CreatedTimeUtc!.Value).TotalMinutes);

                if (mode == AgentReplyBackfillMode.LogOnly)
                {
                    // Ids, age and channel only. Never the reply's text: this runs before anyone has
                    // confirmed the filters pick the right threads, which is exactly when a log line
                    // carrying an internal note would be worst.
                    if (_reportedWouldRelay.TryAdd(t.Id, now))
                        _logger.LogInformation(
                            "[ZOHO-BACKFILL] would relay ticket={TicketId} thread={ThreadId} session={SessionId} " +
                            "age={Minutes}m channel={Channel}",
                            c.TicketId, t.Id, c.SessionId, ageMinutes, t.Channel ?? "?");
                    would++;
                    continue;
                }

                if (relayCalls >= MaxRelaysPerSweep)
                {
                    _logger.LogWarning(
                        "[ZOHO-BACKFILL] {Cap} replies relayed this sweep — the rest wait for the next one",
                        MaxRelaysPerSweep);
                    stop = true;
                    break;
                }

                relayCalls++;
                ZohoAgentReplyService.RelayOutcome outcome;
                try
                {
                    // CancellationToken.None for the webhook's reason: a relay abandoned half way
                    // because the host is stopping would drop a reply that was already fetched.
                    outcome = await relay.RelayReplyAsync(c.TicketId, t.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // One thread must not cost the rest of the sweep. The relay may have added its
                    // row before SaveChanges threw; that entity is still in the shared change
                    // tracker and the NEXT relay's save would quietly try to insert it again, so
                    // it is cleared here.
                    db.ChangeTracker.Clear();
                    _logger.LogError(ex, "[ZOHO-BACKFILL] relaying ticket {TicketId} thread {ThreadId} threw",
                        c.TicketId, t.Id);
                    CountAttempt(t.Id, now);
                    continue;
                }

                if (outcome == ZohoAgentReplyService.RelayOutcome.Delivered)
                {
                    delivered++;

                    // Warning, like the escalation recovery: every line here is a customer who was
                    // waiting on a reply that had already been written. If these are not rare, the
                    // webhook itself is failing and deserves looking at.
                    _logger.LogWarning(
                        "[ZOHO-BACKFILL] recovered ticket={TicketId} thread={ThreadId} session={SessionId} " +
                        "age={Minutes}m — the webhook never delivered it",
                        c.TicketId, t.Id, c.SessionId, ageMinutes);
                }
                else if (outcome != ZohoAgentReplyService.RelayOutcome.AlreadySeen)
                {
                    CountAttempt(t.Id, now);
                }
            }

            if (stop) break;
        }

        const string summary =
            "[ZOHO-BACKFILL] tickets={Tickets} threads={Threads} eligible={Eligible} stored={Stored} " +
            "relayed={Relayed} would={Would} gaveUp={GaveUp} refused={Refused} skipped: in={In} private={Private} desc={Desc} " +
            "fwd={Fwd} notAgent={NotAgent} draft={Draft} noTime={NoTime} new={New} old={Old}";
        object[] args =
        {
            tickets, threads, eligible, stored, delivered, would, gaveUp, refused,
            skipped.GetValueOrDefault(BackfillVerdict.NotOutgoing),
            skipped.GetValueOrDefault(BackfillVerdict.NotPublic),
            skipped.GetValueOrDefault(BackfillVerdict.Description),
            skipped.GetValueOrDefault(BackfillVerdict.Forward),
            skipped.GetValueOrDefault(BackfillVerdict.NotAgent),
            skipped.GetValueOrDefault(BackfillVerdict.Draft),
            skipped.GetValueOrDefault(BackfillVerdict.NoTime),
            skipped.GetValueOrDefault(BackfillVerdict.TooNew),
            skipped.GetValueOrDefault(BackfillVerdict.TooOld),
        };

        // LogOnly always reports, because the counts ARE the point of that stage — they are how the
        // filters get checked against real threads. On mode reports only when there was something to
        // weigh, so a steady state of "nothing eligible" stays out of the log.
        if (delivered > 0)
            _logger.LogWarning(summary, args);
        else if (mode == AgentReplyBackfillMode.LogOnly || eligible > 0)
            _logger.LogInformation(summary, args);
    }

    private void CountAttempt(string threadId, DateTime nowUtc)
    {
        _attempts[threadId] = _attempts.TryGetValue(threadId, out var prev)
            ? (prev.Attempts + 1, prev.FirstSeenUtc)
            : (1, nowUtc);
    }
}
