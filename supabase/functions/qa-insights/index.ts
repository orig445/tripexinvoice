import { corsHeaders } from 'npm:@supabase/supabase-js@2/cors'

interface Row {
  date?: string
  user?: string
  source?: string
  intent?: string | null
  question?: string
  answer?: string
}

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: corsHeaders })
  }

  try {
    const { question, rows = [], stats = {}, history = [] } = await req.json() as {
      question: string
      rows: Row[]
      stats: Record<string, unknown>
      history: { role: string; content: string }[]
    }

    if (!question || typeof question !== 'string') {
      return new Response(JSON.stringify({ error: 'Question is required' }), {
        status: 400,
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      })
    }

    const apiKey = Deno.env.get('oracleapikey') || Deno.env.get('LOVABLE_API_KEY')
    if (!apiKey) {
      return new Response(JSON.stringify({ error: 'AI is not configured' }), {
        status: 500,
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      })
    }

    // Keep the payload bounded so the model stays inside its context window.
    const sample = rows.slice(0, 300).map((r, i) =>
      `#${i + 1} | ${r.date ?? ''} | user: ${r.user ?? ''} | source: ${r.source ?? ''} | intent: ${r.intent ?? '-'}\n` +
      `Q: ${(r.question ?? '').slice(0, 400)}\n` +
      `A: ${(r.answer ?? '').slice(0, 400)}`
    ).join('\n---\n')

    const systemPrompt =
      'You are the Q&A Analytics assistant for the TripEX dashboard. ' +
      'You answer ONLY using the dataset provided below (the currently filtered questions and answers) and the summary stats. ' +
      'Never invent data, never answer questions unrelated to this dataset. ' +
      'If the answer cannot be derived from the dataset, reply exactly: "This information is not available in the current Q&A data." ' +
      'Always answer in English. Be concise, use bullet points and concrete numbers, and quote example questions when helpful.\n\n' +
      `SUMMARY STATS: ${JSON.stringify(stats)}\n\n` +
      `DATASET (${rows.length} filtered rows, showing up to 300):\n${sample}`

    const res = await fetch(
      'https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions',
      {
        method: 'POST',
        headers: { Authorization: `Bearer ${apiKey}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model: 'meta.llama-4-maverick-17b-128e-instruct-fp8',
          max_tokens: 1500,
          temperature: 0.2,
          messages: [
            { role: 'system', content: systemPrompt },
            ...history.slice(-6).map((m) => ({ role: m.role, content: m.content })),
            { role: 'user', content: question },
          ],
        }),
      },
    )

    if (!res.ok) {
      const detail = await res.text()
      console.error('qa-insights gateway error:', res.status, detail)
      return new Response(JSON.stringify({ error: 'AI request failed' }), {
        status: res.status === 429 ? 429 : 502,
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      })
    }

    const data = await res.json()
    const text = data?.choices?.[0]?.message?.content?.trim() ?? ''

    return new Response(JSON.stringify({ text }), {
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  } catch (err) {
    console.error('qa-insights error:', err)
    return new Response(JSON.stringify({ error: 'Unexpected error' }), {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    })
  }
})
