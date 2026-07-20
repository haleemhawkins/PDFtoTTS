namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// A spoken token produced by normalization. <see cref="SourceStart"/> and
/// <see cref="SourceEnd"/> are the inclusive range of source word indices this
/// token derives from (equal for the common 1→N case where one source word
/// expands to several tokens; a range for the N→1 case). <see cref="Index"/> is
/// the token's position in the normalized stream. <see cref="EndsSentence"/>
/// marks a sentence boundary used as a chunking hint. <see cref="Terminator"/>
/// is the sentence-ending mark ('.', '?' or '!') stripped from the source word,
/// re-attached to the chunk text so the voice gets period/question intonation and
/// a natural pause; '\0' when unknown (callers fall back to '.').
/// </summary>
public sealed record NormToken(
    int Index,
    string Text,
    int SourceStart,
    int SourceEnd,
    bool EndsSentence = false,
    char Terminator = '\0');
