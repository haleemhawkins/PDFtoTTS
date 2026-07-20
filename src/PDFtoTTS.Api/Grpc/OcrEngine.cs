using Grpc.Core;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Ingestion;
using OcrV1 = PDFtoTTS.Grpc.Ocr.V1;

namespace PDFtoTTS.Api.Grpc;

/// <summary>
/// Recognizes a scanned/image-only PDF into positioned words. Implemented by the
/// Surya GPU worker; <see cref="DocumentPipeline"/> falls back to ocrmypdf/Tesseract
/// when <see cref="Enabled"/> is false or a call fails.
/// </summary>
public interface IOcrEngine
{
    /// <summary>True when this engine should be attempted (a worker is configured).</summary>
    bool Enabled { get; }

    /// <summary>
    /// OCR the PDF at <paramref name="pdfRelPath"/> (relative to the shared data dir,
    /// as the worker resolves it under its own /data mount) into reading-order words
    /// with PDF user-space bounding boxes.
    /// </summary>
    Task<ExtractionResult> RecognizeAsync(string pdfRelPath, string language, CancellationToken ct);
}

/// <summary>No-op engine used when Surya is disabled (e.g. mock mode); always
/// reports unavailable so the pipeline goes straight to ocrmypdf.</summary>
public sealed class DisabledOcrEngine : IOcrEngine
{
    public bool Enabled => false;

    public Task<ExtractionResult> RecognizeAsync(string pdfRelPath, string language, CancellationToken ct) =>
        throw new InvalidOperationException("Surya OCR is disabled.");
}

/// <summary>Adapts the generated Surya OCR gRPC client to <see cref="IOcrEngine"/>.</summary>
public sealed class SuryaOcr : IOcrEngine
{
    private readonly OcrV1.Ocr.OcrClient _client;
    private readonly TimeSpan _timeout;

    public SuryaOcr(OcrV1.Ocr.OcrClient client, IConfiguration config)
    {
        _client = client;
        // OCR over a whole scanned book is the slowest single call in the system
        // (render + detect + recognize every page), so the deadline is generous, but
        // still bounded so a wedged GPU surfaces as an error instead of hanging.
        _timeout = TimeSpan.FromSeconds(config.GetValue("SURYA_TIMEOUT_SECONDS", 1800));
    }

    public bool Enabled => true;

    public async Task<ExtractionResult> RecognizeAsync(string pdfRelPath, string language, CancellationToken ct)
    {
        try
        {
            var resp = await _client.RecognizeAsync(
                new OcrV1.OcrRequest { PdfPath = pdfRelPath, Language = language ?? "" },
                deadline: DateTime.UtcNow.Add(_timeout), cancellationToken: ct);

            var words = new List<SourceWord>(resp.Words.Count);
            int i = 0;
            foreach (var w in resp.Words)
                words.Add(new SourceWord(i++, w.Text, w.Page,
                    new BoundingBox(w.X, w.Y, w.Width, w.Height)));

            return new ExtractionResult(words, resp.PageCount);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw WorkerCalls.Translate(ex, "OCR");
        }
    }
}
