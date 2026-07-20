using Grpc.Core;
using PDFtoTTS.Orchestration;
using KokoroV1 = PDFtoTTS.Grpc.Kokoro.V1;
using AlignV1 = PDFtoTTS.Grpc.Alignment.V1;

namespace PDFtoTTS.Api.Grpc;

/// <summary>
/// Raised when a GPU worker is unreachable or doesn't respond in time. Carries a
/// reader-facing message: the pipeline surfaces <see cref="Exception.Message"/> as
/// the session's <c>Error</c> event, so this is what the user sees in the banner.
/// </summary>
public sealed class WorkerUnavailableException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Translates a gRPC failure into a friendly <see cref="WorkerUnavailableException"/>.
/// Without a deadline, a worker hung on a wedged GPU (the AMD suspend/resume failure
/// mode) never returns and the reader sits at 0% forever with no error. A deadline
/// turns that into a surfaced error the user can act on (restart the workers / retry).
/// </summary>
internal static class WorkerCalls
{
    public static WorkerUnavailableException Translate(RpcException ex, string worker) =>
        ex.StatusCode switch
        {
            StatusCode.DeadlineExceeded => new WorkerUnavailableException(
                $"The {worker} worker didn't respond in time — its GPU may have stalled " +
                "(e.g. after the machine slept). Restart the workers and try again.", ex),
            StatusCode.Unavailable => new WorkerUnavailableException(
                $"Can't reach the {worker} worker — it may be down. " +
                "Restart the workers and try again.", ex),
            _ => new WorkerUnavailableException(
                $"The {worker} worker failed: {ex.Status.Detail}", ex),
        };
}

/// <summary>Adapts the generated Kokoro gRPC client to <see cref="ISpeechSynthesizer"/>.</summary>
public sealed class KokoroSynthesizer : ISpeechSynthesizer
{
    private readonly KokoroV1.KokoroTts.KokoroTtsClient _client;
    private readonly TimeSpan _timeout;

    public KokoroSynthesizer(KokoroV1.KokoroTts.KokoroTtsClient client, IConfiguration config)
    {
        _client = client;
        // Generous vs. a slow chunk (GPU synth is ~8s) but bounded so a wedged worker
        // surfaces as an error instead of hanging the session forever.
        _timeout = TimeSpan.FromSeconds(config.GetValue("KOKORO_TIMEOUT_SECONDS", 120));
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        try
        {
            var resp = await _client.SynthesizeAsync(new KokoroV1.SynthesizeRequest
            {
                Text = request.Text,
                VoiceId = request.VoiceId,
                Speed = request.Speed,
                Language = request.Language,
                OutPath = request.OutPath
            }, deadline: DateTime.UtcNow.Add(_timeout), cancellationToken: ct);

            return new SynthesisResult(resp.AudioPath, (long)Math.Round(resp.DurationSeconds * 1000));
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw WorkerCalls.Translate(ex, "speech");
        }
    }
}

/// <summary>Adapts the generated WhisperX gRPC client to <see cref="IForcedAligner"/>.</summary>
public sealed class WhisperxAligner : IForcedAligner
{
    private readonly AlignV1.Alignment.AlignmentClient _client;
    private readonly TimeSpan _timeout;

    public WhisperxAligner(AlignV1.Alignment.AlignmentClient client, IConfiguration config)
    {
        _client = client;
        _timeout = TimeSpan.FromSeconds(config.GetValue("WHISPERX_TIMEOUT_SECONDS", 90));
    }

    public async Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken ct)
    {
        try
        {
            var resp = await _client.AlignAsync(new AlignV1.AlignRequest
            {
                AudioPath = request.AudioPath,
                Transcript = request.Transcript,
                Language = request.Language
            }, deadline: DateTime.UtcNow.Add(_timeout), cancellationToken: ct);

            var words = resp.Words
                .Select(w => new AlignedWord(w.Text, w.StartMs, w.EndMs, w.Confidence, w.LowConfidence))
                .ToList();

            return new AlignmentResult(words, resp.AudioDurationMs);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw WorkerCalls.Translate(ex, "alignment");
        }
    }
}
