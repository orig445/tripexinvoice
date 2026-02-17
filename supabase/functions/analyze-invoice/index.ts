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

    const ORACLE_API_KEY = Deno.env.get("oracleapikey_2");
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

    const systemPrompt = `You are an expert invoice/receipt analyzer. Analyze the image and extract the following information.

CRITICAL - Finding the Document Number:
Look for labels such as: Invoice #, Receipt #, Document #, Reference #, or any prominent number at the top of the document.
For Hebrew documents, look for: "מספר קבלה", "אסמכתא", "מס' חשבונית", "מספר חשבונית", or "מס'" followed by digits.
Do NOT confuse the document number with a business registration number (e.g. ח.פ. / עוסק מורשה).

CRITICAL - Amount Extraction Rules (FOLLOW THESE STEPS IN ORDER):

STEP 1: Find the TOTAL (the LARGEST monetary amount on the document).
  Look for these labels IN THIS PRIORITY ORDER:
  - "AMOUNT DUE" (highest priority - this is ALWAYS the total)
  - "Total Amount" / "Total"
  - "Grand Total"
  - "Amount Payable"
  - "סה"כ לתשלום" / "סה"כ כולל מע"מ"
  The total is almost always the BIGGEST number on the invoice.

STEP 2: Find the TAX/VAT (the SMALLEST of the three amounts).
  Look for: VAT, Tax, מע"מ, GST.
  The tax/VAT is ALWAYS SMALLER than the subtotal. It is typically 5%-25% of the subtotal.
  
STEP 3: Find the SUBTOTAL (the amount BEFORE tax).
  Look for: Subtotal, Net Amount, סה"כ לפני מע"מ.
  The subtotal is LARGER than tax but SMALLER than total.

ABSOLUTE RULES - DO NOT VIOLATE:
- tax_amount is ALWAYS SMALLER than subtotal. If you think tax > subtotal, you have them SWAPPED.
- subtotal + tax_amount = total_amount (approximately)
- Do NOT put the subtotal value in the tax_amount field
- Do NOT put the tax/VAT value in the subtotal field

IMPORTANT - Fields to IGNORE (do NOT use these as total_amount):
- "Cash" / "Cash Received" / "Cash Tendered" - this is the payment given by the customer, NOT the total
- "Change" / "Change Due" - this is change returned to the customer
- Any field showing payment method or tendered amount

- If only one amount exists, treat it as total_amount.
- If "Amount Due" and "Total" both exist, use "Amount Due" as total_amount.

CRITICAL - Currency Detection Rules (in order of priority):
1. Look for EXPLICIT currency codes printed on the invoice (PHP, USD, ILS, EUR, GBP, THB, JPY, etc.) — use that code exactly.
2. Look for currency SYMBOLS: ₪=ILS, $=USD, €=EUR, £=GBP, ₱=PHP, ¥=JPY/CNY, ฿=THB, ₩=KRW, etc.
3. ONLY as a last resort, infer from language: Hebrew→ILS, English→USD, etc.
- NEVER default to ILS just because the text is in Hebrew.
- Always read what is ACTUALLY printed on the document.

Return a JSON object with ONLY these fields:
{
  "invoice_number": "string - the document/receipt number, NOT the business ID",
  "invoice_date": "YYYY-MM-DD or null",
  "total_amount": number or null (the LARGEST amount - final amount to pay),
  "subtotal": number or null (BEFORE tax - must be LARGER than tax_amount),
  "tax_amount": number or null (VAT/tax - must be SMALLER than subtotal),
  "currency": "USD/ILS/EUR/GBP/PHP/etc based on detection rules above"
}

Return ONLY the JSON object, no additional text.`;

    // Call Oracle Generative AI (OCI) - US Chicago region
    // Using OpenAI-compatible chat completions endpoint with Meta Llama 4 Maverick Vision
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

    // Post-processing: only swap if tax > subtotal (clearly reversed)
    const total = parsedData.total_amount;
    const sub = parsedData.subtotal;
    const tax = parsedData.tax_amount;

    if (total != null && sub != null && tax != null && tax > sub) {
      console.log(`Swapping tax (${tax}) and subtotal (${sub}) — tax was larger`);
      parsedData.subtotal = tax;
      parsedData.tax_amount = sub;
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
