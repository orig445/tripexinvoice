import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { ingestDocument, type IngestAction } from "../_shared/ingest.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-sync-secret",
};

// All synced documents land in the INTERNAL knowledge base.
const AUDIENCE = "internal";
const MAX_ITEMS_PER_SOURCE = 500;

interface SyncSummary {
  source: string;
  ok: boolean;
  created: number;
  updated: number;
  skipped: number;
  errors: number;
  message?: string;
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });

  const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
  const anonKey = Deno.env.get("SUPABASE_ANON_KEY")!;

  try {
    // ── Auth: allow either the scheduler (x-sync-secret) or a signed-in user ──
    const syncSecret = Deno.env.get("SYNC_SECRET");
    const providedSecret = req.headers.get("x-sync-secret");
    const authHeader = req.headers.get("Authorization");

    let authorized = false;
    if (syncSecret && providedSecret && providedSecret === syncSecret) {
      authorized = true; // scheduled invocation
    } else if (authHeader) {
      const userClient = createClient(supabaseUrl, anonKey, { global: { headers: { Authorization: authHeader } } });
      const { data: { user } } = await userClient.auth.getUser();
      if (user) authorized = true; // manual trigger from the admin UI
    }
    if (!authorized) {
      return json({ error: "Unauthorized" }, 401);
    }

    const supabase = createClient(supabaseUrl, serviceKey);
    const lovableApiKey = Deno.env.get("LOVABLE_API_KEY") || undefined;

    let sources: string[] = ["sharepoint", "zoho_crm"];
    try {
      const body = await req.json();
      if (Array.isArray(body?.sources) && body.sources.length > 0) sources = body.sources;
    } catch { /* no body → sync all */ }

    const summaries: SyncSummary[] = [];
    if (sources.includes("sharepoint")) summaries.push(await syncSharePoint(supabase, lovableApiKey));
    if (sources.includes("zoho_crm")) summaries.push(await syncZohoCrm(supabase));

    // Record the run for the admin UI / debugging.
    await supabase.from("chatbot_logs").insert({
      event_type: "knowledge_sync",
      details: { summaries },
    }).catch(() => {});

    return json({ success: true, summaries }, 200);
  } catch (err) {
    console.error("[sync] fatal:", err);
    return json({ error: err instanceof Error ? err.message : "Unknown error" }, 500);
  }

  function json(obj: unknown, status: number) {
    return new Response(JSON.stringify(obj), {
      status,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});

// ─────────────────────────────────────────────────────────────
// SharePoint (Microsoft Graph, app-only / client credentials)
// ─────────────────────────────────────────────────────────────
async function syncSharePoint(supabase: any, lovableApiKey?: string): Promise<SyncSummary> {
  const s: SyncSummary = { source: "sharepoint", ok: false, created: 0, updated: 0, skipped: 0, errors: 0 };
  try {
    const tenant = Deno.env.get("SHAREPOINT_TENANT_ID");
    const clientId = Deno.env.get("SHAREPOINT_CLIENT_ID");
    const clientSecret = Deno.env.get("SHAREPOINT_CLIENT_SECRET");
    const siteId = Deno.env.get("SHAREPOINT_SITE_ID");
    const driveId = Deno.env.get("SHAREPOINT_DRIVE_ID"); // optional
    const folderPath = Deno.env.get("SHAREPOINT_FOLDER_PATH"); // optional, e.g. "Policies/2026"

    if (!tenant || !clientId || !clientSecret || !siteId) {
      s.message = "SharePoint not configured (need SHAREPOINT_TENANT_ID / CLIENT_ID / CLIENT_SECRET / SITE_ID)";
      return s;
    }

    // App-only token
    const tokenRes = await fetch(`https://login.microsoftonline.com/${tenant}/oauth2/v2.0/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        client_id: clientId,
        client_secret: clientSecret,
        scope: "https://graph.microsoft.com/.default",
        grant_type: "client_credentials",
      }),
    });
    if (!tokenRes.ok) {
      s.message = `Graph token error ${tokenRes.status}: ${await tokenRes.text()}`;
      return s;
    }
    const token = (await tokenRes.json()).access_token as string;
    const authHeaders = { Authorization: `Bearer ${token}` };

    const driveBase = driveId
      ? `https://graph.microsoft.com/v1.0/drives/${driveId}`
      : `https://graph.microsoft.com/v1.0/sites/${siteId}/drive`;

    // Starting folder listing URL (root or a specific folder path).
    const firstUrl = folderPath
      ? `${driveBase}/root:/${encodeURI(folderPath)}:/children?$top=100`
      : `${driveBase}/root/children?$top=100`;

    // BFS through folders so nested files are included.
    const queue: string[] = [firstUrl];
    let processed = 0;

    while (queue.length > 0 && processed < MAX_ITEMS_PER_SOURCE) {
      let url: string | undefined = queue.shift();
      while (url && processed < MAX_ITEMS_PER_SOURCE) {
        const listRes = await fetch(url, { headers: authHeaders });
        if (!listRes.ok) {
          s.message = `Graph list error ${listRes.status}: ${await listRes.text()}`;
          return s;
        }
        const page = await listRes.json();
        for (const item of page.value || []) {
          if (item.folder) {
            queue.push(`${driveBase}/items/${item.id}/children?$top=100`);
            continue;
          }
          if (!item.file) continue;
          processed++;
          try {
            const dlUrl = item["@microsoft.graph.downloadUrl"];
            if (!dlUrl) { s.errors++; continue; }
            const fileRes = await fetch(dlUrl);
            if (!fileRes.ok) { s.errors++; continue; }
            const bytes = new Uint8Array(await fileRes.arrayBuffer());

            const r = await ingestDocument(supabase, {
              source: "sharepoint",
              externalId: item.id,
              externalUrl: item.webUrl,
              externalModified: item.lastModifiedDateTime,
              fileName: item.name,
              fileType: item.file.mimeType || "application/octet-stream",
              audience: AUDIENCE,
              domain: "sharepoint",
              bytes,
            }, lovableApiKey);
            tally(s, r.action);
          } catch (e) {
            console.error("[sharepoint] item error:", e);
            s.errors++;
          }
        }
        url = page["@odata.nextLink"];
      }
    }

    s.ok = true;
    return s;
  } catch (e) {
    s.message = e instanceof Error ? e.message : "sharepoint failed";
    return s;
  }
}

// ─────────────────────────────────────────────────────────────
// Zoho CRM (OAuth refresh token → records of a module as text)
// ─────────────────────────────────────────────────────────────
async function syncZohoCrm(supabase: any): Promise<SyncSummary> {
  const s: SyncSummary = { source: "zoho_crm", ok: false, created: 0, updated: 0, skipped: 0, errors: 0 };
  try {
    const clientId = Deno.env.get("ZOHO_CLIENT_ID");
    const clientSecret = Deno.env.get("ZOHO_CLIENT_SECRET");
    const refreshToken = Deno.env.get("ZOHO_REFRESH_TOKEN");
    const dc = Deno.env.get("ZOHO_DC") || "com"; // com | eu | in | com.au | jp
    const module = Deno.env.get("ZOHO_CRM_MODULE") || "Accounts";

    if (!clientId || !clientSecret || !refreshToken) {
      s.message = "Zoho not configured (need ZOHO_CLIENT_ID / ZOHO_CLIENT_SECRET / ZOHO_REFRESH_TOKEN)";
      return s;
    }

    // Refresh access token
    const tokenRes = await fetch(
      `https://accounts.zoho.${dc}/oauth/v2/token?` +
        new URLSearchParams({
          refresh_token: refreshToken,
          client_id: clientId,
          client_secret: clientSecret,
          grant_type: "refresh_token",
        }),
      { method: "POST" },
    );
    if (!tokenRes.ok) {
      s.message = `Zoho token error ${tokenRes.status}: ${await tokenRes.text()}`;
      return s;
    }
    const accessToken = (await tokenRes.json()).access_token as string;
    if (!accessToken) { s.message = "Zoho returned no access_token (check refresh token / scopes)"; return s; }
    const zHeaders = { Authorization: `Zoho-oauthtoken ${accessToken}` };
    const apiBase = `https://www.zohoapis.${dc}/crm/v6`;

    // Discover field API names for the module.
    let fields: string[] = [];
    const fieldsRes = await fetch(`${apiBase}/settings/fields?module=${encodeURIComponent(module)}`, { headers: zHeaders });
    if (fieldsRes.ok) {
      const fj = await fieldsRes.json();
      fields = (fj.fields || [])
        .map((f: any) => f.api_name)
        .filter((n: string) => n && n !== "id");
    }
    if (fields.length === 0) fields = ["Name", "Email", "Phone", "Description"]; // fallback

    // Zoho caps the fields param length; keep it reasonable.
    const fieldsParam = fields.slice(0, 50).join(",");

    let page = 1;
    let more = true;
    let processed = 0;
    while (more && processed < MAX_ITEMS_PER_SOURCE) {
      const recRes = await fetch(
        `${apiBase}/${encodeURIComponent(module)}?fields=${encodeURIComponent(fieldsParam)}&per_page=200&page=${page}`,
        { headers: zHeaders },
      );
      if (recRes.status === 204) break; // no content
      if (!recRes.ok) {
        s.message = `Zoho records error ${recRes.status}: ${await recRes.text()}`;
        return s;
      }
      const rj = await recRes.json();
      const records: any[] = rj.data || [];
      for (const rec of records) {
        processed++;
        try {
          const display =
            rec.Name || rec.Account_Name || rec.Deal_Name || rec.Full_Name || rec.Subject || rec.id;
          const text = recordToText(rec);
          const r = await ingestDocument(supabase, {
            source: "zoho_crm",
            externalId: String(rec.id),
            externalUrl: `https://crm.zoho.${dc}/crm/tab/${module}/${rec.id}`,
            externalModified: rec.Modified_Time,
            fileName: `${module}: ${display}`,
            fileType: "text/plain",
            audience: AUDIENCE,
            domain: "zoho_crm",
            text,
          });
          tally(s, r.action);
        } catch (e) {
          console.error("[zoho] record error:", e);
          s.errors++;
        }
      }
      more = rj.info?.more_records === true;
      page++;
    }

    s.ok = true;
    return s;
  } catch (e) {
    s.message = e instanceof Error ? e.message : "zoho failed";
    return s;
  }
}

/** Flatten a Zoho record into readable "Field: value" lines for RAG. */
function recordToText(rec: Record<string, any>): string {
  const lines: string[] = [];
  for (const [key, value] of Object.entries(rec)) {
    if (value == null || value === "") continue;
    if (["$", "id"].some((p) => key.startsWith(p))) continue;
    let v: string;
    if (typeof value === "object") {
      // Zoho lookups come back as { name, id }
      v = value.name ?? JSON.stringify(value);
    } else {
      v = String(value);
    }
    lines.push(`${key.replace(/_/g, " ")}: ${v}`);
  }
  return lines.join("\n");
}

function tally(s: SyncSummary, action: IngestAction) {
  if (action === "created") s.created++;
  else if (action === "updated") s.updated++;
  else if (action === "skipped") s.skipped++;
  else s.errors++;
}
