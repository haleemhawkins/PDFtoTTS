using System.Collections.Concurrent;
using PDFtoTTS.Api.Documents;
using PDFtoTTS.Api.Grpc;
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
    private readonly IOcrEngine _surya;
    private readonly ILogger<DocumentPipeline> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public DocumentPipeline(
        IDocumentStore documents,
        IFileStorage files,
        PdfExtractor pdf,
        EpubExtractor epub,
        PdfOcr ocr,
        IOcrEngine surya,
        ILogger<DocumentPipeline> logger)
    {
        _documents = documents;
        _files = files;
        _pdf = pdf;
        _epub = epub;
        _ocr = ocr;
        _surya = surya;
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

            // A PDF with pages but no words is a scanned/image PDF: OCR it.
            if (doc.Type == DocumentType.Pdf && result.Words.Count == 0 && result.PageCount > 0)
                result = await OcrScanned(doc, originalRelPath, path);

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

    // OCR a scanned PDF. Prefer the Surya GPU engine (far higher accuracy + reading
    // order, returns positioned words directly); fall back to ocrmypdf/Tesseract when
    // Surya is disabled or unreachable so scans still work without the GPU worker.
    private async Task<ExtractionResult> OcrScanned(Document doc, string relPath, string path)
    {
        if (_surya.Enabled)
        {
            try
            {
                _logger.LogInformation("Document {DocumentId} has no text; running Surya OCR", doc.Id);
                var result = await _surya.RecognizeAsync(relPath, "en", CancellationToken.None);
                if (result.Words.Count > 0)
                {
                    _logger.LogInformation("Surya OCR produced {Words} words for {DocumentId}",
                        result.Words.Count, doc.Id);
                    return result;
                }
                _logger.LogWarning("Surya OCR returned no words for {DocumentId}; trying ocrmypdf", doc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Surya OCR failed for {DocumentId}; falling back to ocrmypdf", doc.Id);
            }
        }

        return await OcrmypdfAndReExtract(doc, path);
    }

    private async Task<ExtractionResult> OcrmypdfAndReExtract(Document doc, string path)
    {
        if (!_ocr.Available)
        {
            _logger.LogWarning("Document {DocumentId} has no text and OCR is unavailable", doc.Id);
            return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);
        }

        _logger.LogInformation("Document {DocumentId}: running ocrmypdf", doc.Id);
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
