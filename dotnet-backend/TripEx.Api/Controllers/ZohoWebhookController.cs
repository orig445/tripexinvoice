using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

/// <summary>
/// Where Zoho Desk tells us an agent has replied.
///
/// AllowAnonymous is not an oversight and cannot be tightened: Zoho's own documentation says
/// "Only open webhooks that are publicly accessible and do not require authentication are
/// supported" — there is no facility to attach an API key or bearer token to a Desk webhook. The
/// protection is therefore layered rather than at the door:
///
///   1. The URL ends in a shared secret (Zoho:WebhookSecret), so it cannot be found by guessing.
///   2. The body is never believed. It supplies a ticket id and nothing else; the reply itself is
///      re-read from Desk over our own authenticated connection. The most a leaked URL buys an
///      attacker is making us fetch a ticket we already own.
///   3. A ticket with no row in chat_session_tickets is ignored, so the reachable surface is
///      conversations we started ourselves.
///
/// Every path answers 200 quickly. Desk requires a response within five seconds, disables a
/// webhook that keeps failing and DELETES one that answers 410 — so returning an error to say
/// "not for me" would eventually cost us the registration itself.
/// </summary>
[ApiController]
[Route("api/zoho/desk")]
[AllowAnonymous]
public class ZohoWebhookController : ControllerBase
{
    private readonly ZohoAgentReplyService _relay;
    private readonly ZohoDeskService _zoho;
    private readonly ILogger<ZohoWebhookController> _logger;

    public ZohoWebhookController(
        ZohoAgentReplyService relay, ZohoDeskService zoho, ILogger<ZohoWebhookController> logger)
    {
        _relay = relay;
        _zoho = zoho;
        _logger = logger;
    }

    /// <summary>
    /// Desk sends a HEAD or GET here when the webhook is registered and expects 200, otherwise
    /// creation fails outright. Answering it is the entire purpose of this method.
    /// </summary>
    [HttpGet("thread/{secret}")]
    [HttpHead("thread/{secret}")]
    public IActionResult Validate(string secret)
    {
        if (!SecretMatches(secret)) return NotFound();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Ticket_Thread_Add. The body is a JSON ARRAY of events — Desk batches — so it is parsed as
    /// a list even when one event is the normal case.
    /// </summary>
    [HttpPost("thread/{secret}")]
    public async Task<IActionResult> ThreadAdded(string secret, [FromBody] JsonElement body)
    {
        // A wrong secret gets the same answer as a URL that was never routed. Anything more
        // specific tells whoever is probing that they have found the right shape.
        if (!SecretMatches(secret)) return NotFound();

        if (!_zoho.Options.IsRelayConfigured)
        {
            // Registered but switched off. Still 200: see the note on the class.
            _logger.LogInformation("[ZOHO-HOOK] received while the agent relay is off — acknowledged, ignored");
            return Ok(new { ok = true, relayed = 0 });
        }

        var events = ExtractThreadEvents(body);
        if (events.Count == 0) return Ok(new { ok = true, relayed = 0 });

        var relayed = 0;
        foreach (var e in events)
        {
            try
            {
                // CancellationToken.None on purpose. Desk's five-second budget governs how long it
                // waits for our answer, not how long we may take: abandoning a half-finished relay
                // because the client hung up would drop an agent's reply on the floor, and Desk's
                // retry policy is undocumented so there may be no second chance.
                var outcome = await _relay.RelayReplyAsync(e.TicketId, e.ThreadId, CancellationToken.None);
                if (outcome == ZohoAgentReplyService.RelayOutcome.Delivered) relayed++;
            }
            catch (Exception ex)
            {
                // One bad event must not cost the others in the same batch, and must not turn
                // into a non-200 that counts against the subscription.
                _logger.LogError(ex, "[ZOHO-HOOK] relaying ticket {TicketId} thread {ThreadId} threw",
                    e.TicketId, e.ThreadId);
            }
        }

        return Ok(new { ok = true, relayed });
    }

    private bool SecretMatches(string? provided)
    {
        var expected = _zoho.Options.WebhookSecret;

        // An unconfigured secret must never be matchable — otherwise a blank config would turn
        // this into an open endpoint rather than a closed one.
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided)) return false;

        // Fixed-time comparison: the secret sits in a URL an attacker can call repeatedly, which
        // is exactly the setting where an early-exit string compare leaks it one character at a
        // time. CryptographicOperations.FixedTimeEquals needs equal lengths, and length alone is
        // not worth protecting.
        var a = System.Text.Encoding.UTF8.GetBytes(expected.Trim());
        var b = System.Text.Encoding.UTF8.GetBytes(provided.Trim());
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>One thread Desk says was added: which ticket, and which thread on it.</summary>
    public readonly record struct ThreadEvent(string TicketId, string? ThreadId);

    /// <summary>
    /// Pulls the (ticket, thread) pairs out of a Ticket_Thread_Add batch, keeping only the events
    /// that could possibly be an agent answering a customer.
    ///
    /// Each event is kept SEPARATELY, including several on the same ticket. Collapsing them per
    /// ticket — which is what this did first — silently loses replies: Desk batches thread events
    /// into one POST, so an agent who writes "let me check" and then the actual answer produces two
    /// events, one relay, and only the newer thread ever reaches the customer. No timing race is
    /// needed for that, just a batch.
    ///
    /// The filtering here is a cheap first pass, not the decision: an event that survives it only
    /// earns a re-read from Desk, and that read applies the real check. Written defensively
    /// because this parses a body from an unauthenticated endpoint — every field is optional,
    /// every type is checked, and anything unexpected is skipped rather than thrown on.
    /// </summary>
    public static List<ThreadEvent> ExtractThreadEvents(JsonElement body)
    {
        var events = new List<ThreadEvent>();

        // Desk documents an array. A single object is accepted too, because a payload shape that
        // changes under us should degrade to working rather than to silence.
        var rawEvents = body.ValueKind switch
        {
            JsonValueKind.Array => body.EnumerateArray().ToList(),
            JsonValueKind.Object => new List<JsonElement> { body },
            _ => new List<JsonElement>(),
        };

        foreach (var ev in rawEvents)
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;

            if (!ev.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                continue;

            // "in" is the customer's own message arriving; relaying it would show customers their
            // own words back. Absent means unknown, and unknown is allowed through to the read.
            if (payload.TryGetProperty("direction", out var dir)
                && dir.ValueKind == JsonValueKind.String
                && !string.Equals(dir.GetString(), "out", StringComparison.OrdinalIgnoreCase))
                continue;

            // An internal note is written for colleagues, not customers.
            if (payload.TryGetProperty("visibility", out var vis)
                && vis.ValueKind == JsonValueKind.String
                && !string.Equals(vis.GetString(), "public", StringComparison.OrdinalIgnoreCase))
                continue;

            // The ticket's opening description is not a reply to anything.
            if (payload.TryGetProperty("isDescriptionThread", out var desc)
                && desc.ValueKind == JsonValueKind.True)
                continue;

            if (!payload.TryGetProperty("ticketId", out var tid)) continue;

            var ticketId = ReadId(tid);
            if (string.IsNullOrWhiteSpace(ticketId)) continue;

            // The thread's own id — which thread this notification is actually about. Optional:
            // a payload without it falls back to "the latest thread on that ticket", which is what
            // this always did and is still right when there is nothing better to go on.
            var threadId = payload.TryGetProperty("id", out var thr) ? ReadId(thr) : null;

            // Deduped on the PAIR, not on the ticket. Two notifications for the same thread are
            // genuinely the same event and only need relaying once; two threads on one ticket are
            // two different replies and both belong to the customer.
            var candidate = new ThreadEvent(ticketId!, threadId);
            if (!events.Contains(candidate)) events.Add(candidate);
        }

        return events;
    }

    /// <summary>Zoho sends ids as strings; a numeric one in a future payload should not be lost.</summary>
    private static string? ReadId(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.ToString(),
        _ => null,
    };
}
