namespace TripEx.Api.Services;

/// <summary>
/// Service for processing knowledge base documents (chunking text for RAG).
/// Reads files from local storage or S3 instead of Supabase Storage.
/// </summary>
public class KnowledgeService
{
    private readonly Data.TripExDbContext _db;
    private readonly FileStorageService _storage;
    private const int ChunkSize = 1000;
    private const int ChunkOverlap = 200;

    public KnowledgeService(Data.TripExDbContext db, FileStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    /// <summary>
    /// Full upload pipeline for one file: store bytes, create the document row
    /// (with domain / type / description tags), then chunk + index for RAG.
    /// </summary>
    public async Task<(bool Success, Guid DocumentId, int ChunksCreated, string? Error)> UploadAndProcessAsync(
        byte[] fileBytes,
        string fileName,
        string contentType,
        string? domain,
        string? docType,
        string? description,
        Guid? uploadedBy,
        string? audience = "external")
    {
        if (fileBytes.Length == 0)
            return (false, Guid.Empty, 0, "Empty file");

        var documentId = Guid.NewGuid();

        // Store the raw file under knowledge/<id>.<ext> so it can be re-processed or downloaded later.
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        var relativePath = $"knowledge/{documentId}{ext}";
        await _storage.SaveFileAsync(relativePath, fileBytes);

        var doc = new Data.KnowledgeDocument
        {
            Id = documentId,
            FileName = fileName,
            FileType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            FileUrl = relativePath,
            FileSize = fileBytes.Length,
            Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim(),
            DocType = string.IsNullOrWhiteSpace(docType) ? null : docType.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Audience = audience == "internal" ? "internal" : "external",
            UploadedBy = uploadedBy,
            Status = "pending"
        };
        _db.KnowledgeDocuments.Add(doc);
        await _db.SaveChangesAsync();

        var (success, chunks, error) = await ProcessDocumentAsync(documentId);
        return (success, documentId, chunks, error);
    }

    public async Task<(bool Success, int ChunksCreated, string? Error)> ProcessDocumentAsync(Guid documentId)
    {
        var doc = await _db.KnowledgeDocuments.FindAsync(documentId);
        if (doc == null) return (false, 0, "Document not found");

        doc.Status = "processing";
        await _db.SaveChangesAsync();

        try
        {
            // Download file from configured storage (local or S3)
            var fileBytes = await _storage.ReadFileAsync(doc.FileUrl);
            
            // Extract text based on file type
            var extractedText = ExtractText(fileBytes, doc.FileType, doc.FileName);
            
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                doc.Status = "error";
                await _db.SaveChangesAsync();
                return (false, 0, "Could not extract text from document");
            }

            var chunksCreated = await ChunkAndSaveAsync(documentId, extractedText);

            return (true, chunksCreated, null);
        }
        catch (Exception ex)
        {
            doc.Status = "error";
            await _db.SaveChangesAsync();
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Extract text from file bytes based on file type.
    /// Supports plain text/CSV/JSON/XML/MD, PDF (PdfPig) and DOCX/XLSX (OpenXml).
    /// </summary>
    private static string ExtractText(byte[] fileBytes, string fileType, string fileName)
    {
        var lowerType = fileType.ToLowerInvariant();
        var lowerName = fileName.ToLowerInvariant();

        // Word documents
        if (lowerName.EndsWith(".docx") || lowerType.Contains("wordprocessingml"))
            return ExtractDocxText(fileBytes);

        // Excel spreadsheets
        if (lowerName.EndsWith(".xlsx") || lowerType.Contains("spreadsheetml"))
            return ExtractXlsxText(fileBytes);

        // PDF documents (embedded text only; scanned PDFs need OCR)
        if (lowerName.EndsWith(".pdf") || lowerType.Contains("pdf"))
            return ExtractPdfText(fileBytes);

        // Plain text formats
        if (lowerType.Contains("text") || lowerType.Contains("csv") ||
            lowerType.Contains("json") || lowerType.Contains("xml") ||
            lowerName.EndsWith(".txt") || lowerName.EndsWith(".csv") ||
            lowerName.EndsWith(".json") || lowerName.EndsWith(".xml") ||
            lowerName.EndsWith(".md"))
        {
            return System.Text.Encoding.UTF8.GetString(fileBytes);
        }

        // Fallback: try reading as UTF-8
        return System.Text.Encoding.UTF8.GetString(fileBytes);
    }

    private static string ExtractPdfText(byte[] fileBytes)
    {
        var sb = new System.Text.StringBuilder();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(fileBytes);
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private static string ExtractDocxText(byte[] fileBytes)
    {
        using var ms = new MemoryStream(fileBytes);
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";

        var sb = new System.Text.StringBuilder();
        // Each paragraph on its own line so chunking can break on sentence/line boundaries.
        foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = para.InnerText;
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static string ExtractXlsxText(byte[] fileBytes)
    {
        using var ms = new MemoryStream(fileBytes);
        using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(ms, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart == null) return "";

        // Shared strings table holds most cell text in xlsx.
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var sb = new System.Text.StringBuilder();

        foreach (var wsPart in workbookPart.WorksheetParts)
        {
            foreach (var row in wsPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>())
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                {
                    var value = cell.CellValue?.InnerText ?? "";
                    if (cell.DataType != null &&
                        cell.DataType.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString &&
                        int.TryParse(value, out var idx) && sharedStrings != null)
                    {
                        value = sharedStrings.ElementAt(idx).InnerText;
                    }
                    if (!string.IsNullOrWhiteSpace(value)) cells.Add(value.Trim());
                }
                // Tab-separated cells keep row structure readable for the model.
                if (cells.Count > 0) sb.AppendLine(string.Join("\t", cells));
            }
        }
        return sb.ToString();
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
