using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Chat service — session management, RAG, intent detection, corrections
/// </summary>
public class ChatService
{
    private readonly TripExDbContext _db;
    private readonly OracleAiService _oracle;
    private readonly InvoiceService _invoiceService;
    private readonly GeolocationService _geoService;

    // Intent → Actions mapping
    private static readonly Dictionary<string, (List<string> Actions, string? RedirectPage)> ActionMapping = new()
    {
        ["help"]             = (new(), null),
        ["scan"]             = (new() { "Camera" }, null),
        ["expense"]          = (new(), null),
        ["expense_complete"] = (new(), null),
        ["bi"]               = (new() { "DisplayResults" }, null),
        ["online"]           = (new(), null),
        ["online_complete"]  = (new(), null),
        ["general"]          = (new(), null),
    };

    public ChatService(
        TripExDbContext db,
        OracleAiService oracle,
        InvoiceService invoiceService,
        GeolocationService geoService)
    {
        _db = db;
        _oracle = oracle;
        _invoiceService = invoiceService;
        _geoService = geoService;
    }

    public async Task<ChatResponse> ProcessAsync(ChatRequest request, Guid userId, string? ipAddress)
    {
        // ── Session handling ──
        Guid sessionId;
        if (!string.IsNullOrEmpty(request.SessionToken) && Guid.TryParse(request.SessionToken, out var existingId))
        {
            sessionId = existingId;
        }
        else
        {
            var session = new ChatSession { UserId = userId, Source = request.Source };
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync();
            sessionId = session.Id;
        }

        // ── Image flow ──
        if (request.Type == "image")
        {
            return await HandleImageAsync(request, sessionId, userId);
        }

        // ── Empty text ──
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new ChatResponse
            {
                Text = "Hello 👋 I'm TripEX AI. How can I assist you today?",
                SessionId = sessionId.ToString()
            };
        }

        // ── Save user message ──
        _db.ChatMessages.Add(new ChatMessage
        {
            SessionId = sessionId,
            Role = "user",
            Content = request.Text
        });
        await _db.SaveChangesAsync();

        // ── Load history ──
        var history = await _db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .Take(30)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync();

        // ── Load config ──
        var config = await _db.ChatbotConfigs
            .Where(c => c.IsActive)
            .FirstOrDefaultAsync();

        var temperature = (double)(config?.Temperature ?? 0.3m);
        var maxTokens = config?.MaxTokens ?? 2048;

        // ── Geolocation ──
        var geo = await _geoService.GetLocationAsync(ipAddress);

        // ── RAG: Search knowledge base ──
        var knowledgeContext = await SearchKnowledgeBase(request.Text);

        // ── Build system prompt ──
        var systemPrompt = BuildSystemPrompt(
            request, geo, knowledgeContext);

        // ── Build messages ──
        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };
        messages.AddRange(history.Select(h => new OracleMessage { Role = h.Role, Content = h.Content }));

        // ── Call Oracle AI ──
        var rawContent = await _oracle.ChatAsync(messages, maxTokens, temperature);

        // ── Parse response ──
        var (intent, responseText) = ParseAiResponse(rawContent);

        // ── Map intent to actions ──
        var mapping = ActionMapping.GetValueOrDefault(intent, ActionMapping["general"]);

        // ── Save corrections (learning from OCR corrections) ──
        await TrySaveCorrections(intent, sessionId, userId);

        // ── Save assistant message ──
        _db.ChatMessages.Add(new ChatMessage
        {
            SessionId = sessionId,
            Role = "assistant",
            Content = responseText,
            Intent = intent,
            Metadata = JsonSerializer.Serialize(new { actions = mapping.Actions, redirectPage = mapping.RedirectPage ?? "" })
        });

        // ── Log ──
        _db.ChatbotLogs.Add(new ChatbotLog
        {
            SessionId = sessionId,
            UserId = userId,
            EventType = "intent_detected",
            Details = JsonSerializer.Serialize(new
            {
                intent,
                actions = mapping.Actions,
                redirectPage = mapping.RedirectPage ?? "",
                message_preview = request.Text.Length > 100 ? request.Text[..100] : request.Text,
                source = request.Source
            })
        });

        await _db.SaveChangesAsync();

        return new ChatResponse
        {
            Text = responseText,
            Actions = mapping.Actions,
            RedirectPage = mapping.RedirectPage ?? "",
            SessionId = sessionId.ToString()
        };
    }

    private async Task<ChatResponse> HandleImageAsync(ChatRequest request, Guid sessionId, Guid userId)
    {
        // Log OCR request
        _db.ChatbotLogs.Add(new ChatbotLog
        {
            SessionId = sessionId,
            UserId = userId,
            EventType = "ocr_request",
            Details = JsonSerializer.Serialize(new { source = request.Source })
        });

        // Extract country from request scope or default
        var country = request.Scope; // client can pass country in Scope field
        var result = await _invoiceService.AnalyzeAsync(request.Text, null, country);

        if (!result.Success)
        {
            await _db.SaveChangesAsync();
            return new ChatResponse
            {
                Text = "Failed to scan receipt. Please try again.",
                SessionId = sessionId.ToString()
            };
        }

        // Build summary from AlgoText-compatible fields
        var f = result.Fields;
        var lines = new List<string> { "✅ Invoice scanned successfully! Here are the details:" };
        if (f?.Type != null) lines.Add($"📄 Type: {f.Type.Replace("_", " ")}");
        if (f?.MerchantName != null) lines.Add($"🏪 Merchant: {f.MerchantName}");
        if (f?.MerchantTin != null) lines.Add($"🆔 TIN: {f.MerchantTin}");
        if (f?.MerchantAddress != null) lines.Add($"📍 Address: {f.MerchantAddress}");
        if (f?.MerchantCity != null) lines.Add($"🌆 City: {f.MerchantCity}");
        if (f?.InvoiceNumber != null) lines.Add($"🔢 Invoice #: {f.InvoiceNumber}");
        if (f?.InvoiceDate != null) lines.Add($"📅 Date: {f.InvoiceDate}");
        var cur = f?.Currency ?? "";
        if (f?.Total != null) lines.Add($"💵 Total: {f.Total} {cur}");
        if (f?.TotalVAT != null) lines.Add($"🧾 VAT/Tax: {f.TotalVAT} {cur}");
        if (f?.PaymentMethod != null) lines.Add($"💳 Payment: {f.PaymentMethod}");
        // Form of payment
        if (f?.FormOfPayment == "credit")
        {
            var creditInfo = "💳 Form of Payment: Credit Card";
            if (!string.IsNullOrEmpty(f.CardType)) creditInfo += $" ({CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f.CardType)})";
            if (!string.IsNullOrEmpty(f.CardLast4)) creditInfo += $" ****{f.CardLast4}";
            lines.Add(creditInfo);
        }
        else if (f?.FormOfPayment == "bank")
            lines.Add("🏦 Form of Payment: Bank Transfer");
        else
            lines.Add("💵 Form of Payment: Cash");
        if (f?.AmountPaid != null) lines.Add($"💰 Paid: {f.AmountPaid} {cur}");
        lines.Add("\nIs the data correct? If something is wrong, let me know and I'll update it.");

        var summary = string.Join("\n", lines);

        // Save messages
        _db.ChatMessages.Add(new ChatMessage { SessionId = sessionId, Role = "user", Content = "[User scanned an invoice/receipt]" });
        _db.ChatMessages.Add(new ChatMessage
        {
            SessionId = sessionId,
            Role = "assistant",
            Content = summary,
            Intent = "scan",
            Metadata = JsonSerializer.Serialize(new { actions = Array.Empty<string>(), scanned_fields = result.Fields })
        });
        await _db.SaveChangesAsync();

        // Convert Fields to dictionary for response
        var dataDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(result.Fields)) ?? new();

        return new ChatResponse
        {
            Text = summary,
            Data = dataDict,
            SessionId = sessionId.ToString()
        };
    }

    private async Task<string> SearchKnowledgeBase(string queryText)
    {
        // Call the search_knowledge database function via raw SQL.
        // Returns file_name + content + tagging (domain / doc_type / description)
        // so the agent knows WHEN each snippet is relevant.
        var chunks = new List<KbChunk>();

        var connection = _db.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync();

            const string sql = "SELECT file_name, content, domain, doc_type, description FROM dbo.search_knowledge(@query, @max)";

            // Full query search
            await RunKnowledgeQuery(connection, sql, queryText, 5, chunks);

            // Also search individual words for better Hebrew matching
            var words = queryText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2).Take(3);

            foreach (var word in words)
                await RunKnowledgeQuery(connection, sql, word, 3, chunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RAG search error: {ex.Message}");
        }
        finally
        {
            if (connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }

        if (chunks.Count == 0) return "";

        var topChunks = chunks.Take(5);
        return "\n\n## Knowledge Base Context (use this to answer the user):\n" +
               "Each snippet is tagged with its domain/type and an optional hint — prefer snippets whose tags match the user's question.\n" +
               string.Join("\n\n", topChunks.Select(FormatChunk));
    }

    private readonly record struct KbChunk(string FileName, string Content, string? Domain, string? DocType, string? Description);

    private static async Task RunKnowledgeQuery(DbConnection connection, string sql, string query, int max, List<KbChunk> chunks)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        var qp = cmd.CreateParameter();
        qp.ParameterName = "query";
        qp.Value = query;
        cmd.Parameters.Add(qp);

        var mp = cmd.CreateParameter();
        mp.ParameterName = "max";
        mp.Value = max;
        cmd.Parameters.Add(mp);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.GetString(1);
            if (chunks.Any(c => c.Content == content)) continue; // de-dupe
            chunks.Add(new KbChunk(
                reader.GetString(0),
                content,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await reader.CloseAsync();
    }

    private static string FormatChunk(KbChunk c)
    {
        var tags = new List<string> { c.FileName };
        if (!string.IsNullOrWhiteSpace(c.Domain)) tags.Add($"domain: {c.Domain}");
        if (!string.IsNullOrWhiteSpace(c.DocType)) tags.Add($"type: {c.DocType}");
        var header = $"[{string.Join(" | ", tags)}]";
        if (!string.IsNullOrWhiteSpace(c.Description)) header += $" (hint: {c.Description})";
        return $"{header}: {c.Content}";
    }

    private static (string Intent, string Text) ParseAiResponse(string rawContent)
    {
        var intent = "general";
        var responseText = rawContent;

        try
        {
            var parsed = OracleAiService.ParseJsonFromAiResponse(rawContent);

            intent = parsed.TryGetProperty("intent", out var i) ? i.GetString() ?? "general" : "general";

            if (parsed.TryGetProperty("text", out var t))
            {
                responseText = t.ValueKind == JsonValueKind.Object
                    ? t.ToString()
                    : t.GetString() ?? rawContent;
            }

            responseText = OracleAiService.DecodeUnicodeEscapes(responseText);
        }
        catch
        {
            // Fallback: try regex extraction
            var textMatch = Regex.Match(rawContent, @"""text""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
            var intentMatch = Regex.Match(rawContent, @"""intent""\s*:\s*""([^""]*)""");

            if (textMatch.Success)
            {
                responseText = textMatch.Groups[1].Value
                    .Replace("\\n", "\n")
                    .Replace("\\\"", "\"");
                responseText = OracleAiService.DecodeUnicodeEscapes(responseText);
                intent = intentMatch.Success ? intentMatch.Groups[1].Value : "general";
            }
        }

        return (intent, responseText);
    }

    private async Task TrySaveCorrections(string intent, Guid sessionId, Guid userId)
    {
        if (intent != "scan" && intent != "expense_complete") return;

        try
        {
            var recentMsgs = await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(10)
                .ToListAsync();

            var scanMsg = recentMsgs.FirstOrDefault(m =>
                m.Metadata != null && m.Metadata.Contains("scanned_data"));

            if (scanMsg?.Metadata == null) return;

            var metadata = JsonDocument.Parse(scanMsg.Metadata).RootElement;
            if (!metadata.TryGetProperty("scanned_data", out var scannedData)) return;

            var allText = string.Join("\n", recentMsgs.Select(m => m.Content));

            var corrections = new List<InvoiceCorrection>();
            var ctx = scannedData.TryGetProperty("vendor_name", out var vn) ? vn.GetString() : null;

            // Check for total amount correction
            var totalMatch = Regex.Match(allText, @"(?:Total)[:\s]*([0-9,.]+)", RegexOptions.IgnoreCase);
            if (totalMatch.Success && scannedData.TryGetProperty("total_amount", out var origTotal))
            {
                var correctedVal = totalMatch.Groups[1].Value.Replace(",", "");
                if (decimal.TryParse(correctedVal, out var corrected) &&
                    origTotal.ValueKind == JsonValueKind.Number &&
                    corrected != origTotal.GetDecimal())
                {
                    corrections.Add(new InvoiceCorrection
                    {
                        UserId = userId,
                        FieldName = "total_amount",
                        OriginalValue = origTotal.GetDecimal().ToString(),
                        CorrectedValue = correctedVal,
                        Context = ctx
                    });
                }
            }

            // Check for tax correction
            var taxMatch = Regex.Match(allText, @"(?:VAT|Tax|מע""מ)[:\s]*([0-9,.]+)", RegexOptions.IgnoreCase);
            if (taxMatch.Success && scannedData.TryGetProperty("tax_amount", out var origTax))
            {
                var correctedVal = taxMatch.Groups[1].Value.Replace(",", "");
                if (decimal.TryParse(correctedVal, out var corrected) &&
                    origTax.ValueKind == JsonValueKind.Number &&
                    corrected != origTax.GetDecimal())
                {
                    corrections.Add(new InvoiceCorrection
                    {
                        UserId = userId,
                        FieldName = "tax_amount",
                        OriginalValue = origTax.GetDecimal().ToString(),
                        CorrectedValue = correctedVal,
                        Context = ctx
                    });
                }
            }

            if (corrections.Count > 0)
            {
                _db.InvoiceCorrections.AddRange(corrections);
                Console.WriteLine($"Saved {corrections.Count} chatbot correction(s)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save corrections: {ex.Message}");
        }
    }

    private static string BuildSystemPrompt(ChatRequest request, GeoInfo geo, string knowledgeContext)
    {
        return $@"You are Milo 🦊 — a friendly, professional customer service assistant for TripEX (Travel & Expense Management). Your goal is to HELP users warmly and patiently. You ALWAYS respond in English regardless of the user's language.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The ""text"" field must ALWAYS contain natural, human-readable text. NEVER put JSON objects, code, or raw data structures inside the ""text"" field.
CRITICAL LANGUAGE RULE: You MUST ALWAYS respond in English, no matter what language the user writes in.

## Intent Categories:
- help: user wants guidance or how-to
- scan: user wants to scan a receipt/invoice
- bi: user wants reports, data analysis, or statistics
- online: user wants to book flights/hotels
- expense: user wants to add or manage expenses
- general: casual conversation or anything else

## Conversational Flows:

### When intent = ""expense"" (Add Expense):
Guide the user through collecting these fields one by one:
1. Description
2. Amount
3. Currency
4. Date
5. Category
When ALL fields are collected, respond with intent ""expense_complete"" and show a READABLE SUMMARY.

### When intent = ""online"" (Flight/Travel Request):
Guide through: 1. Destination 2. Departure date 3. Return date 4. Number of passengers 5. Special notes (optional)

### When user corrects OCR/scanned data:
Acknowledge, show UPDATED summary, ask if correct now. Use intent ""scan"".

### When user CONFIRMS data:
Respond warmly: ""Got it! ✅ Updating your expenses...""

## Response Style:
- If ""Knowledge Base Context"" is provided below, base your answer ONLY on that content.
- NEVER INVENT OR HALLUCINATE information.
- Be DETAILED and thorough
- Use friendly, supportive language
- ALWAYS respond in English

## Output format (ONLY this JSON, nothing else):
{{""intent"": ""<intent>"", ""text"": ""<your detailed, friendly answer in English>""}}

User's location (from IP): {geo.Location}
User's local time: {geo.LocalTime}
User's timezone: {geo.Timezone}
Browser-reported time: {request.UserDate ?? "unknown"} {request.UserTime ?? ""} ({request.UserTimezone ?? "unknown"})
Current context: source={request.Source}, scope={request.Scope ?? ""}{(request.Trid != null ? $", trid={request.Trid}" : "")}{knowledgeContext}";
    }
}
