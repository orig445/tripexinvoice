# TripEX AI — C# .NET Backend Specification

> Complete specification for building the TripEX AI backend as a C# ASP.NET Core Web API.

---

## Table of Contents

1. [Project Setup](#project-setup)
2. [API Endpoints](#api-endpoints)
3. [Data Models](#data-models)
4. [Authentication](#authentication)
5. [Business Logic](#business-logic)
6. [Database Schema](#database-schema)
7. [External Services](#external-services)
8. [Environment Variables](#environment-variables)

---

## Project Setup

```bash
dotnet new webapi -n TripEx.Api
cd TripEx.Api
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package Swashbuckle.AspNetCore
```

### Program.cs Essentials

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = Environment.GetEnvironmentVariable("SUPABASE_URL") + "/auth/v1";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Environment.GetEnvironmentVariable("SUPABASE_URL") + "/auth/v1",
            ValidateAudience = false,
            ValidateLifetime = true,
        };
    });
```

---

## API Endpoints

### 1. POST `/api/chat`

Main entry point for chat and image scanning.

**Request Body:**

```csharp
public class ChatRequest
{
    public string Text { get; set; } = "";          // User message OR base64 image
    public string Type { get; set; } = "text";      // "text" | "image"
    public string Source { get; set; } = "web";      // "web" | "mobile" | "widget"
    public string? SessionToken { get; set; }        // Existing session ID (null = new)
    public string? Scope { get; set; }
    public string? Trid { get; set; }                // Travel Request ID
    public string? UserDate { get; set; }
    public string? UserTime { get; set; }
    public string? UserTimezone { get; set; }
}
```

**Response Body:**

```csharp
public class ChatResponse
{
    public string Text { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    public string RedirectPage { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
    public string SessionId { get; set; } = "";
}
```

**Logic Flow:**

1. Validate JWT → extract `user_id`
2. Create or retrieve session from `chat_sessions` table
3. If `type == "image"` → call `POST /api/invoice/analyze` internally
4. If `type == "text"`:
   a. Save user message to `chat_messages`
   b. Load last 30 messages from session
   c. Search knowledge base (RAG)
   d. Build system prompt (see [System Prompt](#system-prompt))
   e. Call Oracle AI
   f. Parse JSON response → extract `intent` and `text`
   g. Map intent to actions (see [Intent Mapping](#intent-mapping))
   h. Save assistant message to `chat_messages`
   i. Return response

---

### 2. POST `/api/invoice/analyze`

Direct invoice OCR without chat context.

**Request Body:**

```csharp
public class AnalyzeInvoiceRequest
{
    public string? ImageBase64 { get; set; }   // Base64-encoded image
    public string? ImageUrl { get; set; }       // OR public image URL
}
```

**Response Body:**

```csharp
public class AnalyzeInvoiceResponse
{
    public bool Success { get; set; }
    public InvoiceData? Data { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
}

public class InvoiceData
{
    public string? InvoiceNumber { get; set; }
    public string? InvoiceDate { get; set; }        // YYYY-MM-DD
    public string? DocumentType { get; set; }
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
```

**Logic:**
1. Send image to Oracle AI (Vision model) with extraction prompt
2. Parse structured JSON from AI response
3. Load recent corrections from `invoice_corrections` for few-shot learning
4. Return extracted data

---

### 3. POST `/api/knowledge/process`

Trigger processing of an uploaded knowledge document.

**Request Body:**

```csharp
public class ProcessKnowledgeRequest
{
    public string DocumentId { get; set; } = "";
}
```

**Response:** `200 OK` with `{ "success": true }`

**Logic:**
1. Download file from storage (Supabase bucket `knowledge`)
2. Extract text (PDF → text, image → OCR, etc.)
3. Split into chunks (~500 chars each)
4. Insert into `knowledge_chunks` table with `tsvector` for search
5. Update document status to `"ready"`

---

## Authentication

Use Supabase JWT tokens. The frontend sends `Authorization: Bearer <token>`.

Validate using Supabase's JWT secret or JWKS endpoint:
- JWKS: `{SUPABASE_URL}/auth/v1/.well-known/jwks.json`
- Extract `sub` claim as `user_id`

---

## Intent Mapping

```csharp
private static readonly Dictionary<string, (List<string> Actions, string? RedirectPage)> IntentMap = new()
{
    ["help"]             = (new(), null),
    ["scan"]             = (new() { "Camera" }, null),
    ["expense"]          = (new(), null),
    ["expense_complete"] = (new(), null),
    ["bi"]               = (new() { "DisplayResults" }, null),
    ["online"]           = (new(), null),
    ["online_complete"]  = (new(), null),
    ["general"]          = (new(), null),
};
```

---

## System Prompt

The system prompt is stored in `chatbot_config` table and can be edited via admin panel. Default:

```
You are Milo 🦊 — a friendly, professional customer service assistant for TripEX...
```

Full prompt available in the `ai-router` Edge Function source code (`supabase/functions/ai-router/index.ts`, line ~316).

---

## External Services

### Oracle Generative AI

**Endpoint:** `https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions`

**Model:** `meta.llama-4-maverick-17b-128e-instruct-fp8`

**Auth:** Bearer token (API key stored as `oracleapikey_2`)

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oracleApiKey);

var body = new
{
    model = modelName,
    messages = messageList,
    max_tokens = maxTokens,
    temperature = temperature
};

var response = await client.PostAsJsonAsync(ORACLE_ENDPOINT, body);
```

### IP Geolocation

**Endpoint:** `http://ip-api.com/json/{ip}?fields=status,country,city,regionName,timezone,lat,lon&lang=he`

Used to detect user location/timezone for contextual responses.

---

## Database Schema

### Tables (PostgreSQL)

All tables are in `public` schema. Connect to the existing Supabase PostgreSQL database.

| Table | Purpose |
|-------|---------|
| `chat_sessions` | Conversation sessions |
| `chat_messages` | Individual messages |
| `chatbot_config` | Bot settings (name, prompt, model) |
| `chatbot_logs` | Event logging |
| `invoices` | Processed invoices |
| `invoice_corrections` | User corrections for learning |
| `knowledge_documents` | Uploaded knowledge files |
| `knowledge_chunks` | Chunked text with tsvector |
| `profiles` | User profiles |
| `user_roles` | Role-based access (admin/user) |

### Key Database Function

```sql
SELECT * FROM search_knowledge('query text', 5);
-- Returns: chunk_id, document_id, content, file_name, rank
```

Use this for RAG (knowledge base search).

---

## Environment Variables

```env
# Database
DATABASE_URL=postgresql://postgres:[password]@[host]:5432/postgres

# Supabase (for JWT validation)
SUPABASE_URL=https://osuyokvyhiyvyhjrbcxm.supabase.co
SUPABASE_ANON_KEY=eyJhbGciOi...

# Oracle AI
ORACLE_API_KEY=[your Oracle API key]
ORACLE_AI_ENDPOINT=https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions
ORACLE_MODEL=meta.llama-4-maverick-17b-128e-instruct-fp8

# Storage
SUPABASE_SERVICE_ROLE_KEY=[for storage access]
```

---

## RAG (Knowledge Base Search)

```csharp
// Use Npgsql to call the search function
using var cmd = new NpgsqlCommand("SELECT * FROM search_knowledge(@query, @max)", conn);
cmd.Parameters.AddWithValue("query", userText);
cmd.Parameters.AddWithValue("max", 5);

var reader = await cmd.ExecuteReaderAsync();
var chunks = new List<string>();
while (await reader.ReadAsync())
{
    chunks.Add($"[{reader.GetString("file_name")}]: {reader.GetString("content")}");
}

// Inject into system prompt
var knowledgeContext = chunks.Count > 0
    ? "\n\n## Knowledge Base Context:\n" + string.Join("\n\n", chunks)
    : "";
```

---

## Self-Learning (Invoice Corrections)

Load recent corrections and inject as few-shot examples:

```csharp
var corrections = await dbContext.InvoiceCorrections
    .OrderByDescending(c => c.CreatedAt)
    .Take(50)
    .ToListAsync();

// Add to extraction prompt as examples
```

---

## Folder Structure Suggestion

```
TripEx.Api/
├── Controllers/
│   ├── ChatController.cs         # POST /api/chat
│   ├── InvoiceController.cs      # POST /api/invoice/analyze
│   └── KnowledgeController.cs    # POST /api/knowledge/process
├── Services/
│   ├── OracleAiService.cs        # Oracle AI calls
│   ├── ChatService.cs            # Session mgmt, RAG, intent
│   ├── InvoiceService.cs         # OCR logic
│   ├── KnowledgeService.cs       # Document processing
│   └── GeolocationService.cs     # IP-based geolocation
├── Models/
│   ├── ChatModels.cs
│   ├── InvoiceModels.cs
│   └── KnowledgeModels.cs
├── Data/
│   └── TripExDbContext.cs
├── Program.cs
└── appsettings.json
```
