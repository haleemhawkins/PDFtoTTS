using PDFtoTTS.Api.Documents;
using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Api.Storage;

/// <summary>
/// Owns the shared-volume layout (design §1.4/§5.6). Paths handed to workers are
/// relative to the data root so they are mount-point independent; the API uses
/// the absolute form to serve audio.
/// </summary>
public interface IFileStorage
{
    Task<string> SaveOriginalAsync(Guid documentId, string extension, Stream content, CancellationToken ct);
    /// <summary>
    /// Stream an upload straight to a temp file under originals/, sniff magic bytes,
    /// then rename into place. Avoids buffering the whole body in managed heap
    /// (uploads can be hundreds of MB). Returns null type for unsupported content
    /// (temp file is deleted).
    /// </summary>
    Task<(string? RelativePath, DocumentType? Type)> SaveUploadAsync(
        Guid documentId, Stream content, CancellationToken ct);
    string AudioRelativePath(Guid sessionId, int chunkIndex);
    string AudioFullPath(Guid sessionId, int chunkIndex);
    /// <summary>Absolute path to a session's audio directory (chunks + HLS segments).</summary>
    string AudioSessionDir(Guid sessionId);
    /// <summary>Absolute path to a document's stored original (for serving/deletion).</summary>
    string OriginalFullPath(Guid documentId, DocumentType type);
    /// <summary>Delete a document's stored original; no-op if it's already gone.</summary>
    void DeleteOriginal(Guid documentId, DocumentType type);
    /// <summary>Resolve a path relative to the data root to an absolute path, creating its directory.</summary>
    string FullPath(string relativePath);
    void DeleteSessionAudio(Guid sessionId);
}

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _dataDir;

    public LocalFileStorage(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(Path.Combine(_dataDir, "originals"));
        Directory.CreateDirectory(Path.Combine(_dataDir, "audio"));
    }

    public async Task<string> SaveOriginalAsync(Guid documentId, string extension, Stream content, CancellationToken ct)
    {
        string rel = Path.Combine("originals", $"{documentId}{extension}");
        string full = Path.Combine(_dataDir, rel);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return rel;
    }

    public async Task<(string? RelativePath, DocumentType? Type)> SaveUploadAsync(
        Guid documentId, Stream content, CancellationToken ct)
    {
        string tempRel = Path.Combine("originals", $"{documentId}.upload");
        string tempFull = Path.Combine(_dataDir, tempRel);
        try
        {
            await using (var fs = File.Create(tempFull))
                await content.CopyToAsync(fs, ct);

            // Sniff magic bytes from the on-disk file (PDF / EPUB zip header).
            byte[] head = new byte[512];
            int n;
            await using (var fs = File.OpenRead(tempFull))
                n = await fs.ReadAsync(head.AsMemory(0, head.Length), ct);

            var type = FileTypeDetector.Detect(head.AsSpan(0, n));
            if (type is null)
            {
                TryDelete(tempFull);
                return (null, null);
            }

            string rel = Path.Combine("originals",
                $"{documentId}{FileTypeDetector.Extension(type.Value)}");
            string full = Path.Combine(_dataDir, rel);
            File.Move(tempFull, full, overwrite: true);
            return (rel, type);
        }
        catch
        {
            TryDelete(tempFull);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    public string AudioRelativePath(Guid sessionId, int chunkIndex) =>
        Path.Combine("audio", sessionId.ToString(), $"{chunkIndex:D5}.wav");

    public string AudioFullPath(Guid sessionId, int chunkIndex) =>
        Path.Combine(_dataDir, AudioRelativePath(sessionId, chunkIndex));

    public string AudioSessionDir(Guid sessionId) =>
        Path.Combine(_dataDir, "audio", sessionId.ToString());

    public string OriginalFullPath(Guid documentId, DocumentType type) =>
        Path.Combine(_dataDir, "originals", $"{documentId}{FileTypeDetector.Extension(type)}");

    public void DeleteOriginal(Guid documentId, DocumentType type)
    {
        string path = OriginalFullPath(documentId, type);
        if (File.Exists(path)) File.Delete(path);
    }

    public string FullPath(string relativePath)
    {
        string full = Path.Combine(_dataDir, relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return full;
    }

    public void DeleteSessionAudio(Guid sessionId)
    {
        string dir = Path.Combine(_dataDir, "audio", sessionId.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Delete every session's audio (WAV chunks + HLS segments). Sessions
    /// live only in memory, so audio left on the volume by a previous process is
    /// unreachable — call this once at startup so it can't accumulate forever.</summary>
    public void PurgeAllSessionAudio()
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(_dataDir, "audio")))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* in use / already gone — best effort */ }
        }
    }
}
