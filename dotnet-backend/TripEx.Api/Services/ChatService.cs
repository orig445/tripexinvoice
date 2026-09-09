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

    // Milo's own judgment on "is this question ambiguous enough to ask a clarifying
    // question" isn't perfectly reliable (same class of issue as its page-selection
    // judgment) — without a hard code-level cap, a confused model could keep asking
    // "clarify" forever instead of ever reaching an answer or a human, defeating the
    // whole point of adding it (fewer support tickets, not an endless interrogation).
    private const int MaxConsecutiveClarifications = 2;

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
    public static bool OptionsRenderAsButtons(List<string> options, bool clientRendersParamerter)
        => clientRendersParamerter && OptionsCanBeButtons(options);

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
    public static bool OptionsCanBeButtons(List<string> options)
        => options.Count >= 2
           && options.All(o => o.Length <= MaxOptionLabelLength)
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
        IConfiguration configuration)
    {
        _db = db;
        _oracle = oracle;
        _invoiceService = invoiceService;
        _geoService = geoService;
        _logger = logger;
        _supportContact = configuration["Support:Contact"] ?? "support@tripex.io";
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
    /// </summary>
    private async Task<bool> CanResumeSessionAsync(Guid sessionId, Guid userId)
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
            if (owner == null) return true;

            if (owner == userId) return true;

            _logger.LogWarning("[CHAT] Session {SessionId} belongs to another user — starting a fresh conversation instead", sessionId);
            return false;
        }
        catch (Exception ex)
        {
            // DB unavailable: honour the token. Dropping a user's history because we could not
            // verify ownership would be a worse failure than the one this guards against.
            Console.WriteLine($"⚠️ [CHAT] Session ownership not verified (DB unavailable): {ex.Message}");
            return true;
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
        if (!string.IsNullOrEmpty(request.SessionToken)
            && Guid.TryParse(request.SessionToken, out var existingId)
            && await CanResumeSessionAsync(existingId, userId))
        {
            sessionId = existingId;
            continuedSession = true;
        }
        else
        {
            try
            {
                var session = new ChatSession { UserId = userId, Source = request.Source };
                _db.ChatSessions.Add(session);
                await _db.SaveChangesAsync();
                sessionId = session.Id;
            }
            catch (Exception ex)
            {
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

        // ── Save user message (best-effort) ──
        try
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId,
                Role = "user",
                Content = request.Text
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] User message not persisted (DB unavailable): {ex.Message}");
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
        messages.AddRange(history.Select(h => new OracleMessage { Role = h.Role, Content = h.Content }));

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
        var rawContent = await _oracle.ChatAsync(messages, maxTokens, temperature, allowCustomModel: true);
        sw.Stop();

        // ── Parse response ──
        var (intent, responseText, page, modelOptions) = ParseAiResponse(rawContent);

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
        var quickReplies = new List<string>();
        var isInternalAudience = string.Equals(request.Source, "internal", StringComparison.OrdinalIgnoreCase);
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
        // skipped further down. A flag rather than re-testing the intent afterwards, because the
        // 2-in-a-row cap REASSIGNS intent to "escalate" — that turn also has its page cleared
        // deliberately, and an intent test after the fact would no longer be able to tell.
        var pageDeliberatelyCleared = false;
        if (!isInternalAudience && ClarifyTypeIntents.Contains(intent))
        {
            clarifyRationale = responseText;
            var consecutiveClarifications = CountTrailingConsecutiveClarifications(historyRows);
            page = null; // none of these ever link to a page — there's nothing to link to yet
            pageDeliberatelyCleared = true;

            if (consecutiveClarifications >= MaxConsecutiveClarifications)
            {
                intent = "escalate";
                // The model's own "text" was phrased as yet another question, not an
                // escalation explanation — replace it with a fixed, honest line instead of
                // showing a mismatched question right above the support-contact line.
                responseText = IsHebrewDominant(request.Text)
                    ? "כדי לוודא שתקבל את העזרה המדויקת ביותר, אני מעביר את זה לתמיכה."
                    : "To make sure you get the most accurate help, let me connect you with support.";
            }
            else if (intent == "clarify_status_trip" || intent == "clarify_status_expense")
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
                // The FIRST clarifying question on any topic is always this fixed, three-way
                // orientation question — deterministic and identical every time, instead of
                // whatever the model would have improvised, so the opening question is
                // predictable and reliably useful regardless of how well the model judged its
                // own phrasing. The model's OWN clarifying question is used for the SECOND
                // round instead (the final `else` case below) — by then the user's answer here
                // has already narrowed things down to one area, so it can ask something specific.
                var isHebrew = IsHebrewDominant(request.Text);
                clarifyOptions = isHebrew
                    ? new() { "תפעול שוטף של נסיעות והוצאות", "ניתוח נתונים ודוחות במערכת", "ניהול ושינוי הגדרות במערכת" }
                    : new() { "Travel & expense operations", "Data analysis & reports", "System management & settings" };
                clarifyQuestion = isHebrew
                    ? "כדי שאוכל לכוון אותך לתשובה המדויקת ביותר — במה מדובר?"
                    : "To point you to the most accurate answer — which of these is it about?";
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
                quickReplies = OptionsCanBeButtons(clarifyOptions) ? clarifyOptions : new List<string>();
                optionsRenderAsButtons = OptionsRenderAsButtons(quickReplies, request.IsTasWidgetClient);
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

            responseText += $"\n\n<a href=\"{safeUrl}\" target=\"_top\" rel=\"noopener\">{safeLabel}</a>";
        }

        var escalated = intent == "escalate";
        // The support contact line is added here (deterministically, in code) rather than left to
        // the AI's own wording — guarantees it always names the real address. It now appears ONLY
        // on a true escalation, where routing the user to a human IS the point of the message.
        //
        // It used to be appended after every page link too ("if this isn't exactly the page you
        // were looking for, contact your System Admin or support…"). Removed 2026-09-09 at Roi's
        // request: a page link is attached to most answers, so that caveat was on nearly every
        // reply — it reads as the bot hedging on an answer that is in fact correct, and works
        // against the "answer confidently, be concise" direction the rest of the prompt gives.
        if (escalated)
        {
            responseText += isHebrewReply
                ? $"\n\nניתן לפנות לתמיכה במייל {_supportContact}"
                : $"\n\nYou can reach support by email at {_supportContact}";
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
            SupportContact = escalated ? _supportContact : null
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
   You get at most 2 clarifying questions in a row for the same topic:
     - The FIRST one is handled FOR you automatically — a fixed, three-way orientation question
       (operations / reports & data analysis / settings & management). You do not need to write
       your own wording for it: set intent to ""clarify"", omit ""page"", and put ONE short sentence
       in ""text"" naming the two or more specific entries you are genuinely torn between. That
       sentence is not shown to the user — it is recorded, so a wrongly-chosen ""clarify"" can be
       reviewed afterwards. If you cannot name at least two competing entries, then you are not in
       case (i) or (ii) at all and must answer the question instead of asking one.
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
     - If the user's answer is still not enough to decide after that second question, make your
       best specific guess (or escalate if genuinely nothing fits) rather than asking a third time.
   Do NOT use ""clarify"" when you simply have no relevant knowledge at all about the topic — that
   is still ""escalate""; ""clarify"" is only for when you DO know (or could narrow down to) the
   relevant page(s) but need more information to pick between them.
3b. 🔴 AFTER THE USER ANSWERS THE FIRST ORIENTATION QUESTION (3a above), what you do next depends
   on which of the three areas they picked:
     - Option 1 (travel & expense operations): decide whether their ORIGINAL question was about
       a standalone expense report with NO trip involved, or about a trip (with or without an
       expense report attached to it) — then set intent ""clarify_status_expense"" for the
       former or ""clarify_status_trip"" for the latter (when genuinely unclear which, prefer
       ""clarify_status_trip"" — it's the more complete list). Do NOT write your own question
       either way, its wording is automatic (a fixed status list matching whichever you picked).
       Once the user then picks a status from that list, answer their ORIGINAL question in light
       of that status — using the Status Glossary below (and the Knowledge Base Context if it
       adds anything relevant) — as GENERAL guidance for that status, never as if you looked up
       their specific, real, live record. You have no
       access to live trip/expense data — never claim or imply that you checked their actual
       current status. Do NOT set ""page"" anywhere in this operations path — it never ends in a
       link.
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
The host page reports the customer's locale as ""{request.Widget!.Locale}"" — prefer that over your own language detection whenever the two would disagree (e.g. a short or ambiguous message)." : "")}

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
Escalate when: you don't know the answer, the Knowledge Base has nothing relevant, or the user needs a human to take action.
- {escalationRule}
- Use intent ""escalate"" and, in ""text"", explain the situation and let the user know a human will help.
- Do NOT write a specific support email/contact address yourself — the real one is appended
  automatically right after your text. You may say ""your System Admin"" generically when that
  applies, but never invent or state a specific email.

## Intent Categories
- help: the user wants guidance, a how-to, or an explanation
{(isInternalAudience ? "" : @"- clarify: the question is too general to know the area, or ambiguous between two or more specific
  pages — ask instead of guessing (see rule 3a above; first round is automatic, max 2 in a row).
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
- If nothing relevant is in the Knowledge Base, say so honestly and escalate — do NOT guess.
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
