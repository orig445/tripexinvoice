# TAS Deploy Checklist — TripEX AI Chatbot (.NET backend)

Deploy the chatbot into TAS the same way the OCR AI was deployed: it's the **same
`dotnet-backend/TripEx.Api` project**. The chatbot adds chat + knowledge/RAG
endpoints and tables on top of the existing OCR API — no separate service.

**Status (verified):** Release build ✅ 0 errors / 0 warnings · Tests ✅ 39/39 passing.

---

## 0. Prerequisites on the TAS host
- .NET runtime (the project targets **net8.0**; runs on net8/net10 with `DOTNET_ROLL_FORWARD=Major`).
- Reachable **SQL Server** (the TAS DB or a dedicated `tripex` DB).
- **Oracle OCI GenAI** API key + compartment (the same account used for OCR).

## 1. Get the code
```bash
git pull origin main
cd dotnet-backend
```

## 2. Configure (do NOT commit real secrets)
Set via `appsettings.Production.json` or environment variables:

| Setting | Env var | Value |
|---|---|---|
| DB connection | `ConnectionStrings__DefaultConnection` | `Server=<tas-sql>;Database=tripex;User Id=<u>;Password=<p>;TrustServerCertificate=True` |
| JWT secret (≥32 chars) | `Jwt__Secret` | a long random string |
| API key (server-to-server) | `ApiKey__Key` | a static key for TAS→API calls |
| Oracle key | `Oracle__ApiKey` | the OCI GenAI key |
| Oracle compartment | `Oracle__CompartmentId` | `ocid1.compartment.oc1..…` |
| Oracle model | `Oracle__Model` | `google.gemini-2.0-flash` (or your chosen model) |
| Storage | `Storage__Provider` | `local` (default) or `s3` |

## 3. Build / publish
```bash
dotnet publish TripEx.Api/TripEx.Api.csproj -c Release -o ./publish
```
`init-db.sql` is copied into the publish output and runs automatically on first
start (idempotent — safe to re-run; creates all tables + `dbo.search_knowledge`).

## 4. Database
No manual step needed — on startup the app runs `Data/init-db.sql` against the
configured DB. To pre-create it manually instead, run that file on the TAS SQL Server.

## 5. Deploy & run (same as OCR AI)
Host it the way the OCR API is already hosted on TAS (IIS site / Windows service /
`dotnet TripEx.Api.dll`). It's the same executable — deploying the new build
upgrades both OCR and the chatbot together.

## 6. Verify
```bash
curl http://<host>/api/health
```
Then a chat smoke test (needs a JWT from `/api/auth/login`, or the API key):
```bash
curl -X POST http://<host>/api/chat -H "Content-Type: application/json" \
  -H "Authorization: Bearer <jwt>" \
  -d '{"text":"How do I submit an expense report?","type":"text","source":"tas"}'
```

## What this deploy includes
- **Chat** — `POST /api/chat` (Milo brain: RAG + OCI, intents, sessions/history).
- **Knowledge / RAG** — `/api/knowledge/*` (upload, list, delete, process) with
  `domain` / `doc_type` / `description` tags and **audience** (external vs internal)
  isolation + a PII guardrail in the system prompt.
- **Auth** — `/api/auth/*` (JWT) + static API-key auth for server-to-server.
- **OCR / invoices** — `/api/invoice/*` (unchanged, already in TAS).
- **DB schema** — `init-db.sql`: all tables + `dbo.search_knowledge` (CHARINDEX-based,
  needs no SQL Server Full-Text feature).

## Not in this deploy (cloud-only, on Lovable/Supabase)
- WhatsApp (Green API), Outlook auto-reply, SharePoint/Zoho sync, and the
  PII-scrub/distill pipeline are built as **Supabase edge functions** for the cloud
  app. If TAS should host these too, their logic must be ported into the .NET backend
  (say the word and I'll do it).
- The React admin UI (`src/`) — the management/upload front-end, hosted separately.

## Rollback
Redeploy the previous published build. `init-db.sql` is additive (no drops), so the
DB stays compatible.
