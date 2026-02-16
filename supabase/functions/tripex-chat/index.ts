import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
};

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
    const supabaseKey = Deno.env.get("SUPABASE_ANON_KEY")!;
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

    const { message, session_id, source = "web" } = await req.json();
    if (!message) {
      return new Response(JSON.stringify({ error: "Message is required" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const ORACLE_API_KEY = Deno.env.get("oracleapikey_2");
    if (!ORACLE_API_KEY) {
      throw new Error("Oracle API key is not configured");
    }

    // Load chatbot config
    const { data: config } = await supabase
      .from("chatbot_config")
      .select("*")
      .eq("is_active", true)
      .limit(1)
      .single();

    const defaultPrompt = `You are TripEX AI, a Personal Assistant for Travel & Expense Management.

Your job is to categorize and handle user requests following this decision tree:

## Step 1: Categorize the user's request into one of these intents:
- online (booking flights/hotels)
- scan (scan a receipt/invoice)
- bi (business intelligence / data analysis)
- expense (manage expenses)
- approval (approval workflows)
- details (trip details)
- help (user guide / general help)
- general (casual conversation)

## Step 2: Handle each intent:

### Online Booking:
- Check if source is "web" or "mobile"
- If mobile: respond that online booking is not supported on mobile, suggest using web
- If web: extract search parameters (departure airport, return airport, start date, end date)
- If parameters are missing: ask the user for the missing parameters one by one
- Once all parameters are available: respond with action "show_search" and include parameters in metadata

### Scan Receipt:
- Ask if the user has a TR (Travel Request) number
- If TR is not known: ask the user to provide the TR number first
- Once TR is known: respond with action "camera" to open the scanner

### BI (Business Intelligence):
- Analyze the user's data question and provide insights
- If the follow-up is still BI-related: continue the BI conversation
- If not: re-categorize the new request

### Expense:
- Help the user manage expenses (view, add, categorize)
- Respond with action "redirect" to the expenses page when needed

### Approval:
- Help with approval workflows
- This feature is planned for the future - let the user know it's coming soon

### Details:
- Provide trip details and information
- This feature is planned for the future - let the user know it's coming soon

### Help:
- Provide guidance on how to use the TripEX system
- Explain available features: scanning receipts, managing expenses, BI reports, online booking

### General:
- Respond naturally to casual conversation

## Response Format:
ALWAYS respond with valid JSON:
{"intent": "<intent>", "action": "<action>", "text": "<your response>", "metadata": {<optional extra data>}}

Actions: "none", "camera", "redirect", "show_search", "ask_tr", "ask_params"

Always respond in the user's language. Be concise and helpful.`;

    const systemPrompt = config?.system_prompt !== "You are TripEX AI, a helpful assistant for travel and expense management. You help users scan receipts, manage expenses, analyze data, and book travel.\n\nDetect user intent and respond accordingly:\n- Help/guidance -> respond with JSON: {\"intent\": \"help\", \"action\": \"none\", \"text\": \"<your helpful response>\"}\n- Scan receipt -> respond with JSON: {\"intent\": \"scan\", \"action\": \"camera\", \"text\": \"<your response>\"}\n- Analyze data (BI) -> respond with JSON: {\"intent\": \"bi\", \"action\": \"none\", \"text\": \"<your response>\"}\n- Online booking -> respond with JSON: {\"intent\": \"online\", \"action\": \"redirect\", \"text\": \"<your response>\"}\n- Manage expenses -> respond with JSON: {\"intent\": \"expense\", \"action\": \"redirect\", \"text\": \"<your response>\"}\n- General chat -> respond with JSON: {\"intent\": \"general\", \"action\": \"none\", \"text\": \"<your natural response>\"}\n\nAlways respond in the user's language. Always return valid JSON with: intent, action, text." 
      ? (config?.system_prompt || defaultPrompt)
      : defaultPrompt;
    const modelName = config?.model_name || "meta.llama-4-maverick-17b-128e-instruct-fp8";
    const maxTokens = config?.max_tokens || 1024;
    const temperature = config?.temperature || 0.3;

    // Create or reuse session
    let currentSessionId = session_id;
    if (!currentSessionId) {
      const { data: newSession, error: sessErr } = await supabase
        .from("chat_sessions")
        .insert({ user_id: user.id, source })
        .select("id")
        .single();
      if (sessErr) throw sessErr;
      currentSessionId = newSession.id;
    }

    // Save user message
    await supabase.from("chat_messages").insert({
      session_id: currentSessionId,
      role: "user",
      content: message,
    });

    // Load recent history (last 10 messages)
    const { data: history } = await supabase
      .from("chat_messages")
      .select("role, content")
      .eq("session_id", currentSessionId)
      .order("created_at", { ascending: true })
      .limit(10);

    const messages = [
      { role: "system", content: systemPrompt + `\n\nCurrent context: source=${source}` },
      ...(history || []).map((m: any) => ({ role: m.role, content: m.content })),
    ];

    // Call Oracle AI
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

      // Log error
      await supabase.from("chatbot_logs").insert({
        session_id: currentSessionId,
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

    // Parse the response - try JSON first, fallback to plain text
    let intent = "general";
    let action = "none";
    let text = rawContent;

    try {
      const cleaned = rawContent.replace(/```json\n?/g, "").replace(/```\n?/g, "").trim();
      const parsed = JSON.parse(cleaned);
      intent = parsed.intent || "general";
      action = parsed.action || "none";
      text = parsed.text || rawContent;
    } catch {
      // AI returned plain text, treat as general
    }

    // Save assistant message
    await supabase.from("chat_messages").insert({
      session_id: currentSessionId,
      role: "assistant",
      content: text,
      intent,
      metadata: { action, raw: rawContent },
    });

    // Log intent detection
    await supabase.from("chatbot_logs").insert({
      session_id: currentSessionId,
      user_id: user.id,
      event_type: "intent_detected",
      details: { intent, action, message_preview: message.substring(0, 100) },
    });

    return new Response(
      JSON.stringify({
        text,
        intent,
        action,
        session_id: currentSessionId,
      }),
      { status: 200, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error) {
    console.error("tripex-chat error:", error);
    return new Response(
      JSON.stringify({ error: error instanceof Error ? error.message : "Unknown error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
