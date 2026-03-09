using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
[Authorize]
public class KnowledgeController : ControllerBase
{
    private readonly KnowledgeService _knowledgeService;

    public KnowledgeController(KnowledgeService knowledgeService)
    {
        _knowledgeService = knowledgeService;
    }

    /// <summary>
    /// Trigger processing of an uploaded knowledge document
    /// </summary>
    [HttpPost("process")]
    public async Task<ActionResult<ProcessKnowledgeResponse>> Process([FromBody] ProcessKnowledgeRequest request)
    {
        if (!Guid.TryParse(request.DocumentId, out var docId))
            return BadRequest(new ProcessKnowledgeResponse { Success = false, Error = "Invalid document ID" });

        try
        {
            var (success, chunksCreated, error) = await _knowledgeService.ProcessDocumentAsync(docId);
            return Ok(new ProcessKnowledgeResponse
            {
                Success = success,
                ChunksCreated = chunksCreated,
                Error = error
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ProcessKnowledgeResponse { Success = false, Error = ex.Message });
        }
    }
}
