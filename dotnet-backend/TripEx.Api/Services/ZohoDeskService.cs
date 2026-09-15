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

    /// <summary>Everything that must be present before a single call is worth attempting.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RefreshToken)
        && !string.IsNullOrWhiteSpace(OrgId)
        && !string.IsNullOrWhiteSpace(DepartmentId)
        && !string.IsNullOrWhiteSpace(FallbackContactEmail);
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
        return http;
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

        if (!string.IsNullOrWhiteSpace(Options.SessionIdField))
            payload["cf"] = new Dictionary<string, string> { [Options.SessionIdField] = draft.SessionId.ToString() };

        if (!string.IsNullOrWhiteSpace(Options.AssigneeId))
            payload["assigneeId"] = Options.AssigneeId;

        var (json, outcome) = await SendAsync(HttpMethod.Post, "api/v1/tickets", payload, ct);

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

    /// <summary>Moves a ticket to a specific agent. Separate from UpdateStatusAsync so the two
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

    /// <summary>Moves a ticket's status — used to reopen an AI-handled ticket once it escalates.</summary>
    public async Task<bool> UpdateStatusAsync(string ticketId, string status, CancellationToken ct = default)
    {
        if (!Options.IsConfigured) return false;

        var payload = new Dictionary<string, object?> { ["status"] = status };
        return (await SendAsync(HttpMethod.Patch, $"api/v1/tickets/{ticketId}", payload, ct)).Body != null;
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
}
