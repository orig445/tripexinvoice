import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { encode as base64Encode } from "https://deno.land/std@0.168.0/encoding/base64.ts";
// Official SheetJS ESM build (recommended for Deno / Supabase Edge Functions).
import * as XLSX from "https://cdn.sheetjs.com/xlsx-0.20.3/package/xlsx.mjs";
// NOTE: unpdf is imported DYNAMICALLY inside extractPdfTextLocal (not a static
// top-level import) so a bundling issue with it can never block deployment.


const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
};

const CHUNK_SIZE = 1000; // characters per chunk
const CHUNK_OVERLAP = 200;

function chunkText(text: string): string[] {
  const chunks: string[] = [];
  if (!text || text.trim().length === 0) return chunks;

  let start = 0;
  while (start < text.length) {
    let end = Math.min(start + CHUNK_SIZE, text.length);
    // Try to break at sentence boundary
    if (end < text.length) {
      const lastPeriod = text.lastIndexOf(".", end);
      const lastNewline = text.lastIndexOf("\n", end);
      const breakPoint = Math.max(lastPeriod, lastNewline);
      if (breakPoint > start + CHUNK_SIZE / 2) {
        end = breakPoint + 1;
      }
    }
    const chunk = text.slice(start, end).trim();
    if (chunk.length > 0) chunks.push(chunk);
    start = end - CHUNK_OVERLAP;
    if (start < 0) start = 0;
    if (end >= text.length) break;
  }
  return chunks;
}

// ── PII scrubbing (deterministic safety net) ──
// Masks the identifiers that show up in Glassix support threads regardless of
// what the AI distiller does. Applied to the final text before chunking.
function scrubPII(text: string): string {
  let t = text;
  // Email addresses
  t = t.replace(/[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g, "[email]");
  // Phone numbers (Israeli / international, 7+ digits with optional separators)
  t = t.replace(/(?:\+?\d[\d\-\s().]{6,}\d)/g, (m) => (m.replace(/\D/g, "").length >= 7 ? "[phone]" : m));
  // Email header lines that carry names/addresses
  t = t.replace(/^\s*(From|To|Cc|Bcc|Sent|Subject|On .* wrote:)\s*:?.*$/gim, "");
  // Glassix / TAS identifiers
  t = t.replace(/\bTicket\s*#?\s*:?\s*\d+/gi, "[ticket]");
  t = t.replace(/\b(TAS)\s*0*\d+[A-Z]?\b/gi, "[TAS]");
  t = t.replace(/\bמספר פנייה\s*\d+/g, "[פנייה]");
  t = t.replace(/\b(trip|נסיעה|travel)\s*#?\s*\d{2,}\b/gi, "$1 [id]");
  t = t.replace(/\bשיחה מזוהה\s*\d+/g, "[מזוהה]");
  // Collapse blank lines left behind
  return t.replace(/\n{3,}/g, "\n\n").trim();
}

// Fetch that retries on rate limit (429) / transient (503) with exponential
// backoff, honoring Retry-After when present. Prevents bulk reprocessing from
// failing the whole batch when the AI gateway throttles.
async function aiFetchWithRetry(url: string, init: RequestInit, tries = 4): Promise<Response> {
  let delay = 1500;
  for (let attempt = 1; attempt <= tries; attempt++) {
    const res = await fetch(url, init);
    if (res.status !== 429 && res.status !== 503) return res;
    if (attempt === tries) return res;
    const retryAfter = Number(res.headers.get("retry-after"));
    const wait = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : delay;
    console.warn(`[ai] ${res.status} — backing off ${wait}ms (attempt ${attempt}/${tries})`);
    await new Promise((r) => setTimeout(r, wait));
    delay *= 2;
  }
  return fetch(url, init);
}

// ── Oracle OCI Generative AI (replaces the Lovable AI gateway) ──
const OCI_ENDPOINT =
  "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const OCI_MODEL = Deno.env.get("OCI_MODEL") || "meta.llama-4-maverick-17b-128e-instruct-fp8";

function ociKey(): string | undefined {
  return Deno.env.get("oracleapikey_2") || Deno.env.get("oracleapikey") || Deno.env.get("invoice");
}

/** Text-only chat completion via OCI. */
async function ociChatText(prompt: string, maxTokens = 4096): Promise<string> {
  const key = ociKey();
  if (!key) throw new Error("Oracle API key not configured (oracleapikey_2 / oracleapikey / invoice)");
  const res = await aiFetchWithRetry(OCI_ENDPOINT, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [{ role: "user", content: prompt }],
      max_tokens: maxTokens,
      temperature: 0,
    }),
  });
  if (!res.ok) throw new Error(`OCI AI error: ${res.status} - ${await res.text()}`);
  const data = await res.json();
  return data.choices?.[0]?.message?.content || "";
}

/** Vision chat completion via OCI (image data URL). */
async function ociVision(instruction: string, dataUrl: string, maxTokens = 4096): Promise<string> {
  const key = ociKey();
  if (!key) throw new Error("Oracle API key not configured");
  const res = await aiFetchWithRetry(OCI_ENDPOINT, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [
        { role: "user", content: [
          { type: "text", text: instruction },
          { type: "image_url", image_url: { url: dataUrl } },
        ] },
      ],
      max_tokens: maxTokens,
      temperature: 0,
    }),
  });
  if (!res.ok) throw new Error(`OCI vision error: ${res.status} - ${await res.text()}`);
  const data = await res.json();
  return data.choices?.[0]?.message?.content || "";
}

/** Extract text from a PDF locally (no AI). Returns "" if the PDF has no embedded text. */
async function extractPdfTextLocal(bytes: Uint8Array): Promise<string> {
  try {
    // Dynamic import so a bundling problem with unpdf never blocks deployment.
    const { getDocumentProxy, extractText } = await import("https://esm.sh/unpdf@0.12.1");
    const pdf = await getDocumentProxy(bytes);
    const { text } = await extractText(pdf, { mergePages: true });
    return (Array.isArray(text) ? text.join("\n") : text || "").trim();
  } catch (e) {
    console.error("[pdf] local extraction failed:", e);
    return "";
  }
}

type FileKind = "spreadsheet" | "pdf" | "word" | "text" | "image" | "unknown";

/**
 * Decide how to extract a file. Extension and magic bytes win over the mime
 * type, because ZIP/bulk uploads arrive as "application/octet-stream".
 */
function detectKind(mime: string, name: string, head: Uint8Array): FileKind {
  const ext = (name.match(/\.([a-z0-9]+)$/)?.[1] || "").toLowerCase();
  if (["xlsx", "xls", "xlsm", "xlsb"].includes(ext)) return "spreadsheet";
  if (ext === "pdf") return "pdf";
  if (["docx", "doc", "pptx", "ppt", "rtf"].includes(ext)) return "word";
  if (["txt", "csv", "json", "xml", "md", "html", "htm", "log", "yml", "yaml"].includes(ext)) return "text";
  if (["png", "jpg", "jpeg", "webp", "gif", "bmp", "tif", "tiff"].includes(ext)) return "image";

  if (mime.includes("spreadsheet") || mime.includes("excel") || mime.includes("sheet")) return "spreadsheet";
  if (mime.includes("pdf")) return "pdf";
  if (mime.includes("word") || mime.includes("presentation") || mime.includes("officedocument")) return "word";
  if (mime.includes("image")) return "image";
  if (mime.includes("text") || mime.includes("csv") || mime.includes("json") || mime.includes("xml") || mime.includes("markdown")) return "text";

  // Magic-byte sniffing for octet-stream / missing mime.
  const sig = String.fromCharCode(...head.slice(0, 4));
  if (sig === "%PDF") return "pdf";
  if (head[0] === 0x50 && head[1] === 0x4b) return "word"; // OOXML zip (docx/pptx/xlsx)
  if (head[0] === 0xd0 && head[1] === 0xcf) return "word"; // legacy OLE2 (doc/xls/ppt)
  if (head[0] === 0x89 && head[1] === 0x50) return "image"; // PNG
  if (head[0] === 0xff && head[1] === 0xd8) return "image"; // JPEG
  return "unknown";
}

/** Keep only readable characters from a binary blob (legacy .doc salvage). */
function salvageStrings(bytes: Uint8Array): string {
  const raw = new TextDecoder("utf-8", { fatal: false }).decode(bytes);
  return raw
    .replace(/[^\P{C}\n\t]/gu, " ")
    .replace(/[ \t]{2,}/g, " ")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

/** Extract text from OOXML (docx / pptx) by unzipping and stripping XML tags. */
async function extractOoxmlText(bytes: Uint8Array): Promise<string> {
  try {
    const JSZip = (await import("https://esm.sh/jszip@3.10.1")).default;
    const zip = await JSZip.loadAsync(bytes);
    const parts: string[] = [];
    const names = Object.keys(zip.files).filter((n) =>
      /^word\/(document|header\d*|footer\d*|footnotes|endnotes)\.xml$/.test(n) ||
      /^ppt\/slides\/slide\d+\.xml$/.test(n) ||
      /^ppt\/notesSlides\/notesSlide\d+\.xml$/.test(n)
    ).sort();

    for (const n of names) {
      const xml = await zip.files[n].async("string");
      const text = xml
        .replace(/<\/w:p>|<\/a:p>|<w:br\s*\/>/g, "\n")
        .replace(/<w:tab\s*\/>/g, "\t")
        .replace(/<[^>]+>/g, "")
        .replace(/&lt;/g, "<").replace(/&gt;/g, ">")
        .replace(/&amp;/g, "&").replace(/&quot;/g, '"').replace(/&apos;/g, "'")
        .replace(/[ \t]{2,}/g, " ")
        .replace(/\n{3,}/g, "\n\n")
        .trim();
      if (text.length > 0) parts.push(text);
    }
    return parts.join("\n\n").trim();
  } catch (e) {
    console.error("[ooxml] extraction failed:", e);
    return "";
  }
}



interface Normalized {
  audience?: "external" | "internal";
  domain?: string;
  docType?: string;
  content: string;
  distilled: boolean;
}

/**
 * Turn raw extracted text into clean, de-identified knowledge and classify it.
 * For support email threads (Glassix) this distills the thread into a generic
 * problem→solution note and routes it to the customer (external) or staff
 * (internal) base by relevance. Non-support docs are kept but still scrubbed.
 * Falls back to a plain PII scrub if the AI is unavailable or fails.
 */
async function normalizeKnowledge(rawText: string): Promise<Normalized> {
  const disabled = Deno.env.get("KNOWLEDGE_DISTILL") === "false";
  if (disabled || !ociKey()) {
    return { content: scrubPII(rawText), distilled: false };
  }

  const prompt = `You normalize a raw document into clean knowledge for a TripEX/TAS travel & expense support assistant.

Rules:
1. REMOVE all personal / customer-identifying data: person names, company/customer names, email addresses, phone numbers, ticket numbers, TAS/trip numbers, signatures, email headers (From/To/Sent/Subject). Never keep them.
2. If the document is a SUPPORT EMAIL THREAD, distill it into a GENERIC, reusable note: the problem/question and the resolution/answer, written as instructions that apply to ANY customer. Drop small talk, greetings, auto-replies.
3. If it is a guide/policy/reference, keep the substantive content but still remove identifiers.
4. Decide "audience":
   - "external" = safe & useful for END CUSTOMERS (how-to, general product answers).
   - "internal" = for SUPPORT STAFF only (internal troubleshooting, system/config/back-office steps, anything customer-specific or operational).
5. Pick "domain" (short slug like travel, expenses, invoices, billing, policy, technical, general) and "doc_type" (faq, guide, troubleshooting, policy, reference, other).

Return ONLY compact JSON: {"audience":"external|internal","domain":"...","doc_type":"...","content":"<clean text>"}.

RAW DOCUMENT:
"""
${rawText.slice(0, 24000)}
"""`;

  try {
    let raw = await ociChatText(prompt, 4096);
    // Strip markdown fences if present
    raw = raw.replace(/^```(json)?/i, "").replace(/```$/i, "").trim();
    const start = raw.indexOf("{");
    const end = raw.lastIndexOf("}");
    if (start === -1 || end === -1) return { content: scrubPII(rawText), distilled: false };
    const parsed = JSON.parse(raw.slice(start, end + 1));
    const content = scrubPII(String(parsed.content || "").trim());
    if (content.length < 5) return { content: scrubPII(rawText), distilled: false };
    return {
      audience: parsed.audience === "internal" ? "internal" : "external",
      domain: typeof parsed.domain === "string" ? parsed.domain : undefined,
      docType: typeof parsed.doc_type === "string" ? parsed.doc_type : undefined,
      content,
      distilled: true,
    };
  } catch (e) {
    console.error("[normalize] failed:", e);
    return { content: scrubPII(rawText), distilled: false };
  }
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const authHeader = req.headers.get("Authorization");
    if (!authHeader) {
      return new Response(JSON.stringify({ error: "Missing authorization" }), {
        status: 401,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabaseAnonKey = Deno.env.get("SUPABASE_ANON_KEY")!;

    // Auth check with user token
    const userClient = createClient(supabaseUrl, supabaseAnonKey, {
      global: { headers: { Authorization: authHeader } },
    });
    const { data: { user }, error: authError } = await userClient.auth.getUser();
    if (authError || !user) {
      return new Response(JSON.stringify({ error: "Unauthorized" }), {
        status: 401,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Use service role for DB operations
    const supabase = createClient(supabaseUrl, supabaseServiceKey);

    const { document_id } = await req.json();
    if (!document_id) {
      return new Response(JSON.stringify({ error: "document_id required" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Get document
    const { data: doc, error: docErr } = await supabase
      .from("knowledge_documents")
      .select("*")
      .eq("id", document_id)
      .single();

    if (docErr || !doc) {
      return new Response(JSON.stringify({ error: "Document not found" }), {
        status: 404,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Update status to processing
    await supabase
      .from("knowledge_documents")
      .update({ status: "processing" })
      .eq("id", document_id);

    try {
      // Download file from storage
      const { data: fileData, error: dlErr } = await supabase.storage
        .from("knowledge")
        .download(doc.file_url);

      if (dlErr || !fileData) {
        throw new Error("Failed to download file: " + (dlErr?.message || "unknown"));
      }

      let extractedText = "";
      const fileType = (doc.file_type || "").toLowerCase();
      const fileName = (doc.file_name || "").toLowerCase();
      const headBytes = new Uint8Array(await fileData.slice(0, 8).arrayBuffer());
      const kind = detectKind(fileType, fileName, headBytes);
      console.log(`[process] file=${fileName} mime=${fileType} kind=${kind}`);

      const isSpreadsheet = kind === "spreadsheet";

      if (isSpreadsheet) {

        const buffer = await fileData.arrayBuffer();
        const uint8 = new Uint8Array(buffer);

        try {
          // type:"array" lets SheetJS auto-detect the format (xls vs xlsx vs csv).
          const workbook = XLSX.read(uint8, { type: "array" });
          const parts: string[] = [];
          for (const sheetName of workbook.SheetNames) {
            const sheet = workbook.Sheets[sheetName];
            if (!sheet) continue;
            // CSV keeps rows/columns readable for the model.
            const csv = XLSX.utils.sheet_to_csv(sheet, { blankrows: false });
            if (csv && csv.trim().length > 0) {
              parts.push(`--- ${sheetName} ---`);
              parts.push(csv.trim());
            }
          }
          extractedText = parts.join("\n\n");
        } catch (xlsErr) {
          console.error("SheetJS extraction failed:", xlsErr);
        }

        // Fallback 1: many "report .xls" exports are really HTML tables (or CSV)
        // saved with an .xls extension. Decode as text and strip tags.
        if (!extractedText || extractedText.length < 20) {
          try {
            const asText = new TextDecoder("utf-8").decode(uint8);
            const looksHtml = /<\s*(table|tr|td|html|body)/i.test(asText);
            if (looksHtml) {
              const stripped = asText
                .replace(/<\s*(br|\/tr|\/p)\s*>/gi, "\n")
                .replace(/<\s*\/td\s*>/gi, "\t")
                .replace(/<[^>]+>/g, " ")
                .replace(/&nbsp;/gi, " ")
                .replace(/&amp;/gi, "&")
                .replace(/[ \t]+/g, " ")
                .replace(/\n{2,}/g, "\n")
                .trim();
              if (stripped.length > extractedText.length) extractedText = stripped;
            } else if (asText.trim().length > 20 && /[,;\t]/.test(asText)) {
              // Looks like delimited/plain text (CSV-ish)
              extractedText = asText.trim();
            }
          } catch (txtErr) {
            console.error("Text fallback failed:", txtErr);
          }
        }

        // Fallback 2: OCI vision only if nothing else produced usable text.
        if ((!extractedText || extractedText.length < 20) && ociKey()) {
          try {
            const base64 = base64Encode(uint8);
            const aiText = await ociVision(
              "Extract ALL text and data from this spreadsheet. Output every sheet, every row, every cell as plain text. Do NOT summarize.",
              `data:${doc.file_type};base64,${base64}`,
              8192,
            );
            if (aiText.length > extractedText.length) extractedText = aiText;
          } catch (e) {
            console.error("[xls] OCI vision fallback failed:", e);
          }
        }

        console.log("Spreadsheet extracted text length:", extractedText.length, "preview:", extractedText.substring(0, 300));

        if (!extractedText || extractedText.length < 20) {
          throw new Error("Could not extract text from spreadsheet. Try uploading as CSV instead.");
        }
      } else if (kind === "text") {
        // Plain text files
        extractedText = await fileData.text();
      } else if (kind === "pdf") {
        // PDF: extract embedded text locally (no AI / no quota). Most support
        // emails are text-based, so this covers them for free.
        const bytes = new Uint8Array(await fileData.arrayBuffer());
        extractedText = await extractPdfTextLocal(bytes);

        // Scanned PDF (no embedded text) → OCI vision as a fallback.
        if (extractedText.length < 20 && ociKey()) {
          try {
            const base64 = base64Encode(bytes);
            extractedText = await ociVision(
              "Extract ALL text content from this document. Return ONLY the raw text, preserving structure.",
              `data:application/pdf;base64,${base64}`,
              4096,
            );
          } catch (e) {
            console.error("[pdf] OCI vision fallback failed:", e);
          }
        }
      } else if (kind === "word") {
        // Word/PowerPoint (OOXML): unzip and read the XML parts locally.
        const bytes = new Uint8Array(await fileData.arrayBuffer());
        extractedText = await extractOoxmlText(bytes);
        if (extractedText.length < 20) {
          // Legacy .doc (OLE2) or odd formats → salvage printable strings.
          extractedText = salvageStrings(bytes);
        }
        if (extractedText.length < 20) {
          throw new Error("Could not extract text from Word document");
        }
      } else if (kind === "image") {

        // Use Oracle AI to describe image content
        const ORACLE_API_KEY =
          Deno.env.get("oracleapikey") ||
          Deno.env.get("oracleapikey_2") ||
          Deno.env.get("invoice");
        if (!ORACLE_API_KEY) {
          throw new Error("Oracle API key not configured");
        }

        const buffer = await fileData.arrayBuffer();
        const base64 = base64Encode(new Uint8Array(buffer));

        const response = await fetch(
          "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions",
          {
            method: "POST",
            headers: {
              Authorization: `Bearer ${ORACLE_API_KEY}`,
              "Content-Type": "application/json",
            },
            body: JSON.stringify({
              model: "meta.llama-4-maverick-17b-128e-instruct-fp8",
              messages: [
                {
                  role: "system",
                  content: "Describe this image in detail. Extract any text visible in the image. Describe charts, tables, diagrams if present. Be thorough.",
                },
                {
                  role: "user",
                  content: [
                    { type: "text", text: "Describe this image and extract all text:" },
                    {
                      type: "image_url",
                      image_url: { url: `data:${doc.file_type};base64,${base64}` },
                    },
                  ],
                },
              ],
              max_tokens: 4096,
              temperature: 0,
            }),
          }
        );

        if (!response.ok) {
          const errText = await response.text();
          throw new Error(`Oracle AI error: ${response.status} - ${errText}`);
        }

        const aiData = await response.json();
        extractedText = aiData.choices?.[0]?.message?.content || "";
      } else {
        // Last resort: treat as UTF-8 text (covers unknown/octet-stream mimes).
        const raw = await fileData.text();
        const printable = raw.replace(/[^\P{C}\n\t]/gu, "");
        if (printable.trim().length < 20) {
          throw new Error(`Unsupported file type: ${fileType} (${fileName})`);
        }
        extractedText = printable;
      }


      // Sanitize: remove null bytes that crash Postgres
      extractedText = extractedText.replace(/\u0000/g, "");

      if (!extractedText.trim()) {
        throw new Error("No text could be extracted from the file");
      }

      // ── Normalize: strip PII, distill support threads, classify audience ──
      const norm = await normalizeKnowledge(extractedText);
      const finalText = norm.content || extractedText;

      // Persist the AI's audience/domain/type classification (route by relevance).
      const metaUpdate: Record<string, unknown> = {};
      if (norm.audience) metaUpdate.audience = norm.audience;
      if (norm.domain) metaUpdate.domain = norm.domain;
      if (norm.docType) metaUpdate.doc_type = norm.docType;
      if (Object.keys(metaUpdate).length > 0) {
        await supabase.from("knowledge_documents").update(metaUpdate).eq("id", document_id);
      }
      console.log(`[process] distilled=${norm.distilled} audience=${norm.audience ?? "(kept)"} textLen=${finalText.length}`);

      // Chunk the cleaned text
      const chunks = chunkText(finalText);

      // Delete existing chunks for this document
      await supabase
        .from("knowledge_chunks")
        .delete()
        .eq("document_id", document_id);

      // Insert new chunks
      const chunkRows = chunks.map((content, idx) => ({
        document_id,
        content,
        chunk_index: idx,
      }));

      const { error: insertErr } = await supabase
        .from("knowledge_chunks")
        .insert(chunkRows);

      if (insertErr) throw insertErr;

      // Update status to ready
      await supabase
        .from("knowledge_documents")
        .update({ status: "ready" })
        .eq("id", document_id);

      return new Response(JSON.stringify({
        success: true,
        chunks_created: chunks.length,
        text_length: finalText.length,
        distilled: norm.distilled,
        audience: norm.audience,
      }), {
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    } catch (processErr) {
      console.error("Processing error:", processErr);
      await supabase
        .from("knowledge_documents")
        .update({ status: "error" })
        .eq("id", document_id);

      return new Response(JSON.stringify({
        success: false,
        error: processErr instanceof Error ? processErr.message : "Unknown error",
      }), {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
  } catch (error) {
    console.error("process-knowledge error:", error);
    return new Response(
      JSON.stringify({ error: error instanceof Error ? error.message : "Unknown error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
