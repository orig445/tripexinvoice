# TripEx AI — TAS Integration & Production Deployment Guide

> How to deploy the **TripEx.Api** C# backend into the TripEx TAS system, load the
> knowledge base (RAG), and run it in production. This is the authoritative guide;
> it reflects the code as it actually is (SQL Server + Oracle Generative AI).

---

## 1. What this backend is

A **standalone ASP.NET Core 8 Web API** (`dotnet-backend/TripEx.Api`). No Supabase
dependency at runtime.

| Concern | Technology |
|---------|-----------|
| Runtime | ASP.NET Core 8 (`net8.0`) |
| Database | **SQL Server** (`Microsoft.Data.SqlClient` / EF Core `UseSqlServer`) |
| AI engine | **Oracle Generative AI** (OCI) — chat + vision |
| RAG search | `dbo.search_knowledge` T-SQL function (no Full-Text feature required) |
| Auth | JWT (HMAC-SHA256) **or** static API key (`X-Api-Key`) |
| File storage | Local filesystem or S3-compatible |
| Hosting | IIS in-process (`web.config` included) or Kestrel |

---

## 2. Prerequisites on the TAS host

1. **.NET 8 Hosting Bundle** (ASP.NET Core Runtime 8 + IIS module `AspNetCoreModuleV2`).
   - The project targets `net8.0`. The build machine may use a newer SDK, but the
     **runtime on the server must include .NET 8** (or be configured to roll forward).
2. **SQL Server** reachable from the host (any edition — Express/Standard/Azure SQL).
   No Full-Text Search feature is needed.
3. **Oracle Generative AI** API key + endpoint (OCI, e.g. `us-chicago-1`).
4. (Optional) An S3 bucket if you prefer S3 storage over local disk.

---

## 3. Database setup

The schema is defined in **`TripEx.Api/Data/init-db.sql`** and runs **automatically**
on startup (in a background task — see `Program.cs`). It is **idempotent** (`IF NOT
EXISTS` guards) and safe to run repeatedly; it never drops data.

You can also apply it manually before first run:

```bash
sqlcmd -S <host> -d tripex -U <user> -P <password> -i TripEx.Api/Data/init-db.sql
```

It creates all 14 tables, indexes, a seed `chatbot_config` row, and the
`dbo.search_knowledge` RAG function.

> **RAG search implementation note:** `search_knowledge` uses a `CHARINDEX`-based
> substring match with an occurrence-count rank. This was chosen over SQL Server
> Full-Text Search so it works on **any** SQL Server instance without installing the
> Full-Text feature, and it handles Hebrew/Unicode because all content is `NVARCHAR`.

---

## 4. Configuration

Edit `appsettings.json` **or** set environment variables (env vars win in containers/IIS).

| appsettings key | Env var | Required | Notes |
|-----------------|---------|----------|-------|
| `ConnectionStrings:DefaultConnection` | `DATABASE_URL` | ✅ | SQL Server connection string |
| `Jwt:Secret` | `JWT_SECRET` | ✅ | HMAC key, **min 32 chars** |
| `Jwt:Issuer` | — | | default `TripEx.Api` |
| `Jwt:Audience` | — | | default `TripEx.Client` |
| `Jwt:ExpirationHours` | — | | default 24 |
| `ApiKey:Key` | `API_KEY` | | static key for `X-Api-Key` auth; empty = disabled |
| `Oracle:ApiKey` | `ORACLE_API_KEY` | ✅ | Oracle GenAI key |
| `Oracle:Endpoint` | — | ✅ | OCI inference chat endpoint |
| `Oracle:Model` | — | ✅ | e.g. `google.gemini-2.0-flash` or `meta.llama-4-maverick-...` |
| `Storage:Provider` | `STORAGE_PROVIDER` | | `local` (default) or `s3` |
| `Storage:LocalPath` | `STORAGE_PATH` | | default `./storage` |
| `Storage:S3:*` | `S3_*` | | bucket/keys/region/endpoint for S3 |

> **Never commit real secrets.** The checked-in `appsettings.json` contains only
> placeholders (`YOUR_...`). Use env vars or a secret store in TAS.

---

## 5. Authentication — choose ONE path

All business endpoints require auth. `Program.cs` wires a **MultiAuth** scheme:

- If the request has an **`X-Api-Key`** header → validated against `ApiKey:Key`.
- Otherwise → treated as a **JWT Bearer** token, validated against `Jwt:Secret`
  with issuer/audience `TripEx.Api` / `TripEx.Client`.

### Option A — Static API key (simplest for server-to-server / internal admin)
Set `ApiKey:Key`, and send `X-Api-Key: <key>` on each request. Good for the TAS
backend calling this API, or an internal admin tool.

### Option B — JWT
Call `POST /api/auth/login` (or `/register`) to obtain a token signed by this API,
then send `Authorization: Bearer <token>`.

> ⚠️ **Frontend note:** the React admin app's `api-service.ts` currently sends the
> **Supabase** session token as the Bearer. That token is **not** valid for this
> backend (different signing key/issuer). When pointing the admin app at this
> backend (`VITE_API_BASE_URL`), pick one:
> 1. Authenticate the admin app via `POST /api/auth/login` here and use that token; **or**
> 2. Set this backend's `Jwt:Secret` + issuer/audience to match whatever token TAS
>    issues (so existing tokens validate); **or**
> 3. Use `X-Api-Key` for the admin/knowledge endpoints.
>
> This is an integration decision for TAS auth topology — the backend supports all three.

---

## 6. Endpoints

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| `POST` | `/api/auth/register` | none | Create user, returns JWT |
| `POST` | `/api/auth/login` | none | Login, returns JWT |
| `GET`  | `/api/health` | none | Health check |
| `POST` | `/api/chat` | ✅ | Chat + image scan (RAG-backed) |
| `POST` | `/api/invoice/analyze` | ✅ | Direct invoice OCR |
| **`POST`** | **`/api/knowledge/upload`** | ✅ | **Upload + ingest one file with tags** |
| **`GET`**  | **`/api/knowledge/documents`** | ✅ | **List knowledge documents** |
| **`DELETE`** | **`/api/knowledge/documents/{id}`** | ✅ | **Delete document (file + chunks + row)** |
| **`PATCH`** | **`/api/knowledge/documents/{id}`** | ✅ | **Update a document's tags** |
| `POST` | `/api/knowledge/process` | ✅ | Re-chunk an already-uploaded document |

Swagger UI is served at `/swagger`.

---

## 7. Knowledge base (RAG) — how tagging works

Each document carries three optional tags that help the agent decide **when** a
document is relevant:

- **`domain`** — business area (e.g. `travel`, `expenses`, `invoices`, `policy`).
- **`doc_type`** — kind of content (e.g. `faq`, `guide`, `policy`, `reference`).
- **`description`** — a short free-text hint written by the uploader.

On every user question, `ChatService.SearchKnowledgeBase` retrieves the top chunks
and injects them into the system prompt with their tags, e.g.:

```
[travel-policy.pdf | domain: travel | type: policy] (hint: 2026 overseas travel reimbursement rules): <chunk text…>
```

so the model prefers snippets whose tags match the question.

### Supported file types for ingestion
`PDF` (embedded text via PdfPig), `DOCX`, `XLSX` (via OpenXml), and
`TXT / MD / CSV / JSON / XML`. Scanned/image-only PDFs have no embedded text and
would need OCR — send those through the invoice/vision path instead.

### Uploading via the admin UI
Admin panel → **בסיס ידע (Knowledge Base)** tab: drag in many files, set each file's
domain + type + optional hint, then **העלה**. Files are stored, chunked, indexed, and
immediately searchable.

### Uploading via the API (bulk scripting)
```bash
curl -X POST "$API/api/knowledge/upload" \
  -H "X-Api-Key: $API_KEY" \
  -F "file=@travel-policy.pdf" \
  -F "domain=travel" \
  -F "docType=policy" \
  -F "description=2026 overseas travel reimbursement rules"
```

---

## 8. Hosting on IIS (TAS)

`web.config` is included and configured for **in-process** hosting:

- `hostingModel="inprocess"`, `startupTimeLimit=300s`, `requestTimeout=10m`.
- `maxAllowedContentLength=52428800` (50 MB) — comfortably above the 25 MB per-file
  upload cap enforced in `KnowledgeController`.
- Logs: log4net writes daily-rolling files to `./logs/tripex-YYYYMMDD.log`; ASP.NET
  stdout to `./logs/stdout`.

**Publish & deploy:**
```bash
dotnet publish TripEx.Api/TripEx.Api.csproj -c Release -o ./publish
# copy ./publish to the IIS site folder; ensure the app-pool identity can reach SQL Server
```

Kestrel (non-IIS) works too — just `dotnet TripEx.Api.dll` behind a reverse proxy.

---

## 9. Production readiness checklist

- [x] Backend builds clean — **0 errors, 0 warnings** (`dotnet build`).
- [x] Unit tests pass — **39/39** (`dotnet test`).
- [x] Frontend builds clean (`vite build`) and typechecks (`tsc --noEmit`).
- [x] `init-db.sql` present, idempotent, auto-runs at startup.
- [x] ImageSharp upgraded to 3.1.11 (patches known CVEs).
- [ ] Set real secrets via env vars (`DATABASE_URL`, `JWT_SECRET`, `ORACLE_API_KEY`, `API_KEY`).
- [ ] Confirm the `Oracle:Model` value is a model enabled in your OCI compartment.
- [ ] Decide the auth path (§5) for the admin UI / TAS callers.
- [ ] Lock down CORS: `Program.cs` currently uses `AllowAnyOrigin()` — restrict to the
      TAS frontend origin(s) before go-live.
- [ ] Point the admin app at the backend with `VITE_API_BASE_URL=https://<api-host>`.
- [ ] Load the knowledge base (§7) and smoke-test a few real questions via `/api/chat`.

---

## 10. Optional future enhancement — semantic (vector) RAG

Current retrieval is lexical (substring + per-word). For fuzzy/semantic matching
("what's the mileage policy?" matching a doc that says "reimbursement per km"),
add an embeddings layer:

1. Add a `knowledge_chunks.embedding` column (store as JSON/`VARBINARY`, or use a
   vector store).
2. On ingest, call an OCI embeddings model per chunk and store the vector.
3. On query, embed the question and rank chunks by cosine similarity, blended with
   the current lexical rank.

This is additive and does not change the API surface. Not required for launch.
