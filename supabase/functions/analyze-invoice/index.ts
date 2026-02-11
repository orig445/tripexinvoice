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

    const ORACLE_API_KEY = Deno.env.get("invoice");
    if (!ORACLE_API_KEY) {
      throw new Error("Oracle API key (invoice secret) is not configured");
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

    const systemPrompt = `You are an expert invoice/receipt analyzer. Analyze the image and extract ONLY the essential information.

CRITICAL - Finding the Document Number:
For Hebrew documents, look for these labels (in order of priority):
1. "מספר קבלה" (Receipt number)
2. "אסמכתא" (Reference number)  
3. "מס' חשבונית" or "מספר חשבונית" (Invoice number)
4. "מס'" followed by digits
5. Any prominent number at the top of the document (often starts with digits like 0166, 01, etc.)
6. Look for numbers near "קבלה" or "חשבונית" text

For English documents, look for: Invoice #, Receipt #, Document #, Reference #

CRITICAL - Currency Detection Rules:
- If the invoice is in HEBREW or has ₪ symbol → currency is "ILS"
- If the invoice is in ENGLISH or has $ symbol → currency is "USD"  
- If the invoice is in GERMAN/FRENCH/SPANISH or has € symbol → currency is "EUR"
- If the invoice is in BRITISH ENGLISH or has £ symbol → currency is "GBP"
- Look for explicit currency codes (USD, EUR, ILS, etc.) on the invoice

Return a JSON object with ONLY these 4 fields:
{
  "invoice_number": "string - THE DOCUMENT/RECEIPT NUMBER (e.g., '01667543'), NOT the business ID (עוסק מורשה)",
  "invoice_date": "YYYY-MM-DD or null", 
  "total_amount": number (the final amount to pay, including tax),
  "currency": "USD/ILS/EUR/GBP/etc based on detection rules above"
}

IMPORTANT:
- For invoice_number: This is the RECEIPT/INVOICE number, NOT the business registration number (ח.פ./עוסק מורשה)
- For total_amount: Use the FINAL total amount (after tax), not subtotal
- For currency: Be smart - detect based on language, symbols, and country
- If multiple amounts exist, use the largest/final "Total" or "סה"כ" amount
- Return ONLY the JSON object, no additional text.`;

    // Call Oracle Generative AI (OCI) - EU Frankfurt region
    // Using OpenAI-compatible chat completions endpoint with Llama 3.2 90B Vision
    const response = await fetch(
      "https://inference.generativeai.eu-frankfurt-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions",
      {
        method: "POST",
        headers: {
          Authorization: `Bearer ${ORACLE_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          model: "meta.llama-4-maverick-17b-128e-instruct-fp8",
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
          max_tokens: 1024,
          temperature: 0.1,
        }),
      }
    );

    if (!response.ok) {
      if (response.status === 429) {
        return new Response(
          JSON.stringify({ error: "Rate limit exceeded. Please try again later." }),
          { status: 429, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
      if (response.status === 402) {
        return new Response(
          JSON.stringify({ error: "Payment required. Please check your Oracle Cloud account." }),
          { status: 402, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
      const errorText = await response.text();
      console.error("Oracle AI error:", response.status, errorText);
      throw new Error(`Oracle AI error: ${response.status} - ${errorText}`);
    }

    const aiResponse = await response.json();
    const content = aiResponse.choices?.[0]?.message?.content;

    if (!content) {
      throw new Error("No response from Oracle AI");
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
