using System.Collections.Concurrent;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Api.Storage;

/// <summary>A document plus its extracted words.</summary>
public sealed class StoredDocument
{
    public required Document Document { get; set; }
    /// <summary>Mutable: starts empty while a document is <c>Extracting</c> and is
    /// filled once extraction (and any OCR fallback) completes.</summary>
    public IReadOnlyList<SourceWord> Words { get; set; } = Array.Empty<SourceWord>();
}

public interface IDocumentStore
{
    StoredDocument Add(Document document, IReadOnlyList<SourceWord> words);
    StoredDocument? Get(Guid id);
    /// <summary>Publish the extraction result (or failure): updates the document
    /// record and its words atomically once the background pipeline finishes.</summary>
    void SetExtraction(Guid id, Document document, IReadOnlyList<SourceWord> words);
    /// <summary>Update just the extraction/OCR progress (0..1) so the client's
    /// "Preparing document…" bar advances while a scanned PDF is OCR'd.</summary>
    void SetProgress(Guid id, double progress);
}

public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<Guid, StoredDocument> _docs = new();

    public StoredDocument Add(Document document, IReadOnlyList<SourceWord> words)
    {
        var stored = new StoredDocument { Document = document, Words = words };
        _docs[document.Id] = stored;
        return stored;
    }

    public StoredDocument? Get(Guid id) => _docs.GetValueOrDefault(id);

    public void SetExtraction(Guid id, Document document, IReadOnlyList<SourceWord> words)
    {
        if (!_docs.TryGetValue(id, out var stored)) return;
        stored.Document = document;
        stored.Words = words;
    }

    public void SetProgress(Guid id, double progress)
    {
        if (!_docs.TryGetValue(id, out var stored)) return;
        stored.Document = stored.Document with { Progress = Math.Clamp(progress, 0, 1) };
    }
}

/// <summary>A session plus its accumulated processed chunks and progress.</summary>
public sealed class StoredSession
{
    private readonly List<ProcessedChunk> _chunks = new();
    private readonly Lock _gate = new();

    public required TtsSession Session { get; set; }
    public int TotalChunks { get; set; }
    /// <summary>Source-word index where this session's synthesis begins.</summary>
    public int StartWordIndex { get; init; }
    public CancellationTokenSource Cancellation { get; } = new();

    public void AddChunk(ProcessedChunk chunk)
    {
        lock (_gate) _chunks.Add(chunk);
    }

    public IReadOnlyList<ProcessedChunk> SnapshotChunks()
    {
        lock (_gate) return _chunks.ToList();
    }

    public ProcessedChunk? GetChunk(int index)
    {
        lock (_gate) return _chunks.FirstOrDefault(c => c.ChunkIndex == index);
    }
}

public interface ISessionStore
{
    StoredSession Add(TtsSession session, int startWordIndex = 0);
    StoredSession? Get(Guid id);
    IReadOnlyList<StoredSession> ForDocument(Guid documentId);
    void Update(Guid id, Func<TtsSession, TtsSession> mutate);
    bool Remove(Guid id);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<Guid, StoredSession> _sessions = new();

    public StoredSession Add(TtsSession session, int startWordIndex = 0)
    {
        var stored = new StoredSession { Session = session, StartWordIndex = startWordIndex };
        _sessions[session.Id] = stored;
        return stored;
    }

    public StoredSession? Get(Guid id) => _sessions.GetValueOrDefault(id);

    public IReadOnlyList<StoredSession> ForDocument(Guid documentId) =>
        _sessions.Values.Where(s => s.Session.DocumentId == documentId).ToList();

    public void Update(Guid id, Func<TtsSession, TtsSession> mutate)
    {
        if (_sessions.TryGetValue(id, out var stored))
            stored.Session = mutate(stored.Session);
    }

    public bool Remove(Guid id) => _sessions.TryRemove(id, out _);
}
