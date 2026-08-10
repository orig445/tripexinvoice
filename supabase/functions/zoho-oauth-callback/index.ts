import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// ─────────────────────────────────────────────────────────────────────────────
// Zoho OAuth 2.0 callback (Server-based Application flow).
//
// One-time admin setup to obtain a long-lived refresh token:
//   1. Register a "Server-based Application" at https://api-console.zoho.com
//      and set the Authorized Redirect URI to THIS function's URL:
//        https://<project-ref>.supabase.co/functions/v1/zoho-oauth-callback
//   2. Set the function secrets: ZOHO_CLIENT_ID, ZOHO_CLIENT_SECRET, ZOHO_DC.
//   3. Open this function's URL in a browser with no query string — it redirects
//      you to Zoho's consent screen.
//   4. After you approve, Zoho redirects back here with ?code=... and this
//      function exchanges it for a refresh_token, which it shows you once.
//   5. Copy that refresh_token into the ZOHO_REFRESH_TOKEN secret.
//
// The app itself never uses this endpoint at runtime — it uses the stored
// refresh token (see sync-knowledge-sources) to mint access tokens.
// ─────────────────────────────────────────────────────────────────────────────

const DC = Deno.env.get("ZOHO_DC") || "com"; // com | eu | in | com.au | jp | ca
const CLIENT_ID = Deno.env.get("ZOHO_CLIENT_ID") || "";
const CLIENT_SECRET = Deno.env.get("ZOHO_CLIENT_SECRET") || "";
// Default scopes: read CRM modules (knowledge sync) + create/read Desk tickets
// (chat → ticket). Override per-request with ?scope=...
const DEFAULT_SCOPE = "ZohoCRM.modules.READ,Desk.tickets.CREATE,Desk.tickets.READ";

function html(body: string, status = 200): Response {
  return new Response(
    `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
     <body style="font-family:system-ui;max-width:720px;margin:40px auto;padding:0 16px;line-height:1.5">${body}</body>`,
    { status, headers: { "Content-Type": "text/html; charset=utf-8" } },
  );
}

serve(async (req) => {
  const url = new URL(req.url);
  // The redirect_uri must be byte-for-byte identical in the auth request, the
  // token exchange, and the Zoho console. Derive it from this request (or pin
  // it via ZOHO_REDIRECT_URI if Supabase routing rewrites the host).
  const redirectUri = Deno.env.get("ZOHO_REDIRECT_URI") || `${url.origin}${url.pathname}`;
  const code = url.searchParams.get("code");
  const error = url.searchParams.get("error");
  const scope = url.searchParams.get("scope") || DEFAULT_SCOPE;

  if (error) return html(`<h2>Zoho returned an error</h2><pre>${error}</pre>`, 400);

  if (!CLIENT_ID || !CLIENT_SECRET) {
    return html(`<h2>Not configured</h2><p>Set <code>ZOHO_CLIENT_ID</code> and
      <code>ZOHO_CLIENT_SECRET</code> as function secrets first.</p>`, 500);
  }

  // Step 1 — no code yet: send the admin to Zoho's consent screen.
  if (!code) {
    const authUrl =
      `https://accounts.zoho.${DC}/oauth/v2/auth?` +
      new URLSearchParams({
        response_type: "code",
        client_id: CLIENT_ID,
        scope,
        redirect_uri: redirectUri,
        access_type: "offline", // required to receive a refresh_token
        prompt: "consent",
      });
    return Response.redirect(authUrl, 302);
  }

  // Step 2 — exchange the authorization code for tokens.
  const tokenRes = await fetch(`https://accounts.zoho.${DC}/oauth/v2/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: CLIENT_ID,
      client_secret: CLIENT_SECRET,
      redirect_uri: redirectUri,
      code,
    }),
  });

  const data = await tokenRes.json().catch(() => ({}));

  if (!tokenRes.ok || !data.refresh_token) {
    return html(
      `<h2>Could not get a refresh token</h2>
       <pre>${JSON.stringify(data, null, 2)}</pre>
       <p>Common causes: the redirect_uri here does not match the one registered
       in the Zoho console, wrong data center (<code>ZOHO_DC=${DC}</code>), the code
       was already used, or <code>access_type=offline</code> was missing.</p>
       <p>redirect_uri used: <code>${redirectUri}</code></p>`,
      400,
    );
  }

  return html(
    `<h2>✅ Zoho connected</h2>
     <p>Copy this <b>refresh_token</b> into the Supabase secret
     <code>ZOHO_REFRESH_TOKEN</code> (it is shown only once):</p>
     <textarea readonly style="width:100%;height:90px;font-family:monospace">${data.refresh_token}</textarea>
     <p>Data center: <code>${DC}</code> · You can close this tab now.</p>`,
  );
});
