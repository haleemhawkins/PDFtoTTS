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
    string AudioRelativePath(Guid sessionId, int chunkIndex);
    string AudioFullPath(Guid sessionId, int chunkIndex);
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

    public string AudioRelativePath(Guid sessionId, int chunkIndex) =>
        Path.Combine("audio", sessionId.ToString(), $"{chunkIndex:D5}.wav");

    public string AudioFullPath(Guid sessionId, int chunkIndex) =>
        Path.Combine(_dataDir, AudioRelativePath(sessionId, chunkIndex));

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
}
