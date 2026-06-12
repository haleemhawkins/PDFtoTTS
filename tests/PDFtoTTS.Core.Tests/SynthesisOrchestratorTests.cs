using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Orchestration;

namespace PDFtoTTS.Core.Tests;

public class SynthesisOrchestratorTests
{
    // A chunk of single-word tokens "w{n}" mapping 1:1 to source words.
    private static (IReadOnlyList<Chunk>, IReadOnlyList<SourceWord>) Build(int chunkCount)
    {
        var src = new List<SourceWord>();
        var chunks = new List<Chunk>();
        for (int i = 0; i < chunkCount; i++)
        {
            var word = new SourceWord(i, $"w{i}", Page: 1, Bbox: new BoundingBox(0, 0, 5, 5));
            src.Add(word);
            var token = new NormToken(0, word.Text, i, i, EndsSentence: true);
            chunks.Add(new Chunk(i, word.Text, i, i, 0, new[] { token }));
        }

        return (chunks, src);
    }

    // Aligner that fabricates one timing per transcript word.
    private sealed class FakeAligner : IForcedAligner
    {
        public Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken ct)
        {
            var words = request.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var aligned = words.Select((w, i) =>
                new AlignedWord(w, i * 100, (i + 1) * 100, 0.9f, false)).ToList();
            return Task.FromResult(new AlignmentResult(aligned, words.Length * 100));
        }
    }

    // Synthesizer that tracks concurrency and delays so that lower-index chunks
    // finish *last*, proving the orchestrator reorders rather than emitting by
    // completion time.
    private sealed class TrackingSynthesizer : ISpeechSynthesizer
    {
        private int _active;
        public int MaxConcurrent;
        private readonly int _total;
        public TrackingSynthesizer(int total) => _total = total;

        public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);

            int index = int.Parse(request.OutPath); // we pass index as out path below
            await Task.Delay(20 * (_total - index), ct); // chunk 0 is slowest

            Interlocked.Decrement(ref _active);
            return new SynthesisResult($"/data/audio/{index}.wav", 100);
        }
    }

    [Fact]
    public async Task Emits_chunks_in_order_despite_out_of_order_completion()
    {
        var (chunks, src) = Build(6);
        var orch = new SynthesisOrchestrator(new TrackingSynthesizer(6), new FakeAligner(), new ChunkMerger());

        var emitted = new List<int>();
        await foreach (var pc in orch.RunAsync(chunks, src,
            new PipelineOptions("v", 1f, "en", Concurrency: 3),
            outPath: i => i.ToString(), audioUrl: i => $"/audio/{i}"))
        {
            emitted.Add(pc.ChunkIndex);
        }

        Assert.Equal(Enumerable.Range(0, 6), emitted);
    }

    [Fact]
    public async Task Respects_the_concurrency_limit()
    {
        var (chunks, src) = Build(8);
        var synth = new TrackingSynthesizer(8);
        var orch = new SynthesisOrchestrator(synth, new FakeAligner(), new ChunkMerger());

        await foreach (var _ in orch.RunAsync(chunks, src,
            new PipelineOptions("v", 1f, "en", Concurrency: 2),
            outPath: i => i.ToString(), audioUrl: i => $"/audio/{i}"))
        {
        }

        Assert.True(synth.MaxConcurrent <= 2, $"max concurrency was {synth.MaxConcurrent}");
        Assert.True(synth.MaxConcurrent >= 2, "expected real parallelism");
    }

    [Fact]
    public async Task Produces_merged_word_data_per_chunk()
    {
        var (chunks, src) = Build(3);
        var orch = new SynthesisOrchestrator(new TrackingSynthesizer(3), new FakeAligner(), new ChunkMerger());

        var results = new List<ProcessedChunk>();
        await foreach (var pc in orch.RunAsync(chunks, src,
            new PipelineOptions("v", 1f, "en", Concurrency: 2),
            outPath: i => i.ToString(), audioUrl: i => $"/audio/{i}"))
        {
            results.Add(pc);
        }

        Assert.All(results, pc =>
        {
            Assert.Single(pc.Words);
            Assert.False(pc.Degraded);
            Assert.Equal($"/audio/{pc.ChunkIndex}", pc.AudioUrl);
        });
    }
}
