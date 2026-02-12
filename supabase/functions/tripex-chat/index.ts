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

    const systemPrompt = config?.system_prompt || "You are TripEX AI, a helpful assistant.";
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
      { role: "system", content: systemPrompt },
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
