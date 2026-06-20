using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>All documents, most-recently-added first (for the library view).</summary>
    IReadOnlyList<Document> All();
    /// <summary>Publish the extraction result (or failure): updates the document
    /// record and its words atomically once the background pipeline finishes.</summary>
    void SetExtraction(Guid id, Document document, IReadOnlyList<SourceWord> words);
    /// <summary>Update just the extraction/OCR progress (0..1) so the client's
    /// "Preparing document…" bar advances while a scanned PDF is OCR'd.</summary>
    void SetProgress(Guid id, double progress);
    /// <summary>Change a document's display name; returns the updated record or null.</summary>
    Document? Rename(Guid id, string name);
    /// <summary>Update the reader's resume position (last-writer-wins by
    /// <see cref="ReadingPosition.UpdatedAtMs"/>, so a stale tab can't clobber a
    /// newer device); returns the updated record, or null if unknown.</summary>
    Document? SetPosition(Guid id, ReadingPosition position);
    /// <summary>Remove a document and its persisted words; returns the removed record.</summary>
    StoredDocument? Remove(Guid id);
}

public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StoredDocument> _docs = new();
    private readonly List<Guid> _order = new(); // insertion order; All() reverses it

    public StoredDocument Add(Document document, IReadOnlyList<SourceWord> words)
    {
        var stored = new StoredDocument { Document = document, Words = words };
        lock (_gate)
        {
            if (!_docs.ContainsKey(document.Id)) _order.Add(document.Id);
            _docs[document.Id] = stored;
        }
        return stored;
    }

    public StoredDocument? Get(Guid id)
    {
        lock (_gate) return _docs.GetValueOrDefault(id);
    }

    public IReadOnlyList<Document> All()
    {
        lock (_gate)
            return _order.AsEnumerable().Reverse()
                .Select(id => _docs[id].Document).ToList();
    }

    public void SetExtraction(Guid id, Document document, IReadOnlyList<SourceWord> words)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return;
            stored.Document = document;
            stored.Words = words;
        }
    }

    public void SetProgress(Guid id, double progress)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return;
            stored.Document = stored.Document with { Progress = Math.Clamp(progress, 0, 1) };
        }
    }

    public Document? Rename(Guid id, string name)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return null;
            stored.Document = stored.Document with { Filename = name };
            return stored.Document;
        }
    }

    public Document? SetPosition(Guid id, ReadingPosition position)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return null;
            var cur = stored.Document.Position;
            if (cur != null && position.UpdatedAtMs < cur.UpdatedAtMs) return stored.Document; // stale
            stored.Document = stored.Document with { Position = position };
            return stored.Document;
        }
    }

    public StoredDocument? Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_docs.Remove(id, out var stored)) return null;
            _order.Remove(id);
            return stored;
        }
    }
}

/// <summary>
/// Document store backed by a JSON catalogue on the shared volume so the library
/// survives API restarts. The catalogue (`catalogue.json`) holds the lightweight
/// <see cref="Document"/> records in insertion order; each document's extracted
/// words live in `words/{id}.json` and are loaded lazily on first access and held
/// in memory thereafter. Synthesized audio is deliberately NOT part of this store
/// — it stays per-session and on-the-fly. All mutations write through to disk
/// (atomic temp-file swap) under a single lock.
/// </summary>
public sealed class PersistentDocumentStore : IDocumentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly Dictionary<Guid, StoredDocument> _docs = new();
    private readonly List<Guid> _order = new();
    private readonly IFileStorage _files;
    private readonly ILogger<PersistentDocumentStore> _logger;

    public PersistentDocumentStore(IFileStorage files, ILogger<PersistentDocumentStore> logger)
    {
        _files = files;
        _logger = logger;
        Load();
    }

    public StoredDocument Add(Document document, IReadOnlyList<SourceWord> words)
    {
        var stored = new StoredDocument { Document = document, Words = words };
        lock (_gate)
        {
            if (!_docs.ContainsKey(document.Id)) _order.Add(document.Id);
            _docs[document.Id] = stored;
            if (words.Count > 0) SaveWords(document.Id, words);
            SaveCatalogue();
        }
        return stored;
    }

    public StoredDocument? Get(Guid id)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return null;
            // Words are loaded lazily: a catalogue entry read at startup has none in
            // memory until first opened. Hydrate from disk on demand.
            if (stored.Words.Count == 0 && stored.Document.WordCount > 0)
                stored.Words = LoadWords(id);
            return stored;
        }
    }

    public IReadOnlyList<Document> All()
    {
        lock (_gate)
            return _order.AsEnumerable().Reverse()
                .Select(id => _docs[id].Document).ToList();
    }

    public void SetExtraction(Guid id, Document document, IReadOnlyList<SourceWord> words)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return;
            stored.Document = document;
            stored.Words = words;
            if (words.Count > 0) SaveWords(id, words);
            SaveCatalogue();
        }
    }

    public void SetProgress(Guid id, double progress)
    {
        // Progress ticks are transient and frequent (per OCR page) — keep them in
        // memory only; the terminal status is persisted via SetExtraction.
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return;
            stored.Document = stored.Document with { Progress = Math.Clamp(progress, 0, 1) };
        }
    }

    public Document? Rename(Guid id, string name)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return null;
            stored.Document = stored.Document with { Filename = name };
            SaveCatalogue();
            return stored.Document;
        }
    }

    public Document? SetPosition(Guid id, ReadingPosition position)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var stored)) return null;
            var cur = stored.Document.Position;
            if (cur != null && position.UpdatedAtMs < cur.UpdatedAtMs) return stored.Document; // stale write
            stored.Document = stored.Document with { Position = position };
            SaveCatalogue();
            return stored.Document;
        }
    }

    public StoredDocument? Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_docs.Remove(id, out var stored)) return null;
            _order.Remove(id);
            TryDelete(WordsPath(id));
            SaveCatalogue();
            return stored;
        }
    }

    // --- persistence ---------------------------------------------------------

    private string CataloguePath => _files.FullPath("catalogue.json");
    private string WordsPath(Guid id) => _files.FullPath(Path.Combine("words", $"{id}.json"));

    private void Load()
    {
        string path = CataloguePath;
        if (!File.Exists(path)) return;
        try
        {
            var docs = JsonSerializer.Deserialize<List<Document>>(File.ReadAllText(path), Json)
                       ?? new List<Document>();
            foreach (var doc in docs)
            {
                // Reconciliation: drop entries whose original file vanished, and demote
                // a Ready doc whose words file is missing back to Error (defensive).
                if (!File.Exists(_files.OriginalFullPath(doc.Id, doc.Type)))
                {
                    _logger.LogWarning("Dropping catalogue entry {Id}: original missing", doc.Id);
                    continue;
                }
                var d = doc;
                if (d.Status == DocumentStatus.Ready && !File.Exists(WordsPath(d.Id)))
                {
                    _logger.LogWarning("Document {Id} Ready but words missing; demoting to Error", d.Id);
                    d = d with { Status = DocumentStatus.Error };
                }
                _docs[d.Id] = new StoredDocument { Document = d };
                _order.Add(d.Id);
            }
            _logger.LogInformation("Loaded {Count} document(s) from catalogue", _docs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load document catalogue; starting empty");
        }
    }

    // Caller holds _gate.
    private void SaveCatalogue()
    {
        var docs = _order.Select(id => _docs[id].Document).ToList();
        WriteAtomic(CataloguePath, JsonSerializer.Serialize(docs, Json));
    }

    // Caller holds _gate.
    private void SaveWords(Guid id, IReadOnlyList<SourceWord> words) =>
        WriteAtomic(WordsPath(id), JsonSerializer.Serialize(words, Json));

    private IReadOnlyList<SourceWord> LoadWords(Guid id)
    {
        try
        {
            string path = WordsPath(id);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<SourceWord>>(File.ReadAllText(path), Json) ?? new()
                : Array.Empty<SourceWord>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load words for {Id}", id);
            return Array.Empty<SourceWord>();
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
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
