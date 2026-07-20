using PDFtoTTS.Api.Storage;
using PDFtoTTS.Orchestration;

namespace PDFtoTTS.Api.Mock;

/// <summary>
/// In-process fake workers for GPU-free end-to-end testing (USE_MOCK_WORKERS).
/// The synthesizer writes an audible tone WAV; the aligner distributes word
/// timings evenly across the same duration. Both derive duration from the word
/// count with the same formula so audio length and word timings stay in sync.
/// </summary>
internal static class MockTiming
{
    public static long DurationMs(int wordCount) => Math.Max(600, wordCount * 350L);
}

public sealed class MockSynthesizer : ISpeechSynthesizer
{
    private readonly IFileStorage _files;

    public MockSynthesizer(IFileStorage files) => _files = files;

    public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        int words = request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        long durationMs = MockTiming.DurationMs(words);
        WavWriter.WriteTone(_files.FullPath(request.OutPath), durationMs);
        return Task.FromResult(new SynthesisResult(request.OutPath, durationMs));
    }
}

public sealed class MockAligner : IForcedAligner
{
    private readonly IFileStorage _files;

    public MockAligner(IFileStorage files) => _files = files;

    public Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken ct)
    {
        var words = request.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Use the real audio duration so timings stay in sync with whatever
        // produced the WAV (real Kokoro in hybrid mode, or the mock tone).
        string full = _files.FullPath(request.AudioPath);
        long durationMs = File.Exists(full) ? WavReader.DurationMs(full) : MockTiming.DurationMs(words.Length);
        if (durationMs <= 0) durationMs = MockTiming.DurationMs(words.Length);
        int n = words.Length;

        var aligned = new List<AlignedWord>(n);
        for (int i = 0; i < n; i++)
        {
            long start = (long)((double)i / n * durationMs);
            long end = (long)((double)(i + 1) / n * durationMs);
            aligned.Add(new AlignedWord(words[i], start, end, 1f, false));
        }

        return Task.FromResult(new AlignmentResult(aligned, durationMs));
    }
}
