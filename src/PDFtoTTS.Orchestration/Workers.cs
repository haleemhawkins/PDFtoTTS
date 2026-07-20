namespace PDFtoTTS.Orchestration;

/// <summary>Request to synthesize one chunk of text.</summary>
public sealed record SynthesisRequest(string Text, string VoiceId, float Speed, string Language, string OutPath);

/// <summary>Result of synthesizing a chunk: where the WAV landed and how long it is.</summary>
public sealed record SynthesisResult(string AudioPath, long DurationMs);

/// <summary>One aligned word from the forced-alignment worker.</summary>
public sealed record AlignedWord(string Text, long StartMs, long EndMs, float Confidence, bool LowConfidence);

/// <summary>Result of force-aligning a transcript against audio.</summary>
public sealed record AlignmentResult(IReadOnlyList<AlignedWord> Words, long AudioDurationMs);

/// <summary>Request to align a known transcript against an audio file.</summary>
public sealed record AlignmentRequest(string AudioPath, string Transcript, string Language);

/// <summary>
/// Abstraction over the Kokoro TTS gRPC worker. The orchestrator depends on this
/// (not the generated client) so the pipeline is testable without a GPU.
/// </summary>
public interface ISpeechSynthesizer
{
    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct);
}

/// <summary>Abstraction over the WhisperX forced-alignment gRPC worker.</summary>
public interface IForcedAligner
{
    Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken ct);
}
