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
    const { data } = await supabase.rpc("search_knowledge", { query_text: query, max_results: 8 });
    if (!data || data.length === 0) return "";
    const ids = [...new Set(data.map((d: any) => d.document_id).filter(Boolean))];
    let allowed = new Set<string>(ids);
    if (ids.length > 0) {
      const { data: docs, error } = await supabase
        .from("knowledge_documents").select("id, audience").in("id", ids);
      if (!error && docs) {
        allowed = new Set(docs.filter((d: any) => (d.audience ?? "external") === "external").map((d: any) => d.id));
      }
    }
    return data.filter((d: any) => allowed.has(d.document_id)).slice(0, 5)
      .map((d: any) => `[${d.file_name}]: ${d.content}`).join("\n\n");
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
- Reply in the SAME LANGUAGE the customer used (Hebrew if they wrote Hebrew).
- Ground your answer STRICTLY in the Knowledge Base Context below. Never invent facts, prices, features or policies.
- PRIVACY: never reveal other customers' personal data — names, emails, phone numbers, company names, ticket/TAS/trip numbers.
- Keep it concise and friendly for chat (short paragraphs, no email headers/signature).
- ESCALATION: if the request is too complex, requires account-specific action, involves billing/refund/legal/security, or the Knowledge Base does NOT contain a clear answer — reply with a short polite message telling the customer you'll route them to human support at ${SUPPORT_EMAIL}, and END your reply with the exact token ${ESCALATE_TAG} on its own line. Do not include ${ESCALATE_TAG} when you were able to answer from the KB.

Knowledge Base Context:
${kb || "(no relevant knowledge base entries found)"}`;

  const resp = await fetch(OCI_ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${key}` },
    body: JSON.stringify({
      model: OCI_MODEL,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: `Customer${senderName ? ` (${senderName})` : ""} asks:\n${question}` },
      ],
      max_tokens: 1024,
      temperature: 0.3,
    }),
  });
  if (!resp.ok) throw new Error(`OCI AI ${resp.status}: ${await resp.text()}`);
  const j = await resp.json();
  const raw = (j.choices?.[0]?.message?.content ?? "").trim();
  const escalate = raw.includes(ESCALATE_TAG) || !kb;
  const reply = raw.replace(ESCALATE_TAG, "").trim();
  return { reply, escalate };
}

function isHebrew(s: string): boolean {
  return /[\u0590-\u05FF]/.test(s);
}

function escalationSuffix(question: string): string {
  return isHebrew(question)
    ? `\n\n📩 לפנייה מפורטת יותר, אנא כתבו לנו למייל: ${SUPPORT_EMAIL} — נציג אנושי יחזור אליכם בהקדם.`
    : `\n\n📩 For a more detailed request, please email us at: ${SUPPORT_EMAIL} — a human agent will get back to you shortly.`;
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
    try {
      const kb = await searchKB(supabase, text.slice(0, 800));
      reply = await generateReply(text, senderName, kb);
    } catch (e) {
      console.error("[whatsapp] generate failed:", e);
      reply = "מצטער, יש כרגע תקלה זמנית. נציג אנושי יחזור אליך בהקדם. 🙏";
    }
    if (!reply) reply = "לא הצלחתי למצוא תשובה מדויקת. אעביר את פנייתך לנציג אנושי. 🙏";

    await sendWhatsApp(chatId, reply);

    await supabase.from("chatbot_logs").insert({
      event_type: "whatsapp_reply",
      details: { chatId, senderName, question: text.slice(0, 500), reply: reply.slice(0, 1000) },
    }).catch(() => {});

    return ok();
  } catch (err) {
    console.error("[whatsapp] webhook error:", err);
    // Return 200 so Green API doesn't retry-storm on our internal errors.
    return ok();
  }
});
