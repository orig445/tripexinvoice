// ────────────────────────────────────────────────────────────────────────────
// TripEX WhatsApp support bot — SELF-CONTAINED, runs on Deno Deploy.
//
// Independent of Lovable/Supabase-functions deployment. Green API POSTs incoming
// WhatsApp messages here; we answer from the knowledge base (RAG, via the public
// anon key) using Oracle OCI, and reply via Green API. Falls back to built-in TAS
// knowledge when the RAG has no match.
//
// Deploy: https://deno.com/deploy → new project → deploy this file (link the repo
// path `deno-deploy/whatsapp-bot.ts`, or paste it into a Playground).
//
// Required environment variables (set in the Deno Deploy project settings):
//   GREENAPI_ID          Green API idInstance
//   GREENAPI_TOKEN       Green API apiTokenInstance
//   GREENAPI_BASE        (optional) e.g. https://7105.api.greenapi.com  [default https://api.green-api.com]
//   OCI_KEY              Oracle OCI GenAI API key (your oracleapikey_2 value)
//   OCI_MODEL            (optional) [default meta.llama-4-maverick-17b-128e-instruct-fp8]
//   SUPABASE_URL         your project URL (https://osuyokvyhiyvyhjrbcxm.supabase.co)
//   SUPABASE_ANON_KEY    the public anon/publishable key (VITE_SUPABASE_PUBLISHABLE_KEY)
//   WEBHOOK_SECRET       (optional) if set, the webhook URL must include ?secret=...
// ────────────────────────────────────────────────────────────────────────────

const GREENAPI_ID = Deno.env.get("GREENAPI_ID") ?? "";
const GREENAPI_TOKEN = Deno.env.get("GREENAPI_TOKEN") ?? "";
const GREENAPI_BASE = Deno.env.get("GREENAPI_BASE") ?? "https://api.green-api.com";
const OCI_KEY = Deno.env.get("OCI_KEY") ?? "";
const OCI_MODEL = Deno.env.get("OCI_MODEL") ?? "meta.llama-4-maverick-17b-128e-instruct-fp8";
const OCI_ENDPOINT =
  "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") ?? "";
const SUPABASE_ANON_KEY = Deno.env.get("SUPABASE_ANON_KEY") ?? "";
const WEBHOOK_SECRET = Deno.env.get("WEBHOOK_SECRET") ?? "";

// Condensed built-in TAS knowledge — used when the RAG returns nothing so the
// bot is still useful before the knowledge base is populated.
const BUILTIN_KNOWLEDGE = `TripEX / TAS (Travel & Expense) — key facts:
- A trip moves through statuses: Draft → TR Approval → Coordinator Approval → Reservation → Proposal Approval → Approved → Issued → Active → Travel Completed → Expense Report → Expense Approval → Expense Approved → Closed. Cancellation: Pending for Cancellation → Cancelled.
- You can only submit an Expense Report after the trip is fully approved and Issued, you've travelled (Travel Completed) and filled End-Trip Confirmation.
- Reports are found by code+name (e.g. 1006 Travel Status, 7001 Invoice List, 1002 Travel Expenses). Most reports exclude Draft and Cancelled.
- To pass a trip to an agent use "Pass Trip"; the agent must be linked to the supplier and active.`;

// ── RAG: query the knowledge base with the public anon key (external docs only) ──
async function searchKB(query: string): Promise<string> {
  if (!SUPABASE_URL || !SUPABASE_ANON_KEY) return "";
  const headers = {
    apikey: SUPABASE_ANON_KEY,
    Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
    "Content-Type": "application/json",
  };
  try {
    const res = await fetch(`${SUPABASE_URL}/rest/v1/rpc/search_knowledge`, {
      method: "POST",
      headers,
      body: JSON.stringify({ query_text: query.slice(0, 800), max_results: 8 }),
    });
    if (!res.ok) return "";
    const chunks = await res.json();
    if (!Array.isArray(chunks) || chunks.length === 0) return "";

    // Keep only chunks whose document is external (never leak internal docs).
    const ids = [...new Set(chunks.map((c: any) => c.document_id).filter(Boolean))];
    let externalIds = new Set<string>();
    try {
      const q = `${SUPABASE_URL}/rest/v1/knowledge_documents?select=id&id=in.(${ids.join(",")})&or=(audience.eq.external,audience.is.null)`;
      const dres = await fetch(q, { headers });
      if (dres.ok) {
        const docs = await dres.json();
        externalIds = new Set((docs || []).map((d: any) => d.id));
      } else {
        return ""; // can't verify audience → don't risk leaking; use built-in instead
      }
    } catch {
      return "";
    }

    return chunks
      .filter((c: any) => externalIds.has(c.document_id))
      .slice(0, 5)
      .map((c: any) => `[${c.file_name}]: ${c.content}`)
      .join("\n\n");
  } catch {
    return "";
  }
}

async function generateReply(question: string, senderName: string, kb: string): Promise<string> {
  if (!OCI_KEY) throw new Error("OCI_KEY not set");
  const grounded = kb && kb.length > 0;
  const systemPrompt = `You are Milo 🦊 — a friendly, professional customer support assistant for TripEX (Travel & Expense / TAS) answering on WhatsApp.
RULES:
- Reply in the SAME LANGUAGE the customer used (Hebrew if they wrote Hebrew).
- ${grounded
      ? "Base your answer on the Knowledge Base Context below. Do not invent facts."
      : "Use the Built-in Knowledge below to help. If you don't know, say so and offer to connect a human agent."}
- PRIVACY: never reveal other customers' personal data (names, emails, phones, company names, ticket/TAS/trip numbers).
- Keep it concise and friendly for chat.

${grounded ? "Knowledge Base Context:\n" + kb : "Built-in Knowledge:\n" + BUILTIN_KNOWLEDGE}`;

  const res = await fetch(OCI_ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${OCI_KEY}` },
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
  if (!res.ok) throw new Error(`OCI ${res.status}: ${await res.text()}`);
  const j = await res.json();
  return j.choices?.[0]?.message?.content?.trim() ?? "";
}

async function sendWhatsApp(chatId: string, message: string): Promise<void> {
  const url = `${GREENAPI_BASE}/waInstance${GREENAPI_ID}/sendMessage/${GREENAPI_TOKEN}`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ chatId, message }),
  });
  if (!res.ok) throw new Error(`Green API send ${res.status}: ${await res.text()}`);
}

Deno.serve(async (req: Request) => {
  const url = new URL(req.url);
  if (req.method === "GET") {
    return Response.json({ ok: true, service: "whatsapp-bot", greenapi: !!GREENAPI_ID, oci: !!OCI_KEY });
  }
  if (req.method !== "POST") return new Response("method not allowed", { status: 405 });
  if (WEBHOOK_SECRET && url.searchParams.get("secret") !== WEBHOOK_SECRET) {
    return new Response("forbidden", { status: 403 });
  }

  // Always ack 200 so Green API doesn't retry-storm.
  const ack = () => Response.json({ ok: true });

  try {
    const body = await req.json().catch(() => ({} as any));
    if (body?.typeWebhook !== "incomingMessageReceived") return ack();

    const chatId: string = body?.senderData?.chatId ?? "";
    const senderName: string = body?.senderData?.senderName ?? "";
    const md = body?.messageData ?? {};
    const text: string = md?.textMessageData?.textMessage ?? md?.extendedTextMessageData?.text ?? "";

    if (!chatId || !chatId.endsWith("@c.us") || !text.trim()) return ack();

    let reply = "";
    try {
      const kb = await searchKB(text);
      reply = await generateReply(text, senderName, kb);
    } catch (e) {
      console.error("generate failed:", e);
      reply = "מצטער, יש כרגע תקלה זמנית. נציג אנושי יחזור אליך בהקדם. 🙏";
    }
    if (!reply) reply = "לא הצלחתי למצוא תשובה מדויקת — אעביר את פנייתך לנציג אנושי. 🙏";

    await sendWhatsApp(chatId, reply);
    console.log(`replied to ${chatId} (${senderName}): ${reply.slice(0, 120)}`);
    return ack();
  } catch (e) {
    console.error("webhook error:", e);
    return ack();
  }
});
