using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TripEx.Api.Services;

/// <summary>
/// Configuration for mirroring Milo conversations into Zoho Desk. Lives under "Zoho" in
/// appsettings.Production.json (git-ignored) — see appsettings.Production.example.json.
///
/// Enabled=false is the whole feature's off switch and the default: with it off nothing is
/// queued, no scope is created, no table is touched and no HTTP call is made. Flipping it back
/// to false is the instant rollback, exactly like Oracle:UseCustomModel.
/// </summary>
public class ZohoDeskOptions
{
    public bool Enabled { get; set; }

    // ── OAuth (Self Client + refresh token; see the example config for how to mint one) ──
    // The refresh token does not expire, so this is a one-time manual step. Access tokens last
    // one hour and are refreshed here automatically.
    public string AccountsBaseUrl { get; set; } = "https://accounts.zoho.com";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    // ── Desk ──
    // Zoho Desk lives on a DIFFERENT host per data centre and the API base is NOT the generic
    // api_domain the token response returns (that one is www.zohoapis.com). US: desk.zoho.com,
    // EU: desk.zoho.eu, IN: desk.zoho.in, AU: desk.zoho.com.au. Wrong host = 401/404 on every
    // call, so this is explicit config rather than anything guessed.
    public string ApiBaseUrl { get; set; } = "https://desk.zoho.com";
    public string OrgId { get; set; } = "";
    public string DepartmentId { get; set; } = "";

    // ── How an AI-handled ticket is marked, so it never mixes with human-handled work ──
    // The department is the real separation (its own queue, its own agents); these are the
    // in-ticket markers that make it filterable and reportable.
    public string Classification { get; set; } = "AI Answer";
    public string Channel { get; set; } = "Chat";
    public string SubjectPrefix { get; set; } = "[Milo] ";
    /// <summary>Status for a conversation Milo handled alone — closed on creation, so it is a
    /// record and not work sitting in someone's queue.</summary>
    public string AiHandledStatus { get; set; } = "Closed";
    /// <summary>Status once the conversation escalates: this one IS work for a human.</summary>
    public string EscalatedStatus { get; set; } = "Open";

    /// <summary>
    /// Priority for a conversation Milo handled alone. The point is sorting, not severity: a
    /// ticket nobody has to read should never sit above one that someone is waiting on, so an
    /// agent can order the queue by priority and see the real work first without filtering
    /// anything out. Set to "" to leave the field off the payload entirely — which is also the
    /// escape hatch if this Desk portal has been given a custom priority list that has no "Low".
    /// </summary>
    public string AiHandledPriority { get; set; } = "Low";
    /// <summary>
    /// Priority the ticket is raised to the moment the conversation escalates — the other half
    /// of the pair above, and the only thing that separates "Milo answered this" from "a person
    /// is waiting". Raised once and never lowered again: a conversation that needed a human
    /// still needed one even if the turns after it went fine. "" leaves the field untouched.
    ///
    /// Also applied, once, when a customer writes to an agent on a ticket Milo handled alone (an
    /// agent replied from Desk, so no escalation ever ran) and that ticket is still Closed at
    /// AiHandledPriority — the reopen carries it in the same PATCH. A ticket in any other state, or
    /// at any other priority, is the agent's and keeps what they gave it. "" turns that raise off too.
    /// </summary>
    public string EscalatedPriority { get; set; } = "High";

    // ── A development request from the widget's opening menu ──
    // Not a conversation Milo answered but a change the customer wants in the product, so it is
    // filed as work for the product team: open, classified as a feature, and sorted between the two
    // priorities above. See ChatService.ClassifyDevRequestTurn.
    /// <summary>Status a development-request ticket is created with. Blank means EscalatedStatus.</summary>
    public string DevRequestStatus { get; set; } = "Open";
    /// <summary>
    /// Classification for a development-request ticket. If Desk refuses the create — a portal whose
    /// classification list has no such value — the one retry falls back to Classification, so a
    /// request the customer was told reached the product team is never lost over a label. Blank
    /// means Classification.
    /// </summary>
    public string DevRequestClassification { get; set; } = "Feature";
    /// <summary>
    /// Priority for a development-request ticket; "" leaves the field off, as above. Medium is not
    /// AiHandledPriority, so a customer writing to an agent on a closed one does not get it raised
    /// (ZohoTicketSyncWorker.PlanReopen raises only a ticket still at the AI-handled priority).
    /// </summary>
    public string DevRequestPriority { get; set; } = "Medium";
    /// <summary>Goes in front of the request text in the subject, after SubjectPrefix:
    /// "[Milo] [Development request] …". Blank leaves it out.</summary>
    public string DevRequestSubjectLabel { get; set; } = "[Development request]";

    /// <summary>Optional Desk custom-field API name to receive our chat session GUID (e.g.
    /// "cf_milo_session_id"), so a ticket can be traced back to our own records. Values are
    /// capped at 255 chars by Desk; a GUID is 36. Leave empty to skip.</summary>
    public string SessionIdField { get; set; } = "";

    /// <summary>
    /// Optional Desk AGENT id (not a ZUID, not an email) to put every mirrored ticket into one
    /// person's queue — the shape of a trial run, where one owner reviews everything Milo
    /// handled before it reaches the real support queue. Empty means the ticket is left for the
    /// department's own assignment rules, which is the normal end state: clearing this value
    /// and restarting is the whole "trial is over" switch, with no code change.
    /// Look it up with ListAgentsAsync, or GET /api/v1/agents/email/{email}.
    /// </summary>
    public string AssigneeId { get; set; } = "";

    // ── Contact ──
    // Desk refuses to create a ticket without a contact, and an inline contact needs BOTH a
    // last name and an email. The widget does not send the customer's email today (see
    // ChatRequest.Email), so every ticket falls back to this one address until it does — which
    // maps them all onto a single "Milo" contact in Desk. That is deliberately visible rather
    // than papered over with a synthesised address, which would fill the contact list with
    // mailboxes that do not exist.
    public string FallbackContactEmail { get; set; } = "";
    public string FallbackContactLastName { get; set; } = "Milo";

    public int TimeoutSeconds { get; set; } = 15;

    // ── The way back: an agent's reply reaching the customer in Milo's own widget ────────────

    /// <summary>
    /// Our own client id, sent as the "sourceId" header on every write we make to Desk and set as
    /// the webhook's ignoreSourceId. Desk then does not fire the webhook for changes WE caused.
    ///
    /// Without it the relay is a loop: we mirror the customer's turn into the ticket, Desk tells
    /// us a thread was added, we treat it as new traffic, and round it goes. Zoho requires a UUID
    /// here and rejects anything else, so an invalid value is worse than an empty one — empty
    /// simply means "no suppression", which is safe as long as AgentRelayEnabled is off.
    /// </summary>
    public string SourceId { get; set; } = "";

    /// <summary>
    /// The off switch for the reply relay alone. Separate from Enabled on purpose: mirroring
    /// conversations into tickets and pushing an agent's reply back to the customer are different
    /// risks. The first is a record nobody sees; the second puts text in front of a customer.
    ///
    /// With this false the webhook endpoint still answers 200 — Desk deletes a subscription that
    /// 410s and disables one that keeps failing, so refusing loudly would cost us the
    /// registration — but nothing is fetched, stored or shown.
    /// </summary>
    public bool AgentRelayEnabled { get; set; }

    /// <summary>
    /// Whether a new ticket's contact is put under the customer's company (a Desk Account), so
    /// the company shows in the ticket's Contact Info. The company comes from the TAS address
    /// the customer works in (ChatService.CompanyFromHostInstance): "Avt_Test" in
    /// https://taseu.combtas.com/Avt_Test/... is the company Avt. Roi, 2026-09-29.
    ///
    /// Only for a contact with the customer's own email, never the fallback contact, and never
    /// over an account the contact already has. It needs the token to be allowed to search and
    /// create accounts and to update contacts (Desk.search.READ together with Desk.contacts.READ
    /// for the search, Desk.contacts.CREATE, Desk.contacts.UPDATE); without those the ticket is created exactly as before and the log
    /// says which call was refused. False turns it off.
    /// </summary>
    public bool LinkContactAccount { get; set; } = true;

    /// <summary>
    /// Shared secret that forms the last segment of the webhook URL we hand Zoho, e.g.
    /// /api/zoho/desk/thread/{this}. Desk supports no auth header at all on a webhook
    /// ("Only open webhooks that are publicly accessible and do not require authentication are
    /// supported"), so an unguessable path is the only gate available at the door.
    ///
    /// It is deliberately NOT the only defence: the endpoint treats the payload purely as a
    /// signal and re-reads the reply from Desk over our own authenticated connection, so the
    /// worst a leaked URL buys an attacker is making us fetch a ticket we already own.
    /// </summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>
    /// The safety net under the webhook: "Off" (the default), "LogOnly" or "On". See
    /// ZohoAgentReplyBackfillWorker for what it does and why it has a stage that shows nothing.
    ///
    /// A STRING, not an AgentReplyBackfillMode, and that is the whole point of its type.
    /// ConfigurationBinder throws on an enum value it does not recognise, and a throw while binding
    /// lands in ZohoDeskService's constructor catch — which replaces EVERY Zoho option with its
    /// default. One typo in the newest, least-proven switch ("Logonly ", "yes") would then silently
    /// stop mirroring every conversation and switch the relay itself off. As a string it cannot fail
    /// to bind; ParseBackfillMode decides what it means, and anything it does not recognise is Off.
    /// </summary>
    public string AgentReplyBackfill { get; set; } = "Off";

    /// <summary>Everything that must be present before a single call is worth attempting.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RefreshToken)
        && !string.IsNullOrWhiteSpace(OrgId)
        && !string.IsNullOrWhiteSpace(DepartmentId)
        && !string.IsNullOrWhiteSpace(FallbackContactEmail);

    /// <summary>
    /// The relay needs everything a ticket needs, plus its own switch and a secret long enough to
    /// be worth having. Checked as one property so no caller can half-enable it.
    /// </summary>
    public bool IsRelayConfigured =>
        IsConfigured
        && AgentRelayEnabled
        && WebhookSecret.Trim().Length >= 24;

    /// <summary>
    /// What an AgentReplyBackfill value means. Fails closed: only the words themselves turn it on,
    /// and everything else — absent, blank, "true", a typo — is Off. Trimmed and case-blind because
    /// a stray space or a capital in a hand-edited JSON file is not a decision to switch it off, but
    /// deliberately no cleverer than that: guessing at what "enabled" or "1" meant is how a switch
    /// that puts text in front of customers ends up on without anyone choosing it.
    /// </summary>
    public static AgentReplyBackfillMode ParseBackfillMode(string? v)
    {
        var s = v?.Trim();
        if (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) return AgentReplyBackfillMode.On;
        if (string.Equals(s, "logonly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "log", StringComparison.OrdinalIgnoreCase))
            return AgentReplyBackfillMode.LogOnly;
        return AgentReplyBackfillMode.Off;
    }

    public AgentReplyBackfillMode BackfillMode => ParseBackfillMode(AgentReplyBackfill);

    /// <summary>
    /// Whether the backfill worker has anything to do. It rides on the relay rather than beside it:
    /// it recovers replies the relay should have delivered, through the relay's own entry point, so
    /// with the relay off there is nothing to recover and it must stay idle whatever its own switch
    /// says — otherwise "AgentRelayEnabled=false" would stop being the instant rollback for putting
    /// agent text in front of customers.
    /// </summary>
    public bool IsBackfillActive => IsRelayConfigured && BackfillMode != AgentReplyBackfillMode.Off;
}

/// <summary>
/// The three stages of the agent-reply backfill. LogOnly exists because the fields the backfill
/// filters on come from a Desk response that could not be inspected when it was written: it runs
/// the whole sweep and logs what it WOULD relay, so the filters can be checked against real threads
/// before a single recovered line reaches a customer.
/// </summary>
public enum AgentReplyBackfillMode { Off, LogOnly, On }

/// <summary>What a new ticket needs to exist. Assembled by the sync worker from our own records.</summary>
public record ZohoTicketDraft(
    string Subject,
    string Description,
    string ContactLastName,
    string ContactEmail,
    Guid SessionId,
    bool Escalated,
    // A development request from the widget's opening menu, filed with the DevRequest* settings
    // instead of the AI-handled ones. See ZohoTicketSyncWorker.NewTicketDraft.
    bool DevRequest = false);

/// <summary>
/// Thin client for the two Zoho Desk endpoints this feature needs, plus token management.
///
/// Registered as a SINGLETON so one access token is shared by the whole process (they last an
/// hour; minting one per request would be both slow and a good way to hit Zoho's cap of 10 live
/// access tokens per refresh token). It holds no DbContext and no per-request state.
///
/// Every method returns null/false instead of throwing. Mirroring a conversation into a
/// helpdesk is strictly secondary to answering the customer — nothing in here may ever surface
/// as a failed chat.
/// </summary>
public class ZohoDeskService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ZohoDeskService> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc = DateTime.MinValue;

    public ZohoDeskOptions Options { get; }

    public ZohoDeskService(IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<ZohoDeskService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;

        // This constructor runs on the STARTUP path, because ZohoTicketSyncWorker takes this
        // service and AddHostedService resolves eagerly inside Host.StartAsync. ConfigurationBinder
        // throws on a type mismatch — `"Enabled": 1` or `"TimeoutSeconds": "15s"` in
        // appsettings.Production.json is enough — and an exception here would abort startup and
        // take the entire API down with HTTP 500.30. A typo in the off-by-default feature's own
        // config section must never be able to kill the chatbot, so a section that will not bind
        // degrades to "not configured" (which is inert) and says so loudly in the log.
        try
        {
            Options = config.GetSection("Zoho").Get<ZohoDeskOptions>() ?? new ZohoDeskOptions();
        }
        catch (Exception ex)
        {
            Options = new ZohoDeskOptions();
            _logger.LogError(ex,
                "[ZOHO] The Zoho configuration section could not be read and is being ignored — " +
                "conversations will NOT be mirrored. Check the value types in appsettings.Production.json " +
                "(Enabled must be true/false, TimeoutSeconds a plain number).");
        }

        if (Options.Enabled && !Options.IsConfigured)
        {
            _logger.LogWarning(
                "[ZOHO] Enabled=true but the configuration is incomplete — conversations will NOT be " +
                "mirrored. Required: ClientId, ClientSecret, RefreshToken, OrgId, DepartmentId, " +
                "FallbackContactEmail.");
        }
        else if (Options.IsConfigured)
        {
            _logger.LogInformation("[ZOHO] Enabled — mirroring conversations to {BaseUrl} department={Dept}",
                Options.ApiBaseUrl, Options.DepartmentId);

            // Said out loud at startup because there is no other way to find out. The relay's
            // config is read once, here, into a singleton — so editing appsettings without
            // recycling changes nothing, and the webhook endpoint deliberately answers
            // identically whether the relay is on or off (an open endpoint should not report its
            // own configuration to whoever asks). Without this line the only way to learn the
            // answer is to make a customer wait for a reply that never arrives.
            if (Options.IsRelayConfigured)
            {
                // The backfill mode rides on the same line for the same reason: it is read once,
                // here, and "Logonly " or a typo quietly means Off — the log is the only place that
                // says which of the three it actually took.
                _logger.LogInformation(
                    "[ZOHO] Agent reply relay is ON — an agent's reply on a mirrored ticket will be shown " +
                    "to the customer. sourceId={SourceIdSet} backfill={BackfillMode}",
                    string.IsNullOrWhiteSpace(Options.SourceId) ? "MISSING (echo loop risk)" : "set",
                    Options.BackfillMode);
            }
            else
            {
                _logger.LogInformation(
                    "[ZOHO] Agent reply relay is OFF — enabled={Enabled} secretLength={Len} (needs true and 24+). " +
                    "Webhook calls will be acknowledged and ignored.",
                    Options.AgentRelayEnabled, Options.WebhookSecret.Trim().Length);
            }
        }
    }

    // ── Token ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A valid access token, refreshed when it is within five minutes of expiry. The lock means
    /// a burst of callers mints one token between them rather than one each.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiresUtc) return _accessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed it while we waited for the lock.
            if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiresUtc) return _accessToken;

            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(Options.TimeoutSeconds);

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = Options.RefreshToken,
                ["client_id"] = Options.ClientId,
                ["client_secret"] = Options.ClientSecret,
                ["grant_type"] = "refresh_token",
            });

            var url = $"{Options.AccountsBaseUrl.TrimEnd('/')}/oauth/v2/token";
            var response = await http.PostAsync(url, form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ZOHO] Token refresh failed: {Status} {Body}",
                    (int)response.StatusCode, Truncate(body, 300));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            // Zoho answers HTTP 200 with {"error":"invalid_code"} on a bad refresh token, so the
            // status code alone is not proof of success.
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                _logger.LogWarning("[ZOHO] Token refresh rejected: {Error}", err.GetString());
                return null;
            }
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenProp))
            {
                _logger.LogWarning("[ZOHO] Token response had no access_token: {Body}", Truncate(body, 300));
                return null;
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var secs)
                ? secs
                : 3600;

            _accessToken = tokenProp.GetString();
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
            _logger.LogInformation("[ZOHO] Access token refreshed, valid for {Minutes} min", expiresIn / 60);
            return _accessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ZOHO] Token refresh threw: {Message}", ex.Message);
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpClient?> CreateAuthorizedClientAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        if (token == null) return null;

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(Options.TimeoutSeconds);
        http.BaseAddress = new Uri(Options.ApiBaseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        http.DefaultRequestHeaders.Add("orgId", Options.OrgId);

        // Stamps every write as ours. Paired with the webhook's ignoreSourceId, this is what
        // stops the relay eating itself: Desk skips the webhook for changes carrying this id, so
        // mirroring the customer's own turn into the ticket does not come back to us as if an
        // agent had written it. Harmless when no webhook exists, so it is sent unconditionally
        // rather than only when the relay is on — a header that is sometimes absent is the kind
        // of thing that works in testing and loops in production.
        if (!string.IsNullOrWhiteSpace(Options.SourceId))
            http.DefaultRequestHeaders.Add("sourceId", Options.SourceId.Trim());

        return http;
    }

    /// <summary>
    /// The public reply an agent last wrote on a ticket, as PLAIN TEXT.
    ///
    /// Deliberately a fresh read rather than trusting the webhook's own body, for three reasons
    /// that all point the same way. The webhook carries "content" as a raw HTML email body —
    /// signature block, Zoho's happiness survey and the entire quoted history included — and no
    /// plain-text field; it can arrive truncated (isContentTruncated) with the remainder behind
    /// another fetch anyway; and its payload reaches us over an endpoint Zoho requires to be
    /// unauthenticated, so anything it says is a claim rather than a fact. Reading it back over
    /// our own authenticated connection answers all three: the text is clean, complete, and
    /// actually from Zoho.
    ///
    /// needPublic=true asks for the customer-visible reply, never an internal note.
    ///
    /// threadId is the thread the webhook actually pointed at, and passing it is what stops
    /// replies being lost. Asking for "latestThread" means asking a question whose answer changes
    /// underneath you: if a second agent replies before the first notification is processed — or
    /// if Desk simply batches both thread events into one POST, which needs no timing luck at all
    /// — then both notifications read the same newest thread, the first reply is never stored, and
    /// nothing ever looks for it again. Naming the thread makes each notification about the thread
    /// it was actually raised for.
    ///
    /// The id comes from an unauthenticated payload, but it is used as a POINTER and never as
    /// content: it selects which thread OF THIS TICKET to fetch over our own authenticated
    /// connection, and a thread that is not on this ticket simply does not resolve.
    ///
    /// Null threadId falls back to latestThread, which is still right for a payload that carried
    /// no id at all.
    /// </summary>
    public async Task<AgentReply?> GetPublicReplyAsync(
        string ticketId, string? threadId, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || string.IsNullOrWhiteSpace(ticketId)) return null;

        var path = string.IsNullOrWhiteSpace(threadId)
            ? $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}/latestThread?needPublic=true&include=plainText"
            : $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}/threads/{Uri.EscapeDataString(threadId)}?include=plainText";

        var result = await GetAsync(path, ct);
        if (result.Outcome != ZohoCallOutcome.Ok || string.IsNullOrWhiteSpace(result.Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            var root = doc.RootElement;

            var resolvedThreadId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(resolvedThreadId)) return null;

            // "out" is Desk's own word for a thread leaving the helpdesk towards the customer.
            // An "in" thread is the customer's own message coming back to us — relaying that to
            // the widget would show the customer their own words in an agent's voice.
            var direction = root.TryGetProperty("direction", out var d) ? d.GetString() : null;
            if (!string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase)) return null;

            // Checked HERE, in code, and not left to the query string. The latestThread path asks
            // Zoho for needPublic=true and is filtered server-side; fetching a named thread is not,
            // so without this an internal note — the thing agents write to each other believing the
            // customer cannot see it — would be relayed straight into the customer's chat window.
            // It is the single most damaging thing this class could do, so it is not conditional on
            // which path was taken.
            var visibility = root.TryGetProperty("visibility", out var v) ? v.GetString() : null;
            if (visibility != null && !string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
                return null;

            // plainText is what include=plainText adds; content is the HTML we asked to avoid and
            // is only a fallback for the case where Zoho omits the field entirely.
            var text = root.TryGetProperty("plainText", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(text) && root.TryGetProperty("content", out var c))
                text = StripHtml(c.GetString());

            text = TidyReply(text);
            if (string.IsNullOrWhiteSpace(text)) return null;

            var author = root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                ? (a.TryGetProperty("name", out var n) ? n.GetString() : null)
                : null;

            return new AgentReply(resolvedThreadId!, text!, author);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("[ZOHO] latestThread for ticket {TicketId} was not readable JSON: {Message}",
                ticketId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// One entry of a ticket's thread list: the fields the backfill filters on, and nothing it could
    /// show. The list's own text preview ("summary") is deliberately not read — a recovered reply is
    /// always re-read in full through GetPublicReplyAsync, which applies the same checks and
    /// TidyReply the webhook path does, so nothing in this record ever reaches a customer or a log.
    ///
    /// Every field Desk might omit is nullable, and the two flags are false unless Desk said a
    /// literal true. The consumer skips a thread whose direction, visibility, author type or time is
    /// missing (see ZohoAgentReplyBackfillWorker.Classify), so a response shaped differently from
    /// what was expected recovers nothing rather than something wrong.
    /// </summary>
    public readonly record struct ThreadSummary(
        string Id,
        string? Direction,
        string? Visibility,
        bool IsDescriptionThread,
        bool IsForward,
        string? AuthorType,
        string? Status,
        string? Channel,
        DateTime? CreatedTimeUtc);

    /// <summary>
    /// How many threads one list call asks for. The backfill only looks at threads from the last day
    /// on conversations a person is already answering, which is a handful, so one page is the whole
    /// answer for any ordinary ticket. If Desk ever refuses this value (a 4xx on /threads in the
    /// log), this is the number to lower.
    /// </summary>
    public const int ThreadListLimit = 100;

    /// <summary>The outcome of listing a ticket's threads: the list on Ok, null otherwise.</summary>
    public readonly record struct ThreadListResult(List<ThreadSummary>? Threads, ZohoCallOutcome Outcome);

    /// <summary>
    /// A ticket's threads, as the metadata the backfill needs to decide which ones to ask for.
    /// Threads is null when Desk could not be read, and the outcome says why — which the caller
    /// must tell apart: Unknown ("Desk is down") is a reason to stop sweeping, Rejected is about
    /// this one ticket (deleted, merged, moved out of reach) and says nothing about the next, and
    /// an empty list is just a ticket with nothing to recover.
    ///
    /// Read-only, and needs the same ticket-read permission as GetPublicReplyAsync and
    /// GetTicketStatusAsync, which already run in production.
    /// </summary>
    public async Task<ThreadListResult> ListThreadsAsync(string ticketId, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || string.IsNullOrWhiteSpace(ticketId))
            return new ThreadListResult(null, ZohoCallOutcome.Rejected);

        var result = await GetAsync(
            $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}/threads?from=0&limit={ThreadListLimit}", ct);
        if (result.Outcome != ZohoCallOutcome.Ok) return new ThreadListResult(null, result.Outcome);

        var threads = ParseThreadList(result.Body);

        // A full page means there may be a second one, and nothing here reads it. Said out loud
        // because the order Desk lists threads in is not something this code could check, so a
        // reply sitting on the next page would be missed without any other sign.
        if (threads.Count == ThreadListLimit)
        {
            _logger.LogWarning(
                "[ZOHO] ticket {TicketId} listed {Count} threads, a full page — any on a later page are not " +
                "checked by the reply backfill", ticketId, threads.Count);
        }

        return new ThreadListResult(threads, ZohoCallOutcome.Ok);
    }

    /// <summary>
    /// Reads a /threads response. Split out so it can be tested without Desk, and written so it
    /// cannot throw: whatever Desk sends, the answer is a list, at worst an empty one.
    ///
    /// An empty body is an empty list and not an error: Desk answers 204 with no body when a list
    /// has nothing in it, and GetAsync counts a 204 as success. Every field is type-checked before
    /// it is read, because JsonElement.GetString throws on a number and "the field turned into a
    /// number one day" must degrade to "skip the thread", not to a sweep that dies every interval.
    /// </summary>
    public static List<ThreadSummary> ParseThreadList(string? json)
    {
        var threads = new List<ThreadSummary>();
        if (string.IsNullOrWhiteSpace(json)) return threads;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return threads;

            foreach (var t in data.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;

                // The same leniency as the webhook's ReadId: Desk sends ids as strings, and a numeric
                // one should not be lost. A thread without an id cannot be fetched, so it is dropped.
                var id = t.TryGetProperty("id", out var idProp)
                    ? idProp.ValueKind switch
                    {
                        JsonValueKind.String => idProp.GetString(),
                        JsonValueKind.Number => idProp.ToString(),
                        _ => null,
                    }
                    : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                // author.type is what separates a person from Desk's own auto-acknowledgements and
                // bots, so it is only believed when author really is an object that says so.
                var authorType = t.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                    ? StringField(a, "type")
                    : null;

                DateTime? created = null;
                var createdRaw = StringField(t, "createdTime");
                if (createdRaw != null
                    && DateTime.TryParse(createdRaw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    created = d;

                threads.Add(new ThreadSummary(
                    id!,
                    StringField(t, "direction"),
                    StringField(t, "visibility"),
                    // True only for a literal JSON true — the same test the webhook applies to
                    // isDescriptionThread. These two flags are the one place "unknown" does not mean
                    // "skip", on purpose: if an absent isForward excluded the thread, a response that
                    // simply omits it would make the backfill recover nothing at all. The checks that
                    // actually keep the wrong text out (direction, visibility, author.type) do not
                    // lean on these, and a string "true" is not Desk's shape, so it is not guessed at.
                    t.TryGetProperty("isDescriptionThread", out var desc) && desc.ValueKind == JsonValueKind.True,
                    t.TryGetProperty("isForward", out var fwd) && fwd.ValueKind == JsonValueKind.True,
                    authorType,
                    StringField(t, "status"),
                    StringField(t, "channel"),
                    created));
            }
        }
        catch (JsonException)
        {
            // Truncated or not JSON at all. Whatever was read before the break is dropped with it:
            // a half-read list is not an answer this code wants to act on.
            return new List<ThreadSummary>();
        }

        return threads;
    }

    /// <summary>A property's value when it is a string, otherwise null — never a throw.</summary>
    private static string? StringField(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The ticket's status TYPE — "Open", "On Hold" or "Closed" — with the outcome of reading it.
    ///
    /// The type rather than the status name, because the name is whatever this portal's admin called
    /// it ("Resolved", "סגור", "Waiting on supplier") while the type is one of three fixed values.
    /// Asking "is it closed?" of the name would break the first time someone renames a status.
    ///
    /// Carries the call's outcome alongside, because the caller has to tell "Desk is having a bad
    /// minute, try again" (Unknown) from "Desk refused, and will refuse again" (Rejected — the
    /// ticket was deleted, merged, or is not ours to read). Only the first is worth waiting for.
    ///
    /// Also carries the ticket's current priority, read from the same body. The GET already returns
    /// the whole ticket, and the reopen needs to know whether the ticket is still sitting at the
    /// AI-handled priority before it decides to raise it — so the answer costs no extra call.
    /// </summary>
    public async Task<TicketStatus> GetTicketStatusAsync(string ticketId, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || string.IsNullOrWhiteSpace(ticketId))
            return new TicketStatus(null, ZohoCallOutcome.Rejected);

        var result = await GetAsync($"api/v1/tickets/{Uri.EscapeDataString(ticketId)}", ct);
        if (result.Outcome != ZohoCallOutcome.Ok) return new TicketStatus(null, result.Outcome);

        // Read fine but no recognisable type: an answer, just not a useful one. Ok with a null type,
        // so the caller neither reopens on a guess nor waits for a retry that would say the same.
        return new TicketStatus(ReadStatusType(result.Body), ZohoCallOutcome.Ok, ReadPriority(result.Body));
    }

    /// <summary>A ticket's status type, whether reading it worked, and its priority when it did.
    /// Priority defaults to null — "not known" — which never triggers a raise.</summary>
    public readonly record struct TicketStatus(string? StatusType, ZohoCallOutcome Outcome, string? Priority = null);

    /// <summary>
    /// Sets the ticket's status and nothing else — no priority, no assignee. The reopen a customer's
    /// reply is owed, as distinct from UpdateStatusAndPriorityAsync, which is the escalation itself
    /// (the reopen uses SetStatusAndPriorityAsync instead only for the one-time raise of a ticket
    /// still at the AI-handled priority — see ZohoTicketSyncWorker.PlanReopen).
    /// Returns the raw outcome so the caller can retry a timeout but not a refusal.
    /// </summary>
    public async Task<ZohoCallOutcome> SetStatusAsync(string ticketId, string status, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || string.IsNullOrWhiteSpace(ticketId)) return ZohoCallOutcome.Rejected;

        var result = await SendAsync(HttpMethod.Patch, $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}",
            new Dictionary<string, object?> { ["status"] = status }, ct);
        return result.Outcome;
    }

    /// <summary>Pulls statusType out of a ticket body. Split out so it can be tested without Desk.</summary>
    public static string? ReadStatusType(string? ticketJson)
    {
        if (string.IsNullOrWhiteSpace(ticketJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(ticketJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("statusType", out var t) && t.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(t.GetString()))
                return t.GetString()!.Trim();

            // No type in the body: fall back to the one status name whose meaning is fixed — Desk's
            // built-in "Closed". Anything else is unknown, and unknown must not trigger a reopen.
            if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                && string.Equals(s.GetString()?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase))
                return "Closed";

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the priority out of a ticket body. Split out so it can be tested without Desk; unknown
    /// = null, which never triggers a raise.
    ///
    /// Only a non-blank string counts. Desk sends null for a ticket nobody gave a priority, and
    /// anything else (a number, an empty string) is not a picklist value we could compare against —
    /// treating it as "Low" would raise a ticket on a guess.
    /// </summary>
    public static string? ReadPriority(string? ticketJson)
    {
        if (string.IsNullOrWhiteSpace(ticketJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(ticketJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.GetString()))
                return p.GetString()!.Trim();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One public reply from a human agent, ready to show a customer.</summary>
    public readonly record struct AgentReply(string ThreadId, string Text, string? AuthorName);

    /// <summary>
    /// A ticket Zoho just created, under both of the numbers it has.
    ///
    /// Id is the internal key every later API call needs. Number is the short one Desk shows its
    /// agents — the only one worth quoting to a customer, and the only one they could read back to
    /// an agent over the phone. Number is nullable because it is a convenience: if Zoho ever omits
    /// it the ticket still exists and still works, the customer simply does not get a reference.
    /// </summary>
    public readonly record struct CreatedTicket(string Id, string? Number, string? ContactId = null, string? AccountId = null);

    private async Task<ZohoCallResult> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var http = await CreateAuthorizedClientAsync(ct);
            if (http == null) return new ZohoCallResult(null, ZohoCallOutcome.Rejected);

            using var response = await http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode) return new ZohoCallResult(body, ZohoCallOutcome.Ok);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _accessToken = null;
                _accessTokenExpiresUtc = DateTime.MinValue;
            }

            _logger.LogWarning("[ZOHO] GET {Path} → {Status} {Body}",
                path, (int)response.StatusCode, Truncate(body, 400));
            return new ZohoCallResult(null, (int)response.StatusCode >= 500
                ? ZohoCallOutcome.Unknown
                : ZohoCallOutcome.Rejected) { Status = (int)response.StatusCode };
        }
        catch (Exception ex)
        {
            // A read changes nothing, so every failure here is simply "we did not get it" — the
            // Unknown/Rejected distinction that matters for writes does not apply.
            _logger.LogWarning("[ZOHO] GET {Path} threw: {Message}", path, ex.Message);
            return new ZohoCallResult(null, ZohoCallOutcome.Unknown);
        }
    }

    // ── Operations ───────────────────────────────────────────────────────────────────────────

    /// <summary>Creates the ticket for a conversation. Returns its id, or null on any failure.</summary>
    public async Task<CreatedTicket?> CreateTicketAsync(ZohoTicketDraft draft, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return null;

        // A development request is filed with settings of its own (see DevRequestStatus). Should one
        // ever escalate as well, the escalation decides status and priority: a person waiting
        // outranks a change to consider.
        var classification = draft.DevRequest
            ? ConfiguredOr(Options.DevRequestClassification, Options.Classification)
            : Options.Classification;
        var label = draft.DevRequest ? Options.DevRequestSubjectLabel?.Trim() : null;

        var payload = new Dictionary<string, object?>
        {
            ["subject"] = Truncate(Options.SubjectPrefix + (string.IsNullOrEmpty(label) ? "" : label + " ") + draft.Subject, 255),
            ["departmentId"] = Options.DepartmentId,
            ["description"] = Truncate(draft.Description, MaxCommentLength),
            ["status"] = draft.Escalated ? Options.EscalatedStatus
                : draft.DevRequest ? ConfiguredOr(Options.DevRequestStatus, Options.EscalatedStatus)
                : Options.AiHandledStatus,
            ["classification"] = classification,
            ["channel"] = Options.Channel,
            // Desk needs a contact and will reuse an existing one when the email matches, so this
            // both attaches the ticket and keeps the contact list from growing a row per chat.
            ["contact"] = new Dictionary<string, string>
            {
                ["lastName"] = Truncate(draft.ContactLastName, 200),
                ["email"] = draft.ContactEmail,
            },
        };

        // Low unless a human is already needed. Added here rather than in the initializer above
        // because a blank setting has to leave the key OFF the payload: Desk rejects an empty
        // string for a picklist field, so an operator who wants no priority at all — or whose
        // portal uses a custom priority list — clears the setting instead of inventing a value.
        // Trimmed, not just whitespace-tested: Desk matches a picklist value exactly, so a
        // trailing space someone left in the JSON config would be a value it does not know.
        var priority = (draft.Escalated ? Options.EscalatedPriority
            : draft.DevRequest ? Options.DevRequestPriority
            : Options.AiHandledPriority)?.Trim();
        if (!string.IsNullOrEmpty(priority))
            payload["priority"] = priority;

        if (!string.IsNullOrWhiteSpace(Options.SessionIdField))
            payload["cf"] = new Dictionary<string, string> { [Options.SessionIdField] = draft.SessionId.ToString() };

        if (!string.IsNullOrWhiteSpace(Options.AssigneeId))
            payload["assigneeId"] = Options.AssigneeId;

        var (json, outcome) = await SendAsync(HttpMethod.Post, "api/v1/tickets", payload, ct);

        // Priority is a picklist, and Desk matches picklist values EXACTLY. A portal whose
        // priority list has been customised may simply not have "Low" — and because this field
        // rides on the create payload, that would turn a live, working mirror into zero tickets
        // rather than into tickets with no priority. That trade is unacceptable: the ticket and
        // its transcript are the point, the sort order is a convenience. So a clean refusal
        // (4xx) gets exactly one more attempt with the field dropped.
        //
        // Deliberately not conditioned on the error text: a 4xx body is logged by SendAsync but
        // not returned here, and guessing at Zoho's error codes to save one retry on an
        // unrelated failure (a bad orgId, a revoked token — which this also gives a second,
        // freshly-minted-token attempt) is not worth the chance of missing the case this exists
        // for. One extra call on a failing create, never on a succeeding one.
        //
        // A development request also goes back to the ordinary classification in that same retry:
        // its own is a picklist value too, one this portal may not have, and the customer was told
        // the request reached the product team. Still one retry, never two.
        var classificationDropped = outcome == ZohoCallOutcome.Rejected && draft.DevRequest
                                    && !string.Equals(classification, Options.Classification, StringComparison.Ordinal);
        if (classificationDropped)
            payload["classification"] = Options.Classification;
        var priorityDropped = outcome == ZohoCallOutcome.Rejected && payload.Remove("priority");

        if (priorityDropped || classificationDropped)
        {
            if (draft.DevRequest)
                _logger.LogWarning(
                    "[ZOHO] Development request ticket create for session={SessionId} was refused while sending " +
                    "classification=\"{Classification}\" priority=\"{Priority}\" — retrying with classification=\"{Fallback}\" " +
                    "and no priority. If this line repeats, one of those values is not in this portal's picklists: " +
                    "correct or clear Zoho:DevRequestClassification / Zoho:DevRequestPriority (and check Zoho:DevRequestStatus).",
                    draft.SessionId, classification, priority, Options.Classification);
            else
                _logger.LogWarning(
                    "[ZOHO] Ticket create for session={SessionId} was refused while sending priority=\"{Priority}\" — " +
                    "retrying WITHOUT it. If this line repeats, that value is not in this portal's priority " +
                    "picklist: correct or clear Zoho:AiHandledPriority / Zoho:EscalatedPriority.",
                    draft.SessionId, priority);

            (json, outcome) = await SendAsync(HttpMethod.Post, "api/v1/tickets", payload, ct);
        }

        if (outcome == ZohoCallOutcome.Unknown)
        {
            // The request left here and we never learned what happened to it — a timeout, a
            // dropped connection, a 5xx. Zoho may well have created the ticket. There is no
            // idempotency key to retry safely against and its ticket search cannot be used to
            // check (new tickets take minutes to be indexed), so the next turn will create a
            // second one. That is rare and recoverable by merging in Desk, but it must never be
            // silent: this marker is what makes an orphan findable.
            _logger.LogWarning(
                "[ZOHO] ⚠ AMBIGUOUS create for session={SessionId} — no response received, so the ticket " +
                "MAY exist in Zoho. If a duplicate appears for this session, this is why.",
                draft.SessionId);
        }

        if (json == null) return null;

        string? id;
        string? number;
        string? assignedTo;
        string? contactId = null;
        string? accountId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            // Two different numbers, and only one of them is fit to say to a customer. "id" is
            // Zoho's internal key — 31138000011972149 — which is what every API call needs and
            // what nobody can read down a phone line. "ticketNumber" is the short one Desk shows
            // agents and puts in its subject lines. Both are kept: the id to work with, the
            // number to quote.
            number = doc.RootElement.TryGetProperty("ticketNumber", out var numProp)
                ? (numProp.ValueKind == JsonValueKind.Number ? numProp.ToString() : numProp.GetString())
                : null;

            assignedTo = doc.RootElement.TryGetProperty("assigneeId", out var aProp) && aProp.ValueKind == JsonValueKind.String
                ? aProp.GetString()
                : null;

            (contactId, accountId) = ReadCreatedTicketLinks(json);

            if (id == null)
                _logger.LogWarning("[ZOHO] Ticket created but the response carried no id: {Body}", Truncate(json, 300));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ZOHO] Could not read the created ticket's id: {Message}", ex.Message);
            return null;
        }

        // Zoho accepts assigneeId on create and then, when the portal has its own assignment
        // rules, can quietly hand the ticket to somebody else and report success either way —
        // there is no error to catch, only a different value in the response. So the value is
        // read back and corrected, rather than assumed to have stuck. One extra call, and only
        // when it actually went somewhere else.
        if (id != null
            && !string.IsNullOrWhiteSpace(Options.AssigneeId)
            && !string.Equals(assignedTo, Options.AssigneeId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[ZOHO] Ticket {TicketId} came back assigned to {Actual} instead of {Wanted} " +
                "(an assignment rule in the portal overrode it) — correcting.",
                id, assignedTo ?? "nobody", Options.AssigneeId);

            if (!await UpdateAssigneeAsync(id, Options.AssigneeId, ct))
                _logger.LogWarning("[ZOHO] Ticket {TicketId} could not be reassigned to {Wanted}.",
                    id, Options.AssigneeId);
        }

        return id == null ? null : new CreatedTicket(id, number, contactId, accountId);
    }

    /// <summary>
    /// The contact a created ticket was attached to, and the account the ticket already carries.
    /// A non-null account means Desk put it there from the contact itself, so the contact is
    /// already under a company and is left alone. Split out so it can be tested without Desk;
    /// anything unreadable is null.
    /// </summary>
    public static (string? ContactId, string? AccountId) ReadCreatedTicketLinks(string? ticketJson)
    {
        if (string.IsNullOrWhiteSpace(ticketJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(ticketJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (ReadIdProperty(doc.RootElement, "contactId"), ReadIdProperty(doc.RootElement, "accountId"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadIdProperty(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p)) return null;
        var value = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // Account ids by company name, for this process. A company's account never changes id, so
    // one search per company per restart is enough, however many tickets it opens.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _accountIds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long company linking rests after Desk refuses it for lack of permission (401/403). Every
    /// 401 throws away the shared access token (see GetAsync), and Zoho mints at most ten access
    /// tokens per refresh token in ten minutes. Trying again on every new ticket could use those
    /// up and lock out every Zoho call — ticket creation included — so a refusal pauses linking
    /// instead: at most one refused call an hour, and tickets are created as usual meanwhile.
    /// </summary>
    public static readonly TimeSpan AccountLinkPause = TimeSpan.FromHours(1);
    private DateTime _accountLinkPausedUntilUtc = DateTime.MinValue;

    /// <summary>The clock the pause is measured on. Public so a test can move it; nothing else does.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private bool PausedForPermission(ZohoCallResult result, string scope, string accountName)
    {
        if (result.Outcome != ZohoCallOutcome.Rejected || result.Status is not (401 or 403)) return false;
        _accountLinkPausedUntilUtc = UtcNow() + AccountLinkPause;
        _logger.LogWarning(
            "[ZOHO-ACCOUNT] Zoho refused the account call for '{Account}' ({Status}): the Zoho token needs the " +
            "{Scope} scope. Company linking is paused until {Until:HH:mm} UTC; tickets are created as usual.",
            accountName, result.Status, scope, _accountLinkPausedUntilUtc);
        return true;
    }

    /// <summary>
    /// Puts a newly created ticket's contact, and the ticket, under the company's account,
    /// creating the account the first time that company is seen. Never throws, and never blocks
    /// the ticket: every failure is logged and left as it is. Returns whether it linked.
    /// </summary>
    public async Task<bool> LinkContactToAccountAsync(
        string ticketId, string contactId, string accountName, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || !Options.LinkContactAccount) return false;
        if (UtcNow() < _accountLinkPausedUntilUtc)
        {
            _logger.LogInformation(
                "[ZOHO-ACCOUNT] ticket={TicketId} company '{Account}' not set: linking is paused until {Until:HH:mm} UTC " +
                "after Zoho refused it for lack of permission (see the warning before)", ticketId, accountName, _accountLinkPausedUntilUtc);
            return false;
        }

        var accountId = await ResolveAccountIdAsync(accountName, ct);
        if (accountId == null) return false;

        var contact = await SendAsync(HttpMethod.Patch,
            $"api/v1/contacts/{Uri.EscapeDataString(contactId)}",
            new Dictionary<string, object?> { ["accountId"] = accountId }, ct);
        if (contact.Outcome != ZohoCallOutcome.Ok)
        {
            if (!PausedForPermission(contact, "Desk.contacts.UPDATE", accountName))
                _logger.LogWarning("[ZOHO-ACCOUNT] contact {ContactId} was not put under account '{Account}'",
                    contactId, accountName);
            return false;
        }

        // The ticket was created before the contact had an account, so it carries none of its own
        // yet. Tickets opened later by the same contact get it from the contact.
        var ticket = await SendAsync(HttpMethod.Patch,
            $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}",
            new Dictionary<string, object?> { ["accountId"] = accountId }, ct);
        if (ticket.Outcome != ZohoCallOutcome.Ok)
            _logger.LogWarning("[ZOHO-ACCOUNT] ticket {TicketId} kept no account, though its contact now has '{Account}'",
                ticketId, accountName);

        _logger.LogInformation("[ZOHO-ACCOUNT] ticket={TicketId} contact={ContactId} → account '{Account}' ({AccountId})",
            ticketId, contactId, accountName, accountId);
        return true;
    }

    /// <summary>
    /// The account for a company name: from the cache, else by search, else newly created. A new
    /// account is created ONLY after a search that succeeded and found none — a search that
    /// failed says nothing about whether one exists, and creating on a guess would add a second
    /// "Avt" for every ticket while the search keeps failing.
    /// </summary>
    private async Task<string?> ResolveAccountIdAsync(string accountName, CancellationToken ct)
    {
        if (_accountIds.TryGetValue(accountName, out var cached)) return cached;

        // Desk refuses to search for fewer than three characters (422 for "QA", 2026-10-05), so a
        // shorter name is searched as a prefix, "QA*", and FindAccountId still takes only the exact
        // name. Such a name is linked to an account that exists and never created: whether Desk
        // reads the * as a wildcard cannot be checked from here, and a search that did not really
        // look would create another "QA" after every restart.
        var shortName = IsTooShortToSearch(accountName);
        var term = shortName ? accountName.Trim() + "*" : accountName;
        var search = await GetAsync(
            $"api/v1/accounts/search?accountName={Uri.EscapeDataString(term)}&limit={AccountSearchLimit}", ct);
        if (search.Outcome != ZohoCallOutcome.Ok)
        {
            if (!PausedForPermission(search, "Desk.search.READ and Desk.contacts.READ", accountName))
                _logger.LogWarning("[ZOHO-ACCOUNT] could not search for account '{Account}'", accountName);
            return null;
        }

        var found = FindAccountId(search.Body, accountName);
        if (found == null && shortName)
        {
            _logger.LogWarning(
                "[ZOHO-ACCOUNT] no account named '{Account}' found in Zoho. A name under {Min} characters is not " +
                "created automatically: create the account '{Account}' in Zoho once, and later tickets are linked to it.",
                accountName, MinAccountSearchLength, accountName);
            return null;
        }
        if (found == null && CountAccounts(search.Body) >= AccountSearchLimit)
        {
            // A full page with no exact match may only mean the exact one is on the next page.
            _logger.LogWarning(
                "[ZOHO-ACCOUNT] the search for '{Account}' filled a page without an exact match — not creating one, " +
                "since it may already exist further down", accountName);
            return null;
        }
        if (found == null)
        {
            var created = await SendAsync(HttpMethod.Post, "api/v1/accounts",
                new Dictionary<string, object?> { ["accountName"] = accountName }, ct);
            if (created.Outcome != ZohoCallOutcome.Ok)
            {
                if (!PausedForPermission(created, "Desk.contacts.CREATE", accountName))
                    _logger.LogWarning("[ZOHO-ACCOUNT] could not create account '{Account}'", accountName);
                return null;
            }

            found = ReadAccountIdFromBody(created.Body);
            if (found == null)
            {
                _logger.LogWarning("[ZOHO-ACCOUNT] account '{Account}' was created but the response carried no id", accountName);
                return null;
            }
            _logger.LogInformation("[ZOHO-ACCOUNT] created account '{Account}' ({AccountId})", accountName, found);
        }

        _accountIds[accountName] = found;
        return found;
    }

    /// <summary>The shortest value Desk's accounts search accepts (its 422 says "minimum length of '3'").</summary>
    public const int MinAccountSearchLength = 3;
    public const int AccountSearchLimit = 10;

    public static bool IsTooShortToSearch(string accountName) => accountName.Trim().Length < MinAccountSearchLength;

    /// <summary>How many accounts a search response holds. 0 for an empty or unreadable body. Never throws.</summary>
    public static int CountAccounts(string? searchJson)
    {
        if (string.IsNullOrWhiteSpace(searchJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(searchJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? ReadAccountIdFromBody(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadIdProperty(doc.RootElement, "id") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The id of the account whose name is exactly this one (ignoring case and surrounding
    /// space) in an accounts search response. Search matches loosely — "Avt" also finds
    /// "Avtech" — so only an exact name counts; anything else means none was found. An empty
    /// body (Desk answers 204 when nothing matches) is none too. Never throws.
    /// </summary>
    public static string? FindAccountId(string? searchJson, string accountName)
    {
        if (string.IsNullOrWhiteSpace(searchJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(searchJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var a in data.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                if (!a.TryGetProperty("accountName", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(n.GetString()?.Trim(), accountName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var id = ReadIdProperty(a, "id");
                if (id != null) return id;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Moves a ticket to a specific agent. Separate from UpdateStatusAndPriorityAsync so the two
    /// can fail independently — a ticket in the wrong queue is still a ticket with its transcript.</summary>
    public async Task<bool> UpdateAssigneeAsync(string ticketId, string agentId, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return false;

        var payload = new Dictionary<string, object?> { ["assigneeId"] = agentId };
        return (await SendAsync(HttpMethod.Patch, $"api/v1/tickets/{ticketId}", payload, ct)).Body != null;
    }

    /// <summary>
    /// Every agent in the portal, as (id, name, email). Used to pick the AssigneeId without
    /// anyone having to dig an internal id out of the Desk UI. Needs Desk.agents.READ.
    /// </summary>
    public async Task<List<(string Id, string Name, string Email)>> ListAgentsAsync(CancellationToken ct = default)
    {
        var agents = new List<(string, string, string)>();
        if (!Options.IsConfigured) return agents;

        try
        {
            using var http = await CreateAuthorizedClientAsync(ct);
            if (http == null) return agents;

            using var response = await http.GetAsync("api/v1/agents?limit=200", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ZOHO] Agent list → {Status} {Body}",
                    (int)response.StatusCode, Truncate(body, 300));
                return agents;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return agents;

            foreach (var a in data.EnumerateArray())
            {
                var id = a.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id == null) continue;
                // The Agents module calls it emailId; a ticket's embedded assignee calls the same
                // thing "email". This endpoint is the former.
                agents.Add((id,
                    a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    a.TryGetProperty("emailId", out var e) ? e.GetString() ?? "" : ""));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ZOHO] Agent list failed: {Message}", ex.Message);
        }

        return agents;
    }

    /// <summary>
    /// Appends one batch of conversation turns to an existing ticket.
    ///
    /// A private comment on purpose. Desk ships a (disabled-by-default) notification rule that
    /// emails the requester whenever a PUBLIC comment is added — one mail per chat turn if
    /// anyone ever switches it on. A private comment cannot trigger it. The other candidate,
    /// sendReply, is defined as sending email and is not used at all.
    /// </summary>
    public async Task<bool> AddCommentAsync(string ticketId, string content, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return false;

        var payload = new Dictionary<string, object?>
        {
            ["content"] = Truncate(content, MaxCommentLength),
            ["isPublic"] = false,
            // HTML, matching the ticket description — which is an HTML field with no plainText
            // option, and which collapsed every newline when it was fed plain text. The
            // transcript is built as HTML once (see ZohoTicketSyncWorker.Render) so both fields
            // take the same string and line breaks survive in both.
            ["contentType"] = "html",
        };

        return (await SendAsync(HttpMethod.Post, $"api/v1/tickets/{ticketId}/comments", payload, ct)).Body != null;
    }

    /// <summary>
    /// Raises an existing ticket to its escalated state. Status and priority go in ONE PATCH on
    /// purpose: they are the same fact ("a person is needed now") written to two fields, and two
    /// calls could leave the ticket reopened but still sitting at Low, which is exactly the row
    /// an agent scanning by priority would skip. A blank priority sends status alone.
    ///
    /// A plain yes/no over SetStatusAndPriorityAsync, which is where the PATCH actually lives: the
    /// escalation push only needs to know whether it landed, and keeps exactly the answers it had.
    /// </summary>
    public async Task<bool> UpdateStatusAndPriorityAsync(
        string ticketId, string status, string? priority, CancellationToken ct = default)
        => await SetStatusAndPriorityAsync(ticketId, status, priority, ct) == ZohoCallOutcome.Ok;

    /// <summary>
    /// The status + priority PATCH itself, returning the raw outcome — the same shape as
    /// SetStatusAsync, and for the same reason: the reopen owed to a customer writing to an agent
    /// has to retry a timeout (Unknown) but not a refusal (Rejected), and a bool cannot say which.
    /// UpdateStatusAndPriorityAsync is this method with the answer folded to "did it land".
    /// </summary>
    public async Task<ZohoCallOutcome> SetStatusAndPriorityAsync(
        string ticketId, string status, string? priority, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return ZohoCallOutcome.Rejected;

        var payload = new Dictionary<string, object?> { ["status"] = status };
        priority = priority?.Trim();
        if (!string.IsNullOrEmpty(priority))
            payload["priority"] = priority;

        var result = await SendAsync(HttpMethod.Patch, $"api/v1/tickets/{ticketId}", payload, ct);

        // Same trade as on create, and the reason this merge is safe: bundling priority into the
        // reopen is only an improvement while it cannot PREVENT the reopen. A refused PATCH gets
        // one more attempt with the status alone, so a priority value this portal doesn't know
        // leaves the ticket correctly reopened and merely sorted as before — never stuck closed.
        if (result.Outcome == ZohoCallOutcome.Rejected && payload.Remove("priority"))
        {
            _logger.LogWarning(
                "[ZOHO] Ticket {TicketId} refused the reopen while sending priority=\"{Priority}\" — " +
                "retrying with the status alone.", ticketId, priority);

            result = await SendAsync(HttpMethod.Patch, $"api/v1/tickets/{ticketId}", payload, ct);
        }

        return result.Outcome;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Desk's documented ceiling for a comment body (and description).</summary>
    public const int MaxCommentLength = 32000;

    /// <summary>
    /// Whether a call is known to have succeeded, known to have been refused, or — the case that
    /// actually matters — left us with no idea. For a request that CREATES something, "no idea"
    /// is not the same as "no", and the caller has to treat it differently.
    /// </summary>
    public enum ZohoCallOutcome { Ok, Rejected, Unknown }

    public readonly record struct ZohoCallResult(string? Body, ZohoCallOutcome Outcome)
    {
        /// <summary>The HTTP status Desk answered with, when it answered at all.</summary>
        public int? Status { get; init; }
    }

    private async Task<ZohoCallResult> SendAsync(HttpMethod method, string path,
        Dictionary<string, object?> payload, CancellationToken ct)
    {
        try
        {
            using var http = await CreateAuthorizedClientAsync(ct);
            // No token: nothing was ever sent, so this is a clean refusal, not an unknown.
            if (http == null) return new ZohoCallResult(null, ZohoCallOutcome.Rejected);

            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode) return new ZohoCallResult(body, ZohoCallOutcome.Ok);

            // 401 usually means the cached token was revoked before its stated expiry. Drop it so
            // the next attempt mints a fresh one rather than replaying a dead token forever.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _accessToken = null;
                _accessTokenExpiresUtc = DateTime.MinValue;
            }

            _logger.LogWarning("[ZOHO] {Method} {Path} → {Status} {Body}",
                method.Method, path, (int)response.StatusCode, Truncate(body, 400));

            // A 5xx means Zoho took the request and then fell over — it may have applied it. A
            // 4xx is a clean refusal and definitely changed nothing.
            return new ZohoCallResult(null, (int)response.StatusCode >= 500
                ? ZohoCallOutcome.Unknown
                : ZohoCallOutcome.Rejected) { Status = (int)response.StatusCode };
        }
        catch (OperationCanceledException)
        {
            // The request was already on the wire. Zoho may have processed it.
            _logger.LogWarning("[ZOHO] {Method} {Path} timed out after {Seconds}s",
                method.Method, path, Options.TimeoutSeconds);
            return new ZohoCallResult(null, ZohoCallOutcome.Unknown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ZOHO] {Method} {Path} threw: {Message}", method.Method, path, ex.Message);
            return new ZohoCallResult(null, ZohoCallOutcome.Unknown);
        }
    }

    /// <summary>A picklist setting, trimmed (Desk matches those values exactly), or the fallback when blank.</summary>
    private static string ConfiguredOr(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// Truncates to a hard character budget, marking the cut so nobody reads a clipped
    /// transcript as the whole conversation. The full record always remains in chat_messages.
    /// </summary>
    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= max) return value;

        const string marker = "… [truncated]";
        return max <= marker.Length
            ? value[..max]
            : value[..(max - marker.Length)] + marker;
    }

    // ── Making an agent's reply fit to show a customer ───────────────────────────────────────

    /// <summary>
    /// Everything a helpdesk staples onto a reply that the person who wrote it never typed, and
    /// that a chat bubble must not show: the quoted history of the whole conversation, the
    /// agent's signature, and Zoho's own satisfaction survey. Desk marks the seams in HTML with
    /// title="beforequote:::" / "sign_holder::start" / "survey_holder::start", but plain text
    /// keeps only the human-readable headers, so those are what we cut on.
    ///
    /// Everything from the first marker onwards goes — a reply is written above its quoted
    /// history, never below it.
    /// </summary>
    private static readonly string[] ReplyCutMarkers =
    {
        "---- On ",                      // Zoho's quoted-history header: "---- On Thu, 15 Apr 2021 ... wrote ----"
        "-----Original Message-----",
        "How would you rate our customer service?",
        "Sent from Zoho Desk",
    };

    /// <summary>
    /// The same seams, found by SHAPE instead of by wording — which is what makes this work when
    /// the Desk portal is not in English.
    ///
    /// Every marker above is an English string, and Zoho localises both the quoted-history header
    /// and the satisfaction survey. On a Hebrew portal the English list matches nothing and the
    /// customer gets the entire conversation quoted back underneath every agent reply. What does
    /// NOT change between languages is the punctuation Zoho builds those blocks from: a line
    /// fenced by runs of dashes, a long rule of underscores, and quoted lines prefixed with "&gt;".
    /// Those survive translation, so they are the reliable cut.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex StructuralCut = new(
        @"^[ \t]*(?:-{3,}.*-{3,}|_{10,}|-{10,}|={10,})[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Multiline
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Where a block of "&gt;" quoted lines that runs to the END of the message begins, or -1.
    ///
    /// The "runs to the end" condition is the entire point, and it is there because the obvious
    /// version was wrong in a way that lost replies. Cutting at the first "&gt;" line unconditionally
    /// erases a reply that OPENS with a quote — an agent quoting the customer's question before
    /// answering it, which is an ordinary thing to do. The body came out empty, the relay read
    /// that as "nothing to say", and because nothing was stored the thread id was never recorded
    /// either, so no later poll retried it. The customer waited for an answer that had been
    /// written and thrown away.
    ///
    /// A quote block that reaches the end is quoted history. One with the agent's own words after
    /// it is not, and is left alone: showing a customer a line they wrote is cosmetic noise,
    /// losing the answer is not.
    /// </summary>
    private static int FindTrailingQuoteBlock(string text)
    {
        var lines = text.Split('\n');
        var start = -1;
        var offset = 0;
        var offsets = new int[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            offsets[i] = offset;
            offset += lines[i].Length + 1;   // +1 for the '\n' that Split consumed
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r').TrimStart(' ', '\t');

            if (line.Length == 0) continue;            // blank lines belong to either side
            if (line.StartsWith('>')) { start = i; continue; }
            break;                                      // real text — the block ends here
        }

        return start < 0 ? -1 : offsets[start];
    }

    /// <summary>
    /// Trims a reply down to what the agent actually wrote. Returns "" when nothing is left,
    /// which the caller treats as "no reply to relay" rather than sending an empty bubble.
    /// </summary>
    public static string TidyReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cut = text.Length;
        foreach (var marker in ReplyCutMarkers)
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < cut) cut = at;
        }

        // Whichever seam comes first wins. The structural match is what carries a non-English
        // portal, but it is checked alongside the worded ones rather than instead of them: the
        // survey has no punctuation of its own to find it by.
        var structural = StructuralCut.Match(text);
        if (structural.Success && structural.Index < cut) cut = structural.Index;

        var quoted = FindTrailingQuoteBlock(text);
        if (quoted >= 0 && quoted < cut) cut = quoted;

        var body = text[..cut];

        // Collapse the runs of blank lines a stripped signature leaves behind, without touching
        // the single blank line a person uses between paragraphs.
        body = System.Text.RegularExpressions.Regex.Replace(body, @"(\r?\n){3,}", "\n\n");
        return body.Trim();
    }

    /// <summary>
    /// Last-resort HTML to text, for the case where Zoho returns no plainText field at all.
    /// Not a general-purpose converter and not trying to be: it drops script/style outright,
    /// turns block boundaries into newlines so sentences do not run together, strips what is
    /// left of the tags and decodes entities.
    /// </summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var t = System.Text.RegularExpressions.Regex.Replace(
            html, @"<(script|style)\b[^>]*>.*?</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        t = System.Text.RegularExpressions.Regex.Replace(
            t, @"<br\s*/?>|</(p|div|li|tr|h[1-6])>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        t = System.Text.RegularExpressions.Regex.Replace(t, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t);
    }
}
