using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Ingestion;

/// <summary>Words extracted from a document, in reading order, plus a page count.</summary>
public sealed record ExtractionResult(IReadOnlyList<SourceWord> Words, int PageCount);
