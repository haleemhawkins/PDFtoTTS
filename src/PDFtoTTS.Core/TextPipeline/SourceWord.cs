using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// A word as extracted from the source document, before normalization. Carries
/// its global document <see cref="Index"/> and on-screen anchor; timing is added
/// later by the merge step. PDF words have a page + bbox; EPUB words instead
/// carry a <see cref="Locator"/> (spine href + ordinal) so the frontend can find
/// the matching injected span in the reflowed chapter.
/// </summary>
public sealed record SourceWord(
    int Index,
    string Text,
    int? Page = null,
    BoundingBox? Bbox = null,
    string? Locator = null);
