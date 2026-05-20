-- ═══════════════════════════════════════════════════════════
-- TripEx SQL Server - Full Database Initialization Script
-- Run this once against your SQL Server database
-- ═══════════════════════════════════════════════════════════

-- ── 1. Profiles ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'profiles')
CREATE TABLE profiles (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    email NVARCHAR(255) NULL,
    display_name NVARCHAR(255) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 2. User Roles ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_roles')
CREATE TABLE user_roles (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    role NVARCHAR(50) NOT NULL DEFAULT 'user',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 3. User Credentials ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_credentials')
CREATE TABLE user_credentials (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    email NVARCHAR(255) NOT NULL,
    password_hash NVARCHAR(500) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 4. Chat Sessions ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_sessions')
CREATE TABLE chat_sessions (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    source NVARCHAR(50) NOT NULL DEFAULT 'web',
    status NVARCHAR(50) NOT NULL DEFAULT 'active',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 5. Chat Messages ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_messages')
CREATE TABLE chat_messages (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    session_id UNIQUEIDENTIFIER NOT NULL,
    role NVARCHAR(50) NOT NULL DEFAULT 'user',
    content NVARCHAR(MAX) NOT NULL DEFAULT '',
    intent NVARCHAR(100) NULL,
    metadata NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_chat_messages_session FOREIGN KEY (session_id) REFERENCES chat_sessions(id) ON DELETE CASCADE
);
GO

-- ── 6. Chatbot Config ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_config')
CREATE TABLE chatbot_config (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    bot_name NVARCHAR(100) NOT NULL DEFAULT 'TripEX AI',
    avatar_url NVARCHAR(500) NULL,
    welcome_message NVARCHAR(MAX) NOT NULL DEFAULT N'שלום! אני TripEX AI, העוזר האישי שלך לניהול נסיעות והוצאות. איך אוכל לעזור?',
    system_prompt NVARCHAR(MAX) NOT NULL DEFAULT '',
    model_name NVARCHAR(200) NOT NULL DEFAULT 'google.gemini-3-flash',
    temperature DECIMAL(3,2) NOT NULL DEFAULT 0.3,
    max_tokens INT NOT NULL DEFAULT 1024,
    is_active BIT NOT NULL DEFAULT 1,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 7. Chatbot Logs ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_logs')
CREATE TABLE chatbot_logs (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    session_id UNIQUEIDENTIFIER NULL,
    user_id UNIQUEIDENTIFIER NULL,
    event_type NVARCHAR(100) NOT NULL DEFAULT '',
    details NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 8. Invoices ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'invoices')
CREATE TABLE invoices (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NULL,
    invoice_number NVARCHAR(100) NULL,
    invoice_date DATE NULL,
    vendor_name NVARCHAR(255) NULL,
    vendor_address NVARCHAR(500) NULL,
    vendor_phone NVARCHAR(50) NULL,
    vendor_email NVARCHAR(255) NULL,
    vendor_id NVARCHAR(100) NULL,
    customer_name NVARCHAR(255) NULL,
    customer_address NVARCHAR(500) NULL,
    currency NVARCHAR(10) NULL DEFAULT 'ILS',
    subtotal DECIMAL(18,2) NULL,
    tax_amount DECIMAL(18,2) NULL,
    total_amount DECIMAL(18,2) NULL,
    line_items NVARCHAR(MAX) NULL,
    notes NVARCHAR(MAX) NULL,
    payment_terms NVARCHAR(255) NULL,
    image_url NVARCHAR(1000) NULL,
    raw_ai_response NVARCHAR(MAX) NULL,
    status NVARCHAR(50) NULL DEFAULT 'processed',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 9. Invoice Corrections ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'invoice_corrections')
CREATE TABLE invoice_corrections (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    field_name NVARCHAR(100) NOT NULL DEFAULT '',
    original_value NVARCHAR(MAX) NULL,
    corrected_value NVARCHAR(MAX) NOT NULL DEFAULT '',
    context NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 10. Knowledge Documents ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'knowledge_documents')
CREATE TABLE knowledge_documents (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    file_name NVARCHAR(255) NOT NULL DEFAULT '',
    file_type NVARCHAR(50) NOT NULL DEFAULT '',
    file_url NVARCHAR(1000) NOT NULL DEFAULT '',
    file_size INT NULL,
    uploaded_by UNIQUEIDENTIFIER NULL,
    status NVARCHAR(50) NOT NULL DEFAULT 'pending',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- ── 11. Knowledge Chunks ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'knowledge_chunks')
CREATE TABLE knowledge_chunks (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    document_id UNIQUEIDENTIFIER NOT NULL,
    content NVARCHAR(MAX) NOT NULL DEFAULT '',
    chunk_index INT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_knowledge_chunks_document FOREIGN KEY (document_id) REFERENCES knowledge_documents(id) ON DELETE CASCADE
);
GO

-- ── 12. InvoiceScanLogs (NEW — for OCR debugging & support) ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'InvoiceScanLogs')
CREATE TABLE [dbo].[InvoiceScanLogs] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [RawAiResponse] NVARCHAR(MAX) NULL,
    [CountryHint] NVARCHAR(10) NULL,
    [Status] NVARCHAR(50) NULL,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);
GO

-- ── 12b. InvoiceScanLogs — additive diagnostic columns (idempotent) ──
IF COL_LENGTH('dbo.InvoiceScanLogs', 'DurationMs') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [DurationMs] BIGINT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'HttpStatusCode') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [HttpStatusCode] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ErrorMessage') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ErrorMessage] NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ErrorType') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ErrorType] NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'OciResponseBody') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [OciResponseBody] NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageSizeBytes') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageSizeBytes] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'Source') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [Source] NVARCHAR(50) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'AttemptNumber') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [AttemptNumber] INT NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageMimeType') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageMimeType] NVARCHAR(50) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageHash') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageHash] NVARCHAR(64) NULL;
IF COL_LENGTH('dbo.InvoiceScanLogs', 'ImageDebugPath') IS NULL
    ALTER TABLE [dbo].[InvoiceScanLogs] ADD [ImageDebugPath] NVARCHAR(1024) NULL;
GO

-- ── 13. OCR Training Samples ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingSamples')
CREATE TABLE [dbo].[OcrTrainingSamples] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [VendorName] NVARCHAR(255) NULL,
    [Country] NVARCHAR(10) NULL,
    [Currency] NVARCHAR(10) NULL,
    [DocumentType] NVARCHAR(100) NULL,
    [ExtractedFields] NVARCHAR(MAX) NULL,
    [FieldPositions] NVARCHAR(MAX) NULL,
    [Corrections] NVARCHAR(MAX) NULL,
    [ImageUrl] NVARCHAR(2048) NULL,
    [IsVerified] BIT NOT NULL DEFAULT 0,
    [IsRejected] BIT NOT NULL DEFAULT 0,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);
IF COL_LENGTH('dbo.OcrTrainingSamples', 'ImageUrl') IS NULL
    ALTER TABLE [dbo].[OcrTrainingSamples] ADD [ImageUrl] NVARCHAR(2048) NULL;
GO

-- ── 14. OCR Training Patterns ──
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OcrTrainingPatterns')
CREATE TABLE [dbo].[OcrTrainingPatterns] (
    [Id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [FieldName] NVARCHAR(100) NOT NULL DEFAULT '',
    [PatternRule] NVARCHAR(MAX) NOT NULL DEFAULT '',
    [Country] NVARCHAR(10) NULL,
    [Currency] NVARCHAR(10) NULL,
    [Confidence] FLOAT NOT NULL DEFAULT 0,
    [SourceCount] INT NOT NULL DEFAULT 0,
    [CreatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    [UpdatedAt] DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);
GO

-- ═══════════════════════════════════════════════════════════
-- Indexes
-- ═══════════════════════════════════════════════════════════

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_user_credentials_email')
    CREATE UNIQUE INDEX IX_user_credentials_email ON user_credentials(email);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_profiles_user_id')
    CREATE INDEX IX_profiles_user_id ON profiles(user_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_user_roles_user_id')
    CREATE INDEX IX_user_roles_user_id ON user_roles(user_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_chat_sessions_user_id')
    CREATE INDEX IX_chat_sessions_user_id ON chat_sessions(user_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_chat_messages_session_id')
    CREATE INDEX IX_chat_messages_session_id ON chat_messages(session_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_invoices_user_id')
    CREATE INDEX IX_invoices_user_id ON invoices(user_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_invoice_corrections_user_id')
    CREATE INDEX IX_invoice_corrections_user_id ON invoice_corrections(user_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_knowledge_chunks_document_id')
    CREATE INDEX IX_knowledge_chunks_document_id ON knowledge_chunks(document_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_chatbot_logs_session_id')
    CREATE INDEX IX_chatbot_logs_session_id ON chatbot_logs(session_id);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_InvoiceScanLogs_UserId')
    CREATE INDEX IX_InvoiceScanLogs_UserId ON [InvoiceScanLogs]([UserId]);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_InvoiceScanLogs_CreatedAt')
    CREATE INDEX IX_InvoiceScanLogs_CreatedAt ON [InvoiceScanLogs]([CreatedAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_OcrTrainingSamples_Country')
    CREATE INDEX IX_OcrTrainingSamples_Country ON [OcrTrainingSamples]([Country], [IsVerified]);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_OcrTrainingPatterns_Country')
    CREATE INDEX IX_OcrTrainingPatterns_Country ON [OcrTrainingPatterns]([Country], [FieldName]);
GO

-- ═══════════════════════════════════════════════════════════
-- Seed default chatbot config (if empty)
-- ═══════════════════════════════════════════════════════════
IF NOT EXISTS (SELECT 1 FROM chatbot_config)
BEGIN
    INSERT INTO chatbot_config (id, bot_name, welcome_message, system_prompt, model_name)
    VALUES (
        NEWID(),
        'TripEX AI',
        N'שלום! אני TripEX AI, העוזר האישי שלך לניהול נסיעות והוצאות. איך אוכל לעזור?',
        N'You are TripEX AI, a Personal Assistant for Travel & Expense Management.
Detect user intent and respond accordingly:
- Help/guidance -> respond with JSON: {"intent": "help", "action": "none", "text": "<your helpful response>"}
- Scan receipt -> respond with JSON: {"intent": "scan", "action": "camera", "text": "<your response>"}
- Analyze data (BI) -> respond with JSON: {"intent": "bi", "action": "none", "text": "<your response>"}
- Online booking -> respond with JSON: {"intent": "online", "action": "redirect", "text": "<your response>"}
- Manage expenses -> respond with JSON: {"intent": "expense", "action": "redirect", "text": "<your response>"}
- General chat -> respond with JSON: {"intent": "general", "action": "none", "text": "<your natural response>"}
Always respond in the user''s language. Always return valid JSON with: intent, action, text.',
        'google.gemini-3-flash'
    );
END
GO

PRINT '══════════════════════════════════════════';
PRINT ' TripEx Database initialized successfully!';
PRINT '══════════════════════════════════════════';
