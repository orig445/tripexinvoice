using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TripEx.Api.Data;

public class TripExDbContext : DbContext
{
    public TripExDbContext(DbContextOptions<TripExDbContext> options) : base(options) { }

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatbotConfig> ChatbotConfigs => Set<ChatbotConfig>();
    public DbSet<ChatbotLog> ChatbotLogs => Set<ChatbotLog>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceCorrection> InvoiceCorrections => Set<InvoiceCorrection>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();
    public DbSet<InvoiceScanLog> InvoiceScanLogs => Set<InvoiceScanLog>();
    public DbSet<OcrTrainingSample> OcrTrainingSamples => Set<OcrTrainingSample>();
    public DbSet<OcrTrainingPattern> OcrTrainingPatterns => Set<OcrTrainingPattern>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatSession>().ToTable("chat_sessions");
        modelBuilder.Entity<ChatMessage>().ToTable("chat_messages");
        modelBuilder.Entity<ChatbotConfig>().ToTable("chatbot_config");
        modelBuilder.Entity<ChatbotLog>().ToTable("chatbot_logs");
        modelBuilder.Entity<Invoice>().ToTable("invoices");
        modelBuilder.Entity<InvoiceCorrection>().ToTable("invoice_corrections");
        modelBuilder.Entity<KnowledgeDocument>().ToTable("knowledge_documents");
        modelBuilder.Entity<KnowledgeChunk>().ToTable("knowledge_chunks");
        modelBuilder.Entity<Profile>().ToTable("profiles");
        modelBuilder.Entity<UserRole>().ToTable("user_roles");
        modelBuilder.Entity<UserCredential>().ToTable("user_credentials");
        modelBuilder.Entity<InvoiceScanLog>().ToTable("InvoiceScanLogs");
        modelBuilder.Entity<OcrTrainingSample>().ToTable("OcrTrainingSamples");
        modelBuilder.Entity<OcrTrainingPattern>().ToTable("OcrTrainingPatterns");

        modelBuilder.Entity<UserCredential>()
            .HasIndex(c => c.Email)
            .IsUnique();
    }
}

// ═══════════════════════════════════════
// Entity Classes
// ═══════════════════════════════════════

[Table("chat_sessions")]
public class ChatSession
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("source")] public string Source { get; set; } = "web";
    [Column("status")] public string Status { get; set; } = "active";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("chat_messages")]
public class ChatMessage
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("session_id")] public Guid SessionId { get; set; }
    [Column("role")] public string Role { get; set; } = "user";
    [Column("content")] public string Content { get; set; } = "";
    [Column("intent")] public string? Intent { get; set; }
    [Column("metadata", TypeName = "nvarchar(max)")] public string? Metadata { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("chatbot_config")]
public class ChatbotConfig
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("bot_name")] public string BotName { get; set; } = "TripEX AI";
    [Column("avatar_url")] public string? AvatarUrl { get; set; }
    [Column("welcome_message")] public string WelcomeMessage { get; set; } = "";
    [Column("system_prompt")] public string SystemPrompt { get; set; } = "";
    [Column("model_name")] public string ModelName { get; set; } = "";
    [Column("temperature")] public decimal Temperature { get; set; } = 0.3m;
    [Column("max_tokens")] public int MaxTokens { get; set; } = 1024;
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("chatbot_logs")]
public class ChatbotLog
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("session_id")] public Guid? SessionId { get; set; }
    [Column("user_id")] public Guid? UserId { get; set; }
    [Column("event_type")] public string EventType { get; set; } = "";
    [Column("details", TypeName = "nvarchar(max)")] public string? Details { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("invoices")]
public class Invoice
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid? UserId { get; set; }
    [Column("invoice_number")] public string? InvoiceNumber { get; set; }
    [Column("invoice_date")] public DateOnly? InvoiceDate { get; set; }
    [Column("vendor_name")] public string? VendorName { get; set; }
    [Column("vendor_address")] public string? VendorAddress { get; set; }
    [Column("vendor_phone")] public string? VendorPhone { get; set; }
    [Column("vendor_email")] public string? VendorEmail { get; set; }
    [Column("vendor_id")] public string? VendorId { get; set; }
    [Column("customer_name")] public string? CustomerName { get; set; }
    [Column("customer_address")] public string? CustomerAddress { get; set; }
    [Column("currency")] public string? Currency { get; set; } = "ILS";
    [Column("subtotal")] public decimal? Subtotal { get; set; }
    [Column("tax_amount")] public decimal? TaxAmount { get; set; }
    [Column("total_amount")] public decimal? TotalAmount { get; set; }
    [Column("line_items", TypeName = "nvarchar(max)")] public string? LineItems { get; set; }
    [Column("notes")] public string? Notes { get; set; }
    [Column("payment_terms")] public string? PaymentTerms { get; set; }
    [Column("image_url")] public string? ImageUrl { get; set; }
    [Column("raw_ai_response")] public string? RawAiResponse { get; set; }
    [Column("status")] public string? Status { get; set; } = "processed";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("invoice_corrections")]
public class InvoiceCorrection
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("field_name")] public string FieldName { get; set; } = "";
    [Column("original_value")] public string? OriginalValue { get; set; }
    [Column("corrected_value")] public string CorrectedValue { get; set; } = "";
    [Column("context")] public string? Context { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("knowledge_documents")]
public class KnowledgeDocument
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("file_name")] public string FileName { get; set; } = "";
    [Column("file_type")] public string FileType { get; set; } = "";
    [Column("file_url")] public string FileUrl { get; set; } = "";
    [Column("file_size")] public int? FileSize { get; set; }
    [Column("uploaded_by")] public Guid? UploadedBy { get; set; }
    [Column("status")] public string Status { get; set; } = "pending";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("knowledge_chunks")]
public class KnowledgeChunk
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("document_id")] public Guid DocumentId { get; set; }
    [Column("content")] public string Content { get; set; } = "";
    [Column("chunk_index")] public int ChunkIndex { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("profiles")]
public class Profile
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("email")] public string? Email { get; set; }
    [Column("display_name")] public string? DisplayName { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("user_roles")]
public class UserRole
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("role")] public string Role { get; set; } = "user";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("user_credentials")]
public class UserCredential
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("password_hash")] public string PasswordHash { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// New InvoiceScanLogs table — simplified logging per your spec
/// </summary>
[Table("InvoiceScanLogs")]
public class InvoiceScanLog
{
    [Key, Column("Id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("UserId")] public Guid UserId { get; set; }
    [Column("RawAiResponse", TypeName = "nvarchar(max)")] public string? RawAiResponse { get; set; }
    [Column("CountryHint")] public string? CountryHint { get; set; }
    [Column("Status")] public string? Status { get; set; }
    [Column("CreatedAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Detailed diagnostics (added for bulk-train debugging) ──
    [Column("DurationMs")] public long? DurationMs { get; set; }
    [Column("HttpStatusCode")] public int? HttpStatusCode { get; set; }
    [Column("ErrorMessage", TypeName = "nvarchar(max)")] public string? ErrorMessage { get; set; }
    [Column("ErrorType")] public string? ErrorType { get; set; }
    [Column("OciResponseBody", TypeName = "nvarchar(max)")] public string? OciResponseBody { get; set; }
    [Column("ImageSizeBytes")] public int? ImageSizeBytes { get; set; }
    [Column("Source")] public string? Source { get; set; }      // "analyze" | "bulk-train" | "chat"
    [Column("AttemptNumber")] public int? AttemptNumber { get; set; }

    // ── Image integrity diagnostics ──
    [Column("ImageMimeType")] public string? ImageMimeType { get; set; }   // actual detected MIME type
    [Column("ImageHash")] public string? ImageHash { get; set; }           // SHA256 of decoded bytes
    [Column("ImageDebugPath")] public string? ImageDebugPath { get; set; } // path to saved debug file
}

/// <summary>
/// Stores verified OCR extraction results for pattern learning
/// </summary>
[Table("OcrTrainingSamples")]
public class OcrTrainingSample
{
    [Key, Column("Id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("VendorName")] public string? VendorName { get; set; }
    [Column("Country")] public string? Country { get; set; }
    [Column("Currency")] public string? Currency { get; set; }
    [Column("DocumentType")] public string? DocumentType { get; set; }
    [Column("ExtractedFields", TypeName = "nvarchar(max)")] public string? ExtractedFields { get; set; }
    [Column("FieldPositions", TypeName = "nvarchar(max)")] public string? FieldPositions { get; set; }
    [Column("Corrections", TypeName = "nvarchar(max)")] public string? Corrections { get; set; }
    [Column("ImageUrl", TypeName = "nvarchar(2048)")] public string? ImageUrl { get; set; }
    [Column("IsVerified")] public bool IsVerified { get; set; } = false;
    [Column("IsRejected")] public bool IsRejected { get; set; } = false;
    [Column("CreatedAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Stores learned structural patterns injected into OCR prompt
/// </summary>
[Table("OcrTrainingPatterns")]
public class OcrTrainingPattern
{
    [Key, Column("Id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("FieldName")] public string FieldName { get; set; } = "";
    [Column("PatternRule")] public string PatternRule { get; set; } = "";
    [Column("Country")] public string? Country { get; set; }
    [Column("Currency")] public string? Currency { get; set; }
    [Column("Confidence")] public double Confidence { get; set; } = 0;
    [Column("SourceCount")] public int SourceCount { get; set; } = 0;
    [Column("CreatedAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("UpdatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
