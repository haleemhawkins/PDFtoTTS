using PDFtoTTS.Orchestration;
using KokoroV1 = PDFtoTTS.Grpc.Kokoro.V1;
using AlignV1 = PDFtoTTS.Grpc.Alignment.V1;

namespace PDFtoTTS.Api.Grpc;

/// <summary>Adapts the generated Kokoro gRPC client to <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class KokoroSynthesizer : ISpeechSynthesizer
{
    private readonly KokoroV1.KokoroTts.KokoroTtsClient _client;

    public KokoroSynthesizer(KokoroV1.KokoroTts.KokoroTtsClient client) => _client = client;

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        var resp = await _client.SynthesizeAsync(new KokoroV1.SynthesizeRequest
        {
            Text = request.Text,
            VoiceId = request.VoiceId,
            Speed = request.Speed,
            Language = request.Language,
            OutPath = request.OutPath
        }, cancellationToken: ct);

        return new SynthesisResult(resp.AudioPath, (long)Math.Round(resp.DurationSeconds * 1000));
    }
}

/// <summary>Adapts the generated WhisperX gRPC client to <see cref="IForcedAligner"/>.</summary>
public sealed class WhisperxAligner : IForcedAligner
{
    private readonly AlignV1.Alignment.AlignmentClient _client;

    public WhisperxAligner(AlignV1.Alignment.AlignmentClient client) => _client = client;

    public async Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken ct)
    {
        var resp = await _client.AlignAsync(new AlignV1.AlignRequest
        {
            AudioPath = request.AudioPath,
            Transcript = request.Transcript,
            Language = request.Language
        }, cancellationToken: ct);

        var words = resp.Words
            .Select(w => new AlignedWord(w.Text, w.StartMs, w.EndMs, w.Confidence, w.LowConfidence))
            .ToList();

        return new AlignmentResult(words, resp.AudioDurationMs);
    }
}
