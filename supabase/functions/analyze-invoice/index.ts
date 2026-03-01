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

    // Fetch past corrections to inject as learning context
    let correctionsContext = "";
    try {
      const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
      const supabaseKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
      const res = await fetch(`${supabaseUrl}/rest/v1/invoice_corrections?order=created_at.desc&limit=50`, {
        headers: {
          "apikey": supabaseKey,
          "Authorization": `Bearer ${supabaseKey}`,
        },
      });
      if (res.ok) {
        const corrections = await res.json();
        if (corrections.length > 0) {
          const lines = corrections.map((c: any) =>
            `- Field "${c.field_name}": AI extracted "${c.original_value}" but correct value was "${c.corrected_value}"${c.context ? ` (context: ${c.context})` : ""}`
          ).join("\n");
          correctionsContext = `\n\nLEARNING FROM PAST MISTAKES — Apply these corrections patterns:\n${lines}\n`;
        }
      }
    } catch (e) {
      console.error("Failed to fetch corrections:", e);
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

CRITICAL - Date Extraction Rules (HIGHEST PRIORITY):
- Look CAREFULLY for the date PRINTED on the document. Common labels: "Date", "Invoice Date", "תאריך", "Date Issued", "Transaction Date".
- The date is usually near the top of the receipt, next to the invoice number or time.
- Read the EXACT digits printed. Do NOT guess or estimate the date.
- If you see "06/27/2025" → that is MM/DD/YYYY → output "2025-06-27"
- If you see "27/06/2025" → that is DD/MM/YYYY → output "2025-06-27"
- If you see "2025-06-27" → output as-is
- If you see "27 Jun 2025" or "June 27, 2025" → output "2025-06-27"
- For Hebrew months: ינואר=01, פברואר=02, מרץ=03, אפריל=04, מאי=05, יוני=06, יולי=07, אוגוסט=08, ספטמבר=09, אוקטובר=10, נובמבר=11, דצמבר=12
- NEVER invent a date. If no date is visible, return null.
- NEVER use today's date or any default date.
- Double-check: read the digits on the image one more time before answering.

CRITICAL - Finding the Document Number:
Look for labels such as: Invoice #, Receipt #, Document #, Reference #, or any prominent number at the top of the document.
For Hebrew documents, look for: "מספר קבלה", "אסמכתא", "מס' חשבונית", "מספר חשבונית", or "מס'" followed by digits.
Do NOT confuse the document number with a business registration number (e.g. ח.פ. / עוסק מורשה).

CRITICAL - Amount Extraction Rules (FOLLOW THESE STEPS IN ORDER):

STEP 0: FIRST, identify and COMPLETELY IGNORE these fields:
  - "Cash" / "Cash Received" / "Cash Tendered" / "Paid" / "Payment" — this is money the customer GAVE, not the charge
  - "Change" / "Change Due" / "Change Given" — this is money returned to the customer
  - These amounts are NEVER the total. Pretend they don't exist.

STEP 1: Find the TOTAL — the amount the customer was CHARGED.
  Look for these labels IN THIS PRIORITY ORDER:
  - "AMOUNT DUE" (highest priority - this is ALWAYS the total)
  - "Total Amount" / "Total"
  - "Grand Total"
  - "Amount Payable"
  - "סה"כ לתשלום" / "סה"כ כולל מע"מ"
  IMPORTANT: The total is the amount the customer OWES, NOT the amount of cash they handed over.
  If "Amount Due" says 150 and "Cash" says 200, the total is 150.

STEP 2: Find the TAX/VAT.
  Look for: VAT, Tax, מע"מ, GST.
  The tax/VAT is ALWAYS SMALLER than the total.

ABSOLUTE RULES - DO NOT VIOLATE:
- Cash/Change amounts are NEVER the total_amount
- If only one labeled amount exists (excluding Cash/Change), treat it as total_amount.
- If "Amount Due" and "Total" both exist, use "Amount Due" as total_amount.

CRITICAL - Currency Detection Rules (in order of priority):
1. Look for EXPLICIT currency codes printed on the invoice (PHP, USD, ILS, EUR, GBP, THB, JPY, etc.) — use that code exactly.
2. Look for currency SYMBOLS: ₪=ILS, $=USD, €=EUR, £=GBP, ₱=PHP, ¥=JPY/CNY, ฿=THB, ₩=KRW, etc.
3. ONLY as a last resort, infer from language: Hebrew→ILS, English→USD, etc.

CRITICAL - Category Classification:
Based on the vendor name, type of business, or items on the receipt, classify into ONE of these categories:
- "food" — restaurants, cafes, fast food, bakeries, grocery stores, supermarkets
- "transport" — taxis, uber, gas stations, parking, public transit, airlines
- "hotel" — hotels, hostels, Airbnb, lodging
- "office" — office supplies, printing, stationery
- "telecom" — phone, internet, mobile plans
- "entertainment" — movies, events, attractions
- "health" — pharmacy, medical, doctor
- "shopping" — clothing, electronics, general retail
- "other" — anything that doesn't fit above

CRITICAL - Item Count:
Count the number of line items / products on the receipt. If unclear, use 1.

CRITICAL - TIN Extraction (Philippines ONLY):
If the invoice/receipt is from the Philippines (currency is PHP, or Philippine address/language detected):
- Look for "TIN" (Taxpayer Identification Number) on the document.
- Common labels: "TIN", "TIN:", "TIN No.", "VAT Reg TIN", "Taxpayer ID"
- TIN format is typically: XXX-XXX-XXX-XXX or XXX-XXX-XXX-XXXV
- Extract the EXACT printed TIN digits/dashes.
- If NO TIN is visible, return null for the tin field.
- ONLY extract TIN for Philippine documents. For all other countries, return null.

Return a JSON object with ONLY these fields:
{
  "invoice_number": "string - the document/receipt number, NOT the business ID",
  "invoice_date": "YYYY-MM-DD or null",
  "total_amount": number or null (AMOUNT DUE / Total — what the customer owes),
  "tax_amount": number or null (VAT/tax amount),
  "currency": "USD/ILS/EUR/GBP/PHP/etc",
  "category": "food/transport/hotel/office/telecom/entertainment/health/shopping/other",
  "item_count": number (how many line items/products),
  "vendor_name": "string or null",
  "tin": "string or null — Taxpayer Identification Number, ONLY for Philippine invoices"
}

Return ONLY the JSON object, no additional text.${correctionsContext}`;

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

    // Post-processing: ensure category exists
    if (!parsedData.category) parsedData.category = "other";
    if (!parsedData.item_count) parsedData.item_count = 1;

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
