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
    private readonly ILogger<ChatService> _logger;
    private readonly string _supportContact;

    // Data/page-links.json is a large, static, deployment-wide dataset (hundreds of TAS
    // pages) — load it once per process (like log4net's LogDir) rather than per request.
    // Anchored to AppContext.BaseDirectory because IIS in-process hosting changes the
    // process's current directory to C:\Windows\System32\inetsrv (see Program.cs LogDir).
    private static readonly Dictionary<string, PageLinkConfig> _pageLinks = LoadPageLinks();

    private static Dictionary<string, PageLinkConfig> LoadPageLinks()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "page-links.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"⚠️ [CHAT] Data/page-links.json not found at '{path}' — Milo will answer without page links.");
                return new();
            }
            var list = JsonSerializer.Deserialize<List<PageLinkConfig>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var dict = list
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"✅ [CHAT] Loaded {dict.Count} page links from Data/page-links.json");
            return dict;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Failed to load Data/page-links.json — Milo will answer without page links: {ex.Message}");
            return new();
        }
    }

    private static bool ContainsHebrew(string text) => Regex.IsMatch(text, @"[֐-׿]");

    // Intent → Actions mapping
    private static readonly Dictionary<string, (List<string> Actions, string? RedirectPage)> ActionMapping = new()
    {
        ["help"]             = (new(), null),
        ["escalate"]         = (new(), null),
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
        GeolocationService geoService,
        ILogger<ChatService> logger,
        IConfiguration configuration)
    {
        _db = db;
        _oracle = oracle;
        _invoiceService = invoiceService;
        _geoService = geoService;
        _logger = logger;
        _supportContact = configuration["Support:Contact"] ?? "support@tripex.io";
    }

    public async Task<ChatResponse> ProcessAsync(ChatRequest request, Guid userId, string? ipAddress, string userRole = "user")
    {
        // ── Session handling ──
        // DB writes are best-effort: if the database is unavailable we still answer
        // (via OCI) instead of failing the whole request — persistence is just skipped.
        Guid sessionId = Guid.NewGuid();
        if (!string.IsNullOrEmpty(request.SessionToken) && Guid.TryParse(request.SessionToken, out var existingId))
        {
            sessionId = existingId;
        }
        else
        {
            try
            {
                var session = new ChatSession { UserId = userId, Source = request.Source };
                _db.ChatSessions.Add(session);
                await _db.SaveChangesAsync();
                sessionId = session.Id;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [CHAT] Session not persisted (DB unavailable): {ex.Message}");
            }
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

        // ── Save user message (best-effort) ──
        try
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId,
                Role = "user",
                Content = request.Text
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] User message not persisted (DB unavailable): {ex.Message}");
        }

        // ── Load history (best-effort; empty when DB is unavailable) ──
        var history = new List<(string Role, string Content)>();
        try
        {
            history = (await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .Take(50)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync())
                .Select(m => (m.Role, m.Content))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] History not loaded (DB unavailable): {ex.Message}");
        }

        // ── Load config (best-effort; defaults when DB is unavailable) ──
        ChatbotConfig? config = null;
        try
        {
            config = await _db.ChatbotConfigs
                .Where(c => c.IsActive)
                .OrderByDescending(c => c.UpdatedAt)   // deterministic: newest active config wins
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Config not loaded (DB unavailable), using defaults: {ex.Message}");
        }

        var temperature = (double)(config?.Temperature ?? 0.3m);
        var maxTokens = config?.MaxTokens ?? 2048;

        // ── Geolocation ──
        var geo = await _geoService.GetLocationAsync(ipAddress);

        // ── RAG: Search knowledge base ──
        var knowledgeContext = await SearchKnowledgeBase(request.Text);

        // ── Build system prompt ──
        // Every known page goes in — the AI's own semantic matching handles Hebrew
        // morphology/synonyms far better than a keyword-overlap filter would (tried and
        // dropped; see git history). Descriptions are kept short (tag-phrases, not full
        // sentences) specifically to keep this affordable at ~366 entries.
        var allPages = _pageLinks.Values.ToList();
        var systemPrompt = BuildSystemPrompt(
            request, geo, knowledgeContext, userRole, allPages);

        // ── Build messages ──
        var messages = new List<OracleMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };
        messages.AddRange(history.Select(h => new OracleMessage { Role = h.Role, Content = h.Content }));

        // The current user message is normally already the last entry in `history`
        // (saved to the DB above, then reloaded). But DB access is best-effort — if
        // persistence or the reload failed, `history` can be empty, and the model
        // would receive ONLY the system prompt with no actual question at all,
        // silently degrading every reply to a generic greeting no matter what the
        // user asked. Guarantee the current message is present regardless of DB state.
        var lastIsCurrentUserMessage = messages.Count > 0
            && messages[^1].Role == "user"
            && messages[^1].Content is string lastContent
            && lastContent == request.Text;
        if (!lastIsCurrentUserMessage)
            messages.Add(new OracleMessage { Role = "user", Content = request.Text });

        // ── Call Oracle AI ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rawContent = await _oracle.ChatAsync(messages, maxTokens, temperature);
        sw.Stop();

        // ── Parse response ──
        var (intent, responseText, page) = ParseAiResponse(rawContent);

        // ── Map intent to actions ──
        var mapping = ActionMapping.GetValueOrDefault(intent, ActionMapping["general"]);

        // ── Map page → a real TAS URL + button label (Chatbot:PageLinks config) ──
        // The AI only ever sees the page KEY (and its Description); the actual URL
        // lives in config so pages can be added/changed without a code deploy.
        var pageLink = !string.IsNullOrEmpty(page) && _pageLinks.TryGetValue(page, out var pl) ? pl : null;

        // The widget renders "text" as raw HTML (innerHTML), so a plain <a> tag with
        // target="_top" becomes a real clickable link that breaks out of the chat
        // iframe on click — no frontend change needed to support this.
        var isHebrewReply = ContainsHebrew(responseText);
        if (pageLink != null)
        {
            // Match the link label to whatever language the reply actually came out in —
            // detected from the reply text itself (Hebrew Unicode block present or not)
            // rather than asking the AI for a separate field, so it can't get out of sync
            // with what the user actually sees.
            var label = isHebrewReply ? pageLink.Label : pageLink.LabelEn;
            var safeUrl = System.Net.WebUtility.HtmlEncode(pageLink.Url);
            var safeLabel = System.Net.WebUtility.HtmlEncode(label);
            responseText += $"\n\n<a href=\"{safeUrl}\" target=\"_top\" rel=\"noopener\">{safeLabel}</a>";
        }

        var escalated = intent == "escalate";
        // The support contact line is added here (deterministically, in code) rather than
        // left to the AI's own wording — guarantees it always names the real address and
        // always comes AFTER the page link above, never before it.
        if (escalated)
        {
            responseText += isHebrewReply
                ? $"\n\nניתן לפנות לתמיכה במייל {_supportContact}"
                : $"\n\nYou can reach support by email at {_supportContact}";
        }

        // ── Save corrections (learning from OCR corrections) ──
        await TrySaveCorrections(intent, sessionId, userId);

        var latencyMs = sw.ElapsedMilliseconds;
        var ragChars = knowledgeContext?.Length ?? 0;

        // Full Q&A to the rolling file log (logs/tripex-*.log) — always, even if DB is down.
        _logger.LogInformation(
            "[CHAT] session={SessionId} user={UserId} source={Source} intent={Intent} rag={RagChars}c latency={LatencyMs}ms\n  Q: {Message}\n  A: {Response}",
            sessionId, userId, request.Source ?? "-", intent, ragChars, latencyMs, request.Text, responseText);

        // ── Persist assistant message + audit log + escalation (best-effort) ──
        // Skipped silently if the DB is unavailable so the answer still returns.
        try
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId,
                Role = "assistant",
                Content = responseText,
                Intent = intent,
                Metadata = JsonSerializer.Serialize(new { actions = mapping.Actions, page, redirectPage = pageLink?.Url ?? "" })
            });

            _db.ChatbotLogs.Add(new ChatbotLog
            {
                SessionId = sessionId,
                UserId = userId,
                EventType = "intent_detected",
                Details = JsonSerializer.Serialize(new
                {
                    intent,
                    actions = mapping.Actions,
                    page,
                    redirectPage = pageLink?.Url ?? "",
                    source = request.Source,
                    latency_ms = latencyMs,
                    rag_chars = ragChars,
                    // Full message + response so a chat turn can be examined end-to-end.
                    message = request.Text,
                    response = responseText
                })
            });

            // ── Escalation: hand the ticket to a human when Milo can't help ──
            if (escalated)
            {
                var ticket = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (ticket != null)
                {
                    ticket.Escalated = true;
                    ticket.EscalatedAt = DateTime.UtcNow;
                    ticket.Status = "escalated";
                    ticket.EscalationReason = request.Text.Length > 1000 ? request.Text[..1000] : request.Text;
                    ticket.UpdatedAt = DateTime.UtcNow;
                }
                _logger.LogInformation("[TICKET-ESCALATED] session={SessionId} user={UserId} reason={Reason}",
                    sessionId, userId, request.Text);
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [CHAT] Assistant message/log not persisted (DB unavailable): {ex.Message}");
        }

        return new ChatResponse
        {
            Text = responseText,
            Actions = mapping.Actions,
            RedirectPage = pageLink?.Url ?? "",
            RedirectLabel = pageLink?.Label,
            SessionId = sessionId.ToString(),
            Escalated = escalated,
            SupportContact = escalated ? _supportContact : null
        };
    }

    /// <summary>
    /// List tickets (= chat sessions) for review / learning. Admin-only via the controller.
    /// </summary>
    public async Task<List<object>> ListTicketsAsync(bool escalatedOnly, int take)
    {
        var q = _db.ChatSessions.AsQueryable();
        if (escalatedOnly) q = q.Where(s => s.Escalated);

        var tickets = await q
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new
            {
                ticketId = s.Id,
                userId = s.UserId,
                source = s.Source,
                status = s.Status,
                escalated = s.Escalated,
                escalatedAt = s.EscalatedAt,
                escalationReason = s.EscalationReason,
                messageCount = _db.ChatMessages.Count(m => m.SessionId == s.Id),
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt
            })
            .ToListAsync();

        return tickets.Cast<object>().ToList();
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

    private async Task<string> SearchKnowledgeBase(string queryText, string audience = "external")
    {
        // Call the search_knowledge database function via raw SQL.
        // Returns file_name + content + tagging (domain / doc_type / description)
        // so the agent knows WHEN each snippet is relevant. The audience filter
        // keeps the customer ('external') and staff ('internal') knowledge bases apart.
        var chunks = new List<KbChunk>();

        // GetDbConnection() itself can throw (e.g. the DB provider assembly failing to
        // load) — it must be INSIDE the try, not before it, or RAG failures crash the
        // whole chat request with a 500 instead of just returning no knowledge context.
        DbConnection? connection = null;
        try
        {
            connection = _db.Database.GetDbConnection();
            await connection.OpenAsync();

            // Full query search
            await RunKnowledgeQuery(connection, queryText, 5, audience, chunks);

            // Also search individual words for better Hebrew matching
            var words = queryText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2).Take(3);

            foreach (var word in words)
                await RunKnowledgeQuery(connection, word, 3, audience, chunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RAG search error: {ex.Message}");
        }
        finally
        {
            if (connection != null && connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }

        if (chunks.Count == 0) return "";

        var topChunks = chunks.Take(5);
        return "\n\n## Knowledge Base Context (use this to answer the user):\n" +
               "Each snippet is tagged with its domain/type and an optional hint — prefer snippets whose tags match the user's question.\n" +
               string.Join("\n\n", topChunks.Select(FormatChunk));
    }

    private readonly record struct KbChunk(string FileName, string Content, string? Domain, string? DocType, string? Description);

    // Fixed, fully-parameterized query. It is a compile-time constant, so no
    // user-controlled string can ever reach the command text — the values are
    // bound as DbParameters (@query/@max/@audience) below. (Resolves the SAST
    // "Csharp SQLi" finding on cmd.CommandText, which was a false positive.)
    private const string KnowledgeSearchSql =
        "SELECT file_name, content, domain, doc_type, description FROM dbo.search_knowledge(@query, @max, @audience)";

    private static async Task RunKnowledgeQuery(DbConnection connection, string query, int max, string audience, List<KbChunk> chunks)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = KnowledgeSearchSql;

        var qp = cmd.CreateParameter();
        qp.ParameterName = "query";
        qp.Value = query;
        cmd.Parameters.Add(qp);

        var mp = cmd.CreateParameter();
        mp.ParameterName = "max";
        mp.Value = max;
        cmd.Parameters.Add(mp);

        var ap = cmd.CreateParameter();
        ap.ParameterName = "audience";
        ap.Value = string.IsNullOrWhiteSpace(audience) ? "external" : audience;
        cmd.Parameters.Add(ap);

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

    private static (string Intent, string Text, string Page) ParseAiResponse(string rawContent)
    {
        var intent = "general";
        var responseText = rawContent;
        var page = "";

        try
        {
            var parsed = OracleAiService.ParseJsonFromAiResponse(rawContent);

            intent = parsed.TryGetProperty("intent", out var i) ? i.GetString() ?? "general" : "general";
            page = parsed.TryGetProperty("page", out var p) ? p.GetString() ?? "" : "";

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
            var pageMatch = Regex.Match(rawContent, @"""page""\s*:\s*""([^""]*)""");

            if (textMatch.Success)
            {
                responseText = textMatch.Groups[1].Value
                    .Replace("\\n", "\n")
                    .Replace("\\\"", "\"");
                responseText = OracleAiService.DecodeUnicodeEscapes(responseText);
                intent = intentMatch.Success ? intentMatch.Groups[1].Value : "general";
                page = pageMatch.Success ? pageMatch.Groups[1].Value : "";
            }
        }

        return (intent, responseText, page);
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

    private static string BuildSystemPrompt(
        ChatRequest request, GeoInfo geo, string knowledgeContext, string userRole,
        List<PageLinkConfig> allPages)
    {
        var isAdmin = string.Equals(userRole, "admin", StringComparison.OrdinalIgnoreCase);
        var escalationRule = isAdmin
            ? "This user IS a system admin — when escalation is needed, route them to Support only (never tell an admin to contact their admin)."
            : "This user is a regular user — when escalation is needed, route them to their System Admin, or to Support.";

        var navigationSection = "";
        if (allPages.Count > 0)
        {
            var hubPages = allPages.Where(p => p.Category == "Navigation").ToList();
            var specificPages = allPages.Where(p => p.Category != "Navigation").ToList();
            var hubList = string.Join("\n", hubPages.Select(p => $"- \"{p.Key}\": {p.Description}"));
            var specificList = string.Join("\n", specificPages.Select(p => $"- \"{p.Key}\": {p.Description}"));
            navigationSection = $@"

## Navigation — sending the user to a page
Some topics have one specific TripEX page. If the user's question is CLEARLY about reaching or
using ONE of the pages below, include ""page"": ""<key>"" in the JSON (alongside intent/text) so a
clickable link to that page is added to your answer automatically. Only set it when there's a
clear match — if none apply, omit ""page"" or set it to """". Never invent a key that isn't in this list.

🔴 READ THE FULL SPECIFIC LIST FIRST — do this in order, every time:
1. Go through the ""Specific pages/reports"" list below FIRST, top to bottom. It is long — do not
   stop at the first plausible-looking entry. Identify EVERY entry that could relate to the
   question, then pick the SINGLE one whose description most exactly matches what was asked.
2. Only if NOTHING in that list fits — not even loosely — look at ""General sections"" (below it)
   as a fallback for browsing that whole area (e.g. ""what reports do you have?"").
3. NEVER pick a ""General sections"" hub just because you're unsure which specific page is exactly
   right, or because you noticed it before finishing the specific list. Uncertainty means: pick
   your best specific guess, not the hub.
4. When you DO set a ""page"" key, do not add your own ""if this isn't right, contact your admin/
   support"" disclaimer in ""text"" — a link to that exact page is already added automatically
   after your text, so that caveat is unnecessary noise. Just give the direct answer.
5. 🔴 CONSISTENCY RULE: if ""text"" names ONE specific report/page (by its title or key) as THE
   answer — not just mentioned in passing — ""page"" MUST be that exact same key. Do not write a
   specific report in ""text"" and then set ""page"" to a different, more general key (e.g. a hub) —
   that mismatch is worse than picking neither. Committing to your best specific guess in BOTH
   fields together is correct even when you're not fully certain; hedging by keeping ""text""
   specific but ""page"" general is not allowed.

Specific pages/reports — scan ALL of these before considering anything else:
{specificList}

General sections — LAST RESORT ONLY, use only if nothing above fits:
{hubList}";
        }

        return $@"You are Milo 🦊 — a friendly, professional customer-service assistant for TripEX (Travel & Expense Management). Your job is to HELP users understand and use the TripEX system: answer their questions, explain how features work, and help troubleshoot problems. Be warm, patient and clear.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The ""text"" field must ALWAYS contain natural, human-readable text. NEVER put JSON objects, code, or raw data structures inside the ""text"" field.
CRITICAL LANGUAGE RULE: Detect the language of the user's latest message and reply in that SAME language (Hebrew → Hebrew, English → English, etc.). Never switch languages on your own — mirror the user.

## What you CAN do
- Answer questions about the TripEX system and how to use it.
- Walk the user, step by step, through how to do things THEMSELVES in the system (e.g. how to submit an expense report, scan a receipt, book travel) — based ONLY on the Knowledge Base Context below.
- Help troubleshoot problems the user describes.

## What you CANNOT do — be honest, never pretend
- You do NOT perform actions for the user. You cannot upload invoices, create or submit expense reports, book flights or hotels, or change anything in the system on their behalf.
- If the user asks you to DO such an action, say clearly that you can't do it for them, then either guide them how to do it themselves (from the Knowledge Base) or escalate (see below).

## Escalation — routing to a human
Escalate when: you don't know the answer, the Knowledge Base has nothing relevant, or the user needs a human to take action.
- {escalationRule}
- Use intent ""escalate"" and, in ""text"", explain the situation and let the user know a human will help.
- Do NOT write a specific support email/contact address yourself — the real one is appended
  automatically right after your text. You may say ""your System Admin"" generically when that
  applies, but never invent or state a specific email.

## Intent Categories
- help: the user wants guidance, a how-to, or an explanation
- escalate: route the user to a human (System Admin / Support) per the rule above
- general: greetings, small talk, or anything else

## Response Style
- If ""Knowledge Base Context"" is provided below, base your answer ONLY on that content. NEVER invent or hallucinate.
- If nothing relevant is in the Knowledge Base, say so honestly and escalate — do NOT guess.
- PRIVACY (CRITICAL): NEVER reveal personal or customer-specific data — names, emails, phone numbers, company/customer names, ticket/TAS/trip numbers, or one customer's details to another. If a snippet contains such data, use only the general how-to and omit the identifiers.
- Be DETAILED and thorough, friendly and supportive. Reply in the same language the user wrote in.

## Formatting (applies inside the ""text"" field, using \n for line breaks)
- If the answer involves a SEQUENCE of actions the user must take, structure it as a
  numbered list: ""1."", ""2."", ""3.""… — ONE step per line, in the order they must be done.
- Put a blank line (\n\n) before the numbered list and, if you add a closing line
  (e.g. offering further help), a blank line before that too. Keep a short intro
  sentence before the list when useful context is needed (e.g. why these steps apply).
- If one step itself is a navigation path through menus/screens (e.g. Menu → Submenu →
  Button), keep that whole path on the SAME numbered line, in order.
- For a simple one-fact answer with no sequence of actions, plain prose is fine — do not
  force a numbered list where there is nothing to sequence.
{navigationSection}

## Output format (ONLY this JSON, nothing else — omit ""page"" when it doesn't apply)
{{""intent"": ""<intent>"", ""text"": ""<your detailed, friendly answer in English>"", ""page"": ""<page key or omit>""}}

User role: {userRole}
User's location (from IP): {geo.Location}
User's local time: {geo.LocalTime}
User's timezone: {geo.Timezone}
Browser-reported time: {request.UserDate ?? "unknown"} {request.UserTime ?? ""} ({request.UserTimezone ?? "unknown"})
Current context: source={request.Source}, scope={request.Scope ?? ""}{(request.Trid != null ? $", trid={request.Trid}" : "")}{knowledgeContext}";
    }
}
