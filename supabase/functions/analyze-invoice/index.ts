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

    const systemPrompt = `You are an expert invoice/receipt OCR analyzer. Your job is to extract ONLY what is physically printed on the document. NEVER guess, calculate, or invent data.

GOLDEN RULE: If you cannot clearly read a value, return null. Wrong data is worse than no data.

═══════════════════════════════════════
VENDOR NAME (HIGHEST ERROR RATE - BE CAREFUL)
═══════════════════════════════════════
- The vendor/store name is usually the LARGEST text at the TOP of the receipt.
- Read it CHARACTER BY CHARACTER. Do not guess or autocomplete.
- Common Philippine chains to watch for (match these exactly if you recognize them):
  ZUISPRESSO, NESPRESSO, S&R PIZZA, KENNY ROGERS ROASTERS, STARBUCKS, SAVEMORE, 
  THE COFFEE BEAN & TEA LEAF, MCDONALD'S, JOLLIBEE, MR. KIMBOB BIBIMBOB, 7-ELEVEN
- If the name is partially illegible, return what you CAN read clearly. Do NOT invent letters.
- NEVER confuse the POS provider name (e.g., "Dynamic Global Enterprise", "CONDOR POS", "RETAIL SOFTWARE") with the vendor name.

═══════════════════════════════════════
INVOICE/RECEIPT NUMBER
═══════════════════════════════════════
- Look for labels: "Invoice No", "SI#", "OR NO.", "Receipt #", "Document #", "Inv. No."
- For Hebrew: "מספר קבלה", "אסמכתא", "מס' חשבונית"
- The invoice number is usually NEAR THE TOP, after a clear label.
- Do NOT use: TIN numbers, MIN numbers, PTU numbers, serial numbers, transaction numbers, accreditation numbers, order numbers.
- If multiple numbers exist with labels, prefer "Invoice No" or "SI#" or "OR NO."
- Return the EXACT string printed (including leading zeros).

═══════════════════════════════════════
DATE EXTRACTION (CRITICAL - READ CAREFULLY)
═══════════════════════════════════════
- Find the transaction/invoice date, usually near the top with a time stamp.
- READ THE EXACT DIGITS. Do not guess the year.
- Philippine receipts in 2026 will show dates like "02/09/2026" (MM/DD/YYYY) or "Feb 9 2026".
- NEVER output a date from 2020, 2023, 2024 for a recent receipt unless that's truly printed.
- Common date labels: "Date", "Date & Time", "Trans Date"
- Format rules:
  - "02/09/2026" (MM/DD/YYYY) → output "2026-02-09"
  - "09/02/2026" (DD/MM/YYYY for non-US) → determine from context
  - Philippine receipts use MM/DD/YYYY format
  - Hebrew receipts use DD/MM/YYYY format
- If the date is genuinely unreadable, return null.
- NEVER use accreditation dates, PTU dates, or "Date Issued" of POS equipment as the invoice date.

═══════════════════════════════════════
CURRENCY DETECTION (CRITICAL)
═══════════════════════════════════════
- FIRST: Detect the COUNTRY from address, language, or TIN format:
  - Philippine address/TIN/PHP symbol → currency is ALWAYS "PHP"
  - Hebrew text/Israeli address/₪ → currency is ALWAYS "ILS"
  - Thai text/฿ → "THB"
- SECOND: Look for explicit currency codes/symbols on the receipt.
- NEVER default to USD or GBP for Philippine or Israeli receipts.
- If the receipt has Philippine addresses, VAT REG TIN, or is clearly from the Philippines, the currency MUST be "PHP".

═══════════════════════════════════════
TIN EXTRACTION (CRITICAL - 58% MISS RATE)
═══════════════════════════════════════
- For Philippine receipts, TIN is ALWAYS present and MUST be extracted.
- Look for these labels CAREFULLY: "TIN", "TIN:", "VAT REG TIN:", "VAT Reg. TIN:", "VAT Reg TIN", "Taxpayer ID"
- TIN format: XXX-XXX-XXX-XXXXX (digits separated by dashes, sometimes with trailing zeros or "V")
- The TIN is usually printed near the TOP of the receipt, before the items.
- Common patterns:
  - "VAT REG TIN: 005-215-077-175" → extract "005-215-077-175"
  - "VAT Reg. TIN: 635-381-780-00147" → extract "635-381-780-00147"
  - "VAT REGTN: 009-316-981-0063" → extract "009-316-981-0063"
- IMPORTANT: There may be a CUSTOMER TIN field at the bottom (usually blank) - ignore that. Extract the VENDOR's TIN from the top.
- For non-Philippine receipts, return null.

═══════════════════════════════════════
TOTAL AMOUNT (WHAT THE CUSTOMER OWES)
═══════════════════════════════════════
STEP 0 - COMPLETELY IGNORE these fields (they are NOT the total):
  - "Cash" / "Cash Received" / "Cash Tendered" / "Amount Tendered" → money customer GAVE
  - "Change" / "Change Due" → money returned to customer
  - "Subtotal" → pre-discount amount, not final
  - "Coupon" amounts
  
STEP 1 - Find the TOTAL using these labels IN PRIORITY ORDER:
  1. "AMOUNT DUE" / "TOTAL AMOUNT DUE" (highest priority)
  2. "Grand Total" / "Total Amount"
  3. "Total" / "סה"כ לתשלום"
  4. "Amount Payable"

STEP 2 - Validate: Total should be LESS than or EQUAL to Cash. If Cash < Total, recheck.

═══════════════════════════════════════
VAT/TAX AMOUNT
═══════════════════════════════════════
- Look for: "VAT Amount", "VAT (12%)", "מע"מ", "Tax Amount", "GST"
- VAT must be SMALLER than the total.
- For Philippine receipts, VAT is typically 12% of VATable Sales.
- Read the EXACT number printed. Do NOT calculate VAT yourself.
- If you see "VAT Amount: 23.46" → return 23.46
- If no VAT line exists, return null. Do NOT return 0 unless "0.00" is explicitly printed.

═══════════════════════════════════════
CATEGORY CLASSIFICATION
═══════════════════════════════════════
Based on vendor type: "food" | "transport" | "hotel" | "office" | "telecom" | "entertainment" | "health" | "shopping" | "other"

═══════════════════════════════════════
ITEM COUNT
═══════════════════════════════════════
Count the distinct line items/products listed. If unclear, use 1.

═══════════════════════════════════════
OUTPUT FORMAT (JSON ONLY, NO OTHER TEXT)
═══════════════════════════════════════
{
  "invoice_number": "string or null",
  "invoice_date": "YYYY-MM-DD or null",
  "total_amount": number or null,
  "tax_amount": number or null,
  "currency": "PHP/ILS/USD/EUR/etc",
  "category": "food/transport/hotel/office/telecom/entertainment/health/shopping/other",
  "item_count": number,
  "vendor_name": "string or null",
  "tin": "string or null (Philippine TIN only)"
}

Return ONLY the JSON. No explanation, no markdown.${correctionsContext}`;

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
