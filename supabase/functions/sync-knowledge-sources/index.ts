import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import * as XLSX from "https://cdn.sheetjs.com/xlsx-0.20.3/package/xlsx.mjs";
// NOTE: the knowledge-ingest helpers are INLINED at the bottom of this file
// (previously ../_shared/ingest.ts). Lovable's function deploy does not bundle
// the _shared folder, so importing from it made this function fail to deploy
// ("Failed to send a request to the Edge Function"). Keep it self-contained.

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
    let sources: string[] = ["sharepoint", "zoho_crm"];
    try {
      const body = await req.json();
      if (Array.isArray(body?.sources) && body.sources.length > 0) sources = body.sources;
    } catch { /* no body → sync all */ }

    const summaries: SyncSummary[] = [];
    if (sources.includes("sharepoint")) summaries.push(await syncSharePoint(supabase));
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
async function syncSharePoint(supabase: any): Promise<SyncSummary> {
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
            });
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

// ─────────────────────────────────────────────────────────────────────────────
// Inlined knowledge-ingest helpers (were ../_shared/ingest.ts). Self-contained
// so Lovable deploys this function without needing the _shared folder.
// ─────────────────────────────────────────────────────────────────────────────

const CHUNK_SIZE = 1000;
const CHUNK_OVERLAP = 200;

const OCI_ENDPOINT =
  "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const OCI_MODEL = Deno.env.get("OCI_MODEL") || "meta.llama-4-maverick-17b-128e-instruct-fp8";

function ociKey(): string | undefined {
  return Deno.env.get("oracleapikey_2") || Deno.env.get("oracleapikey") || Deno.env.get("invoice");
}

async function ociVision(instruction: string, dataUrl: string, maxTokens = 4096): Promise<string> {
  const key = ociKey();
  if (!key) return "";
  const res = await fetch(OCI_ENDPOINT, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [{ role: "user", content: [
        { type: "text", text: instruction },
        { type: "image_url", image_url: { url: dataUrl } },
      ] }],
      max_tokens: maxTokens,
      temperature: 0,
    }),
  });
  if (!res.ok) { console.error("[ingest] OCI error:", res.status); return ""; }
  const data = await res.json();
  return data.choices?.[0]?.message?.content || "";
}

function chunkText(text: string): string[] {
  const chunks: string[] = [];
  if (!text || text.trim().length === 0) return chunks;
  let start = 0;
  while (start < text.length) {
    let end = Math.min(start + CHUNK_SIZE, text.length);
    if (end < text.length) {
      const lastPeriod = text.lastIndexOf(".", end);
      const lastNewline = text.lastIndexOf("\n", end);
      const breakPoint = Math.max(lastPeriod, lastNewline);
      if (breakPoint > start + CHUNK_SIZE / 2) end = breakPoint + 1;
    }
    const chunk = text.slice(start, end).trim();
    if (chunk.length > 0) chunks.push(chunk);
    start = end - CHUNK_OVERLAP;
    if (start < 0) start = 0;
    if (end >= text.length) break;
  }
  return chunks;
}

async function extractText(bytes: Uint8Array, fileName: string, fileType: string): Promise<string> {
  const type = (fileType || "").toLowerCase();
  const name = (fileName || "").toLowerCase();

  const isSpreadsheet =
    type.includes("spreadsheet") || type.includes("excel") || type.includes("sheet") ||
    /\.(xlsx|xls|xlsm|xlsb)$/.test(name);
  const isText =
    type.includes("text") || type.includes("csv") || type.includes("json") ||
    type.includes("xml") || type.includes("markdown") ||
    /\.(txt|csv|json|xml|md)$/.test(name);
  const isDoc = type.includes("pdf") || type.includes("word") || type.includes("document") ||
    /\.(pdf|docx?)$/.test(name);

  if (isSpreadsheet) {
    try {
      const wb = XLSX.read(bytes, { type: "array" });
      const parts: string[] = [];
      for (const sheetName of wb.SheetNames) {
        const sheet = wb.Sheets[sheetName];
        if (!sheet) continue;
        const csv = XLSX.utils.sheet_to_csv(sheet, { blankrows: false });
        if (csv && csv.trim()) { parts.push(`--- ${sheetName} ---`); parts.push(csv.trim()); }
      }
      if (parts.join("").trim().length >= 20) return parts.join("\n\n");
    } catch (e) {
      console.error("[ingest] SheetJS failed:", e);
    }
    const asText = new TextDecoder("utf-8").decode(bytes);
    if (/<\s*(table|tr|td|html|body)/i.test(asText)) {
      return asText.replace(/<[^>]+>/g, " ").replace(/&nbsp;/gi, " ").replace(/[ \t]+/g, " ").trim();
    }
    return asText.trim();
  }

  if (isText) return new TextDecoder("utf-8").decode(bytes);

  if (isDoc) {
    if (type.includes("pdf") || /\.pdf$/.test(name)) {
      try {
        const { getDocumentProxy, extractText: pdfExtractText } = await import("https://esm.sh/unpdf@0.12.1");
        const pdf = await getDocumentProxy(bytes);
        const { text } = await pdfExtractText(pdf, { mergePages: true });
        const local = (Array.isArray(text) ? text.join("\n") : text || "").trim();
        if (local.length >= 20) return local;
      } catch (e) {
        console.error("[ingest] local PDF extraction failed:", e);
      }
      return await ociVision(
        "Extract ALL text content from this document. Return ONLY the raw text.",
        `data:application/pdf;base64,${base64Encode(bytes)}`,
      );
    }
    return await ociVision(
      "Extract ALL text content from this document. Return ONLY the raw text, preserving structure.",
      `data:${fileType};base64,${base64Encode(bytes)}`,
    );
  }

  try { return new TextDecoder("utf-8").decode(bytes); } catch { return ""; }
}

function base64Encode(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

interface IngestItem {
  source: "sharepoint" | "zoho_crm";
  externalId: string;
  externalUrl?: string;
  externalModified?: string;
  fileName: string;
  fileType: string;
  audience: string;
  domain?: string;
  docType?: string;
  bytes?: Uint8Array;
  text?: string;
}

type IngestAction = "created" | "updated" | "skipped" | "error";

async function ingestDocument(
  supabase: any,
  item: IngestItem,
): Promise<{ action: IngestAction; chunks: number; error?: string }> {
  const { data: existing } = await supabase
    .from("knowledge_documents")
    .select("id, external_modified, status")
    .eq("source", item.source)
    .eq("external_id", item.externalId)
    .maybeSingle();

  if (
    existing &&
    existing.status === "ready" &&
    item.externalModified &&
    existing.external_modified &&
    new Date(existing.external_modified).getTime() === new Date(item.externalModified).getTime()
  ) {
    return { action: "skipped", chunks: 0 };
  }

  let text = item.text ?? "";
  let fileUrl = item.externalUrl ?? "";
  let fileSize: number | null = item.text ? item.text.length : null;

  if (item.bytes) {
    fileSize = item.bytes.length;
    const ext = (item.fileName.split(".").pop() || "bin").toLowerCase().slice(0, 8);
    const path = `sync/${item.source}/${item.externalId}.${ext}`;
    const { error: upErr } = await supabase.storage
      .from("knowledge")
      .upload(path, item.bytes, { upsert: true, contentType: item.fileType || "application/octet-stream" });
    if (!upErr) fileUrl = path;
    text = await extractText(item.bytes, item.fileName, item.fileType);
  }

  text = (text || "").replace(/\u0000/g, "").trim();

  const row = {
    file_name: item.fileName,
    file_type: item.fileType || "text/plain",
    file_url: fileUrl,
    file_size: fileSize,
    audience: item.audience,
    domain: item.domain ?? null,
    doc_type: item.docType ?? null,
    source: item.source,
    external_id: item.externalId,
    external_url: item.externalUrl ?? null,
    external_modified: item.externalModified ?? null,
    status: text.length >= 5 ? "processing" : "error",
    updated_at: new Date().toISOString(),
  };

  const { data: upserted, error: upsertErr } = await supabase
    .from("knowledge_documents")
    .upsert(row, { onConflict: "source,external_id" })
    .select("id")
    .single();

  if (upsertErr || !upserted) {
    return { action: "error", chunks: 0, error: upsertErr?.message || "upsert failed" };
  }
  const docId = upserted.id;

  if (text.length < 5) {
    return { action: existing ? "updated" : "created", chunks: 0, error: "no text extracted" };
  }

  await supabase.from("knowledge_chunks").delete().eq("document_id", docId);
  const chunks = chunkText(text);
  if (chunks.length > 0) {
    const rows = chunks.map((content, idx) => ({ document_id: docId, content, chunk_index: idx }));
    const { error: chunkErr } = await supabase.from("knowledge_chunks").insert(rows);
    if (chunkErr) return { action: "error", chunks: 0, error: chunkErr.message };
  }

  await supabase.from("knowledge_documents").update({ status: "ready" }).eq("id", docId);
  return { action: existing ? "updated" : "created", chunks: chunks.length };
}
