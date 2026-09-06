/**
 * API Service Layer
 * 
 * Central service for all backend API calls.
 * Replace VITE_API_BASE_URL with your C# .NET backend URL.
 * 
 * When running against Supabase Edge Functions (dev/sandbox), 
 * it falls back to Supabase functions.invoke().
 * 
 * When VITE_API_BASE_URL is set, all calls go to your external backend.
 */

import { supabase } from "@/integrations/supabase/client";

// Set this env var to point to your C# backend, e.g. "https://api.tripex.io"
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || "";

/** Whether we're using an external backend */
export const isExternalBackend = !!API_BASE_URL;

/** Get auth token for API calls */
async function getAuthToken(): Promise<string | null> {
  const { data: { session } } = await supabase.auth.getSession();
  return session?.access_token || null;
}

/** Generic API call to external backend */
async function callExternalAPI<T = any>(
  endpoint: string,
  body: Record<string, any>,
): Promise<{ data: T | null; error: Error | null }> {
  try {
    const token = await getAuthToken();
    const res = await fetch(`${API_BASE_URL}${endpoint}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify(body),
    });

    if (!res.ok) {
      const errText = await res.text();
      return { data: null, error: new Error(`API error ${res.status}: ${errText}`) };
    }

    const data = await res.json();
    return { data, error: null };
  } catch (err: any) {
    return { data: null, error: err };
  }
}

/** Fallback: call Supabase Edge Function */
async function callSupabaseFunction<T = any>(
  functionName: string,
  body: Record<string, any>,
): Promise<{ data: T | null; error: Error | null }> {
  const { data, error } = await supabase.functions.invoke(functionName, { body });
  if (!error) return { data: data as T, error: null };

  // supabase.functions.invoke returns a generic "non-2xx status" message and
  // hides the real reason in error.context (the raw Response). Pull the
  // function's JSON { error } body so callers can show a useful message.
  let detail = error.message || String(error);
  const ctx = (error as any).context;
  if (ctx && typeof ctx.json === "function") {
    try {
      const respBody = await ctx.json();
      if (respBody?.error) detail = respBody.error;
    } catch {
      /* body not JSON / already consumed — keep generic message */
    }
  }
  return { data: null, error: new Error(detail) };
}

// ─── Public API Methods ───

/** Send a chat message (text) to AI Router */
export async function sendChatMessage(params: {
  text: string;
  source?: string;
  sessionToken?: string | null;
  userDate?: string;
  userTime?: string;
  userTimezone?: string;
  audience?: KnowledgeAudience;
}) {
  const body = {
    text: params.text,
    type: "text",
    source: params.source || "web",
    sessionToken: params.sessionToken || null,
    userDate: params.userDate || "",
    userTime: params.userTime || "",
    userTimezone: params.userTimezone || "",
    audience: params.audience || "external",
  };

  if (isExternalBackend) {
    return callExternalAPI("/api/chat", body);
  }
  return callSupabaseFunction("ai-router", body);
}

/** Send an image (base64) for scanning via AI Router */
export async function sendImageForScan(params: {
  base64: string;
  source?: string;
  sessionToken?: string | null;
  audience?: KnowledgeAudience;
}) {
  const body = {
    text: params.base64,
    type: "image",
    source: params.source || "web",
    sessionToken: params.sessionToken || null,
    audience: params.audience || "external",
  };

  if (isExternalBackend) {
    return callExternalAPI("/api/chat", body);
  }
  return callSupabaseFunction("ai-router", body);
}

/** Direct invoice analysis (standalone OCR) */
export async function analyzeInvoice(imageBase64: string) {
  if (isExternalBackend) {
    return callExternalAPI("/api/invoice/analyze", { imageBase64 });
  }
  return callSupabaseFunction("analyze-invoice", { imageBase64 });
}

/** Bulk train: scan receipt and save as training sample */
export async function bulkTrainInvoice(imageBase64: string, country?: string) {
  if (isExternalBackend) {
    return callExternalAPI("/api/invoice/bulk-train", { imageBase64, country });
  }
  // Step 1: Scan via analyze-invoice
  const scanResult = await callSupabaseFunction("analyze-invoice", { imageBase64 });
  if (scanResult.error || !scanResult.data?.success) {
    return scanResult;
  }
  // Step 2: Save as training sample via ocr-training
  const { data: { user } } = await supabase.auth.getUser();
  const saveResult = await callSupabaseFunction("ocr-training", {
    action: "bulk-train",
    extractedData: scanResult.data.data,
    country: country || "IL",
    userId: user?.id || null,
  });
  if (saveResult.error) {
    return saveResult;
  }
  // Return combined result
  return {
    data: {
      success: true,
      fields: scanResult.data.data,
      sampleId: saveResult.data?.sampleId,
    },
    error: null,
  };
}

/** Verify or reject a training sample */
export async function verifyTrainingSample(sampleId: string, isCorrect: boolean, corrections?: Record<string, string>) {
  if (isExternalBackend) {
    return callExternalAPI("/api/invoice/verify-sample", { sampleId, isCorrect, corrections });
  }
  return callSupabaseFunction("ocr-training", { action: "verify-sample", sampleId, isCorrect, corrections });
}

/** Rebuild OCR patterns from verified samples */
export async function rebuildOcrPatterns() {
  if (isExternalBackend) {
    return callExternalAPI("/api/invoice/rebuild-patterns", {});
  }
  return callSupabaseFunction("ocr-training", { action: "rebuild-patterns" });
}

/** Get OCR training statistics */
export async function getTrainingStats() {
  if (isExternalBackend) {
    const token = await getAuthToken();
    const res = await fetch(`${API_BASE_URL}/api/invoice/training-stats`, {
      headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    });
    const data = await res.json();
    return { data, error: null };
  }
  return callSupabaseFunction("ocr-training", { action: "stats" });
}

/** Trigger knowledge document processing */
export async function processKnowledgeDocument(documentId: string) {
  if (isExternalBackend) {
    return callExternalAPI("/api/knowledge/process", { document_id: documentId });
  }
  return callSupabaseFunction("process-knowledge", { document_id: documentId });
}

// ─── Knowledge Base: upload / list / tag / delete ───

/** Normalized shape used by the admin UI regardless of backend. */
export interface KnowledgeDoc {
  id: string;
  file_name: string;
  file_type: string;
  file_size: number | null;
  domain: string | null;
  doc_type: string | null;
  description: string | null;
  audience: string | null;
  source: string;
  external_url: string | null;
  file_url: string | null;
  status: string;
  created_at: string;
}

/** Which chatbot a knowledge base belongs to. */
export type KnowledgeAudience = "external" | "internal";

/** Map a .NET (camelCase) or Supabase (snake_case) row to the common shape. */
function normalizeDoc(d: any): KnowledgeDoc {
  return {
    id: d.id,
    file_name: d.fileName ?? d.file_name ?? "",
    file_type: d.fileType ?? d.file_type ?? "",
    file_size: d.fileSize ?? d.file_size ?? null,
    domain: d.domain ?? null,
    doc_type: d.docType ?? d.doc_type ?? null,
    description: d.description ?? null,
    audience: d.audience ?? null,
    source: d.source ?? "upload",
    external_url: d.externalUrl ?? d.external_url ?? null,
    file_url: d.fileUrl ?? d.file_url ?? null,
    status: d.status ?? "pending",
    created_at: d.createdAt ?? d.created_at ?? new Date().toISOString(),
  };
}

/** Get a temporary download URL for an uploaded knowledge file. */
export async function getKnowledgeDownloadUrl(
  doc: KnowledgeDoc,
): Promise<{ url: string | null; error: Error | null }> {
  const path = doc.file_url;
  if (!path) {
    if (doc.external_url) return { url: doc.external_url, error: null };
    return { url: null, error: new Error("No source file available for download") };
  }
  if (/^https?:\/\//i.test(path)) return { url: path, error: null };

  if (isExternalBackend) {
    return { url: `${API_BASE_URL}/api/knowledge/documents/${doc.id}/download`, error: null };
  }

  const { data, error } = await supabase.storage
    .from("knowledge")
    .createSignedUrl(path, 300, { download: doc.file_name });
  if (error || !data?.signedUrl) return { url: null, error: new Error(String(error?.message || "signing failed")) };
  return { url: data.signedUrl, error: null };
}


/** Trigger an on-demand sync of the external knowledge sources (SharePoint / Zoho CRM).
 *  Runs against the internal knowledge base. Returns the per-source summary. */
export async function triggerKnowledgeSync(
  sources?: Array<"sharepoint" | "zoho_crm">,
): Promise<{ data: any; error: Error | null }> {
  return callSupabaseFunction("sync-knowledge-sources", sources ? { sources } : {});
}

/**
 * Upload one file to the agent's RAG together with its tags.
 * Uses the .NET /api/knowledge/upload endpoint when an external backend is
 * configured; otherwise falls back to Supabase storage + a document row.
 */
export async function uploadKnowledgeFile(params: {
  file: File;
  domain?: string;
  docType?: string;
  description?: string;
  audience?: KnowledgeAudience;
}): Promise<{ data: any; error: Error | null }> {
  const audience: KnowledgeAudience = params.audience || "external";

  if (isExternalBackend) {
    try {
      const token = await getAuthToken();
      const form = new FormData();
      form.append("file", params.file);
      if (params.domain) form.append("domain", params.domain);
      if (params.docType) form.append("docType", params.docType);
      if (params.description) form.append("description", params.description);
      form.append("audience", audience);

      const res = await fetch(`${API_BASE_URL}/api/knowledge/upload`, {
        method: "POST",
        headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        body: form,
      });
      if (!res.ok) {
        const errText = await res.text();
        return { data: null, error: new Error(`API error ${res.status}: ${errText}`) };
      }
      return { data: await res.json(), error: null };
    } catch (err: any) {
      return { data: null, error: err };
    }
  }

  // Supabase fallback
  try {
    const ext = params.file.name.split(".").pop() || "bin";
    const filePath = `${crypto.randomUUID()}.${ext}`;
    const { error: uploadErr } = await supabase.storage.from("knowledge").upload(filePath, params.file);
    if (uploadErr) return { data: null, error: new Error(uploadErr.message) };

    const userId = (await supabase.auth.getUser()).data.user?.id;
    const baseRow = {
      file_name: params.file.name,
      file_type: params.file.type,
      file_url: filePath,
      file_size: params.file.size,
      uploaded_by: userId,
    };
    const tagRow = {
      domain: params.domain || null,
      doc_type: params.docType || null,
      description: params.description || null,
      audience,
    };

    // Try with tags; if the DB doesn't have the tag columns yet (schema not
    // migrated), retry without them so the upload + RAG still succeed.
    let insert = await supabase
      .from("knowledge_documents")
      .insert({ ...baseRow, ...tagRow })
      .select("id")
      .single();

    if (insert.error && isMissingColumnError(insert.error)) {
      // SAFETY: an internal document with no audience column would default to
      // external and could leak to the customer bot. Refuse rather than
      // silently mis-scope it — tell the user to run the migration.
      if (audience === "internal") {
        return {
          data: null,
          error: new Error(
            "The internal knowledge base requires a schema update (audience column). Run the provided SQL and try again.",
          ),
        };
      }
      insert = await supabase
        .from("knowledge_documents")
        .insert(baseRow)
        .select("id")
        .single();
    }

    if (insert.error) return { data: null, error: new Error(insert.error.message) };

    const processing = await processKnowledgeDocument(insert.data.id);
    if (processing.error) {
      return {
        data: { success: false, documentId: insert.data.id, fileName: params.file.name },
        error: processing.error,
      };
    }
    return {
      data: {
        success: true,
        documentId: insert.data.id,
        fileName: params.file.name,
        ...((processing.data as Record<string, unknown> | null) || {}),
      },
      error: null,
    };
  } catch (err: any) {
    return { data: null, error: err };
  }
}

/** True when a PostgREST error is caused by a column missing from the schema cache. */
function isMissingColumnError(error: any): boolean {
  const code = error?.code;
  const msg = (error?.message || "").toLowerCase();
  return code === "PGRST204" || (msg.includes("column") && msg.includes("schema cache"));
}

/** List knowledge documents for one audience (normalized). */
export async function listKnowledgeDocuments(
  audience: KnowledgeAudience = "external",
): Promise<{ data: KnowledgeDoc[]; error: Error | null }> {
  if (isExternalBackend) {
    try {
      const token = await getAuthToken();
      const res = await fetch(`${API_BASE_URL}/api/knowledge/documents?audience=${audience}`, {
        headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      });
      if (!res.ok) return { data: [], error: new Error(`API error ${res.status}`) };
      const rows = await res.json();
      return { data: (rows || []).map(normalizeDoc), error: null };
    } catch (err: any) {
      return { data: [], error: err };
    }
  }

  // Supabase: external includes legacy rows with a NULL audience; internal is exact.
  let query = supabase.from("knowledge_documents").select("*").order("created_at", { ascending: false });
  query =
    audience === "internal"
      ? query.eq("audience", "internal")
      : query.or("audience.eq.external,audience.is.null");

  let { data, error } = await query;

  // If the audience column doesn't exist yet, fall back gracefully:
  // external → show all existing docs; internal → empty (none can exist yet).
  if (error && isMissingColumnError(error)) {
    if (audience === "internal") return { data: [], error: null };
    ({ data, error } = await supabase
      .from("knowledge_documents")
      .select("*")
      .order("created_at", { ascending: false }));
  }

  return {
    data: (data || []).map(normalizeDoc),
    error: error ? new Error(String(error)) : null,
  };
}

/** Delete a knowledge document (file + chunks + row). */
export async function deleteKnowledgeDocument(id: string, fileUrl?: string): Promise<{ error: Error | null }> {
  if (isExternalBackend) {
    try {
      const token = await getAuthToken();
      const res = await fetch(`${API_BASE_URL}/api/knowledge/documents/${id}`, {
        method: "DELETE",
        headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      });
      if (!res.ok) return { error: new Error(`API error ${res.status}`) };
      return { error: null };
    } catch (err: any) {
      return { error: err };
    }
  }

  if (fileUrl) await supabase.storage.from("knowledge").remove([fileUrl]).catch(() => {});
  const { error } = await supabase.from("knowledge_documents").delete().eq("id", id);
  return { error: error ? new Error(String(error)) : null };
}

/** Update the tags (domain / type / description) of an existing document. */
export async function updateKnowledgeTags(
  id: string,
  tags: { domain?: string; docType?: string; description?: string },
): Promise<{ error: Error | null }> {
  if (isExternalBackend) {
    try {
      const token = await getAuthToken();
      const res = await fetch(`${API_BASE_URL}/api/knowledge/documents/${id}`, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify({ domain: tags.domain, docType: tags.docType, description: tags.description }),
      });
      if (!res.ok) return { error: new Error(`API error ${res.status}`) };
      return { error: null };
    } catch (err: any) {
      return { error: err };
    }
  }

  const { error } = await supabase
    .from("knowledge_documents")
    .update({ domain: tags.domain || null, doc_type: tags.docType || null, description: tags.description || null })
    .eq("id", id);
  // If the tag columns don't exist yet, treat it as a no-op rather than an error.
  if (error && isMissingColumnError(error)) return { error: null };
  return { error: error ? new Error(String(error)) : null };
}
