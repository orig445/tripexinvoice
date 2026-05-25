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
  return { data: data as T, error: error ? new Error(String(error)) : null };
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
}) {
  const body = {
    text: params.text,
    type: "text",
    source: params.source || "web",
    sessionToken: params.sessionToken || null,
    userDate: params.userDate || "",
    userTime: params.userTime || "",
    userTimezone: params.userTimezone || "",
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
}) {
  const body = {
    text: params.base64,
    type: "image",
    source: params.source || "web",
    sessionToken: params.sessionToken || null,
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

// ─────────────────────────────────────────────────────────────────
// Generic REST helpers (used by integrations pages)
// Support GET + POST to any endpoint on the external backend
// ─────────────────────────────────────────────────────────────────

async function restGet<T = any>(endpoint: string): Promise<T> {
  const token = await getAuthToken();
  const res = await fetch(`${API_BASE_URL}${endpoint}`, {
    method: "GET",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
  });
  if (!res.ok) {
    const errText = await res.text();
    throw new Error(`API error ${res.status}: ${errText}`);
  }
  return res.json();
}

async function restPost<T = any>(endpoint: string, body?: unknown): Promise<T> {
  const token = await getAuthToken();
  const res = await fetch(`${API_BASE_URL}${endpoint}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    const errText = await res.text();
    throw new Error(`API error ${res.status}: ${errText}`);
  }
  return res.json();
}

/** Convenience object for components that prefer apiService.get / .post style */
export const apiService = {
  get: restGet,
  post: restPost,
};
