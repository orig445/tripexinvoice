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
    /// </summary>
    public string EscalatedPriority { get; set; } = "High";
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
}

/// <summary>What a new ticket needs to exist. Assembled by the sync worker from our own records.</summary>
public record ZohoTicketDraft(
    string Subject,
    string Description,
    string ContactLastName,
    string ContactEmail,
    Guid SessionId,
    bool Escalated);

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
    /// </summary>
    public async Task<AgentReply?> GetLatestPublicReplyAsync(string ticketId, CancellationToken ct = default)
    {
        if (!Options.IsConfigured || string.IsNullOrWhiteSpace(ticketId)) return null;

        var result = await GetAsync(
            $"api/v1/tickets/{Uri.EscapeDataString(ticketId)}/latestThread?needPublic=true&include=plainText", ct);
        if (result.Outcome != ZohoCallOutcome.Ok || string.IsNullOrWhiteSpace(result.Body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            var root = doc.RootElement;

            var threadId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(threadId)) return null;

            // "out" is Desk's own word for a thread leaving the helpdesk towards the customer.
            // An "in" thread is the customer's own message coming back to us — relaying that to
            // the widget would show the customer their own words in an agent's voice.
            var direction = root.TryGetProperty("direction", out var d) ? d.GetString() : null;
            if (!string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase)) return null;

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

            return new AgentReply(threadId!, text!, author);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("[ZOHO] latestThread for ticket {TicketId} was not readable JSON: {Message}",
                ticketId, ex.Message);
            return null;
        }
    }

    /// <summary>One public reply from a human agent, ready to show a customer.</summary>
    public readonly record struct AgentReply(string ThreadId, string Text, string? AuthorName);

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
                : ZohoCallOutcome.Rejected);
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
    public async Task<string?> CreateTicketAsync(ZohoTicketDraft draft, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return null;

        var payload = new Dictionary<string, object?>
        {
            ["subject"] = Truncate(Options.SubjectPrefix + draft.Subject, 255),
            ["departmentId"] = Options.DepartmentId,
            ["description"] = Truncate(draft.Description, MaxCommentLength),
            ["status"] = draft.Escalated ? Options.EscalatedStatus : Options.AiHandledStatus,
            ["classification"] = Options.Classification,
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
        var priority = (draft.Escalated ? Options.EscalatedPriority : Options.AiHandledPriority)?.Trim();
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
        if (outcome == ZohoCallOutcome.Rejected && payload.Remove("priority"))
        {
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
        string? assignedTo;
        try
        {
            using var doc = JsonDocument.Parse(json);
            id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            assignedTo = doc.RootElement.TryGetProperty("assigneeId", out var aProp) && aProp.ValueKind == JsonValueKind.String
                ? aProp.GetString()
                : null;

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

        return id;
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
    /// </summary>
    public async Task<bool> UpdateStatusAndPriorityAsync(
        string ticketId, string status, string? priority, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return false;

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

        return result.Body != null;
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

    public readonly record struct ZohoCallResult(string? Body, ZohoCallOutcome Outcome);

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
                : ZohoCallOutcome.Rejected);
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
