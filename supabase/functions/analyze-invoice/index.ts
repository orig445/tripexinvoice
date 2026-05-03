import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const ORACLE_URL = "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
const MODEL = Deno.env.get("ORACLE_MODEL") || "google.gemini-2.5-flash";
const ORACLE_COMPARTMENT_ID =
  Deno.env.get("ORACLE_COMPARTMENT_ID") ||
  Deno.env.get("oraclecompartmentid") ||
  Deno.env.get("Oracle__CompartmentId") ||
  "";

function inferFormOfPayment(method: unknown, currentValue: unknown): "credit" | "cash" | "bank" {
  const text = `${String(currentValue ?? "")} ${String(method ?? "")}`.toLowerCase().trim();

  if (
    text.includes("credit") ||
    text.includes("debit") ||
    text.includes("card") ||
    text.includes("visa") ||
    text.includes("master") ||
    text.includes("amex") ||
    text.includes("diners") ||
    text.includes("isracard") ||
    text.includes("ישראכרט") ||
    text.includes("אשראי") ||
    text.includes("כרטיס") ||
    text.includes("סליקה") ||
    text.includes("סליקת") ||
    text.includes("emv") ||
    text.includes("contactless") ||
    text.includes("tap") ||
    text.includes("chip") ||
    text.includes("swipe")
  ) {
    return "credit";
  }

  if (
    text.includes("bank") ||
    text.includes("transfer") ||
    text.includes("wire") ||
    text.includes("iban") ||
    text.includes("העברה")
  ) {
    return "bank";
  }

  if (text.includes("cash") || text.includes("מזומן")) {
    return "cash";
  }

  return "cash";
}

function inferCardType(method: unknown, currentValue: unknown): string | null {
  const text = `${String(currentValue ?? "")} ${String(method ?? "")}`.toLowerCase();

  if (text.includes("visa") || text.includes("ויזה")) return "visa";
  if (text.includes("mastercard") || text.includes("master card") || text.includes("מאסטר") || text.includes("מסטר")) return "mastercard";
  if (text.includes("amex") || text.includes("american express") || text.includes("אמריקן אקספרס")) return "amex";
  if (text.includes("diners") || text.includes("דיינרס")) return "diners";
  if (text.includes("isracard") || text.includes("ישראכרט")) return "isracart";
  if (text.includes("card") || text.includes("כרטיס") || text.includes("אשראי") || text.includes("סליקה")) return "other";

  return null;
}

function inferCardLast4(method: unknown, currentValue: unknown): string | null {
  const existing = String(currentValue ?? "").trim();
  if (/^\d{4}$/.test(existing)) return existing;

  const text = String(method ?? "");
  const maskedMatch = text.match(/(?:\*{2,}|x{2,}|X{2,}|•{2,}|#){1,}[\s-]?(\d{4})/);
  if (maskedMatch?.[1]) return maskedMatch[1];

  const endingMatch = text.match(/(?:card|visa|mastercard|amex|diners|ישראכרט|כרטיס|אשראי|סליקת|סליקה)[^\d]{0,20}(\d{4})\b/i);
  if (endingMatch?.[1]) return endingMatch[1];

  return null;
}

function normalizeInvoiceDate(dateStr: string | null | undefined, currency: string | null | undefined): string | null {
  if (!dateStr || typeof dateStr !== "string") return dateStr ?? null;
  const s = dateStr.trim();
  const isEastAsian = currency === "KRW" || currency === "JPY" || currency === "CNY" || currency === "TWD";
  const isUS = currency === "USD";
  const currentYear = new Date().getUTCFullYear();

  const buildDate = (y: number, m: number, d: number): string | null => {
    if (m < 1 || m > 12 || d < 1 || d > 31) return null;
    const dt = new Date(Date.UTC(y, m - 1, d));
    if (dt.getUTCFullYear() !== y || dt.getUTCMonth() !== m - 1 || dt.getUTCDate() !== d) return null;
    return `${y.toString().padStart(4, "0")}-${m.toString().padStart(2, "0")}-${d.toString().padStart(2, "0")}`;
  };

  // YYYY-MM-DD or YYYY/MM/DD
  const ymd = s.match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})/);
  if (ymd) {
    const y = parseInt(ymd[1]), g2 = parseInt(ymd[2]), g3 = parseInt(ymd[3]);

    // 🩹 East Asian flip: AI parsed "26/02/13" (KRW) as "2013-02-26" — restore to 2026-02-13
    if (isEastAsian && y < currentYear - 5 && g3 >= 13 && g3 <= 31 && g2 >= 1 && g2 <= 12) {
      const flippedYear = 2000 + g3;
      const flippedDay = y % 100;
      if (flippedDay >= 1 && flippedDay <= 31 && flippedYear <= currentYear + 1) {
        const flipped = buildDate(flippedYear, g2, flippedDay);
        if (flipped) {
          console.log(`[DateNorm] East Asian flip: '${s}' → '${flipped}' (currency=${currency})`);
          return flipped;
        }
      }
    }
    if (g2 <= 12 && g3 <= 31) return buildDate(y, g2, g3);
    if (g2 > 12 && g3 <= 12) return buildDate(y, g3, g2);
  }

  // Short YY/MM/DD or DD/MM/YY or MM/DD/YY
  const short = s.match(/^(\d{1,2})[-/](\d{1,2})[-/](\d{2})$/);
  if (short) {
    const p1 = parseInt(short[1]), p2 = parseInt(short[2]), p3 = parseInt(short[3]);
    if (isEastAsian && p2 >= 1 && p2 <= 12 && p3 >= 1 && p3 <= 31) {
      const year = (p1 < 50 ? 2000 : 1900) + p1;
      const built = buildDate(year, p2, p3);
      if (built) {
        console.log(`[DateNorm] East Asian YY/MM/DD: '${s}' → '${built}'`);
        return built;
      }
    }
    const fullYear = (p3 < 50 ? 2000 : 1900) + p3;
    if (isUS && p1 <= 12 && p2 <= 31) return buildDate(fullYear, p1, p2);
    if (p1 <= 31 && p2 <= 12) return buildDate(fullYear, p2, p1);
    if (p1 <= 12 && p2 <= 31) return buildDate(fullYear, p1, p2);
  }

  // Full DD/MM/YYYY or MM/DD/YYYY
  const dmy = s.match(/^(\d{1,2})[-/](\d{1,2})[-/](\d{4})/);
  if (dmy) {
    const g1 = parseInt(dmy[1]), g2 = parseInt(dmy[2]), y = parseInt(dmy[3]);
    if (g1 > 12 && g2 <= 12) return buildDate(y, g2, g1);
    if (g2 > 12 && g1 <= 12) return buildDate(y, g1, g2);
    if (isUS) return buildDate(y, g1, g2);
    return buildDate(y, g2, g1);
  }

  return s;
}

function normalizeInvoiceData(data: any) {
  if (!data || typeof data !== "object") return data;

  const normalized = structuredClone(data);
  const payment = normalized.payment && typeof normalized.payment === "object" ? normalized.payment : {};
  const method = payment.method ?? null;

  const formOfPayment = inferFormOfPayment(method, payment.form_of_payment);
  payment.form_of_payment = formOfPayment;

  if (formOfPayment === "credit") {
    payment.card_last4 = inferCardLast4(method, payment.card_last4);
    payment.card_type = inferCardType(method, payment.card_type);
  } else {
    payment.card_last4 = null;
    payment.card_type = null;
  }

  normalized.payment = payment;

  // Normalize date based on currency
  if (normalized.invoice_date) {
    normalized.invoice_date = normalizeInvoiceDate(normalized.invoice_date, normalized.currency);
  }

  return normalized;
}

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

    let imageContent;
    if (imageBase64) {
      const normalizedBase64 = imageBase64.startsWith("data:") ? imageBase64 : `data:image/jpeg;base64,${imageBase64}`;
      if (normalizedBase64.length < 128) throw new Error("Invalid imageBase64 payload");
      imageContent = {
        type: "image_url",
        image_url: {
          url: normalizedBase64,
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
- If the name is partially illegible, return what you CAN read clearly. Do NOT invent letters.
- NEVER confuse the POS provider name with the vendor name.

═══════════════════════════════════════
MERCHANT ADDRESS & CITY
═══════════════════════════════════════
- Extract the FULL address printed below/near the merchant name.
- Separate into "address" (street, floor, unit, building, barangay) and "city" (city + country).

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
- ⚠️ DATE FORMAT BY COUNTRY/CURRENCY:
  • KOREA (KRW, ₩, Seoul, +82, "THE PLAZA", Hangul): format is **YY/MM/DD** (East Asian).
    Example: "26/02/13" = **2026-02-13** (Feb 13, 2026), NOT 2013-02-26.
  • JAPAN (JPY, +81), CHINA (CNY, +86), TAIWAN (TWD): also **YY/MM/DD** or **YYYY/MM/DD**.
  • USA (USD, US merchant): **MM/DD/YYYY**.
  • PHILIPPINES (PHP): **MM/DD/YYYY**.
  • ISRAEL (ILS, ₪, Hebrew), EUROPE (EUR), UK (GBP), most of world: **DD/MM/YYYY**.
- 2-digit year: <50 → 20YY, ≥50 → 19YY.
- Sanity check: result should be in the past or near-present, NOT 10+ years old unless clearly historical.
- NEVER use accreditation dates, PTU dates as the invoice date.

═══════════════════════════════════════
CURRENCY DETECTION
═══════════════════════════════════════
- Philippine address/TIN/PHP symbol → currency is ALWAYS "PHP"
- Hebrew text/Israeli address/₪ → currency is ALWAYS "ILS"
- Korean address/Seoul/₩/+82 → currency is ALWAYS "KRW"
- Thai text/฿ → "THB"
- NEVER default to USD for Philippine, Israeli, or Korean receipts.

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
- vatable_sales_amount: Look for "VATable Sales", "VATable Amount", "Net of VAT"
- non_vatable_sales_amount: Look for "Non-VAT", "VAT-Exempt Sales", "Zero Rated Sales"
- service_charge_amount: Look for "Service Charge", "SC"
- tax_amount: Look for "VAT Amount", "VAT (12%)", "Tax Amount", "מע"מ"
- COMPLETELY IGNORE: "Cash", "Cash Received", "Cash Tendered", "Amount Tendered", "Change", "Change Due"

═══════════════════════════════════════
PAYMENT INFO (CRITICAL)
═══════════════════════════════════════
- payment.method: return the EXACT text printed for payment method, character by character.
- payment.amount_paid: the actual paid/tendered amount if printed.
- payment.form_of_payment must be one of: "credit", "cash", "bank"
- If the text contains "סליקת אשראי", "סליקה", "אשראי", "כרטיס", "Credit", "Debit", "Visa", "Mastercard", "Amex", "EMV", "Contactless" → form_of_payment = "credit"
- If the text contains "העברה", "bank", "transfer", "wire" → form_of_payment = "bank"
- If the text contains "cash", "מזומן" → form_of_payment = "cash"
- If payment method is missing, default form_of_payment to "cash"
- If it is a credit/debit card transaction, extract payment.card_last4 when visible
- If it is a credit/debit card transaction, extract payment.card_type as one of: "visa", "mastercard", "amex", "diners", "isracart", "other"
- IMPORTANT: "סליקת אשראי" is ALWAYS credit, never cash.

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
    "amount_paid": number or null,
    "form_of_payment": "credit|cash|bank",
    "card_last4": "string or null",
    "card_type": "visa|mastercard|amex|diners|isracart|other|null"
  }
}

Return ONLY the JSON. No explanation, no markdown.${correctionsContext}`;

    const callOracle = async (messages: any[], maxTokens = 1024) => {
      const body: Record<string, unknown> = {
        model: MODEL,
        messages,
        max_tokens: maxTokens,
        temperature: 0.1,
      };

      if (ORACLE_COMPARTMENT_ID) {
        body.compartmentId = ORACLE_COMPARTMENT_ID;
      }

      const response = await fetch(ORACLE_URL, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${ORACLE_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        if (response.status === 429) throw { code: 429, msg: "Rate limit exceeded" };
        if (response.status === 402) throw { code: 402, msg: "Payment required" };
        const errText = await response.text();
        throw new Error(`Oracle AI error: ${response.status} - ${errText}`);
      }
      const data = await response.json();
      return data.choices?.[0]?.message?.content || "";
    };

    const parseJson = (raw: string) => {
      let cleaned = raw.replace(/```json\n?/g, "").replace(/```\n?/g, "").trim();
      const startIdx = cleaned.indexOf("{");
      if (startIdx !== -1) {
        let depth = 0, endIdx = -1, inStr = false, esc = false;
        for (let i = startIdx; i < cleaned.length; i++) {
          const ch = cleaned[i];
          if (esc) { esc = false; continue; }
          if (ch === "\\") { esc = true; continue; }
          if (ch === '"') { inStr = !inStr; continue; }
          if (inStr) continue;
          if (ch === "{") depth++;
          else if (ch === "}") { depth--; if (depth === 0) { endIdx = i; break; } }
        }
        if (endIdx !== -1) cleaned = cleaned.substring(startIdx, endIdx + 1);
      }
      cleaned = cleaned.replace(/,\s*}/g, "}").replace(/,\s*]/g, "]");
      // Remove control chars only OUTSIDE of quoted strings to avoid breaking them
      let sanitized = "";
      let inString = false;
      let escaped = false;
      for (let i = 0; i < cleaned.length; i++) {
        const c = cleaned[i];
        if (escaped) { sanitized += c; escaped = false; continue; }
        if (c === "\\") { sanitized += c; escaped = true; continue; }
        if (c === '"') { inString = !inString; sanitized += c; continue; }
        if (!inString && c.charCodeAt(0) < 32) continue; // skip control chars outside strings
        if (inString && c.charCodeAt(0) < 32) { sanitized += " "; continue; } // replace with space inside strings
        sanitized += c;
      }
      try {
        return JSON.parse(sanitized);
      } catch (e) {
        // Recovery: response was truncated. Close any open string and braces.
        let repaired = sanitized;
        let inStr = false, esc = false, depth = 0;
        for (let i = 0; i < repaired.length; i++) {
          const c = repaired[i];
          if (esc) { esc = false; continue; }
          if (c === "\\") { esc = true; continue; }
          if (c === '"') { inStr = !inStr; continue; }
          if (inStr) continue;
          if (c === "{") depth++;
          else if (c === "}") depth--;
        }
        // Trim trailing escape backslash that would orphan the next char
        if (repaired.endsWith("\\")) repaired = repaired.slice(0, -1);
        // Close unterminated string
        if (inStr) repaired += '"';
        // Remove trailing comma / colon / partial key
        repaired = repaired.replace(/,\s*"[^"]*$/, "");
        repaired = repaired.replace(/:\s*$/, ": null");
        repaired = repaired.replace(/,\s*$/, "");
        // Close unbalanced braces
        while (depth-- > 0) repaired += "}";
        return JSON.parse(repaired);
      }
    };

    let scan1Raw: string;
    let scan1Data: any;
    try {
      scan1Raw = await callOracle([
        { role: "system", content: systemPrompt },
        { role: "user", content: [
          { type: "text", text: "Please analyze this invoice and extract all information as JSON:" },
          imageContent,
        ]},
      ], 2048);
      scan1Data = normalizeInvoiceData(parseJson(scan1Raw));
    } catch (err: any) {
      if (err.code === 429 || err.code === 402) {
        return new Response(JSON.stringify({ error: err.msg }), {
          status: err.code, headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      throw err;
    }

    let finalData = scan1Data;
    const amountPaid = scan1Data?.payment?.amount_paid;
    const vatableAmount = scan1Data?.amounts?.vatable_sales_amount;
    const needsVerification = (amountPaid == null || amountPaid === 0) && (vatableAmount == null || vatableAmount === 0);

    if (needsVerification) {
      try {
        const verifyPrompt = `You are a receipt/invoice verification expert. I extracted the following data from a receipt image. Please look at the SAME image and verify each field. If any value is WRONG, return the corrected JSON. If everything is correct, return the SAME JSON unchanged.

EXTRACTED DATA:
${JSON.stringify(scan1Data, null, 2)}

RULES:
- Compare each field against what you see in the image
- Pay special attention to: amounts, dates, merchant name, TIN, invoice number, payment.method, payment.form_of_payment, payment.card_last4, payment.card_type
- "סליקת אשראי" / "סליקה" / EMV / Contactless must stay credit, never cash
- If VAT amount seems wrong (e.g. larger than vatable sales), fix it
- If merchant name has typos compared to the image, fix it
- Return ONLY the corrected/verified JSON, no explanation`;

        const scan2Raw = await callOracle([
          { role: "system", content: verifyPrompt },
          { role: "user", content: [
            { type: "text", text: "Verify this extracted invoice data against the image:" },
            imageContent,
          ]},
        ], 2048);
        finalData = normalizeInvoiceData(parseJson(scan2Raw));
        console.log("Verification scan completed successfully");
      } catch (verifyErr) {
        console.error("Verification scan failed, using scan 1 data:", verifyErr);
      }
    }

    return new Response(
      JSON.stringify({
        success: true,
        data: finalData,
        rawResponse: scan1Raw!,
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
