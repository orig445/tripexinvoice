using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TripEx.Api.Data;
using TripEx.Api.Models;
using TripEx.Api.Services;

namespace TripEx.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
[Authorize]
public class KnowledgeController : ControllerBase
{
    private readonly KnowledgeService _knowledgeService;
    private readonly FileStorageService _storage;
    private readonly TripExDbContext _db;

    // Guardrail: reject oversized uploads early (matches the frontend's 20MB hint).
    private const long MaxFileBytes = 25 * 1024 * 1024;

    public KnowledgeController(KnowledgeService knowledgeService, FileStorageService storage, TripExDbContext db)
    {
        _knowledgeService = knowledgeService;
        _storage = storage;
        _db = db;
    }

    /// <summary>
    /// Upload a single file and ingest it into the agent's RAG in one call.
    /// Accepts multipart/form-data: file + optional domain / docType / description tags.
    /// The frontend calls this once per file so each file carries its own tags.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxFileBytes + (1 * 1024 * 1024))]
    public async Task<ActionResult<UploadKnowledgeResponse>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? domain,
        [FromForm] string? docType,
        [FromForm] string? description)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new UploadKnowledgeResponse { Success = false, Error = "No file provided" });

        if (file.Length > MaxFileBytes)
            return BadRequest(new UploadKnowledgeResponse
            {
                Success = false,
                FileName = file.FileName,
                Error = $"File too large (max {MaxFileBytes / (1024 * 1024)}MB)"
            });

        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var (success, documentId, chunks, error) = await _knowledgeService.UploadAndProcessAsync(
                ms.ToArray(),
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                domain,
                docType,
                description,
                GetUserId());

            return Ok(new UploadKnowledgeResponse
            {
                Success = success,
                DocumentId = documentId == Guid.Empty ? null : documentId.ToString(),
                FileName = file.FileName,
                ChunksCreated = chunks,
                Error = error
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new UploadKnowledgeResponse
            {
                Success = false,
                FileName = file.FileName,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// List all knowledge documents (for the admin upload page).
    /// </summary>
    [HttpGet("documents")]
    public async Task<ActionResult<List<KnowledgeDocumentDto>>> ListDocuments()
    {
        var docs = await _db.KnowledgeDocuments
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new KnowledgeDocumentDto
            {
                Id = d.Id.ToString(),
                FileName = d.FileName,
                FileType = d.FileType,
                FileSize = d.FileSize,
                Domain = d.Domain,
                DocType = d.DocType,
                Description = d.Description,
                Status = d.Status,
                CreatedAt = d.CreatedAt
            })
            .ToListAsync();

        return Ok(docs);
    }

    /// <summary>
    /// Delete a knowledge document: its chunks, its stored file, and the row.
    /// </summary>
    [HttpDelete("documents/{id}")]
    public async Task<IActionResult> DeleteDocument(string id)
    {
        if (!Guid.TryParse(id, out var docId))
            return BadRequest(new { success = false, error = "Invalid document ID" });

        var doc = await _db.KnowledgeDocuments.FindAsync(docId);
        if (doc == null)
            return NotFound(new { success = false, error = "Document not found" });

        // Remove chunks first, then the stored file, then the row.
        var chunks = _db.KnowledgeChunks.Where(c => c.DocumentId == docId);
        _db.KnowledgeChunks.RemoveRange(chunks);

        try { await _storage.DeleteFileAsync(doc.FileUrl); }
        catch (Exception ex) { Console.Error.WriteLine($"[KNOWLEDGE] Could not delete file {doc.FileUrl}: {ex.Message}"); }

        _db.KnowledgeDocuments.Remove(doc);
        await _db.SaveChangesAsync();

        return Ok(new { success = true });
    }

    /// <summary>
    /// Update the tags (domain / type / description) of an existing document.
    /// </summary>
    [HttpPatch("documents/{id}")]
    public async Task<IActionResult> UpdateTags(string id, [FromBody] UpdateKnowledgeTagsRequest request)
    {
        if (!Guid.TryParse(id, out var docId))
            return BadRequest(new { success = false, error = "Invalid document ID" });

        var doc = await _db.KnowledgeDocuments.FindAsync(docId);
        if (doc == null)
            return NotFound(new { success = false, error = "Document not found" });

        doc.Domain = string.IsNullOrWhiteSpace(request.Domain) ? null : request.Domain.Trim();
        doc.DocType = string.IsNullOrWhiteSpace(request.DocType) ? null : request.DocType.Trim();
        doc.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        doc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true });
    }

    /// <summary>
    /// Trigger (re)processing of an already-uploaded knowledge document.
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

    private Guid? GetUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
