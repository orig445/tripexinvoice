using System.Text.RegularExpressions;

namespace TripEx.Api.Services;

/// <summary>
/// What a knowledge snippet may show a customer (Roi, 2026-10-05). Most of the knowledge base is
/// real support history: 187 Glassix transcripts, named Glassix_&lt;ticket&gt;_&lt;Company&gt;_&lt;subject&gt;.pdf,
/// carrying other customers' email addresses, phone numbers, ticket and trip numbers and company
/// names — and up to five snippets go into every customer's prompt. They are masked here, on the
/// way into the prompt, so the database and seed-knowledge.sql stay as they are and a document
/// uploaded later is covered the moment it can be found.
///
/// What is replaced:
///   [email]   every address except TripEx's own: *@tripex.io and the configured Support:Contact —
///             and what is left of one where a chunk cut it off, the PDF broke it over two lines or
///             right-to-left text turned it round.
///   [phone]   international (+ or 00), Israeli (0X / 972), North American, and the reversed
///             "…972+" form PDF extraction leaves behind in right-to-left text.
///   [number]  a run of 7 or more digits — ticket, trip, passport, interface and booking ids. Not
///             the fraction of a decimal (an exchange rate of 3.7280000) and not hex (0x80131040).
///   [company] the customer a Glassix file is named after, wherever it appears in that snippet, and
///             — given <see cref="KnowledgeCompanies"/> — every other company the knowledge base's
///             transcripts are about, in every snippet; the file name itself becomes
///             "Support conversation example: &lt;subject&gt;".
/// Dates (05/10/2026), times, amounts (1,250.00), page paths and short codes match none of them.
///
/// The rules were tuned against every chunk of seed-knowledge.sql: afterwards no address or part of
/// one outside the kept ones remains, no Israeli phone number, and no company the knowledge base
/// names written as a name; and every time and TAS trip number — and every date and amount that is
/// not part of a phone number — is still there.
/// </summary>
public static class KnowledgePrivacy
{
    public const string EmailMask = "[email]";
    public const string PhoneMask = "[phone]";
    public const string NumberMask = "[number]";
    public const string CompanyMask = "[company]";

    /// <summary>What a snippet becomes if masking it cannot finish — never the unmasked text.</summary>
    public const string Withheld = "[withheld]";

    public const string ConversationLabel = "Support conversation example";

    // Phone numbers are 8 to 15 digits (E.164 caps them at 15). A run outside that is something else.
    private const int MinPhoneDigits = 8;
    private const int MaxPhoneDigits = 15;

    // Some chunks are binary PDF residue. Every pattern below is linear on ordinary text; the timeout
    // is only there so that a pathological chunk is withheld rather than holding up an answer.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // An address, or what is left of one: cut off where a chunk ends ("dana.levi@acme."), broken over
    // two lines by the PDF ("dana@acme-⏎tech.co.il", "dana@acme⏎.co.il") or turned round by
    // right-to-left text ("acme.co.il@dana.l"). A whole address is tried first, so one of TripEx's
    // own stays whole. The part before the @ has to have a letter or digit in it: "+@Dana" is a
    // Glassix mention, not an address.
    private static readonly Regex Email = new(
        @"(?<![A-Za-z0-9._%+-])(?:(?<whole>[A-Za-z0-9._%+-]+@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![A-Za-z0-9-]))" +
        @"|(?=[._%+-]*[A-Za-z0-9])[A-Za-z0-9._%+-]+@[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]*)*" +
        @"(?:(?<=-)[ \t]*\r?\n[ \t]*[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)+|[ \t]*\r?\n[ \t]*\.[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]*)*)?)",
        Options, MatchTimeout);

    // The other half of an address a chunk boundary cut: the chunk before ends "dana.levi@acme" (Email,
    // above) and this one starts with the rest of the domain — "acmegroup.com", "-tech.co.il>",
    // ".com>,", "@acme.co.il". Only the very start of a chunk's text can be that (see MaskContent).
    private static readonly Regex DomainTailAtStart = new(
        @"\A(?<lead>[\s<(]*)(?<tail>@?[A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*\.(?i:com|net|org|io|co|il|ac|gov|edu|biz|info|tech)(?:\.[A-Za-z]{2})?)(?![A-Za-z0-9.@-])",
        Options, MatchTimeout);

    // A digit group that ends where its digits end and does not run on into a time (12:30) or a
    // date (05/10).
    private const string PhoneGroup = @"\d{1,12}(?!\d|[:/]\d)";

    // "+972-54-1234567", "+972 (0) 54 1234567", "+1 (408) 555-1234", "00972 3 1234567". Matched
    // generously and then judged by its digit count, in MaskPhone. A digit before the plus stops
    // it ("2+1") — except before +972, where it is right-to-left text that glued two numbers
    // together ("03 123 4567+972 3 765 4321").
    private static readonly Regex PlusPhone = new(
        @"(?:(?<![A-Za-z0-9_+])(?:\+|(?<![\d.,-])00(?=[1-9]))|(?<=\d)\+(?=972))[1-9]\d{0,2}(?:[ .-]{0,2}\(\d{1,4}\))?[ .-]{0,2}" + PhoneGroup +
        @"(?:[ .-]{1,2}(?:\(\d{1,4}\)[ .-]?)?" + PhoneGroup + "){0,4}",
        Options, MatchTimeout);

    // The same number out of right-to-left text: "972-3-1234567+", "4567 123 54 972+".
    private static readonly Regex TrailingPlusPhone = new(
        @"(?<![A-Za-z0-9_+.,:/])\d{1,12}(?:[ .-]{1,2}\d{1,12}){0,4}\+(?![A-Za-z0-9_+])",
        Options, MatchTimeout);

    // "054-1234567", "03-1234567", "0541234567", "054 123 4567", "972-54-1234567", "972.3.766872".
    // The second digit has to be a real Israeli prefix, which is what keeps 01-01-2025 out. A comma
    // after it is a separator, not a decimal point ("0541234567,0" in a profile export).
    private static readonly Regex IsraeliPhone = new(
        @"(?<![A-Za-z0-9_+.,/]|\d-)(?:972|0)(?:[ .-]{0,2}\(0\))?[ .-]{0,2}(?:[23489]|5\d|7\d)[ .-]?\d{3}[ .-]?\d{3,4}(?!\d|[:/]\d|\.\d)",
        Options, MatchTimeout);

    // "408-555-1234", "(408) 555-1234", "1 408 555 1234". Separators required: a bare run of ten
    // digits is left to LongNumber.
    private static readonly Regex NorthAmericanPhone = new(
        @"(?<![A-Za-z0-9_+.,/]|\d-)(?:1[ .-])?(?:\(\d{3}\)|\d{3})[ .-]\d{3}[ .-]\d{4}(?!\d|[:/]\d|[.,]\d)",
        Options, MatchTimeout);

    // Letters around the digits do not save them — passports (FF1234567), visas, frequent-flyer and
    // booking numbers are written that way. Hex does (0x80131040), and so does the fraction of a
    // decimal with a short whole part (1.0000000, 3.7280000), and an amount with cents — but not
    // what only looks like one: a phone number (0541234567,0 — no amount starts with a 0) or a
    // field of a comma-separated line (312345678,1,Dana).
    private static readonly Regex LongNumber = new(
        @"(?<!\d|0[xX][0-9A-Fa-f]{0,16}|(?<!\d)\d{1,3}[.,])\d{7,}" +
        @"(?!\d|(?<!(?<!\d)0\d{8,9})(?:\.\d{1,2}(?!\d)|,\d{1,2}(?![\d,])))",
        Options, MatchTimeout);

    // "+30 01.03.2024 …" is a date after a plus sign, not a phone number.
    private static readonly Regex DateInside = new(
        @"(?<!\d)\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2}(?!\d)", Options, MatchTimeout);

    private static readonly Regex GlassixName = new(
        @"^Glassix_\d+_(?<company>[^_]+)_(?<subject>.*?)(?:\.pdf)?$", Options | RegexOptions.IgnoreCase, MatchTimeout);

    // Words Glassix appends to a company name that the transcript itself usually leaves off:
    // "Talma US" is "Talma" in the text, "Harmonic Inc" is "Harmonic".
    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "ltd", "llc", "corp", "group", "us", "usa", "israel", "medical", "digital", "travel", "tours", "integrated", "shipping",
    };

    // Glassix tags that are not a company: a role, a queue, TripEx itself, a field label. A file
    // named after one of them ("Glassix_…_Project Manager_…") names no company — its customer is in
    // the tag line beside it, and KnowledgeCompanies picks it up from there.
    private static readonly HashSet<string> NotACompany = new(StringComparer.OrdinalIgnoreCase)
    {
        "project manager", "tas admin", "tas", "tripex", "combtas", "comb tas", "support", "information",
        "additional", "additional information", "owner", "innovation", "address", "crm", "participants", "tags",
    };

    // A customer whose name is also something every transcript mentions: monday is TripEx's CRM
    // ("נפתחה קריאה בMonday") and a weekday. Masked in its own transcripts, not in the others.
    private static readonly HashSet<string> NotACompanyElsewhere = new(StringComparer.OrdinalIgnoreCase) { "monday" };

    // The tag line of a transcript, in either language, and where it ends.
    private static readonly Regex TagsLabel = new(@"(?:Tags|תיוגים)\s*:", Options, MatchTimeout);
    private static readonly Regex ParticipantsLabel = new(@"(?:Participants|משתתפים)\s*:", Options, MatchTimeout);

    // A tag that is a name: one to four Latin words, each starting with a capital letter ("Hooli
    // Tours", "NetCorp", "Abc4you", "IAI"). That leaves out subjects, dates, ticket and trip
    // numbers (TAS01270T) and sentences.
    private static readonly Regex TagName = new(
        @"^(?:[A-Z](?![A-Za-z0-9&'.-]*\d{3})[A-Za-z0-9&'.-]*)(?: [A-Z](?![A-Za-z0-9&'.-]*\d{3})[A-Za-z0-9&'.-]*){0,3}$",
        Options, MatchTimeout);

    /// <summary>
    /// The text with every email address, phone number, long number and — when given — the company
    /// masked. keepEmail is TripEx's configured support address, which stays readable along with any
    /// tripex.io address. others masks every other company the knowledge base knows of as well.
    /// Empty for null; <see cref="Withheld"/> if masking could not finish.
    /// </summary>
    public static string Mask(string? text, string? company = null, string? keepEmail = null, KnowledgeCompanies? others = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            text = Email.Replace(text, m => IsOwnAddress(m.Value, m.Groups["whole"].Success, keepEmail) ? m.Value : EmailMask);
            if (!string.IsNullOrWhiteSpace(company)) text = MaskCompany(text, company);
            if (others?.Pattern != null) text = others.Pattern.Replace(text, CompanyMask);
            text = PlusPhone.Replace(text, m => MaskPhone(m.Value, plusAtEnd: false));
            text = TrailingPlusPhone.Replace(text, m => MaskPhone(m.Value, plusAtEnd: true));
            text = IsraeliPhone.Replace(text, PhoneMask);
            text = NorthAmericanPhone.Replace(text, PhoneMask);
            return LongNumber.Replace(text, NumberMask);
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    /// <summary>
    /// A chunk's text: <see cref="Mask"/>, and before it the rest of an address the chunk before this
    /// one cut off, where the text starts with one. Only for the text of a chunk — a file name, subject
    /// or hint that starts with a domain ("Booking.com refunds") is a name, not half an address.
    /// </summary>
    public static string MaskContent(string? text, string? company = null, string? keepEmail = null, KnowledgeCompanies? others = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            text = DomainTailAtStart.Replace(text,
                m => IsOwnDomainTail(m.Groups["tail"].Value, keepEmail) ? m.Value : m.Groups["lead"].Value + EmailMask, 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
        return Mask(text, company, keepEmail, others);
    }

    // The end of one of TripEx's own domains ("x.io", "ex.io", "eu.tripex.io") or of Support:Contact's.
    private static bool IsOwnDomainTail(string tail, string? keepEmail)
    {
        var rest = tail.TrimStart('@', '.', '-');
        if ("tripex.io".EndsWith(rest, StringComparison.OrdinalIgnoreCase)
            || rest.EndsWith(".tripex.io", StringComparison.OrdinalIgnoreCase))
            return true;
        var keep = keepEmail?.Trim() ?? "";
        var at = keep.LastIndexOf('@');
        return at >= 0 && keep[(at + 1)..].EndsWith(rest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How a snippet's file is named in the prompt, and the company to mask in it. A Glassix file
    /// becomes "Support conversation example: &lt;subject&gt;" — no ticket number, no company — and
    /// gives its company (none when the file is named after a role tag such as "Project Manager");
    /// any other name is only masked, and gives none.
    /// </summary>
    public static (string Label, string? Company) DescribeSource(string? fileName, string? keepEmail = null, KnowledgeCompanies? others = null)
    {
        Match m;
        try { m = GlassixName.Match(fileName ?? ""); }
        catch (RegexMatchTimeoutException) { return (Withheld, null); }
        if (!m.Success) return (Mask(fileName, null, keepEmail, others), null);

        var token = m.Groups["company"].Value.Trim();
        var company = token.Length == 0 || NotACompany.Contains(token) ? null : token;
        var subject = Mask(m.Groups["subject"].Value.Trim(), company, keepEmail, others);
        return (subject.Length == 0 ? ConversationLabel : $"{ConversationLabel}: {subject}", company);
    }

    /// <summary>
    /// Every company the knowledge base's support conversations are about, read from the knowledge
    /// base itself: the company each Glassix file is named after, and the Latin names in the tag
    /// line of each transcript ("Tags: Acme, Globex Tours" / "תיוגים: Initech, Hooli").
    /// A transcript is often filed under its travel agency, or under a role tag, and names other
    /// customers besides — the file name alone misses them. Role and queue tags are left out.
    /// fileNames are the knowledge base's file names (only Glassix ones count); tagText is the text
    /// of the chunks that hold a tag line.
    /// </summary>
    public static KnowledgeCompanies Companies(IEnumerable<string?> fileNames, IEnumerable<string?> tagText)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate)
        {
            var name = string.Join(' ', candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            // Three letters at least: a two-letter tag ("US", "IT") would hit ordinary text in every snippet.
            if (name.Length >= 3 && !NotACompany.Contains(name) && !NotACompanyElsewhere.Contains(name)) names.Add(name);
        }

        foreach (var fileName in fileNames)
        {
            var m = GlassixName.Match(fileName ?? "");
            if (m.Success) Add(m.Groups["company"].Value);
        }

        foreach (var text in tagText)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match label in TagsLabel.Matches(text))
            {
                // The tags run to the participants list, and are in its first lines (further down is
                // an email the PDF pasted in); where the PDF lost the list, the next few lines.
                var rest = text[(label.Index + label.Length)..];
                var end = ParticipantsLabel.Match(rest);
                rest = string.Join('\n', (end.Success ? rest[..end.Index] : rest).Split('\n').Take(end.Success ? 7 : 4));
                if (rest.Length > 300) rest = rest[..300];

                // The PDF mixes the ticket's other fields in. A labelled value ("שם הפונה: <agent>"),
                // or the line after a label of its own ("Owner:" then the agent's name), is that
                // field's — the requester or the TripEx agent — not a tag.
                var afterLabel = false;
                foreach (var line in rest.Split('\n'))
                {
                    var skip = afterLabel;
                    afterLabel = line.TrimEnd().EndsWith(':');
                    if (skip) continue;
                    foreach (var piece in line.Split(','))
                    {
                        var value = piece.Trim();
                        if (!value.Contains(':') && TagName.IsMatch(value)) Add(value);
                    }
                }
            }
        }
        return new KnowledgeCompanies(names);
    }

    /// <summary>
    /// The names a company goes by in its own transcripts: the Glassix token; the token without the
    /// words in CompanySuffixes and the codes after it ("Wayne DU - BTC") when what is left is
    /// still a name (3+ characters); and without a code in front of it ("BD Stark") when a name of
    /// 5+ characters is left that is not only suffix words.
    /// </summary>
    public static IReadOnlyList<string> CompanyNames(string? company)
    {
        var names = new List<string>();
        var words = (company ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return names;
        names.Add(string.Join(' ', words));

        var core = words.Length;
        while (core > 1 && (CompanySuffixes.Contains(words[core - 1]) || IsCode(words[core - 1]))) core--;
        var shorter = string.Join(' ', words.Take(core));
        if (core < words.Length && shorter.Length >= 3) names.Add(shorter);

        var start = 0;
        while (start < core - 1 && IsCode(words[start])) start++;
        var rest = words[start..core];
        var withoutCode = string.Join(' ', rest);
        if (start > 0 && withoutCode.Length >= 5 && !rest.All(CompanySuffixes.Contains)) names.Add(withoutCode);
        return names;
    }

    // "DU", "BTC", "-": a code or a dash beside the name, not a word of it.
    private static bool IsCode(string word) => word.Length <= 3 && !word.Any(char.IsLower);

    private static string MaskCompany(string text, string company)
    {
        foreach (var name in CompanyNames(company))
        {
            if (name.Length < 2) continue;
            var pattern = NameOrDomain(NamePattern(name), Words(name));
            text = Regex.Replace(text, pattern, CompanyMask, RegexOptions.CultureInvariant, MatchTimeout);
        }
        return text;
    }

    // Whole words only; between the words of a name any spacing, a hyphen or nothing at all
    // ("Bar Ilan", "Bar-Ilan", "BarIlan"). Whole LATIN words: a Hebrew prefix written onto the name
    // ("לElbit") does not protect it. A short name is an acronym and is matched as written (IAI, JDC)
    // so it cannot hit an ordinary word ("it", "us"); a longer one in any case.
    private static string NamePattern(string name) => (name.Length <= 3 ? "(?-i:" : "(?i:") + Words(name) + ")";

    // An acronym may be written with dots ("B.D." for BD).
    private static string Words(string name) =>
        string.Join(@"[\s._-]*", name.Split(' ').Select(w =>
            IsCode(w) && w.All(char.IsLetter) ? string.Join(@"\.?", w.Select(c => c.ToString())) : Regex.Escape(w)));

    // The name as a word of its own, or a web domain that starts with it, in any case: the company's
    // site and its other domains ("www.acmesystems.com", "acmetravel.com") name it as plainly.
    private static string NameOrDomain(string names, string domains) =>
        "(?<![A-Za-z0-9])(?:" + names + "(?![A-Za-z0-9])|(?i:" + domains + @")[A-Za-z0-9-]*(?=\.[A-Za-z]{2,}))";

    // Every other company the knowledge base knows of, in one pattern, longest first so "Talma US"
    // is masked whole before "Talma" is. Outside its own transcripts a name is only masked written
    // as a name, with a capital ("Beacon", "BEACON", "Bar-ilan") or as a domain: a name can be an
    // everyday word as well ("a weak beacon").
    internal static Regex? CompanyPattern(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        var longestFirst = names.OrderByDescending(n => n.Length).ToList();
        return new Regex(NameOrDomain("(?:" + string.Join("|", longestFirst.Select(CapitalizedNamePattern)) + ")",
                                      string.Join("|", longestFirst.Select(Words))),
                         Options, MatchTimeout);
    }

    private static string CapitalizedNamePattern(string name) =>
        name.Length <= 3 || !char.IsLetter(name[0])
            ? NamePattern(name)
            : $"(?=(?-i:[{char.ToUpperInvariant(name[0])}{name[0]}]))(?i:{Words(name)})";

    private static string MaskPhone(string match, bool plusAtEnd)
    {
        var digits = match.Count(char.IsDigit);
        if (digits < MinPhoneDigits || DateInside.IsMatch(match)) return match;
        if (digits <= MaxPhoneDigits) return PhoneMask;

        // More digits than any phone number: a phone with other numbers run on after it (or, reversed,
        // before it). Mask the 8–15 digits nearest the plus sign and leave the rest to the rules that
        // follow — LongNumber still catches a long run among them.
        var body = plusAtEnd ? match[..^1] : match;
        var count = 0;
        var cut = -1;
        if (!plusAtEnd)
        {
            for (var i = 0; i < body.Length; i++)
            {
                if (!char.IsDigit(body[i])) continue;
                count++;
                var groupEnds = i + 1 == body.Length || !char.IsDigit(body[i + 1]);
                if (groupEnds && count <= MaxPhoneDigits && count >= MinPhoneDigits) cut = i + 1;
            }
            return cut < 0 ? match : PhoneMask + body[cut..];
        }
        for (var i = body.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(body[i])) continue;
            count++;
            var groupStarts = i == 0 || !char.IsDigit(body[i - 1]);
            if (groupStarts && count <= MaxPhoneDigits && count >= MinPhoneDigits) cut = i;
        }
        return cut < 0 ? match : body[..cut] + PhoneMask;
    }

    private static bool IsOwnAddress(string address, bool whole, string? keepEmail)
    {
        if (whole)
        {
            if (!string.IsNullOrWhiteSpace(keepEmail)
                && string.Equals(address, keepEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            var domain = address[(address.LastIndexOf('@') + 1)..];
            return domain.Equals("tripex.io", StringComparison.OrdinalIgnoreCase)
                   || domain.EndsWith(".tripex.io", StringComparison.OrdinalIgnoreCase);
        }

        // What is left of an address stays only when it can be nothing but one of TripEx's own: the
        // start of the configured support address, or a tripex domain cut short ("noa@tripex.").
        var rest = string.Concat(address.Where(c => !char.IsWhiteSpace(c))).TrimEnd('.', '-');
        if (!string.IsNullOrWhiteSpace(keepEmail)
            && keepEmail.Trim().StartsWith(rest, StringComparison.OrdinalIgnoreCase))
            return true;
        return OwnDomainCutShort.IsMatch(rest[(rest.IndexOf('@') + 1)..]);
    }

    private static readonly Regex OwnDomainCutShort = new(
        @"^(?:[A-Za-z0-9-]+\.)*tripex(?:\.io?)?$", Options | RegexOptions.IgnoreCase, MatchTimeout);
}

/// <summary>
/// The companies <see cref="KnowledgePrivacy.Companies"/> found in the knowledge base — each with its
/// shorter form ("Talma US", "Talma") — ready to be masked in any snippet.
/// </summary>
public sealed class KnowledgeCompanies
{
    public IReadOnlyList<string> Names { get; }
    internal Regex? Pattern { get; }

    internal KnowledgeCompanies(IEnumerable<string> names)
    {
        Names = names.SelectMany(KnowledgePrivacy.CompanyNames)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                     .ToList();
        Pattern = KnowledgePrivacy.CompanyPattern(Names);
    }
}
