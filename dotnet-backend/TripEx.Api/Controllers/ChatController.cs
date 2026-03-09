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

        try
        {
            var response = await _chatService.ProcessAsync(request, userId.Value, ipAddress);
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

    private Guid? GetUserId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
