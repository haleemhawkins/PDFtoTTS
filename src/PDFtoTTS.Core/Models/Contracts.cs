namespace PDFtoTTS.Core.Models;

/// <summary>Source format of an uploaded document.</summary>
public enum DocumentType
{
    Pdf,
    Epub
}

/// <summary>Lifecycle of an uploaded document's text extraction.</summary>
public enum DocumentStatus
{
    Queued,
    Extracting,
    Ready,
    Error
}

/// <summary>Lifecycle of a TTS render session.</summary>
public enum SessionStatus
{
    Queued,
    Processing,
    Streaming,
    Complete,
    Error
}

/// <summary>
/// A word's box in PDF user space (origin bottom-left). Null for reflowable
/// EPUB, where on-screen position is resolved client-side.
/// </summary>
public readonly record struct BoundingBox(double X, double Y, double Width, double Height);

/// <summary>
/// A single source word: its on-screen anchor (page + bbox) and, once aligned,
/// its spoken time window. <see cref="Index"/> is the global, document-wide word
/// index that the frontend uses to map highlights back to rendered words.
/// </summary>
public sealed record WordData(
    int Index,
    string Text,
    long StartMs,
    long EndMs,
    int? Page,
    BoundingBox? Bbox,
    float Confidence = 1f);

/// <summary>
/// One synthesized + aligned chunk pushed to the client. <see cref="Degraded"/>
/// marks chunks whose timings were partly interpolated (low confidence or a
/// token-count mismatch) rather than fully aligned.
/// </summary>
public sealed record ProcessedChunk(
    int ChunkIndex,
    string AudioUrl,
    long DurationMs,
    IReadOnlyList<WordData> Words,
    bool Degraded = false);

/// <summary>An uploaded document and its extraction status.</summary>
public sealed record Document(
    Guid Id,
    string Filename,
    DocumentType Type,
    int PageCount,
    int WordCount,
    DocumentStatus Status);

/// <summary>A TTS render session over a document with chosen voice/speed.</summary>
public sealed record TtsSession(
    Guid Id,
    Guid DocumentId,
    string Voice,
    float Speed,
    string Language,
    SessionStatus Status,
    double Progress);

/// <summary>A selectable TTS voice (mirrors the gRPC Voice message).</summary>
public sealed record Voice(string Id, string Label, string Language, string Gender);
