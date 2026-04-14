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
}

// ═══════════════════════════════════════
// Invoice Models (AlgoText-compatible format)
// ═══════════════════════════════════════

public class AnalyzeInvoiceRequest
{
    public string? ImageBase64 { get; set; }
    public string? ImageUrl { get; set; }
    public string? Country { get; set; }  // "IL", "PH", etc.
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
    public string? TotalAmount { get; set; }      // Total amount (same as Total, explicit field)
    public string? FormOfPayment { get; set; }    // credit, cash, bank
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
