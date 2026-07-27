// Shared knowledge ingestion helpers used by the sync connectors
// (sync-knowledge-sources). Kept self-contained so it does not disturb the
// already-deployed process-knowledge function.

import * as XLSX from "https://cdn.sheetjs.com/xlsx-0.20.3/package/xlsx.mjs";

const CHUNK_SIZE = 1000;
const CHUNK_OVERLAP = 200;

export function chunkText(text: string): string[] {
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

/** Extract plain text from a file's bytes. Handles text/CSV/JSON/XML/MD,
 *  Excel (.xls/.xlsx via SheetJS) and PDF/Word (via the Lovable AI gateway). */
export async function extractText(
  bytes: Uint8Array,
  fileName: string,
  fileType: string,
  lovableApiKey?: string,
): Promise<string> {
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
    // HTML/CSV-as-xls fallback
    const asText = new TextDecoder("utf-8").decode(bytes);
    if (/<\s*(table|tr|td|html|body)/i.test(asText)) {
      return asText.replace(/<[^>]+>/g, " ").replace(/&nbsp;/gi, " ").replace(/[ \t]+/g, " ").trim();
    }
    return asText.trim();
  }

  if (isText) return new TextDecoder("utf-8").decode(bytes);

  if (isDoc && lovableApiKey) {
    const base64 = base64Encode(bytes);
    const resp = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: { Authorization: `Bearer ${lovableApiKey}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash",
        messages: [
          { role: "system", content: "Extract ALL text content from this document. Return ONLY the raw text, preserving structure. No summaries." },
          { role: "user", content: [
            { type: "text", text: "Extract all text from this document:" },
            { type: "image_url", image_url: { url: `data:${fileType};base64,${base64}` } },
          ] },
        ],
        max_tokens: 4096, temperature: 0,
      }),
    });
    if (resp.ok) {
      const data = await resp.json();
      return data.choices?.[0]?.message?.content || "";
    }
    console.error("[ingest] AI extraction failed:", resp.status);
    return "";
  }

  // Last resort: try UTF-8
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

export interface IngestItem {
  source: "sharepoint" | "zoho_crm";
  externalId: string;
  externalUrl?: string;
  externalModified?: string; // ISO timestamp
  fileName: string;
  fileType: string;
  audience: string;          // 'internal' | 'external'
  domain?: string;
  docType?: string;
  bytes?: Uint8Array;        // for file sources (SharePoint)
  text?: string;             // for record sources (Zoho CRM)
}

export type IngestAction = "created" | "updated" | "skipped" | "error";

/**
 * Idempotently ingest one item into knowledge_documents + knowledge_chunks.
 * Skips items whose external_modified is unchanged since the last sync.
 */
export async function ingestDocument(
  supabase: any,
  item: IngestItem,
  lovableApiKey?: string,
): Promise<{ action: IngestAction; chunks: number; error?: string }> {
  // Look up an existing row for this source item.
  const { data: existing } = await supabase
    .from("knowledge_documents")
    .select("id, external_modified, status")
    .eq("source", item.source)
    .eq("external_id", item.externalId)
    .maybeSingle();

  // Skip if nothing changed and it was already processed.
  if (
    existing &&
    existing.status === "ready" &&
    item.externalModified &&
    existing.external_modified &&
    new Date(existing.external_modified).getTime() === new Date(item.externalModified).getTime()
  ) {
    return { action: "skipped", chunks: 0 };
  }

  // Resolve text content.
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
    text = await extractText(item.bytes, item.fileName, item.fileType, lovableApiKey);
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

  // Upsert by (source, external_id).
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

  // Replace chunks.
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
