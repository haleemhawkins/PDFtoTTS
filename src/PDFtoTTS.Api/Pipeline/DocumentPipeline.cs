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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

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
    public void Start(Document doc, string originalRelPath)
    {
        // Replace any prior CTS for this id (resume after restart, or rare re-start).
        var cts = new CancellationTokenSource();
        if (_tokens.TryRemove(doc.Id, out var old))
        {
            try { old.Cancel(); } catch { /* best effort */ }
            old.Dispose();
        }
        _tokens[doc.Id] = cts;
        _running[doc.Id] = Task.Run(() => RunAsync(doc, originalRelPath, cts));
    }

    /// <summary>Cancel in-flight extraction/OCR for a document (e.g. on delete).
    /// Safe if nothing is running.</summary>
    public void Cancel(Guid documentId)
    {
        if (_tokens.TryRemove(documentId, out var cts))
        {
            try { cts.Cancel(); } catch { /* best effort */ }
            cts.Dispose();
        }
    }

    /// <summary>
    /// After a store reload, re-queue any document left in Extracting/Queued whose
    /// original is still on disk. Without this, a crash mid-extraction leaves the
    /// catalogue entry stuck forever even though the file is recoverable.
    /// </summary>
    public void ResumeIncomplete()
    {
        foreach (var doc in _documents.All())
        {
            if (doc.Status is not (DocumentStatus.Extracting or DocumentStatus.Queued))
                continue;
            if (!File.Exists(_files.OriginalFullPath(doc.Id, doc.Type)))
            {
                _logger.LogWarning("Document {DocumentId} is {Status} but original missing; marking Error",
                    doc.Id, doc.Status);
                Fail(doc, doc.PageCount);
                continue;
            }
            string rel = Path.Combine("originals",
                $"{doc.Id}{FileTypeDetector.Extension(doc.Type)}");
            _logger.LogInformation("Resuming extraction for document {DocumentId} ({Status})",
                doc.Id, doc.Status);
            // Ensure status is Extracting so clients keep polling the progress bar.
            if (doc.Status != DocumentStatus.Extracting)
                _documents.SetExtraction(doc.Id, doc with { Status = DocumentStatus.Extracting },
                    Array.Empty<Core.TextPipeline.SourceWord>());
            Start(doc with { Status = DocumentStatus.Extracting }, rel);
        }
    }

    private async Task RunAsync(Document doc, string originalRelPath, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Document may have been deleted between Start and here.
            if (_documents.Get(doc.Id) is null) return;

            string path = _files.FullPath(originalRelPath);
            ExtractionResult result = doc.Type == DocumentType.Epub
                ? _epub.Extract(path)
                : _pdf.Extract(path);

            ct.ThrowIfCancellationRequested();

            // A PDF with pages but no words is a scanned/image PDF: OCR it.
            if (doc.Type == DocumentType.Pdf && result.Words.Count == 0 && result.PageCount > 0)
                result = await OcrScanned(doc, originalRelPath, path, ct);

            ct.ThrowIfCancellationRequested();
            if (_documents.Get(doc.Id) is null) return;

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
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Extraction cancelled for document {DocumentId}", doc.Id);
            // If the document is still in the store (cancel without delete), leave
            // it Extracting so ResumeIncomplete / a later retry can pick it up; if
            // it was deleted, SetExtraction is a no-op.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction failed for document {DocumentId}", doc.Id);
            if (_documents.Get(doc.Id) is not null)
                Fail(doc, doc.PageCount);
        }
        finally
        {
            _running.TryRemove(doc.Id, out _);
            // Drop our CTS only if a newer Start hasn't replaced it.
            if (_tokens.TryGetValue(doc.Id, out var current) && ReferenceEquals(current, cts)
                && _tokens.TryRemove(doc.Id, out var removed))
            {
                try { removed.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    // OCR a scanned PDF. Prefer the Surya GPU engine (far higher accuracy + reading
    // order, returns positioned words directly); fall back to ocrmypdf/Tesseract when
    // Surya is disabled or unreachable so scans still work without the GPU worker.
    private async Task<ExtractionResult> OcrScanned(Document doc, string relPath, string path,
        CancellationToken ct)
    {
        if (_surya.Enabled)
        {
            try
            {
                _logger.LogInformation("Document {DocumentId} has no text; running Surya OCR", doc.Id);
                var result = await _surya.RecognizeAsync(relPath, "en", ct);
                if (result.Words.Count > 0)
                {
                    _logger.LogInformation("Surya OCR produced {Words} words for {DocumentId}",
                        result.Words.Count, doc.Id);
                    return result;
                }
                _logger.LogWarning("Surya OCR returned no words for {DocumentId}; trying ocrmypdf", doc.Id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Surya OCR failed for {DocumentId}; falling back to ocrmypdf", doc.Id);
            }
        }

        return await OcrmypdfAndReExtract(doc, path, ct);
    }

    private async Task<ExtractionResult> OcrmypdfAndReExtract(Document doc, string path,
        CancellationToken ct)
    {
        if (!_ocr.Available)
        {
            _logger.LogWarning("Document {DocumentId} has no text and OCR is unavailable", doc.Id);
            return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);
        }

        _logger.LogInformation("Document {DocumentId}: running ocrmypdf", doc.Id);
        string ocrPath = path + ".ocr.pdf";
        try
        {
            // Surface page-by-page OCR progress to the client's "Preparing document…" bar.
            var progress = new Progress<double>(p => _documents.SetProgress(doc.Id, p));
            bool ok = await _ocr.TryOcrAsync(path, ocrPath, ct, progress);
            if (!ok) return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);

            ct.ThrowIfCancellationRequested();
            // Don't recreate a deleted original: if the doc is gone (or cancelled),
            // drop the OCR temp and stop without File.Move into originals/.
            if (_documents.Get(doc.Id) is null || ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                return new ExtractionResult(Array.Empty<Core.TextPipeline.SourceWord>(), 0);
            }

            var result = _pdf.Extract(ocrPath);
            // Keep the searchable PDF in place of the original so future re-uploads /
            // page rendering use the text-bearing version.
            try { File.Move(ocrPath, path, overwrite: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not replace original with OCR'd PDF for {Id}", doc.Id);
            }
            return result;
        }
        finally
        {
            TryDelete(ocrPath);
        }
    }

    private void Fail(Document doc, int pageCount) =>
        _documents.SetExtraction(doc.Id,
            doc with { PageCount = pageCount, WordCount = 0, Status = DocumentStatus.Error },
            Array.Empty<Core.TextPipeline.SourceWord>());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
