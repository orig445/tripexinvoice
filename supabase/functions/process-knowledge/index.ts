import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { encode as base64Encode } from "https://deno.land/std@0.168.0/encoding/base64.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
};

const CHUNK_SIZE = 1000; // characters per chunk
const CHUNK_OVERLAP = 200;

function chunkText(text: string): string[] {
  const chunks: string[] = [];
  if (!text || text.trim().length === 0) return chunks;

  let start = 0;
  while (start < text.length) {
    let end = Math.min(start + CHUNK_SIZE, text.length);
    // Try to break at sentence boundary
    if (end < text.length) {
      const lastPeriod = text.lastIndexOf(".", end);
      const lastNewline = text.lastIndexOf("\n", end);
      const breakPoint = Math.max(lastPeriod, lastNewline);
      if (breakPoint > start + CHUNK_SIZE / 2) {
        end = breakPoint + 1;
      }
    }
    const chunk = text.slice(start, end).trim();
    if (chunk.length > 0) chunks.push(chunk);
    start = end - CHUNK_OVERLAP;
    if (start < 0) start = 0;
    if (end >= text.length) break;
  }
  return chunks;
}

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
    const supabaseServiceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabaseAnonKey = Deno.env.get("SUPABASE_ANON_KEY")!;

    // Auth check with user token
    const userClient = createClient(supabaseUrl, supabaseAnonKey, {
      global: { headers: { Authorization: authHeader } },
    });
    const { data: { user }, error: authError } = await userClient.auth.getUser();
    if (authError || !user) {
      return new Response(JSON.stringify({ error: "Unauthorized" }), {
        status: 401,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Use service role for DB operations
    const supabase = createClient(supabaseUrl, supabaseServiceKey);

    const { document_id } = await req.json();
    if (!document_id) {
      return new Response(JSON.stringify({ error: "document_id required" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Get document
    const { data: doc, error: docErr } = await supabase
      .from("knowledge_documents")
      .select("*")
      .eq("id", document_id)
      .single();

    if (docErr || !doc) {
      return new Response(JSON.stringify({ error: "Document not found" }), {
        status: 404,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Update status to processing
    await supabase
      .from("knowledge_documents")
      .update({ status: "processing" })
      .eq("id", document_id);

    try {
      // Download file from storage
      const { data: fileData, error: dlErr } = await supabase.storage
        .from("knowledge")
        .download(doc.file_url);

      if (dlErr || !fileData) {
        throw new Error("Failed to download file: " + (dlErr?.message || "unknown"));
      }

      let extractedText = "";
      const fileType = doc.file_type.toLowerCase();

      if (fileType.includes("text") || fileType.includes("csv") || fileType.includes("json") || fileType.includes("xml") || fileType.includes("markdown")) {
        // Plain text files
        extractedText = await fileData.text();
      } else if (fileType.includes("spreadsheet") || fileType.includes("excel") || fileType.includes("sheet")) {
        // Excel/spreadsheet: use Lovable AI (Gemini) to extract content via vision
        const buffer = await fileData.arrayBuffer();
        const base64 = base64Encode(new Uint8Array(buffer));
        
        const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
        if (!LOVABLE_API_KEY) {
          throw new Error("LOVABLE_API_KEY not configured");
        }

        const resp = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
          method: "POST",
          headers: {
            Authorization: `Bearer ${LOVABLE_API_KEY}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            model: "google/gemini-2.5-flash",
            messages: [
              {
                role: "system",
                content: "Extract ALL text content from this spreadsheet file. Return every cell value, every sheet name, every header, every data row. Preserve the structure as much as possible using plain text. No summaries - just the full raw text content of all sheets.",
              },
              {
                role: "user",
                content: [
                  { type: "text", text: "Extract all text and data from this spreadsheet file:" },
                  {
                    type: "image_url",
                    image_url: { url: `data:${doc.file_type};base64,${base64}` },
                  },
                ],
              },
            ],
            max_tokens: 4096,
            temperature: 0,
          }),
        });

        if (!resp.ok) {
          const errText = await resp.text();
          console.error("Lovable AI error for spreadsheet:", resp.status, errText);
          throw new Error(`AI error processing spreadsheet: ${resp.status}`);
        }

        const spreadsheetData = await resp.json();
        extractedText = spreadsheetData.choices?.[0]?.message?.content || `Spreadsheet: ${doc.file_name}`;
      } else if (fileType.includes("pdf") || fileType.includes("word") || fileType.includes("document")) {
        // For PDF/Word, use Lovable AI (Gemini) which handles documents better
        const buffer = await fileData.arrayBuffer();
        const base64 = base64Encode(new Uint8Array(buffer));

        const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
        if (!LOVABLE_API_KEY) {
          throw new Error("LOVABLE_API_KEY not configured");
        }

        const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
          method: "POST",
          headers: {
            Authorization: `Bearer ${LOVABLE_API_KEY}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            model: "google/gemini-2.5-flash",
            messages: [
              {
                role: "system",
                content: "Extract ALL text content from this document. Return ONLY the raw text, preserving paragraphs and structure. No summaries, no analysis - just the full text content.",
              },
              {
                role: "user",
                content: [
                  { type: "text", text: "Extract all text from this document:" },
                  {
                    type: "image_url",
                    image_url: { url: `data:${doc.file_type};base64,${base64}` },
                  },
                ],
              },
            ],
            max_tokens: 4096,
            temperature: 0,
          }),
        });

        if (!response.ok) {
          const errText = await response.text();
          console.error("Lovable AI error for PDF:", response.status, errText);
          throw new Error(`AI error processing document: ${response.status}`);
        }

        const pdfData = await response.json();
        extractedText = pdfData.choices?.[0]?.message?.content || "";
      } else if (fileType.includes("image")) {
        // Use Oracle AI to describe image content
        const ORACLE_API_KEY = Deno.env.get("oracleapikey_2");
        if (!ORACLE_API_KEY) {
          throw new Error("Oracle API key not configured");
        }

        const buffer = await fileData.arrayBuffer();
        const base64 = base64Encode(new Uint8Array(buffer));

        const response = await fetch(
          "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions",
          {
            method: "POST",
            headers: {
              Authorization: `Bearer ${ORACLE_API_KEY}`,
              "Content-Type": "application/json",
            },
            body: JSON.stringify({
              model: "meta.llama-4-maverick-17b-128e-instruct-fp8",
              messages: [
                {
                  role: "system",
                  content: "Describe this image in detail. Extract any text visible in the image. Describe charts, tables, diagrams if present. Be thorough.",
                },
                {
                  role: "user",
                  content: [
                    { type: "text", text: "Describe this image and extract all text:" },
                    {
                      type: "image_url",
                      image_url: { url: `data:${doc.file_type};base64,${base64}` },
                    },
                  ],
                },
              ],
              max_tokens: 4096,
              temperature: 0,
            }),
          }
        );

        if (!response.ok) {
          const errText = await response.text();
          throw new Error(`Oracle AI error: ${response.status} - ${errText}`);
        }

        const aiData = await response.json();
        extractedText = aiData.choices?.[0]?.message?.content || "";
      } else {
        throw new Error(`Unsupported file type: ${fileType}`);
      }

      // Sanitize: remove null bytes that crash Postgres
      extractedText = extractedText.replace(/\u0000/g, "");

      if (!extractedText.trim()) {
        throw new Error("No text could be extracted from the file");
      }

      // Chunk the text
      const chunks = chunkText(extractedText);

      // Delete existing chunks for this document
      await supabase
        .from("knowledge_chunks")
        .delete()
        .eq("document_id", document_id);

      // Insert new chunks
      const chunkRows = chunks.map((content, idx) => ({
        document_id,
        content,
        chunk_index: idx,
      }));

      const { error: insertErr } = await supabase
        .from("knowledge_chunks")
        .insert(chunkRows);

      if (insertErr) throw insertErr;

      // Update status to ready
      await supabase
        .from("knowledge_documents")
        .update({ status: "ready" })
        .eq("id", document_id);

      return new Response(JSON.stringify({
        success: true,
        chunks_created: chunks.length,
        text_length: extractedText.length,
      }), {
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    } catch (processErr) {
      console.error("Processing error:", processErr);
      await supabase
        .from("knowledge_documents")
        .update({ status: "error" })
        .eq("id", document_id);

      return new Response(JSON.stringify({
        success: false,
        error: processErr instanceof Error ? processErr.message : "Unknown error",
      }), {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
  } catch (error) {
    console.error("process-knowledge error:", error);
    return new Response(
      JSON.stringify({ error: error instanceof Error ? error.message : "Unknown error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
