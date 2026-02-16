import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
};

// ── Action Mapping (hardcoded, not AI-dependent) ──
const ACTION_MAPPING: Record<string, { actions: string[]; redirectPage?: string }> = {
  help:    { actions: ["Redirect"],       redirectPage: "help" },
  scan:    { actions: ["Camera"] },
  expense: { actions: ["AddExpense"] },
  bi:      { actions: ["DisplayResults"] },
  online:  { actions: ["Redirect"],       redirectPage: "booking" },
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

    const { data: { user }, error: authError } = await supabase.auth.getUser();
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
    } = await req.json();

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
          return new Response(JSON.stringify({
            actions: [],
            text: "Failed to scan receipt. Please try again.",
            redirectPage: "",
            data: {},
            session_id: sessionId,
          }), {
            status: 200,
            headers: { ...corsHeaders, "Content-Type": "application/json" },
          });
        }

        // Build a summary of the scanned data
        const d = ocrData.data || {};
        const lines: string[] = ["✅ החשבונית נסרקה בהצלחה! הנה הפרטים שזוהו:"];
        if (d.vendor_name) lines.push(`🏪 ספק: ${d.vendor_name}`);
        if (d.invoice_number) lines.push(`🔢 מספר חשבונית: ${d.invoice_number}`);
        if (d.total_amount != null) lines.push(`💰 סכום: ${d.total_amount}${d.currency ? " " + d.currency : ""}`);
        if (d.invoice_date) lines.push(`📅 תאריך: ${d.invoice_date}`);
        if (d.line_items?.length) lines.push(`📋 פריטים: ${d.line_items.length}`);
        lines.push("\nהנתונים נשמרו במערכת. תוכל לצפות בהם בעמוד החשבוניות.");

        return new Response(JSON.stringify({
          actions: [],
          text: lines.join("\n"),
          redirectPage: "",
          data: ocrData.data,
          session_id: sessionId,
        }), {
          status: 200,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      } catch (ocrErr) {
        console.error("OCR error:", ocrErr);
        await supabase.from("chatbot_logs").insert({
          session_id: sessionId,
          user_id: user.id,
          event_type: "error",
          details: { error: String(ocrErr), phase: "ocr" },
        });
        return new Response(JSON.stringify({
          actions: [],
          text: "Error processing image. Please try again.",
          redirectPage: "",
          data: {},
          session_id: sessionId,
        }), {
          status: 200,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
    }

    // ── Text flow ──
    if (!text.trim()) {
      return new Response(JSON.stringify({
        actions: [],
        text: "Hello 👋 I'm TripEX AI. How can I assist you today?",
        redirectPage: "",
        data: {},
        session_id: sessionId,
      }), {
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Save user message
    await supabase.from("chat_messages").insert({
      session_id: sessionId,
      role: "user",
      content: text,
    });

    // Load recent history
    const { data: history } = await supabase
      .from("chat_messages")
      .select("role, content")
      .eq("session_id", sessionId)
      .order("created_at", { ascending: true })
      .limit(10);

    // ── Load chatbot config ──
    const { data: config } = await supabase
      .from("chatbot_config")
      .select("*")
      .eq("is_active", true)
      .limit(1)
      .single();

    const temperature = config?.temperature || 0.3;
    const maxTokens = config?.max_tokens || 2048;
    const modelName = config?.model_name || "meta.llama-4-maverick-17b-128e-instruct-fp8";

    const ORACLE_API_KEY = Deno.env.get("oracleapikey_2");
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

      // Limit to top 5
      allChunks = allChunks.slice(0, 5);

      if (allChunks.length > 0) {
        knowledgeContext = "\n\n## Knowledge Base Context (use this to answer the user):\n" +
          allChunks.map((c: any) => `[${c.file_name}]: ${c.content}`).join("\n\n");
      }
    } catch (ragErr) {
      console.error("RAG search error:", ragErr);
    }

    const systemPrompt = `You are TripEX AI — a friendly, professional customer service assistant for Travel & Expense Management. Your goal is to HELP users who may not be tech-savvy. Be warm, patient, and thorough.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.

## Intent Categories:
- help: user wants guidance or how-to
- scan: user wants to scan a receipt/invoice
- bi: user wants reports, data analysis, or statistics
- online: user wants to book flights/hotels
- expense: user wants to add or manage expenses
- general: casual conversation or anything else

## Response Style:
- Be DETAILED and thorough — explain step by step when needed
- Use friendly, supportive language
- If knowledge base context is provided, quote it extensively and explain it clearly to the user
- Structure long answers with line breaks for readability
- Always greet warmly and offer further help at the end
- Respond in the SAME language as the user (Hebrew for Hebrew, English for English)

## Output format (ONLY this JSON, nothing else):
{"intent": "<intent>", "text": "<your detailed, friendly answer>"}

Current context: source=${source}, scope=${scope}${trid ? `, trid=${trid}` : ""}${knowledgeContext}`;

    const messages = [
      { role: "system", content: systemPrompt },
      ...(history || []).map((m: any) => ({ role: m.role, content: m.content })),
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
      }
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

    try {
      // Try to extract JSON even if there's surrounding text/reasoning
      let jsonStr = rawContent;
      // Remove markdown code blocks
      jsonStr = jsonStr.replace(/```json\n?/g, "").replace(/```\n?/g, "");
      // Find the first { ... } block containing "intent" and "text"
      const jsonMatch = jsonStr.match(/\{[^{}]*"intent"\s*:\s*"[^"]*"[^{}]*"text"\s*:\s*"[^"]*"[^{}]*\}|\{[^{}]*"text"\s*:\s*"[^"]*"[^{}]*"intent"\s*:\s*"[^"]*"[^{}]*\}/s);
      if (jsonMatch) {
        jsonStr = jsonMatch[0];
      } else {
        jsonStr = jsonStr.trim();
      }
      const parsed = JSON.parse(jsonStr);
      intent = parsed.intent || "general";
      responseText = parsed.text || rawContent;
    } catch {
      // If JSON parse fails, strip any reasoning prefix and use raw text
      // Remove common AI reasoning patterns
      responseText = rawContent
        .replace(/^(Since|Given|Based on|Let me|I'll|To provide|However|Here's)[\s\S]*?(?=\{)/i, "")
        .replace(/```json\n?/g, "").replace(/```\n?/g, "").trim();
      // One more attempt to parse
      try {
        const lastAttempt = JSON.parse(responseText);
        intent = lastAttempt.intent || "general";
        responseText = lastAttempt.text || rawContent;
      } catch {
        // Truly plain text - use as-is
      }
    }

    // ── Map intent to actions ──
    const mapping = ACTION_MAPPING[intent] || ACTION_MAPPING.general;
    const finalText = responseText;

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

    return new Response(JSON.stringify({
      actions: mapping.actions,
      text: finalText,
      redirectPage: mapping.redirectPage || "",
      data: {},
      session_id: sessionId,
    }), {
      status: 200,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (error) {
    console.error("ai-router error:", error);
    return new Response(
      JSON.stringify({ error: error instanceof Error ? error.message : "Unknown error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
