using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Chat service — session management, RAG, intent detection, corrections
/// </summary>
public class ChatService
{
    private readonly TripExDbContext _db;
    private readonly OracleAiService _oracle;
    private readonly InvoiceService _invoiceService;
    private readonly GeolocationService _geoService;
    private readonly ILogger<ChatService> _logger;
    private readonly string _supportContact;
    private readonly bool _statusListShortcut;
    private readonly bool _modelAuthoredFirstClarify;
    private readonly string? _sessionTokenSalt;
    private readonly ZohoDeskService _zoho;
    private readonly ZohoTicketSyncQueue _zohoQueue;

    // Data/page-links.json is a large, static, deployment-wide dataset (hundreds of TAS
    // pages) — load it once per process (like log4net's LogDir) rather than per request.
    // Anchored to AppContext.BaseDirectory because IIS in-process hosting changes the
    // process's current directory to C:\Windows\System32\inetsrv (see Program.cs LogDir).
    private static readonly Dictionary<string, PageLinkConfig> _pageLinks;

    // The TAS host + site-folder (e.g. "https://deveu.combtas.com/QA_3_70"), read from the
    // "baseUrl" field at the top of page-links.json. Every page's "url" is just the relative
    // path from there, so promoting this file to a different environment (dev → prod) only
    // ever requires editing this one line — no per-page edits, no code change/redeploy.
    private static readonly string _pageLinksBaseUrl;

    static ChatService()
    {
        (_pageLinks, _pageLinksBaseUrl) = LoadPageLinks();
        (_statusGlossary, _mechanisms) = LoadStatusGlossary();
    }

    // Ground-truth explanations for trip/expense-report statuses and related operational
    // mechanisms (approval rounds, Per Diem, currency, Statement Match, External/Guest users),
    // distilled from real support-ticket history (2026-09-06) rather than guessed — see
    // Data/status-glossary.json. Fed into the clarify-flow's operations path (rule 3b) so
    // Milo's "general guidance for this status" answer is accurate instead of relying only
    // on whatever happens to be in the separate Knowledge Base. Same "edit the JSON, no code
    // deploy" pattern as _pageLinks.
    private static readonly Dictionary<string, string> _statusGlossary;
    private static readonly List<MechanismEntry> _mechanisms;

    // Read-only view for TripEx.Api.Tests — lets the full-catalog link-resolution regression
    // test enumerate every real page-links.json entry without duplicating the load logic.
    public static IReadOnlyDictionary<string, PageLinkConfig> AllPageLinks => _pageLinks;

    // Pages whose description is broad enough to look relevant to almost any admin
    // question (e.g. "System Settings", "Master File") even though they're essentially
    // never the actually-correct answer to a specific question. Excluded from BOTH the
    // AI's visible options (BuildSystemPrompt) AND the post-hoc mentioned-page substring
    // scan in ProcessAsync — a single shared list so the two can't drift out of sync.
    private static readonly HashSet<string> _excludedGenericKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemSetting",         // "System Settings" — global system parameters
        "tbl_system_Master_New", // "Master File" — generic master table
        "ManageTAS",             // "Manage TAS" — general TAS system management
        "Settings_Default",      // "Settings Default" — general default values
        "System_Wizard",         // "System Wizard" — general parameter setup wizard
    };

    // A minimum length guard on any page-name substring match (mentioned-key scan, and the
    // raw-key scrub below) avoids a short, generic label (a few characters) causing a
    // false-positive match against unrelated text.
    private const int MinPageNameMatchLength = 8;

    // Longest an option may be and still work as a button label (see OptionsCanBeButtons). A
    // choice is a label the user clicks and, when clicked, becomes their next message verbatim —
    // so a whole clause is not a choice, it's the model having written prose into the wrong
    // field. Comfortably above the longest fixed option that ships ("Other (Matched / Closed /
    // Pending for Cancel / Cancelled)", 55).
    private const int MaxOptionLabelLength = 60;

    /// <summary>
    /// The same budget for a client that relays through Zoho SalesIQ, which caps a suggestion at
    /// 20 characters and TRIMS anything longer instead of refusing it. That silent trim is the
    /// dangerous part: the label is what comes back as the user's next message, so a trimmed one
    /// no longer matches the option it came from — the orientation answer stops being recognised,
    /// the status shortcut never fires, and the model receives half a sentence. Failing the
    /// button test here instead means the choices are shown as a numbered list, which is
    /// answerable and which this file already knows how to render.
    /// </summary>
    public const int SalesIqOptionLabelLength = 20;

    /// <summary>
    /// The value of ChatRequest.Source that means "this conversation is being relayed by Zoho
    /// SalesIQ, not rendered by the TAS widget". One definition rather than the literal repeated
    /// at each gate: three separate behaviours hang off this answer, and two of them going one way
    /// while the third goes the other is the failure mode worth designing out — a relay judged
    /// non-relay for the ticket gate alone silently duplicates every helpdesk ticket.
    ///
    /// Ordinal-ignore-case because the value is client JSON: "SalesIQ" is how Zoho spells its own
    /// product, and it is the spelling a person configuring the Zobot is most likely to send.
    /// </summary>
    public static bool IsRelaySource(string? source)
        => string.Equals(source?.Trim(), "salesiq", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Can a human support agent actually answer the customer inside this chat window?
    ///
    /// One question with two consequences, and they have to agree. It decides what an escalation
    /// SAYS ("stay here, the reply will arrive in this chat" versus "email support"), and it
    /// decides whether Milo goes quiet afterwards so the agent can take over. Answered in two
    /// places it drifted: the handover message used the relay switch alone, so TripEx's own
    /// "internal" staff chat — which never opens a ticket — was told to wait for a reply nobody
    /// would ever write.
    ///
    /// True only when all three hold: the relay is switched on (a reply can come back at all),
    /// the conversation is mirrored into Desk (see IsMirroredSource — so there is a ticket for an
    /// agent to open), and the caller is the TAS widget, which is the only client that polls
    /// /api/chat/updates. That last one is a positive signal on purpose: Source cannot tell the
    /// widget apart from this repo's own /chat page (both arrive as "web"), and a client that never
    /// polls would be told to wait for a reply it can never display — and then be silenced.
    /// </summary>
    public static bool AgentAnswersHere(bool relayConfigured, string? source, bool isTasWidgetClient)
        => relayConfigured && isTasWidgetClient && IsMirroredSource(source);

    /// <summary>
    /// Is a conversation from this source mirrored into a Zoho Desk ticket? Not "internal" (TripEx's
    /// own staff chat is not customer support) and not a SalesIQ relay (SalesIQ raises its own
    /// ticket, so ours would be a duplicate). The one definition every Zoho path consults — the
    /// enqueue in this class, the recovery sweep and the worker itself — so that no path can open a
    /// ticket the others assume does not exist.
    /// </summary>
    public static bool IsMirroredSource(string? source)
        => !string.Equals(source?.Trim(), "internal", StringComparison.OrdinalIgnoreCase)
           && !IsRelaySource(source);

    /// <summary>
    /// The intent stored on a customer message that needs a PERSON to see it: one that went to the
    /// agent instead of to Milo, or a repeat request for a human in a conversation that already
    /// escalated. It is the durable record of that need — the Zoho worker reads it to reopen a
    /// ticket the agent had closed, and the recovery sweep reads it to retry one that did not reach
    /// the ticket — and it survives a restart between the message and the sync, which an in-memory
    /// flag on the queue item would not. It also shows in the ticket transcript as "[handover]", so
    /// the agent can tell which lines were written to them.
    /// </summary>
    public const string HandoverIntent = "handover";

    /// <summary>
    /// Once a conversation has been handed to a person, Milo stops answering in it.
    ///
    /// Answering alongside the agent is worse than useless: the customer gets two voices, one of
    /// which has not read the ticket, and Milo's reply lands first because it never waits for a
    /// human to type. Worse, it tends to contradict the agent, or re-escalate a conversation a
    /// person is already handling.
    ///
    /// Deliberately NOT ended by the agent closing the ticket. Desk's "Send and Close" closes it
    /// in the same click as the reply, so ending the handover on close would route the customer's
    /// very next line — "thanks, but it still doesn't work" — to Milo instead of to the person
    /// who just answered. A customer writing to a closed ticket reopens it, which is how every
    /// helpdesk treats a reply. The customer gets Milo back by starting a new chat.
    ///
    /// humanInvolved is "Milo escalated it" OR "an agent has already replied in it". The second
    /// half matters because every conversation is mirrored, including the ones Milo handled alone:
    /// an agent can reply on one of those tickets without any escalation, the reply reaches the
    /// widget, and the customer's answer to it is meant for that agent — not for Milo.
    /// </summary>
    public static bool IsHandedOver(bool continuedSession, bool humanInvolved, bool agentAnswersHere)
        => continuedSession && humanInvolved && agentAnswersHere;

    /// <summary>
    /// The reply body for a message forwarded to the agent. The updated widget draws nothing for
    /// a handed-over turn (it checks HandedOver); this text is only for a widget that has not been
    /// updated yet, which renders an empty reply as a stock "Here's where you can find more:" line.
    /// A short receipt is the honest thing to show there — and it means the backend and the widget
    /// can go live in either order.
    /// </summary>
    public static string HandoverReceipt(bool hebrew)
        => hebrew ? "✓ ההודעה הועברה לנציג" : "✓ Sent to the agent";

    /// <summary>
    /// Shown instead when the customer's message could not be stored. Silence would be a lie here:
    /// the message never reached the ticket, so nobody is going to answer it.
    /// </summary>
    public static string HandoverNotDelivered(bool hebrew)
        => hebrew
            ? "ההודעה לא הגיעה לנציג — אפשר לשלוח אותה שוב?"
            : "That message didn't reach the agent — could you send it again?";

    /// <summary>
    /// Turns a stored chat_messages role into one the chat completions API will accept.
    ///
    /// The table's vocabulary is wider than the API's and has just grown again: "agent" is a
    /// reply typed by a human in Zoho Desk. The API takes system, user and assistant and nothing
    /// else, so an unmapped role is a rejected request — and because the row stays in history
    /// forever, that rejection repeats on every later turn. A conversation a person helped with
    /// would be a conversation Milo could never answer in again.
    ///
    /// Everything that is not the user is mapped to assistant, which is the honest reading rather
    /// than a safe default: whoever wrote it, it was said back TO the user, and Milo needs to see
    /// it or its next turn will contradict the human who just resolved the problem.
    /// </summary>
    public static string ToModelRole(string? storedRole)
        => string.Equals(storedRole?.Trim(), "user", StringComparison.OrdinalIgnoreCase)
            ? "user"
            : "assistant";

    /// <summary>
    /// Values of Jwt:Secret that are published in this repository and therefore secret to nobody:
    /// the one appsettings.json ships and the one the production template tells you to replace.
    /// A config layer always supplies SOME value for that key, so "did anyone actually set it"
    /// cannot be answered by a null check alone. Used only to decide whether a foreign
    /// conversation id may be hashed into a session — never to reject a login, which is not this
    /// class's call to make.
    /// </summary>
    public static readonly HashSet<string> PlaceholderSecrets = new(StringComparer.Ordinal)
    {
        "",
        "YOUR_JWT_SECRET_KEY_MIN_32_CHARS_LONG",
        "REPLACE_WITH_A_RANDOM_STRING_AT_LEAST_32_CHARS",
    };

    // How many clarifying questions in a row before the reply also names a human to talk to.
    //
    // This used to be a HARD CAP of 2: the third clarify in a row had its intent rewritten to
    // "escalate" and its own text thrown away, whether or not the conversation was actually
    // going badly. Removed 2026-09-15 at Roi's request — a user who is still answering the
    // questions is making progress, and ending the flow on a turn count threw that progress
    // away. The offer replaces the cap: Milo asks as many questions as it needs to, and every
    // Nth one carries the support address beside it, so leaving is always one line away and
    // never forced. N counts the question being asked right now, so it lands on 3, 6, 9, …
    private const int SupportOfferEveryNClarifications = 3;

    // The TAS trip/expense-report status values, exactly as they appear in the system —
    // supplied directly by the product owner (2026-09-03), NOT derived from any live TAS
    // connection: TripEx.Api's own database has no trip/expense tables at all (see
    // init-db.sql) — it cannot look up a specific customer's real, current status. These lists
    // only power the fixed multiple-choice question; the answer that follows is general
    // guidance for whichever status the user picks, not a live per-customer fact. Split in two
    // because a question about a standalone expense report (no trip involved) only has 3 of
    // the 16 total statuses available to it — showing all 16 there would offer choices that
    // can't actually apply. "Other" bundles the 4 statuses that didn't fit either named list
    // (Matched / Closed / Pending for Cancel / Cancelled) rather than silently dropping them.
    // Update by hand if TAS's own status set ever changes — and note that the exact wording is
    // load-bearing twice over: the chosen option is sent back verbatim as the next user message,
    // and it has to survive the widget's button rules (no commas, no "TID" — see
    // BuildWidgetParamerter), or the options silently revert to a plain text list. Public so the
    // tests pin both properties against the real shipping data rather than a copy of it.
    public static readonly IReadOnlyList<string> TripStatusOptionsForTrip = new[]
    {
        "Draft", "TR Approval", "Coordinator Approval", "Reservations", "Proposal Approval",
        "Approved", "Issued", "Active", "Travel Completed", "Expense Report", "Expense Approval",
        "Expense Approved", "Other (Matched / Closed / Pending for Cancel / Cancelled)",
    };

    public static readonly IReadOnlyList<string> TripStatusOptionsForExpenseOnly = new[]
    {
        "Expense Report", "Expense Approval", "Expense Approved", "Other",
    };

    // The three areas offered by the fixed first orientation question, in both languages.
    // Constants rather than literals at the point of use because they are matched BACK on the
    // following turn: the option the user picks is re-sent verbatim as their next message, and
    // recognising it is what lets that turn skip the model entirely (see IsStatusListTurn).
    // Reworded in one place only, the recognition would silently stop matching and quietly
    // restore a 39-second turn — so the writer and the reader share one definition.
    public static readonly IReadOnlyList<string> OrientationOptionsHe = new[]
    {
        "תפעול שוטף של נסיעות והוצאות", "ניתוח נתונים ודוחות במערכת", "ניהול ושינוי הגדרות במערכת",
    };

    public static readonly IReadOnlyList<string> OrientationOptionsEn = new[]
    {
        "Travel & expense operations", "Data analysis & reports", "System management & settings",
    };

    // Every intent that counts as a "clarifying-type" turn for the consecutive-clarification
    // cap — "clarify" (the fixed 3-way orientation round) plus the two fixed status-list
    // follow-ups (kept as distinct intent values for logging: which one fired tells you
    // whether the user was on the trip or the expense-only path).
    private static readonly HashSet<string> ClarifyTypeIntents = new(StringComparer.Ordinal)
    {
        "clarify", "clarify_status_trip", "clarify_status_expense",
    };

    // Counts, from the end, an unbroken run of assistant turns whose Intent was one of
    // ClarifyTypeIntents — pulled out as its own testable unit (public + static, same
    // reasoning as ResolvePageOverride below) since an off-by-one or interleaving mistake
    // here directly controls the consecutive-clarification cap enforced in ProcessAsync.
    // Stops at the first assistant message that ISN'T one of those, so only a run
    // immediately preceding the current turn counts — an old clarify from earlier in a
    // long-lived session that was already followed by a real answer must not count against
    // a brand new, unrelated question. User messages in between are skipped, not counted as
    // breaks.
    public static int CountTrailingConsecutiveClarifications(
        IReadOnlyList<(string Role, string Content, string? Intent)> historyRows)
    {
        var count = 0;
        for (var i = historyRows.Count - 1; i >= 0; i--)
        {
            if (historyRows[i].Role != "assistant") continue;
            if (historyRows[i].Intent is not string it || !ClarifyTypeIntents.Contains(it)) break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Turns whatever a caller sent as its conversation id into the Guid this service keys
    /// sessions by. A real Guid is used as-is; anything else is hashed into a stable one.
    ///
    /// It used to be Guid.TryParse and nothing else, so a token in any other shape was silently
    /// dropped and EVERY turn opened a fresh session — no history, no second clarifying round,
    /// no status shortcut, and a bot that answers each message as if it were the first. That is
    /// invisible from the outside: it looks like the model forgetting, not like a rejected id.
    /// Zoho's own conversation ids (1473081000000457007) are exactly that shape, so any future
    /// integration keyed on them would have hit it.
    ///
    /// SHA-256, not string.GetHashCode: GetHashCode is randomised per process on .NET Core, so
    /// the same conversation would land on a different session after every restart — the same
    /// bug, just rarer and harder to see. Truncating a 256-bit digest to 128 bits leaves
    /// collisions far below the level worth engineering against.
    ///
    /// The salt is what keeps this from undoing the 2026-09-08 cross-conversation history fix.
    /// A Guid session id is 122 random bits and cannot be guessed; a FOREIGN id generally can be
    /// — Zoho hands out consecutive numbers — so hashing one unsalted would publish a formula for
    /// turning "the conversation before this one" into a live session key. CanResumeSessionAsync
    /// cannot catch that: every X-Api-Key caller authenticates as the same system principal, so
    /// ownership passes for any widget visitor (see its own remarks). With a server-side salt the
    /// mapping is unguessable without the secret, and still perfectly stable with it.
    ///
    /// An empty salt therefore does NOT mean "hash it anyway" — it means refuse, and a foreign id
    /// goes back to starting a fresh conversation, exactly as it did before this method existed.
    /// Degrading to the old behaviour is safe; degrading to a guessable one is not.
    ///
    /// Guid.Empty means "no usable token" and is never a valid session id.
    /// </summary>
    public static Guid ResolveSessionToken(string? token, string? salt)
    {
        if (string.IsNullOrWhiteSpace(token)) return Guid.Empty;
        if (Guid.TryParse(token, out var parsed)) return parsed;
        if (string.IsNullOrEmpty(salt)) return Guid.Empty;

        // HMAC rather than SHA256(salt + token): with plain concatenation, salt "ab" + token
        // "c" and salt "a" + token "bc" hash to the same value, and a caller able to influence
        // either half could aim at another session. HMAC keys the hash instead of prefixing it,
        // so no separator is needed and none can be smuggled past one.
        var digest = System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(salt),
            System.Text.Encoding.UTF8.GetBytes(token.Trim()));
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Did the user just answer the fixed orientation question with its FIRST option — the
    /// travel &amp; expense operations branch? Compared against the shipped option strings in both
    /// languages, through the same normalisation every other option match uses, because the
    /// widget re-sends a button's label verbatim and a stray quote or trailing period must not
    /// read as a different answer.
    /// </summary>
    public static bool IsOperationsOrientationAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeForOptionMatch(StripBidiMarks(text));
        return normalized.Equals(NormalizeForOptionMatch(OrientationOptionsHe[0]), StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(NormalizeForOptionMatch(OrientationOptionsEn[0]), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when this turn's reply is ALREADY DECIDED before the model is asked anything: the
    /// last assistant turn was the fixed orientation question and the user picked its operations
    /// option, so the reply is the fixed status list and the only open question is which of the
    /// two lists to show.
    ///
    /// Worth detecting because of what it costs otherwise. Measured against real production
    /// usage on 2026-09-08, this exact turn spent 3,561 thinking tokens and 39.3 seconds — the
    /// slowest turn of the whole conversation — to write text that the clarify block below then
    /// throws away and replaces with a hard-coded list. Every other turn in that sample sat
    /// between 11 and 23 seconds.
    /// </summary>
    public static bool IsStatusListTurn(
        IReadOnlyList<(string Role, string Content, string? Intent)> historyRows, string? userText)
    {
        if (historyRows == null || !IsOperationsOrientationAnswer(userText)) return false;

        // The most recent assistant turn has to be the FIXED orientation round. "clarify" is
        // also the intent of the model-authored SECOND round, but that one never offers these
        // three options — so a user message equal to one of them cannot have come from it.
        for (var i = historyRows.Count - 1; i >= 0; i--)
        {
            if (historyRows[i].Role != "assistant") continue;
            return historyRows[i].Intent == "clarify";
        }
        return false;
    }

    /// <summary>
    /// The question that triggered the orientation round, so the trip-vs-expense choice is made
    /// against what the user actually asked rather than against the button they just clicked —
    /// which names an area and says nothing about either.
    /// </summary>
    public static string? FindQuestionBeforeOrientation(
        IReadOnlyList<(string Role, string Content, string? Intent)> historyRows)
    {
        if (historyRows == null) return null;

        var orientation = -1;
        for (var i = historyRows.Count - 1; i >= 0; i--)
        {
            if (historyRows[i].Role == "assistant") { orientation = i; break; }
        }
        if (orientation < 0) return null;

        for (var i = orientation - 1; i >= 0; i--)
            if (historyRows[i].Role == "user") return historyRows[i].Content;

        return null;
    }

    /// <summary>
    /// Decides what a "travel &amp; expense operations" answer should actually lead to, with a
    /// four-line prompt instead of the full one. Three outcomes:
    ///   "clarify_status_trip"    — show the 13-status trip list
    ///   "clarify_status_expense" — show the 3-status standalone-expense list
    ///   null                     — NOT a status question at all; fall through to the full prompt
    ///
    /// The null case is the important one, and it is why this is three-way and not two-way.
    /// Picking "operations" was being treated as "I want to know about statuses", which it is
    /// not: it is the honest answer for a HOW-TO question about a trip too. Seen in production
    /// 2026-09-15 — "איך אני מוציא דוח הוצאות של טיסה" (how do I export a flight's expense
    /// report) was, by Milo's own recorded reasoning, a choice between two specific REPORTS; the
    /// user answered "operations" because a trip's expense report plainly is an operational
    /// thing, and the status list then took the conversation somewhere it could not answer from.
    ///
    /// A status list is right for "where has my trip got to"; it is a dead end for "how do I".
    /// Returning null costs that turn a second, full round trip, which is the right trade: a
    /// slower correct answer beats a fast wrong one.
    ///
    /// Both status outcomes still default to the TRIP list on any failure — the longer and more
    /// complete of the two, and the one rule 3b already names "when genuinely unclear which".
    /// </summary>
    private async Task<string?> ResolveStatusListIntentAsync(string? originalQuestion, CancellationToken ct)
    {
        const string trip = "clarify_status_trip";
        const string expense = "clarify_status_expense";

        // With nothing to judge, the shortcut declines rather than guessing. The full prompt
        // then handles the turn exactly as it did before any of this existed.
        if (string.IsNullOrWhiteSpace(originalQuestion)) return null;

        var messages = new List<OracleMessage>
        {
            new()
            {
                Role = "system",
                Content =
                    "Reply with ONE word and nothing else: TRIP, EXPENSE or NEITHER.\n" +
                    "Below is a user's question about a travel & expense management system. They have " +
                    "just said it concerns day-to-day travel and expense operations.\n" +
                    "Reply NEITHER if the question asks HOW to do something — how to create, export, " +
                    "submit, approve, attach, find or produce something, or which screen or report to " +
                    "use. Those need an answer, not a question about status.\n" +
                    "Otherwise the question is about the current state or progress of something. Reply " +
                    "EXPENSE if that is a standalone expense report with NO trip involved, and TRIP in " +
                    "every other case, including any doubt between TRIP and EXPENSE.",
            },
            new() { Role = "user", Content = originalQuestion! },
        };

        try
        {
            // 512, not the 4096 floor the conversational path needs: the answer is one word, and
            // this budget still has to cover the thinking tokens Gemini spends out of the same
            // allowance. If a trivial question somehow exhausts it, the reply comes back empty
            // and the shortcut declines, which is the safe direction.
            var raw = await _oracle.ChatAsync(messages, maxTokens: 512, temperature: 0, ct);

            // Matched as whole words anywhere in the reply rather than by a prefix test, so a
            // model that wraps its answer in quotes, JSON, or a stray sentence still parses.
            var match = System.Text.RegularExpressions.Regex.Match(
                raw ?? "", @"\b(TRIP|EXPENSE|NEITHER)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // No recognisable word is not a vote for the status list. Decline and let the full
            // prompt decide, the same as an outright NEITHER.
            if (!match.Success) return null;

            var answer = match.Groups[1].Value;
            if (answer.Equals("NEITHER", StringComparison.OrdinalIgnoreCase)) return null;
            return answer.Equals("EXPENSE", StringComparison.OrdinalIgnoreCase) ? expense : trip;
        }
        catch (Exception ex)
        {
            // Declining, not defaulting to a list: a failure here tells us nothing about whether
            // a status list is even the right shape of answer, and the full prompt can still do
            // the whole job.
            _logger.LogWarning(ex, "[CLARIFY-FAST] the status-shape call failed — falling through to the full prompt.");
            return null;
        }
    }

    // Mirrors the top-level shape of page-links.json: { "baseUrl": "...", "pages": [...] }.
    private class PageLinksFile
    {
        public string BaseUrl { get; set; } = "";
        public List<PageLinkConfig> Pages { get; set; } = new();
    }

    private static (Dictionary<string, PageLinkConfig>, string) LoadPageLinks()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "page-links.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"⚠️ [CHAT] Data/page-links.json not found at '{path}' — Milo will answer without page links.");
                return (new(), "");
            }
            var file = JsonSerializer.Deserialize<PageLinksFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var dict = file.Pages
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"✅ [CHAT] Loaded {dict.Count} page links from Data/page-links.json (baseUrl={file.BaseUrl})");
            return (dict, file.BaseUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Failed to load Data/page-links.json — Milo will answer without page links: {ex.Message}");
            return (new(), "");
        }
    }

    // Mirrors the top-level shape of status-glossary.json: { "statuses": [...], "mechanisms": [...] }.
    private class StatusGlossaryFile
    {
        public List<StatusGlossaryEntry> Statuses { get; set; } = new();
        public List<MechanismEntry> Mechanisms { get; set; } = new();
    }

    private static (Dictionary<string, string>, List<MechanismEntry>) LoadStatusGlossary()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "status-glossary.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"⚠️ [CHAT] Data/status-glossary.json not found at '{path}' — Milo will answer status questions from the Knowledge Base only.");
                return (new(), new());
            }
            var file = JsonSerializer.Deserialize<StatusGlossaryFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var dict = file.Statuses
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .ToDictionary(s => s.Key, s => s.Explanation, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"✅ [CHAT] Loaded {dict.Count} status-glossary entries + {file.Mechanisms.Count} mechanism notes from Data/status-glossary.json");
            return (dict, file.Mechanisms);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Failed to load Data/status-glossary.json — Milo will answer status questions from the Knowledge Base only: {ex.Message}");
            return (new(), new());
        }
    }

    // Majority-language check, not mere presence — a reply that's fundamentally in English
    // can still quote one Hebrew term (e.g. a report's Hebrew label) without being a Hebrew
    // reply; counting which script actually dominates avoids misreading that as Hebrew.
    private static bool IsHebrewDominant(string text)
    {
        int hebrew = 0, latin = 0;
        foreach (var ch in text)
        {
            if (ch >= (char)0x0590 && ch <= (char)0x05FF) hebrew++;
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) latin++;
        }
        return hebrew > latin;
    }

    // Renders one option of a fixed question as plain, readable text.
    //
    // This used to emit <a href="javascript:...(getElementById('message-box')...
    // getElementById('send-btn')...)"> — an anchor that filled one specific widget's message box
    // and clicked its own send button. That was abandoned on 2026-09-07 because it hardcoded
    // ANOTHER application's private DOM ids into this backend, and:
    //   * it bailed out silently (`if(!b)return;`) on any widget whose ids differ — which is
    //     exactly what a second embedded widget did, producing links that looked clickable and
    //     did nothing, with no error anywhere to explain why;
    //   * the whole href was HTML-entity-encoded, so any consumer that does NOT render "text" as
    //     raw HTML (e.g. this repo's own React widget, which renders it as escaped text) showed
    //     the user a wall of `&#39;` garbage instead of an option;
    //   * a javascript: URL is blocked outright by a strict Content-Security-Policy, which an
    //     embedded iframe may acquire at any time without telling us.
    // Plain text has none of those failure modes: the user can read the option and type it, on
    // every consumer, with no assumptions about the frontend at all. A widget that wants real
    // buttons should read ChatResponse.QuickReplies — the same options as clean, unnumbered,
    // unencoded strings — and send the chosen string as the next message. That field is already
    // populated on every one of these turns and needs no backend change to start using.
    private static string BuildClickableOption(string optionText) => optionText;

    // The same options, flattened for the TAS widget's `paramerter` field (see
    // ChatResponse.Paramerter for why it is spelled that way). The widget does
    // paramerter.split(","), which imposes two constraints — and in both failure cases this
    // returns null so the numbered plain-text list already in "text" carries the options alone:
    //   * A comma inside an option would split it into two bogus buttons. Rewriting the label is
    //     not an option either: the widget sends the button's text back verbatim as the next user
    //     message, so an altered label would arrive as an answer that matches no known option.
    //   * "TID" anywhere in the string switches the widget to a different trip-link rendering.
    //     Matched case-sensitively, exactly as the widget matches it.
    private static string? BuildWidgetParamerter(List<string> options)
    {
        if (options.Count == 0) return null;
        if (options.Any(o => o.Contains(','))) return null;

        var flattened = string.Join(", ", options.Select(o => o.Trim()));
        return flattened.Contains("TID", StringComparison.Ordinal) ? null : flattened;
    }

    // Will these options actually reach the user as clickable buttons? That is the one and only
    // case in which ALSO listing them inside "text" shows them twice — which is what the TAS
    // widget did: it renders `<div>${response.text}</div>` immediately followed by a button per
    // paramerter entry (verified in its live source, DEV_AI_2/assets/script/app.js), so a fixed
    // question carrying its own numbered list came out as the list and then the same options
    // again as buttons.
    //
    // Both halves of the condition matter:
    //   * clientRendersParamerter — only the TAS widget reads that field (see
    //     ChatRequest.IsTasWidgetClient). For every other caller the numbered list is the only
    //     way the options reach the user, so it has to stay.
    //   * OptionsCanBeButtons — the options themselves have to be fit for a button. When they
    //     are not, the widget shows NO buttons, and the list has to stay for those too or the
    //     question becomes unanswerable.
    // Public + static, like ResolvePageOverride, so the tests exercise the shipping logic.
    public static bool OptionsRenderAsButtons(
        List<string> options, bool clientRendersParamerter, int maxLabelLength = MaxOptionLabelLength)
        => clientRendersParamerter && OptionsCanBeButtons(options, maxLabelLength);

    // Is this set of options fit to be rendered as buttons at all, for any client? The single
    // place that answers it, so ChatResponse.QuickReplies (and the Paramerter derived from it)
    // can never disagree with what the reply text shows.
    //   * At least two — one button is not a choice, it is a dead end with no way to say
    //     "neither", and the question it belongs to was a choice between alternatives.
    //   * Short enough to read on a button. A whole clause is the model having written prose
    //     into the wrong field; it is still shown, as the numbered list, just not clickable.
    //   * Whatever the widget's own split(",") / "TID" rules accept (BuildWidgetParamerter).
    // No cap on how MANY: the prompt asks for 2-4 and a longer set means the model improvised,
    // but silently dropping choices the user was asked to pick between is worse than showing
    // more buttons than intended.
    // maxLabelLength defaults to the widget's budget, so every existing caller and test keeps the
    // behaviour it had; only a relay with a tighter cap of its own passes something smaller.
    public static bool OptionsCanBeButtons(List<string> options, int maxLabelLength = MaxOptionLabelLength)
        => options.Count >= 2
           && options.All(o => o.Length <= maxLabelLength)
           && BuildWidgetParamerter(options) != null;

    // One clarifying question plus its options, rendered ONCE: as buttons alone where the client
    // draws them, otherwise as the numbered plain-text list the question can't do without.
    public static string ComposeClarifyText(string question, List<string> options, bool optionsRenderAsButtons)
    {
        if (options.Count == 0) return question;

        // Only the two FIXED questions are guaranteed to be the question alone — the second,
        // model-authored round is whatever the model wrote, and the prompt asking it to keep the
        // choices out of "text" is a request, not a guarantee. So drop any line of the question
        // that is just one of the options restated, before deciding what to append. Without this
        // a disobedient reply shows every choice twice all over again, which is the exact bug
        // this whole path exists to prevent.
        question = StripOptionLines(question, options);

        if (optionsRenderAsButtons) return question;

        var numbered = string.Join("\n", options.Select((s, i) => $"{i + 1}. {BuildClickableOption(s)}"));
        return $"{question}\n{numbered}";
    }

    // Removes whole lines that merely restate one of the options (with or without the numbering
    // or bullet the model may have added). Only whole-line matches: "the older report, or the
    // newer one?" IS the question and must survive even though both options appear inside it.
    public static string StripOptionLines(string question, List<string> options)
    {
        var kept = question
            .Split('\n')
            .Where(line => !options.Any(o => string.Equals(
                NormalizeForOptionMatch(line), NormalizeForOptionMatch(o), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Trailing blanks are left behind by the removed lines; a fully-stripped question would
        // mean the model wrote nothing but the list, in which case keep the original rather than
        // send an empty bubble.
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        var stripped = string.Join("\n", kept).TrimEnd();
        return string.IsNullOrWhiteSpace(stripped) ? question : stripped;
    }

    // The answer choices from the second, model-authored clarifying round, tidied into labels.
    // Only PER-ITEM cleaning happens here — nothing is rejected for being too long or for there
    // being too many, because whether the set can be BUTTONS is a separate question
    // (OptionsCanBeButtons) with a separate answer: show them as a numbered list instead. The
    // prompt tells the model to keep the choices out of "text", so discarding them here would
    // leave the user a question with no answers anywhere.
    public static List<string> CleanModelOptions(IEnumerable<string>? raw)
    {
        var cleaned = new List<string>();
        if (raw == null) return cleaned;

        foreach (var option in raw)
        {
            if (string.IsNullOrWhiteSpace(option)) continue;
            var text = StripListMarker(option);
            if (text.Length == 0) continue;
            // A label is words, never markup. The widget interpolates it into the button's
            // innerHTML (`<span>${p}</span>`, unescaped — only its data-message attribute is
            // escaped), and "text" is innerHTML there too, so anything angle-bracketed would be
            // parsed as HTML in the host page. This field is new, so it is not inheriting that
            // exposure: an option containing markup is not a label the model should have
            // written, and is dropped.
            if (text.Contains('<') || text.Contains('>')) continue;
            // Two labels that differ only in case, punctuation or emphasis are one choice to the
            // user but two identical-looking buttons on screen.
            if (cleaned.Any(c => string.Equals(
                    NormalizeForOptionMatch(c), NormalizeForOptionMatch(text), StringComparison.OrdinalIgnoreCase)))
                continue;
            cleaned.Add(text);
        }

        return cleaned;
    }

    // "1. Draft" / "2) Draft" / "- Draft" / "• Draft" → "Draft". Numbering is presentation the
    // code owns, never part of the label that gets echoed back as the next user message.
    //
    // The (?!\d) matters: without it the marker alternative also ate the start of any label that
    // legitimately opens with one or two digits and a dot — "1.4.2026" became "4.2026" and
    // "10.2025" became "2025". dd.mm.yyyy is the Israeli date format in a Hebrew-facing bot, so
    // "which period do you mean?" is a realistic question for this very round, and a corrupted
    // label is worse than an unstripped one: the widget sends it back verbatim as the next
    // message. A digit right after the separator is never a list marker.
    //
    // Bidi marks come off first. They are Unicode category Cf, not whitespace, so Trim() leaves
    // them — and a model writing a numbered list in Hebrew puts a RLM before the digit precisely
    // so it displays correctly. Left in, they block this ^-anchored strip entirely.
    private static string StripListMarker(string line) =>
        System.Text.RegularExpressions.Regex.Replace(
            StripBidiMarks(line).Trim(), @"^(?:\d{1,2}[.)](?!\d)|[-*•–—])\s*", "").Trim();

    // Bidi control marks (RLM/LRM and the embedding/isolate controls) are invisible and carry no
    // meaning for matching, but they make two otherwise identical strings compare unequal.
    private static string StripBidiMarks(string s) => new string(s.Where(ch =>
        (ch < (char)0x200B || ch > (char)0x200F) &&
        (ch < (char)0x202A || ch > (char)0x202E) &&
        (ch < (char)0x2066 || ch > (char)0x2069)).ToArray());

    // Comparison form ONLY — never a label and never shown. Used to decide "is this line of the
    // question just one of the options restated?" and "are these two options the same choice?".
    //
    // It has to be more forgiving than the label cleaning, because a model that lists its own
    // choices decorates them: "1. Budget by Division." with a full stop, "- **Budget by
    // Division**" in bold, or the label in quotes. Exact equality missed every one of those, and
    // the line then survived into the text while the same option also rendered as a button —
    // the "written twice" bug back again, just harder to spot.
    private static string NormalizeForOptionMatch(string s)
    {
        var text = StripListMarker(s);
        // Markdown emphasis and quoting, anywhere in the string. Safe because this value is
        // thrown away after the comparison.
        text = text.Replace("*", "").Replace("_", "").Replace("`", "")
                   .Replace("\"", "").Replace("'", "").Replace("״", "").Replace("׳", "")
                   .Replace("“", "").Replace("”", "").Replace("‘", "").Replace("’", "");
        // Sentence punctuation the model adds when it writes a choice as a line of prose.
        return text.Trim().TrimEnd('.', ',', ';', ':', '!', '?', '־', '-', '–', '—').Trim();
    }

    /// <summary>
    /// Swaps any literal page key the model leaked into visible text for that page's own
    /// human-readable name. Applied to everything the user can read — the reply body and the
    /// option labels alike — so the "never show an internal identifier" rule holds no matter
    /// which field the key landed in. Not static: it reads the loaded page catalog.
    /// </summary>
    private string ScrubRawPageKeys(string text, bool hebrew)
    {
        foreach (var kv in _pageLinks)
        {
            if (kv.Key.Length < MinPageNameMatchLength) continue;
            if (!text.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
            var replacement = hebrew ? kv.Value.Label : kv.Value.LabelEn;
            if (!string.IsNullOrWhiteSpace(replacement))
                text = text.Replace(kv.Key, replacement, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    // Given the AI's own stated "page" and its full "text" reply, returns the page key that
    // should actually be linked. Public + static so TripEx.Api.Tests can run it directly
    // against the real production logic — the same 366-entry catalog this loads — as a
    // permanent regression test (`dotnet test`), rather than a one-off reimplementation that
    // could silently drift from what actually ships. See git history 2026-08-17 for why this
    // exists and what it fixed (the "ManageTAS" / "Travel Status" family collision bugs).
    public static string? ResolvePageOverride(string? page, string responseText)
    {
        // The model has proven far more reliable at naming the exact right specific report
        // INLINE in its own "text" than at keeping the separate structured "page" field in
        // sync with it (observed repeatedly: text correctly names the report while "page"
        // still says the general hub). So scan the text itself for any known page's name —
        // by design the model now names reports by their human-readable Label/LabelEn, never
        // the raw key (users must never see the internal key), so that's what we scan for —
        // and prefer the EARLIEST one mentioned over whatever "page" says. This derives the
        // link from what the user actually reads, not a second, less reliable field.
        // Hub/Navigation entries are excluded since they're the fallback we're overriding.
        // A minimum length guard on the candidate strings avoids a short, generic label
        // (a few characters) causing a false-positive match against unrelated text.
        // Strip Unicode bidi control marks first — the model sometimes inserts them (e.g.
        // U+200F RLM) around an embedded LTR name inside Hebrew text, which silently breaks
        // a plain substring match against the clean dictionary value.
        //
        // Excluding by Category=="Navigation" alone isn't enough: some non-Navigation pages
        // (e.g. an Administrator-category "Analysis Reports" admin screen for managing report
        // definitions) happen to share the same hub-like label as a real Navigation entry
        // ("דוחות ניתוח" is literally the tail of the Navigation entry's "מעבר לדוחות ניתוח").
        // Every Reports answer's boilerplate opening line ("go to Analysis Reports") contains
        // that phrase BEFORE the specific report name mentioned later, so without this check
        // the earliest-match rule always locked onto that admin page instead of the report —
        // live-tested and confirmed 2026-08-17. So also drop any candidate that is itself a
        // substring of a Navigation entry's Label/LabelEn — that marks it as a duplicate of
        // the hub we're already excluding, regardless of which category it happens to live in.
        const int minMatchLength = MinPageNameMatchLength;
        var textForKeyScan = new string(responseText.Where(ch =>
            (ch < (char)0x200B || ch > (char)0x200F) &&
            (ch < (char)0x202A || ch > (char)0x202E) &&
            (ch < (char)0x2066 || ch > (char)0x2069)).ToArray());
        var navigationPhrases = _pageLinks.Values
            .Where(p => p.Category == "Navigation")
            .SelectMany(p => new[] { p.Label, p.LabelEn })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        // When one candidate's name is itself a prefix of another's (e.g. "Company Segment"
        // vs "Company Segment Manager"), both match at the SAME starting index in the text
        // if the AI wrote the longer name — a plain OrderBy(Index) then breaks that tie by
        // JSON file order, not by which name is actually the more specific/correct match.
        // Prefer the longer (more specific) match on a tie.
        var mentionedKey = _pageLinks
            .Where(kv => kv.Value.Category != "Navigation" && !_excludedGenericKeys.Contains(kv.Key))
            .SelectMany(kv => new[] { kv.Value.LabelEn, kv.Value.Label, kv.Value.Key }
                .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= minMatchLength
                    && !navigationPhrases.Any(nav => nav.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Select(s => new { kv.Key, Index = textForKeyScan.IndexOf(s, StringComparison.OrdinalIgnoreCase), Length = s.Length }))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .ThenByDescending(x => x.Length)
            .Select(x => x.Key)
            .FirstOrDefault();
        // Only let the substring scan OVERRIDE the AI's own "page" field when that field
        // needs correcting in the first place (missing, a Navigation hub, an excluded
        // generic key, or an invalid/hallucinated key) — the documented failure mode this
        // scan exists for (text names a specific report while "page" lazily still says the
        // general hub). When the AI already committed to a valid, specific page, trust it:
        // otherwise a short/common page name (e.g. "Travel Status", "Suppliers", "Aircraft")
        // that merely appears in passing, earlier in the text than the AI's real answer,
        // silently hijacks an already-correct link — confirmed via static analysis on
        // 2026-08-17 that "Travel Status" alone collides with 26 other entries' own text.
        var pageIsAlreadySpecific = !string.IsNullOrEmpty(page)
            && _pageLinks.TryGetValue(page, out var existingPageEntry)
            && existingPageEntry.Category != "Navigation"
            && !_excludedGenericKeys.Contains(page);
        return (mentionedKey != null && !pageIsAlreadySpecific) ? mentionedKey : page;
    }

    // Intent → Actions mapping
    private static readonly Dictionary<string, (List<string> Actions, string? RedirectPage)> ActionMapping = new()
    {
        ["help"]             = (new(), null),
        ["escalate"]         = (new(), null),
        ["clarify"]          = (new(), null),
        ["clarify_status_trip"]     = (new(), null),
        ["clarify_status_expense"]  = (new(), null),
        ["scan"]             = (new() { "Camera" }, null),
        ["expense"]          = (new(), null),
        ["expense_complete"] = (new(), null),
        ["bi"]               = (new() { "DisplayResults" }, null),
        ["online"]           = (new(), null),
        ["online_complete"]  = (new(), null),
        ["general"]          = (new(), null),
    };

    public ChatService(
        TripExDbContext db,
        OracleAiService oracle,
        InvoiceService invoiceService,
        GeolocationService geoService,
        ILogger<ChatService> logger,
        IConfiguration configuration,
        ZohoDeskService zoho,
        ZohoTicketSyncQueue zohoQueue)
    {
        _db = db;
        _oracle = oracle;
        _invoiceService = invoiceService;
        _geoService = geoService;
        _logger = logger;
        _supportContact = configuration["Support:Contact"] ?? "support@tripex.io";

        // Off switch for the status-list shortcut, so undoing it is a config edit on the server
        // and an app restart — not a code change, a rebuild and a publish. It changes two things
        // at once (the turn stops costing ~39 s, and the status list stops depending on the
        // model choosing to show it), and only real traffic will say whether the second one is
        // wanted. Anything other than a literal "false" leaves it on, so a typo cannot silently
        // disable it.
        _statusListShortcut = !string.Equals(
            configuration["Milo:StatusListShortcut"], "false", StringComparison.OrdinalIgnoreCase);

        // Off switch for the model-authored FIRST clarifying question. This one changes the most
        // visible thing in the whole flow — the opening question every customer sees — so it can
        // go back to the fixed three-way orientation question with a config edit and a restart,
        // no deploy. Same rule as above: only a literal "false" turns it off.
        _modelAuthoredFirstClarify = !string.Equals(
            configuration["Milo:ModelAuthoredFirstClarify"], "false", StringComparison.OrdinalIgnoreCase);

        // The key that makes a foreign conversation id unguessable (see ResolveSessionToken).
        // Jwt:Secret rather than a new setting: it is already required in production, already at
        // least 32 characters, and already the one value nobody is tempted to put in a document.
        // Rotating it restarts every relayed conversation and nothing else — the TAS widget sends
        // real Guids, which never touch this. Absent, the feature turns itself off rather than
        // falling back to a guessable mapping.
        //
        // "Absent" has to include the shipped placeholders, and that is the whole reason this is
        // not a plain null check. appsettings.json carries Jwt:Secret with a placeholder value, so
        // configuration["Jwt:Secret"] is NEVER null — a deployment that forgot to override it in
        // appsettings.Production.json would key this on a string published in the repository,
        // which is no better than no salt at all and would look configured while being wide open.
        _sessionTokenSalt = PlaceholderSecrets.Contains(configuration["Jwt:Secret"]?.Trim() ?? "")
            ? null
            : configuration["Jwt:Secret"];

        if (string.IsNullOrEmpty(_sessionTokenSalt))
            _logger.LogWarning("[CHAT] Jwt:Secret is unset or still the example placeholder — a non-Guid conversation id will start a fresh session instead of resuming one");

        _zoho = zoho;
        _zohoQueue = zohoQueue;
    }

    /// <summary>
    /// Whether this caller may continue an existing conversation. Without this check any caller
    /// holding a session GUID could resume someone else's conversation and read its history back
    /// out of the model's context — demonstrated against live QA on 2026-09-08 using a different
    /// API token. A foreign or stale token is ignored rather than rejected, so the worst outcome
    /// for a legitimate user is simply starting a fresh conversation.
    ///
    /// Deliberately a weak boundary on the widget path, and worth being precise about why: every
    /// X-Api-Key/Token caller authenticates as the same system principal
    /// (ChatController.ApiKeySystemUserId), so this separates real logged-in users from each
    /// other but NOT two widget visitors. Doing that properly needs a per-visitor id stored on
    /// the session row — the widget does now supply customerId (see ChatRequest) — and that is a
    /// schema change, not this fix.
    ///
    /// NeedsRow separates the two reasons this says yes, because they are not the same situation:
    /// the session is this caller's own AND has a row, or there is no row at all. The second one
    /// has to be told apart, or the conversation runs "rowless" forever — see the caller.
    /// </summary>
    /// <summary>
    /// The short, customer-facing number of this conversation's Zoho ticket, or null.
    ///
    /// Null is an ordinary answer with several ordinary causes: Zoho is switched off, the ticket
    /// has not been opened yet (a background worker does that, so the first turn of a conversation
    /// usually beats it), the row predates the column, or the database is simply unavailable.
    /// None of them is worth failing a reply over — a chat answer that works without a reference
    /// number beats an error that has one.
    /// </summary>
    private async Task<string?> LookupTicketNumberAsync(Guid sessionId)
    {
        if (!_zoho.Options.IsConfigured || sessionId == Guid.Empty) return null;

        try
        {
            return await _db.ChatSessionTickets
                .Where(t => t.SessionId == sessionId)
                .Select(t => t.ZohoTicketNumber)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CHAT] Ticket number not read for session {SessionId}: {Message}",
                sessionId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Is a person involved in this conversation — did Milo escalate it, or has an agent already
    /// replied in it? False whenever it cannot be told: a new conversation, a check the caller does
    /// not need, or a database that will not answer.
    ///
    /// False is the safe side of that doubt on purpose: a customer Milo answers while an agent is
    /// also on the ticket gets one reply too many, but a customer Milo ignores because a read
    /// failed gets none at all.
    /// </summary>
    /// <summary>
    /// The query behind IsHumanInvolvedAsync, exposed so a test can prove it translates to SQL —
    /// a LINQ shape EF cannot translate compiles fine and only throws at runtime, on the chat path.
    /// </summary>
    public static IQueryable<bool> HumanInvolvedQuery(TripExDbContext db, Guid sessionId)
        => db.ChatSessions
            .Where(s => s.Id == sessionId)
            .Select(s => s.Escalated
                         || db.ChatMessages.Any(m => m.SessionId == s.Id
                                                     && m.Role == ZohoAgentReplyService.AgentRole));

    private async Task<bool> IsHumanInvolvedAsync(Guid sessionId, bool worthAsking)
    {
        if (!worthAsking) return false;

        try
        {
            return await HumanInvolvedQuery(_db, sessionId).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[HANDOVER] session={SessionId} escalation state not read — Milo answers this turn: {Message}",
                sessionId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// A message the customer sent after the hand-off: on to the ticket, no reply from Milo.
    ///
    /// The message itself was already stored by the caller, tagged with HandoverIntent. What is
    /// left is making sure a person sees it: the Zoho worker posts it on the ticket, and — because
    /// of that tag — reopens the ticket if the agent had closed it. A customer who replies after
    /// "Send and Close" is not finished, and the ticket should say so.
    ///
    /// Deliberately NOT a re-push of the escalation. That PATCH also sets the escalation priority,
    /// and repeating it on every customer line would undo whatever the agent had set — an Urgent
    /// ticket dropped back to High by "any update?", a custom status reset to Open. It would also
    /// re-arm the recovery sweep on every message, so a ticket that can no longer be written to
    /// would be retried every two minutes for a week.
    /// </summary>
    private async Task<ChatResponse> ForwardToAgentAsync(ChatRequest request, Guid sessionId, bool userMessageSaved)
    {
        var hebrew = IsHebrewReceipt(request.Text, request.Widget?.Locale);

        if (!userMessageSaved)
        {
            // Nothing reached the ticket, so nobody will answer it — say so rather than go quiet.
            _logger.LogWarning("[HANDOVER] session={SessionId} customer message NOT stored — asked them to resend",
                sessionId);
            return new ChatResponse
            {
                Text = HandoverNotDelivered(hebrew),
                SessionId = sessionId.ToString(),
            };
        }

        await TouchSessionAsync(sessionId);

        // AgentAnswersHere already required IsRelayConfigured, which requires IsConfigured, so the
        // worker is running and this is the same hand-off every answered turn makes.
        _zohoQueue.Enqueue(new ZohoSyncRequest(
            sessionId,
            request.Widget?.CustomerName,
            request.Widget?.CompanyName,
            request.Widget?.Email));

        _logger.LogInformation(
            "[HANDOVER] session={SessionId} source={Source} forwarded to the agent, Milo silent\n  Q: {Message}",
            sessionId, request.Source ?? "-", request.Text);

        return new ChatResponse
        {
            Text = HandoverReceipt(hebrew),
            SessionId = sessionId.ToString(),
            HandedOver = true,
            // Kept on the response so the header badge stays right for a widget that was reloaded
            // after the ticket number arrived.
            TicketNumber = await LookupTicketNumberAsync(sessionId),
        };
    }

    /// <summary>
    /// Which language the one-line handover receipt goes out in.
    ///
    /// The message decides when it clearly can. A short Latin fragment inside a Hebrew
    /// conversation — "ok", "PDF?", "TID 55123" — says nothing about the customer's language, so
    /// under a handful of Latin letters a Hebrew widget locale wins instead of flipping the receipt
    /// to English mid-conversation.
    /// </summary>
    public static bool IsHebrewReceipt(string text, string? locale)
    {
        if (IsHebrewDominant(text)) return true;

        var latin = text.Count(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
        return latin < 4 && locale?.StartsWith("he", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Moves chat_sessions.updated_at for a message that bypassed the normal turn. The recovery
    /// sweep finds handed-over messages that never reached the ticket by looking at recently active
    /// conversations, and a forwarded message writes nothing else to the session row.
    /// </summary>
    private async Task TouchSessionAsync(Guid sessionId)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _db.ChatSessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[HANDOVER] session={SessionId} activity time not updated: {Message}",
                sessionId, ex.Message);
        }
    }

    /// <summary>
    /// The session a caller's own token points at, or Guid.Empty if it points at nothing it may
    /// read. Exists so a read-only caller — the widget asking whether a human has answered yet —
    /// can be held to exactly the same ownership rule as a caller sending a message, without
    /// duplicating either the token resolution or the check.
    ///
    /// Empty is returned for "no token", "unknown token" and "someone else's conversation"
    /// alike. Telling those apart would answer the question an attacker is asking.
    ///
    /// Note what this deliberately does NOT do: it never mints a session row. A caller polling
    /// for updates on a conversation that does not exist should learn nothing and create nothing.
    /// </summary>
    public async Task<Guid> ResolveOwnedSessionAsync(string? sessionToken, Guid userId)
    {
        var sessionId = ResolveSessionToken(sessionToken, _sessionTokenSalt);
        if (sessionId == Guid.Empty) return Guid.Empty;

        var resume = await CanResumeSessionAsync(sessionId, userId);

        // NeedsRow means no row exists — there is no conversation here to read, whoever asked.
        return resume is { CanResume: true, NeedsRow: false } ? sessionId : Guid.Empty;
    }

    private async Task<(bool CanResume, bool NeedsRow)> CanResumeSessionAsync(Guid sessionId, Guid userId)
    {
        try
        {
            var owner = await _db.ChatSessions
                .Where(s => s.Id == sessionId)
                .Select(s => (Guid?)s.UserId)
                .FirstOrDefaultAsync();

            // No row: a token for a session that was never persisted (the pre-2026-09-08
            // behaviour, and still what happens whenever the DB was down when it was minted).
            // Treat it as this caller's own empty conversation rather than throwing history away.
            if (owner == null) return (true, NeedsRow: true);

            if (owner == userId) return (true, NeedsRow: false);

            _logger.LogWarning("[CHAT] Session {SessionId} belongs to another user — starting a fresh conversation instead", sessionId);
            return (false, NeedsRow: false);
        }
        catch (Exception ex)
        {
            // DB unavailable: honour the token. Dropping a user's history because we could not
            // verify ownership would be a worse failure than the one this guards against.
            // NeedsRow stays false — we already know a write would fail, so there is no point
            // making the caller attempt one just to catch the same exception again.
            Console.WriteLine($"⚠️ [CHAT] Session ownership not verified (DB unavailable): {ex.Message}");
            return (true, NeedsRow: false);
        }
    }

    public async Task<ChatResponse> ProcessAsync(ChatRequest request, Guid userId, string? ipAddress, string userRole = "user")
    {
        // Fold the TAS widget's flat request shape (sessionId/conversationId, and the identity
        // fields it sends at the top level rather than nested) into the canonical properties
        // before anything reads them. See ChatRequest.NormalizeWidgetShape — until this call
        // existed the widget's conversation id was discarded on arrival, which is what left Milo
        // with no memory between messages.
        request.NormalizeWidgetShape();

        // ── Session handling ──
        // DB writes are best-effort: if the database is unavailable we still answer
        // (via OCI) instead of failing the whole request — persistence is just skipped.
        Guid sessionId = Guid.NewGuid();
        bool continuedSession = false;
        var resumeId = ResolveSessionToken(request.SessionToken, _sessionTokenSalt);
        var resume = resumeId != Guid.Empty
            ? await CanResumeSessionAsync(resumeId, userId)
            : (CanResume: false, NeedsRow: false);

        if (resume.CanResume)
        {
            sessionId = resumeId;
            continuedSession = true;

            if (resume.NeedsRow)
            {
                // Resuming an id that has no row of its own. Nothing here used to mint one, and
                // the conversation then stayed rowless for its whole life — every turn resumed
                // the same id, found no row, and moved on. That is silent and it defeats
                // escalation end to end: the "escalated" block below does
                // FirstOrDefaultAsync(...) and simply skips the assignment when the row is
                // missing, while still logging [TICKET-ESCALATED], so the log says a human was
                // asked for and nothing recorded it. ZohoTicketSync then reads Escalated=false
                // and leaves the ticket Closed at Low priority — the exact opposite of the
                // priority routing this is supposed to drive.
                //
                // It was unreachable while a session id had to be a GUID we minted. It stops
                // being unreachable the moment a caller supplies its own conversation id, which
                // is precisely what ResolveSessionToken now allows: for a relayed conversation
                // the id is Zoho's, never ours, so EVERY relayed conversation would be rowless.
                // Minting the row here is what makes resuming a foreign id a real session.
                var row = new ChatSession
                {
                    Id = sessionId,               // the caller's id, not a fresh one — that is the point
                    UserId = userId,
                    Source = request.Source
                };
                try
                {
                    _db.ChatSessions.Add(row);
                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    // Unlike every other write on this path, this one can fail while the database
                    // is perfectly healthy: two turns of the same conversation arriving together
                    // both read no row and both insert the SAME primary key, so the loser gets a
                    // duplicate-key error (2627 — not in EnableRetryOnFailure's transient list).
                    //
                    // Detaching is not tidiness, it is the whole fix. EF only accepts changes
                    // after a SUCCESSFUL save, so a swallowed failure leaves this entity sitting
                    // in the tracker as Added — and _db is scoped to the request and shared by
                    // every later write in this turn. Each of those calls SaveChangesAsync, which
                    // re-sends this insert, hits 2627 again and takes the real write down with
                    // it: the user's message, the assistant's reply, the audit log and the
                    // escalation flag this block exists to make possible. It would also hand the
                    // escalation query below a phantom — FirstOrDefaultAsync resolves against the
                    // tracker first and would return this Added instance instead of the row the
                    // winning request committed, turning the UPDATE into another failed INSERT.
                    //
                    // Detached, the loser simply proceeds on the winner's row, which is exactly
                    // the outcome it wanted. A genuine outage still costs only the escalation
                    // flag, as before.
                    _db.Entry(row).State = EntityState.Detached;
                    Console.WriteLine($"⚠️ [CHAT] Resumed session {sessionId} not persisted: {ex.Message}");
                }
            }
        }
        else
        {
            var session = new ChatSession { UserId = userId, Source = request.Source };
            try
            {
                _db.ChatSessions.Add(session);
                await _db.SaveChangesAsync();
                sessionId = session.Id;
            }
            catch (Exception ex)
            {
                // Same reason as above. This branch mints its own Guid so it cannot lose a race,
                // but a failure here used to leave the entity Added with sessionId still pointing
                // at a DIFFERENT Guid — so if a later save in the turn succeeded it wrote a
                // session row that none of this turn's messages belong to.
                _db.Entry(session).State = EntityState.Detached;
                Console.WriteLine($"⚠️ [CHAT] Session not persisted (DB unavailable): {ex.Message}");
            }
        }
        // Single grep-able line to watch for the caller-side session-continuity fix landing —
        // continued=False on every request for a given source means that caller is still not
        // sending back SessionToken at all (today: true for every non-internal source). No
        // other change in this file can make continued=True for a caller that doesn't send it.
        _logger.LogInformation("[CHAT-CONTINUITY] source={Source} session={SessionId} continued={Continued}",
            request.Source, sessionId, continuedSession);

        // ── Image flow ──
        if (request.Type == "image")
        {
            return await HandleImageAsync(request, sessionId, userId);
        }

        // ── Empty text ──
        // This is the path a widget hits when it opens a chat or presses "New chat" without a
        // real question, so it IS the welcome message in practice — it used to answer with a
        // hardcoded English line, ignoring both the welcome message configured in
        // ChatbotConfig and the customer's own locale, which is why a Hebrew user opening the
        // chat got greeted in English.
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            string? configuredWelcome = null;
            try
            {
                configuredWelcome = (await _db.ChatbotConfigs
                    .Where(c => c.IsActive)
                    .OrderByDescending(c => c.UpdatedAt)
                    .Select(c => c.WelcomeMessage)
                    .FirstOrDefaultAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [CHAT] Welcome message not loaded (DB unavailable): {ex.Message}");
            }

            var wantsHebrew = request.Widget?.Locale?.StartsWith("he", StringComparison.OrdinalIgnoreCase) == true;
            var fallbackWelcome = wantsHebrew
                ? "היי 👋 אני מילו, העוזר של TripEX. איך אפשר לעזור?"
                : "Hello 👋 I'm Milo, your TripEX assistant. How can I help?";

            _logger.LogInformation("[CHAT] welcome message returned (empty text) session={SessionId} source={Source} configured={HasConfigured}",
                sessionId, request.Source ?? "-", !string.IsNullOrWhiteSpace(configuredWelcome));

            return new ChatResponse
            {
                Text = !string.IsNullOrWhiteSpace(configuredWelcome) ? configuredWelcome! : fallbackWelcome,
                SessionId = sessionId.ToString()
            };
        }

        // ── Handed over to a person? ──
        // Decided BEFORE the message is stored, so it can be stored with the HandoverIntent that
        // tells the Zoho worker a person needs to see it (see HandoverIntent). Acted on after the
        // save, below. See IsHandedOver for why this lasts until the customer starts a new chat
        // rather than until the ticket closes.
        var agentAnswersHere = AgentAnswersHere(_zoho.Options.IsRelayConfigured, request.Source, request.IsTasWidgetClient);
        var handedOver = IsHandedOver(
            continuedSession,
            await IsHumanInvolvedAsync(sessionId, continuedSession && agentAnswersHere),
            agentAnswersHere);

        // ── Save user message (best-effort) ──
        var userMessage = new ChatMessage
        {
            SessionId = sessionId,
            Role = "user",
            Content = request.Text,
            Intent = handedOver ? HandoverIntent : null
        };
        var userMessageSaved = false;
        try
        {
            _db.ChatMessages.Add(userMessage);
            await _db.SaveChangesAsync();
            userMessageSaved = true;
        }
        catch (Exception ex)
        {
            // NOT detached, unlike the session row above: that one fails on a duplicate key, which
            // is permanent, while this row has a fresh Guid and fails only when the database does.
            // Left pending, it rides along with the assistant-message save at the end of the turn
            // and still lands if the outage was brief.
            Console.WriteLine($"⚠️ [CHAT] User message not persisted (DB unavailable): {ex.Message}");
        }

        // ── Handed over to a person: Milo stays out of it ──
        // Before any of the expensive work below — no history, no knowledge search, no model call.
        // The customer is talking to a human now; the only job left is to get their words onto the
        // ticket that human is reading.
        if (handedOver)
        {
            return await ForwardToAgentAsync(request, sessionId, userMessageSaved);
        }

        // ── Load history (best-effort; empty when DB is unavailable) ──
        // Intent is carried alongside Role/Content (not just for the AI prompt, which only
        // needs Role/Content) so the consecutive-"clarify" cap below can inspect what each
        // past assistant turn actually was without a second DB round trip.
        var historyRows = new List<(string Role, string Content, string? Intent)>();
        try
        {
            // Newest 50, then flipped back to chronological order. NOT OrderBy+Take: that
            // pins the window to the OLDEST 50 messages of the session, so once a session
            // passes 50 messages the model keeps re-reading the opening turns and never
            // sees the recent ones it actually has to answer in context of.
            var recent = await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(50)
                .Select(m => new { m.Role, m.Content, m.Intent })
                .ToListAsync();
            recent.Reverse();
            historyRows = recent.Select(m => (m.Role, m.Content, m.Intent)).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] History not loaded (DB unavailable): {ex.Message}");
        }
        var history = historyRows.Select(h => (h.Role, h.Content)).ToList();

        // ── Load config (best-effort; defaults when DB is unavailable) ──
        ChatbotConfig? config = null;
        try
        {
            config = await _db.ChatbotConfigs
                .Where(c => c.IsActive)
                .OrderByDescending(c => c.UpdatedAt)   // deterministic: newest active config wins
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Config not loaded (DB unavailable), using defaults: {ex.Message}");
        }

        var temperature = (double)(config?.Temperature ?? 0.3m);
        // Floor, not just a fallback: a "thinking" model (e.g. gemini-2.5-pro) spends part of
        // this budget on invisible reasoning tokens before writing anything visible, so a
        // config row saved back when the default model was a non-thinking one (MaxTokens
        // default is 1024 — see ChatbotConfig) starves the actual answer, cutting it off
        // mid-sentence (seen in prod 2026-09-07: latency=8-10s, "[OCI-PARSE] Truncated JSON").
        // Never LOWERS an intentionally-higher configured value, only raises a too-low one.
        var maxTokens = Math.Max(config?.MaxTokens ?? 2048, 4096);

        // ── Geolocation ──
        var geo = await _geoService.GetLocationAsync(ipAddress);

        // ── RAG: Search knowledge base ──
        var knowledgeContext = await SearchKnowledgeBase(request.Text);

        // ── Widget context (e.g. the "Sports Support" embed's postMessage payload) ──
        // TAS is already the trusted, authenticated caller for this whole request (the same
        // static server-to-server key that authenticates everything else on this path) — these
        // fields are just more request data from that same already-authenticated caller, exactly
        // like Source/Scope/Trid above. No extra verification layer on top of that.
        if (request.Widget != null)
        {
            // Every field logged except the token itself (only whether one was present) —
            // a session/identity token doesn't belong in a plaintext log file. This one line
            // is meant to be the single place to confirm, from a real test message, that
            // everything the host page sent actually made it all the way to this backend.
            _logger.LogInformation(
                "[WIDGET-CONTEXT] hasToken={HasToken} customerId={CustomerId} customerName={CustomerName} company={CompanyName} role={Role} pageContext={PageContext} locale={Locale}",
                !string.IsNullOrEmpty(request.Widget.Token), request.Widget.CustomerId, request.Widget.CustomerName,
                request.Widget.CompanyName, request.Widget.Role, request.Widget.PageContext, request.Widget.Locale);
        }
        var effectiveRole = !string.IsNullOrWhiteSpace(request.Widget?.Role) ? request.Widget!.Role! : userRole;

        // ── Build system prompt ──
        // Every known page goes in — the AI's own semantic matching handles Hebrew
        // morphology/synonyms far better than a keyword-overlap filter would (tried and
        // dropped; see git history). Descriptions are kept short (tag-phrases, not full
        // sentences) specifically to keep this affordable at ~366 entries.
        var allPages = _pageLinks.Values.ToList();
        var systemPrompt = BuildSystemPrompt(
            request, geo, knowledgeContext, effectiveRole, allPages);

        // ── Build messages ──
        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };
        // The role is MAPPED rather than passed through, and it has to be. chat_messages now
        // carries a third role — "agent", a reply typed by a human in Zoho Desk — while the chat
        // completions API accepts only system/user/assistant. Sending "agent" straight through
        // would be rejected, and the row stays in history forever, so Milo would stop answering
        // that conversation permanently from the moment a person helped in it. The one thing this
        // feature exists to make possible would be the thing that breaks it.
        //
        // "assistant" is also the honest mapping and not merely the safe one: from the
        // conversation's point of view the agent's reply IS a previous answer, and Milo has to see
        // it — otherwise its next turn cheerfully contradicts the person who just sorted it out.
        // Anything that is not the user is something said back TO the user.
        messages.AddRange(history.Select(h => new OracleMessage
        {
            Role = ToModelRole(h.Role),
            Content = h.Content,
        }));

        // The current user message is normally already the last entry in `history`
        // (saved to the DB above, then reloaded). But DB access is best-effort — if
        // persistence or the reload failed, `history` can be empty, and the model
        // would receive ONLY the system prompt with no actual question at all,
        // silently degrading every reply to a generic greeting no matter what the
        // user asked. Guarantee the current message is present regardless of DB state.
        var lastIsCurrentUserMessage = messages.Count > 0
            && messages[^1].Role == "user"
            && messages[^1].Content is string lastContent
            && lastContent == request.Text;
        if (!lastIsCurrentUserMessage)
            messages.Add(new OracleMessage { Role = "user", Content = request.Text });

        // ── Call Oracle AI ──
        // allowCustomModel: true opts the Milo conversational path into the fine-tuned
        // custom model IF Oracle:UseCustomModel is also enabled in config (see
        // OracleAiService.ResolveChatTarget). OCR/invoice-scan call sites never pass
        // this, so they always stay on the vision-capable default model regardless.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var isInternalAudience = string.Equals(request.Source, "internal", StringComparison.OrdinalIgnoreCase);

        // The chat is being relayed by Zoho SalesIQ rather than rendered by the TAS widget. Three
        // things in this file are written for a client we control and are wrong through a relay:
        // the helpdesk ticket (SalesIQ raises its own from the chat, so ours would be a duplicate),
        // the HTML anchor appended to the reply (SalesIQ shows plain text, so the tag would be
        // read out as characters), and the button-label budget (SalesIQ silently truncates a
        // label past 20 characters — and the truncated text is what comes back as the user's next
        // message). Nothing else changes: Source has never gated anything but "internal", so every
        // other path behaves exactly as it does for the widget.
        var isSalesIqRelay = IsRelaySource(request.Source);

        string intent, responseText, page;
        List<string> modelOptions;

        // ── The one turn whose answer is already known ──
        // The user has just picked "travel & expense operations" from the fixed orientation
        // question, so the reply is the fixed status list either way and the ONLY undecided
        // thing is which of the two lists applies. Asking the full prompt — 60,000 characters
        // of page catalog, the whole navigation rulebook, the glossary — to settle a binary
        // choice is what made this the slowest turn in production (39.3 s, 3,561 thinking
        // tokens) for output the clarify block below discards anyway. Ask the small question
        // instead. Gated on the same audience check as the clarify flow itself: internal staff
        // never see this flow, so the shortcut must never fire for them.
        var isStatusListTurn = !isInternalAudience && IsStatusListTurn(historyRows, request.Text);

        // Null unless the small question below decided this really is a status-list turn.
        string? fastIntent = null;
        if (_statusListShortcut && isStatusListTurn)
            fastIntent = await ResolveStatusListIntentAsync(
                FindQuestionBeforeOrientation(historyRows), CancellationToken.None);
        else if (isStatusListTurn)
            // Logged only on the exact turn the shortcut would have taken, so switching it off
            // in config produces visible proof that it is off — rather than the absence of a
            // line, which is also what a broken setting name looks like.
            _logger.LogInformation(
                "[CLARIFY-FAST] session={SessionId} shortcut disabled by Milo:StatusListShortcut — using the full prompt",
                sessionId);

        if (fastIntent != null)
        {
            intent = fastIntent;
            // Everything else on this path is fixed. "text" is replaced by the status list, the
            // page is cleared deliberately, and there are no model-authored options — so the
            // values here only have to be the harmless ones the clarify block expects.
            responseText = "";
            page = "";
            modelOptions = new List<string>();

            _logger.LogInformation(
                "[CLARIFY-FAST] session={SessionId} answered the orientation question without the full prompt → {Intent}",
                sessionId, intent);
        }
        else
        {
            // ── Call Oracle AI ──
            // forceJsonOutput sets response_format={"type":"json_object"} on the request, so the
            // JSON contract this whole path depends on is enforced by the API and not only asked
            // for in the prompt's CRITICAL OUTPUT RULE. It was never passed here — only the two
            // OCR call sites used it — which is why ParseAiResponse carries a whole repair layer
            // and why prod logged "[OCI-PARSE] Truncated JSON" on 2026-09-07. The prompt already
            // says "Respond with ONLY a JSON object", which is the wording these modes require.
            // ParseAiResponse is unchanged and still handles a fenced or broken reply, so this
            // only removes failures; it cannot introduce one.
            var rawContent = await _oracle.ChatAsync(
                messages, maxTokens, temperature, forceJsonOutput: true, allowCustomModel: true);
            (intent, responseText, page, modelOptions) = ParseAiResponse(rawContent);
        }

        sw.Stop();

        // ── Shape "clarify"/"clarify_status_*" responses: fixed questions first, cap at the limit ──
        // quickReplies carries the round's choices as plain option strings (no numbering), for a
        // frontend that renders them as clickable buttons; clicking one just re-sends its exact
        // text as the next message, like typing it. It is populated only when the choices are
        // fit to be buttons — see the composition step below and OptionsCanBeButtons.
        // The whole clarify-flow is customer-support-ticket-reduction UX (see BuildSystemPrompt)
        // — irrelevant for TripEx's own internal staff (source:"internal"). The prompt already
        // never offers these intents to the model for that audience; this is the same "don't
        // rely on the prompt alone" defense-in-depth this file uses everywhere else, in case
        // the model emits one of them anyway.
        // (isInternalAudience itself is computed further up, where the status-list shortcut
        // needs it — the reasoning above is why it exists at all, so it stays here.)
        var quickReplies = new List<string>();
        // The model's own "text" on a clarify turn is about to be overwritten by a fixed
        // question, which used to make a wrongly-chosen "clarify" completely undiagnosable:
        // nothing recorded WHY it asked instead of answering. The prompt now requires that text
        // to name the entries it was torn between, so keep it for the [CHAT] log line below.
        string? clarifyRationale = null;
        // The clarifying question on its own, WITHOUT its option list — the branches below set
        // this and `clarifyOptions`, and the single composition step at the end of the block
        // decides whether the options also belong in the text. Stays null on the branches with
        // nothing to choose between (the escalate cap, and a round 2 that offered no options).
        string? clarifyQuestion = null;
        // Every choice to put in front of the user. Distinct from `quickReplies`, which is the
        // structured set of choices fit to be BUTTONS: an over-long or comma-bearing label is
        // still a choice the user must be able to pick, it just has to arrive as text.
        var clarifyOptions = new List<string>();
        // Whether the options will reach the user as real buttons. Read again further down, by
        // the "you can also just type the option" escape hatch, which only makes sense when they
        // won't. See OptionsRenderAsButtons.
        var optionsRenderAsButtons = false;
        // Set when the block below clears `page` on purpose, so ResolvePageOverride can be
        // skipped further down. A flag rather than re-testing the intent afterwards, so it stays
        // honest even if a branch REASSIGNS `intent` after clearing the page — the 2-in-a-row cap
        // did exactly that (to "escalate") until the support offer replaced it, and an intent
        // test after the fact would no longer have been able to tell.
        var pageDeliberatelyCleared = false;

        // Which clarifying question this turn is, counting the one about to be asked: 1 on the
        // first, 2 on the next, and so on. Stays 0 on every turn that isn't a clarify. Read far
        // below, by the periodic support offer — which is why it lives out here rather than
        // inside the block, where `consecutiveClarifications` itself is scoped.
        var clarifyRoundNumber = 0;
        if (!isInternalAudience && ClarifyTypeIntents.Contains(intent))
        {
            clarifyRationale = responseText;
            var consecutiveClarifications = CountTrailingConsecutiveClarifications(historyRows);
            clarifyRoundNumber = consecutiveClarifications + 1;
            page = null; // none of these ever link to a page — there's nothing to link to yet
            pageDeliberatelyCleared = true;

            if (intent == "clarify_status_trip" || intent == "clarify_status_expense")
            {
                // Fixed, deterministic status list — shown whenever the user picked "travel &
                // expense operations" in the first round, instead of whatever the model would
                // have phrased/listed on its own (the model only decides WHICH list applies —
                // trip-related vs. a standalone expense report — never what either one says).
                var statusOptions = intent == "clarify_status_trip"
                    ? TripStatusOptionsForTrip
                    : TripStatusOptionsForExpenseOnly;
                clarifyOptions = statusOptions.ToList();
                clarifyQuestion = IsHebrewDominant(request.Text)
                    ? "באיזה סטטוס נמצאת הנסיעה או דוח ההוצאות?"
                    : "What status is the trip or expense report currently in?";
            }
            else if (consecutiveClarifications == 0)
            {
                // ── The FIRST clarifying question ──
                // It used to be the fixed three-way orientation question, always, on the
                // reasoning that a deterministic opener beats whatever the model improvises.
                // What that traded away was visible in production on 2026-09-15: asked "how do
                // I export a flight's expense report", the model had ALREADY worked out the two
                // candidates and wrote them down — "[CLARIFY-WHY] the expense report for a
                // single specific trip ... or an analytical report showing expenses from
                // multiple trips" — and that sentence went to the log while the user was shown
                // "operations / reports & data analysis / settings & management" instead. The
                // user then answered the generic question honestly and the conversation went
                // somewhere neither of the two real candidates lived.
                //
                // So: when the model can name the specific alternatives, ASK THOSE. The fixed
                // three-way question stays as the fallback for a question so vague that not even
                // the model can name two candidates — which is the case it was written for.
                //
                // Scrubbed and validated exactly like the second round below (same helpers, same
                // 2-4 / length / comma rules), so an unusable set falls back rather than
                // rendering mangled buttons.
                var isHebrew = IsHebrewDominant(request.Text);

                var ownOptions = _modelAuthoredFirstClarify
                    ? CleanModelOptions(modelOptions.Select(o => ScrubRawPageKeys(o, IsHebrewDominant(responseText))))
                    : new List<string>();

                if (ownOptions.Count >= 2 && !string.IsNullOrWhiteSpace(responseText))
                {
                    clarifyOptions = ownOptions;
                    clarifyQuestion = responseText;
                    _logger.LogInformation(
                        "[CLARIFY-SPECIFIC] session={SessionId} asked its own question instead of the orientation one: {Options}",
                        sessionId, string.Join(" | ", ownOptions));
                }
                else
                {
                    clarifyOptions = (isHebrew ? OrientationOptionsHe : OrientationOptionsEn).ToList();
                    clarifyQuestion = isHebrew
                        ? "כדי שאוכל לכוון אותך לתשובה המדויקת ביותר — במה מדובר?"
                        : "To point you to the most accurate answer — which of these is it about?";
                }
            }
            else
            {
                // Plain "clarify" with consecutiveClarifications == 1: the second,
                // model-authored clarifying question for the reports/settings paths. Its wording
                // has to be the model's — only it knows which two entries it is torn between —
                // but the ANSWERING should work exactly like the two fixed questions above, so
                // the prompt asks it for the choices in a separate "options" array and they
                // become the same clickable buttons. Before this, this one round was the odd one
                // out: its choices were prose inside the question, so the user had to read them
                // and type one back while every other guiding question offered buttons.
                //
                // Whatever the model wrote stays the question; the choices are added to it below.
                // Scrubbed BEFORE anything measures them, because a swapped-in page name is what
                // the user actually sees — so the length and comma rules have to apply to that,
                // not to the raw key it replaced. Real labels in page-links.json do contain
                // commas and some run long, and either one correctly falls back to the numbered
                // list instead of producing mangled buttons.
                var hebrewQuestion = IsHebrewDominant(responseText);
                clarifyOptions = CleanModelOptions(
                    modelOptions.Select(o => ScrubRawPageKeys(o, hebrewQuestion)));

                // Fewer than two is not a choice, so there is nothing to render and the model's
                // own prose stands, as it did before this existed. Logged because the prompt
                // told it to keep the choices out of "text": if this fires often, the reply the
                // user saw may have been a question with no visible answers.
                if (clarifyOptions.Count >= 2)
                    clarifyQuestion = responseText;
                else if (modelOptions.Count > 0)
                    _logger.LogWarning(
                        "[CLARIFY-OPTIONS] session={SessionId} discarded {Raw} unusable option(s): {Options}",
                        sessionId, modelOptions.Count, string.Join(" | ", modelOptions));
            }

            // The branches above deliberately set only the question and the options, never the
            // final text: whether those options belong IN the text is one decision, made once,
            // here — never in each branch, where the two could drift apart.
            if (clarifyQuestion != null)
            {
                // QuickReplies is the structured button set, so it carries the options only when
                // they are fit to BE buttons — and Paramerter is derived from it. An unfit set
                // (an over-long label, a comma) is shown as the numbered list and nothing else,
                // which is what stops a client from rendering it both ways.
                // A SalesIQ relay draws the choices itself, from ChatResponse.QuickReplies, so it
                // counts as a client that renders buttons — with its own tighter label budget.
                var labelBudget = isSalesIqRelay ? SalesIqOptionLabelLength : MaxOptionLabelLength;
                var clientDrawsButtons = request.IsTasWidgetClient || isSalesIqRelay;

                quickReplies = OptionsCanBeButtons(clarifyOptions, labelBudget) ? clarifyOptions : new List<string>();
                optionsRenderAsButtons = OptionsRenderAsButtons(quickReplies, clientDrawsButtons, labelBudget);
                responseText = ComposeClarifyText(clarifyQuestion, clarifyOptions, optionsRenderAsButtons);
            }
        }

        // ── Map intent to actions ──
        var mapping = ActionMapping.GetValueOrDefault(intent, ActionMapping["general"]);

        // A turn that cleared its page above ("there's nothing to link to yet") has to KEEP it
        // cleared. ResolvePageOverride derives a link from any page name it finds in the reply
        // text, which is right for an answer and wrong for a question: the second, self-worded
        // clarifying round names the very entries it is asking the user to choose between, so
        // letting it run attaches a link to whichever is mentioned first — the bot asks "did you
        // mean Budget by Division Report or Budget by Company Report?" and then links Budget by
        // Division, answering its own question with a coin flip. Verified against the real
        // 367-entry catalog: that exact sentence resolves to TASR_08002_BudgetByDivision.
        // (The fixed questions happen to resolve to nothing, but that is luck, not design.)
        page = pageDeliberatelyCleared ? null : ResolvePageOverride(page, responseText);

        // ── Map page → a real TAS URL + button label (Data/page-links.json) ──
        // The AI only ever sees the page KEY (and its Description); the actual URL
        // lives in the data file so pages can be added/changed without a code deploy.
        var pageLink = !string.IsNullOrEmpty(page) && _pageLinks.TryGetValue(page, out var pl) ? pl : null;

        // pageLink.Url is only the relative path (e.g. "/Master_Pages/x.aspx") — prepend the
        // per-environment host once here so every consumer below gets the full, real URL.
        var pageUrl = pageLink != null ? _pageLinksBaseUrl + pageLink.Url : "";

        // The widget renders "text" as raw HTML (innerHTML), so a plain <a> tag with
        // target="_top" becomes a real clickable link that breaks out of the chat
        // iframe on click — no frontend change needed to support this.
        var isHebrewReply = IsHebrewDominant(responseText);

        // Defense-in-depth for the "never show the raw key" rule: the prompt instructs
        // the AI to always name a report/page by its human-readable Label/LabelEn (never
        // the raw key), but live testing showed it still leaks the raw key sometimes on
        // questions that push it to be maximally precise (e.g. disambiguating near-duplicate
        // report names). Rather than rely on the model to comply every time, scrub any
        // literal page key that slipped into the visible text and swap in its name — this
        // guarantees the user never sees an internal identifier regardless of what the
        // model wrote. Option labels get the same treatment where they are built (they are
        // model text too, and a button is just as visible as the reply body).
        responseText = ScrubRawPageKeys(responseText, isHebrewReply);

        if (pageLink != null)
        {
            // Match the link label to whatever language the reply actually came out in —
            // detected from the reply text itself (Hebrew Unicode block present or not)
            // rather than asking the AI for a separate field, so it can't get out of sync
            // with what the user actually sees.
            var label = isHebrewReply ? pageLink.Label : pageLink.LabelEn;
            var safeUrl = System.Net.WebUtility.HtmlEncode(pageUrl);
            var safeLabel = System.Net.WebUtility.HtmlEncode(label);

            // Show the report/page selector in the language of the reply, but always use
            // English report name + numeric code (how TAS displays it) so the user knows
            // exactly what to search for in the system.
            var reportCode = System.Text.RegularExpressions.Regex.Match(page, @"TASR_(\d+)_").Groups[1].Value;
            if (!string.IsNullOrEmpty(reportCode))
            {
                var reportSelectText = isHebrewReply
                    ? $"בחר {pageLink.LabelEn} - {reportCode}"
                    : $"Select {pageLink.LabelEn} - {reportCode}";
                responseText += $"\n\n{reportSelectText}";
            }

            // A relay that renders plain text would read the tag out as characters, so it gets the
            // label and the bare URL instead — which every chat client linkifies on its own.
            //
            // Deliberately NOT "suppress the anchor and let the client use RedirectPage /
            // RedirectLabel": those two fields are on the response, but nothing has ever read
            // them — the live TAS widget contains no occurrence of either, and neither does this
            // repo outside the line that writes them. Dropping the anchor in favour of them would
            // leave the user with a reply that names a page and offers no way to open it. The
            // caption also has to come from `label` here rather than RedirectLabel, because that
            // field is always the Hebrew Label with no language branch at all.
            responseText += isSalesIqRelay
                ? $"\n\n{label}\n{pageUrl}"
                : $"\n\n<a href=\"{safeUrl}\" target=\"_top\" rel=\"noopener\">{safeLabel}</a>";
        }

        var escalated = intent == "escalate";
        // Filled only on an escalation, and carried onto the response as well as into the text —
        // the text is what the customer reads now, the field is what lets a client keep the
        // reference on screen instead of scrolling back for it.
        string? ticketNumber = null;
        // The support contact line is added here (deterministically, in code) rather than left to
        // the AI's own wording — guarantees it always names the real address. It now appears ONLY
        // on a true escalation, where routing the user to a human IS the point of the message.
        //
        // It used to be appended after every page link too ("if this isn't exactly the page you
        // were looking for, contact your System Admin or support…"). Removed 2026-09-09 at Roi's
        // request: a page link is attached to most answers, so that caveat was on nearly every
        // reply — it reads as the bot hedging on an answer that is in fact correct, and works
        // against the "answer confidently, be concise" direction the rest of the prompt gives.
        //
        // Which of the two things it says depends on whether a human can actually reach the
        // customer here. Naming an email address is the right answer only while this window is a
        // dead end: it asks them to start again somewhere else, and that is worth saying when the
        // alternative is nothing. Once the agent-reply relay is live a person answers in this very
        // thread, and sending someone to their inbox at that moment is the worst possible advice —
        // they leave, and the reply they were waiting for arrives in a window they closed.
        if (escalated)
        {
            // The same answer that decides whether Milo goes quiet on the next turn — see
            // AgentAnswersHere. "Stay here" is a promise, and only this predicate can keep it.
            var handingOverInThisWindow = agentAnswersHere;

            responseText += (isHebrewReply, handingOverInThisWindow) switch
            {
                (true, true)   => "\n\nמעביר אותך לנציג — אפשר להישאר כאן, התשובה תגיע בצ'אט הזה",
                (true, false)  => $"\n\nניתן לפנות לתמיכה במייל {_supportContact}",
                (false, true)  => "\n\nConnecting you to an agent — stay here, their reply will arrive in this chat",
                (false, false) => $"\n\nYou can reach support by email at {_supportContact}",
            };

            // The reference number, when there is one to give. Looked up rather than assumed,
            // because the ticket is opened by a background worker: on the very first turn of a
            // conversation it may not exist yet, and an escalation on that turn therefore has no
            // number to quote. Silence is the right answer then — a made-up or "pending"
            // reference is worse than none, and the widget gets the number on the response as
            // soon as it appears (see TicketNumber below), so nothing is lost by waiting.
            ticketNumber = await LookupTicketNumberAsync(sessionId);
            if (!string.IsNullOrWhiteSpace(ticketNumber))
            {
                responseText += isHebrewReply
                    ? $"\n\nמספר הקריאה שלך: {ticketNumber}"
                    : $"\n\nYour ticket number: {ticketNumber}";
            }
        }
        else if (ClarifyTypeIntents.Contains(intent) && !optionsRenderAsButtons)
        {
            // Escape hatch, for the clients that get the options as a numbered plain-text list
            // because they render no buttons (see OptionsRenderAsButtons): it tells the user the
            // list is answerable by typing, so the turn is survivable no matter how the reply is
            // rendered. Suppressed when the buttons DO render — there is no list on screen to
            // "type the text of" then, just the buttons themselves, so the line would only
            // describe something the user cannot see.
            //
            // The support address is deliberately NOT named here (it was, until 2026-09-09): a
            // clarify turn is us asking the user a question, not us running out of answers, so
            // offering support in the same breath invites them to leave mid-flow. Only the
            // "you can type it instead" half is load-bearing.
            responseText += isHebrewReply
                ? "\n\nאפשר גם פשוט להקליד את הטקסט של האפשרות המתאימה"
                : "\n\nYou can also simply type the text of the option that fits";
        }

        // Every Nth clarifying question in a row also offers a human, with the real address.
        // This is what replaced the hard 2-question cap (see SupportOfferEveryNClarifications):
        // instead of DECIDING for the user that the conversation has gone on long enough, the
        // third, sixth, ninth question simply says support exists and lets them choose. Phrased
        // as an aside, not a hand-off — the question above it is still the main point of the
        // message, and the options/buttons are still there to answer.
        //
        // Deliberately outside the if/else above: that pair is about how the OPTIONS are
        // rendered, which has nothing to do with how long the conversation has run. Both
        // branches, and the branch-free case (a clarify with no options at all), get the offer.
        if (clarifyRoundNumber > 0
            && clarifyRoundNumber % SupportOfferEveryNClarifications == 0
            && !escalated)
        {
            responseText += isHebrewReply
                ? $"\n\nאם בא לך לדלג על השאלות ולדבר עם בן אדם — התמיכה שלנו במייל {_supportContact}"
                : $"\n\nIf you'd rather skip the questions and talk to a person — our support is at {_supportContact}";
        }

        // ── Save corrections (learning from OCR corrections) ──
        await TrySaveCorrections(intent, sessionId, userId);

        var latencyMs = sw.ElapsedMilliseconds;
        var ragChars = knowledgeContext?.Length ?? 0;

        // Full Q&A to the rolling file log (logs/tripex-*.log) — always, even if DB is down.
        _logger.LogInformation(
            "[CHAT] session={SessionId} user={UserId} source={Source} intent={Intent} rag={RagChars}c latency={LatencyMs}ms\n  Q: {Message}\n  A: {Response}",
            sessionId, userId, request.Source ?? "-", intent, ragChars, latencyMs, request.Text, responseText);

        // Why the model chose to ask instead of answer (its own words, before the fixed question
        // replaced them). Only on clarify turns, so this stays quiet on normal traffic.
        if (!string.IsNullOrWhiteSpace(clarifyRationale))
            _logger.LogInformation("[CLARIFY-WHY] session={SessionId} intent={Intent} model_said={Rationale}",
                sessionId, intent, clarifyRationale);

        // ── Persist assistant message + audit log + escalation (best-effort) ──
        // Skipped silently if the DB is unavailable so the answer still returns.
        try
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId,
                Role = "assistant",
                Content = responseText,
                Intent = intent,
                Metadata = JsonSerializer.Serialize(new { actions = mapping.Actions, page, redirectPage = pageUrl })
            });

            _db.ChatbotLogs.Add(new ChatbotLog
            {
                SessionId = sessionId,
                UserId = userId,
                EventType = "intent_detected",
                Details = JsonSerializer.Serialize(new
                {
                    intent,
                    actions = mapping.Actions,
                    page,
                    redirectPage = pageUrl,
                    source = request.Source,
                    latency_ms = latencyMs,
                    rag_chars = ragChars,
                    // Full message + response so a chat turn can be examined end-to-end.
                    message = request.Text,
                    response = responseText
                })
            });

            // ── Escalation: hand the ticket to a human when Milo can't help ──
            if (escalated)
            {
                var ticket = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (ticket != null)
                {
                    // A SECOND request for a human in a conversation that already escalated. The
                    // escalation push is recorded as done once it lands, so on its own this turn
                    // would change nothing at Zoho — and an agent who closed the ticket after the
                    // first hand-off would never hear about the second. Tagging the customer's line
                    // as one a person must see makes the worker reopen a Closed ticket, status only.
                    // Deliberately NOT a re-run of the escalation push: that also re-applies the
                    // escalation priority, overwriting whatever the agent had set since. (With the
                    // relay on this is mostly unreachable, since Milo stops answering after the first
                    // hand-off; with it off, Milo keeps answering and can escalate again.)
                    if (ticket.Escalated) userMessage.Intent = HandoverIntent;

                    ticket.Escalated = true;
                    ticket.EscalatedAt = DateTime.UtcNow;
                    ticket.Status = "escalated";
                    ticket.EscalationReason = request.Text.Length > 1000 ? request.Text[..1000] : request.Text;
                    ticket.UpdatedAt = DateTime.UtcNow;
                }
                _logger.LogInformation("[TICKET-ESCALATED] session={SessionId} user={UserId} reason={Reason}",
                    sessionId, userId, request.Text);
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Assistant message/log not persisted (DB unavailable): {ex.Message}");
        }

        // ── Mirror the conversation into Zoho Desk (one ticket per conversation) ──
        // A hand-off, not a call: this only drops the session id into an in-memory queue, so it
        // adds no measurable time to a reply that already costs 11-39s, and a Zoho outage cannot
        // reach the customer. The worker reads what to send from chat_messages, so a turn that
        // does not make it (queue full, Zoho down, process restart) is picked up by the next one.
        //
        // Skipped for source:"internal" — that is TripEx's own staff chat, and its conversations
        // are not customer support tickets.
        // Also skipped for a SalesIQ relay: that chat already becomes a Desk ticket on Zoho's own
        // side, so mirroring it here would put every conversation in the helpdesk twice.
        if (_zoho.Options.IsConfigured && !isInternalAudience && !isSalesIqRelay)
        {
            _zohoQueue.Enqueue(new ZohoSyncRequest(
                sessionId,
                request.Widget?.CustomerName,
                request.Widget?.CompanyName,
                request.Widget?.Email));
        }

        return new ChatResponse
        {
            Text = responseText,
            Actions = mapping.Actions,
            QuickReplies = quickReplies,
            Paramerter = BuildWidgetParamerter(quickReplies),
            RedirectPage = pageUrl,
            RedirectLabel = pageLink?.Label,
            SessionId = sessionId.ToString(),
            Escalated = escalated,
            SupportContact = escalated ? _supportContact : null,
            TicketNumber = ticketNumber
        };
    }

    /// <summary>
    /// List tickets (= chat sessions) for review / learning. Admin-only via the controller.
    /// </summary>
    public async Task<List<object>> ListTicketsAsync(bool escalatedOnly, int take)
    {
        var q = _db.ChatSessions.AsQueryable();
        if (escalatedOnly) q = q.Where(s => s.Escalated);

        var tickets = await q
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new
            {
                ticketId = s.Id,
                userId = s.UserId,
                source = s.Source,
                status = s.Status,
                escalated = s.Escalated,
                escalatedAt = s.EscalatedAt,
                escalationReason = s.EscalationReason,
                messageCount = _db.ChatMessages.Count(m => m.SessionId == s.Id),
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt
            })
            .ToListAsync();

        return tickets.Cast<object>().ToList();
    }

    private async Task<ChatResponse> HandleImageAsync(ChatRequest request, Guid sessionId, Guid userId)
    {
        // Log OCR request
        _db.ChatbotLogs.Add(new ChatbotLog
        {
            SessionId = sessionId,
            UserId = userId,
            EventType = "ocr_request",
            Details = JsonSerializer.Serialize(new { source = request.Source })
        });

        // Extract country from request scope or default
        var country = request.Scope; // client can pass country in Scope field
        var result = await _invoiceService.AnalyzeAsync(request.Text, null, country);

        if (!result.Success)
        {
            await _db.SaveChangesAsync();
            return new ChatResponse
            {
                Text = "Failed to scan receipt. Please try again.",
                SessionId = sessionId.ToString()
            };
        }

        // Build summary from AlgoText-compatible fields
        var f = result.Fields;
        var lines = new List<string> { "✅ Invoice scanned successfully! Here are the details:" };
        if (f?.Type != null) lines.Add($"📄 Type: {f.Type.Replace("_", " ")}");
        if (f?.MerchantName != null) lines.Add($"🏪 Merchant: {f.MerchantName}");
        if (f?.MerchantTin != null) lines.Add($"🆔 TIN: {f.MerchantTin}");
        if (f?.MerchantAddress != null) lines.Add($"📍 Address: {f.MerchantAddress}");
        if (f?.MerchantCity != null) lines.Add($"🌆 City: {f.MerchantCity}");
        if (f?.InvoiceNumber != null) lines.Add($"🔢 Invoice #: {f.InvoiceNumber}");
        if (f?.InvoiceDate != null) lines.Add($"📅 Date: {f.InvoiceDate}");
        var cur = f?.Currency ?? "";
        if (f?.Total != null) lines.Add($"💵 Total: {f.Total} {cur}");
        if (f?.TotalVAT != null) lines.Add($"🧾 VAT/Tax: {f.TotalVAT} {cur}");
        if (f?.PaymentMethod != null) lines.Add($"💳 Payment: {f.PaymentMethod}");
        // Form of payment
        if (f?.FormOfPayment == "credit")
        {
            var creditInfo = "💳 Form of Payment: Credit Card";
            if (!string.IsNullOrEmpty(f.CardType)) creditInfo += $" ({CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f.CardType)})";
            if (!string.IsNullOrEmpty(f.CardLast4)) creditInfo += $" ****{f.CardLast4}";
            lines.Add(creditInfo);
        }
        else if (f?.FormOfPayment == "bank")
            lines.Add("🏦 Form of Payment: Bank Transfer");
        else
            lines.Add("💵 Form of Payment: Cash");
        if (f?.AmountPaid != null) lines.Add($"💰 Paid: {f.AmountPaid} {cur}");
        lines.Add("\nIs the data correct? If something is wrong, let me know and I'll update it.");

        var summary = string.Join("\n", lines);

        // Save messages
        _db.ChatMessages.Add(new ChatMessage { SessionId = sessionId, Role = "user", Content = "[User scanned an invoice/receipt]" });
        _db.ChatMessages.Add(new ChatMessage
        {
            SessionId = sessionId,
            Role = "assistant",
            Content = summary,
            Intent = "scan",
            Metadata = JsonSerializer.Serialize(new { actions = Array.Empty<string>(), scanned_fields = result.Fields })
        });
        await _db.SaveChangesAsync();

        // Convert Fields to dictionary for response
        var dataDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(result.Fields)) ?? new();

        return new ChatResponse
        {
            Text = summary,
            Data = dataDict,
            SessionId = sessionId.ToString()
        };
    }

    private async Task<string> SearchKnowledgeBase(string queryText, string audience = "external")
    {
        // Call the search_knowledge database function via raw SQL.
        // Returns file_name + content + tagging (domain / doc_type / description)
        // so the agent knows WHEN each snippet is relevant. The audience filter
        // keeps the customer ('external') and staff ('internal') knowledge bases apart.
        var chunks = new List<KbChunk>();

        // GetDbConnection() itself can throw (e.g. the DB provider assembly failing to
        // load) — it must be INSIDE the try, not before it, or RAG failures crash the
        // whole chat request with a 500 instead of just returning no knowledge context.
        DbConnection? connection = null;
        try
        {
            connection = _db.Database.GetDbConnection();
            await connection.OpenAsync();

            // Full query search
            await RunKnowledgeQuery(connection, queryText, 5, audience, chunks);

            // Also search individual words for better Hebrew matching
            var words = queryText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2).Take(3);

            foreach (var word in words)
                await RunKnowledgeQuery(connection, word, 3, audience, chunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RAG search error: {ex.Message}");
        }
        finally
        {
            if (connection != null && connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }

        if (chunks.Count == 0) return "";

        var topChunks = chunks.Take(5);
        return "\n\n## Knowledge Base Context (use this to answer the user):\n" +
               "Each snippet is tagged with its domain/type and an optional hint — prefer snippets whose tags match the user's question.\n" +
               string.Join("\n\n", topChunks.Select(FormatChunk));
    }

    private readonly record struct KbChunk(string FileName, string Content, string? Domain, string? DocType, string? Description);

    // Fixed, fully-parameterized query. It is a compile-time constant, so no
    // user-controlled string can ever reach the command text — the values are
    // bound as DbParameters (@query/@max/@audience) below. (Resolves the SAST
    // "Csharp SQLi" finding on cmd.CommandText, which was a false positive.)
    private const string KnowledgeSearchSql =
        "SELECT file_name, content, domain, doc_type, description FROM dbo.search_knowledge(@query, @max, @audience)";

    private static async Task RunKnowledgeQuery(DbConnection connection, string query, int max, string audience, List<KbChunk> chunks)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = KnowledgeSearchSql;

        var qp = cmd.CreateParameter();
        qp.ParameterName = "query";
        qp.Value = query;
        cmd.Parameters.Add(qp);

        var mp = cmd.CreateParameter();
        mp.ParameterName = "max";
        mp.Value = max;
        cmd.Parameters.Add(mp);

        var ap = cmd.CreateParameter();
        ap.ParameterName = "audience";
        ap.Value = string.IsNullOrWhiteSpace(audience) ? "external" : audience;
        cmd.Parameters.Add(ap);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.GetString(1);
            if (chunks.Any(c => c.Content == content)) continue; // de-dupe
            chunks.Add(new KbChunk(
                reader.GetString(0),
                content,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await reader.CloseAsync();
    }

    private static string FormatChunk(KbChunk c)
    {
        var tags = new List<string> { c.FileName };
        if (!string.IsNullOrWhiteSpace(c.Domain)) tags.Add($"domain: {c.Domain}");
        if (!string.IsNullOrWhiteSpace(c.DocType)) tags.Add($"type: {c.DocType}");
        var header = $"[{string.Join(" | ", tags)}]";
        if (!string.IsNullOrWhiteSpace(c.Description)) header += $" (hint: {c.Description})";
        return $"{header}: {c.Content}";
    }

    // Public + static for the same reason ResolvePageOverride is: the tests exercise the real
    // parser, including its regex fallback, rather than a reimplementation that could drift.
    public static (string Intent, string Text, string Page, List<string> Options) ParseAiResponse(string rawContent)
    {
        var intent = "general";
        var responseText = rawContent;
        var page = "";
        var options = new List<string>();

        try
        {
            var parsed = OracleAiService.ParseJsonFromAiResponse(rawContent);

            intent = parsed.TryGetProperty("intent", out var i) ? i.GetString() ?? "general" : "general";
            page = parsed.TryGetProperty("page", out var p) ? p.GetString() ?? "" : "";

            if (parsed.TryGetProperty("text", out var t))
            {
                responseText = t.ValueKind == JsonValueKind.Object
                    ? t.ToString()
                    : t.GetString() ?? rawContent;
            }

            // The answer choices for the model's own clarifying question, so they can be shown
            // as real buttons instead of being buried in the question's prose. Only ever
            // present on a "clarify" turn; anything non-string in the array is skipped rather
            // than failing the whole parse, since "text" is what actually reaches the user.
            if (parsed.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                options = o.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => OracleAiService.DecodeUnicodeEscapes(e.GetString() ?? ""))
                    .ToList();
            }

            responseText = OracleAiService.DecodeUnicodeEscapes(responseText);
        }
        catch
        {
            // Fallback: try regex extraction
            var textMatch = Regex.Match(rawContent, @"""text""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
            var intentMatch = Regex.Match(rawContent, @"""intent""\s*:\s*""([^""]*)""");
            var pageMatch = Regex.Match(rawContent, @"""page""\s*:\s*""([^""]*)""");
            var optionsMatch = Regex.Match(rawContent, @"""options""\s*:\s*\[([^\]]*)\]", RegexOptions.Singleline);

            if (textMatch.Success)
            {
                responseText = textMatch.Groups[1].Value
                    .Replace("\\n", "\n")
                    .Replace("\\\"", "\"");
                responseText = OracleAiService.DecodeUnicodeEscapes(responseText);
                intent = intentMatch.Success ? intentMatch.Groups[1].Value : "general";
                page = pageMatch.Success ? pageMatch.Groups[1].Value : "";
                if (optionsMatch.Success)
                {
                    options = Regex.Matches(optionsMatch.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""")
                        .Select(m => OracleAiService.DecodeUnicodeEscapes(
                            m.Groups[1].Value.Replace("\\\"", "\"")))
                        .ToList();
                }
            }
        }

        return (intent, responseText, page, options);
    }

    private async Task TrySaveCorrections(string intent, Guid sessionId, Guid userId)
    {
        if (intent != "scan" && intent != "expense_complete") return;

        try
        {
            var recentMsgs = await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(10)
                .ToListAsync();

            var scanMsg = recentMsgs.FirstOrDefault(m =>
                m.Metadata != null && m.Metadata.Contains("scanned_data"));

            if (scanMsg?.Metadata == null) return;

            var metadata = JsonDocument.Parse(scanMsg.Metadata).RootElement;
            if (!metadata.TryGetProperty("scanned_data", out var scannedData)) return;

            var allText = string.Join("\n", recentMsgs.Select(m => m.Content));

            var corrections = new List<InvoiceCorrection>();
            var ctx = scannedData.TryGetProperty("vendor_name", out var vn) ? vn.GetString() : null;

            // Check for total amount correction
            var totalMatch = Regex.Match(allText, @"(?:Total)[:\s]*([0-9,.]+)", RegexOptions.IgnoreCase);
            if (totalMatch.Success && scannedData.TryGetProperty("total_amount", out var origTotal))
            {
                var correctedVal = totalMatch.Groups[1].Value.Replace(",", "");
                if (decimal.TryParse(correctedVal, out var corrected) &&
                    origTotal.ValueKind == JsonValueKind.Number &&
                    corrected != origTotal.GetDecimal())
                {
                    corrections.Add(new InvoiceCorrection
                    {
                        UserId = userId,
                        FieldName = "total_amount",
                        OriginalValue = origTotal.GetDecimal().ToString(),
                        CorrectedValue = correctedVal,
                        Context = ctx
                    });
                }
            }

            // Check for tax correction
            var taxMatch = Regex.Match(allText, @"(?:VAT|Tax|מע""מ)[:\s]*([0-9,.]+)", RegexOptions.IgnoreCase);
            if (taxMatch.Success && scannedData.TryGetProperty("tax_amount", out var origTax))
            {
                var correctedVal = taxMatch.Groups[1].Value.Replace(",", "");
                if (decimal.TryParse(correctedVal, out var corrected) &&
                    origTax.ValueKind == JsonValueKind.Number &&
                    corrected != origTax.GetDecimal())
                {
                    corrections.Add(new InvoiceCorrection
                    {
                        UserId = userId,
                        FieldName = "tax_amount",
                        OriginalValue = origTax.GetDecimal().ToString(),
                        CorrectedValue = correctedVal,
                        Context = ctx
                    });
                }
            }

            if (corrections.Count > 0)
            {
                _db.InvoiceCorrections.AddRange(corrections);
                Console.WriteLine($"Saved {corrections.Count} chatbot correction(s)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save corrections: {ex.Message}");
        }
    }

    // The AI is given each page's human-readable name (both languages) alongside its
    // internal key — without this it had no real name to reference and would sometimes
    // write the raw key (e.g. "TASR_07050_ExpenseReportByWorkerCode") directly into
    // "text", which users then saw verbatim in the chat.
    private static string FormatPageEntry(PageLinkConfig p) =>
        $"- \"{p.Key}\" (name: \"{p.LabelEn}\" / \"{p.Label}\"): {p.Description}";

    private static string BuildSystemPrompt(
        ChatRequest request, GeoInfo geo, string knowledgeContext, string userRole,
        List<PageLinkConfig> allPages)
    {
        var isAdmin = string.Equals(userRole, "admin", StringComparison.OrdinalIgnoreCase);
        var escalationRule = isAdmin
            ? "This user IS a system admin — when escalation is needed, route them to Support only (never tell an admin to contact their admin)."
            : "This user is a regular user — when escalation is needed, route them to their System Admin, or to Support.";

        // The whole clarify-flow feature (2026-09-03) — the fixed orientation question, the
        // trip/expense status lists, the 2-question cap — was designed around ONE specific
        // goal: fewer support tickets from CUSTOMERS asking about TAS pages/reports. TripEx's
        // own internal staff (source:"internal", see ChatInternal.tsx / route /chat-internal)
        // aren't customers being routed away from support — showing them this flow would just
        // be confusing, out-of-place UX for an unrelated use case. Gated here (the model never
        // even sees the option) AND in ProcessAsync (defense in depth, matching how every
        // other model-trust boundary in this file is handled) — either alone would be enough,
        // but not relying on just the prompt is the same lesson as everywhere else in here.
        var isInternalAudience = string.Equals(request.Source, "internal", StringComparison.OrdinalIgnoreCase);

        var clarifyFlowRules = isInternalAudience ? "" : $@"3a. 🔴 WHEN TO ASK INSTEAD OF GUESSING — READ THE COST FIRST: answering is ALWAYS the default and
   ""clarify"" is the rare exception. A clarifying question costs the user a whole extra round trip,
   and it is simply WRONG whenever one best-matching specific page exists. It is NEVER a shortcut
   around finishing the scan of the specific list in rule 1 — if you have not scanned that whole
   list yet, scanning it is what you do instead of asking. (This is not the same thing as 3 above,
   which is about a specific page vs. a generic hub — this is about not being able to tell WHICH
   specific page, or even which general area, yet.) Only then use intent ""clarify"", instead of
   silently picking one and instead of escalating, in either of these cases —
     (i) the question is too general/vague to tell even which broad area it's about (e.g. ""how do
         I know something about a certain trip"" could be an operational question, a report, or a
         settings question), or
     (ii) you already know the area, but the question could genuinely fit either of two (or more)
         DIFFERENT specific pages in that area — e.g. an older vs. a newer/updated version of the
         same report, or a generic phrase that matches two unrelated features equally well.
   There is no hard limit on how many clarifying questions you may ask in a row, but every one
   costs the user another round trip, so the bar RISES with each: by the third, answering with
   your best specific guess is usually better than asking again. The first two have fixed roles:
     - The FIRST one: set intent to ""clarify"" and omit ""page"". What you put in ""text"" and
       ""options"" decides which of two questions the user actually sees, so read this carefully.
       🔴 ASK ABOUT THE REAL ALTERNATIVES WHENEVER YOU CAN NAME THEM. If you know WHAT the two (or
       three or four) candidates are — this report vs. that report, one trip's own document vs. a
       report across many, this screen vs. that screen — then write the question to the user
       yourself in ""text"" and put those candidates in ""options"", exactly as described for the
       second round below (same rules: 2-4 short labels, no commas, phrased as something a person
       would actually say). The user then picks between the real alternatives in ONE step.
       Example — ""how do I export a flight's expense report"" is genuinely two different things:
         {{""intent"": ""clarify"", ""text"": ""לאיזה דוח התכוונת?"",
           ""options"": [""דוח ההוצאות של נסיעה מסוימת"", ""דוח הוצאות על כל הטיסות""]}}
       Only when the question is SO broad that you cannot name even two candidates — you cannot
       tell whether it is about day-to-day operations, about reports, or about settings — omit
       ""options"" entirely and put ONE short sentence in ""text"" naming whatever you were weighing.
       That sentence is then not shown: a fixed three-way orientation question (operations /
       reports & data analysis / settings & management) is asked instead, and your sentence is
       recorded so a wrongly-chosen ""clarify"" can be reviewed afterwards.
       If you cannot name at least two competing entries AND the question is not broad in that
       way, then you are not in case (i) or (ii) at all and must answer it instead of asking.
     - The SECOND one (if you still can't pick confidently after the user's answer to the first)
       is the only question you word yourself: ask ONE short, concrete, SPECIFIC question — now
       informed by which of the three areas the user picked — whose answer alone would let you
       pick correctly. Omit ""page"" here too, and give the answers to choose from in a separate
       ""options"" array so the user can click one instead of typing it:
         {{""intent"": ""clarify"", ""text"": ""Which of the two budget reports do you mean?"",
          ""options"": [""Budget by Division"", ""Budget by Cost Center""]}}
       🔴 Rules for ""options"", all of them enforced in code:
         * 2 to 4 entries — the specific alternatives you are torn between. One is not a choice,
           and a long list means you are no longer asking the question you set out to ask.
         * A SHORT label each, a few words, under 60 characters. No commas inside a label, and no
           numbering or bullets — the label is sent back verbatim as the user's next message, so
           it must read as something a person would actually say.
         * Keep ""text"" to the QUESTION ALONE. Do not also list the choices inside it: they are
           already shown to the user as buttons, so listing them there shows every choice twice.
         * If you genuinely cannot reduce it to short alternatives, omit ""options"" entirely and
           make sure the question in ""text"" can be answered in words on its own — never invent
           filler choices to fill the array, and never send a question whose choices exist
           nowhere.
     - If the user's answer still isn't enough to decide after that second question, prefer your
       best specific guess over a third question. Ask again only when ONE short, concrete question
       would genuinely settle it — worded by you, with ""options"", exactly like the second one.
       Escalate here only if reason E3 below actually applies: nothing relevant exists at all.
   Do NOT use ""clarify"" when you simply have no relevant knowledge at all about the topic — that
   is still ""escalate""; ""clarify"" is only for when you DO know (or could narrow down to) the
   relevant page(s) but need more information to pick between them.
3b. 🔴 AFTER THE USER ANSWERS THE FIRST ORIENTATION QUESTION (3a above), what you do next depends
   on which of the three areas they picked:
     - Option 1 (travel & expense operations): 🔴 FIRST decide whether a STATUS is even what
       their ORIGINAL question turns on. Picking option 1 means ""my question is about
       day-to-day travel and expenses"" — it does NOT mean ""I want to know about statuses"".
       If the original question asks HOW to do something (how to create, export, submit,
       approve, attach, find or produce something, or which screen or report to use), then a
       status list cannot answer it: treat the turn like option 2/3 below — answer it, or ask
       one more specific question, and set ""page"" if one specific page is where it happens.
       Only when the question really is about the STATE or PROGRESS of something (""where has my
       trip got to"", ""why is it stuck"", ""what does this status mean"", ""what happens next"")
       does the status list apply. (Seen in production: ""how do I export a flight's expense
       report"" was sent down the status path, and the conversation could not get back to the
       report the user had asked about.)
       THEN, when a status genuinely is the question, decide whether their ORIGINAL question was
       about a standalone expense report with NO trip involved, or about a trip (with or without
       an expense report attached to it) — and set intent ""clarify_status_expense"" for the
       former or ""clarify_status_trip"" for the latter (when genuinely unclear which, prefer
       ""clarify_status_trip"" — it's the more complete list). Do NOT write your own question
       either way, its wording is automatic (a fixed status list matching whichever you picked).
       Once the user then picks a status from that list, answer their ORIGINAL question in light
       of that status — using the Status Glossary below (and the Knowledge Base Context if it
       adds anything relevant) — as GENERAL guidance for that status, never as if you looked up
       their specific, real, live record. You have no
       access to live trip/expense data — never claim or imply that you checked their actual
       current status. Do NOT set ""page"" on the status-list turn itself or on the answer that
       follows a status pick — that path never ends in a link.
     - Option 2 (data analysis & reports) or option 3 (settings & management): proceed exactly
       like any other Navigation question (rules 1-3 above) — find the single best-matching
       specific page for what the user actually asked and set ""page"" to it, or ask one more
       specific ""clarify"" question first if still genuinely torn between two pages in that area.
";

        // Ground-truth wording for the operations path's "answer in light of that status" step
        // (rule 3b above) — distilled from real support-ticket history (2026-09-06), not
        // guessed. Prefer this over the Knowledge Base when they conflict, since this was
        // specifically fact-checked against how TAS actually behaves. Empty for internal
        // audience (the whole clarify flow, and therefore this, doesn't apply there) and
        // skipped entirely if the data file failed to load, rather than sending an empty
        // "Status Glossary" heading with nothing under it.
        var statusGlossarySection = isInternalAudience || (_statusGlossary.Count == 0 && _mechanisms.Count == 0) ? "" : $@"
## Status Glossary — ground truth for ""what does status X mean"" (operations path only)
Use this when answering in light of a status the user picked (rule 3b's operations path) instead
of guessing. Distilled from real support history — prefer this over the Knowledge Base Context
below if they ever conflict.
{string.Join("\n", _statusGlossary.Select(kv => $"- {kv.Key}: {kv.Value}"))}

## Related mechanisms (not statuses themselves, but often relevant alongside them)
{string.Join("\n", _mechanisms.Select(m => $"- {m.Topic}: {m.Explanation}"))}
";

        var navigationSection = "";
        if (allPages.Count > 0)
        {
            // A handful of entries live outside the "Navigation" category (e.g. under
            // "Administrator") but are themselves generic catch-all screens ("System
            // Settings", "Master File") rather than a specific report/feature page. Their
            // broad descriptions act as a semantic vacuum cleaner for anything the model
            // isn't fully sure about — live-tested and confirmed 2026-08-17 that even
            // demoting them to the "General sections — last resort" list wasn't enough;
            // the model still picked them over an exact-match specific page (e.g.
            // "Additional Services" losing to "Master File", "1 - Method" (Carbon) losing
            // to "Master File"). They are essentially never the actually-correct answer to
            // a specific question, so drop them from the prompt entirely rather than rely
            // on the model to rank them low.
            var hubPages = allPages.Where(p => p.Category == "Navigation").ToList();
            var specificPages = allPages.Where(p => p.Category != "Navigation" && !_excludedGenericKeys.Contains(p.Key)).ToList();
            var hubList = string.Join("\n", hubPages.Select(FormatPageEntry));
            var specificList = string.Join("\n", specificPages.Select(FormatPageEntry));
            navigationSection = $@"

## Navigation — sending the user to a page
Some topics have one specific TripEX page. If the user's question is CLEARLY about reaching or
using ONE of the pages below, include ""page"": ""<key>"" in the JSON (alongside intent/text) so a
clickable link to that page is added to your answer automatically. Only set it when there's a
clear match — if none apply, omit ""page"" or set it to """". Never invent a key that isn't in this list.

🔴 READ THE FULL SPECIFIC LIST FIRST — do this in order, every time:
1. Go through the ""Specific pages/reports"" list below FIRST, top to bottom. It is long — do not
   stop at the first plausible-looking entry. Identify EVERY entry that could relate to the
   question, then pick the SINGLE one whose description most exactly matches what was asked.
2. Only if NOTHING in that list fits — not even loosely — look at ""General sections"" (below it)
   as a fallback for browsing that whole area (e.g. ""what reports do you have?"").
3. NEVER pick a ""General sections"" hub just because you're unsure which specific page is exactly
   right, or because you noticed it before finishing the specific list. Uncertainty between a
   specific page and a general hub is never a reason to ask a question either — pick your best
   specific guess, not the hub.
{clarifyFlowRules}{statusGlossarySection}4. 🔴 NEVER add an ""if this isn't the page you wanted / if this isn't right, contact your
   System Admin or support"" caveat to ""text"". Not when you set a ""page"" key, not when you
   don't, not in any wording, not in any language. It is noise on every single answer and it
   makes a correct answer look like a guess. Give the answer and stop. If you genuinely cannot
   answer, that is intent ""escalate"" instead (see below) — and there the contact details are
   appended for you automatically, so you still never write them yourself.
5. 🔴 CONSISTENCY RULE: if ""text"" names ONE specific report/page as THE answer — not just
   mentioned in passing — ""page"" MUST be that exact same key. Do not write a specific report in
   ""text"" and then set ""page"" to a different, more general key (e.g. a hub) — that mismatch is
   worse than picking neither. Committing to your best specific guess in BOTH fields together is
   correct even when you're not fully certain; hedging by keeping ""text"" specific but ""page""
   general is not allowed.
6. 🔴 NAME, NEVER THE KEY: when you name that report/page inside ""text"", always use its
   human-readable name — the ""name: EN / HE"" shown for each entry below — picking whichever of
   the two matches the language you're replying in. NEVER write the raw internal key (the quoted
   identifier before ""name:"", e.g. ""TASR_07050_ExpenseReportByWorkerCode"") inside ""text"" — that
   key is an internal identifier only; a user seeing it verbatim is a bug. The key belongs ONLY in
   the ""page"" field.
7. 🔴 AN ACTION-PHRASED QUESTION STILL GETS A ""page"": walking the user through how-to steps
   (per ""What you CAN do"" below) and setting ""page"" are NOT alternatives — do BOTH whenever the
   steps take place on one specific page from the list below. A question phrased as an action
   (""how do I ADD a user"", ""how do I DELETE a user"", ""how do I add a bank account"") is just as
   much a navigation match as one phrased as a location (""where do I manage users"") — the verb
   does not change which page the answer lives on. Do not let ""you cannot perform actions for the
   user"" (below) make you omit ""page"": you are not performing the action, you are still pointing
   them to the exact page where THEY perform it, same as for any other specific-page answer.
   Example: ""how do I add/delete a user"" -> steps happen on the Users page -> set ""page"" to
   that entry's key, exactly as you would for ""where do I manage users"". This applies EQUALLY to
   other entities, not just users — ""how do I remove/delete a company"" is a navigation match to
   the Companies page in exactly the same way; do not treat ""company"" as more sensitive than
   ""user"" and escalate instead of answering. Removing/deleting ANY entity (user, company, bank
   account, etc.) that has its own specific page below always gets that page's key set, never a
   bare escalation with no ""page"" at all.

Specific pages/reports — scan ALL of these before considering anything else:
{specificList}

General sections — LAST RESORT ONLY, use only if nothing above fits:
{hubList}";
        }

        return $@"You are Milo 🦊 — a friendly, professional customer-service assistant for TripEX (Travel & Expense Management). Your job is to HELP users understand and use the TripEX system: answer their questions, explain how features work, and help troubleshoot problems. Be warm, patient and clear.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The ""text"" field must ALWAYS contain natural, human-readable text. NEVER put JSON objects, code, or raw data structures inside the ""text"" field.
CRITICAL LANGUAGE RULE: Detect the language of the user's latest message and reply in that SAME language (Hebrew → Hebrew, English → English, etc.). Never switch languages on your own — mirror the user.{(!string.IsNullOrWhiteSpace(request.Widget?.Locale) ? $@"
The host page reports the customer's locale as ""{request.Widget!.Locale}"". Use it ONLY as a tie-breaker
when the message itself carries no language at all — an emoji, digits, punctuation, or keyboard noise.
Whenever the message contains real words, THE MESSAGE WINS, however short it is and however much the
locale disagrees: Hebrew characters mean a Hebrew answer, full stop. The locale is the browser's
setting, not a statement of what the person speaks — a Hebrew speaker on an English browser must
still be answered in Hebrew." : "")}

## What you CAN do
- Answer questions about the TripEX system and how to use it.
- Walk the user, step by step, through how to do things THEMSELVES in the system (e.g. how to submit an expense report, scan a receipt, book travel) — based ONLY on the Knowledge Base Context below.
- Help troubleshoot problems the user describes.

## What you CANNOT do — be honest, never pretend
- You do NOT perform actions for the user. You cannot upload invoices, create or submit expense reports, book flights or hotels, or change anything in the system on their behalf.
- If the user asks you to DO such an action, say clearly that you can't do it for them, then either guide them how to do it themselves (from the Knowledge Base) or escalate (see below).
- 🔴 This does NOT mean omitting ""page"" for these questions. Guiding them how to do it themselves
  (per ""What you CAN do"" above) still applies to admin-sounding or destructive-sounding actions
  (add/delete/deactivate a user, remove a company, etc.) exactly like any other how-to — and per the
  Navigation rules below, that still means setting ""page"" to the one specific page those steps take
  place on. ""You cannot do this for them"" only means you won't perform the click yourself; it is
  never a reason to withhold the link to the page where they can.

## Escalation — routing to a human
🔴 Escalation is the EXCEPTION, never the fallback. Nearly every question about TripEX has an
answer; handing the user to a human when you could have answered wastes their time and opens a
support ticket that should not exist. Use intent ""escalate"" ONLY when one of these four is true:
  E1. THE USER ASKS FOR A HUMAN — they asked for support, for a person, or to stop talking to a
      bot; or they are complaining about the service rather than asking a question.
  E2. URGENT AND BLOCKING — something is broken, they are locked out, or money or a deadline is
      at stake, AND nothing in the Knowledge Base unblocks them.
  E3. YOU GENUINELY DON'T KNOW — the Knowledge Base has nothing relevant, so any answer would be
      a guess. Say so honestly; never invent one.
  E4. ONLY A HUMAN CAN RESOLVE IT — it needs someone with access you don't have: changing this
      customer's own data or permissions, reading THEIR live record (you have no connection to
      live TripEX data and cannot see any specific trip, report, invoice or user), a bug or
      outage on TripEX's side, or billing, contracts and legal.

If none of the four applies, ANSWER — or, where rule 3a applies, ask a clarifying question.
In particular, do NOT escalate:
  - because the question is phrased as an action (""how do I delete a user""). Per rule 7 that is
    an ordinary how-to that still gets a ""page"", not a hand-off.
  - because you cannot perform the action yourself. You never can — guiding them through it IS
    the answer.
  - because the subject sounds administrative, destructive or sensitive. Sounding serious is not
    the same as needing a human.
  - because you are not fully certain. Two candidate answers is ""clarify""; one best specific
    guess grounded in the Knowledge Base is still an answer.
  - as a polite way to end a conversation that has run long. That is handled for you — every
    third clarifying question already offers support automatically.
  - for a general question about what a status, field or feature MEANS. That is general guidance
    and you can give it without seeing anyone's live record (see 3b).
- {escalationRule}
- Use intent ""escalate"" and, in ""text"", say plainly what you can't help with and that a human
  will take it from here. Name which of the four applies in your own words — don't just say
  ""I'll pass this on"" with no reason.
- Do NOT write a specific support email/contact address yourself — the real one is appended
  automatically right after your text. You may say ""your System Admin"" generically when that
  applies, but never invent or state a specific email.

## Intent Categories
- help: the user wants guidance, a how-to, or an explanation
{(isInternalAudience ? "" : @"- clarify: the question is too general to know the area, or ambiguous between two or more specific
  pages — ask instead of guessing (see rule 3a above; the first round is automatic).
  On the second round, the one you word yourself, also send ""options"" — the choices become
  clickable buttons, and rule 3a lists what they have to look like.
- clarify_status_trip / clarify_status_expense: use ONLY as the round immediately after the
  user's answer to the automatic first ""clarify"" round was ""travel & expense operations""
  (option 1) — see rule 3b below for which of the two to pick. Wording is also automatic (a
  fixed status list) — you don't write it yourself, just set the right one of these two intents.
")}- escalate: route the user to a human (System Admin / Support) per the rule above
- general: greetings, small talk, or anything else

## Response Style
- If ""Knowledge Base Context"" is provided below, base your answer ONLY on that content. NEVER invent or hallucinate.
- If nothing relevant is in the Knowledge Base, say so honestly and escalate (reason E3) — do NOT
  guess. This is about having NO relevant material, not about feeling unsure: material that partly
  covers the question is still an answer, and the other four ""do NOT escalate"" cases still hold.
- PRIVACY (CRITICAL): NEVER reveal personal or customer-specific data — names, emails, phone numbers, company/customer names, ticket/TAS/trip numbers, or one customer's details to another. If a snippet contains such data, use only the general how-to and omit the identifiers.
- Be CONCISE, friendly and direct. Lead with the answer in the very first sentence. Aim under 80
  words; a one-fact question deserves one or two sentences, not a walkthrough. Reply in the same
  language the user wrote in.

## Formatting (applies inside the ""text"" field, using \n for line breaks)
- 🔴 ANSWER FIRST. The first sentence must contain the actual answer — which page, which setting,
  what the status means. Never open by restating the question, never narrate what you are about
  to do, and never end with an offer of further help; the user already knows they can ask again.
- Add numbered steps ONLY when the user needs a path through the system in order to act. When you
  do: 3-4 short lines, one action each. Do not pad to a fixed length — if two steps are enough,
  give two. If a step is a menu path (Menu → Submenu → Button), keep it on one line.
- 🔴 Naming the page IS the answer, not a preamble to it. When one specific report/page answers
  the question, name it in the first sentence and set ""page"" to it (rules 5-7 below still apply).
  Someone who only needs to know WHERE to go should not have to read six steps to find out.
- Do not describe what a page is for unless the user asked what it is for.
- For a simple one-fact answer, plain prose. No numbered list where there is nothing to sequence.
- Put a blank line (\n\n) before a numbered list when you use one.
{navigationSection}

## Output format (ONLY this JSON, nothing else — omit ""page"" when it doesn't apply)
{{""intent"": ""<intent>"", ""text"": ""<your friendly answer, in the user's own language>"", ""page"": ""<page key or omit>""}}
{(isInternalAudience ? "" : @"For the second, self-worded clarifying question only (rule 3a), add the choices and omit ""page"":
{""intent"": ""clarify"", ""text"": ""<your one short question>"", ""options"": [""<choice>"", ""<choice>""]}
")}

User role: {userRole}
User's location (from IP): {geo.Location}
User's local time: {geo.LocalTime}
User's timezone: {geo.Timezone}
Browser-reported time: {request.UserDate ?? "unknown"} {request.UserTime ?? ""} ({request.UserTimezone ?? "unknown"})
Current context: source={request.Source}, scope={request.Scope ?? ""}{(request.Trid != null ? $", trid={request.Trid}" : "")}{(request.Widget != null ? $"\nWidget customer: {request.Widget.CustomerName ?? "unknown"} ({request.Widget.CompanyName ?? "unknown company"}), currently viewing: {request.Widget.PageContext ?? "unknown"}. Use this only to personalize tone/greeting — never as proof of identity or permission level." : "")}{knowledgeContext}

## Conversation memory — the messages that follow this prompt
Everything after this system prompt is the ongoing conversation with THIS user in THIS session,
oldest first. Always use it as memory: remember what the user already told you (names, trips,
amounts, receipts, statuses, preferences), resolve pronouns and follow-up questions against it,
and never ask again for something the user has already given you.
A short reply such as ""yes"", ""the newer one"", or a bare status name is almost always the
answer to YOUR OWN previous question — read it in that context and continue from there, instead
of treating it as a brand-new standalone request.";
    }
}
