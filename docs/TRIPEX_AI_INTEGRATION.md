# TripEX AI — Integration Documentation

> Full technical guide for integrating the TripEX AI chatbot and invoice scanner into the TripEX main product.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Tech Stack](#tech-stack)
4. [API Endpoints](#api-endpoints)
   - [AI Router (Chat + OCR)](#1-ai-router--chat--ocr)
   - [Analyze Invoice (Direct OCR)](#2-analyze-invoice--direct-ocr)
5. [Authentication](#authentication)
6. [Request & Response Formats](#request--response-formats)
   - [Text Chat](#text-chat)
   - [Image Scan (via Chat)](#image-scan-via-chat)
   - [Direct Invoice Analysis](#direct-invoice-analysis)
7. [Intent System & Actions](#intent-system--actions)
8. [Conversational Flows](#conversational-flows)
9. [OCR Capabilities](#ocr-capabilities)
10. [Knowledge Base (RAG)](#knowledge-base-rag)
11. [Session Management](#session-management)
12. [Database Schema](#database-schema)
13. [Error Handling](#error-handling)
14. [Frontend Integration Example](#frontend-integration-example)
15. [Environment & Secrets](#environment--secrets)
16. [Rate Limits](#rate-limits)

---

## Overview

TripEX AI is a conversational assistant for travel and expense management. It provides:

- **Intelligent Chat** — Intent detection, multi-turn conversations, expense/travel request flows
- **Invoice/Receipt OCR** — AI-powered image scanning with structured data extraction
- **Knowledge Base (RAG)** — Answers questions based on uploaded company documents
- **Self-Learning Corrections** — Saves user corrections to improve future OCR accuracy
- **Geolocation Awareness** — Detects user location/timezone via IP for contextual responses

The AI engine is **Oracle Generative AI** using **Meta Llama 4 Maverick** (vision-capable model).

---

## Architecture

```
┌─────────────────────┐
│   TripEX Frontend   │
│   (React / Mobile)  │
└─────────┬───────────┘
          │ POST (JSON + JWT)
          ▼
┌─────────────────────┐
│     AI Router       │  ← Single entry point for ALL AI interactions
│  (Edge Function)    │
│                     │
│  • Auth validation  │
│  • Session mgmt     │
│  • Intent detection │
│  • RAG search       │
│  • Geolocation      │
│  • Correction learn │
└────┬───────────┬────┘
     │           │
     │ text      │ image
     ▼           ▼
┌─────────┐  ┌──────────────┐
│ Oracle  │  │   Analyze    │
│ Llama 4 │  │   Invoice    │
│ (Chat)  │  │ (Edge Func)  │
└─────────┘  └──────┬───────┘
                    │
                    ▼
              ┌──────────┐
              │ Oracle   │
              │ Llama 4  │
              │ (Vision) │
              └──────────┘
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| AI Model | Oracle Generative AI — Meta Llama 4 Maverick 17B (FP8) |
| AI Endpoint | `inference.generativeai.us-chicago-1.oci.oraclecloud.com` |
| Backend Functions | Supabase Edge Functions (Deno / TypeScript) |
| Database | PostgreSQL (via Supabase) |
| Auth | Supabase Auth (JWT) |
| File Storage | Supabase Storage (bucket: `invoices`) |
| Knowledge Base | Full-text search with `tsvector` + `ILIKE` fallback |

---

## API Endpoints

### 1. AI Router — Chat + OCR

**URL:** `POST https://osuyokvyhiyvyhjrbcxm.supabase.co/functions/v1/ai-router`

This is the **single entry point** for all AI interactions — text chat AND image scanning.

#### Headers

```
Authorization: Bearer <user_jwt_token>
Content-Type: application/json
apikey: <supabase_anon_key>
```

#### Request Body

```json
{
  "text": "string",           // User message (text) OR base64 image data
  "type": "text" | "image",   // "text" for chat, "image" for invoice scan
  "source": "web" | "mobile" | "widget",  // Client identifier
  "sessionToken": "uuid | null",  // Existing session ID (null = create new)
  "scope": "",                // Optional: context scope
  "trid": "",                 // Optional: Travel Request ID
  "userDate": "string",       // Optional: User's local date (e.g. "Monday, June 27, 2025")
  "userTime": "string",       // Optional: User's local time (e.g. "02:30 PM")
  "userTimezone": "string"    // Optional: User's timezone (e.g. "Asia/Jerusalem")
}
```

#### Response (Success — 200)

```json
{
  "text": "string",           // AI response text (human-readable)
  "actions": ["string"],      // Client-side actions to execute
  "redirectPage": "string",   // Page to navigate to (if applicable)
  "data": {},                 // Structured data (OCR results, etc.)
  "session_id": "uuid"        // Session ID (save for follow-up messages)
}
```

---

### 2. Analyze Invoice — Direct OCR

**URL:** `POST https://osuyokvyhiyvyhjrbcxm.supabase.co/functions/v1/analyze-invoice`

Direct invoice analysis without chat context. Used internally by AI Router, but can also be called directly for standalone OCR.

> **Note:** `verify_jwt` is not enforced on this function, but the AI Router calls it internally with auth headers.

#### Request Body

```json
{
  "imageBase64": "string",    // Base64-encoded image (with or without data: prefix)
  "imageUrl": "string"        // OR a public URL to the image
}
```
*Provide either `imageBase64` or `imageUrl`, not both.*

#### Response (Success — 200)

```json
{
  "success": true,
  "data": {
    "invoice_number": "string | null",
    "invoice_date": "YYYY-MM-DD | null",
    "total_amount": 123.45,
    "tax_amount": 12.34,
    "currency": "ILS",
    "category": "food",
    "item_count": 3,
    "vendor_name": "string | null",
    "tin": "string | null"       // TIN — only for Philippine invoices
  },
  "rawResponse": "string"       // Raw AI output for debugging
}
```

#### Response (Error)

```json
{
  "success": false,
  "error": "Error description"
}
```

---

## Authentication

All requests to `ai-router` require a valid **Supabase JWT token** in the `Authorization` header.

```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

The function:
1. Validates the JWT via `supabase.auth.getUser()`
2. Extracts the `user.id` for session and data ownership
3. Returns `401` if token is missing or invalid

### Getting a Token (Client-Side)

```typescript
import { createClient } from '@supabase/supabase-js';

const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

// Login
const { data } = await supabase.auth.signInWithPassword({
  email: 'user@example.com',
  password: 'password'
});

// The session token is automatically included in supabase.functions.invoke()
// OR get it manually:
const { data: { session } } = await supabase.auth.getSession();
const token = session?.access_token;
```

---

## Request & Response Formats

### Text Chat

**Request:**
```json
{
  "text": "I need to add an expense for lunch",
  "type": "text",
  "source": "mobile",
  "sessionToken": null
}
```

**Response:**
```json
{
  "text": "Sure! Let's add your expense. What was the description? (e.g., lunch, taxi, coffee) 🍽️",
  "actions": [],
  "redirectPage": "",
  "data": {},
  "session_id": "a1b2c3d4-..."
}
```

### Image Scan (via Chat)

**Request:**
```json
{
  "text": "data:image/jpeg;base64,/9j/4AAQ...",
  "type": "image",
  "source": "web",
  "sessionToken": "a1b2c3d4-..."
}
```

**Response:**
```json
{
  "text": "✅ Invoice scanned successfully! Here are the details:\n🏪 Vendor: McDonald's\n📂 Category: 🍽️ Food\n🔢 Invoice number: 1234\n💰 Total: 45.90 ILS\n🧾 VAT/Tax: 6.83 ILS\n📅 Date: 2025-06-27\n📋 Items: 3\n\nIs the data correct? If something is wrong, let me know and I'll update it.",
  "actions": [],
  "redirectPage": "",
  "data": {
    "invoice_number": "1234",
    "invoice_date": "2025-06-27",
    "total_amount": 45.90,
    "tax_amount": 6.83,
    "currency": "ILS",
    "category": "food",
    "item_count": 3,
    "vendor_name": "McDonald's",
    "tin": null
  },
  "session_id": "a1b2c3d4-..."
}
```

### Direct Invoice Analysis

**Request:**
```json
{
  "imageBase64": "data:image/jpeg;base64,/9j/4AAQ..."
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "invoice_number": "INV-2025-001",
    "invoice_date": "2025-06-27",
    "total_amount": 150.00,
    "tax_amount": 25.65,
    "currency": "ILS",
    "category": "office",
    "item_count": 5,
    "vendor_name": "Office Depot",
    "tin": null
  },
  "rawResponse": "{...}"
}
```

---

## Intent System & Actions

The AI Router detects user intent and maps it to client-side actions:

| Intent | Description | Actions | Client Behavior |
|--------|------------|---------|-----------------|
| `help` | User needs guidance | `[]` | Display response text |
| `scan` | User wants to scan a receipt | `["Camera"]` | Open camera/image picker |
| `expense` | User adding an expense (in progress) | `[]` | Continue conversation |
| `expense_complete` | All expense fields collected | `[]` | Save expense data |
| `bi` | User wants reports/analytics | `["DisplayResults"]` | Show charts/data view |
| `online` | User wants to book travel (in progress) | `[]` | Continue conversation |
| `online_complete` | All booking fields collected | `[]` | Submit travel request |
| `general` | General conversation | `[]` | Display response text |

### Action Types

| Action | Description |
|--------|------------|
| `Camera` | Client should open camera or image upload dialog |
| `DisplayResults` | Client should display data visualization |
| `Redirect` | Client should navigate to `redirectPage` |
| `AddExpense` | Client should save expense to system |

---

## Conversational Flows

### Expense Flow
The AI guides users through collecting:
1. **Description** — e.g., "lunch", "taxi"
2. **Amount** — numeric value
3. **Currency** — ILS, USD, EUR, etc.
4. **Date** — when the expense occurred
5. **Category** — food, transport, hotel, etc.

When all fields are collected → intent changes to `expense_complete` with a formatted summary.

### Travel Request Flow
The AI guides users through:
1. **Destination**
2. **Departure date**
3. **Return date**
4. **Number of passengers**
5. **Special notes** (optional)

When all fields are collected → intent changes to `online_complete`.

### OCR Correction Flow
After scanning an invoice:
1. AI presents extracted data summary
2. User says something is wrong → AI acknowledges and asks what to fix
3. User provides correction → AI shows updated summary
4. User confirms → Data is finalized

Corrections are **saved to `invoice_corrections` table** for future learning.

---

## OCR Capabilities

The invoice scanner extracts:

| Field | Type | Description |
|-------|------|-------------|
| `invoice_number` | string | Document/receipt number |
| `invoice_date` | YYYY-MM-DD | Date printed on document |
| `total_amount` | number | Amount due (what customer owes) |
| `tax_amount` | number | VAT/tax amount |
| `currency` | string | ISO currency code (ILS, USD, EUR, PHP, etc.) |
| `category` | string | Auto-classified: food, transport, hotel, office, telecom, entertainment, health, shopping, other |
| `item_count` | number | Number of line items |
| `vendor_name` | string | Business/vendor name |
| `tin` | string | Taxpayer ID — **Philippine invoices only** |

### Supported Image Formats
- JPEG, PNG, WebP
- Base64-encoded or public URL
- Max recommended size: ~4MB

### Smart Extraction Features
- **Date normalization** — Handles DD/MM/YYYY, MM/DD/YYYY, Hebrew dates, etc. → always outputs YYYY-MM-DD
- **Amount disambiguation** — Distinguishes Total vs Cash/Change amounts
- **Currency auto-detection** — From symbols (₪, $, €, ₱) or country context
- **Category auto-classification** — Based on vendor type and items
- **Self-learning corrections** — Past user corrections influence future extractions

---

## Knowledge Base (RAG)

The AI searches an internal knowledge base before responding:

1. Documents are uploaded via admin panel → stored in `knowledge_documents`
2. Documents are chunked and indexed in `knowledge_chunks` with `tsvector`
3. On each user query, the system:
   - Full-text search with `plainto_tsquery`
   - `ILIKE` fallback for better Hebrew/multilingual matching
   - Individual word search for compound queries
4. Top 5 relevant chunks are injected into the AI system prompt

### Database Function

```sql
SELECT * FROM search_knowledge('query text', 5);
-- Returns: chunk_id, document_id, content, file_name, rank
```

---

## Session Management

- Sessions are stored in `chat_sessions` table
- Each session has a unique UUID
- To **start a new session**: send `sessionToken: null` — the API returns a new `session_id`
- To **continue a session**: send `sessionToken: "<previous_session_id>"`
- The AI loads up to **30 recent messages** from the session for context
- Sessions track: `user_id`, `source`, `status`, `created_at`, `updated_at`

### Session Flow

```
1. First message → sessionToken: null
2. Response includes session_id: "abc-123"
3. Next message → sessionToken: "abc-123"
4. ... continues in same conversation
5. New conversation → sessionToken: null (creates new session)
```

---

## Database Schema

### chat_sessions
| Column | Type | Description |
|--------|------|-------------|
| id | UUID (PK) | Session identifier |
| user_id | UUID | Owner |
| source | text | "web", "mobile", "widget" |
| status | text | "active" |
| created_at | timestamptz | Session start |
| updated_at | timestamptz | Last activity |

### chat_messages
| Column | Type | Description |
|--------|------|-------------|
| id | UUID (PK) | Message ID |
| session_id | UUID (FK) | Parent session |
| role | text | "user", "assistant", "system" |
| content | text | Message text |
| intent | text | Detected intent |
| metadata | jsonb | `{ actions: [], scanned_data: {...} }` |
| created_at | timestamptz | Timestamp |

### invoices
| Column | Type | Description |
|--------|------|-------------|
| id | UUID (PK) | Invoice ID |
| user_id | UUID | Owner |
| invoice_number | text | Document number |
| invoice_date | date | Document date |
| total_amount | numeric | Total |
| tax_amount | numeric | VAT/tax |
| subtotal | numeric | Before tax |
| currency | text | ISO code |
| vendor_name | text | Business name |
| vendor_id | text | Business ID / TIN |
| customer_name | text | Customer |
| image_url | text | Scanned image URL |
| raw_ai_response | text | Raw AI output |
| status | text | "processed" |
| line_items | jsonb | Individual items |
| notes | text | Additional notes |
| created_at / updated_at | timestamptz | Timestamps |

### invoice_corrections
| Column | Type | Description |
|--------|------|-------------|
| id | UUID (PK) | Correction ID |
| user_id | UUID | Who corrected |
| field_name | text | Which field was wrong |
| original_value | text | AI's original value |
| corrected_value | text | User's correction |
| context | text | Vendor name / invoice # for context |
| created_at | timestamptz | When corrected |

### chatbot_config
| Column | Type | Description |
|--------|------|-------------|
| id | UUID (PK) | Config ID |
| bot_name | text | Display name |
| avatar_url | text | Bot avatar |
| welcome_message | text | First message |
| system_prompt | text | AI system prompt |
| model_name | text | Oracle model ID |
| temperature | numeric | AI temperature |
| max_tokens | integer | Max response length |
| is_active | boolean | Enabled flag |

---

## Error Handling

| HTTP Status | Meaning | Response |
|------------|---------|----------|
| 200 | Success | Normal response |
| 401 | Unauthorized | `{ "error": "Missing authorization" }` or `{ "error": "Unauthorized" }` |
| 429 | Rate limited | `{ "error": "Rate limit exceeded" }` |
| 500 | Server error | `{ "error": "Error description" }` |

For OCR errors within a 200 response (when AI Router handles gracefully):
```json
{
  "actions": [],
  "text": "Failed to scan receipt. Please try again.",
  "redirectPage": "",
  "data": {},
  "session_id": "..."
}
```

---

## Frontend Integration Example

### Using Supabase JS Client (Recommended)

```typescript
import { createClient } from '@supabase/supabase-js';

const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

// Text message
async function sendMessage(text: string, sessionId: string | null) {
  const { data, error } = await supabase.functions.invoke('ai-router', {
    body: {
      text,
      type: 'text',
      source: 'mobile',   // or 'web', 'widget'
      sessionToken: sessionId,
      userDate: new Date().toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' }),
      userTime: new Date().toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }),
      userTimezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    },
  });

  if (error) throw error;
  
  // data.text        → AI response to display
  // data.actions     → Client actions to execute
  // data.session_id  → Save for next message
  // data.data        → Structured data (OCR results, etc.)
  
  return data;
}

// Image scan
async function scanInvoice(base64Image: string, sessionId: string | null) {
  const { data, error } = await supabase.functions.invoke('ai-router', {
    body: {
      text: base64Image,
      type: 'image',
      source: 'mobile',
      sessionToken: sessionId,
    },
  });

  if (error) throw error;
  return data;
}
```

### Using Raw HTTP (for non-JS clients)

```bash
curl -X POST \
  'https://osuyokvyhiyvyhjrbcxm.supabase.co/functions/v1/ai-router' \
  -H 'Authorization: Bearer <JWT_TOKEN>' \
  -H 'apikey: <SUPABASE_ANON_KEY>' \
  -H 'Content-Type: application/json' \
  -d '{
    "text": "Hello, I need help with my expenses",
    "type": "text",
    "source": "mobile",
    "sessionToken": null
  }'
```

### Handling Actions (Client-Side)

```typescript
const response = await sendMessage(userText, sessionId);

// Process actions
for (const action of response.actions) {
  switch (action) {
    case 'Camera':
      openCameraOrImagePicker();
      break;
    case 'DisplayResults':
      showDataVisualization(response.data);
      break;
    case 'Redirect':
      navigateTo(response.redirectPage);
      break;
  }
}

// Display AI response
displayMessage(response.text);

// Save session for continuity
sessionId = response.session_id;
```

---

## Environment & Secrets

| Secret Name | Description | Required By |
|------------|-------------|-------------|
| `oracleapikey_2` | Oracle Generative AI API key (US Chicago region) | ai-router, analyze-invoice |
| `SUPABASE_URL` | Supabase project URL | All functions |
| `SUPABASE_ANON_KEY` | Supabase anon/public key | All functions |
| `SUPABASE_SERVICE_ROLE_KEY` | Supabase service role key (for corrections) | ai-router |

### Oracle AI Endpoint

```
Base URL: https://inference.generativeai.us-chicago-1.oci.oraclecloud.com
Path:     /20231130/actions/v1/chat/completions
Model:    meta.llama-4-maverick-17b-128e-instruct-fp8
Auth:     Bearer <oracleapikey_2>
```

---

## Rate Limits

- Oracle AI: Subject to OCI rate limits (returns HTTP 429)
- The system handles 429 gracefully and returns a user-friendly error
- Recommended: Implement client-side retry with exponential backoff

---

## CORS Configuration

Both edge functions include CORS headers for cross-origin access:

```javascript
const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type, ...',
};
```

Both handle `OPTIONS` preflight requests automatically.

---

## Admin Panel

The system includes an admin panel at `/admin/chatbot` with:

- **Settings** — Configure bot name, avatar, system prompt, model, temperature
- **Knowledge Base** — Upload documents for RAG
- **Sessions** — View all chat sessions
- **Logs** — View chatbot event logs

Access requires the `admin` role in `user_roles` table.

---

*Last updated: March 2026*
*Version: 1.0*
