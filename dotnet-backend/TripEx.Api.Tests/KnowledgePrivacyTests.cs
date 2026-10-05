using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Knowledge snippets are masked before they reach the prompt (Roi, 2026-10-05): most of the
/// knowledge base is other customers' support conversations, and every customer's prompt gets up to
/// five of them. Pinned here: what goes (addresses, phone numbers, long ids, the company a
/// transcript is named after) and — just as much — what must stay readable for the snippet to be
/// any use: dates, times, amounts, exchange rates, page paths, error codes, trip numbers.
///
/// The samples copy the shapes found in seed-knowledge.sql, including the right-to-left PDF
/// residue, with invented names, domains and numbers.
/// </summary>
public class KnowledgePrivacyTests
{
    private const string Support = "support@combtas.com";

    private static string Mask(string text, string? company = null) => KnowledgePrivacy.Mask(text, company, Support);

    // ── Email addresses ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("racheli.k@customer-a.example.co.il")]
    [InlineData("Dana.Levi+travel@customer-b.example.com")]
    [InlineData("noa_123@mail.example.org")]
    [InlineData("x@tripex.io.example.com")] // only looks like ours
    public void A_customer_address_is_masked(string address)
    {
        Assert.Equal("Mail: [email].", Mask($"Mail: {address}."));
        Assert.Equal("<[email]>", Mask($"<{address}>"));
    }

    [Theory]
    [InlineData("support@combtas.com")]
    [InlineData("SUPPORT@COMBTAS.COM")]
    [InlineData("milo@tripex.io")]
    [InlineData("noa@eu.tripex.io")]
    public void TripExs_own_addresses_stay(string address)
    {
        Assert.Equal($"Write to {address} for help", Mask($"Write to {address} for help"));
    }

    [Fact]
    public void Without_a_configured_support_address_only_tripex_io_stays()
    {
        Assert.Equal("[email] / milo@tripex.io", KnowledgePrivacy.Mask("support@combtas.com / milo@tripex.io"));
    }

    [Theory]
    [InlineData("To: Dana Levi <dana.levi@customer-a.", "To: Dana Levi <[email]")]         // the chunk ends on the dot
    [InlineData("Mail: noa@customer-b", "Mail: [email]")]                                  // ...or before it
    [InlineData("<dana.levi@customer-\nexample.co.il>", "<[email]>")]                      // broken at a hyphen
    [InlineData("<dana.levi@customer-a\n.example.co.il>", "<[email]>")]                    // broken before a dot
    [InlineData("<dana.levi@customer-\nDec-2024 (Thu)", "<[email]\nDec-2024 (Thu)")]       // the next line is not the domain
    [InlineData("נשלח מ example.co.il@dana.l", "נשלח מ [email]")]                           // turned round by RTL text
    public void What_is_left_of_a_customer_address_is_masked(string text, string expected)
    {
        Assert.Equal(expected, Mask(text));
    }

    [Theory]
    [InlineData("Reply to <support@combtas.")]      // the start of Support:Contact
    [InlineData("From: Noa <noa@tripex.")]          // a tripex.io address cut short
    [InlineData("Write to support@combtas.com.\nThanks")]
    [InlineData("@Dana Levi please check")]          // a Glassix mention
    [InlineData("seen\n+@Dana Levi")]
    public void What_is_left_of_TripExs_own_address_or_a_mention_stays(string text)
    {
        Assert.Equal(text, Mask(text));
    }

    // The other half of an address cut where one chunk ends: the next chunk starts with its domain.
    [Theory]
    [InlineData("globexgroup.example.com\nנושא: FW: Hotel", "[email]\nנושא: FW: Hotel")]
    [InlineData("-tech.example.co.il>, Dana Levi <x", "[email]>, Dana Levi <x")]
    [InlineData(".co.il> Dana Levi: wrote", "[email]> Dana Levi: wrote")]
    [InlineData("  @globex.example.com :אל", "  [email] :אל")]
    [InlineData("lex.ac.il\nעותק", "[email]\nעותק")]
    [InlineData("edin.com/company/12/admin/", "[email]/company/12/admin/")]
    [InlineData("dana@globex.example.com wrote", "[email] wrote")] // a whole address, as anywhere
    public void The_rest_of_an_address_at_the_start_of_a_chunk_is_masked(string text, string expected)
    {
        Assert.Equal(expected, KnowledgePrivacy.MaskContent(text, null, Support));
    }

    [Theory]
    [InlineData("x.io\nSent: Sunday")]                 // the end of a tripex.io address
    [InlineData("eu.tripex.io/help")]
    [InlineData("tas.com< :אל")]                       // ...or of Support:Contact
    [InlineData("e.g. the report")]
    [InlineData("web.config and tasks.aspx?tab=2")]
    [InlineData("System.Web.UI.Control.LoadRecursive() at")]
    [InlineData("Book it on globex.com today")]        // a domain anywhere but the very start is text
    public void A_chunk_that_starts_with_anything_else_keeps_it(string text)
    {
        Assert.Equal(text, KnowledgePrivacy.MaskContent(text, null, Support));
    }

    [Fact]
    public void Only_the_text_of_a_chunk_is_read_as_starting_mid_address()
    {
        Assert.Equal("[Booking.com refunds.pdf]: [email]>, Dana Levi",
            ChatService.FormatKnowledgeChunk("Booking.com refunds.pdf", "globex.com>, Dana Levi", null, null, null, Support));
    }

    // ── Phone numbers ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("054-1234567")]
    [InlineData("0541234567")]
    [InlineData("054 123 4567")]
    [InlineData("03-1234567")]
    [InlineData("077-1234567")]
    [InlineData("972-54-1234567")]
    [InlineData("972.3.766872")]
    [InlineData("+972-54-1234567")]
    [InlineData("+972 54 1234567")]
    [InlineData("+972 (0) 3 6543210")]
    [InlineData("+972.3.7668721")]
    [InlineData("+9721234567890")]
    [InlineData("00972 3 1234567")]
    [InlineData("+1 (408) 555-0199")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("(408) 555-0199")]
    [InlineData("408-555-0199")]
    [InlineData("972-3-1234567+")]   // right-to-left residue: the plus ends up at the end
    [InlineData("4567 123 54 972+")] // ...and the groups come out reversed
    public void A_phone_number_is_masked(string phone)
    {
        Assert.Equal("Tel: [phone] | Fax", Mask($"Tel: {phone} | Fax"));
    }

    [Fact]
    public void A_phone_number_in_hebrew_text_is_masked()
    {
        Assert.Equal("אפשר לפנות אליי ב-[phone] או בנייד [phone], תודה",
            Mask("אפשר לפנות אליי ב-054-1234567 או בנייד 052 765 4321, תודה"));
    }

    [Fact]
    public void A_phone_number_followed_by_a_time_keeps_the_time()
    {
        Assert.Equal("Call [phone] at 12:30", Mask("Call +972-54-1234567 at 12:30"));
    }

    [Fact]
    public void A_phone_number_run_on_into_other_numbers_is_masked_and_the_rest_is_left()
    {
        // 18 digits in one run of groups: the phone is the first 15 at most, from the plus sign.
        Assert.Equal("[phone] 2024 ext", Mask("+972 54 123 4567 2024 ext"));
    }

    [Theory]
    [InlineData("Mobile: 0541234567,0 Hotel Preference", "Mobile: [phone],0 Hotel Preference")] // a profile export
    [InlineData("Mobile:\n0541234567,0\nHotel", "Mobile:\n[phone],0\nHotel")]
    [InlineData("Mobile: 054-1234567,0", "Mobile: [phone],0")]
    [InlineData("Mobile: 0541234567.5", "Mobile: [number].5")]
    [InlineData("Address: X,0541234567,0 Hotel", "Address: X,[number],0 Hotel")]
    [InlineData("ID 312345678,1,Dana", "ID [number],1,Dana")]                                 // a comma-separated line
    public void A_phone_number_or_id_before_what_looks_like_a_fraction_is_masked(string text, string expected)
    {
        Assert.Equal(expected, Mask(text));
    }

    [Theory]
    [InlineData("Tel 03 123 4567+972 3 765 4321", "Tel [phone][phone]")]
    [InlineData("123 | +972 54 123+972 3 765 4\n4321", "123 | [phone][phone]\n4321")] // the seed's signature shape
    public void A_phone_number_glued_after_another_by_rtl_text_is_masked(string text, string expected)
    {
        Assert.Equal(expected, Mask(text));
    }

    [Theory]
    [InlineData("Total 1250000.00, approved")]
    [InlineData("Total 1250000,00 EUR")]
    [InlineData("Book 2+1 tickets, rate 1.0000000,5")]
    public void An_amount_with_cents_still_stays(string text)
    {
        Assert.Equal(text, Mask(text));
    }

    // ── Long numbers ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("מספר הקריאה הוא 8113603595, אעדכן בהקדם", "מספר הקריאה הוא [number], אעדכן בהקדם")]
    [InlineData("Ticket #: 125354211 Opened on: 31-12-2024 11:13", "Ticket #: [number] Opened on: 31-12-2024 11:13")]
    [InlineData("Interface_Batch_No 100000023662297 TAS01266T", "Interface_Batch_No [number] TAS01266T")]
    [InlineData("Passport Number: FF1234567 Expiry Date: 12-Jan-2030", "Passport Number: FF[number] Expiry Date: 12-Jan-2030")]
    [InlineData("TKT/Reservation1234567890", "TKT/Reservation[number]")]
    [InlineData("employee 1234567 approved", "employee [number] approved")]
    public void A_long_id_is_masked(string text, string expected)
    {
        Assert.Equal(expected, Mask(text));
    }

    // ── What must stay ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("נסיעה מתאריך 05/10/2026 עד 12/10/2026")]
    [InlineData("Opened 31-12-2024 11:13, closed 01-01-2025 09:41")]
    [InlineData("החופשה 22.12 עד 31.12, דוח מ-24.11.2024")]
    [InlineData("Created 2024-12-23T10:00:00Z")]
    [InlineData("Tuesday 24 December 2024 13:37")]
    [InlineData("הסכום 1,250.00 ₪ אושר, 12,500 בסך הכל")]
    [InlineData("Total 1,234,567.89 USD and 1250000.00 ILS")]
    [InlineData("239.00 ILS 1.0000000 239.00 ILS")]
    [InlineData("34.30 EUR 4.0874000 140.20")]
    [InlineData("Exchange rate 3.7280000")]
    [InlineData("Error 0x80131040 while loading")]
    [InlineData("Error 500, ORA-00942, HTTP 404")]
    [InlineData("Open /QA_3_70/Main_Pagesv2/tasks.aspx?tab=2")]
    [InlineData("https://deveu.combtas.com/QA_3_70/Main_Pagesv2/tasks.aspx")]
    [InlineData("Trip TAS01266T, report 4588, נסיעה 394, Tas44991")]
    [InlineData("Call *6050 or press 1")]
    [InlineData("valid +30 01.03.2024 onwards")]
    public void Dates_times_amounts_paths_codes_and_trip_numbers_stay(string text)
    {
        Assert.Equal(text, Mask(text));
        Assert.Equal(text, KnowledgePrivacy.Mask(text, null, Support, Kb()));
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Equal("", KnowledgePrivacy.Mask(null));
        Assert.Equal("", KnowledgePrivacy.Mask(""));
    }

    // ── The company and the file name ────────────────────────────────────────────────────────

    [Fact]
    public void A_glassix_file_is_named_without_its_ticket_and_company()
    {
        var (label, company) = KnowledgePrivacy.DescribeSource("Glassix_118279797_Kornit Digital_RE Proposal request for Kornit Asia.pdf");

        Assert.Equal("Support conversation example: RE Proposal request for [company] Asia", label);
        Assert.Equal("Kornit Digital", company);
    }

    [Fact]
    public void A_glassix_subject_is_masked_like_the_text()
    {
        var (label, _) = KnowledgePrivacy.DescribeSource("Glassix_100570703_Acme_מספר קריאה 7916252249 מאת dana@acme.example.com.pdf");

        Assert.Equal("Support conversation example: מספר קריאה [number] מאת [email]", label);
    }

    [Fact]
    public void Any_other_file_keeps_its_name_and_names_no_company()
    {
        Assert.Equal(("TAS_User_Guide_Updated (1).pdf", (string?)null), KnowledgePrivacy.DescribeSource("TAS_User_Guide_Updated (1).pdf"));
        Assert.Equal(("בוט שאלות נפוצות.docx", (string?)null), KnowledgePrivacy.DescribeSource("בוט שאלות נפוצות.docx"));
    }

    [Fact]
    public void The_company_is_masked_however_the_transcript_writes_it()
    {
        Assert.Equal("[company] asked, [company] approved, [company]'s agent, [company] too",
            Mask("Bar Ilan asked, bar-ilan approved, BarIlan's agent, BAR  ILAN too", "Bar Ilan"));
    }

    [Fact]
    public void A_company_is_also_masked_without_the_words_glassix_adds()
    {
        Assert.Equal(new[] { "Talma US", "Talma" }, KnowledgePrivacy.CompanyNames("Talma US"));
        Assert.Equal(new[] { "ZIM Integrated Shipping", "ZIM" }, KnowledgePrivacy.CompanyNames("ZIM Integrated Shipping"));
        Assert.Equal(new[] { "LR Group" }, KnowledgePrivacy.CompanyNames("LR Group")); // "LR" alone is too short to be a name
        Assert.Equal("[company] booked it for the [company] team", Mask("Talma US booked it for the Talma team", "Talma US"));
    }

    [Fact]
    public void A_company_is_masked_only_as_a_whole_word_and_an_acronym_only_as_written()
    {
        Assert.Equal("[company] admin, Chiaia, iai", Mask("IAI admin, Chiaia, iai", "IAI"));
        Assert.Equal("[company] and Ormatic", Mask("ormat and Ormatic", "Ormat"));
        // A Hebrew prefix on the name is not a word of its own.
        Assert.Equal("פנייה ל[company] ומ-[company]", Mask("פנייה לElbit ומ-Elbit", "Elbit"));
    }

    [Fact]
    public void A_company_is_also_masked_without_the_codes_glassix_adds_and_in_its_domains()
    {
        Assert.Equal(new[] { "Wayne DU - BTC", "Wayne" }, KnowledgePrivacy.CompanyNames("Wayne DU - BTC"));
        Assert.Equal(new[] { "BD Stark", "Stark" }, KnowledgePrivacy.CompanyNames("BD Stark"));
        Assert.Equal(new[] { "Hooli Tours", "Hooli" }, KnowledgePrivacy.CompanyNames("Hooli Tours"));
        Assert.Equal(new[] { "JDC Israel", "JDC" }, KnowledgePrivacy.CompanyNames("JDC Israel")); // never "Israel" alone

        Assert.Equal("[company] approved, [company] too", Mask("B.D. Stark approved, Stark too", "BD Stark"));
        Assert.Equal("see www.[company].com or [company].co.il", Mask("see www.vandelaytravel.com or VANDELAY.co.il", "Vandelay US"));
    }

    [Fact]
    public void A_glassix_file_named_after_a_role_tag_names_no_company()
    {
        var (label, company) = KnowledgePrivacy.DescribeSource("Glassix_101_Project Manager_Proposal mails.pdf");

        Assert.Equal("Support conversation example: Proposal mails", label);
        Assert.Null(company);
        // "Project Manager" stays readable; the customer beside it in the tag line is masked.
        Assert.Equal("Project Manager, [company]", KnowledgePrivacy.Mask("Project Manager, Globex", company, Support, Kb()));
    }

    // ── Every company the knowledge base knows of ────────────────────────────────────────────

    // Shaped like the seed's transcripts: the file name, then a tag line among the ticket's other
    // fields, in either language. Invented names.
    private static readonly string[] KbFiles =
    {
        "Glassix_101_Project Manager_Proposal mails.pdf",
        "Glassix_102_Vandelay US_Invoice upload.pdf",
        "Glassix_103_Wayne DU - BTC_Hotel.pdf",
        "Glassix_104_BD Stark_Approvers.pdf",
        "Glassix_105_Monday_Sandbox.pdf",
        "TAS_User_Guide_Updated (1).pdf",
    };

    private static readonly string[] KbTagText =
    {
        "שם הפונה:\nקריאה במערכת monday\nתיוגים:\nProject Manager, Globex\nמשתתפים:\ndana@globex.example.com Dana Levi",
        "Tags:\nHooli Tours, Initech\nClosed on:\n01-01-2025 09:05\nOwner:\nNoa Agent\nParticipants:",
        "תיוגים: Acme\nנושא הפניה: Tas Admin\nשם הפונה: Tamir\nמשתתפים:",
        "Tags:\nTAS01270T, RE: Proposal request for Umbrella, has been approved\nParticipants:",
        "Tags: US\nParticipants:",
    };

    private static KnowledgeCompanies Kb() => KnowledgePrivacy.Companies(KbFiles, KbTagText);

    [Fact]
    public void The_companies_are_read_from_the_glassix_file_names_and_the_tag_lines()
    {
        Assert.Equal(
            new[] { "Acme", "BD Stark", "Globex", "Hooli", "Hooli Tours", "Initech", "Stark", "Vandelay", "Vandelay US", "Wayne", "Wayne DU - BTC" },
            Kb().Names);
        // Not a role or queue tag, not the requester or the agent, not a trip number, subject or
        // sentence, not a two-letter tag — and not monday, TripEx's CRM, outside its own transcripts.
    }

    [Fact]
    public void Every_company_the_knowledge_base_knows_of_is_masked_in_every_snippet()
    {
        var formatted = ChatService.FormatKnowledgeChunk("Glassix_101_Project Manager_Proposal mails.pdf",
            "Project Manager, Globex\nGlobex asked Hooli Tours to resend the proposal; INITECH was copied. See www.hoolitours.com",
            null, null, null, Support, Kb());

        Assert.Equal(
            "[Support conversation example: Proposal mails]: Project Manager, [company]\n" +
            "[company] asked [company] to resend the proposal; [company] was copied. See www.[company].com",
            formatted);

        // A guide is masked too: a customer's own user guide names it.
        Assert.Equal("[TAS guide.pdf]: [company] employees ask [company]",
            ChatService.FormatKnowledgeChunk("TAS guide.pdf", "Vandelay employees ask Acme", null, null, null, Support, Kb()));
    }

    [Fact]
    public void Outside_its_own_transcripts_a_company_is_masked_only_written_as_a_name()
    {
        var kb = KnowledgePrivacy.Companies(new[] { "Glassix_1_Beacon_x.pdf", "Glassix_2_Monday_y.pdf" }, Array.Empty<string>());

        Assert.Equal("weak beacon: ask [company] or [company], see [company].com",
            KnowledgePrivacy.Mask("weak beacon: ask Beacon or BEACON, see beacontours.com", null, Support, kb));
        // monday is the CRM every transcript mentions, and a weekday.
        Assert.Equal("נפתחה קריאה בMonday ביום Monday", KnowledgePrivacy.Mask("נפתחה קריאה בMonday ביום Monday", "Acme", Support, kb));
        // ...but in its own transcripts it is the company.
        Assert.Equal("נפתחה קריאה ב[company]", KnowledgePrivacy.Mask("נפתחה קריאה בMonday", "Monday", Support, kb));
    }

    // ── What the prompt gets ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_snippet_reaches_the_prompt_masked_in_every_part()
    {
        var formatted = ChatService.FormatKnowledgeChunk(
            "Glassix_125354211_Kornit Digital_Approver missing for 1234567.pdf",
            "From: Dana Levi <dana.levi@kornit.example.com> Mobile: +972-54-765-4321. Kornit asked on 05/10/2026 " +
            "about TAS01266T (1,250.00 ILS). Reply to support@combtas.com.",
            "expenses", "transcript", "Kornit approver case, call 054-1234567", Support);

        Assert.Equal(
            "[Support conversation example: Approver missing for [number] | domain: expenses | type: transcript] " +
            "(hint: [company] approver case, call [phone]): " +
            "From: Dana Levi <[email]> Mobile: [phone]. [company] asked on 05/10/2026 " +
            "about TAS01266T (1,250.00 ILS). Reply to support@combtas.com.",
            formatted);
        Assert.DoesNotContain("125354211", formatted);
        Assert.DoesNotContain("Kornit", formatted);
    }

    // ── The real knowledge base ──────────────────────────────────────────────────────────────

    // Every chunk of Data/seed-knowledge.sql (copied next to the tests), with its file name.
    private static List<(string File, string Content)> SeedChunks()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "seed-knowledge.sql"));
        var names = System.Text.RegularExpressions.Regex.Matches(sql,
                @"INSERT INTO \[dbo\]\.\[knowledge_documents\][^\n]*?VALUES \('([0-9a-f-]+)', N'((?:[^']|'')*)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Replace("''", "'"));
        return System.Text.RegularExpressions.Regex.Matches(sql,
                @"VALUES \(NEWID\(\), '([0-9a-f-]+)', N'((?:[^']|'')*)', \d+\);")
            .Select(m => (File: names.GetValueOrDefault(m.Groups[1].Value, ""), Content: m.Groups[2].Value.Replace("''", "'")))
            .ToList();
    }

    // What ChatService reads for KnowledgePrivacy.Companies: the Glassix file names, and the text of
    // their chunks that hold a tag line.
    private static KnowledgeCompanies SeedCompanies(List<(string File, string Content)> chunks)
    {
        var glassix = chunks.Where(c => c.File.StartsWith("Glassix_", StringComparison.OrdinalIgnoreCase)).ToList();
        return KnowledgePrivacy.Companies(glassix.Select(c => c.File).Distinct(),
            glassix.Where(c => c.Content.Contains("Tags", StringComparison.OrdinalIgnoreCase) || c.Content.Contains("תיוגים"))
                   .Select(c => c.Content));
    }

    [Fact]
    public void No_customer_address_phone_or_glassix_ticket_survives_in_any_seed_snippet()
    {
        // Every seed chunk formatted exactly as the prompt gets it. Counts only in the failure
        // message: the point is not to print what leaked.
        var chunks = SeedChunks();
        var companies = SeedCompanies(chunks);
        // An address or what is left of one: a letter or digit, an @, and the start of a domain —
        // whole, cut off where the chunk ends, broken over two lines or turned round.
        var address = new System.Text.RegularExpressions.Regex(
            @"(?<![A-Za-z0-9._%+-])[._%+-]*[A-Za-z0-9][A-Za-z0-9._%+-]*@[A-Za-z0-9\[][A-Za-z0-9.\[\]-]*");
        var israeliPhone = new System.Text.RegularExpressions.Regex(@"(?<![\d.])0(?:[23489]|5\d|7\d)[ -]?\d{3}[ -]?\d{3,4}(?!\d)");
        var plus972 = new System.Text.RegularExpressions.Regex(@"972(?:[ .()\r\n-]{0,2}\d){8,9}(?!\d)");
        var time = new System.Text.RegularExpressions.Regex(@"(?<!\d)\d{1,2}:\d{2}(?!\d)");
        var trip = new System.Text.RegularExpressions.Regex(@"TAS\d+T");
        // ...and the rest of one the chunk before cut off, at the start of the text: a domain.
        var domainTail = new System.Text.RegularExpressions.Regex(
            @"\A[\s<(]*@?([A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*\.(?i:com|net|org|io|co|il|ac)(?:\.[A-Za-z]{2})?)(?![A-Za-z0-9.@-])");

        int leaked = 0, phones = 0, glassix = 0, timesLost = 0, tripsLost = 0;
        foreach (var (file, content) in chunks)
        {
            var formatted = ChatService.FormatKnowledgeChunk(file, content, null, null, null, Support, companies);
            leaked += address.Matches(formatted).Count(m =>
            {
                var left = m.Value.TrimEnd('.', '-');
                return !Support.StartsWith(left, StringComparison.OrdinalIgnoreCase)
                       && !left[(left.IndexOf('@') + 1)..].StartsWith("tripex", StringComparison.OrdinalIgnoreCase);
            });
            // The text after "[<label>]: " — no domain, type or hint here.
            var text = formatted[(KnowledgePrivacy.DescribeSource(file, Support, companies).Label.Length + 4)..];
            var tail = domainTail.Match(text);
            if (tail.Success)
            {
                var rest = tail.Groups[1].Value.TrimStart('.', '-');
                if (!"tripex.io".EndsWith(rest, StringComparison.OrdinalIgnoreCase)
                    && !"combtas.com".EndsWith(rest, StringComparison.OrdinalIgnoreCase))
                    leaked++;
            }
            phones += israeliPhone.Matches(formatted).Count + plus972.Matches(formatted).Count;
            if (formatted.Contains("Glassix_")) glassix++;
            timesLost += Math.Max(0, time.Matches(content).Count - time.Matches(formatted).Count);
            tripsLost += Math.Max(0, trip.Matches(content).Count - trip.Matches(formatted).Count);
        }

        Assert.True(chunks.Count > 1000, $"only {chunks.Count} chunks read — has the seed format changed?");
        Assert.True(chunks.Count(c => c.File.StartsWith("Glassix_")) > 1000);
        Assert.Equal(0, leaked);
        Assert.Equal(0, phones);
        Assert.Equal(0, glassix);
        Assert.Equal(0, timesLost);
        Assert.Equal(0, tripsLost);
    }

    [Fact]
    public void No_company_the_seed_names_survives_written_as_a_name_in_any_seed_snippet()
    {
        // A transcript is often filed under its travel agency or under "Project Manager", and names
        // other customers besides: every company the knowledge base knows of is masked in every
        // snippet, not only the one the file is named after.
        var chunks = SeedChunks();
        var companies = SeedCompanies(chunks);
        var asWritten = companies.Names
            .SelectMany(n => new[] { n, n.ToUpperInvariant() }).Distinct()
            .Select(n => new System.Text.RegularExpressions.Regex(
                @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(n) + "(?![A-Za-z0-9])"))
            .ToList();

        int survived = 0, snippets = 0;
        foreach (var (file, content) in chunks)
        {
            var formatted = ChatService.FormatKnowledgeChunk(file, content, null, null, null, Support, companies);
            var here = asWritten.Sum(r => r.Matches(formatted).Count);
            survived += here;
            if (here > 0) snippets++;
        }

        Assert.True(companies.Names.Count > 50, $"only {companies.Names.Count} companies read — has the seed format changed?");
        Assert.DoesNotContain("Project Manager", companies.Names);
        Assert.Equal((0, 0), (survived, snippets));
    }
}
