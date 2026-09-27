# Base44 dev notes — TripEx / Milo (Lovable frontend)

## What this app is
A Lovable **Vite + React + TypeScript** SPA (`vite_react_shadcn_ts`). All routes
are behind Supabase auth (`ProtectedRoute` → redirects to `/auth` when logged out).

## Backend wiring
- **No `VITE_API_BASE_URL` is set** → `src/lib/api-service.ts` falls back to the
  **remote Supabase Edge Functions** (`supabase.functions.invoke`) for chat /
  invoice / knowledge calls. Auth also goes to the remote Supabase project.
- The remote Supabase project is `osuyokvyhiyvyhjrbcxm.supabase.co`; its **anon
  publishable key is committed in `.env`** (public, safe — it is the anon role key).
- `src/integrations/supabase/previewAuthStorage.ts` brokers the session over
  postMessage only on Lovable preview zones; everywhere else (incl. Base44) it
  falls back to `localStorage`, so auth works normally here.

## Optional .NET backend (`dotnet-backend/`)
Only used when `VITE_API_BASE_URL` points at it. It needs SQL Server
(`Microsoft.Data.SqlClient` / EF Core), a JWT secret, an API key, and an
**Oracle Generative AI key** (`Oracle:ApiKey` in `appsettings.json`) for the
chatbot. It is NOT required to render the preview. Bring it up separately if the
user wants the self-hosted API path.

## Running here
`docker compose -f docker-compose.base44.yml up -d` — runs the Vite dev server
(`npx vite`) on container port 8080, mapped to host port 3000, with live reload
(chokidar polling enabled for the bind mount).

## Verify it works
`curl -sI http://localhost:3000` → `200 OK` and HTML containing `<div id="root">`.
The first load shows the Auth page (every route is auth-gated).

## Quirks
- `vite.config.ts` sets `server.allowedHosts: true` so the Base44 preview proxy
  (rotating external host) isn't rejected by Vite's Host-header check.
- `node_modules` is created inside the bind mount (gitignored) on first boot via
  `npm install`; subsequent restarts are fast.
