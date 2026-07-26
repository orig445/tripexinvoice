-- ════════════════════════════════════════════════════════════════════
-- TripEx.Api — SQL Server schema initialization
-- Idempotent: safe to run on every startup (Program.cs runs it in background,
-- splitting on GO and executing each batch). Uses IF NOT EXISTS guards so it
-- never drops or overwrites existing data.
-- ════════════════════════════════════════════════════════════════════

-- ── chat_sessions ──────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_sessions')
CREATE TABLE [dbo].[chat_sessions] (
    [id]         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]    UNIQUEIDENTIFIER NOT NULL,
    [source]     NVARCHAR(50)  NOT NULL DEFAULT 'web',
    [status]     NVARCHAR(50)  NOT NULL DEFAULT 'active',
    [created_at] DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at] DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ── chat_messages ──────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_messages')
CREATE TABLE [dbo].[chat_messages] (
    [id]         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [session_id] UNIQUEIDENTIFIER NOT NULL,
    [role]       NVARCHAR(20)   NOT NULL DEFAULT 'user',
    [content]    NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [intent]     NVARCHAR(50)   NULL,
    [metadata]   NVARCHAR(MAX)  NULL,
    [created_at] DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_chat_messages_session_id')
CREATE INDEX [IX_chat_messages_session_id] ON [dbo].[chat_messages] ([session_id]);
GO

-- ── chatbot_config ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_config')
CREATE TABLE [dbo].[chatbot_config] (
    [id]              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [bot_name]        NVARCHAR(100)  NOT NULL DEFAULT 'TripEX AI',
    [avatar_url]      NVARCHAR(2048) NULL,
    [welcome_message] NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [system_prompt]   NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [model_name]      NVARCHAR(200)  NOT NULL DEFAULT '',
    [temperature]     DECIMAL(3,2)   NOT NULL DEFAULT 0.30,
    [max_tokens]      INT            NOT NULL DEFAULT 1024,
    [is_active]       BIT            NOT NULL DEFAULT 1,
    [created_at]      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at]      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
-- Seed a single default config row if the table is empty.
IF NOT EXISTS (SELECT 1 FROM [dbo].[chatbot_config])
INSERT INTO [dbo].[chatbot_config] ([bot_name], [welcome_message], [system_prompt], [model_name], [temperature], [max_tokens], [is_active])
VALUES (
    N'TripEX AI',
    N'שלום! אני העוזר החכם של TripEx. איך אפשר לעזור?',
    N'You are a friendly, professional customer-service assistant for TripEx. Answer based on the Knowledge Base Context when it is provided.',
    N'google.gemini-2.0-flash',
    0.30, 1024, 1
);
GO

-- ── chatbot_logs ───────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_logs')
CREATE TABLE [dbo].[chatbot_logs] (
    [id]         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [session_id] UNIQUEIDENTIFIER NULL,
    [user_id]    UNIQUEIDENTIFIER NULL,
    [event_type] NVARCHAR(100)  NOT NULL DEFAULT '',
    [details]    NVARCHAR(MAX)  NULL,
    [created_at] DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ── invoices ───────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'invoices')
CREATE TABLE [dbo].[invoices] (
    [id]              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]         UNIQUEIDENTIFIER NULL,
    [invoice_number]  NVARCHAR(200)  NULL,
    [invoice_date]    DATE           NULL,
    [vendor_name]     NVARCHAR(500)  NULL,
    [vendor_address]  NVARCHAR(1000) NULL,
    [vendor_phone]    NVARCHAR(100)  NULL,
    [vendor_email]    NVARCHAR(300)  NULL,
    [vendor_id]       NVARCHAR(100)  NULL,
    [customer_name]   NVARCHAR(500)  NULL,
    [customer_address] NVARCHAR(1000) NULL,
    [currency]        NVARCHAR(10)   NULL DEFAULT 'ILS',
    [subtotal]        DECIMAL(18,2)  NULL,
    [tax_amount]      DECIMAL(18,2)  NULL,
    [total_amount]    DECIMAL(18,2)  NULL,
    [line_items]      NVARCHAR(MAX)  NULL,
    [notes]           NVARCHAR(MAX)  NULL,
    [payment_terms]   NVARCHAR(500)  NULL,
    [image_url]       NVARCHAR(2048) NULL,
    [raw_ai_response] NVARCHAR(MAX)  NULL,
    [status]          NVARCHAR(50)   NULL DEFAULT 'processed',
    [created_at]      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at]      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ── invoice_corrections ────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'invoice_corrections')
CREATE TABLE [dbo].[invoice_corrections] (
    [id]              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]         UNIQUEIDENTIFIER NOT NULL,
    [field_name]      NVARCHAR(100)  NOT NULL DEFAULT '',
    [original_value]  NVARCHAR(MAX)  NULL,
    [corrected_value] NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [context]         NVARCHAR(MAX)  NULL,
    [created_at]      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ── knowledge_documents ────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'knowledge_documents')
CREATE TABLE [dbo].[knowledge_documents] (
    [id]          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [file_name]   NVARCHAR(500)  NOT NULL DEFAULT '',
    [file_type]   NVARCHAR(200)  NOT NULL DEFAULT '',
    [file_url]    NVARCHAR(2048) NOT NULL DEFAULT '',
    [file_size]   INT            NULL,
    [uploaded_by] UNIQUEIDENTIFIER NULL,
    [status]      NVARCHAR(50)   NOT NULL DEFAULT 'pending',
    [domain]      NVARCHAR(100)  NULL,
    [doc_type]    NVARCHAR(100)  NULL,
    [description] NVARCHAR(1000) NULL,
    [created_at]  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at]  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
-- Upgrade path: add tagging columns if an older table already exists.
IF COL_LENGTH('dbo.knowledge_documents', 'domain') IS NULL
    ALTER TABLE [dbo].[knowledge_documents] ADD [domain] NVARCHAR(100) NULL;
GO
IF COL_LENGTH('dbo.knowledge_documents', 'doc_type') IS NULL
    ALTER TABLE [dbo].[knowledge_documents] ADD [doc_type] NVARCHAR(100) NULL;
GO
IF COL_LENGTH('dbo.knowledge_documents', 'description') IS NULL
    ALTER TABLE [dbo].[knowledge_documents] ADD [description] NVARCHAR(1000) NULL;
GO

-- ── knowledge_chunks ───────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'knowledge_chunks')
CREATE TABLE [dbo].[knowledge_chunks] (
    [id]          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [document_id] UNIQUEIDENTIFIER NOT NULL,
    [content]     NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [chunk_index] INT            NOT NULL DEFAULT 0,
    [created_at]  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_knowledge_chunks_document_id')
CREATE INDEX [IX_knowledge_chunks_document_id] ON [dbo].[knowledge_chunks] ([document_id]);
GO

-- ── profiles ───────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'profiles')
CREATE TABLE [dbo].[profiles] (
    [id]           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]      UNIQUEIDENTIFIER NOT NULL,
    [email]        NVARCHAR(300)  NULL,
    [display_name] NVARCHAR(300)  NULL,
    [created_at]   DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    [updated_at]   DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ── user_roles ─────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_roles')
CREATE TABLE [dbo].[user_roles] (
    [id]         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]    UNIQUEIDENTIFIER NOT NULL,
    [role]       NVARCHAR(50)   NOT NULL DEFAULT 'user',
    [created_at] DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_user_roles_user_id')
CREATE INDEX [IX_user_roles_user_id] ON [dbo].[user_roles] ([user_id]);
GO

-- ── user_credentials ───────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_credentials')
CREATE TABLE [dbo].[user_credentials] (
    [id]            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [user_id]       UNIQUEIDENTIFIER NOT NULL,
    [email]         NVARCHAR(300)  NOT NULL,
    [password_hash] NVARCHAR(500)  NOT NULL,
    [created_at]    DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_user_credentials_email')
CREATE UNIQUE INDEX [UX_user_credentials_email] ON [dbo].[user_credentials] ([email]);
GO

-- ── InvoiceScanLogs ────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'InvoiceScanLogs')
CREATE TABLE [dbo].[InvoiceScanLogs] (
    [Id]             UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [UserId]         UNIQUEIDENTIFIER NOT NULL,
    [RawAiResponse]  NVARCHAR(MAX)  NULL,
    [CountryHint]    NVARCHAR(10)   NULL,
    [Status]         NVARCHAR(50)   NULL,
    [CreatedAt]      DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    [DurationMs]     BIGINT         NULL,
    [HttpStatusCode] INT            NULL,
    [ErrorMessage]   NVARCHAR(MAX)  NULL,
    [ErrorType]      NVARCHAR(100)  NULL,
    [OciResponseBody] NVARCHAR(MAX) NULL,
    [ImageSizeBytes] INT            NULL,
    [Source]         NVARCHAR(50)   NULL,
    [AttemptNumber]  INT            NULL,
    [ImageMimeType]  NVARCHAR(50)   NULL,
    [ImageHash]      NVARCHAR(64)   NULL,
    [ImageDebugPath] NVARCHAR(1024) NULL
);
GO

-- ── OcrTrainingSamples ─────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingSamples')
CREATE TABLE [dbo].[OcrTrainingSamples] (
    [Id]              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [VendorName]      NVARCHAR(255)  NULL,
    [Country]         NVARCHAR(10)   NULL,
    [Currency]        NVARCHAR(10)   NULL,
    [DocumentType]    NVARCHAR(100)  NULL,
    [ExtractedFields] NVARCHAR(MAX)  NULL,
    [FieldPositions]  NVARCHAR(MAX)  NULL,
    [Corrections]     NVARCHAR(MAX)  NULL,
    [ImageUrl]        NVARCHAR(2048) NULL,
    [IsVerified]      BIT            NOT NULL DEFAULT 0,
    [IsRejected]      BIT            NOT NULL DEFAULT 0,
    [CreatedAt]       DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
);
GO

-- ── OcrTrainingPatterns ────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingPatterns')
CREATE TABLE [dbo].[OcrTrainingPatterns] (
    [Id]          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [FieldName]   NVARCHAR(100)  NOT NULL DEFAULT '',
    [PatternRule] NVARCHAR(MAX)  NOT NULL DEFAULT '',
    [Country]     NVARCHAR(10)   NULL,
    [Currency]    NVARCHAR(10)   NULL,
    [Confidence]  FLOAT          NOT NULL DEFAULT 0,
    [SourceCount] INT            NOT NULL DEFAULT 0,
    [CreatedAt]   DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    [UpdatedAt]   DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
);
GO

-- ════════════════════════════════════════════════════════════════════
-- RAG search function
-- CHARINDEX-based substring match with an occurrence-count rank. Chosen over
-- SQL Server Full-Text Search so it works on ANY SQL Server edition/instance
-- without requiring the Full-Text feature to be installed. Handles Hebrew and
-- other Unicode because content is NVARCHAR. ChatService.cs also issues
-- per-word queries to improve multilingual recall.
-- ════════════════════════════════════════════════════════════════════
CREATE OR ALTER FUNCTION [dbo].[search_knowledge] (@query_text NVARCHAR(4000), @max_results INT)
RETURNS TABLE
AS
RETURN
(
    SELECT TOP (@max_results)
        kc.[id]          AS chunk_id,
        kc.[document_id] AS document_id,
        kc.[content]     AS content,
        kd.[file_name]   AS file_name,
        kd.[domain]      AS domain,
        kd.[doc_type]    AS doc_type,
        kd.[description] AS description,
        (LEN(kc.[content]) - LEN(REPLACE(LOWER(kc.[content]), LOWER(@query_text), N'')))
            / NULLIF(LEN(@query_text), 0) AS rank
    FROM [dbo].[knowledge_chunks] kc
    INNER JOIN [dbo].[knowledge_documents] kd ON kd.[id] = kc.[document_id]
    WHERE kd.[status] = 'ready'
      AND @query_text IS NOT NULL
      AND LEN(@query_text) > 0
      AND CHARINDEX(LOWER(@query_text), LOWER(kc.[content])) > 0
    ORDER BY rank DESC
);
GO
