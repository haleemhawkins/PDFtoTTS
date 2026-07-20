using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using PDFtoTTS.Api.Contracts;
using PDFtoTTS.Api.Hubs;
using PDFtoTTS.Api.Storage;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Orchestration;

namespace PDFtoTTS.Api.Pipeline;

/// <summary>
/// Runs the synthesis pipeline for a session as a background task and publishes
/// progress over the reader hub, advancing the session lifecycle
/// queued→processing→streaming→complete|error (design §5.3/§5.4).
/// </summary>
public sealed class SessionPipeline
{
    private readonly SynthesisOrchestrator _orchestrator;
    private readonly IDocumentStore _documents;
    private readonly ISessionStore _sessions;
    private readonly IFileStorage _files;
    private readonly IHubContext<ReaderHub> _hub;
    private readonly TextNormalizer _normalizer;
    private readonly ILogger<SessionPipeline> _logger;
    private readonly int _maxTokensPerChunk;
    private readonly int _firstChunkTokens;
    private readonly int _concurrency;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public SessionPipeline(
        SynthesisOrchestrator orchestrator,
        IDocumentStore documents,
        ISessionStore sessions,
        IFileStorage files,
        IHubContext<ReaderHub> hub,
        TextNormalizer normalizer,
        ILogger<SessionPipeline> logger,
        IConfiguration config)
    {
        _orchestrator = orchestrator;
        _documents = documents;
        _sessions = sessions;
        _files = files;
        _hub = hub;
        _normalizer = normalizer;
        _logger = logger;
        _maxTokensPerChunk = config.GetValue("MAX_TOKENS_PER_CHUNK", 350);
        // A small first chunk so audio starts in a few seconds instead of waiting
        // for a full-size chunk to synthesize + align.
        _firstChunkTokens = config.GetValue("FIRST_CHUNK_TOKENS", _maxTokensPerChunk);
        // Match Kokoro's parallel synth slots (same host): ~cores/3 unless the
        // env overrides it. The orchestrator runs the first chunk alone for fast
        // time-to-first-audio, then opens up to this many in parallel.
        _concurrency = config.GetValue<int?>("PIPELINE_CONCURRENCY")
            ?? Math.Clamp(Environment.ProcessorCount / 3, 1, 8);
    }

    public void Start(Guid sessionId) =>
        _running[sessionId] = Task.Run(() => RunAsync(sessionId));

    private async Task RunAsync(Guid sessionId)
    {
        var stored = _sessions.Get(sessionId);
        if (stored is null) return;
        var doc = _documents.Get(stored.Session.DocumentId);
        if (doc is null) return;

        var ct = stored.Cancellation.Token;
        string group = ReaderHub.Group(sessionId);

        try
        {
            await SetStatus(sessionId, group, SessionStatus.Processing, ct);

            var tokens = _normalizer.Normalize(doc.Words);
            // Begin at the requested source word: skip tokens that end before it,
            // so the session synthesizes from the reader's position onward.
            if (stored.StartWordIndex > 0)
            {
                int from = 0;
                while (from < tokens.Count && tokens[from].SourceEnd < stored.StartWordIndex) from++;
                if (from > 0) tokens = tokens.Skip(from).ToList();
            }
            var chunks = new Chunker(_maxTokensPerChunk, _firstChunkTokens).Chunk(tokens);
            stored.TotalChunks = chunks.Count;

            var options = new PipelineOptions(
                stored.Session.Voice, stored.Session.Speed, stored.Session.Language, _concurrency);

            bool first = true;
            int completed = 0;

            await foreach (var chunk in _orchestrator.RunAsync(
                chunks, doc.Words, options,
                outPath: i => _files.AudioRelativePath(sessionId, i),
                audioUrl: i => $"/api/sessions/{sessionId}/chunks/{i}/audio",
                ct))
            {
                stored.AddChunk(chunk);

                if (first)
                {
                    await SetStatus(sessionId, group, SessionStatus.Streaming, ct);
                    first = false;
                }

                await _hub.Clients.Group(group).SendAsync("ChunkReady", chunk, ct);

                completed++;
                double progress = chunks.Count == 0 ? 1 : (double)completed / chunks.Count;
                _sessions.Update(sessionId, s => s with { Progress = progress });
                await _hub.Clients.Group(group).SendAsync("Progress",
                    new ProgressPayload(sessionId.ToString(), completed, chunks.Count, progress), ct);
            }

            await SetStatus(sessionId, group, SessionStatus.Complete, ct);
        }
        catch (OperationCanceledException)
        {
            // Session deleted/cancelled — nothing more to publish.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for session {SessionId}", sessionId);
            _sessions.Update(sessionId, s => s with { Status = SessionStatus.Error });
            await _hub.Clients.Group(group).SendAsync("Error",
                new ErrorPayload(sessionId.ToString(), "PIPELINE_FAILED", ex.Message), CancellationToken.None);
            await _hub.Clients.Group(group).SendAsync("SessionStatus",
                new SessionStatusPayload(sessionId.ToString(), SessionStatus.Error.ToString()), CancellationToken.None);
        }
        finally
        {
            _running.TryRemove(sessionId, out _);
            // A cancelled session was torn down by whoever cancelled it (delete /
            // supersede), but a chunk mid-synthesis may have landed on disk AFTER
            // that cleanup — sweep again now the pipeline has actually stopped.
            if (ct.IsCancellationRequested && _sessions.Get(sessionId) is null)
                _files.DeleteSessionAudio(sessionId);
        }
    }

    private async Task SetStatus(Guid sessionId, string group, SessionStatus status, CancellationToken ct)
    {
        _sessions.Update(sessionId, s => s with { Status = status });
        await _hub.Clients.Group(group).SendAsync("SessionStatus",
            new SessionStatusPayload(sessionId.ToString(), status.ToString()), ct);
    }
}
