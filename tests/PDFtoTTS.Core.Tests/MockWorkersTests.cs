using System.Text;
using PDFtoTTS.Api.Mock;
using PDFtoTTS.Api.Storage;
using PDFtoTTS.Orchestration;

namespace PDFtoTTS.Core.Tests;

public class MockWorkersTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "pdftotts-mock-" + Guid.NewGuid());

    [Fact]
    public async Task Mock_synth_writes_a_valid_wav()
    {
        var files = new LocalFileStorage(TempDir());
        var synth = new MockSynthesizer(files);

        var result = await synth.SynthesizeAsync(
            new SynthesisRequest("one two three", "v", 1f, "en", "audio/s/00000.wav"),
            CancellationToken.None);

        Assert.Equal(1050, result.DurationMs); // 3 words × 350ms
        var path = files.FullPath("audio/s/00000.wav");
        Assert.True(File.Exists(path));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        // 16-bit mono PCM at 24kHz; 1.05s → 25200 samples → 50400 data bytes.
        Assert.Equal((short)1, BitConverter.ToInt16(bytes, 22)); // channels
        Assert.Equal(24000, BitConverter.ToInt32(bytes, 24)); // sample rate
        Assert.Equal(50400 + 44, bytes.Length);
    }

    [Fact]
    public async Task Mock_aligner_distributes_words_across_real_audio_duration()
    {
        var files = new LocalFileStorage(TempDir());
        // The synth writes the actual WAV; the aligner must read its real length.
        await new MockSynthesizer(files).SynthesizeAsync(
            new SynthesisRequest("one two three", "v", 1f, "en", "audio/s/00000.wav"),
            CancellationToken.None);

        var align = await new MockAligner(files).AlignAsync(
            new AlignmentRequest("audio/s/00000.wav", "one two three", "en"), CancellationToken.None);

        Assert.Equal(1050, align.AudioDurationMs); // read back from the WAV
        Assert.Equal(new[] { "one", "two", "three" }, align.Words.Select(w => w.Text));
        Assert.Equal(0, align.Words[0].StartMs);
        Assert.Equal(1050, align.Words[^1].EndMs);
        for (int i = 1; i < align.Words.Count; i++)
            Assert.True(align.Words[i].StartMs >= align.Words[i - 1].EndMs);
    }
}
