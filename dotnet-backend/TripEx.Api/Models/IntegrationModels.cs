namespace TripEx.Api.Models;

// ═══════════════════════════════════════════════════════════════════
// NetSuite Configuration Models
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// הגדרות חיבור ל-NetSuite (Token-Based Authentication)
/// NetSuite connection configuration using OAuth 1.0 TBA
/// </summary>
public class NetSuiteConfigDto
{
    public Guid? Id { get; set; }
    public string AccountId { get; set; } = "";         // NetSuite Account ID (e.g. "1234567")
    public string ConsumerKey { get; set; } = "";       // OAuth Consumer Key
    public string ConsumerSecret { get; set; } = "";    // OAuth Consumer Secret
    public string TokenKey { get; set; } = "";          // OAuth Token Key
    public string TokenSecret { get; set; } = "";       // OAuth Token Secret
    public decimal AmountThreshold { get; set; } = 5000; // סף סכום לדיווח - ברירת מחדל 5000 ₪
    public string Currency { get; set; } = "ILS";       // מטבע ברירת מחדל
    public bool IsEnabled { get; set; } = false;
    public bool AutoSync { get; set; } = false;         // סנכרון אוטומטי
    public int AutoSyncIntervalMinutes { get; set; } = 60;
    public DateTime? LastSyncAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SaveNetSuiteConfigRequest
{
    public string AccountId { get; set; } = "";
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
    public string TokenKey { get; set; } = "";
    public string TokenSecret { get; set; } = "";
    public decimal AmountThreshold { get; set; } = 5000;
    public string Currency { get; set; } = "ILS";
    public bool IsEnabled { get; set; } = false;
    public bool AutoSync { get; set; } = false;
    public int AutoSyncIntervalMinutes { get; set; } = 60;
}

public class NetSuiteConnectionTestResponse
{
    public bool Success { get; set; }
    public string? AccountName { get; set; }
    public string? AccountId { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public long ResponseTimeMs { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// NetSuite Expense / Transaction Models
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// הוצאה / חשבונית מ-NetSuite
/// Expense/transaction fetched from NetSuite
/// </summary>
public class NetSuiteExpense
{
    public string InternalId { get; set; } = "";        // NetSuite internal ID
    public string TranId { get; set; } = "";            // Transaction number (מספר עסקה)
    public string? TranDate { get; set; }               // Transaction date
    public decimal Amount { get; set; }                 // סכום כולל
    public decimal TaxAmount { get; set; }              // סכום מע"מ
    public string Currency { get; set; } = "ILS";
    public string? VendorId { get; set; }               // ת.ז. / ח.פ. ספק
    public string? VendorName { get; set; }             // שם ספק
    public string? Memo { get; set; }                   // הערות
    public string? Department { get; set; }             // מחלקה
    public string? Subsidiary { get; set; }             // חברה בת
    public string RecordType { get; set; } = "vendorbill"; // סוג רשומה
    public string Status { get; set; } = "open";
    public string? AllocationNumber { get; set; }       // מספר הקצאה מרשות המיסים
    public string? AllocationStatus { get; set; }       // סטטוס הקצאה: pending/approved/failed
}

/// <summary>
/// תגובה מ-NetSuite SuiteQL
/// </summary>
public class NetSuiteSuiteQlResponse
{
    public List<Dictionary<string, object?>> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public int TotalResults { get; set; }
    public int Count { get; set; }
    public int Offset { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// Israeli Tax Authority Models (רשות המיסים)
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// הגדרות חיבור לרשות המיסים
/// Israeli Tax Authority connection configuration
/// </summary>
public class TaxAuthorityConfigDto
{
    public Guid? Id { get; set; }
    public string ApiBaseUrl { get; set; } = "https://openapi.taxes.gov.il";
    public string? ClientId { get; set; }               // מזהה לקוח API
    public string? ClientSecret { get; set; }           // סוד לקוח API
    public string? Username { get; set; }               // שם משתמש (אם נדרש)
    public string BusinessId { get; set; } = "";        // מספר ח.פ. / ע.מ. של העסק
    public bool IsEnabled { get; set; } = false;
    public bool UseSandbox { get; set; } = true;        // סביבת בדיקה
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SaveTaxAuthorityConfigRequest
{
    public string ApiBaseUrl { get; set; } = "https://openapi.taxes.gov.il";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Username { get; set; }
    public string BusinessId { get; set; } = "";
    public bool IsEnabled { get; set; } = false;
    public bool UseSandbox { get; set; } = true;
}

/// <summary>
/// בקשה לקבלת מספר הקצאה מרשות המיסים
/// Request to get an allocation number (מספר הקצאה) from the Tax Authority
/// </summary>
public class AllocationRequest
{
    public string SupplierId { get; set; } = "";        // ח.פ. / ע.מ. מוציא החשבונית
    public string SupplierName { get; set; } = "";      // שם מוציא החשבונית
    public string? CustomerId { get; set; }             // ח.פ. / ע.מ. מקבל החשבונית
    public string? CustomerName { get; set; }           // שם מקבל החשבונית
    public string InvoiceNumber { get; set; } = "";     // מספר חשבונית
    public string InvoiceDate { get; set; } = "";       // תאריך חשבונית (YYYY-MM-DD)
    public decimal TotalAmount { get; set; }            // סכום כולל מע"מ
    public decimal TaxAmount { get; set; }              // סכום מע"מ
    public decimal AmountBeforeTax { get; set; }        // סכום לפני מע"מ
    public string Currency { get; set; } = "ILS";
    public string DocumentType { get; set; } = "חשבונית מס"; // סוג מסמך
    public string SourceSystem { get; set; } = "NetSuite";
    public string SourceTransactionId { get; set; } = ""; // מזהה ב-NetSuite
}

/// <summary>
/// תגובה מרשות המיסים עם מספר ההקצאה
/// Response from Tax Authority with allocation number
/// </summary>
public class AllocationResponse
{
    public bool Success { get; set; }
    public string? AllocationNumber { get; set; }       // מספר הקצאה
    public string? ConfirmationCode { get; set; }       // קוד אישור
    public string? Status { get; set; }                 // approved / rejected / pending
    public string? RejectionReason { get; set; }        // סיבת דחייה (אם נדחתה)
    public DateTime? ExpiryDate { get; set; }           // תאריך תפוגת מספר ההקצאה
    public string? RawResponse { get; set; }            // תגובה גולמית מה-API
    public string? Error { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// Integration Sync Models
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// בקשה להפעלת סנכרון ידני
/// </summary>
public class TriggerSyncRequest
{
    public bool DryRun { get; set; } = false;           // מצב בדיקה - לא מבצע שינויים
    public string? DateFrom { get; set; }               // מתאריך (YYYY-MM-DD) - ברירת מחדל: 30 ימים אחורה
    public string? DateTo { get; set; }                 // עד תאריך
    public decimal? AmountThresholdOverride { get; set; } // עקיפת סף הסכום
}

/// <summary>
/// תוצאת סנכרון
/// </summary>
public class SyncResult
{
    public Guid SyncId { get; set; } = Guid.NewGuid();
    public bool Success { get; set; }
    public bool IsDryRun { get; set; }
    public int TotalExpensesFetched { get; set; }       // סה"כ הוצאות שנשלפו מ-NetSuite
    public int ExpensesAboveThreshold { get; set; }     // הוצאות מעל הסף
    public int AllocationRequested { get; set; }        // בקשות הקצאה שנשלחו
    public int AllocationApproved { get; set; }         // הקצאות שאושרו
    public int AllocationFailed { get; set; }           // הקצאות שנכשלו
    public int AlreadyProcessed { get; set; }           // כבר עובדו בעבר
    public List<SyncExpenseDetail> Details { get; set; } = new();
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// פרטי הוצאה בתוצאת הסנכרון
/// </summary>
public class SyncExpenseDetail
{
    public string NetSuiteId { get; set; } = "";
    public string TranId { get; set; } = "";
    public decimal Amount { get; set; }
    public string? VendorName { get; set; }
    public string Status { get; set; } = ""; // fetched / skipped / requested / approved / failed / already_processed
    public string? AllocationNumber { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// היסטוריית סנכרון - עבור תצוגה ב-UI
/// </summary>
public class SyncHistoryEntry
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "";            // success / partial / failed
    public bool IsDryRun { get; set; }
    public int TotalFetched { get; set; }
    public int AboveThreshold { get; set; }
    public int Approved { get; set; }
    public int Failed { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// Combined Integration Status (for dashboard)
// ═══════════════════════════════════════════════════════════════════

public class IntegrationStatusResponse
{
    public bool NetSuiteConfigured { get; set; }
    public bool NetSuiteEnabled { get; set; }
    public bool TaxAuthorityConfigured { get; set; }
    public bool TaxAuthorityEnabled { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public int PendingAllocations { get; set; }          // הוצאות הממתינות לבקשת הקצאה
    public decimal AmountThreshold { get; set; }
}
