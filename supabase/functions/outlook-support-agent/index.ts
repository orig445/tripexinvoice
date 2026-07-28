// Outlook Customer Support Agent — polls unread emails from the shared support
// mailbox, generates a Milo reply grounded in the knowledge base, and either
// creates a draft reply or auto-sends based on configuration.
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers":
    "authorization, x-client-info, apikey, content-type",
};

const GATEWAY = "https://connector-gateway.lovable.dev/microsoft_outlook";
const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY")!;
const OUTLOOK_KEY = Deno.env.get("MICROSOFT_OUTLOOK_API_KEY")!;
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

// AI reply generation runs on Oracle OCI (same key the chat uses), NOT the
// Lovable AI gateway — the Lovable gateway ran out of credits (HTTP 402).
const OCI_ENDPOINT =
  "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const OCI_MODEL = Deno.env.get("OCI_MODEL") || "meta.llama-4-maverick-17b-128e-instruct-fp8";
function ociKey(): string {
  return Deno.env.get("oracleapikey_2") || Deno.env.get("oracleapikey") || Deno.env.get("invoice") || "";
}

function gwHeaders(extra: Record<string, string> = {}) {
  return {
    Authorization: `Bearer ${LOVABLE_API_KEY}`,
    "X-Connection-Api-Key": OUTLOOK_KEY,
    ...extra,
  };
}

function stripHtml(html: string): string {
  return html
    .replace(/<style[\s\S]*?<\/style>/gi, "")
    .replace(/<script[\s\S]*?<\/script>/gi, "")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/\s+/g, " ")
    .trim();
}

async function searchKB(supabase: any, query: string): Promise<string> {
  try {
    const { data } = await supabase.rpc("search_knowledge", {
      query_text: query,
      max_results: 8,
    });
    if (!data || data.length === 0) return "";

    // Email replies go to CUSTOMERS → only external (or legacy untagged) docs.
    const ids = [...new Set(data.map((d: any) => d.document_id).filter(Boolean))];
    let allowed = new Set<string>(ids);
    if (ids.length > 0) {
      const { data: docs, error } = await supabase
        .from("knowledge_documents")
        .select("id, audience")
        .in("id", ids);
      if (!error && docs) {
        allowed = new Set(
          docs.filter((d: any) => (d.audience ?? "external") === "external").map((d: any) => d.id),
        );
      }
    }

    return data
      .filter((d: any) => allowed.has(d.document_id))
      .slice(0, 5)
      .map((d: any) => `[${d.file_name}]: ${d.content}`)
      .join("\n\n");
  } catch {
    return "";
  }
}

async function generateReply(
  subject: string,
  body: string,
  fromName: string,
  kbContext: string,
  signature: string,
): Promise<string> {
  const systemPrompt = `You are Milo 🦊 — a friendly, professional customer support assistant for TripEX (Travel & Expense Management). You reply to customer support emails.

RULES:
- Always respond in English (regardless of the customer's language).
- Ground your answer STRICTLY in the Knowledge Base Context below. Never invent facts, prices, features, or policies.
- If the Knowledge Base does not contain the answer, say so politely and offer to escalate to a human agent.
- Never reveal other customers' personal data (names, emails, ticket IDs).
- Be warm, concise, and helpful. Use short paragraphs.
- Do NOT include a subject line or "From:"/"To:" headers — only the email body.
- End the email with this signature exactly:
${signature}

Knowledge Base Context:
${kbContext || "(no relevant knowledge base entries found)"}`;

  const userPrompt = `Customer name: ${fromName || "Customer"}
Subject: ${subject}

Email body:
${body}

Write the reply email body (plain text, no headers).`;

  const key = ociKey();
  if (!key) throw new Error("Oracle API key not configured (oracleapikey_2 / oracleapikey / invoice)");

  const resp = await fetch(OCI_ENDPOINT, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${key}`,
    },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: userPrompt },
      ],
      max_tokens: 2048,
      temperature: 0.3,
    }),
  });

  if (!resp.ok) {
    const t = await resp.text();
    throw new Error(`OCI AI ${resp.status}: ${t}`);
  }
  const j = await resp.json();
  return j.choices?.[0]?.message?.content?.trim() ?? "";
}

// Fetch a single Outlook message by id (for the manual "send test reply" button).
async function fetchMessage(msgId: string): Promise<any | null> {
  const url = `${GATEWAY}/me/messages/${msgId}?$select=id,conversationId,subject,bodyPreview,body,from,receivedDateTime,isRead`;
  const resp = await fetch(url, { headers: gwHeaders() });
  if (!resp.ok) return null;
  return await resp.json();
}

// Process one message: generate a reply and draft/send it. Used by both the
// polling loop and the manual per-email button (force=true bypasses the
// "already processed" skip so an email can be retried).
async function processOne(
  supabase: any,
  cfg: any,
  m: any,
  opts: { force?: boolean; mode?: string } = {},
): Promise<{ status: string; error?: string; reply?: string }> {
  const force = opts.force ?? false;
  const mode = opts.mode ?? cfg.mode ?? "draft";
  const signature = cfg.signature || "Best regards,\nMilo — TripEX Support";

  const msgId = m.id;
  const from = m.from?.emailAddress?.address ?? "";
  const fromName = m.from?.emailAddress?.name ?? "";
  const subject = m.subject ?? "(no subject)";
  const receivedAt = m.receivedDateTime;
  const rawBody = m.body?.contentType === "html"
    ? stripHtml(m.body?.content ?? "")
    : (m.body?.content ?? m.bodyPreview ?? "");

  const { data: existing } = await supabase
    .from("outlook_processed_emails")
    .select("id")
    .eq("message_id", msgId)
    .maybeSingle();

  if (existing && !force) return { status: "skipped" };

  // Reuse the existing row on retry; otherwise insert a fresh pending row.
  let rowId: string;
  if (existing) {
    rowId = existing.id;
    await supabase.from("outlook_processed_emails")
      .update({ status: "pending", error_message: null }).eq("id", rowId);
  } else {
    const { data: row } = await supabase
      .from("outlook_processed_emails")
      .insert({
        message_id: msgId,
        conversation_id: m.conversationId,
        from_address: from,
        from_name: fromName,
        subject,
        received_at: receivedAt,
        body_preview: rawBody.slice(0, 500),
        status: "pending",
      })
      .select("id")
      .single();
    rowId = row!.id;
  }

  try {
    const query = `${subject}\n${rawBody}`.slice(0, 800);
    const kb = await searchKB(supabase, query);
    const reply = await generateReply(subject, rawBody, fromName, kb, signature);
    if (!reply) throw new Error("Empty AI reply");

    if (mode === "auto_reply") {
      const replyResp = await fetch(`${GATEWAY}/me/messages/${msgId}/reply`, {
        method: "POST",
        headers: gwHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ comment: reply }),
      });
      if (!replyResp.ok) throw new Error(`Outlook reply failed ${replyResp.status}: ${await replyResp.text()}`);
      await fetch(`${GATEWAY}/me/messages/${msgId}`, {
        method: "PATCH",
        headers: gwHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ isRead: true }),
      });
      await supabase.from("outlook_processed_emails")
        .update({ reply_text: reply, status: "sent" }).eq("id", rowId);
      return { status: "sent", reply };
    } else {
      const draftResp = await fetch(`${GATEWAY}/me/messages/${msgId}/createReply`, {
        method: "POST",
        headers: gwHeaders({ "Content-Type": "application/json" }),
      });
      if (!draftResp.ok) throw new Error(`Outlook createReply failed ${draftResp.status}: ${await draftResp.text()}`);
      const draft = await draftResp.json();
      const bodyHtml = reply.split("\n").map((l) => `<p>${l.replace(/</g, "&lt;").replace(/>/g, "&gt;")}</p>`).join("");
      const patchResp = await fetch(`${GATEWAY}/me/messages/${draft.id}`, {
        method: "PATCH",
        headers: gwHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ body: { contentType: "HTML", content: bodyHtml } }),
      });
      if (!patchResp.ok) throw new Error(`Outlook draft update failed ${patchResp.status}: ${await patchResp.text()}`);
      await supabase.from("outlook_processed_emails")
        .update({ reply_text: reply, status: "draft_created" }).eq("id", rowId);
      return { status: "draft_created", reply };
    }
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    await supabase.from("outlook_processed_emails")
      .update({ status: "failed", error_message: msg }).eq("id", rowId);
    return { status: "failed", error: msg };
  }
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });

  const supabase = createClient(SUPABASE_URL, SERVICE_ROLE);
  const summary = { fetched: 0, processed: 0, sent: 0, drafts: 0, skipped: 0, errors: 0 as number };
  const details: any[] = [];
  const reqBody = await req.json().catch(() => ({} as any));

  try {
    // Load config
    const { data: cfg } = await supabase
      .from("outlook_agent_config")
      .select("*")
      .limit(1)
      .maybeSingle();

    if (!cfg) {
      return new Response(JSON.stringify({ error: "Agent not configured" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    if (!cfg.enabled) {
      return new Response(JSON.stringify({ skipped: true, reason: "Agent disabled" }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const folder = cfg.folder || "inbox";

    // ── Manual single-email processing (the per-email "send test reply" button) ──
    if (reqBody?.message_id) {
      const one = await fetchMessage(reqBody.message_id);
      if (!one) {
        return new Response(JSON.stringify({ error: "Message not found in mailbox" }), {
          status: 404, headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      const r = await processOne(supabase, cfg, one, { force: true, mode: reqBody.mode || cfg.mode });
      return new Response(JSON.stringify({ single: r }), {
        status: r.status === "failed" ? 500 : 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Fetch unread messages
    const listUrl = `${GATEWAY}/me/mailFolders/${folder}/messages?$filter=isRead eq false&$top=10&$orderby=receivedDateTime desc&$select=id,conversationId,subject,bodyPreview,body,from,receivedDateTime,isRead`;
    const listResp = await fetch(listUrl, { headers: gwHeaders() });
    if (!listResp.ok) {
      const errBody = await listResp.text();
      return new Response(
        JSON.stringify({ error: "Outlook fetch failed", status: listResp.status, details: errBody }),
        { status: listResp.status, headers: { ...corsHeaders, "Content-Type": "application/json" } },
      );
    }
    const listJson = await listResp.json();
    const messages = listJson.value ?? [];
    summary.fetched = messages.length;

    for (const m of messages) {
      const r = await processOne(supabase, cfg, m);
      if (r.status === "sent") summary.sent++;
      else if (r.status === "draft_created") summary.drafts++;
      else if (r.status === "skipped") summary.skipped++;
      else if (r.status === "failed") summary.errors++;
      if (r.status !== "skipped") summary.processed++;
      details.push({ subject: m.subject, from: m.from?.emailAddress?.address, status: r.status, error: r.error });
    }

    await supabase
      .from("outlook_agent_config")
      .update({ last_run_at: new Date().toISOString() })
      .eq("id", cfg.id);

    return new Response(JSON.stringify({ summary, details }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    return new Response(JSON.stringify({ error: msg, summary }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
