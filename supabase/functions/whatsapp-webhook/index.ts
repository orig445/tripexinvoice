// WhatsApp customer-support webhook (Green API).
// Green API POSTs incoming WhatsApp messages here; we answer from the knowledge
// base (RAG) using Oracle OCI and send the reply back via Green API.
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

// Green API instance credentials (added as Supabase secrets).
const GREEN_ID = Deno.env.get("GreenAPI_idInstance") || "";
const GREEN_TOKEN = Deno.env.get("GreenAPI_apiTokenInstance") || "";
const GREEN_BASE = Deno.env.get("GreenAPI_apiUrl") || "https://api.green-api.com";
// Optional: protect the webhook URL with ?secret=... (set WHATSAPP_WEBHOOK_SECRET).
const WEBHOOK_SECRET = Deno.env.get("WHATSAPP_WEBHOOK_SECRET") || "";

// AI on Oracle OCI (same key the chat uses).
const OCI_ENDPOINT =
  "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const OCI_MODEL = Deno.env.get("OCI_MODEL") || "meta.llama-4-maverick-17b-128e-instruct-fp8";
function ociKey(): string {
  return Deno.env.get("oracleapikey_2") || Deno.env.get("oracleapikey") || Deno.env.get("invoice") || "";
}

// Retrieve external (customer) knowledge only — never internal docs.
async function searchKB(supabase: any, query: string): Promise<string> {
  try {
    // Fire multiple queries (full text + a few salient keywords) so Hebrew /
    // multi-word questions still hit the KB. Merge & de-dupe by chunk_id.
    const queries = new Set<string>([query]);
    const keywords = query
      .replace(/[^\p{L}\p{N}\s]/gu, " ")
      .split(/\s+/)
      .filter((w) => w.length >= 3);
    // Add up to 5 longest keywords as individual searches.
    keywords.sort((a, b) => b.length - a.length).slice(0, 5).forEach((k) => queries.add(k));

    const results: any[] = [];
    for (const q of queries) {
      const { data } = await supabase.rpc("search_knowledge", { query_text: q, max_results: 8 });
      if (data) results.push(...data);
    }
    if (results.length === 0) return "";

    const ids = [...new Set(results.map((d: any) => d.document_id).filter(Boolean))];
    let allowed = new Set<string>(ids);
    if (ids.length > 0) {
      const { data: docs, error } = await supabase
        .from("knowledge_documents").select("id, audience").in("id", ids);
      if (!error && docs) {
        allowed = new Set(docs.filter((d: any) => (d.audience ?? "external") === "external").map((d: any) => d.id));
      }
    }

    // De-dupe by chunk_id, keep top 8 by rank.
    const seen = new Set<string>();
    const unique = results
      .filter((d: any) => allowed.has(d.document_id))
      .filter((d: any) => { if (seen.has(d.chunk_id)) return false; seen.add(d.chunk_id); return true; })
      .sort((a: any, b: any) => (b.rank ?? 0) - (a.rank ?? 0))
      .slice(0, 8);

    return unique.map((d: any) => `[${d.file_name}]: ${d.content}`).join("\n\n");
  } catch {
    return "";
  }
}

const SUPPORT_EMAIL = Deno.env.get("SUPPORT_EMAIL") || "support@tripex.co.il";
// Marker the model uses to signal "I can't answer from the KB — escalate".
const ESCALATE_TAG = "[[ESCALATE]]";

async function generateReply(question: string, senderName: string, kb: string): Promise<{ reply: string; escalate: boolean }> {
  const key = ociKey();
  if (!key) throw new Error("Oracle API key not configured");

  const systemPrompt = `You are Milo 🦊 — a friendly, professional customer support assistant for TripEX (Travel & Expense / TAS) answering on WhatsApp.
RULES:
- ALWAYS reply in ENGLISH ONLY. Never reply in Hebrew or any other language, no matter what language the customer used. If the customer wrote in Hebrew, translate their intent internally and answer them in English.
- Ground your answer STRICTLY in the Knowledge Base Context below. USE the KB actively — if the KB contains information relevant to the customer's question (even partially), answer from it. Do NOT escalate when the KB has the answer.
- Never invent facts, prices, features or policies that aren't in the KB.
- PRIVACY: never reveal other customers' personal data — names, emails, phone numbers, company names, ticket/TAS/trip numbers.
- Keep it concise and friendly for chat (short paragraphs, no email headers/signature).
- ESCALATION: only escalate when the KB truly has NO relevant information, OR the request requires account-specific action (billing/refund/legal/security/access to a specific user's data). In those cases, briefly say you'll route them to human support at ${SUPPORT_EMAIL}, and END your reply with the exact token ${ESCALATE_TAG} on its own line. Never include ${ESCALATE_TAG} when the KB has an answer.

Knowledge Base Context:
${kb || "(no relevant knowledge base entries found)"}`;

  const resp = await fetch(OCI_ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${key}` },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: `Customer${senderName ? ` (${senderName})` : ""} asks (respond in English):\n${question}` },
      ],
      max_tokens: 1024,
      temperature: 0.3,
    }),
  });
  if (!resp.ok) throw new Error(`OCI AI ${resp.status}: ${await resp.text()}`);
  const j = await resp.json();
  const raw = (j.choices?.[0]?.message?.content ?? "").trim();
  // Only escalate when the model explicitly said so. Empty KB alone is NOT
  // enough — the model may still give a valid general support answer.
  const escalate = raw.includes(ESCALATE_TAG);
  const reply = raw.replace(ESCALATE_TAG, "").trim();
  return { reply, escalate };
}

function escalationSuffix(): string {
  return `\n\n📩 For a more detailed request, please email us at: ${SUPPORT_EMAIL} — a human agent will get back to you shortly.`;
}

async function sendWhatsApp(chatId: string, message: string): Promise<void> {
  const url = `${GREEN_BASE}/waInstance${GREEN_ID}/sendMessage/${GREEN_TOKEN}`;
  const resp = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ chatId, message }),
  });
  if (!resp.ok) throw new Error(`Green API send ${resp.status}: ${await resp.text()}`);
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });
  // Green API only POSTs; a GET is handy for a quick "is it alive" check.
  if (req.method === "GET") {
    return new Response(JSON.stringify({ ok: true, service: "whatsapp-webhook" }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Optional shared-secret gate (?secret=...).
  if (WEBHOOK_SECRET) {
    const url = new URL(req.url);
    if (url.searchParams.get("secret") !== WEBHOOK_SECRET) {
      return new Response(JSON.stringify({ error: "forbidden" }), {
        status: 403, headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
  }

  const ok = () =>
    new Response(JSON.stringify({ ok: true }), { headers: { ...corsHeaders, "Content-Type": "application/json" } });

  try {
    const body = await req.json().catch(() => ({} as any));

    // Only react to inbound customer messages — ignore delivery statuses, our own
    // outgoing messages, instance-state changes, etc. (prevents reply loops).
    if (body?.typeWebhook !== "incomingMessageReceived") return ok();

    const chatId: string = body?.senderData?.chatId ?? "";
    const senderName: string = body?.senderData?.senderName ?? "";
    const md = body?.messageData ?? {};
    const text: string =
      md?.textMessageData?.textMessage ??
      md?.extendedTextMessageData?.text ??
      "";

    // Only handle 1:1 text messages for now (skip groups and non-text).
    if (!chatId || !chatId.endsWith("@c.us") || !text.trim()) return ok();

    const supabase = createClient(SUPABASE_URL, SERVICE_ROLE);

    let reply = "";
    let escalated = false;
    try {
      const kb = await searchKB(supabase, text.slice(0, 800));
      const out = await generateReply(text, senderName, kb);
      reply = out.reply;
      escalated = out.escalate;
    } catch (e) {
      console.error("[whatsapp] generate failed:", e);
      reply = isHebrew(text)
        ? "מצטער, יש כרגע תקלה זמנית."
        : "Sorry, we're having a temporary issue.";
      escalated = true;
    }
    if (!reply) {
      reply = isHebrew(text)
        ? "לא הצלחתי למצוא תשובה מדויקת."
        : "I couldn't find a precise answer.";
      escalated = true;
    }
    if (escalated) reply += escalationSuffix(text);

    await sendWhatsApp(chatId, reply);

    try {
      await supabase.from("chatbot_logs").insert({
        event_type: "whatsapp_reply",
        details: { chatId, senderName, escalated, question: text.slice(0, 500), reply: reply.slice(0, 1000) },
      });
    } catch (e) {
      console.error("[whatsapp] log insert failed:", e);
    }

    return ok();
  } catch (err) {
    console.error("[whatsapp] webhook error:", err);
    // Return 200 so Green API doesn't retry-storm on our internal errors.
    return ok();
  }
});
