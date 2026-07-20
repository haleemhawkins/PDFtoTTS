using System.Text;

namespace PDFtoTTS.Api.Mock;

/// <summary>Reads the duration of a WAV file by parsing its RIFF chunks.</summary>
public static class WavReader
{
    public static long DurationMs(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);

        if (fs.Length < 12) return 0;
        r.ReadBytes(12); // "RIFF" + size + "WAVE"

        int byteRate = 0;
        long dataSize = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                var fmt = r.ReadBytes(size);
                if (fmt.Length >= 12) byteRate = BitConverter.ToInt32(fmt, 8); // byteRate @ offset 8
            }
            else if (id == "data")
            {
                dataSize = size;
                break;
            }
            else
            {
                fs.Seek(size, SeekOrigin.Current);
            }

            if ((size & 1) == 1) fs.Seek(1, SeekOrigin.Current); // word-align padding
        }

        return byteRate > 0 ? dataSize * 1000 / byteRate : 0;
    }
}
