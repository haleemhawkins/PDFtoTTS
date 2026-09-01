using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PDFtoTTS.Api.Storage;
using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Api.Audio;

/// <summary>
/// Serves a session's audio as HLS — the format iOS plays natively (via AVPlayer),
/// which is what enables background / locked-screen playback and lock-screen
/// controls. Each synthesized WAV chunk becomes one AAC/MPEG-TS segment, transcoded
/// lazily on first request (ffmpeg) and cached on disk. The playlist is an EVENT
/// playlist built from the chunk durations the session already tracks, growing as
/// chunks are produced and finalized with `#EXT-X-ENDLIST` when synthesis ends.
/// </summary>
public sealed class HlsTranscoder
{
    private readonly IFileStorage _files;
    private readonly ILogger<HlsTranscoder> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public HlsTranscoder(IFileStorage files, ILogger<HlsTranscoder> logger)
    {
        _files = files;
        _logger = logger;
    }

    /// <summary>
    /// Drop and dispose per-segment locks for a session that is being torn down,
    /// so long-running hosts don't accumulate SemaphoreSlim entries forever.
    /// Safe to call after (or before) the session's audio directory is deleted.
    /// </summary>
    public void DropLocksForSession(Guid sessionId)
    {
        string prefix = Path.GetFullPath(Path.Combine(_files.AudioSessionDir(sessionId), "hls"))
            + Path.DirectorySeparatorChar;
        foreach (var key in _locks.Keys)
        {
            string full;
            try { full = Path.GetFullPath(key); }
            catch { continue; }
            // Match segment paths under this session's hls/ dir (or the dir itself).
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, prefix.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (_locks.TryRemove(key, out var gate))
            {
                try { gate.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Build the EVENT m3u8 from the contiguous produced chunks (prefix from 0).</summary>
    public string BuildPlaylist(StoredSession session)
    {
        var chunks = session.SnapshotChunks()
            .OrderBy(c => c.ChunkIndex)
            .ToList();

        // Only expose the contiguous prefix so the timeline never has a hole.
        var contiguous = new List<ProcessedChunk>();
        int expected = 0;
        foreach (var c in chunks)
        {
            if (c.ChunkIndex != expected) break;
            contiguous.Add(c);
            expected++;
        }

        long maxMs = contiguous.Count > 0 ? contiguous.Max(c => c.DurationMs) : 1;
        int target = Math.Max(1, (int)Math.Ceiling(maxMs / 1000.0));

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:EVENT\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n");
        foreach (var c in contiguous)
        {
            double sec = c.DurationMs / 1000.0;
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{sec.ToString("0.000", CultureInfo.InvariantCulture)},\n");
            sb.Append(CultureInfo.InvariantCulture, $"{c.ChunkIndex:D5}.ts\n");
        }

        var status = session.Session.Status;
        bool done = status == SessionStatus.Complete || status == SessionStatus.Error;
        // Finalize only once the contiguous run reaches the end of synthesis.
        if (done && (session.TotalChunks == 0 || contiguous.Count >= session.TotalChunks))
            sb.Append("#EXT-X-ENDLIST\n");

        return sb.ToString();
    }

    /// <summary>Block until the first segment exists (or synthesis ends / the client
    /// disconnects), so the player never loads an empty playlist and gives up.</summary>
    public async Task WaitForFirstSegmentAsync(StoredSession session, CancellationToken ct)
    {
        for (int i = 0; i < 300 && !ct.IsCancellationRequested; i++)
        {
            if (session.GetChunk(0) is not null) return;
            var st = session.Session.Status;
            if (st == SessionStatus.Complete || st == SessionStatus.Error) return;
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public string SegmentPath(Guid sessionId, int index) =>
        Path.Combine(_files.AudioSessionDir(sessionId), "hls", $"{index:D5}.ts");

    /// <summary>Transcode chunk {index} to an MPEG-TS/AAC segment if not already cached.
    /// Returns the segment path, or null if that chunk's WAV doesn't exist yet.</summary>
    public async Task<string?> EnsureSegmentAsync(Guid sessionId, int index, CancellationToken ct)
    {
        string wav = _files.AudioFullPath(sessionId, index);
        if (!File.Exists(wav)) return null;

        string ts = SegmentPath(sessionId, index);
        if (File.Exists(ts)) return ts;

        var gate = _locks.GetOrAdd(ts, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(ts)) return ts;
            Directory.CreateDirectory(Path.GetDirectoryName(ts)!);
            string tmp = ts + ".tmp";
            await TranscodeAsync(wav, tmp, ct);
            File.Move(tmp, ts, overwrite: true);
            return ts;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TranscodeAsync(string input, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // Mono AAC at 24 kHz (matches synthesis) in an MPEG-TS container for HLS.
        foreach (var a in new[]
        {
            "-nostdin", "-y", "-i", input,
            "-c:a", "aac", "-b:a", "64k", "-ac", "1", "-ar", "24000",
            "-muxdelay", "0", "-muxpreload", "0",
            "-f", "mpegts", output,
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ffmpeg");
        // Kill ffmpeg if the client disconnects or the session is torn down —
        // otherwise orphan processes hold file handles under a session dir that
        // may already have been deleted.
        await using var killReg = ct.Register(() => TryKill(proc));
        try
        {
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                TryDelete(output);
                _logger.LogError("ffmpeg failed ({Code}) for {Input}: {Err}", proc.ExitCode, input, stderr);
                throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            TryDelete(output);
            throw;
        }
        catch
        {
            TryKill(proc);
            TryDelete(output);
            throw;
        }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
