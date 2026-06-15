using System.Collections.Concurrent;
using PDFtoTTS.Api.Documents;
using PDFtoTTS.Api.Storage;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Ingestion;

namespace PDFtoTTS.Api.Pipeline;

/// <summary>
/// Extracts a document's text on a background task so the upload request returns
/// immediately, advancing the document Extracting→Ready|Error. A PDF that yields no
/// text is treated as a scanned/image PDF and OCR'd (ocrmypdf) before re-extraction,
/// so scanned books become readable instead of silently producing an empty session.
/// </summary>
public sealed class DocumentPipeline
{
    private readonly IDocumentStore _documents;
    private readonly IFileStorage _files;
    private readonly PdfExtractor _pdf;
    private readonly EpubExtractor _epub;
    private readonly PdfOcr _ocr;
    private readonly ILogger<DocumentPipeline> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public DocumentPipeline(
        IDocumentStore documents,
        IFileStorage files,
        PdfExtractor pdf,
        EpubExtractor epub,
        PdfOcr ocr,
        ILogger<DocumentPipeline> logger)
    {
        _documents = documents;
        _files = files;
        _pdf = pdf;
        _epub = epub;
        _ocr = ocr;
        _logger = logger;
    }

    /// <summary>Begin extracting the already-saved original at <paramref name="originalRelPath"/>.</summary>
    public void Start(Document doc, string originalRelPath) =>
        _running[doc.Id] = Task.Run(() => RunAsync(doc, originalRelPath));

    private async Task RunAsync(Document doc, string originalRelPath)
    {
        try
        {
            string path = _files.FullPath(originalRelPath);
            ExtractionResult result = doc.Type == DocumentType.Epub
                ? _epub.Extract(path)
                : _pdf.Extract(path);

            // A PDF with pages but no words is a scanned/image PDF: OCR it to add a
            // text layer, then re-extract through the same path (positions intact).
            if (doc.Type == DocumentType.Pdf && result.Words.Count == 0 && result.PageCount > 0)
                result = await OcrAndReExtract(doc, path);

            if (result.Words.Count == 0)
            {
                _logger.LogWarning("Document {DocumentId} yielded no readable text", doc.Id);
                Fail(doc, result.PageCount);
                return;
            }

            _documents.SetExtraction(doc.Id, doc with
            {
                PageCount = result.PageCount,
                WordCount = result.Words.Count,
                Status = DocumentStatus.Ready,
            }, result.Words);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction failed for document {DocumentId}", doc.Id);
            Fail(doc, doc.PageCount);
        }
        finally
        {
            _running.TryRemove(doc.Id, out _);
        }
    }

    private async Task<ExtractionResult> OcrAndReExtract(Document doc, string path)
    {
        if (!_ocr.Available)
        {
            _logger.LogWarning("Document {DocumentId} has no text and OCR is unavailable", doc.Id);
            return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);
        }

        _logger.LogInformation("Document {DocumentId} has no text; running OCR", doc.Id);
        string ocrPath = path + ".ocr.pdf";
        // Surface page-by-page OCR progress to the client's "Preparing document…" bar.
        var progress = new Progress<double>(p => _documents.SetProgress(doc.Id, p));
        bool ok = await _ocr.TryOcrAsync(path, ocrPath, CancellationToken.None, progress);
        if (!ok) return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);

        var result = _pdf.Extract(ocrPath);
        // Keep the searchable PDF in place of the original so future re-uploads /
        // page rendering use the text-bearing version.
        try { File.Move(ocrPath, path, overwrite: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not replace original with OCR'd PDF for {Id}", doc.Id); }
        return result;
    }

    private void Fail(Document doc, int pageCount) =>
        _documents.SetExtraction(doc.Id,
            doc with { PageCount = pageCount, WordCount = 0, Status = DocumentStatus.Error },
            Array.Empty<Core.TextPipeline.SourceWord>());
}
