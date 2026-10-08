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


// ── Attachments ──
// What a file sent with a chat message is for. An attachment used to mean exactly one thing —
// type:"image" with the base64 in `text`, scanned as a receipt — so a screenshot sent to ask
// about it came back as a list of merchant/VAT/total fields, and the question had nowhere to go.
// Mirrors TripEx.Api's AttachmentIntents/AttachmentRouting so both backends behave the same.
const MAX_ATTACHMENTS = 5;

/**
 * Is a model call needed to decide? Only for an "auto" intent — an explicit one is already the
 * answer, so nobody is billed for a verdict that would be discarded.
 */
function shouldClassifyAttachment(requestedIntent: string): boolean {
  const intent = (requestedIntent || "").trim().toLowerCase();
  return intent !== "scan" && intent !== "ask";
}

/** Decide the route. `verdict` is null when the classifier was skipped or could not answer. */
function decideAttachmentRoute(
  requestedIntent: string,
  hasQuestionText: boolean,
  verdict: string | null,
): "scan" | "ask" {
  const intent = (requestedIntent || "").trim().toLowerCase();
  // An explicit intent is the client reporting what the user pressed — the camera button is
  // "scan this receipt" and must not depend on a model's opinion.
  if (intent === "scan") return "scan";
  if (intent === "ask") return "ask";

  const answer = (verdict || "").trim().toLowerCase();
  if (answer === "scan") return "scan";
  if (answer === "ask") return "ask";

  // No verdict: fall back on the one signal that needs no model. With a question, answering it
  // is the useful failure; without one, a bare attachment has always meant "scan this".
  return hasQuestionText ? "ask" : "scan";
}

/** Is this a receipt to scan, or context for the question? Returns "scan", "ask", or null. */
async function classifyAttachment(
  apiKey: string,
  modelName: string,
  dataUrl: string,
  userText: string,
): Promise<string | null> {
  const said = userText.trim() || "(the user sent the file with no message of their own)";
  try {
    const res = await fetch(
      "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions",
      {
        method: "POST",
        headers: { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json" },
        body: JSON.stringify({
          model: modelName,
          max_tokens: 512,
          temperature: 0,
          messages: [
            {
              role: "system",
              content:
                "Reply with ONE word and nothing else: SCAN or ASK.\n" +
                "A user of a business travel & expense system attached a file to a chat message. " +
                "Decide what they want done with it.\n" +
                "Reply SCAN only if BOTH hold: the file is a purchase document — an invoice, a receipt, " +
                "a tax invoice, a credit-card slip, a hotel or airline bill — AND nothing in their message " +
                "asks a question about it. An empty message with a receipt is SCAN.\n" +
                "Reply ASK for everything else: a screenshot of the system, an error message, a form, a " +
                "table, a chart, a photo, a document that is not a purchase — and ALSO for a genuine " +
                "invoice when the message asks something about it.\n" +
                "When in doubt, reply ASK — a wrong SCAN answers a question nobody asked.",
            },
            {
              role: "user",
              content: [
                { type: "image_url", image_url: { url: dataUrl } },
                { type: "text", text: `Their message: ${said}` },
              ],
            },
          ],
        }),
      },
    );
    if (!res.ok) {
      console.error("Attachment classification failed:", res.status, await res.text());
      return null;
    }
    const body = await res.json();
    const raw = String(body?.choices?.[0]?.message?.content ?? "");
    // Whole-word match, so quotes, JSON or a stray sentence around the answer still parse.
    const m = raw.match(/\b(SCAN|ASK)\b/i);
    return m ? m[1].toLowerCase() : null;
  } catch (err) {
    console.error("Attachment classification error:", err);
    return null;
  }
}

/** A base64 payload as the data: URL an image_url content part expects. */
function toDataUrl(payload: string): string {
  return payload.startsWith("data:") ? payload : `data:image/jpeg;base64,${payload}`;
}

/** What records, in the conversation's own text, that a file came with this turn. */
function attachmentNote(count: number): string {
  return count === 1 ? "[1 file attached]" : `[${count} files attached]`;
}

/**
 * The human-readable scan result for one file. Lifted out of the OCR flow unchanged when that
 * flow learned to handle a turn carrying several files.
 */
function buildOcrSummary(d: Record<string, any>): string {
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
  return lines.join("\n");
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
      text: rawText = "",
      type = "text",
      images: rawImages = [],
      attachmentIntent = "auto",
      sessionToken = "",
      userDate = "",
      userTime = "",
      userTimezone = "",
      audience = "external",
    } = await req.json();

    // ── Attachments ──
    // `let`, because the old single-attachment shape carried the payload in `text` itself and
    // has to be folded out of it before anything downstream reads the user's words.
    let text: string = typeof rawText === "string" ? rawText : "";
    let attachments: string[] = (Array.isArray(rawImages) ? rawImages : [])
      .filter((i: unknown): i is string => typeof i === "string" && i.trim().length > 0)
      .slice(0, MAX_ATTACHMENTS);
    let requestedIntent: string = typeof attachmentIntent === "string" ? attachmentIntent : "auto";

    // type:"image" means the payload is in `text` — the only shape this endpoint used to accept,
    // and the only thing it ever meant was "scan this receipt". Kept working, and kept meaning
    // that, so every caller still on it is unaffected.
    if (type === "image" && attachments.length === 0 && text.trim()) {
      attachments = [text];
      requestedIntent = "scan";
      text = "";
    }

    // Which knowledge base this request may read. Defaults to the customer-facing
    // ("external") base so the public widget can NEVER retrieve internal docs.
    const kbAudience = audience === "internal" ? "internal" : "external";
    // The internal assistant never runs OCR: every picture is context for the question.
    if (kbAudience === "internal") requestedIntent = "ask";

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

    // ── Load chatbot config ──
    // Loaded before the attachment route is decided: the classifier below needs the model
    // name and the API key, and the text flow further down reads the same values.
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


    // ── Which way do this turn's attachments go? ──
    // Every attachment used to be scanned, because an attachment could only mean "scan this
    // receipt". Now only a purchase document is, and anything else is handed to the model as
    // context for the message it came with. See decideAttachmentRoute.
    let attachmentRoute: "scan" | "ask" = "scan";
    if (attachments.length > 0) {
      let verdict: string | null = null;
      // Only an "auto" intent is worth a model call — an explicit one is already the answer.
      // The first file decides for the turn: five photos of one expense, or five screenshots of
      // one problem, are a single act, and one verdict keeps five files costing what one does.
      if (shouldClassifyAttachment(requestedIntent)) {
        verdict = await classifyAttachment(ORACLE_API_KEY, modelName, toDataUrl(attachments[0]), text);
      }
      attachmentRoute = decideAttachmentRoute(requestedIntent, !!text.trim(), verdict);
      console.log(
        `[ATTACHMENT] files=${attachments.length} intent=${requestedIntent} ` +
        `hasQuestion=${!!text.trim()} classifier=${verdict ?? "-"} route=${attachmentRoute}`,
      );
    }

    // ── Scan flow: the attachments are receipts ──
    if (attachmentRoute === "scan" && attachments.length > 0) {
      try {
        // Every attachment on the turn, not only the first: a request used to carry exactly one
        // file (the client sent a separate request per file) and one turn can now carry up to
        // MAX_ATTACHMENTS, so reading attachments[0] alone would silently drop the rest.
        const summaries: string[] = [];
        const scans: Record<string, any>[] = [];
        for (const payload of attachments) {
          const ocrResponse = await fetch(`${supabaseUrl}/functions/v1/analyze-invoice`, {
            method: "POST",
            headers: {
              Authorization: authHeader,
              "Content-Type": "application/json",
              apikey: supabaseKey,
            },
            body: JSON.stringify({ imageBase64: payload }),
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
            // Named per file, so three receipts and one blurry photo report the blurry one
            // instead of failing the whole turn.
            summaries.push("Failed to scan receipt. Please try again.");
            continue;
          }
          summaries.push(buildOcrSummary(ocrData.data || {}));
          scans.push(ocrData.data);
        }

        if (scans.length === 0) {
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

        const ocrSummary = summaries.join("\n\n");

        // Save user message (image scan) and assistant response to chat history
        // so the AI has context for follow-up corrections
        await supabase.from("chat_messages").insert({
          session_id: sessionId,
          role: "user",
          content: attachments.length === 1
            ? "[User scanned an invoice/receipt]"
            : `[User scanned ${attachments.length} invoices/receipts]`,
        });
        await supabase.from("chat_messages").insert({
          session_id: sessionId,
          role: "assistant",
          content: ocrSummary,
          intent: "scan",
          // scanned_data keeps carrying the first scan's fields, unchanged, so the correction
          // flow below (which looks for it) is unaffected; scanned_files carries them all.
          metadata: { actions: [], scanned_data: scans[0], scanned_files: scans },
        });

        return new Response(
          JSON.stringify({
            actions: [],
            text: ocrSummary,
            redirectPage: "",
            // The first scan's fields stay at the top level — the shape every client reads
            // today — with the full set beside them when the turn carried more than one file.
            data: scans.length > 1 ? { ...scans[0], scans } : scans[0],
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
    // An attachment with no words is not an empty turn: the user sent something to be looked
    // at, and greeting them while ignoring it would be the same silent discard this whole
    // change is about. Those turns carry on below with the attachment note standing in for the
    // question they didn't type.
    if (!text.trim() && attachments.length === 0) {
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

    // The attachment note goes into the text itself, so the one string that is saved to
    // chat_messages, replayed as history and searched against the knowledge base all say the
    // same thing. The images last this request only — chat_messages stores text — so without
    // the note a later turn would see "why is this empty?" with nothing it could refer to.
    if (attachments.length > 0) {
      const note = attachmentNote(attachments.length);
      text = text.trim() ? `${text.trim()}\n${note}` : note;
    }

    // Save user message
    await supabase.from("chat_messages").insert({
      session_id: sessionId,
      role: "user",
      content: text,
    });

    // Load recent history — take the LAST 50 messages of this conversation
    // (descending + reverse), so long chats keep their most recent context.
    const { data: historyDesc } = await supabase
      .from("chat_messages")
      .select("role, content, created_at")
      .eq("session_id", sessionId)
      .order("created_at", { ascending: false })
      .limit(50);
    const history = (historyDesc || []).slice().reverse();

    // ── RAG: Search knowledge base ──
    let knowledgeContext = "";
    let knowledgeSources: Array<{ name: string; url: string | null }> = [];
    try {
      // Search with full query
      const { data: chunks } = await supabase.rpc("search_knowledge", {
        query_text: text,
        max_results: kbAudience === "internal" ? 15 : 5,
      });

      // Also search with individual words for better Hebrew matching
      const words = text.split(/\s+/).filter((w: string) => w.length > 2);
      let allChunks = chunks || [];

      for (const word of words.slice(0, kbAudience === "internal" ? 8 : 3)) {
        const { data: wordChunks } = await supabase.rpc("search_knowledge", {
          query_text: word,
          max_results: kbAudience === "internal" ? 8 : 3,
        });
        if (wordChunks) {
          for (const wc of wordChunks) {
            if (!allChunks.some((c: any) => c.chunk_id === wc.chunk_id)) {
              allChunks.push(wc);
            }
          }
        }
      }

      // ── Document-title search ──
      // Full-text chunk search can miss a file whose *name* is the answer
      // (e.g. asking for "a report" should surface "דוחות פופולריים - roi.xlsx").
      // Match the query words against file names and pull that document's chunks.
      try {
        const titleTerms = new Set<string>(words.map((w: string) => w.replace(/[,%()]/g, "")));
        if (/\b(report|reports)\b/i.test(text) || /דוח|דוחות|דו"ח/.test(text)) {
          titleTerms.add("דוח");
          titleTerms.add("report");
          titleTerms.add("roi");
        }
        const terms = [...titleTerms].filter((t) => t.length > 2).slice(0, 10);
        if (terms.length > 0) {
          const orFilter = terms.map((t) => `file_name.ilike.%${t}%`).join(",");
          const { data: namedDocs } = await supabase
            .from("knowledge_documents")
            .select("id, file_name")
            .eq("audience", kbAudience)
            .eq("status", "ready")
            .or(orFilter)
            .limit(5);

          for (const doc of namedDocs || []) {
            const { data: docChunks } = await supabase
              .from("knowledge_chunks")
              .select("id, document_id, content")
              .eq("document_id", doc.id)
              .order("chunk_index", { ascending: true })
              .limit(5);
            for (const dc of docChunks || []) {
              if (!allChunks.some((c: any) => c.chunk_id === dc.id)) {
                allChunks.unshift({
                  chunk_id: dc.id,
                  document_id: dc.document_id,
                  content: dc.content,
                  file_name: doc.file_name,
                });
              }
            }
          }
        }
      } catch (titleErr) {
        console.error("Title search error:", titleErr);
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
            .select("id, audience, file_name, file_url")
            .in("id", docIds);

          if (!docsErr && docs) {
            const audienceById = new Map(docs.map((d: any) => [d.id, d.audience ?? "external"]));
            allChunks = allChunks.filter(
              (c: any) => (audienceById.get(c.document_id) ?? "external") === kbAudience,
            );

            const matchedDocIds = new Set(allChunks.map((c: any) => c.document_id));
            const matchedDocs = docs.filter((d: any) => matchedDocIds.has(d.id));
            knowledgeSources = await Promise.all(
              matchedDocs.map(async (doc: any) => {
                let url: string | null = null;
                if (doc.file_url) {
                  const { data: signed } = await supabase.storage.from("knowledge").createSignedUrl(doc.file_url, 3600);
                  url = signed?.signedUrl || null;
                }
                return { name: doc.file_name, url };
              }),
            );
          }
          // If the column is missing (docsErr), skip filtering — no internal docs exist yet.
        }
      }

      // Internal research needs enough surrounding evidence for a complete answer.
      allChunks = allChunks.slice(0, kbAudience === "internal" ? 15 : 5);

      if (allChunks.length > 0) {
        knowledgeContext =
          "\n\n## Knowledge Base Context (use this to answer the user):\n" +
          allChunks.map((c: any) => `[Source: ${c.file_name}]\n${c.content}`).join("\n\n");
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


    const assistantRole = kbAudience === "internal"
      ? `You are Milo Internal Knowledge — an expert research assistant for TripEX employees. You are NOT a customer-support bot and must never treat the user as a customer. Your job is to investigate the internal knowledge base and help employees with any company topic represented there, including HR, product, operations, finance, procedures, integrations, and technical documentation.

INTERNAL RESEARCH RULES (CRITICAL):
- Thoroughly synthesize all relevant supplied internal excerpts; do not give a generic TripEX support response.
- Answer the employee's actual question directly and comprehensively, even when it is outside travel and expenses.
- For broad requests such as help with an HR interface, summarize what the documents say, organize the findings into clear sections, and provide concrete steps, fields, workflows, dependencies, and caveats found in the sources.
- Cite factual sections inline as [Source: exact file name]. Never invent a source or claim a detail not present in the excerpts.
- When sources are present, finish with a short “Sources” section listing the exact file names. Download links are added separately by the application.
- If the excerpts genuinely do not contain the answer, clearly say which part was not found. Do not redirect the employee to customer support and do not pretend the topic is out of scope.
- If the employee asks for a report, template, or file, identify the matching document by its exact file name, summarize what it contains (sheets, columns, key metrics), and tell them the file is attached below as a download link.
- Treat follow-up questions as continuing internal research and use the conversation history.`
      : `You are Milo 🦊 — a friendly, professional customer service assistant for TripEX (Travel & Expense Management). Your goal is to HELP users warmly and patiently.`;

    const systemPrompt = `${assistantRole} You reply in the SAME language the user wrote in.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The "text" field must ALWAYS contain natural, human-readable text. NEVER put JSON objects, code, or raw data structures inside the "text" field. Always format data as a readable list with dashes or line breaks — like a real person would write it.
CRITICAL LANGUAGE RULE: Detect the language of the user's latest message and write the "text" field in that SAME language (Hebrew → Hebrew, English → English, etc.). Never switch languages on your own; mirror the user.

## Intent Categories:
- help: user wants guidance or how-to
- scan: user wants to scan a receipt/invoice
- bi: user wants reports, data analysis, or statistics
- online: user wants to book flights/hotels
- expense: user wants to add or manage expenses
- general: casual conversation or anything else

## SCOPE — CUSTOMER CHAT ONLY:
${kbAudience === "internal" ? "This section does NOT apply. You are the internal company knowledge researcher described above." : `
You answer support questions. You do NOT perform actions and you do NOT run data-collection wizards.
- NEVER start a travel-request / flight-booking flow. Never ask "Where are you planning to travel to?", for dates, passengers, or notes.
- NEVER start an add-expense wizard (description → amount → currency → date → category).
- If the user wants to create a trip request, book travel, or add an expense, EXPLAIN how to do it in TripEX (concrete steps / where to click) based on the knowledge base — do not collect the details yourself.
- If the knowledge base doesn't cover it, say so honestly and offer to escalate to a human agent.
- Never ask a chain of questions. Answer, then optionally offer one next step.
`}


### When user corrects OCR/scanned data:
If the conversation history shows a previously scanned invoice/receipt and the user says something is wrong:
- Acknowledge the correction warmly
- Show the UPDATED full summary with the corrected field(s) clearly marked
- Ask if everything is correct now or if they want to change anything else
- Use intent "scan" for these correction responses

### When user CONFIRMS scanned data ("yes", "correct", "ok", "looks good"):
If the conversation history shows a scanned invoice summary and the user confirms it:
- Respond warmly: "Got it! ✅" and offer further help
- Use intent "scan"
- This is the END of the flow

Note: OCR scan review is the ONLY multi-step flow you handle. Everything else is plain support Q&A.


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
- Reply in the same language the user wrote in (mirror the user's language)

## Email-style formatting (${kbAudience === "internal" ? "do not apply; use a clear internal research brief instead" : "apply to every answer"}):
${kbAudience === "internal" ? "Use descriptive headings, concise paragraphs, and numbered steps. Do not open like a support email and do not offer escalation to a support agent." : `
Write each reply like a short, professional support email — but WITHOUT a signature block or sign-off.
- Open with a brief greeting line ("Hi," or "Hi <name>," if the user's name is known), then a blank line.
- Body in short paragraphs separated by blank lines; use numbered steps (1., 2., 3.) for instructions.
- Close with one short line offering further help (e.g. "Happy to help if anything is unclear.").
- Do NOT add "Best regards", "Milo — TripEX Support", subject lines, or "From:"/"To:" headers.
- Keep it tight and readable — no walls of text.
`}



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

    // ── The attachments the model is meant to look at ──
    // Only the "ask" route gets here — the scan route returned long before this. They go onto
    // the CURRENT user message, so the model sees them as part of what the user just said.
    if (attachmentRoute === "ask" && attachments.length > 0) {
      const parts: Record<string, any>[] = attachments.map((payload) => ({
        type: "image_url",
        image_url: { url: toDataUrl(payload) },
      }));
      const last = messages[messages.length - 1];
      if (last?.role === "user") {
        parts.push({ type: "text", text: String(last.content) });
        (last as Record<string, any>).content = parts;
      } else {
        // History didn't come back (best-effort reads) — send the question with the files
        // rather than send the files with no question.
        parts.push({ type: "text", text });
        messages.push({ role: "user", content: parts } as unknown as typeof messages[number]);
      }

      // Appended to the system prompt, to stop the two failures available here: reading the
      // picture out loud instead of using it (the scanner's job, and the thing being fixed),
      // and answering about an attachment it could not make out rather than saying so.
      messages[0].content +=
        `\n\n## THE USER ATTACHED ${attachments.length === 1 ? "A FILE" : `${attachments.length} FILES`} TO THIS MESSAGE\n` +
        "It is in this message, and you can see it. It is CONTEXT for what they are asking — not a " +
        "receipt to process. Someone else already decided it is not an expense document.\n" +
        "- Use it to understand the question: a screenshot of the screen they are stuck on, an " +
        "error, a form, a table, a document.\n" +
        "- Do NOT transcribe it, and do NOT list fields out of it. Nobody asked for its contents.\n" +
        "- If they attached it without a question, say what you can see and ask what they need " +
        "done with it — in their own language.\n" +
        "- If you cannot make it out, say so plainly and ask for a clearer one. Never guess at " +
        "what it might have shown.\n";
    }

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
      metadata: { actions: mapping.actions, redirectPage: mapping.redirectPage || "", sources: knowledgeSources },
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
        data: { sources: knowledgeSources },
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
