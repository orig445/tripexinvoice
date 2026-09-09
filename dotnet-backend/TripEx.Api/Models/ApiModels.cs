using System.Text.Json.Serialization;
using TripEx.Api.Json;

namespace TripEx.Api.Models;

// ═══════════════════════════════════════
// Auth Models
// ═══════════════════════════════════════

public class RegisterRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string? DisplayName { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class AuthResponse
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Error { get; set; }
}

// ═══════════════════════════════════════
// Chat Models
// ═══════════════════════════════════════

public class ChatRequest
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "text";       // "text" | "image"
    public string Source { get; set; } = "web";       // "web" | "mobile" | "widget"
    public string? SessionToken { get; set; }
    public string? Scope { get; set; }
    // TAS's widget sends trid as a bare JSON number (e.g. 0), not a string.
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? Trid { get; set; }
    public string? UserDate { get; set; }
    public string? UserTime { get; set; }
    public string? UserTimezone { get; set; }
    // Identity/context a host page hands the widget via postMessage (e.g. the "Sports
    // Support" embed: token, customerName, companyName, customerId, role, pageContext,
    // locale) and the widget forwards verbatim on every request — same trust level as
    // Source/Scope/Trid above, since it rides on the same already-authenticated TAS call.
    public WidgetIdentityContext? Widget { get; set; }

    // ── The TAS widget's own request shape (flat, different names) ──────────────────────────
    // Captured from the live widget on 2026-09-08 by hooking its fetch (DEV_AI_2/app.js):
    //   {"companyName":null,"customerName":"Guest","customerId":null,"role":null,
    //    "sentAt":"2026-09-08T08:12:25.165Z","messageId":"...","locale":"en-US",
    //    "timezone":"Asia/Jerusalem","platform":"web","appVersion":"1.0.0","pageContext":null,
    //    "conversationId":"413494f3-...","sessionId":"413494f3-...","module":"web",
    //    "scope":"webpage","trid":0,"request":"...","text":"...","tts":false}
    //
    // Two contract gaps this closes, both of which looked like client bugs and were not:
    //
    // 1. It returns the conversation id under BOTH conversationId and sessionId — its own
    //    comment reads "Both are sent, so threading works whichever the service reads" — and
    //    this API bound neither, so System.Text.Json silently dropped them and every message
    //    opened a brand-new session. That one field-name gap, not the widget, is the entire
    //    reason Milo had no conversation memory. Proven side by side against live QA: the same
    //    id sent as sessionToken recalls the earlier turn, sent as sessionId/conversationId it
    //    answers "this is your first message" and mints a new GUID.
    // 2. It sends the postMessage identity fields FLAT, not nested under "widget", so
    //    request.Widget was always null: the role/locale/pageContext handling added on
    //    2026-09-07 never once executed, and its [WIDGET-CONTEXT] log line never fired — which
    //    made the log itself misleading, as if the host were sending no context at all.
    //
    // NormalizeWidgetShape() folds all of this into the canonical properties, so nothing
    // downstream needs to know which client it is talking to.
    public string? SessionId { get; set; }
    public string? ConversationId { get; set; }
    public string? CustomerName { get; set; }
    public string? CompanyName { get; set; }
    public string? CustomerId { get; set; }
    public string? Role { get; set; }
    public string? PageContext { get; set; }
    public string? Locale { get; set; }
    public string? SentAt { get; set; }
    public string? Timezone { get; set; }

    /// <summary>
    /// True when this request arrived in the TAS widget's own flat shape above — set by
    /// NormalizeWidgetShape(), never deserialized from the body.
    ///
    /// It exists for exactly one decision: the TAS widget is the only client that renders
    /// ChatResponse.Paramerter as real option buttons, so it is the only one for which
    /// repeating those options as a numbered list inside "text" is a duplicate. Every other
    /// caller in this repo ignores both Paramerter and QuickReplies (verified 2026-09-09 —
    /// nothing under src/ references either), and for them the numbered list in "text" is the
    /// ONLY way the options reach the user. Source can't be used to tell them apart: it
    /// defaults to "web" and the widget doesn't send it, so the widget and this repo's own
    /// /chat page look identical on that field.
    ///
    /// Deliberately NOT a security or trust signal — it only picks between two renderings of
    /// the same options, so a caller that spoofs the shape gets buttons instead of a list.
    /// </summary>
    [JsonIgnore]
    public bool IsTasWidgetClient { get; private set; }

    /// <summary>
    /// Folds the widget's flat shape into the canonical properties. An explicit canonical value
    /// always wins, so a caller that already speaks the documented shape is unaffected.
    /// </summary>
    public void NormalizeWidgetShape()
    {
        // Any of the flat fields means the caller speaks the widget's shape. customerName alone
        // would do in practice (the widget always sends it, falling back to "Guest" — see the
        // captured payload above), but keying off the whole set means a future widget build that
        // drops one field doesn't silently reclassify it as some other client.
        IsTasWidgetClient = !string.IsNullOrWhiteSpace(FirstNonBlank(
            CustomerName, CompanyName, CustomerId, Role, PageContext, Locale,
            SentAt, Timezone, SessionId, ConversationId));

        if (string.IsNullOrWhiteSpace(SessionToken))
            SessionToken = FirstNonBlank(SessionId, ConversationId);

        if (Widget == null && !string.IsNullOrWhiteSpace(
                FirstNonBlank(CustomerName, CompanyName, CustomerId, Role, PageContext, Locale)))
        {
            Widget = new WidgetIdentityContext
            {
                CustomerName = CustomerName,
                CompanyName = CompanyName,
                CustomerId = CustomerId,
                Role = Role,
                PageContext = PageContext,
                Locale = Locale,
            };
        }

        // The widget reports one ISO-8601 instant plus an IANA zone, where the prompt expects a
        // date, a time and a zone. The instant is passed through verbatim rather than reformatted
        // — it is unambiguous, and parsing it here would only add a way to get the time wrong.
        // Without this the model was told "Browser-reported time: unknown (unknown)".
        if (string.IsNullOrWhiteSpace(UserDate)) UserDate = SentAt;
        if (string.IsNullOrWhiteSpace(UserTimezone)) UserTimezone = Timezone;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public class WidgetIdentityContext
{
    public string? Token { get; set; }
    public string? CustomerName { get; set; }
    public string? CompanyName { get; set; }
    public string? CustomerId { get; set; }
    public string? Role { get; set; }
    public string? PageContext { get; set; }
    public string? Locale { get; set; }
}

public class ChatResponse
{
    public string Text { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    // Clickable option labels for a fixed multiple-choice question (the clarify-flow
    // orientation/status questions) — empty for every other kind of reply. Clicking one is
    // meant to just re-send its exact text as the next user message, same as a normal reply.
    public List<string> QuickReplies { get; set; } = new();

    // The SAME options as one comma-separated string, because that is the field the TAS widget
    // actually reads. Verified against its live source on 2026-09-08
    // (qaeu.combtas.com/DEV_AI_2/assets/script/app.js):
    //
    //     const params = response.paramerter ? response.paramerter.split(",")... : [];
    //     if (params.length > 0 && !response.paramerter.includes("TID")) {
    //         linksHtml = params.map(p => `<button class="ai-link-btn" data-message="${p}">`)
    //     botBubble.querySelectorAll(".ai-link-btn").forEach(btn =>
    //         btn.addEventListener("click", () => sendMessage(btn.dataset.message)));
    //
    // Populating this makes that widget render real clickable buttons through its own mechanism,
    // with no widget change — and those survive its hardenLinks() sanitizer, which strips
    // javascript: hrefs and so silently killed the previous approach. It reads neither
    // QuickReplies nor Actions.
    //
    // The misspelling is the widget's, not a typo here — renaming it breaks the contract.
    // Its split(",") imposes two hard constraints, enforced where this is built in ChatService:
    // no option may contain a comma, and the string may never contain "TID" (that substring
    // switches the widget to a different trip-link rendering).
    public string? Paramerter { get; set; }

    public string RedirectPage { get; set; } = "";
    // Button text to show for RedirectPage (e.g. "Go to Settings"). Empty when RedirectPage is empty.
    public string? RedirectLabel { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public string SessionId { get; set; } = "";
    // Set when Milo hands the ticket to a human. The frontend uses these to
    // show a "connect to a human agent" option pointing at SupportContact.
    public bool Escalated { get; set; }
    public string? SupportContact { get; set; }
}

/// <summary>
/// One entry in Data/status-glossary.json — ground-truth wording for a trip/expense-report
/// status or a related mechanism (approval rounds, Per Diem, etc.), fed into the system
/// prompt so Milo explains these correctly instead of guessing or relying only on the
/// separate Knowledge Base. See ChatService.LoadStatusGlossary.
/// </summary>
public class StatusGlossaryEntry
{
    public string Key { get; set; } = "";
    public string Explanation { get; set; } = "";
}

/// <summary>
/// One entry in Data/status-glossary.json's "mechanisms" list — a general operational
/// concept (not a trip status) worth Milo knowing, e.g. approval rounds or Per Diem rules.
/// </summary>
public class MechanismEntry
{
    public string Topic { get; set; } = "";
    public string Explanation { get; set; } = "";
}

/// <summary>
/// One entry in Data/page-links.json — a known TripEX page Milo can send the user to.
/// "Key" is what the AI references as "page" in its JSON response.
/// </summary>
public class PageLinkConfig
{
    /// <summary>Unique, stable identifier (the TAS page's own filename/report code).</summary>
    public string Key { get; set; } = "";
    /// <summary>Path relative to page-links.json's top-level "baseUrl" (e.g. "/Master_Pages/x.aspx").</summary>
    public string Url { get; set; } = "";
    /// <summary>Link text shown to the user when replying in Hebrew (the default/fallback).</summary>
    public string Label { get; set; } = "";
    /// <summary>Link text shown to the user when replying in English.</summary>
    public string LabelEn { get; set; } = "";
    /// <summary>Short hint for the AI on when this page is relevant — not shown to the user.</summary>
    public string Description { get; set; } = "";
    /// <summary>Grouping only (e.g. "Settings > Accounting", "Reports") — not shown to the user.</summary>
    public string Category { get; set; } = "";
}

// ═══════════════════════════════════════
// Invoice Models (AlgoText-compatible format)
// ═══════════════════════════════════════

/// <summary>
/// A single expense-type option sent by the caller so the AI can pick the best match.
/// Accepts both Id/Name and ExpenseTypeId/ExpenseTypeDesc naming conventions.
/// </summary>
public class ExpenseTypeOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // AlgoText / combtas naming aliases
    [System.Text.Json.Serialization.JsonPropertyName("ExpenseTypeId")]
    public int? ExpenseTypeId { get => Id == 0 ? null : Id; set { if (value.HasValue) Id = value.Value; } }

    [System.Text.Json.Serialization.JsonPropertyName("ExpenseTypeDesc")]
    public string? ExpenseTypeDesc { get => string.IsNullOrEmpty(Name) ? null : Name; set { if (value != null) Name = value; } }
}

/// <summary>
/// A single form-of-payment option sent by the caller so the AI can pick the best match.
/// Accepts both Id/Name and FormOfPaymentId/FormOfPaymentDesc naming conventions.
/// </summary>
public class FormOfPaymentOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // AlgoText / combtas naming aliases
    [System.Text.Json.Serialization.JsonPropertyName("FormOfPaymentId")]
    public int? FormOfPaymentId { get => Id == 0 ? null : Id; set { if (value.HasValue) Id = value.Value; } }

    [System.Text.Json.Serialization.JsonPropertyName("FormOfPaymentDesc")]
    public string? FormOfPaymentDesc { get => string.IsNullOrEmpty(Name) ? null : Name; set { if (value != null) Name = value; } }
}

public class AnalyzeInvoiceRequest
{
    public string? ImageBase64 { get; set; }
    public string? ImageUrl { get; set; }
    public string? Country { get; set; }  // "IL", "PH", etc.

    /// <summary>
    /// Optional list of expense-type options (id + name) from the client.
    /// When provided, the AI (and server-side fallback) will return the matching ExpenseTypeId.
    /// Accepts both "ExpenseTypes" and "ListOfExpenseType" JSON keys.
    /// </summary>
    public List<ExpenseTypeOption>? ExpenseTypes { get; set; }

    /// <summary>
    /// Optional list of form-of-payment options (id + name) from the client.
    /// When provided, the AI (and server-side fallback) will return the matching FormOfPaymentId.
    /// Accepts both "FormOfPayments" and "ListOfFormOfPayment" JSON keys.
    /// </summary>
    public List<FormOfPaymentOption>? FormOfPayments { get; set; }

    /// <summary>
    /// When true, bypass the SHA256 response cache and force a fresh OCI call.
    /// Use when a previous scan returned wrong results that were cached.
    /// </summary>
    public bool ForceRefresh { get; set; }

    // AlgoText / combtas naming aliases — delegate to the canonical properties above
    [System.Text.Json.Serialization.JsonPropertyName("ListOfExpenseType")]
    public List<ExpenseTypeOption>? ListOfExpenseType
    {
        get => ExpenseTypes;
        set { if (ExpenseTypes == null) ExpenseTypes = value; }
    }

    [System.Text.Json.Serialization.JsonPropertyName("ListOfFormOfPayment")]
    public List<FormOfPaymentOption>? ListOfFormOfPayment
    {
        get => FormOfPayments;
        set { if (FormOfPayments == null) FormOfPayments = value; }
    }
}

public class CacheInvalidateRequest
{
    /// <summary>Full or partial SHA256 hash of the image to evict from cache.</summary>
    public string? Sha256 { get; set; }
    /// <summary>When true, clears all cached OCR responses.</summary>
    public bool ClearAll { get; set; }
}

public class AnalyzeInvoiceResponse
{
    public bool Success { get; set; }
    public InvoiceFields? Fields { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Flat field structure matching AlgoText format ($.fields.*)
/// TripEx client reads: fields.total, fields.totalVAT, fields.currency,
/// fields.invoiceNumber, fields.invoiceDate, fields.type
/// </summary>
public class InvoiceFields
{
    public string? Total { get; set; }
    public string? TotalVAT { get; set; }
    public string? Currency { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? InvoiceDate { get; set; }  // YYYY-MM-DD
    public string? Type { get; set; }         // category
    public string? SubCategory { get; set; }
    public string? MerchantName { get; set; }
    public string? MerchantTin { get; set; }
    public string? MerchantAddress { get; set; }
    public string? MerchantCity { get; set; }
    public string? PaymentMethod { get; set; }
    public string? AmountPaid { get; set; }
    public string? ExpenseType { get; set; }      // business_meal, vehicle, entertainment, hotel, internet, parking, other, meal, taxi
    public int? ExpenseTypeId { get; set; }       // ID matched from the caller-supplied ExpenseTypes list
    public string? TotalAmount { get; set; }      // Total amount (same as Total, explicit field)
    public string? FormOfPayment { get; set; }    // credit, cash, bank
    public int? FormOfPaymentId { get; set; }     // ID matched from the caller-supplied FormOfPayments list
    public string? CardLast4 { get; set; }        // Last 4 digits of credit card
    public string? CardType { get; set; }         // visa, mastercard, amex, diners, isracart, other
    public string? ExtraDetails { get; set; }     // JSON string with all raw extracted data
}

// ═══════════════════════════════════════
// Legacy internal models (used during AI extraction)
// ═══════════════════════════════════════

public class InvoiceData
{
    public string? DocumentType { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? InvoiceDate { get; set; }
    public string? Currency { get; set; }
    public MerchantInfo? Merchant { get; set; }
    public AmountsInfo? Amounts { get; set; }
    public PaymentInfo? Payment { get; set; }
    public int ItemCount { get; set; }
    public string? Category { get; set; }
}

public class MerchantInfo
{
    public string? Name { get; set; }
    public string? Tin { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
}

public class AmountsInfo
{
    public decimal? VatableSalesAmount { get; set; }
    public decimal? NonVatableSalesAmount { get; set; }
    public decimal? ServiceChargeAmount { get; set; }
    public decimal? TaxAmount { get; set; }
}

public class PaymentInfo
{
    public string? Method { get; set; }
    public decimal? AmountPaid { get; set; }
}

// ═══════════════════════════════════════
// Knowledge Models
// ═══════════════════════════════════════

public class ProcessKnowledgeRequest
{
    public string DocumentId { get; set; } = "";
}

public class ProcessKnowledgeResponse
{
    public bool Success { get; set; }
    public int ChunksCreated { get; set; }
    public int TextLength { get; set; }
    public string? Error { get; set; }
}

public class UploadKnowledgeResponse
{
    public bool Success { get; set; }
    public string? DocumentId { get; set; }
    public string? FileName { get; set; }
    public int ChunksCreated { get; set; }
    public string? Error { get; set; }
}

public class KnowledgeDocumentDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = "";
    public int? FileSize { get; set; }
    public string? Domain { get; set; }
    public string? DocType { get; set; }
    public string? Description { get; set; }
    public string? Audience { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class UpdateKnowledgeTagsRequest
{
    public string? Domain { get; set; }
    public string? DocType { get; set; }
    public string? Description { get; set; }
}

// ═══════════════════════════════════════
// Oracle AI Models
// ═══════════════════════════════════════

public class OracleMessage
{
    public string Role { get; set; } = "";
    public object Content { get; set; } = "";
}

public class OracleChatRequest
{
    public string Model { get; set; } = "";
    public List<OracleMessage> Messages { get; set; } = new();
    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.3;
}

public class OracleChatResponse
{
    public List<OracleChoice>? Choices { get; set; }
}

public class OracleChoice
{
    public OracleResponseMessage? Message { get; set; }
}

public class OracleResponseMessage
{
    public string Content { get; set; } = "";
}

// ═══════════════════════════════════════
// Geolocation
// ═══════════════════════════════════════

public class GeoInfo
{
    public string Location { get; set; } = "";
    public string Timezone { get; set; } = "";
    public string LocalTime { get; set; } = "";
}

// ═══════════════════════════════════════
// OCR Training Models
// ═══════════════════════════════════════

public class BulkTrainRequest
{
    public string? ImageBase64 { get; set; }
    public string? Country { get; set; }
}

public class BulkTrainResponse
{
    public bool Success { get; set; }
    public Guid? SampleId { get; set; }
    public InvoiceFields? Fields { get; set; }
    public string? Error { get; set; }
}

public class VerifyTrainingSampleRequest
{
    public Guid SampleId { get; set; }
    public bool IsCorrect { get; set; }
    public Dictionary<string, string>? Corrections { get; set; }
}

public class RebuildPatternsResponse
{
    public bool Success { get; set; }
    public int PatternsCreated { get; set; }
    public int SamplesAnalyzed { get; set; }
    public string? Error { get; set; }
}

public class TrainingStatsResponse
{
    public int TotalSamples { get; set; }
    public int VerifiedSamples { get; set; }
    public int RejectedSamples { get; set; }
    public int PatternsLearned { get; set; }
    public List<PatternInfo> Patterns { get; set; } = new();
}

public class PatternInfo
{
    public string FieldName { get; set; } = "";
    public string Rule { get; set; } = "";
    public string? Country { get; set; }
    public double Confidence { get; set; }
    public int SourceCount { get; set; }
}
