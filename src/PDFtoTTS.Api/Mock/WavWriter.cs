namespace PDFtoTTS.Api.Mock;

/// <summary>
/// Minimal 16-bit PCM WAV writer for the GPU-free mock TTS. Emits a fading tone
/// so the browser has real, decodable audio to play (and the sync engine has a
/// real duration to track) without a Kokoro model.
/// </summary>
public static class WavWriter
{
    public static void WriteTone(
        string path,
        long durationMs,
        double frequency = 196.0,
        int sampleRate = 24000,
        double amplitude = 0.18)
    {
        int sampleCount = (int)(durationMs * sampleRate / 1000);
        int dataSize = sampleCount * 2; // mono, 16-bit
        int byteRate = sampleRate * 2;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16); // fmt chunk size
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)2); // block align
        w.Write((short)16); // bits per sample
        w.Write("data"u8);
        w.Write(dataSize);

        double fadeSamples = sampleRate * 0.01; // 10ms fade to avoid clicks
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double)sampleRate;
            double env = Math.Min(1.0, Math.Min(i, sampleCount - i) / fadeSamples);
            double value = Math.Sin(2 * Math.PI * frequency * t) * amplitude * env;
            w.Write((short)(value * short.MaxValue));
        }
    }
}
