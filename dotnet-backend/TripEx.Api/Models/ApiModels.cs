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
    public string? Trid { get; set; }
    public string? UserDate { get; set; }
    public string? UserTime { get; set; }
    public string? UserTimezone { get; set; }
}

public class ChatResponse
{
    public string Text { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    public string RedirectPage { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
    public string SessionId { get; set; } = "";
    // Set when Milo hands the ticket to a human. The frontend uses these to
    // show a "connect to a human agent" option pointing at SupportContact.
    public bool Escalated { get; set; }
    public string? SupportContact { get; set; }
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
