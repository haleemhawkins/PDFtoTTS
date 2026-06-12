using System.Runtime.CompilerServices;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Orchestration;

/// <summary>Per-run pipeline settings.</summary>
public sealed record PipelineOptions(string VoiceId, float Speed, string Language, int Concurrency = 2);

/// <summary>
/// Drives the per-chunk pipeline (synthesize → align → merge) with bounded
/// concurrency and in-order streaming output (design §5.4). Uses a sliding
/// window of in-flight tasks: at most <c>Concurrency</c> chunks are processed at
/// once, results are yielded strictly in <c>ChunkIndex</c> order, and a new chunk
/// is only started after the consumer pulls a finished one — giving real
/// consumer-driven backpressure rather than buffering the whole document.
/// </summary>
public sealed class SynthesisOrchestrator
{
    private readonly ISpeechSynthesizer _tts;
    private readonly IForcedAligner _aligner;
    private readonly ChunkMerger _merger;

    public SynthesisOrchestrator(ISpeechSynthesizer tts, IForcedAligner aligner, ChunkMerger merger)
    {
        _tts = tts;
        _aligner = aligner;
        _merger = merger;
    }

    public async IAsyncEnumerable<ProcessedChunk> RunAsync(
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<SourceWord> sourceWords,
        PipelineOptions options,
        Func<int, string> outPath,
        Func<int, string> audioUrl,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int window = Math.Max(1, options.Concurrency);
        var inflight = new Queue<Task<ProcessedChunk>>();
        int next = 0;

        // Start the FIRST chunk alone so it gets the whole worker and audio starts
        // ASAP (the first chunk is intentionally small); widen to the full window
        // afterwards so the remaining chunks synthesize in parallel.
        if (next < chunks.Count)
            inflight.Enqueue(ProcessAsync(chunks[next++], sourceWords, options, outPath, audioUrl, ct));

        while (inflight.Count > 0)
        {
            // FIFO + chunks started in index order ⇒ in-order results.
            var result = await inflight.Dequeue().ConfigureAwait(false);
            yield return result;

            // Refill up to the concurrency window (after the first chunk this opens
            // the pipeline to parallel synthesis; backpressure caps it at `window`).
            while (next < chunks.Count && inflight.Count < window)
                inflight.Enqueue(ProcessAsync(chunks[next++], sourceWords, options, outPath, audioUrl, ct));
        }
    }

    private async Task<ProcessedChunk> ProcessAsync(
        Chunk chunk,
        IReadOnlyList<SourceWord> sourceWords,
        PipelineOptions options,
        Func<int, string> outPath,
        Func<int, string> audioUrl,
        CancellationToken ct)
    {
        var synth = await _tts.SynthesizeAsync(
            new SynthesisRequest(chunk.Text, options.VoiceId, options.Speed, options.Language, outPath(chunk.Index)),
            ct).ConfigureAwait(false);

        var alignment = await _aligner.AlignAsync(
            new AlignmentRequest(synth.AudioPath, chunk.Text, options.Language),
            ct).ConfigureAwait(false);

        return _merger.Merge(chunk, alignment, sourceWords, audioUrl(chunk.Index));
    }
}
