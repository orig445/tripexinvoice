using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Service for calling Oracle Generative AI — Gemini 2.5 Flash via OCI API
/// </summary>
public class OracleAiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _compartmentId;
    private readonly TripExDbContext _db;

    // ── Concurrency throttle: max 10 simultaneous OCI calls process-wide ──
    // Higher limit prevents request queueing under bursts of bulk uploads.
    private static readonly SemaphoreSlim _ociThrottle = new(10, 10);

    // ── Image debug saving: disabled by default to reduce disk I/O and avoid disk-full crashes ──
    // Set environment variable OCR_SAVE_DEBUG_IMAGES=true to re-enable.
    private static readonly bool _saveDebugImages =
        string.Equals(Environment.GetEnvironmentVariable("OCR_SAVE_DEBUG_IMAGES"), "true", StringComparison.OrdinalIgnoreCase);

    // ── Image size limits (raw base64 length) ──
    // 10MB raw bytes ≈ 13.3MB base64 chars
    private const int MaxImageBase64Length = 14_000_000;
    private const int MinImageBase64Length = 100;

    public OracleAiService(IHttpClientFactory httpClientFactory, IConfiguration config, TripExDbContext db)
    {
        _httpClient = httpClientFactory.CreateClient();
        // Gemini Vision can take 30-50s on heavier images. 25s was too aggressive and caused
        // 504s in QA → triggering retries → exceeding host (IIS/Nginx) timeout → Thread Abort.
        // 60s gives the model room to finish on a single attempt for typical receipts.
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _db = db;
        _apiKey = config["Oracle:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ORACLE_API_KEY")
            ?? throw new InvalidOperationException("Oracle API key not configured");
        _endpoint = config["Oracle:Endpoint"]
            ?? "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
        _model = config["Oracle:Model"]
            ?? "google.gemini-2.5-flash";
        _compartmentId = config["Oracle:CompartmentId"];
    }

    /// <summary>
    /// Invoice-specific call: sends image + country hint to Gemini 2.5 Flash.
    /// Optionally accepts caller-supplied expenseTypes and formOfPayments so the AI
    /// can return the matching IDs directly.
    /// </summary>
    public async Task<string> CallGeminiFlashAsync(
        string imageBase64,
        string countryHint,
        IList<TripEx.Api.Models.ExpenseTypeOption>? expenseTypes = null,
        IList<TripEx.Api.Models.FormOfPaymentOption>? formOfPayments = null,
        CancellationToken ct = default)
    {
        // ── Validate input ──
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("imageBase64 cannot be empty");

        // ── Extract base64 part and auto-correct MIME type from magic bytes ──
        var base64Part = imageBase64.Contains(",")
            ? imageBase64[(imageBase64.IndexOf(',') + 1)..].Trim()
            : imageBase64.Trim();

        if (base64Part.Length < MinImageBase64Length)
            throw new ArgumentException("Image data is too small or empty — likely corrupted");
        if (base64Part.Length > MaxImageBase64Length)
            throw new ArgumentException(
                $"Image too large ({base64Part.Length / 1_000_000}MB base64). Max ~10MB. Please compress on the client side.");

        // Detect MIME type from first 12 bytes only (lightweight, no full decode)
        string correctedMime = DetectMimeFromBase64Prefix(base64Part);

        // ── PDF: try text extraction first (digital PDFs process in ~1-2s vs 20-30s as image) ──
        // If the PDF has embedded text (digital invoice), send as text — much faster.
        // If it's a scanned PDF (no text), fall through and send as image_url as before.
        if (correctedMime == "application/pdf")
        {
            string? pdfText = null;
            try { pdfText = TryExtractPdfText(base64Part); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                Console.WriteLine($"[OCR] PdfPig assembly not available on this server, falling back to image: {ex.Message}");
            }

            if (pdfText != null)
            {
                Console.WriteLine($"[OCR] PDF text extracted ({pdfText.Length} chars) — sending as text instead of image");
                var prompt2 = await PrepareSystemPromptAsync(countryHint, expenseTypes, formOfPayments);
                var textMessages = new List<OracleMessage>
                {
                    new() { Role = "system", Content = prompt2 },
                    new() { Role = "user", Content = $"Extract data from this invoice text:\n\n{pdfText}" }
                };
                return await ChatAsync(textMessages, 4096, 0.1, ct);
            }
            Console.WriteLine("[OCR] PDF has no embedded text (scanned or PdfPig unavailable) — sending as image to OCI");
        }

        // ── Resize if too large (skip PDFs) ──
        if (correctedMime != "application/pdf")
        {
            try { base64Part = ResizeIfTooLarge(base64Part, out var resizedMime2); if (resizedMime2 != null) correctedMime = resizedMime2; }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                Console.WriteLine($"[OCR] ImageSharp assembly not available on this server, skipping resize: {ex.Message}");
            }
        }

        var imageUrl = $"data:{correctedMime};base64,{base64Part}";

        var prompt = await PrepareSystemPromptAsync(countryHint, expenseTypes, formOfPayments);

        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = prompt },
            new()
            {
                Role = "user",
                Content = new object[]
                {
                    new { type = "image_url", image_url = new { url = imageUrl } },
                    new { type = "text", text = "Extract data from this invoice." }
                }
            }
        };

        return await ChatAsync(messages, 4096, 0.1, ct);
    }

    /// <summary>
    /// FALLBACK: Focused single-purpose call — extract ONLY the final total amount.
    /// Used when the main scan returns 0/missing total. Aggressive prompt + higher temp for variation.
    /// Returns plain decimal string (e.g. "123.45") or empty if truly nothing found.
    /// </summary>
    public async Task<string> CallGeminiTotalOnlyAsync(string imageBase64, string countryHint, int retryRound = 1)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("imageBase64 cannot be empty");

        var imageUrl = imageBase64.StartsWith("data:")
            ? imageBase64
            : $"data:image/jpeg;base64,{imageBase64}";

        // Round-specific instructions to force different reasoning paths
        var roundHint = retryRound switch
        {
            1 => "Look at the BOTTOM of the receipt for the FINAL grand total (the largest number, usually labeled Total, Sum, סה\"כ, Итого, 合計, 总计, 합계, 总额, Suma, Gesamt, Totale, Total à payer, รวม, Kabuuan).",
            2 => "Find the LARGEST monetary number on the receipt — that is almost always the final amount paid. Ignore subtotals, item prices, change, tip, and tax-only lines.",
            _ => "Scan EVERY number on this receipt. Output the highest monetary value that represents what the customer paid. If there are multiple candidates, pick the one closest to a 'TOTAL' or 'PAID' label in any language."
        };

        var prompt = $@"You are an OCR expert. Your ONLY job is to extract the FINAL TOTAL AMOUNT from this receipt/invoice.
{roundHint}

Country hint: {countryHint}

CRITICAL RULES:
- Output ONLY a JSON object: {{""total"": ""<number>"", ""currency"": ""<3-letter code>""}}
- The number must be a positive decimal (e.g. ""123.45"" or ""1234"").
- NEVER output 0, null, or empty. If you cannot see a clear total, output the largest monetary number visible.
- Do NOT include currency symbols in the total field — only digits and one decimal point.
- No explanations, no markdown, no extra fields.";

        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = prompt },
            new()
            {
                Role = "user",
                Content = new object[]
                {
                    new { type = "image_url", image_url = new { url = imageUrl } },
                    new { type = "text", text = "Extract the final total amount only." }
                }
            }
        };

        // Higher temperature on later rounds to break out of stuck patterns
        double temp = retryRound switch { 1 => 0.2, 2 => 0.4, _ => 0.6 };
        var raw = await ChatAsync(messages, 256, temp);

        // Parse the focused JSON response
        try
        {
            var json = ParseJsonFromAiResponse(raw);
            if (json.TryGetProperty("total", out var totalProp))
            {
                var totalStr = totalProp.ValueKind == JsonValueKind.Number
                    ? totalProp.GetRawText()
                    : (totalProp.GetString() ?? "");
                return totalStr.Trim();
            }
        }
        catch { /* fall through to regex */ }

        // Last resort: regex-extract first decimal — use ParseAmountSmart for locale-aware normalization
        var m = Regex.Match(raw ?? "", @"(\d{1,3}(?:[,\.\s]\d{3})*(?:[\.,]\d{1,2})?|\d+(?:[\.,]\d{1,2})?)");
        if (!m.Success) return "";
        var parsed = InvoiceService.ParseAmountSmart(m.Value);
        return parsed.HasValue
            ? Math.Round(parsed.Value, 2, MidpointRounding.AwayFromZero)
                  .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>
    /// General-purpose chat (used by ChatService and others)
    /// </summary>
    public async Task<string> ChatAsync(
        List<OracleMessage> messages,
        int maxTokens = 1024,
        double temperature = 0.3,
        CancellationToken ct = default)
    {
        // ── Validate messages ──
        if (messages == null || messages.Count == 0)
            throw new ArgumentException("Messages list cannot be empty");

        var requestDict = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature
        };

        // OCI requires compartmentId in the request body
        if (!string.IsNullOrEmpty(_compartmentId))
            requestDict["compartmentId"] = _compartmentId;

        var serializedBody = JsonSerializer.Serialize(requestDict);

        // ── Validate serialized JSON before sending ──
        try
        {
            using var testDoc = JsonDocument.Parse(serializedBody);
            Console.WriteLine($"[OCI] Request body valid JSON, length={serializedBody.Length}, model={_model}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Request body serialization produced invalid JSON: {ex.Message}");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            serializedBody,
            System.Text.Encoding.UTF8,
            "application/json");

        // ── Throttle: wait for an OCI slot (max 3 concurrent process-wide) ──
        var throttleStart = DateTime.UtcNow;
        await _ociThrottle.WaitAsync(ct);
        var waitedMs = (DateTime.UtcNow - throttleStart).TotalMilliseconds;
        if (waitedMs > 100)
            Console.WriteLine($"[OCI] Throttle wait: {waitedMs:F0}ms (queue depth)");

        HttpResponseMessage response;
        string responseBody;
        try
        {
            Console.WriteLine($"[OCI] Sending request to: {_endpoint}");
            try
            {
                response = await _httpClient.SendAsync(request, ct);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ex.CancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout reached — surface as 504 so retry layer treats it as transient
                throw new OciApiException(
                    $"OCI request timed out after {_httpClient.Timeout.TotalSeconds}s",
                    504, null);
            }
            catch (OperationCanceledException) { throw; } // propagate early-timeout cancellation
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Failed to connect to OCI endpoint: {ex.Message}", ex);
            }

            responseBody = await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            _ociThrottle.Release();
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[OCI] Error {(int)response.StatusCode}: {responseBody}");
            throw new OciApiException(
                $"Oracle AI error: {(int)response.StatusCode} - {responseBody}",
                (int)response.StatusCode,
                responseBody);
        }

        // ── Validate response is valid JSON ──
        if (string.IsNullOrWhiteSpace(responseBody))
            throw new InvalidOperationException("OCI returned empty response");

        Console.WriteLine($"[OCI] Response length={responseBody.Length}, status={response.StatusCode}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[OCI] Invalid JSON response: {responseBody[..Math.Min(500, responseBody.Length)]}");
            throw new InvalidOperationException(
                $"OCI returned invalid JSON (length={responseBody.Length}): {ex.Message}. First 200 chars: {responseBody[..Math.Min(200, responseBody.Length)]}");
        }

        using (doc)
        {
            // ── Extract content with safe navigation ──
            if (!doc.RootElement.TryGetProperty("choices", out var choices))
                throw new InvalidOperationException($"OCI response missing 'choices'. Keys: {string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))}");

            if (choices.GetArrayLength() == 0)
                throw new InvalidOperationException("OCI response 'choices' array is empty");

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("message", out var message))
                throw new InvalidOperationException($"OCI choice[0] missing 'message'. Keys: {string.Join(", ", firstChoice.EnumerateObject().Select(p => p.Name))}");

            if (!message.TryGetProperty("content", out var contentProp))
                throw new InvalidOperationException($"OCI message missing 'content'. Keys: {string.Join(", ", message.EnumerateObject().Select(p => p.Name))}");

            var content = contentProp.GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                // Check for finish_reason to understand why content is empty
                var finishReason = firstChoice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "unknown";
                Console.WriteLine($"[OCI] Warning: Empty content, finish_reason={finishReason}");
                throw new InvalidOperationException($"OCI returned empty content (finish_reason={finishReason})");
            }

            Console.WriteLine($"[OCI] Content extracted, length={content.Length}");
            return content;
        }
    }

    /// <summary>
    /// Prepare system prompt based on country hint + learned patterns from DB.
    /// When expenseTypes / formOfPayments are provided, the prompt instructs the AI
    /// to return the matching IDs from those lists.
    /// </summary>
    private async Task<string> PrepareSystemPromptAsync(
        string? countryHint,
        IList<TripEx.Api.Models.ExpenseTypeOption>? expenseTypes = null,
        IList<TripEx.Api.Models.FormOfPaymentOption>? formOfPayments = null)
    {
        string locale = countryHint?.ToUpperInvariant() switch
        {
            "IL" => "Israel (DD/MM/YYYY, ILS ₪)",
            "PH" => "Philippines (MM/DD/YYYY, PHP ₱)",
            "US" => "United States (MM/DD/YYYY, USD $)",
            "CA" => "Canada (MM/DD/YYYY, CAD $)",
            "AU" => "Australia (DD/MM/YYYY, AUD $)",
            "NZ" => "New Zealand (DD/MM/YYYY, NZD $)",
            "GB" => "United Kingdom (DD/MM/YYYY, GBP £)",
            "UK" => "United Kingdom (DD/MM/YYYY, GBP £)",
            "IE" => "Ireland (DD/MM/YYYY, EUR €)",
            "DE" => "Germany (DD.MM.YYYY, EUR €)",
            "FR" => "France (DD/MM/YYYY, EUR €)",
            "IT" => "Italy (DD/MM/YYYY, EUR €)",
            "ES" => "Spain (DD/MM/YYYY, EUR €)",
            "PT" => "Portugal (DD/MM/YYYY, EUR €)",
            "NL" => "Netherlands (DD/MM/YYYY, EUR €)",
            "BE" => "Belgium (DD/MM/YYYY, EUR €)",
            "AT" => "Austria (DD.MM.YYYY, EUR €)",
            "CH" => "Switzerland (DD.MM.YYYY, CHF)",
            "SE" => "Sweden (YYYY-MM-DD, SEK)",
            "NO" => "Norway (DD.MM.YYYY, NOK)",
            "DK" => "Denmark (DD.MM.YYYY, DKK)",
            "FI" => "Finland (DD.MM.YYYY, EUR €)",
            "PL" => "Poland (DD.MM.YYYY, PLN)",
            "CZ" => "Czech Republic (DD.MM.YYYY, CZK)",
            "HU" => "Hungary (YYYY.MM.DD, HUF)",
            "RO" => "Romania (DD.MM.YYYY, RON)",
            "RU" => "Russia (DD.MM.YYYY, RUB ₽)",
            "TR" => "Turkey (DD.MM.YYYY, TRY ₺)",
            "GR" => "Greece (DD/MM/YYYY, EUR €)",
            "TH" => "Thailand (DD/MM/YYYY, THB ฿)",
            "SG" => "Singapore (DD/MM/YYYY, SGD $)",
            "MY" => "Malaysia (DD/MM/YYYY, MYR)",
            "ID" => "Indonesia (DD/MM/YYYY, IDR)",
            "VN" => "Vietnam (DD/MM/YYYY, VND)",
            "JP" => "Japan (YYYY/MM/DD, JPY ¥)",
            "KR" => "South Korea (YY/MM/DD, KRW ₩)",
            "CN" => "China (YYYY/MM/DD, CNY ¥)",
            "TW" => "Taiwan (YYYY/MM/DD, TWD)",
            "HK" => "Hong Kong (DD/MM/YYYY, HKD $)",
            "IN" => "India (DD/MM/YYYY, INR ₹)",
            "AE" => "UAE (DD/MM/YYYY, AED)",
            "SA" => "Saudi Arabia (DD/MM/YYYY, SAR)",
            "EG" => "Egypt (DD/MM/YYYY, EGP)",
            "ZA" => "South Africa (DD/MM/YYYY, ZAR)",
            "NG" => "Nigeria (DD/MM/YYYY, NGN)",
            "KE" => "Kenya (DD/MM/YYYY, KES)",
            "BR" => "Brazil (DD/MM/YYYY, BRL R$)",
            "AR" => "Argentina (DD/MM/YYYY, ARS $)",
            "MX" => "Mexico (DD/MM/YYYY, MXN $)",
            "CO" => "Colombia (DD/MM/YYYY, COP $)",
            "CL" => "Chile (DD/MM/YYYY, CLP $)",
            _ => countryHint != null ? $"Country: {countryHint} — detect date format and currency from receipt content" : "Unknown locale — detect date format and currency from receipt content"
        };

        // ── Load learned patterns from DB ──
        string learnedPatternsSection = "";
        try
        {
            // Defensive: ensure table exists if background DB-init hasn't finished yet
            await SchemaGuard.EnsureOcrTrainingPatternsAsync(_db);

            var patterns = await _db.OcrTrainingPatterns
                .Where(p => p.Country == null || p.Country == "" || p.Country == (countryHint ?? "").ToUpper())
                .OrderByDescending(p => p.Confidence)
                .Take(20)
                .ToListAsync();

            if (patterns.Count > 0)
            {
                var lines = patterns.Select(p =>
                    $"- {p.FieldName.ToUpper()}: {p.PatternRule} ({p.Confidence:F0}% confidence, from {p.SourceCount} receipts)");
                learnedPatternsSection = $@"

LEARNED PATTERNS (from analyzed receipts — use these to improve accuracy):
{string.Join("\n", lines)}
";
                Console.WriteLine($"[OCI] Injected {patterns.Count} learned patterns into prompt");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCI] Warning: Could not load training patterns: {ex.Message}");
        }

        // ── Build caller-supplied lookup sections ──
        string expenseTypesSection = "";
        if (expenseTypes != null && expenseTypes.Count > 0)
        {
            var lines = expenseTypes.Select(e => $"  {e.Id}: \"{e.Name}\"");
            expenseTypesSection = $@"

EXPENSE TYPE OPTIONS (caller-supplied — pick the best matching ID for expense_type_id):
{string.Join("\n", lines)}
- Return the numeric ID of the best match as ""expense_type_id"".
- If none fit, return null for expense_type_id.";
        }

        string formOfPaymentsSection = "";
        if (formOfPayments != null && formOfPayments.Count > 0)
        {
            var lines = formOfPayments.Select(f => $"  {f.Id}: \"{f.Name}\"");
            formOfPaymentsSection = $@"

FORM OF PAYMENT OPTIONS (caller-supplied — pick the best matching ID for form_of_payment_id):
{string.Join("\n", lines)}
- Return the numeric ID of the best match as ""form_of_payment_id"".
- If none fit, return null for form_of_payment_id.";
        }

        // ── Build dynamic output format additions ──
        string expenseTypeIdField = expenseTypes != null && expenseTypes.Count > 0
            ? @",
  ""expense_type_id"": number or null"
            : "";
        string formOfPaymentIdField = formOfPayments != null && formOfPayments.Count > 0
            ? @",
  ""form_of_payment_id"": number or null"
            : "";

        return $@"You are an expert invoice/receipt OCR analyzer. Extract data for {locale}.
Return STRICT JSON only — no markdown, no explanation.

🔴🔴🔴 ABSOLUTE RULES — VIOLATING THESE IS A CRITICAL FAILURE:

STEP 1 — DETECT first: identify the receipt's LANGUAGE and COUNTRY from the script/text/currency symbol BEFORE extracting fields. This determines which label dictionary to use.

STEP 2 — Use the matching language dictionary below to find each MANDATORY field.

📋 MULTI-LANGUAGE LABEL DICTIONARY (use the row matching the receipt's language):

═══ TOTAL / AMOUNT PAID labels (the FINAL amount the customer paid, including tax) ═══
  English:    TOTAL, GRAND TOTAL, AMOUNT DUE, AMOUNT PAID, BALANCE DUE, TOTAL DUE, SALE AMOUNT, NET AMOUNT
  Hebrew:     סה""כ לתשלום, סה""כ, לתשלום, סך הכל, סכום, סך לתשלום, חשבון
  Spanish:    TOTAL, IMPORTE TOTAL, TOTAL A PAGAR, MONTO TOTAL, IMPORTE
  Filipino/Tagalog: KABUUAN, TOTAL, BAYARAN, BAYAD, KABUUANG HALAGA
  Korean:     합계, 총액, 결제금액, 청구금액, 총합계, 받을금액
  Japanese:   合計, 総計, 請求金額, お支払い, お支払金額, ご請求額
  Thai:       รวมทั้งสิ้น, ยอดรวม, จำนวนเงินรวม, ยอดสุทธิ
  French:     TOTAL, MONTANT TOTAL, NET À PAYER, TOTAL TTC, À PAYER
  German:     GESAMT, GESAMTBETRAG, SUMME, ZU ZAHLEN, ENDBETRAG
  Italian:    TOTALE, IMPORTO TOTALE, TOTALE DA PAGARE, NETTO A PAGARE
  Portuguese: TOTAL, VALOR TOTAL, TOTAL A PAGAR, MONTANTE
  Arabic:     المجموع, الإجمالي, المبلغ المستحق, الإجمالي الكلي
  Russian:    ИТОГО, ВСЕГО, СУММА К ОПЛАТЕ, ОБЩАЯ СУММА
  Chinese:    总计, 合计, 应付金额, 总额, 实付金额

═══ TAX / VAT labels ═══
  English:    VAT, TAX, GST, SALES TAX, HST
  Hebrew:     מע""מ, מס
  Spanish:    IVA, IMPUESTO
  Filipino:   VAT, BUWIS
  Korean:     부가세, 세금, 부가가치세
  Japanese:   消費税, 税
  Thai:       ภาษี, ภาษีมูลค่าเพิ่ม, VAT
  French:     TVA, TAXE
  German:     MWST, MEHRWERTSTEUER, STEUER, USt
  Italian:    IVA, IMPOSTA
  Portuguese: IVA, IMPOSTO
  Arabic:     ضريبة, ضريبة القيمة المضافة
  Russian:    НДС, НАЛОГ
  Chinese:    税, 增值税

═══ INVOICE / RECEIPT NUMBER labels ═══
  English:    Invoice #, Receipt #, Order #, Transaction, Ref #, Doc #, Bill No, OR No, SI No
  Hebrew:     מספר חשבונית, מס' חשבונית, חשבונית מס, מספר קבלה, מס' קבלה, הזמנה, מס. עסקה
  Spanish:    Factura N°, Recibo N°, Núm. de factura, Folio
  Filipino:   Resibo Numero, OR No, SI No, Bilang ng resibo
  Korean:     영수증번호, 거래번호, 청구번호
  Japanese:   領収書番号, 請求書番号, 取引番号, 伝票番号
  Thai:       เลขที่ใบเสร็จ, เลขที่ใบกำกับภาษี, เลขที่
  French:     N° facture, N° reçu, Numéro de facture
  German:     Rechnungsnr, Belegnr, Quittungsnr, Rechnungsnummer
  Italian:    N° fattura, N° ricevuta, Numero fattura
  Portuguese: Nº fatura, Nº recibo, Número da fatura
  Arabic:     رقم الفاتورة, رقم الإيصال
  Russian:    Номер счета, Номер чека, Чек №
  Chinese:    发票号, 收据号, 发票号码, 单据号

═══ DATE labels & formats by country ═══
  Date labels: DATE, תאריך, FECHA, PETSA, 날짜, 日付, วันที่, DATE, DATUM, DATA, التاريخ, ДАТА, 日期
  Formats:
    - Israel/EU/UK/Philippines/most-of-world: DD/MM/YYYY or DD/MM/YY
    - USA: MM/DD/YYYY or MM/DD/YY
    - Korea/Japan/China/Taiwan: YYYY/MM/DD or YY/MM/DD (East Asian convention)
    - ISO (anywhere): YYYY-MM-DD

═══ CURRENCY DETECTION ═══
  Symbols: ₪=ILS, $=USD (or PHP/AUD/CAD by context), €=EUR, £=GBP, ¥=JPY/CNY, ₩=KRW, ₱=PHP, ฿=THB, ₹=INR, ₽=RUB
  Country fallback: IL=ILS, US=USD, PH=PHP, GB=GBP, JP=JPY, KR=KRW, TH=THB, EU=EUR, FR=EUR, DE=EUR, IT=EUR, ES=EUR, PT=EUR, CN=CNY, RU=RUB, IN=INR, AU=AUD, CA=CAD

🔴 MANDATORY FIELDS — NEVER return 0 or null unless receipt is genuinely blank:
   1. payment.amount_paid → MUST be a NUMBER > 0. Use the largest amount near a TOTAL label from the dictionary above.
      • Pick the FINAL/LARGEST total (after tax), not subtotal.
      • If multiple totals visible, the FINAL TOTAL is at the bottom of the receipt.
      • Strip currency symbols, commas, decimals → return as plain number (e.g. 13328.00 not ""$13,328.00"").

   2. invoice_date → REQUIRED. Find ANY date pattern. Convert to YYYY-MM-DD using the country/format rules above.

   3. invoice_number → REQUIRED. Find using the language dictionary above.
      • DO NOT confuse with: TIN, ח.פ., ע.מ., RUT, NIF, tax-ID, vendor-ID, phone numbers, dates, postal codes.
      • If multiple candidates, prefer the one explicitly labeled (Invoice/Receipt/Order/Ref).

   4. currency → ALWAYS detect using the currency table above. NEVER return null.

GOLDEN RULE: Inspect EVERY visible number on the image before giving up. NEVER return 0/null on a non-blank receipt.

OUTPUT also include these top-level fields for diagnostics:
   • detected_language: ""he|en|es|fil|ko|ja|th|fr|de|it|pt|ar|ru|zh|other""
   • detected_country: ISO-2 code (best guess from receipt content)


DOCUMENT TYPES to recognize:
- Standard invoice/receipt, Payment terminal (Maya, GCash, BPI), Digital receipt, Handwritten receipt

EXTRACTION RULES:
1. VENDOR: Largest text at TOP. For terminals: business name, NOT terminal brand.
2. AMOUNT: Look for ""SALE AMOUNT"", ""TOTAL"", ""סה""כ"". Return as NUMBER (13328.00 not ""13,328.00"").
3. TAX: Must be SMALLER than subtotal. If tax > subtotal, they are SWAPPED. If no tax visible, use 0.
4. DATE: Transaction date only (not permit/accreditation). Output as YYYY-MM-DD.
   ⚠️ CRITICAL — DATE FORMAT BY COUNTRY/CURRENCY (the receipt's printed format depends on origin):
   - KOREA (KRW, ""THE PLAZA"", Seoul, +82, Hangul): format is **YY/MM/DD** (East Asian convention).
     Example: ""26/02/13"" = **2026-02-13** (Feb 13, 2026), NOT 2013-02-26.
   - JAPAN (JPY, +81), CHINA (CNY, +86), TAIWAN (TWD): also **YY/MM/DD** or **YYYY/MM/DD**.
   - USA (USD, English receipts from US merchants): **MM/DD/YY** or **MM/DD/YYYY**.
     Example: ""02/13/26"" = 2026-02-13.
   - ISRAEL (ILS, ₪, Hebrew), EUROPE (EUR), PHILIPPINES (PHP), UK (GBP), most of world: **DD/MM/YY** or **DD/MM/YYYY**.
     Example: ""13/02/26"" = 2026-02-13.
   - If the year value is ≤ 31 and appears FIRST with two digits, and currency is KRW/JPY/CNY/TWD → it's the YEAR (YY/MM/DD).
   - If a 2-digit year is < 50 → assume 20YY. If ≥ 50 → assume 19YY.
   - Sanity check: the resulting date should be in the past or near-present, NOT 10+ years old unless clearly historical.
   - When uncertain, prefer the format matching the receipt's currency/country over the default DD/MM/YY.
5. INVOICE NUMBER: Document/transaction number, NOT TIN/tax ID.
6. CATEGORY: Must be one of: business_meal, vehicle, entertainment, hotel, internet, parking, meal, taxi, other.

CURRENCY: ₱=PHP, ₪=ILS, ฿=THB, $=USD, ₩=KRW, ¥=JPY/CNY (unless context says otherwise).
{learnedPatternsSection}{expenseTypesSection}{formOfPaymentsSection}
OUTPUT FORMAT:
{{
  ""document_type"": ""string"",
  ""invoice_number"": ""string or null"",
  ""invoice_date"": ""YYYY-MM-DD or null"",
  ""currency"": ""string"",
  ""expense_type"": ""business_meal|vehicle|entertainment|hotel|internet|parking|other|meal|taxi""{expenseTypeIdField},
  ""merchant"": {{ ""name"": ""string"", ""tin"": ""string or null"", ""address"": ""string or null"", ""city"": ""string or null"" }},
  ""amounts"": {{ ""vatable_sales_amount"": number, ""non_vatable_sales_amount"": 0, ""service_charge_amount"": 0, ""tax_amount"": number }},
  ""payment"": {{ ""method"": ""string or null"", ""amount_paid"": number, ""form_of_payment"": ""credit|cash|bank""{formOfPaymentIdField}, ""card_last4"": ""string or null"", ""card_type"": ""visa|mastercard|amex|diners|isracart|other or null"" }},
  ""item_count"": number
}}

PAYMENT FORM RULES:
- form_of_payment: Identify if payment was by credit card, cash, or bank transfer.
  Look for: ""אשראי"", ""credit"", ""כרטיס"", ""card"", ""visa"", ""mastercard"", ""מזומן"", ""cash"", ""העברה"", ""bank"", ""EMV"", ""Contactless"".
  If EMV, Contactless, chip, swipe, or card number visible → ""credit"".
  If no payment method found → default to ""cash"".
- card_last4: If credit card, extract last 4 digits (look for ****1234, XXXX-1234, etc.)
- card_type: Identify card network from text or BIN:
  Visa (starts with 4), Mastercard (starts with 5), Amex (starts with 3), Isracard/Isracart, Diners, etc.";
    }

    /// <summary>
    /// Parses JSON from an AI response. Fully production-hardened:
    /// strips markdown, extracts the JSON block, repairs all truncation edge cases,
    /// and as an absolute last resort reconstructs known fields via regex.
    /// Never returns invalid JSON — always returns a parseable JsonElement.
    /// </summary>
    public static JsonElement ParseJsonFromAiResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("AI response is empty");

        // ── Step 1: strip markdown fences and control chars ──
        var cleaned = Regex.Replace(raw, @"```json\s*\n?", "");
        cleaned = Regex.Replace(cleaned, @"```\s*\n?", "").Trim();
        cleaned = Regex.Replace(cleaned, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

        // ── Step 2: extract the JSON block via brace counting ──
        var startIdx = cleaned.IndexOf('{');
        if (startIdx < 0)
        {
            Console.Error.WriteLine($"[OCI-PARSE] No JSON object found. First 200: {cleaned[..Math.Min(200, cleaned.Length)]}");
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        int depth = 0, endIdx = -1;
        bool inString = false, escapeNext = false;
        for (int i = startIdx; i < cleaned.Length; i++)
        {
            char ch = cleaned[i];
            if (escapeNext) { escapeNext = false; continue; }
            if (ch == '\\') { escapeNext = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
        }

        if (endIdx >= 0)
        {
            cleaned = cleaned[startIdx..(endIdx + 1)];
        }
        else
        {
            // Truncated: close open string, strip trailing punctuation, close open braces/brackets
            Console.WriteLine($"[OCI-PARSE] Truncated JSON detected (depth={depth}), repairing...");
            cleaned = cleaned[startIdx..];
            if (inString) cleaned += "\"";
            cleaned = Regex.Replace(cleaned, @"[,:\s]+$", "");
            cleaned = CloseOpenStructures(cleaned);
        }

        // ── Step 3: multi-pass repair until fixpoint ──
        cleaned = ApplyRepairPassesUntilFixpoint(cleaned);

        // ── Step 4: try parse ──
        if (TryParseJson(cleaned, out var element))
            return element;

        // ── Step 5: aggressive fallback — trim to last complete key-value pair ──
        Console.Error.WriteLine($"[OCI-PARSE] Multi-pass repair failed, trying aggressive trim. Content: {cleaned[..Math.Min(400, cleaned.Length)]}");
        var trimmed = TrimToLastCompleteProperty(cleaned);
        if (trimmed != null && TryParseJson(trimmed, out element))
        {
            Console.WriteLine("[OCI-PARSE] Recovered via property-trim fallback");
            return element;
        }

        // ── Step 6: absolute last resort — regex field extraction ──
        Console.Error.WriteLine("[OCI-PARSE] All repair attempts failed, extracting known fields via regex");
        var extracted = ExtractKnownFieldsAsJson(raw);
        if (TryParseJson(extracted, out element))
        {
            Console.WriteLine("[OCI-PARSE] Recovered via regex field-extraction fallback");
            return element;
        }

        // Should never reach here — ExtractKnownFieldsAsJson always returns valid JSON
        Console.Error.WriteLine("[OCI-PARSE] Complete failure — returning empty JSON to allow retry logic to handle");
        return JsonDocument.Parse("{}").RootElement.Clone();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TryParseJson(string json, out JsonElement element)
    {
        try
        {
            element = JsonDocument.Parse(json).RootElement.Clone();
            return true;
        }
        catch
        {
            element = default;
            return false;
        }
    }

    /// <summary>
    /// Counts and appends the closing braces/brackets needed to balance the JSON string.
    /// </summary>
    private static string CloseOpenStructures(string json)
    {
        int openBraces = 0, openBrackets = 0;
        bool inStr = false, esc = false;
        foreach (char c in json)
        {
            if (esc) { esc = false; continue; }
            if (c == '\\') { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '{') openBraces++;
            else if (c == '}') openBraces--;
            else if (c == '[') openBrackets++;
            else if (c == ']') openBrackets--;
        }
        for (int i = 0; i < openBrackets; i++) json += "]";
        for (int i = 0; i < openBraces; i++) json += "}";
        Console.WriteLine($"[OCI-PARSE] Closed {openBrackets} bracket(s) and {openBraces} brace(s)");
        return json;
    }

    /// <summary>
    /// Applies structural repair rules repeatedly until the JSON stops changing (fixpoint).
    /// Handles all truncation patterns: trailing commas, dangling keys, key-colon-no-value.
    /// </summary>
    private static string ApplyRepairPassesUntilFixpoint(string json)
    {
        const int maxPasses = 8;
        string prev;
        int pass = 0;
        do
        {
            prev = json;

            // Remove trailing comma before any closing token.
            // Known limitation: not string-aware — could in theory corrupt a string value that
            // literally ends with ,} (e.g. "addr": "St. 10,}"). Acceptable trade-off: this code
            // only runs on already-invalid JSON, and real receipt data virtually never contains },].
            json = Regex.Replace(json, @",\s*([}\]])", "$1");

            // Remove key-with-colon-but-no-value:
            //   ,"key": }  →  }      (key with comma before it)
            //   {"key": }  →  {}     (key is first/only entry)
            json = Regex.Replace(json, @",\s*""(?:[^""\\]|\\.)*""\s*:\s*(?=[}\]])", "");
            json = Regex.Replace(json, @"(?<=\{)\s*""(?:[^""\\]|\\.)*""\s*:\s*(?=[}\]])", "");

            // Remove dangling key (no colon, no value):
            //   ,"key"}  →  }        (key with comma before it)
            //   {"key"}  →  {}       (key is first/only entry)
            json = Regex.Replace(json, @",\s*""(?:[^""\\]|\\.)*""\s*(?=[}\]])", "");
            json = Regex.Replace(json, @"(?<=\{)\s*""(?:[^""\\]|\\.)*""\s*(?=[}\]])", "");

            pass++;
        } while (json != prev && pass < maxPasses);

        if (pass > 1)
            Console.WriteLine($"[OCI-PARSE] Repair completed in {pass} pass(es)");

        return json;
    }

    /// <summary>
    /// Strips everything after the last structurally-complete key-value pair, then re-closes.
    /// Handles cases where the broken token is deep inside a nested object.
    /// </summary>
    private static string? TrimToLastCompleteProperty(string json)
    {
        // Walk backwards from the end looking for the last comma that is outside all strings
        // (i.e. a property separator at any depth), then truncate there and re-close.
        var chars = json.ToCharArray();
        bool inStr = false; bool esc = false;
        int lastOuterComma = -1;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (esc) { esc = false; continue; }
            if (c == '\\') { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (!inStr && c == ',') lastOuterComma = i;
        }
        if (lastOuterComma < 0) return null;

        var trimmed = json[..lastOuterComma];
        trimmed = CloseOpenStructures(trimmed);
        return trimmed;
    }

    /// <summary>
    /// Absolute last resort: extracts known OCR field values via regex from the raw AI text
    /// and reassembles them into a minimal valid JSON. Always returns valid JSON.
    /// </summary>
    private static string ExtractKnownFieldsAsJson(string raw)
    {
        static string? StrField(string text, string name)
        {
            var m = Regex.Match(text, $@"""{name}""\s*:\s*""((?:[^""\\]|\\.)*)""");
            return m.Success ? m.Groups[1].Value : null;
        }
        static string? NumField(string text, string name)
        {
            var m = Regex.Match(text, $@"""{name}""\s*:\s*(\d+(?:[.,]\d+)?)");
            return m.Success ? m.Groups[1].Value.Replace(",", ".") : null;
        }

        var parts = new List<string>();

        foreach (var f in new[] { "document_type", "invoice_number", "invoice_date", "currency",
                                   "expense_type", "detected_language", "detected_country" })
        {
            var v = StrField(raw, f);
            if (v != null) parts.Add($@"""{f}"": ""{EscapeJsonString(v)}""");
        }

        // merchant block
        var mName = StrField(raw, "name");
        var mTin  = StrField(raw, "tin");
        var mAddr = StrField(raw, "address");
        var mCity = StrField(raw, "city");
        if (mName != null || mTin != null || mAddr != null || mCity != null)
        {
            var mp = new List<string>();
            if (mName != null) mp.Add($@"""name"": ""{EscapeJsonString(mName)}""");
            if (mTin  != null) mp.Add($@"""tin"": ""{EscapeJsonString(mTin)}""");
            if (mAddr != null) mp.Add($@"""address"": ""{EscapeJsonString(mAddr)}""");
            if (mCity != null) mp.Add($@"""city"": ""{EscapeJsonString(mCity)}""");
            parts.Add($@"""merchant"": {{{string.Join(", ", mp)}}}");
        }

        // amounts block
        var vatAmt  = NumField(raw, "vatable_sales_amount");
        var taxAmt  = NumField(raw, "tax_amount");
        if (vatAmt != null || taxAmt != null)
        {
            var ap = new List<string>
            {
                $@"""vatable_sales_amount"": {vatAmt ?? "0"}",
                $@"""non_vatable_sales_amount"": 0",
                $@"""service_charge_amount"": 0",
                $@"""tax_amount"": {taxAmt ?? "0"}"
            };
            parts.Add($@"""amounts"": {{{string.Join(", ", ap)}}}");
        }

        // payment block
        var amtPaid = NumField(raw, "amount_paid");
        var fop     = StrField(raw, "form_of_payment");
        var method  = StrField(raw, "method");
        if (amtPaid != null || fop != null || method != null)
        {
            var pp = new List<string>();
            if (method  != null) pp.Add($@"""method"": ""{EscapeJsonString(method)}""");
            if (amtPaid != null) pp.Add($@"""amount_paid"": {amtPaid}");
            pp.Add($@"""form_of_payment"": ""{(fop ?? "cash")}""");
            parts.Add($@"""payment"": {{{string.Join(", ", pp)}}}");
        }

        var itemCount = NumField(raw, "item_count");
        if (itemCount != null) parts.Add($@"""item_count"": {itemCount}");

        return parts.Count > 0
            ? $"{{{string.Join(", ", parts)}}}"
            : "{}";
    }

    private static string EscapeJsonString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>
    /// Decode unicode escape sequences
    /// </summary>
    public static string DecodeUnicodeEscapes(string str)
    {
        return Regex.Replace(str, @"\\u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    // ── Image integrity inspection ─────────────────────────────────────────

    /// <summary>
    /// Inspects the image before sending to OCI:
    /// - Detects actual MIME type from magic bytes (ignores declared type if wrong)
    /// - Validates image header
    /// - Computes SHA256 hash to detect corruption in transit
    /// - Always saves a copy to logs/ocr-images/ (override: OCR_DEBUG_IMAGES_DIR env var)
    /// - Verifies written file size matches decoded byte count
    /// - Async cleanup: keeps last 7 days / max 500 files
    /// </summary>
    public ImageInspectionResult InspectImage(string imageBase64)
    {
        // ── Extract declared MIME type and raw base64 from data URI ──
        string declaredMime = "image/jpeg";
        string base64Part = imageBase64;

        if (imageBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var semicolonIdx = imageBase64.IndexOf(';');
            if (semicolonIdx > 5)
                declaredMime = imageBase64.Substring(5, semicolonIdx - 5).Trim().ToLowerInvariant();
            var commaIdx = imageBase64.IndexOf(',');
            if (commaIdx >= 0)
                base64Part = imageBase64[(commaIdx + 1)..];
        }

        // ── Decode base64 ──
        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(base64Part.Trim());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OCR-IMAGE] ❌ Base64 decode failed: {ex.Message}");
            return new ImageInspectionResult(declaredMime, 0, "", null, false,
                $"Base64 decode failed: {ex.Message}");
        }

        if (imageBytes.Length < 8)
        {
            Console.Error.WriteLine($"[OCR-IMAGE] ❌ Image too small: {imageBytes.Length} bytes");
            return new ImageInspectionResult(declaredMime, imageBytes.Length, "", null, false,
                $"Image too small ({imageBytes.Length} bytes)");
        }

        // ── Auto-detect actual format from magic bytes ──
        string actualMime = DetectMimeFromBytes(imageBytes);
        bool mimeMismatch = actualMime != declaredMime;
        if (mimeMismatch)
        {
            Console.Error.WriteLine(
                $"[OCR-IMAGE] ⚠️ MIME TYPE MISMATCH: declared={declaredMime}, actual={actualMime} — using actual for OCI");
        }

        // ── Validate known header ──
        string? invalidReason = null;
        if (actualMime == "unknown")
        {
            var headerHex = string.Join(" ", imageBytes[..Math.Min(8, imageBytes.Length)].Select(b => b.ToString("X2")));
            invalidReason = $"Unrecognized image format. Header bytes: {headerHex}";
            Console.Error.WriteLine($"[OCR-IMAGE] ❌ {invalidReason}");
        }
        else if (actualMime == "application/pdf")
        {
            Console.WriteLine($"[OCR-IMAGE] 📄 PDF detected ({imageBytes.Length:N0} bytes) — will send directly to Gemini (supports PDF input)");
        }

        // ── SHA256 hash ──
        var hashBytes = SHA256.HashData(imageBytes);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // ── Save to debug directory (opt-in: set OCR_SAVE_DEBUG_IMAGES=true to enable) ──
        string? debugFilePath = null;
        if (_saveDebugImages)
        {
            var debugDir = Environment.GetEnvironmentVariable("OCR_DEBUG_IMAGES_DIR");
            if (string.IsNullOrWhiteSpace(debugDir))
                debugDir = Path.Combine(Directory.GetCurrentDirectory(), "logs", "ocr-images");

            try
            {
                Directory.CreateDirectory(debugDir);

                var ext = actualMime switch
                {
                    "image/jpeg"      => ".jpg",
                    "image/png"       => ".png",
                    "image/webp"      => ".webp",
                    "image/gif"       => ".gif",
                    "image/bmp"       => ".bmp",
                    "application/pdf" => ".pdf",
                    "unknown"         => ".bin",
                    _                 => ".jpg"
                };
                var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{hash[..8]}{ext}";
                debugFilePath = Path.Combine(debugDir, fileName);

                File.WriteAllBytes(debugFilePath, imageBytes);

                var writtenSize = new FileInfo(debugFilePath).Length;
                if (writtenSize != imageBytes.Length)
                {
                    Console.Error.WriteLine(
                        $"[OCR-IMAGE] ❌ Write verification failed: wrote {imageBytes.Length} bytes but file is {writtenSize} bytes — {debugFilePath}");
                    debugFilePath = null;
                }
                else
                {
                    Console.WriteLine(
                        $"[OCR-IMAGE] 💾 Saved: {debugFilePath} ({writtenSize:N0} bytes, sha256={hash[..16]}...)");
                }

                _ = Task.Run(() => CleanupDebugImages(debugDir));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OCR-IMAGE] ❌ Could not save debug image: {ex.GetType().Name}: {ex.Message}");
                debugFilePath = null;
            }
        }

        Console.WriteLine(
            $"[OCR-IMAGE] mime={actualMime}{(mimeMismatch ? $" (was {declaredMime})" : "")} | " +
            $"size={imageBytes.Length:N0}B | sha256={hash[..16]}... | valid={invalidReason == null}");

        return new ImageInspectionResult(actualMime, imageBytes.Length, hash, debugFilePath,
            invalidReason == null, invalidReason);
    }

    private static void CleanupDebugImages(string dir)
    {
        try
        {
            var allFiles = Directory.GetFiles(dir)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-7);
            const int maxFiles = 500;

            // Delete files older than 7 days
            var toDelete = allFiles.Where(f => f.LastWriteTimeUtc < cutoff).ToList();

            // If still over limit after age-based cleanup, remove oldest first
            var remaining = allFiles.Count - toDelete.Count;
            if (remaining > maxFiles)
                toDelete.AddRange(allFiles.Except(toDelete).Take(remaining - maxFiles));

            foreach (var f in toDelete)
            {
                try { f.Delete(); } catch { /* best-effort */ }
            }

            if (toDelete.Count > 0)
                Console.WriteLine($"[OCR-IMAGE] Cleaned up {toDelete.Count} old debug image(s) from {dir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR-IMAGE] Cleanup warning: {ex.Message}");
        }
    }

    private static string DetectMimeFromBytes(byte[] b)
    {
        if (b.Length < 4) return "unknown";
        // JPEG: FF D8 FF
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        // WebP: RIFF????WEBP
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        // GIF87a / GIF89a
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        // BMP: BM
        if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        // PDF: %PDF
        if (b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "application/pdf";
        return "unknown";
    }

    // ── PDF text extraction: returns text if PDF has embedded text, null if scanned/empty ──
    // Minimum 80 chars of meaningful text = digital PDF; below that = scanned image PDF.
    // NoInlining is required so that the JIT loads UglyToad.PdfPig only when this method
    // is first called — allowing the caller to catch FileNotFoundException if the DLL is missing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? TryExtractPdfText(string base64Part)
    {
        const int MinTextLength = 80;
        try
        {
            var pdfBytes = Convert.FromBase64String(base64Part);
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var sb = new System.Text.StringBuilder();
            foreach (var page in doc.GetPages())
            {
                foreach (var word in page.GetWords())
                    sb.Append(word.Text).Append(' ');
                sb.AppendLine();
                // Stop after 2 pages — invoices rarely exceed that
                if (page.Number >= 2) break;
            }
            var text = sb.ToString().Trim();
            return text.Length >= MinTextLength ? text : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] PDF text extraction failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    // ── Image resizing: shrink to max 1600px on any dimension before sending to OCI ──
    // Reduces payload size and speeds up API response. Skipped for PDFs.
    // NoInlining defers assembly load to call time so caller can catch FileNotFoundException.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResizeIfTooLarge(string base64Part, out string? resizedMime)
    {
        resizedMime = null;
        const int MaxDimension = 1600;
        try
        {
            var imageBytes = Convert.FromBase64String(base64Part);
            using var img = SixLabors.ImageSharp.Image.Load(imageBytes);

            if (img.Width <= MaxDimension && img.Height <= MaxDimension)
                return base64Part; // already small enough

            double scale = Math.Min((double)MaxDimension / img.Width, (double)MaxDimension / img.Height);
            int newWidth  = (int)Math.Round(img.Width  * scale);
            int newHeight = (int)Math.Round(img.Height * scale);

            img.Mutate(x => x.Resize(newWidth, newHeight));

            using var ms = new System.IO.MemoryStream();
            img.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
            var resized = Convert.ToBase64String(ms.ToArray());

            Console.WriteLine($"[OCR] Image resized {img.Width}x{img.Height} → {newWidth}x{newHeight} | " +
                              $"before={base64Part.Length / 1024}KB base64, after={resized.Length / 1024}KB base64");

            resizedMime = "image/jpeg";
            return resized;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Resize skipped (non-fatal): {ex.Message}");
            return base64Part;
        }
    }

    // Lightweight: decode only the first 16 bytes of base64 to detect MIME type.
    // Used in CallGeminiFlashAsync to avoid a full decode when InspectImage already ran.
    private static string DetectMimeFromBase64Prefix(string base64)
    {
        try
        {
            // Base64 encodes 3 bytes per 4 chars; 16 bytes needs ~24 chars
            var prefix = base64.Length > 24 ? base64[..24] : base64;
            // Pad to valid base64 length
            while (prefix.Length % 4 != 0) prefix += "=";
            var bytes = Convert.FromBase64String(prefix);
            return DetectMimeFromBytes(bytes);
        }
        catch
        {
            return "image/jpeg"; // safe fallback
        }
    }
}

/// <summary>
/// Result of image inspection before sending to OCI.
/// </summary>
public record ImageInspectionResult(
    string MimeType,        // actual detected MIME type (corrected if declared was wrong)
    int DecodedBytes,       // decoded image size in bytes
    string Sha256Hash,      // hex SHA256 of the decoded bytes
    string? DebugFilePath,  // path to saved debug file (null if debug dir not configured)
    bool IsValid,           // false if format is unrecognized
    string? InvalidReason   // human-readable reason when IsValid=false
);

/// <summary>
/// Exception carrying OCI HTTP status + raw response body for diagnostics.
/// </summary>
public class OciApiException : Exception
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public OciApiException(string message, int statusCode, string? responseBody) : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
