using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly ChatService _chatService;

    public ChatController(ChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>
    /// Main chat endpoint — handles text messages and image scanning
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0]?.Trim();

        var userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "user";

        try
        {
            var response = await _chatService.ProcessAsync(request, userId.Value, ipAddress, userRole);
            return Ok(response);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return StatusCode(429, new { error = "Rate limit exceeded" });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Chat error: {ex}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// List support tickets (= chat sessions) for review and learning. Admin only.
    /// Use ?escalatedOnly=true to see only tickets Milo handed to a human.
    /// </summary>
    [HttpGet("tickets")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult> ListTickets([FromQuery] bool escalatedOnly = false, [FromQuery] int take = 100)
    {
        var tickets = await _chatService.ListTicketsAsync(escalatedOnly, Math.Clamp(take, 1, 500));
        return Ok(tickets);
    }

    // Stable id used for server-to-server (X-Api-Key) callers such as TAS, which
    // authenticate as a system principal without a real per-user GUID.
    private static readonly Guid ApiKeySystemUserId = new("00000000-0000-0000-0000-000000000001");

    private Guid? GetUserId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var id)) return id;

        // X-Api-Key auth sets NameIdentifier = "api-key-user" (not a GUID). Map that
        // to a stable system user id so the chat endpoint works for TAS, instead of
        // rejecting it with 401 (this is why OCR worked via API key but chat did not).
        if (User.FindFirst("auth_type")?.Value == "api_key")
            return ApiKeySystemUserId;

        return null;
    }
}
