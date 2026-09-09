using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The second clarifying round is the only guiding question Milo words itself, and it used to be
/// the only one without buttons: its answer choices were prose inside the question, so the user
/// had to read them and type one back while every other guiding question offered a click
/// (reported 2026-09-09, "I want this for all the guiding questions with the buttons").
///
/// The prompt now asks for those choices in a separate "options" array. The model is not reliable
/// about a contract like that, so these tests cover what happens when it complies, when it
/// half-complies, and when it ignores the contract completely — in every case the user must end
/// up with an answerable question and each choice shown exactly once.
/// </summary>
public class ModelAuthoredClarifyOptionsTests
{
    // ── Parsing the model's reply ────────────────────────────────────────────────────────────

    [Fact]
    public void Options_are_read_from_the_reply()
    {
        var (intent, text, page, options) = ChatService.ParseAiResponse(
            """
            {"intent":"clarify","text":"Which budget report do you mean?",
             "options":["Budget by Division","Budget by Cost Center"]}
            """);

        Assert.Equal("clarify", intent);
        Assert.Equal("Which budget report do you mean?", text);
        Assert.Equal("", page);
        Assert.Equal(new[] { "Budget by Division", "Budget by Cost Center" }, options);
    }

    [Fact]
    public void A_reply_without_options_parses_to_an_empty_list()
    {
        // Every non-clarify reply, which is nearly all of them. Must never be null.
        var (_, _, _, options) = ChatService.ParseAiResponse(
            """{"intent":"help","text":"Open Analysis Reports.","page":"TASR_08004_BudgetByDivision"}""");

        Assert.Empty(options);
    }

    [Fact]
    public void Non_string_entries_are_skipped_rather_than_failing_the_parse()
    {
        // "text" is what actually reaches the user, so a malformed options array must not cost
        // us the whole reply.
        var (_, text, _, options) = ChatService.ParseAiResponse(
            """{"intent":"clarify","text":"Which one?","options":["Draft",5,null,"Issued"]}""");

        Assert.Equal("Which one?", text);
        Assert.Equal(new[] { "Draft", "Issued" }, options);
    }

    [Fact]
    public void Options_survive_the_regex_fallback_for_a_broken_reply()
    {
        // Unterminated JSON — the object parse throws and the regex path takes over. It already
        // recovered intent/text/page; it now has to recover the options too, or a truncated
        // reply silently loses its buttons.
        var (intent, text, _, options) = ChatService.ParseAiResponse(
            """{"intent":"clarify","text":"Which one?","options":["The 2024 report","The 2025 report"],""");

        Assert.Equal("clarify", intent);
        Assert.Equal("Which one?", text);
        Assert.Equal(new[] { "The 2024 report", "The 2025 report" }, options);
    }

    [Fact]
    public void Hebrew_options_come_back_readable()
    {
        // OCI returns Hebrew as \u-escapes; DecodeUnicodeEscapes has to run on the options too,
        // or the buttons read as literal escape sequences.
        var (_, _, _, options) = ChatService.ParseAiResponse(
            "{\"intent\":\"clarify\",\"text\":\"?\",\"options\":[\"\\u05d3\\u05d5\\u05d7 \\u05d0\",\"\\u05d3\\u05d5\\u05d7 \\u05d1\"]}");

        Assert.Equal(new[] { "דוח א", "דוח ב" }, options);
    }

    // ── Cleaning the labels ──────────────────────────────────────────────────────────────────
    //
    // CleanModelOptions only cleans, per item. It deliberately rejects nothing for being long or
    // numerous: whether the set can be BUTTONS is OptionsCanBeButtons' question, and the answer
    // "no" means "show them as a numbered list", never "throw the choices away" — the prompt
    // told the model to keep them out of "text", so discarding them here would leave the user a
    // question with no answers anywhere.

    [Fact]
    public void Two_clean_choices_come_through_unchanged()
    {
        var options = ChatService.CleanModelOptions(new[] { "The 2024 version", "The updated 2025 version" });
        Assert.Equal(new[] { "The 2024 version", "The updated 2025 version" }, options);
        Assert.True(ChatService.OptionsCanBeButtons(options));
    }

    [Theory]
    [InlineData("1. Draft")]
    [InlineData("2) Draft")]
    [InlineData("- Draft")]
    [InlineData("• Draft")]
    [InlineData("  Draft  ")]
    public void Numbering_and_bullets_are_stripped_from_a_label(string written)
    {
        // Numbering is presentation the code owns. Left in, it would be sent back as part of the
        // user's next message ("1. Draft"), which matches no option we ever offered.
        var options = ChatService.CleanModelOptions(new[] { written, "Issued" });
        Assert.Equal("Draft", options[0]);
    }

    [Theory]
    [InlineData("1.4.2026")]
    [InlineData("31.12.2025")]
    [InlineData("10.2025")]
    [InlineData("1.5 million")]
    public void A_label_that_merely_starts_with_digits_is_left_intact(string written)
    {
        // The marker strip used to eat these: "1.4.2026" became "4.2026" and "10.2025" became
        // "2025". dd.mm.yyyy is the Israeli date format and this bot answers in Hebrew, so
        // "which period do you mean?" is a realistic question for this very round — and the
        // label is sent back verbatim as the next message, so a corrupted one answers the model
        // with a date it never offered. A digit straight after the separator is never a marker.
        var options = ChatService.CleanModelOptions(new[] { written, "Something else" });
        Assert.Equal(written, options[0]);
    }

    [Fact]
    public void Two_periods_that_differ_only_after_the_marker_strip_stay_two_choices()
    {
        // The same bug's other face: "10.2025" and "11.2025" both collapsed to "2025", the
        // de-duplication then saw one choice, and the whole round lost its options.
        var options = ChatService.CleanModelOptions(new[] { "10.2025", "11.2025" });
        Assert.Equal(new[] { "10.2025", "11.2025" }, options);
    }

    [Fact]
    public void A_single_choice_is_not_a_choice()
    {
        // Cleaning keeps it; it just can't be a button, and the round-2 branch needs two before
        // it will render anything.
        var options = ChatService.CleanModelOptions(new[] { "Budget by Division" });
        Assert.Single(options);
        Assert.False(ChatService.OptionsCanBeButtons(options));
    }

    [Fact]
    public void Blank_entries_are_dropped()
    {
        Assert.Equal(new[] { "Draft" }, ChatService.CleanModelOptions(new[] { "Draft", "", "   ", null! }));
    }

    [Fact]
    public void Duplicates_differing_only_in_case_punctuation_or_emphasis_are_one_choice()
    {
        Assert.Single(ChatService.CleanModelOptions(new[] { "Draft", "draft" }));
        Assert.Single(ChatService.CleanModelOptions(new[] { "Draft", "Draft." }));
        Assert.Single(ChatService.CleanModelOptions(new[] { "Draft", "**Draft**" }));
        Assert.Equal(new[] { "Draft", "Issued" },
            ChatService.CleanModelOptions(new[] { "Draft", "draft", "Issued" }));
    }

    [Fact]
    public void An_invisible_bidi_mark_does_not_make_a_second_copy_of_a_choice()
    {
        // RLM/LRM are Unicode category Cf, so Trim() leaves them and two visually identical
        // Hebrew labels compared unequal — the user got two buttons that read the same.
        var options = ChatService.CleanModelOptions(new[]
        {
            "‏דוח תקציב לפי חטיבה",
            "דוח תקציב לפי חטיבה",
        });
        Assert.Single(options);
        Assert.Equal("דוח תקציב לפי חטיבה", options[0]);
    }

    [Fact]
    public void A_label_that_is_a_whole_clause_is_kept_but_cannot_be_a_button()
    {
        // The model writing prose into the options array. It is still a choice the user has to
        // be able to pick, so it survives — as a line of the numbered list, not as a button.
        var options = ChatService.CleanModelOptions(new[]
        {
            "Budget by Division",
            "The other one, which is the updated version of the same report that was added later on",
        });

        Assert.Equal(2, options.Count);
        Assert.False(ChatService.OptionsCanBeButtons(options));
        Assert.Contains("2. The other one", ChatService.ComposeClarifyText("Which?", options, false));
    }

    [Fact]
    public void More_choices_than_asked_for_are_still_all_offered()
    {
        // The prompt asks for the 2-4 it is torn between, and nine means it improvised — but
        // silently dropping seven choices the user was told to pick between is worse than nine
        // buttons.
        var nine = Enumerable.Range(1, 9).Select(i => $"Choice {i}").ToArray();
        var options = ChatService.CleanModelOptions(nine);
        Assert.Equal(9, options.Count);
        Assert.True(ChatService.OptionsCanBeButtons(options));
    }

    [Fact]
    public void A_label_containing_markup_is_dropped()
    {
        // The widget puts the label straight into the button's innerHTML without escaping it,
        // and "text" is innerHTML there too — so markup would be parsed as HTML in the host
        // page either way. A label is words.
        Assert.Equal(new[] { "Budget by Division" }, ChatService.CleanModelOptions(new[]
        {
            "Budget by Division",
            "<img src=x onerror=alert(1)>",
        }));

        Assert.Equal(new[] { "Budget by Division", "Budget by Company" },
            ChatService.CleanModelOptions(new[]
            {
                "Budget by Division",
                "<b>bold</b>",
                "Budget by Company",
            }));
    }

    [Fact]
    public void An_ampersand_in_a_label_is_fine()
    {
        // "Travel & expense operations" is one of the real fixed options — the markup guard must
        // not be so broad that it rejects ordinary punctuation.
        Assert.Equal(2, ChatService.CleanModelOptions(
            new[] { "Travel & expense operations", "Data analysis & reports" }).Count);
    }

    [Fact]
    public void A_label_containing_a_comma_cannot_be_a_button_but_is_still_offered()
    {
        // The widget does paramerter.split(","), so this set gets no buttons — and therefore the
        // numbered list, which is the only place these choices can appear.
        var options = ChatService.CleanModelOptions(new[] { "Draft, revised", "Issued" });

        Assert.Equal(2, options.Count);
        Assert.False(ChatService.OptionsCanBeButtons(options));
        Assert.Contains("1. Draft, revised", ChatService.ComposeClarifyText("Which?", options, false));
    }

    [Fact]
    public void No_options_at_all_is_not_an_error()
    {
        Assert.Empty(ChatService.CleanModelOptions(null));
        Assert.Empty(ChatService.CleanModelOptions(Array.Empty<string>()));
        Assert.False(ChatService.OptionsCanBeButtons(new List<string>()));
    }

    // ── Keeping the choices out of the question text ─────────────────────────────────────────

    [Fact]
    public void A_question_that_also_listed_the_choices_is_cleaned_up()
    {
        // The prompt asks for the question alone, but that is a request, not a guarantee — and
        // an ignored request would reproduce the exact "written twice" bug on this round.
        var question = "Which budget report do you mean?\n1. Budget by Division\n2. Budget by Cost Center";
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        Assert.Equal("Which budget report do you mean?",
            ChatService.ComposeClarifyText(question, options, optionsRenderAsButtons: true));
    }

    [Fact]
    public void Choices_named_inside_the_question_itself_are_left_alone()
    {
        // "the older one, or the newer one?" IS the question. Only whole lines that merely
        // restate an option are removed.
        var question = "Do you mean Budget by Division, or Budget by Cost Center?";
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        Assert.Equal(question, ChatService.ComposeClarifyText(question, options, optionsRenderAsButtons: true));
    }

    [Fact]
    public void A_question_that_is_nothing_but_the_list_is_kept_as_written()
    {
        // Stripping every line would leave an empty bubble, which is worse than a redundant one.
        var question = "1. Budget by Division\n2. Budget by Cost Center";
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        Assert.Equal(question, ChatService.StripOptionLines(question, options));
    }

    [Fact]
    public void The_blank_line_left_behind_by_a_stripped_list_goes_too()
    {
        var question = "Which one?\n\n- Draft\n- Issued\n";
        var options = new List<string> { "Draft", "Issued" };

        Assert.Equal("Which one?", ChatService.StripOptionLines(question, options));
    }

    [Theory]
    // A model that lists its own choices decorates them, and exact equality missed every form
    // below — the line then survived into the text while the same option also became a button.
    [InlineData("Which one?\n1. Budget by Division.\n2. Budget by Cost Center.")]
    [InlineData("Which one?\n- **Budget by Division**\n- **Budget by Cost Center**")]
    [InlineData("Which one?\n1. \"Budget by Division\"\n2. \"Budget by Cost Center\"")]
    [InlineData("Which one?\n* Budget by Division;\n* Budget by Cost Center;")]
    public void A_decorated_list_of_the_choices_is_stripped_too(string question)
    {
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        Assert.Equal("Which one?",
            ChatService.ComposeClarifyText(question, options, optionsRenderAsButtons: true));
    }

    [Fact]
    public void A_hebrew_numbered_list_with_bidi_marks_is_stripped_too()
    {
        // A model numbering Hebrew list lines puts a RLM before the digit so it displays
        // correctly. That mark blocked the ^-anchored marker strip, the line never matched its
        // own option, and the "written twice" bug came back — in Hebrew, which is the language
        // it was reported in.
        var question = "במה מדובר?\n‏1. Budget by Division\n‏2. Budget by Cost Center";
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        Assert.Equal("במה מדובר?",
            ChatService.ComposeClarifyText(question, options, optionsRenderAsButtons: true));
    }

    [Fact]
    public void A_client_without_buttons_still_gets_the_choices_exactly_once()
    {
        // This repo's own /chat page. The inlined list is stripped and then re-added by the
        // composition, so the options appear once — numbered — and remain answerable by typing.
        var question = "Which budget report do you mean?\n1. Budget by Division\n2. Budget by Cost Center";
        var options = new List<string> { "Budget by Division", "Budget by Cost Center" };

        var text = ChatService.ComposeClarifyText(question, options, optionsRenderAsButtons: false);

        Assert.Equal("Which budget report do you mean?\n1. Budget by Division\n2. Budget by Cost Center", text);
        Assert.Equal(1, text.Split("Budget by Division").Length - 1); // once in the list, and nowhere else
    }

    // ── A question must not carry a link that answers it ─────────────────────────────────────

    [Fact]
    public void A_self_worded_question_naming_two_reports_would_resolve_to_one_of_them()
    {
        // Documents WHY ProcessAsync must not run ResolvePageOverride on a clarifying turn.
        // The override derives the link from any page name in the reply — correct for an answer,
        // wrong for a question, because round 2's whole job is to name the competing entries.
        // Run against the real 367-entry catalog, so this stays true (or fails loudly) as the
        // catalog changes.
        var question = "Which of the two do you mean — Budget by Division Report, or Budget by Company Report?";

        Assert.NotNull(ChatService.ResolvePageOverride(null, question));
    }

    [Fact]
    public void The_fixed_questions_happen_to_resolve_to_nothing()
    {
        // True today, and the reason the fixed rounds never showed a stray link — but it is luck,
        // not design, which is exactly why the guard is at the call site and not here.
        Assert.Null(ChatService.ResolvePageOverride(null,
            "כדי שאוכל לכוון אותך לתשובה המדויקת ביותר — במה מדובר?"));
        Assert.Null(ChatService.ResolvePageOverride(null,
            "What status is the trip or expense report currently in?"));
    }

    // ── End to end, the way a compliant reply flows ──────────────────────────────────────────

    [Fact]
    public void A_compliant_reply_becomes_a_bare_question_plus_buttons()
    {
        var (intent, text, _, raw) = ChatService.ParseAiResponse(
            """
            {"intent":"clarify","text":"האם מדובר בדוח התקציב לפי אגף או לפי מרכז עלות?",
             "options":["תקציב לפי אגף","תקציב לפי מרכז עלות"]}
            """);
        var options = ChatService.CleanModelOptions(raw);
        var renders = ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true);

        Assert.Equal("clarify", intent);
        Assert.True(renders);
        Assert.Equal(text, ChatService.ComposeClarifyText(text, options, renders));
        Assert.DoesNotContain("1.", ChatService.ComposeClarifyText(text, options, renders));
    }

    [Fact]
    public void An_unbuttonable_reply_still_puts_every_choice_in_front_of_the_user()
    {
        // The path that used to end in a dead end: one label too long for a button, so the whole
        // set was discarded — and because the prompt tells the model to keep the choices out of
        // "text", they reached the user nowhere at all. Now the set is shown as a numbered list,
        // QuickReplies stays empty (nothing to click, nothing echoed back verbatim), and the
        // question is answerable by typing.
        var (_, text, _, raw) = ChatService.ParseAiResponse(
            """
            {"intent":"clarify","text":"Which of the budget reports do you mean?",
             "options":["Budget by Division",
                        "Budget vs Actual by Department for the current fiscal year including committed amounts"]}
            """);
        var options = ChatService.CleanModelOptions(raw);

        Assert.Equal(2, options.Count);
        Assert.False(ChatService.OptionsCanBeButtons(options));

        var composed = ChatService.ComposeClarifyText(text, options,
            ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true));

        Assert.StartsWith("Which of the budget reports do you mean?", composed);
        Assert.Contains("1. Budget by Division", composed);
        Assert.Contains("2. Budget vs Actual by Department", composed);
    }
}
