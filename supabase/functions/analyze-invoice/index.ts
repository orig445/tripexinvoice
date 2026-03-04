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
DOCUMENT TYPE
═══════════════════════════════════════
Classify as one of: "sales_invoice", "official_receipt", "charge_invoice", "delivery_receipt", "credit_memo", "debit_memo", "purchase_order", "quotation", "receipt", "other"
Look for labels like "SALES INVOICE", "OFFICIAL RECEIPT", "OR", "SI", "CHARGE INVOICE" etc.

═══════════════════════════════════════
VENDOR / MERCHANT NAME (HIGHEST ERROR RATE - BE CAREFUL)
═══════════════════════════════════════
- The vendor/store name is usually the LARGEST text at the TOP of the receipt.
- Read it CHARACTER BY CHARACTER. Do not guess or autocomplete.
- Common Philippine chains to watch for (match these exactly if you recognize them):
  ZUISPRESSO, NESPRESSO, S&R PIZZA, KENNY ROGERS ROASTERS, STARBUCKS, SAVEMORE, 
  THE COFFEE BEAN & TEA LEAF, MCDONALD'S, JOLLIBEE, MR. KIMBOB BIBIMBOB, 7-ELEVEN,
  BENCH, BENCH BOUTIQUE, UNIQLO, SM DEPARTMENT STORE, WATSONS, MERCURY DRUG
- Also look for the parent company name in parentheses or smaller text below the store name.
  Example: "BENCH BOUTIQUE" with "Suyen Corporation" → name = "Bench Boutique (Suyen Corporation)"
- If the name is partially illegible, return what you CAN read clearly. Do NOT invent letters.
- NEVER confuse the POS provider name with the vendor name.

═══════════════════════════════════════
MERCHANT ADDRESS & CITY
═══════════════════════════════════════
- Extract the FULL address printed below/near the merchant name.
- Separate into "address" (street, floor, unit, building, barangay) and "city" (city + country).
- Example: "2nd Flr, Unit 228-231, Paranaque Integrated, Brgy. Tambo" → address
           "Paranaque City, Philippines" → city

═══════════════════════════════════════
INVOICE/RECEIPT NUMBER
═══════════════════════════════════════
- Look for labels: "Invoice No", "SI#", "OR NO.", "Receipt #", "Document #"
- For Hebrew: "מספר קבלה", "אסמכתא", "מס' חשבונית"
- Do NOT use: TIN numbers, MIN numbers, PTU numbers, serial numbers, accreditation numbers.
- Return the EXACT string printed (including leading zeros).

═══════════════════════════════════════
DATE EXTRACTION (CRITICAL)
═══════════════════════════════════════
- Find the transaction/invoice date, usually near the top with a time stamp.
- READ THE EXACT DIGITS. Do not guess the year.
- Philippine receipts use MM/DD/YYYY format → output as YYYY-MM-DD
- Hebrew receipts use DD/MM/YYYY format → output as YYYY-MM-DD
- NEVER use accreditation dates, PTU dates as the invoice date.

═══════════════════════════════════════
CURRENCY DETECTION
═══════════════════════════════════════
- Philippine address/TIN/PHP symbol → currency is ALWAYS "PHP"
- Hebrew text/Israeli address/₪ → currency is ALWAYS "ILS"
- Thai text/฿ → "THB"
- NEVER default to USD for Philippine or Israeli receipts.

═══════════════════════════════════════
TIN EXTRACTION
═══════════════════════════════════════
- For Philippine receipts, TIN format: XXX-XXX-XXX-XXXXX
- Extract the VENDOR's TIN from the top. Ignore customer TIN at the bottom.
- For non-Philippine receipts, return null.

═══════════════════════════════════════
AMOUNTS (CRITICAL - READ EXACTLY)
═══════════════════════════════════════
Extract these EXACT amounts as printed on the document:

1. "vatable_sales_amount" - Look for: "VATable Sales", "VATable Amount", "Net of VAT"
   This is the base amount BEFORE tax. Read the exact number.

2. "non_vatable_sales_amount" - Look for: "Non-VAT", "VAT-Exempt Sales", "Zero Rated Sales"
   Often 0.00. Read the exact number. If not found, use 0.00.

3. "service_charge_amount" - Look for: "Service Charge", "SC"
   Often 0.00. Read the exact number. If not found, use 0.00.

4. "tax_amount" - Look for: "VAT Amount", "VAT (12%)", "Tax Amount", "מע"מ"
   This is the TAX portion. Must be SMALLER than vatable_sales_amount.
   Read the EXACT number. Do NOT calculate it yourself.

COMPLETELY IGNORE these fields (they are NOT amounts to extract):
- "Cash" / "Cash Received" / "Cash Tendered" / "Amount Tendered"
- "Change" / "Change Due"

═══════════════════════════════════════
PAYMENT INFO
═══════════════════════════════════════
- "method": How did the customer pay? "Cash", "Credit Card", "Debit Card", "GCash", "Maya", etc.
- "amount_paid": The actual amount the customer handed over (Cash Tendered / Amount Tendered).
  This may be MORE than the total (customer gave change). Read exact number.

═══════════════════════════════════════
OUTPUT FORMAT (JSON ONLY, NO OTHER TEXT)
═══════════════════════════════════════
{
  "document_type": "sales_invoice|official_receipt|receipt|other",
  "invoice_number": "string or null",
  "invoice_date": "YYYY-MM-DD or null",
  "currency": "PHP|ILS|USD|EUR|etc",
  "merchant": {
    "name": "string or null",
    "tin": "string or null",
    "address": "string or null",
    "city": "string or null"
  },
  "amounts": {
    "vatable_sales_amount": number or null,
    "non_vatable_sales_amount": number or null,
    "service_charge_amount": number or null,
    "tax_amount": number or null
  },
  "payment": {
    "method": "string or null",
    "amount_paid": number or null
  }
}

Return ONLY the JSON. No explanation, no markdown.${correctionsContext}`;

    // Call Oracle AI
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
      let cleaned = content.replace(/```json\n?/g, "").replace(/```\n?/g, "").trim();
      // Find JSON boundaries using brace counting
      const startIdx = cleaned.indexOf("{");
      if (startIdx !== -1) {
        let depth = 0;
        let endIdx = -1;
        let inString = false;
        let escapeNext = false;
        for (let i = startIdx; i < cleaned.length; i++) {
          const ch = cleaned[i];
          if (escapeNext) { escapeNext = false; continue; }
          if (ch === "\\") { escapeNext = true; continue; }
          if (ch === '"') { inString = !inString; continue; }
          if (inString) continue;
          if (ch === "{") depth++;
          else if (ch === "}") { depth--; if (depth === 0) { endIdx = i; break; } }
        }
        if (endIdx !== -1) cleaned = cleaned.substring(startIdx, endIdx + 1);
      }
      // Fix common JSON issues
      cleaned = cleaned
        .replace(/,\s*}/g, "}")
        .replace(/,\s*]/g, "]")
        .replace(/[\x00-\x1F\x7F]/g, "");
      parsedData = JSON.parse(cleaned);
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

