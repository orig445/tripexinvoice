import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers":
    "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
};

// ── Action Mapping (hardcoded, not AI-dependent) ──
const ACTION_MAPPING: Record<string, { actions: string[]; redirectPage?: string }> = {
  help: { actions: [] },
  scan: { actions: ["Camera"] },
  expense: { actions: [] },
  expense_complete: { actions: [] },
  bi: { actions: ["DisplayResults"] },
  online: { actions: [] },
  online_complete: { actions: [] },
  general: { actions: [] },
};

// ── Oracle TAS Stubs (future integration) ──
// TODO: Connect to Oracle TAS API
async function fetchTASData(_userId: string) {
  return { placeholder: true, message: "TAS data not yet connected" };
}
async function fetchTRDetails(_trId: string) {
  return { placeholder: true, message: "TR details not yet connected" };
}
async function validateApproval(_trId: string) {
  return { placeholder: true, approved: false, message: "Approval validation not yet connected" };
}
async function submitExpense(_data: Record<string, unknown>) {
  return { placeholder: true, message: "Expense submission not yet connected" };
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
  const supabaseKey = Deno.env.get("SUPABASE_ANON_KEY")!;

  try {
    // ── Auth ──
    const authHeader = req.headers.get("Authorization");
    if (!authHeader) {
      return new Response(JSON.stringify({ error: "Missing authorization" }), {
        status: 401,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const supabase = createClient(supabaseUrl, supabaseKey, {
      global: { headers: { Authorization: authHeader } },
    });

    const {
      data: { user },
      error: authError,
    } = await supabase.auth.getUser();
    if (authError || !user) {
      return new Response(JSON.stringify({ error: "Unauthorized" }), {
        status: 401,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ── Parse input ──
    const {
      source = "web",
      scope = "",
      trid = "",
      text = "",
      type = "text",
      sessionToken = "",
      userDate = "",
      userTime = "",
      userTimezone = "",
      audience = "external",
    } = await req.json();

    // Which knowledge base this request may read. Defaults to the customer-facing
    // ("external") base so the public widget can NEVER retrieve internal docs.
    const kbAudience = audience === "internal" ? "internal" : "external";

    // ── IP-based Geolocation ──
    let userLocation = "";
    let ipTimezone = "";
    let ipLocalTime = "";
    try {
      const clientIp =
        req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ||
        req.headers.get("cf-connecting-ip") ||
        req.headers.get("x-real-ip") ||
        "";
      if (clientIp && clientIp !== "127.0.0.1" && clientIp !== "::1") {
        const geoRes = await fetch(
          `http://ip-api.com/json/${clientIp}?fields=status,country,city,regionName,timezone,lat,lon&lang=he`,
        );
        if (geoRes.ok) {
          const geo = await geoRes.json();
          if (geo.status === "success") {
            userLocation = `${geo.city || ""}, ${geo.regionName || ""}, ${geo.country || ""}`
              .replace(/, ,/g, ",")
              .replace(/^, |, $/g, "");
            ipTimezone = geo.timezone || "";
            // Calculate local time at the user's IP location
            if (ipTimezone) {
              try {
                const now = new Date();
                ipLocalTime = now.toLocaleString("he-IL", {
                  timeZone: ipTimezone,
                  weekday: "long",
                  year: "numeric",
                  month: "long",
                  day: "numeric",
                  hour: "2-digit",
                  minute: "2-digit",
                });
              } catch {}
            }
          }
        }
      }
    } catch (geoErr) {
      console.error("Geolocation error:", geoErr);
    }

    // ── Session handling ──
    let sessionId = sessionToken || null;
    if (!sessionId) {
      const { data: newSession, error: sessErr } = await supabase
        .from("chat_sessions")
        .insert({ user_id: user.id, source })
        .select("id")
        .single();
      if (sessErr) throw sessErr;
      sessionId = newSession.id;
    }

    // ── OCR flow (type === "image") ──
    if (type === "image") {
      try {
        const ocrResponse = await fetch(`${supabaseUrl}/functions/v1/analyze-invoice`, {
          method: "POST",
          headers: {
            Authorization: authHeader,
            "Content-Type": "application/json",
            apikey: supabaseKey,
          },
          body: JSON.stringify({ imageBase64: text }),
        });

        const ocrData = await ocrResponse.json();

        // Log OCR request
        await supabase.from("chatbot_logs").insert({
          session_id: sessionId,
          user_id: user.id,
          event_type: "ocr_request",
          details: { success: ocrData.success, source },
        });

        if (!ocrData.success) {
          return new Response(
            JSON.stringify({
              actions: [],
              text: "Failed to scan receipt. Please try again.",
              redirectPage: "",
              data: {},
              session_id: sessionId,
            }),
            {
              status: 200,
              headers: { ...corsHeaders, "Content-Type": "application/json" },
            },
          );
        }

        // Build a summary of the scanned data using new structured format
        const d = ocrData.data || {};
        const lines: string[] = ["✅ Invoice scanned successfully! Here are the details:"];
        if (d.document_type) lines.push(`📄 Type: ${d.document_type.replace(/_/g, " ")}`);
        if (d.merchant?.name) lines.push(`🏪 Merchant: ${d.merchant.name}`);
        if (d.merchant?.tin) lines.push(`🆔 TIN: ${d.merchant.tin}`);
        if (d.merchant?.address) lines.push(`📍 Address: ${d.merchant.address}`);
        if (d.merchant?.city) lines.push(`🌆 City: ${d.merchant.city}`);
        if (d.invoice_number) lines.push(`🔢 Invoice #: ${d.invoice_number}`);
        if (d.invoice_date) lines.push(`📅 Date: ${d.invoice_date}`);
        const cur = d.currency || "";
        if (d.amounts?.vatable_sales_amount != null)
          lines.push(`💵 VATable Sales: ${d.amounts.vatable_sales_amount} ${cur}`);
        if (d.amounts?.non_vatable_sales_amount != null && d.amounts.non_vatable_sales_amount > 0)
          lines.push(`💵 Non-VAT Sales: ${d.amounts.non_vatable_sales_amount} ${cur}`);
        if (d.amounts?.service_charge_amount != null && d.amounts.service_charge_amount > 0)
          lines.push(`💵 Service Charge: ${d.amounts.service_charge_amount} ${cur}`);
        if (d.amounts?.tax_amount != null) lines.push(`🧾 VAT/Tax: ${d.amounts.tax_amount} ${cur}`);
        if (d.payment?.method) lines.push(`💳 Payment: ${d.payment.method}`);
        // Form of payment details with fallback inference from payment.method
        const paymentText = `${d.payment?.form_of_payment ?? ""} ${d.payment?.method ?? ""}`.toLowerCase();
        const inferredFop =
          paymentText.includes("credit") ||
          paymentText.includes("debit") ||
          paymentText.includes("card") ||
          paymentText.includes("visa") ||
          paymentText.includes("master") ||
          paymentText.includes("amex") ||
          paymentText.includes("diners") ||
          paymentText.includes("isracard") ||
          paymentText.includes("ישראכרט") ||
          paymentText.includes("אשראי") ||
          paymentText.includes("כרטיס") ||
          paymentText.includes("סליקה") ||
          paymentText.includes("סליקת") ||
          paymentText.includes("emv") ||
          paymentText.includes("contactless")
            ? "credit"
            : paymentText.includes("bank") || paymentText.includes("transfer") || paymentText.includes("העברה")
              ? "bank"
              : paymentText.includes("cash") || paymentText.includes("מזומן")
                ? "cash"
                : "cash";
        if (inferredFop === "credit") {
          let creditInfo = "💳 Form of Payment: Credit Card";
          if (d.payment?.card_type)
            creditInfo += ` (${d.payment.card_type.charAt(0).toUpperCase() + d.payment.card_type.slice(1)})`;
          if (d.payment?.card_last4) creditInfo += ` ****${d.payment.card_last4}`;
          lines.push(creditInfo);
        } else if (inferredFop === "bank") {
          lines.push("🏦 Form of Payment: Bank Transfer");
        } else {
          lines.push("💵 Form of Payment: Cash");
        }
        if (d.payment?.amount_paid != null) lines.push(`💰 Paid: ${d.payment.amount_paid} ${cur}`);
        lines.push("\nIs the data correct? If something is wrong, let me know and I'll update it.");

        const ocrSummary = lines.join("\n");

        // Save user message (image scan) and assistant response to chat history
        // so the AI has context for follow-up corrections
        await supabase.from("chat_messages").insert({
          session_id: sessionId,
          role: "user",
          content: "[User scanned an invoice/receipt]",
        });
        await supabase.from("chat_messages").insert({
          session_id: sessionId,
          role: "assistant",
          content: ocrSummary,
          intent: "scan",
          metadata: { actions: [], scanned_data: ocrData.data },
        });

        return new Response(
          JSON.stringify({
            actions: [],
            text: ocrSummary,
            redirectPage: "",
            data: ocrData.data,
            session_id: sessionId,
          }),
          {
            status: 200,
            headers: { ...corsHeaders, "Content-Type": "application/json" },
          },
        );
      } catch (ocrErr) {
        console.error("OCR error:", ocrErr);
        await supabase.from("chatbot_logs").insert({
          session_id: sessionId,
          user_id: user.id,
          event_type: "error",
          details: { error: String(ocrErr), phase: "ocr" },
        });
        return new Response(
          JSON.stringify({
            actions: [],
            text: "Error processing image. Please try again.",
            redirectPage: "",
            data: {},
            session_id: sessionId,
          }),
          {
            status: 200,
            headers: { ...corsHeaders, "Content-Type": "application/json" },
          },
        );
      }
    }

    // ── Text flow ──
    if (!text.trim()) {
      return new Response(
        JSON.stringify({
          actions: [],
          text: "Hello 👋 I'm TripEX AI. How can I assist you today?",
          redirectPage: "",
          data: {},
          session_id: sessionId,
        }),
        {
          status: 200,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        },
      );
    }

    // Save user message
    await supabase.from("chat_messages").insert({
      session_id: sessionId,
      role: "user",
      content: text,
    });

    // Load recent history — take the LAST 30 messages of this conversation
    // (descending + reverse), so long chats keep their most recent context.
    const { data: historyDesc } = await supabase
      .from("chat_messages")
      .select("role, content, created_at")
      .eq("session_id", sessionId)
      .order("created_at", { ascending: false })
      .limit(30);
    const history = (historyDesc || []).slice().reverse();

    // ── Load chatbot config ──
    const { data: config } = await supabase.from("chatbot_config").select("*").eq("is_active", true).limit(1).single();

    const temperature = config?.temperature || 0.3;
    const maxTokens = config?.max_tokens || 2048;
    const modelName = config?.model_name || "meta.llama-4-maverick-17b-128e-instruct-fp8";

    const ORACLE_API_KEY = Deno.env.get("oracleapikey")
      || Deno.env.get("oracleapikey_2")
      || Deno.env.get("invoice");
    if (!ORACLE_API_KEY) {
      throw new Error("Oracle API key is not configured");
    }


    // ── RAG: Search knowledge base ──
    let knowledgeContext = "";
    try {
      // Search with full query
      const { data: chunks } = await supabase.rpc("search_knowledge", {
        query_text: text,
        max_results: 5,
      });

      // Also search with individual words for better Hebrew matching
      const words = text.split(/\s+/).filter((w: string) => w.length > 2);
      let allChunks = chunks || [];

      for (const word of words.slice(0, 3)) {
        const { data: wordChunks } = await supabase.rpc("search_knowledge", {
          query_text: word,
          max_results: 3,
        });
        if (wordChunks) {
          for (const wc of wordChunks) {
            if (!allChunks.some((c: any) => c.chunk_id === wc.chunk_id)) {
              allChunks.push(wc);
            }
          }
        }
      }

      // ── Audience isolation ──
      // search_knowledge is not audience-aware, so filter the returned chunks by
      // the audience of their source document. This guarantees the customer bot
      // (external) never sees internal documents and vice-versa. Legacy docs with
      // a NULL audience count as external. If the audience column doesn't exist
      // yet, we only enforce isolation once it does (internal upload is blocked
      // client-side until then, so nothing internal can exist).
      if (allChunks.length > 0) {
        const docIds = [...new Set(allChunks.map((c: any) => c.document_id).filter(Boolean))];
        if (docIds.length > 0) {
          const { data: docs, error: docsErr } = await supabase
            .from("knowledge_documents")
            .select("id, audience")
            .in("id", docIds);

          if (!docsErr && docs) {
            const audienceById = new Map(docs.map((d: any) => [d.id, d.audience ?? "external"]));
            allChunks = allChunks.filter(
              (c: any) => (audienceById.get(c.document_id) ?? "external") === kbAudience,
            );
          }
          // If the column is missing (docsErr), skip filtering — no internal docs exist yet.
        }
      }

      // Limit to top 5
      allChunks = allChunks.slice(0, 5);

      if (allChunks.length > 0) {
        knowledgeContext =
          "\n\n## Knowledge Base Context (use this to answer the user):\n" +
          allChunks.map((c: any) => `[${c.file_name}]: ${c.content}`).join("\n\n");
      }
    } catch (ragErr) {
      console.error("RAG search error:", ragErr);
    }

    // ── Team lessons: corrections taught by users through the chat ──
    let lessonsContext = "";
    try {
      const words = text
        .split(/\s+/)
        .filter((w: string) => w.length > 3)
        .slice(0, 6);

      let lessons: any[] = [];
      if (words.length > 0) {
        const orFilter = words
          .map((w: string) => `question.ilike.%${w.replace(/[,%()]/g, "")}%`)
          .join(",");
        const { data } = await supabase
          .from("bot_lessons")
          .select("question, answer")
          .eq("audience", kbAudience)
          .eq("is_approved", true)
          .or(orFilter)
          .order("created_at", { ascending: false })
          .limit(5);
        lessons = data || [];
      }

      if (lessons.length === 0) {
        const { data } = await supabase
          .from("bot_lessons")
          .select("question, answer")
          .eq("audience", kbAudience)
          .eq("is_approved", true)
          .order("created_at", { ascending: false })
          .limit(3);
        lessons = data || [];
      }

      if (lessons.length > 0) {
        lessonsContext =
          "\n\n## Team Lessons (corrections taught by real users — these OVERRIDE the knowledge base when they conflict):\n" +
          lessons.map((l: any) => `Q: ${l.question}\nA: ${l.answer}`).join("\n\n");
      }
    } catch (lessonErr) {
      console.error("Lessons lookup error:", lessonErr);
    }


    const systemPrompt = `You are Milo 🦊 — a friendly, professional customer service assistant for TripEX (Travel & Expense Management). Your goal is to HELP users warmly and patiently. You ALWAYS respond in English regardless of the user's language.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The "text" field must ALWAYS contain natural, human-readable text. NEVER put JSON objects, code, or raw data structures inside the "text" field. Always format data as a readable list with dashes or line breaks — like a real person would write it.
CRITICAL LANGUAGE RULE: You MUST ALWAYS respond in English, no matter what language the user writes in.

## Intent Categories:
- help: user wants guidance or how-to
- scan: user wants to scan a receipt/invoice
- bi: user wants reports, data analysis, or statistics
- online: user wants to book flights/hotels
- expense: user wants to add or manage expenses
- general: casual conversation or anything else

## SCOPE — YOU ARE A SUPPORT AGENT ONLY (CRITICAL):
You answer support questions. You do NOT perform actions and you do NOT run data-collection wizards.
- NEVER start a travel-request / flight-booking flow. Never ask "Where are you planning to travel to?", for dates, passengers, or notes.
- NEVER start an add-expense wizard (description → amount → currency → date → category).
- If the user wants to create a trip request, book travel, or add an expense, EXPLAIN how to do it in TripEX (concrete steps / where to click) based on the knowledge base — do not collect the details yourself.
- If the knowledge base doesn't cover it, say so honestly and offer to escalate to a human agent.
- Never ask a chain of questions. Answer, then optionally offer one next step.


### When user corrects OCR/scanned data:
If the conversation history shows a previously scanned invoice/receipt and the user says something is wrong:
- Acknowledge the correction warmly
- Show the UPDATED full summary with the corrected field(s) clearly marked
- Ask if everything is correct now or if they want to change anything else
- Use intent "scan" for these correction responses

### When user CONFIRMS data (says "yes", "correct", "confirmed", "ok", "okay", "approve", "looks good"):
If the conversation history shows a pending expense summary or scanned invoice summary and the user confirms it:
- Respond warmly: "Got it! ✅ Updating your expenses..."
- Then add: "If you need help with anything else, I'm here! 😊"
- Use intent "expense_complete" if it was an expense flow, or "scan" if it was an OCR scan confirmation
- This is the END of the flow

### Important for ALL flows:
- Only start a field-collection flow when the user clearly asked to CREATE something (add an expense, request a trip). Never turn a question into an interrogation.
- Ask for ONE field at a time, and only for fields that are truly missing
- If the user provides multiple fields at once, acknowledge all of them
- NEVER ask the same question twice. Check conversation history carefully.
- When all required fields are collected, IMMEDIATELY respond with the _complete intent and a summary
- Be conversational and friendly, use emojis occasionally

## Support-Agent Answering Style (MOST IMPORTANT):
You are a smart support agent, not a form. ANSWER FIRST, ask later.
- Default to giving a real, useful answer immediately. Do NOT open with clarifying questions.
- Ask AT MOST ONE clarifying question, and only when the request is genuinely ambiguous AND you cannot give any useful answer without it. Never ask two or more questions in the same reply.
- If details are missing but you can reasonably guess, state your assumption ("Assuming you mean the web app...") and answer anyway.
- If several interpretations exist, cover the most likely one fully, then briefly offer the alternative ("If you meant X instead, tell me and I'll walk you through that").
- Prefer concrete, actionable steps (numbered 1, 2, 3) over generic advice. Tell the user exactly where to click / what to do.
- Solve the underlying problem, not just the literal question — anticipate the next obstacle and mention it proactively.
- Keep it tight: a short empathetic opener, the answer, then one short closing offer of further help. No walls of text, no repeating the question back.
- Never end with a list of questions. End with a solution or a next step.

## Response Style:
- **CRITICAL: If "Knowledge Base Context" is provided below, you MUST base your answer ONLY on that content.**
- **NEVER INVENT OR HALLUCINATE information.**
- **If a "Team Lessons" section appears below, treat it as the highest-priority source of truth — it contains corrections taught by real users. Never mention that a lesson exists or who taught it; just answer correctly.**
- **PRIVACY (CRITICAL): NEVER reveal personal or customer-specific data — names, email addresses, phone numbers, company/customer names, ticket numbers, TAS/trip numbers, or one customer's details to another. If a knowledge snippet contains such data, use only the general lesson/how-to from it and omit the identifiers. Never answer questions about a specific named person or another customer's case.**
- If you don't have enough information, say honestly what you do know, then: "I couldn't find the rest in my knowledge base — if you share a bit more detail I'll dig in, or I can escalate this to a human agent."
- Be clear and thorough — explain step by step when needed, without padding
- Use friendly, supportive language
- Structure answers with line breaks and numbered steps for readability
- ALWAYS respond in English

## Email-style formatting (apply to every answer):
Write each reply like a short, professional support email — but WITHOUT a signature block or sign-off.
- Open with a brief greeting line ("Hi," or "Hi <name>," if the user's name is known), then a blank line.
- Body in short paragraphs separated by blank lines; use numbered steps (1., 2., 3.) for instructions.
- Close with one short line offering further help (e.g. "Happy to help if anything is unclear.").
- Do NOT add "Best regards", "Milo — TripEX Support", subject lines, or "From:"/"To:" headers.
- Keep it tight and readable — no walls of text.



## Output format (ONLY this JSON, nothing else):
{"intent": "<intent>", "text": "<your detailed, friendly answer in English>"}

User's ACTUAL location (from IP): ${userLocation || "unknown"}
User's ACTUAL local time at their location: ${ipLocalTime || `${userDate || "unknown"} ${userTime || ""}`}
User's ACTUAL timezone: ${ipTimezone || userTimezone || "unknown"}
Browser-reported time (may differ if user traveled): ${userDate || "unknown"} ${userTime || ""} (${userTimezone || "unknown"})
IMPORTANT: When the user asks "what time is it" or "where am I", use the IP-based location and time above — this reflects where they PHYSICALLY are right now.
Current context: source=${source}, scope=${scope}${trid ? `, trid=${trid}` : ""}${lessonsContext}${knowledgeContext}`;

    // Full conversation memory for this session: every prior turn is replayed
    // to the model so it answers in context instead of treating each question
    // as a standalone request.
    const historyTurns = (history || [])
      .filter((m: any) => typeof m.content === "string" && m.content.trim().length > 0)
      .map((m: any) => ({
        role: m.role === "assistant" ? "assistant" : "user",
        content: String(m.content).slice(0, 4000),
      }));

    const messages = [
      { role: "system", content: systemPrompt },
      {
        role: "system",
        content:
          "The following messages are the ongoing conversation with THIS user in THIS session. " +
          "Always use them as memory: remember names, trips, amounts, receipts and preferences already mentioned, " +
          "resolve pronouns and follow-up questions against them, and never ask again for information the user already gave.",
      },
      ...historyTurns,
    ];

    // ── Call Oracle AI ──
    const aiResponse = await fetch(
      "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions",
      {
        method: "POST",
        headers: {
          Authorization: `Bearer ${ORACLE_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          model: modelName,
          messages,
          max_tokens: maxTokens,
          temperature,
        }),
      },
    );

    if (!aiResponse.ok) {
      const errText = await aiResponse.text();
      console.error("Oracle AI error:", aiResponse.status, errText);

      await supabase.from("chatbot_logs").insert({
        session_id: sessionId,
        user_id: user.id,
        event_type: "error",
        details: { status: aiResponse.status, error: errText },
      });

      if (aiResponse.status === 429) {
        return new Response(JSON.stringify({ error: "Rate limit exceeded" }), {
          status: 429,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      throw new Error(`Oracle AI error: ${aiResponse.status}`);
    }

    const aiData = await aiResponse.json();
    const rawContent = aiData.choices?.[0]?.message?.content || "";

    // ── Parse AI response ──
    let intent = "general";
    let responseText = rawContent;

    // Helper: decode unicode escapes like \u05e9\u05dc\u05d5\u05dd
    function decodeUnicodeEscapes(str: string): string {
      return str.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));
    }

    try {
      // Clean markdown wrappers
      let cleaned = rawContent
        .replace(/```json\n?/g, "")
        .replace(/```\n?/g, "")
        .trim();

      // Find the outermost { ... } using brace counting
      const startIdx = cleaned.indexOf("{");
      if (startIdx !== -1) {
        let depth = 0;
        let endIdx = -1;
        let inString = false;
        let escapeNext = false;
        for (let i = startIdx; i < cleaned.length; i++) {
          const ch = cleaned[i];
          if (escapeNext) {
            escapeNext = false;
            continue;
          }
          if (ch === "\\") {
            escapeNext = true;
            continue;
          }
          if (ch === '"') {
            inString = !inString;
            continue;
          }
          if (inString) continue;
          if (ch === "{") depth++;
          else if (ch === "}") {
            depth--;
            if (depth === 0) {
              endIdx = i;
              break;
            }
          }
        }
        if (endIdx !== -1) {
          cleaned = cleaned.substring(startIdx, endIdx + 1);
        }
      }

      const parsed = JSON.parse(cleaned);
      intent = parsed.intent || "general";
      // Ensure text is a string, not an object
      if (typeof parsed.text === "object" && parsed.text !== null) {
        responseText = JSON.stringify(parsed.text);
      } else {
        responseText = String(parsed.text || rawContent);
      }
      // Decode any remaining unicode escapes
      responseText = decodeUnicodeEscapes(responseText);
    } catch {
      // If JSON parse fails, try to extract text field manually
      const textMatch = rawContent.match(/"text"\s*:\s*"((?:[^"\\]|\\.)*)"/s);
      const intentMatch = rawContent.match(/"intent"\s*:\s*"([^"]*)"/);
      if (textMatch) {
        responseText = textMatch[1].replace(/\\n/g, "\n").replace(/\\"/g, '"');
        responseText = decodeUnicodeEscapes(responseText);
        intent = intentMatch?.[1] || "general";
      } else {
        responseText =
          decodeUnicodeEscapes(rawContent)
            .replace(/^\s*\{[\s\S]*"text"\s*:\s*"/i, "")
            .replace(/"\s*\}\s*$/, "")
            .replace(/\\n/g, "\n")
            .replace(/\\"/g, '"')
            .trim() || rawContent;
      }
    }

    // ── Map intent to actions ──
    const mapping = ACTION_MAPPING[intent] || ACTION_MAPPING.general;
    const finalText = responseText;

    // ── Save corrections for AI learning (when user corrects OCR data) ──
    try {
      // Check if this is a scan correction by looking for scanned_data in recent history
      if (intent === "scan" || intent === "expense_complete") {
        const { data: recentMsgs } = await supabase
          .from("chat_messages")
          .select("metadata, content, role")
          .eq("session_id", sessionId)
          .order("created_at", { ascending: false })
          .limit(10);

        // Find the original scanned data
        const scanMsg = recentMsgs?.find((m: any) => m.metadata?.scanned_data);
        if (scanMsg?.metadata?.scanned_data) {
          const original = scanMsg.metadata.scanned_data;
          const corrections: Array<{
            user_id: string;
            field_name: string;
            original_value: string;
            corrected_value: string;
            context?: string;
          }> = [];
          const ctx = original.vendor_name || original.invoice_number || undefined;

          // Find the latest correction summary (assistant message with UPDATED or corrected values)
          // Look at ALL recent assistant messages for corrected values
          const allAssistantText = (recentMsgs || [])
            .filter((m: any) => m.role === "assistant")
            .map((m: any) => m.content)
            .join("\n");

          // Also include user messages to catch direct corrections like "the date is 27/06/2025"
          const allUserText = (recentMsgs || [])
            .filter((m: any) => m.role === "user")
            .map((m: any) => m.content)
            .join("\n");

          const allText = allAssistantText + "\n" + allUserText;

          // Extract values using multiple patterns
          const totalMatch = allText.match(/(?:Total)[:\s]*([0-9,.]+)/i);
          if (
            totalMatch &&
            original.total_amount != null &&
            parseFloat(totalMatch[1].replace(",", "")) !== original.total_amount
          ) {
            corrections.push({
              user_id: user.id,
              field_name: "total_amount",
              original_value: String(original.total_amount),
              corrected_value: totalMatch[1].replace(",", ""),
              context: ctx,
            });
          }

          const taxMatch = allText.match(/(?:VAT|Tax|מע"מ)[:\s]*([0-9,.]+)/i);
          if (
            taxMatch &&
            original.tax_amount != null &&
            parseFloat(taxMatch[1].replace(",", "")) !== original.tax_amount
          ) {
            corrections.push({
              user_id: user.id,
              field_name: "tax_amount",
              original_value: String(original.tax_amount),
              corrected_value: taxMatch[1].replace(",", ""),
              context: ctx,
            });
          }

          const invoiceNumMatch = allText.match(/(?:Invoice number)[:\s]*([^\n,]+)/i);
          if (invoiceNumMatch && original.invoice_number && invoiceNumMatch[1].trim() !== original.invoice_number) {
            corrections.push({
              user_id: user.id,
              field_name: "invoice_number",
              original_value: original.invoice_number,
              corrected_value: invoiceNumMatch[1].trim(),
              context: ctx,
            });
          }

          // Date: match patterns like DD/MM/YYYY or YYYY-MM-DD
          const dateMatch = allText.match(/(?:Date)[:\s]*([0-9]{1,4}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})/i);
          if (dateMatch && original.invoice_date && dateMatch[1].trim() !== original.invoice_date) {
            corrections.push({
              user_id: user.id,
              field_name: "invoice_date",
              original_value: original.invoice_date,
              corrected_value: dateMatch[1].trim(),
              context: ctx,
            });
          }

          // Category
          const categoryMatch = allText.match(/(?:Category)[:\s]*(?:[\p{Emoji}\s]*)(\w+)/iu);
          if (categoryMatch && original.category && categoryMatch[1].trim().toLowerCase() !== original.category) {
            corrections.push({
              user_id: user.id,
              field_name: "category",
              original_value: original.category,
              corrected_value: categoryMatch[1].trim().toLowerCase(),
              context: ctx,
            });
          }

          if (corrections.length > 0) {
            const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
            const res = await fetch(`${supabaseUrl}/rest/v1/invoice_corrections`, {
              method: "POST",
              headers: {
                apikey: serviceKey,
                Authorization: `Bearer ${serviceKey}`,
                "Content-Type": "application/json",
                Prefer: "return=minimal",
              },
              body: JSON.stringify(corrections),
            });
            console.log(`Saved ${corrections.length} chatbot correction(s), status: ${res.status}`);
          }
        }
      }
    } catch (corrErr) {
      console.error("Failed to save chatbot corrections:", corrErr);
    }

    // Save assistant message
    await supabase.from("chat_messages").insert({
      session_id: sessionId,
      role: "assistant",
      content: finalText,
      intent,
      metadata: { actions: mapping.actions, redirectPage: mapping.redirectPage || "" },
    });

    // Log
    await supabase.from("chatbot_logs").insert({
      session_id: sessionId,
      user_id: user.id,
      event_type: "intent_detected",
      details: {
        intent,
        actions: mapping.actions,
        redirectPage: mapping.redirectPage || "",
        message_preview: text.substring(0, 100),
        source,
      },
    });

    return new Response(
      JSON.stringify({
        actions: mapping.actions,
        text: finalText,
        redirectPage: mapping.redirectPage || "",
        data: {},
        session_id: sessionId,
      }),
      {
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      },
    );
  } catch (error) {
    console.error("ai-router error:", error);
    return new Response(JSON.stringify({ error: error instanceof Error ? error.message : "Unknown error" }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
