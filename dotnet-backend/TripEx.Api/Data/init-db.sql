-- ═══════════════════════════════════════
-- TripEx SQL Server Database Init Script
-- ═══════════════════════════════════════

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'profiles')
CREATE TABLE profiles (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    email NVARCHAR(255) NULL,
    display_name NVARCHAR(255) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_roles')
CREATE TABLE user_roles (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    role NVARCHAR(50) NOT NULL DEFAULT 'user',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_credentials')
CREATE TABLE user_credentials (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    email NVARCHAR(255) NOT NULL,
    password_hash NVARCHAR(500) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_user_credentials_email')
    CREATE UNIQUE INDEX IX_user_credentials_email ON user_credentials(email);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_sessions')
CREATE TABLE chat_sessions (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NOT NULL,
    source NVARCHAR(50) NOT NULL DEFAULT 'web',
    status NVARCHAR(50) NOT NULL DEFAULT 'active',
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chat_messages')
CREATE TABLE chat_messages (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    session_id UNIQUEIDENTIFIER NOT NULL,
    role NVARCHAR(50) NOT NULL DEFAULT 'user',
    content NVARCHAR(MAX) NOT NULL DEFAULT '',
    intent NVARCHAR(100) NULL,
    metadata NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_config')
CREATE TABLE chatbot_config (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    bot_name NVARCHAR(100) NOT NULL DEFAULT 'TripEX AI',
    avatar_url NVARCHAR(500) NULL,
    welcome_message NVARCHAR(MAX) NOT NULL DEFAULT '',
    system_prompt NVARCHAR(MAX) NOT NULL DEFAULT '',
    model_name NVARCHAR(200) NOT NULL DEFAULT '',
    temperature DECIMAL(3,2) NOT NULL DEFAULT 0.3,
    max_tokens INT NOT NULL DEFAULT 1024,
    is_active BIT NOT NULL DEFAULT 1,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    updated_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'chatbot_logs')
CREATE TABLE chatbot_logs (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    session_id UNIQUEIDENTIFIER NULL,
    user_id UNIQUEIDENTIFIER NULL,
    event_type NVARCHAR(100) NOT NULL DEFAULT '',
    details NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

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

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'knowledge_chunks')
CREATE TABLE knowledge_chunks (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    document_id UNIQUEIDENTIFIER NOT NULL,
    content NVARCHAR(MAX) NOT NULL DEFAULT '',
    chunk_index INT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

PRINT 'All tables created successfully.';
