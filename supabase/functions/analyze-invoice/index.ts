import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { imageBase64, imageUrl } = await req.json();

    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    // Build the image content based on what was provided
    let imageContent;
    if (imageBase64) {
      imageContent = {
        type: "image_url",
        image_url: {
          url: imageBase64.startsWith("data:") ? imageBase64 : `data:image/jpeg;base64,${imageBase64}`,
        },
      };
    } else if (imageUrl) {
      imageContent = {
        type: "image_url",
        image_url: { url: imageUrl },
      };
    } else {
      throw new Error("Either imageBase64 or imageUrl must be provided");
    }

    const systemPrompt = `You are an expert invoice analyzer. Analyze the invoice image and extract ONLY the essential information.

CRITICAL - Currency Detection Rules:
- If the invoice is in HEBREW or has ₪ symbol → currency is "ILS"
- If the invoice is in ENGLISH or has $ symbol → currency is "USD"  
- If the invoice is in GERMAN/FRENCH/SPANISH or has € symbol → currency is "EUR"
- If the invoice is in BRITISH ENGLISH or has £ symbol → currency is "GBP"
- Look for explicit currency codes (USD, EUR, ILS, etc.) on the invoice
- Consider the vendor's country/address for additional context

Return a JSON object with ONLY these 4 fields:
{
  "invoice_number": "string or null",
  "invoice_date": "YYYY-MM-DD or null", 
  "total_amount": number (the final amount to pay, including tax),
  "currency": "USD/ILS/EUR/GBP/etc based on detection rules above"
}

IMPORTANT:
- For total_amount: Use the FINAL total amount (after tax), not subtotal
- For currency: Be smart - detect based on language, symbols, and country
- If multiple amounts exist, use the largest/final "Total" or "סה"כ" amount
- Return ONLY the JSON object, no additional text.`;

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash",
        messages: [
          { role: "system", content: systemPrompt },
          {
            role: "user",
            content: [
              { type: "text", text: "Please analyze this invoice and extract all information as JSON:" },
              imageContent,
            ],
          },
        ],
      }),
    });

    if (!response.ok) {
      if (response.status === 429) {
        return new Response(
          JSON.stringify({ error: "Rate limit exceeded. Please try again later." }),
          { status: 429, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
      if (response.status === 402) {
        return new Response(
          JSON.stringify({ error: "Payment required. Please add credits to your workspace." }),
          { status: 402, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
      const errorText = await response.text();
      console.error("AI gateway error:", response.status, errorText);
      throw new Error(`AI gateway error: ${response.status}`);
    }

    const aiResponse = await response.json();
    const content = aiResponse.choices?.[0]?.message?.content;

    if (!content) {
      throw new Error("No response from AI");
    }

    // Parse the JSON from the AI response
    let parsedData;
    try {
      // Remove markdown code blocks if present
      const cleanedContent = content.replace(/```json\n?/g, "").replace(/```\n?/g, "").trim();
      parsedData = JSON.parse(cleanedContent);
    } catch (parseError) {
      console.error("Failed to parse AI response:", content);
      throw new Error("Failed to parse invoice data from AI response");
    }

    return new Response(
      JSON.stringify({ 
        success: true, 
        data: parsedData,
        rawResponse: content 
      }),
      { status: 200, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error) {
    console.error("Error analyzing invoice:", error);
    return new Response(
      JSON.stringify({ 
        success: false, 
        error: error instanceof Error ? error.message : "Unknown error" 
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
