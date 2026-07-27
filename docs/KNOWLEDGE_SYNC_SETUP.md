# Knowledge Sync — SharePoint + Zoho CRM → Internal Knowledge Base

This connects the **internal** chatbot's knowledge base to SharePoint (documents)
and Zoho CRM (records). A scheduled job pulls items on an interval, extracts text,
chunks and indexes them for RAG. Everything lands in the **internal** base
(`audience = internal`) and is isolated from the customer bot.

Pieces (already in the repo):
- Edge function `sync-knowledge-sources` — the connectors + ingest pipeline.
- Shared ingest logic `supabase/functions/_shared/ingest.ts`.
- Schema: `source`, `external_id`, `external_url`, `external_modified` columns
  (idempotent upsert — re-running updates changed items, skips unchanged).
- Manual "סנכרן מ-SharePoint / Zoho" button on `/knowledge-internal` (for testing).

> Nothing syncs until the steps below are done: the DB columns, the OAuth apps,
> the Supabase secrets, and the schedule. Use the manual button to test first.

---

## 1. Run the DB migrations (Supabase SQL Editor)

`https://supabase.com/dashboard/project/osuyokvyhiyvyhjrbcxm/sql/new`

```sql
-- tags + audience (if not already run)
alter table public.knowledge_documents
  add column if not exists domain      text,
  add column if not exists doc_type    text,
  add column if not exists description text,
  add column if not exists audience    text not null default 'external';

-- source tracking for connectors
alter table public.knowledge_documents
  add column if not exists source            text not null default 'upload',
  add column if not exists external_id       text,
  add column if not exists external_url      text,
  add column if not exists external_modified timestamptz;

create unique index if not exists ux_knowledge_documents_source_external
  on public.knowledge_documents (source, external_id)
  where external_id is not null;
```

---

## 2. SharePoint — Azure AD app (app-only access)

1. Azure Portal → **Microsoft Entra ID → App registrations → New registration**.
   Name it e.g. `tripex-knowledge-sync`. Single tenant is fine.
2. **Certificates & secrets → New client secret** → copy the **Value** (this is
   `SHAREPOINT_CLIENT_SECRET`; it's shown only once).
3. **API permissions → Add → Microsoft Graph → Application permissions** →
   add **`Sites.Read.All`** (or `Files.Read.All`) → **Grant admin consent**.
4. Collect:
   - `SHAREPOINT_TENANT_ID` — Directory (tenant) ID (Overview page).
   - `SHAREPOINT_CLIENT_ID` — Application (client) ID (Overview page).
   - `SHAREPOINT_CLIENT_SECRET` — the secret value from step 2.
   - `SHAREPOINT_SITE_ID` — get it from Graph Explorer:
     `GET https://graph.microsoft.com/v1.0/sites/{host}:/sites/{siteName}`
     e.g. `.../sites/contoso.sharepoint.com:/sites/HR` → use the returned `id`.
   - *(optional)* `SHAREPOINT_DRIVE_ID` — a specific document library, else the
     site's default drive is used.
   - *(optional)* `SHAREPOINT_FOLDER_PATH` — restrict to a folder, e.g. `Policies/2026`.
     Omit to sync the whole library (nested folders included).

---

## 3. Zoho CRM — OAuth app + refresh token

1. `https://api-console.zoho.com` → **Add Client → Self Client** (simplest for a
   server-to-server sync).
2. **Scopes**: `ZohoCRM.modules.READ,ZohoCRM.settings.READ`. Generate a code, then
   exchange it for a **refresh token** (one-time):
   ```
   POST https://accounts.zoho.<dc>/oauth/v2/token
     ?grant_type=authorization_code
     &client_id=<id>&client_secret=<secret>
     &code=<generated_code>
   ```
   Save the `refresh_token` from the response (it does not expire).
3. Collect:
   - `ZOHO_CLIENT_ID`, `ZOHO_CLIENT_SECRET`, `ZOHO_REFRESH_TOKEN`.
   - `ZOHO_DC` — your data center: `com` (US), `eu`, `in`, `com.au`, `jp`. Default `com`.
   - `ZOHO_CRM_MODULE` — which module to ingest, e.g. `Accounts`, `Contacts`,
     `Deals`, `Solutions`. Default `Accounts`. (One module per project for now.)

---

## 4. Set the Supabase function secrets

Dashboard → **Edge Functions → Manage secrets** (or `supabase secrets set`). Add:

```
SYNC_SECRET=<make-a-long-random-string>     # used by the scheduler to authenticate

SHAREPOINT_TENANT_ID=...
SHAREPOINT_CLIENT_ID=...
SHAREPOINT_CLIENT_SECRET=...
SHAREPOINT_SITE_ID=...
# SHAREPOINT_DRIVE_ID=...        (optional)
# SHAREPOINT_FOLDER_PATH=...     (optional)

ZOHO_CLIENT_ID=...
ZOHO_CLIENT_SECRET=...
ZOHO_REFRESH_TOKEN=...
# ZOHO_DC=com                    (optional)
# ZOHO_CRM_MODULE=Accounts       (optional)
```

`LOVABLE_API_KEY` is already set (used to extract text from PDF/Word). A source
whose secrets are missing is simply skipped with a message — you can enable one
before the other.

---

## 5. Test it (manual)

1. Make sure the `sync-knowledge-sources` function is deployed (open the Lovable
   project so it syncs the repo, or `supabase functions deploy sync-knowledge-sources`).
2. Go to **`/knowledge-internal`** → click **"סנכרן מ-SharePoint / Zoho"**.
3. You'll get a summary toast (created / updated / errors) and any per-source
   messages (e.g. "not configured"). Documents show a blue **SharePoint** / **Zoho CRM**
   badge. Then ask the internal bot at **`/chat-internal`**.
4. Logs: Dashboard → Edge Functions → `sync-knowledge-sources` → Logs.

---

## 6. Schedule it (automatic)

Enable the extensions once (Dashboard → Database → Extensions: `pg_cron`, `pg_net`),
then in the SQL Editor:

```sql
-- Run every hour. Replace <SYNC_SECRET> with the value from step 4.
select cron.schedule(
  'sync-knowledge-hourly',
  '0 * * * *',
  $$
  select net.http_post(
    url     := 'https://osuyokvyhiyvyhjrbcxm.supabase.co/functions/v1/sync-knowledge-sources',
    headers := jsonb_build_object('Content-Type','application/json','x-sync-secret','<SYNC_SECRET>'),
    body    := '{}'::jsonb
  );
  $$
);

-- to change/stop later:
-- select cron.unschedule('sync-knowledge-hourly');
```

Adjust the cron expression (`0 * * * *` = hourly, `0 6 * * *` = daily 06:00 UTC).

---

## Notes & limits (v1)

- Each sync caps at 500 items per source (guardrail). Raise `MAX_ITEMS_PER_SOURCE`
  in the function if needed.
- Idempotent: unchanged items (same `external_modified`) are skipped, so repeated
  runs are cheap.
- Deletions in the source are **not** yet removed from the KB (add-only). Delete
  such docs manually on `/knowledge-internal` if needed.
- Zoho ingests one module per project; ping me to support multiple modules.
- Legacy `.xls` and HTML-as-`.xls` are handled; scanned PDFs rely on the AI extractor.
