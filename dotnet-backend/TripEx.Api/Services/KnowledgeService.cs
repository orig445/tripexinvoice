namespace TripEx.Api.Services;

/// <summary>
/// Service for processing knowledge base documents (chunking text for RAG)
/// </summary>
public class KnowledgeService
{
    private readonly Data.TripExDbContext _db;
    private const int ChunkSize = 1000;
    private const int ChunkOverlap = 200;

    public KnowledgeService(Data.TripExDbContext db)
    {
        _db = db;
    }

    public async Task<(bool Success, int ChunksCreated, string? Error)> ProcessDocumentAsync(Guid documentId)
    {
        var doc = await _db.KnowledgeDocuments.FindAsync(documentId);
        if (doc == null) return (false, 0, "Document not found");

        doc.Status = "processing";
        await _db.SaveChangesAsync();

        try
        {
            // TODO: Download file from Supabase Storage and extract text
            // For now, this is a placeholder — you need to implement file download
            // using Supabase Storage REST API:
            // GET {SUPABASE_URL}/storage/v1/object/knowledge/{doc.FileUrl}
            // Headers: Authorization: Bearer {SERVICE_ROLE_KEY}
            //
            // Then extract text based on file type:
            // - text/csv/json/xml → read as string
            // - pdf/docx → use a library like iTextSharp or DocumentFormat.OpenXml
            // - images → call Oracle AI Vision for OCR

            throw new NotImplementedException(
                "Implement file download from Supabase Storage and text extraction. " +
                "See the Edge Function source in supabase/functions/process-knowledge/index.ts for logic.");
        }
        catch (Exception ex)
        {
            doc.Status = "error";
            await _db.SaveChangesAsync();
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Call this after extracting text from a document
    /// </summary>
    public async Task<int> ChunkAndSaveAsync(Guid documentId, string extractedText)
    {
        // Sanitize: remove null bytes
        extractedText = extractedText.Replace("\0", "");

        if (string.IsNullOrWhiteSpace(extractedText))
            throw new InvalidOperationException("No text to chunk");

        var chunks = ChunkText(extractedText);

        // Delete existing chunks
        var existingChunks = _db.KnowledgeChunks.Where(c => c.DocumentId == documentId);
        _db.KnowledgeChunks.RemoveRange(existingChunks);

        // Insert new chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            _db.KnowledgeChunks.Add(new Data.KnowledgeChunk
            {
                DocumentId = documentId,
                Content = chunks[i],
                ChunkIndex = i
            });
        }

        // Update document status
        var doc = await _db.KnowledgeDocuments.FindAsync(documentId);
        if (doc != null) doc.Status = "ready";

        await _db.SaveChangesAsync();
        return chunks.Count;
    }

    private static List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + ChunkSize, text.Length);

            // Try to break at sentence boundary
            if (end < text.Length)
            {
                var lastPeriod = text.LastIndexOf('.', end, end - start);
                var lastNewline = text.LastIndexOf('\n', end, end - start);
                var breakPoint = Math.Max(lastPeriod, lastNewline);
                if (breakPoint > start + ChunkSize / 2)
                    end = breakPoint + 1;
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);

            start = end - ChunkOverlap;
            if (start < 0) start = 0;
            if (end >= text.Length) break;
        }

        return chunks;
    }
}
