import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

function getSupabaseAdmin() {
  const url = Deno.env.get("SUPABASE_URL")!;
  const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
  return { url, key };
}

async function supabaseRest(path: string, options: { method?: string; body?: any } = {}) {
  const { url, key } = getSupabaseAdmin();
  const res = await fetch(`${url}/rest/v1/${path}`, {
    method: options.method || "GET",
    headers: {
      "apikey": key,
      "Authorization": `Bearer ${key}`,
      "Content-Type": "application/json",
      "Prefer": options.method === "POST" ? "return=representation" : "return=minimal",
    },
    ...(options.body ? { body: JSON.stringify(options.body) } : {}),
  });
  if (!res.ok) {
    const errText = await res.text();
    throw new Error(`Supabase REST error ${res.status}: ${errText}`);
  }
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const body = await req.json();
    const action = body.action as string;

    if (action === "bulk-train") {
      // Save OCR result as training sample
      const { extractedData, country, userId } = body;
      if (!extractedData) throw new Error("extractedData is required");

      const imageHash = `hash_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;

      const rows = await supabaseRest("ocr_training_samples", {
        method: "POST",
        body: {
          user_id: userId || null,
          image_hash: imageHash,
          country: country || "IL",
          extracted_data: extractedData,
          is_verified: false,
        },
      });

      const sampleId = rows?.[0]?.id || rows?.id;

      return new Response(
        JSON.stringify({ success: true, sampleId }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    if (action === "verify-sample") {
      const { sampleId, isCorrect, corrections } = body;
      if (!sampleId) throw new Error("sampleId is required");

      // Use PATCH to update
      const { url, key } = getSupabaseAdmin();
      const patchBody: any = {
        is_verified: true,
        is_correct: isCorrect,
      };
      if (corrections) patchBody.corrections = corrections;

      const res = await fetch(`${url}/rest/v1/ocr_training_samples?id=eq.${sampleId}`, {
        method: "PATCH",
        headers: {
          "apikey": key,
          "Authorization": `Bearer ${key}`,
          "Content-Type": "application/json",
          "Prefer": "return=minimal",
        },
        body: JSON.stringify(patchBody),
      });

      if (!res.ok) {
        const errText = await res.text();
        throw new Error(`Failed to verify sample: ${errText}`);
      }

      return new Response(
        JSON.stringify({ success: true }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    if (action === "rebuild-patterns") {
      // Fetch all verified+correct samples
      const samples = await supabaseRest(
        "ocr_training_samples?is_verified=eq.true&is_correct=eq.true&select=extracted_data,country"
      );

      if (!samples || samples.length < 3) {
        return new Response(
          JSON.stringify({ success: false, error: "Need at least 3 verified samples" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }

      // Analyze patterns from verified samples
      const fieldPositions: Record<string, Record<string, number>> = {};
      const fieldCountries: Record<string, Record<string, number>> = {};

      for (const sample of samples) {
        const data = sample.extracted_data;
        const country = sample.country || "unknown";

        // Track which fields have values (indicating reliable extraction)
        const fieldsToTrack = [
          { name: "merchant_name", value: data?.merchant?.name },
          { name: "invoice_number", value: data?.invoice_number },
          { name: "invoice_date", value: data?.invoice_date },
          { name: "currency", value: data?.currency },
          { name: "tax_amount", value: data?.amounts?.tax_amount },
          { name: "vatable_sales", value: data?.amounts?.vatable_sales_amount },
          { name: "payment_method", value: data?.payment?.form_of_payment },
          { name: "document_type", value: data?.document_type },
        ];

        for (const field of fieldsToTrack) {
          if (field.value != null && field.value !== "" && field.value !== 0) {
            if (!fieldPositions[field.name]) fieldPositions[field.name] = {};
            const key = typeof field.value === "string" ? "present" : "numeric_present";
            fieldPositions[field.name][key] = (fieldPositions[field.name][key] || 0) + 1;

            if (!fieldCountries[field.name]) fieldCountries[field.name] = {};
            fieldCountries[field.name][country] = (fieldCountries[field.name][country] || 0) + 1;
          }
        }
      }

      // Delete old patterns
      const { url, key } = getSupabaseAdmin();
      await fetch(`${url}/rest/v1/ocr_training_patterns?id=not.is.null`, {
        method: "DELETE",
        headers: {
          "apikey": key,
          "Authorization": `Bearer ${key}`,
          "Prefer": "return=minimal",
        },
      });

      // Generate new patterns
      const patterns: any[] = [];
      const totalSamples = samples.length;

      for (const [fieldName, positions] of Object.entries(fieldPositions)) {
        const presentCount = Object.values(positions).reduce((a, b) => a + b, 0);
        const confidence = (presentCount / totalSamples) * 100;

        if (confidence >= 50) {
          const topCountry = fieldCountries[fieldName]
            ? Object.entries(fieldCountries[fieldName]).sort((a, b) => b[1] - a[1])[0]?.[0]
            : null;

          let rule = "";
          switch (fieldName) {
            case "merchant_name":
              rule = `Merchant name successfully extracted in ${presentCount}/${totalSamples} samples. Look for largest text at top of receipt.`;
              break;
            case "invoice_date":
              rule = `Date found in ${presentCount}/${totalSamples} samples. ${topCountry === "IL" ? "Hebrew receipts use DD/MM/YYYY." : "Check MM/DD/YYYY or DD/MM/YYYY based on country."}`;
              break;
            case "invoice_number":
              rule = `Invoice/receipt number found in ${presentCount}/${totalSamples} samples. Look for labels: Receipt #, Invoice No, מספר קבלה.`;
              break;
            case "currency":
              rule = `Currency detected in ${presentCount}/${totalSamples} samples. ${topCountry === "IL" ? "Israeli receipts are ILS." : topCountry === "PH" ? "Philippine receipts are PHP." : "Check symbols and address."}`;
              break;
            case "tax_amount":
              rule = `Tax/VAT amount found in ${presentCount}/${totalSamples} samples. Look for VAT Amount, מע"מ labels.`;
              break;
            case "payment_method":
              rule = `Payment method detected in ${presentCount}/${totalSamples} samples. Check for credit card indicators, cash, or bank transfer.`;
              break;
            default:
              rule = `Field ${fieldName} extracted in ${presentCount}/${totalSamples} verified samples.`;
          }

          patterns.push({
            field_name: fieldName,
            pattern_rule: rule,
            country: topCountry,
            confidence,
            source_count: presentCount,
          });
        }
      }

      // Insert new patterns
      if (patterns.length > 0) {
        await supabaseRest("ocr_training_patterns", {
          method: "POST",
          body: patterns,
        });
      }

      return new Response(
        JSON.stringify({
          success: true,
          patternsCreated: patterns.length,
          samplesAnalyzed: totalSamples,
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    if (action === "stats") {
      const { url, key } = getSupabaseAdmin();

      // Get counts
      const [totalRes, verifiedRes, rejectedRes, patternsRes] = await Promise.all([
        fetch(`${url}/rest/v1/ocr_training_samples?select=id&limit=1000`, {
          headers: { "apikey": key, "Authorization": `Bearer ${key}`, "Prefer": "count=exact" },
        }),
        fetch(`${url}/rest/v1/ocr_training_samples?is_verified=eq.true&is_correct=eq.true&select=id&limit=1`, {
          headers: { "apikey": key, "Authorization": `Bearer ${key}`, "Prefer": "count=exact" },
        }),
        fetch(`${url}/rest/v1/ocr_training_samples?is_verified=eq.true&is_correct=eq.false&select=id&limit=1`, {
          headers: { "apikey": key, "Authorization": `Bearer ${key}`, "Prefer": "count=exact" },
        }),
        fetch(`${url}/rest/v1/ocr_training_patterns?select=*`, {
          headers: { "apikey": key, "Authorization": `Bearer ${key}` },
        }),
      ]);

      const totalCount = parseInt(totalRes.headers.get("content-range")?.split("/")?.[1] || "0");
      const verifiedCount = parseInt(verifiedRes.headers.get("content-range")?.split("/")?.[1] || "0");
      const rejectedCount = parseInt(rejectedRes.headers.get("content-range")?.split("/")?.[1] || "0");
      const patterns = patternsRes.ok ? await patternsRes.json() : [];

      // Consume response bodies
      await totalRes.text();
      await verifiedRes.text();
      await rejectedRes.text();

      return new Response(
        JSON.stringify({
          totalSamples: totalCount,
          verifiedSamples: verifiedCount,
          rejectedSamples: rejectedCount,
          patternsLearned: patterns.length,
          patterns: patterns.map((p: any) => ({
            fieldName: p.field_name,
            rule: p.pattern_rule,
            country: p.country,
            confidence: p.confidence,
            sourceCount: p.source_count,
          })),
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    throw new Error(`Unknown action: ${action}`);
  } catch (error) {
    console.error("OCR Training error:", error);
    return new Response(
      JSON.stringify({
        success: false,
        error: error instanceof Error ? error.message : "Unknown error",
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
