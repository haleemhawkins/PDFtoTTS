using System.Text.Json.Serialization;
using PDFtoTTS.Api.Audio;
using PDFtoTTS.Api.Contracts;
using PDFtoTTS.Api.Documents;
using PDFtoTTS.Api.Grpc;
using PDFtoTTS.Api.Hubs;
using PDFtoTTS.Api.Pipeline;
using PDFtoTTS.Api.Storage;
using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Ingestion;
using PDFtoTTS.Orchestration;
using KokoroV1 = PDFtoTTS.Grpc.Kokoro.V1;
using AlignV1 = PDFtoTTS.Grpc.Alignment.V1;
using CommonV1 = PDFtoTTS.Grpc.Common.V1;
using OcrV1 = PDFtoTTS.Grpc.Ocr.V1;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Allow large document uploads (Kestrel's default cap is ~30 MB).
long maxUploadBytes = builder.Configuration.GetValue("UPLOAD_MAX_MB", 256) * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadBytes;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

// gRPC clients to the Python workers.
string kokoroAddr = builder.Configuration["KOKORO_GRPC"] ?? "http://localhost:50051";
string whisperxAddr = builder.Configuration["WHISPERX_GRPC"] ?? "http://localhost:50052";
string suryaAddr = builder.Configuration["SURYA_GRPC"] ?? "http://localhost:50053";
builder.Services.AddGrpcClient<KokoroV1.KokoroTts.KokoroTtsClient>(o => o.Address = new Uri(kokoroAddr));
builder.Services.AddGrpcClient<AlignV1.Alignment.AlignmentClient>(o => o.Address = new Uri(whisperxAddr));
builder.Services.AddGrpcClient<OcrV1.Ocr.OcrClient>(o => o.Address = new Uri(suryaAddr));

// Worker abstractions. The mock flags swap real gRPC workers for in-process
// fakes so the pipeline can run without a GPU. USE_MOCK_WORKERS toggles both;
// USE_MOCK_TTS / USE_MOCK_ALIGNER override individually (e.g. real Kokoro on CPU
// + mock alignment).
bool mockAll = builder.Configuration.GetValue("USE_MOCK_WORKERS", false);
if (builder.Configuration.GetValue("USE_MOCK_TTS", mockAll))
    builder.Services.AddSingleton<ISpeechSynthesizer, PDFtoTTS.Api.Mock.MockSynthesizer>();
else
    builder.Services.AddSingleton<ISpeechSynthesizer, KokoroSynthesizer>();

if (builder.Configuration.GetValue("USE_MOCK_ALIGNER", mockAll))
    builder.Services.AddSingleton<IForcedAligner, PDFtoTTS.Api.Mock.MockAligner>();
else
    builder.Services.AddSingleton<IForcedAligner, WhisperxAligner>();

// Text pipeline + orchestration.
builder.Services.AddSingleton<TextNormalizer>();
builder.Services.AddSingleton<HlsTranscoder>();
builder.Services.AddSingleton<ChunkMerger>();
builder.Services.AddSingleton<SynthesisOrchestrator>();
builder.Services.AddSingleton<PdfExtractor>();
builder.Services.AddSingleton<EpubExtractor>();
builder.Services.AddSingleton<PDFtoTTS.Api.Documents.PdfOcr>();
// Surya (GPU) is the primary scanned-PDF OCR engine; the pipeline falls back to
// ocrmypdf/Tesseract when it's disabled or unreachable. Disabled by default in mock
// mode (no GPU), where ocrmypdf in the api image handles scans.
if (builder.Configuration.GetValue("USE_SURYA_OCR", !mockAll))
    builder.Services.AddSingleton<IOcrEngine, SuryaOcr>();
else
    builder.Services.AddSingleton<IOcrEngine, DisabledOcrEngine>();
builder.Services.AddSingleton<DocumentPipeline>();

// Storage + pipeline runner.
string dataDir = builder.Configuration["DATA_DIR"]
    ?? Path.Combine(Path.GetTempPath(), "pdftotts-data");
builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(dataDir));
builder.Services.AddSingleton<IDocumentStore, PersistentDocumentStore>();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<SessionPipeline>();

builder.Services.AddSignalR();

var app = builder.Build();

// --- Documents ------------------------------------------------------------

app.MapPost("/api/documents", async (IFormFile file, IFileStorage files, IDocumentStore docs,
    DocumentPipeline pipeline, CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new ErrorResponse("EMPTY_FILE", "No file uploaded."));

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();

    var type = FileTypeDetector.Detect(bytes);
    if (type is null)
        return Results.Json(new ErrorResponse("UNSUPPORTED_FORMAT",
            "Only PDF and EPUB are supported."), statusCode: StatusCodes.Status415UnsupportedMediaType);

    // Extraction (and OCR for scanned PDFs) can take minutes, so it runs in the
    // background: persist the original, return an Extracting document immediately,
    // and let the client poll GET /api/documents/{id} until Ready or Error.
    var id = Guid.NewGuid();
    string rel = await files.SaveOriginalAsync(id, FileTypeDetector.Extension(type.Value), new MemoryStream(bytes), ct);

    var document = new Document(id, file.FileName, type.Value, 0, 0, DocumentStatus.Extracting);
    docs.Add(document, Array.Empty<PDFtoTTS.Core.TextPipeline.SourceWord>());
    pipeline.Start(document, rel);

    return Results.Created($"/api/documents/{id}", document);
}).DisableAntiforgery();

app.MapGet("/api/documents", (IDocumentStore docs) => Results.Ok(docs.All()));

app.MapGet("/api/documents/{id:guid}", (Guid id, IDocumentStore docs) =>
    docs.Get(id) is { } d ? Results.Ok(d.Document) : Results.NotFound());

app.MapGet("/api/documents/{id:guid}/words", (Guid id, IDocumentStore docs) =>
    docs.Get(id) is { } d ? Results.Ok(d.Words) : Results.NotFound());

// Serve the stored original so the client can render the PDF/EPUB without
// re-uploading it (the library opens documents by id).
app.MapGet("/api/documents/{id:guid}/original", (Guid id, IDocumentStore docs, IFileStorage files) =>
{
    if (docs.Get(id) is not { } d) return Results.NotFound();
    string path = files.OriginalFullPath(id, d.Document.Type);
    if (!File.Exists(path)) return Results.NotFound();
    string contentType = d.Document.Type == DocumentType.Epub ? "application/epub+zip" : "application/pdf";
    return Results.File(path, contentType, d.Document.Filename, enableRangeProcessing: true);
});

// Rename a document's display title.
app.MapPatch("/api/documents/{id:guid}", (Guid id, RenameDocumentRequest body, IDocumentStore docs) =>
{
    if (string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new ErrorResponse("INVALID_NAME", "A name is required."));
    return docs.Rename(id, body.Name.Trim()) is { } d
        ? Results.Ok(d)
        : Results.NotFound(new ErrorResponse("DOCUMENT_NOT_FOUND", $"No document {id}."));
});

// Save the reader's resume position so it follows the user across devices (the
// client also caches it locally; the server is the cross-device source of truth).
// Idempotent: PUT the latest known position; the store keeps the newest by timestamp.
app.MapPut("/api/documents/{id:guid}/position", (Guid id, UpdatePositionRequest body, IDocumentStore docs) =>
{
    // Last-writer-wins orders by the client's clock, so clamp a wildly-future
    // timestamp (a badly-skewed device) to server-now + 1h: it can't out-rank
    // everything forever and permanently freeze the resume point on other devices.
    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long updatedAtMs = Math.Min(body.UpdatedAtMs, nowMs + 3_600_000);
    var position = new ReadingPosition(
        Page: Math.Max(1, body.Page),
        Word: Math.Max(0, body.Word),
        Voice: string.IsNullOrWhiteSpace(body.Voice) ? null : body.Voice,
        Speed: body.Speed <= 0 ? 1f : body.Speed,
        UpdatedAtMs: updatedAtMs);
    return docs.SetPosition(id, position) is { } d
        ? Results.Ok(d)
        : Results.NotFound(new ErrorResponse("DOCUMENT_NOT_FOUND", $"No document {id}."));
});

// Delete a document, cascading to its in-flight sessions, their audio, the stored
// original, and the persisted words. Synthesized audio is per-session, so this
// just tears those down — nothing audio-related is persisted to begin with.
app.MapDelete("/api/documents/{id:guid}", (Guid id, IDocumentStore docs,
    ISessionStore sessions, IFileStorage files) =>
{
    foreach (var session in sessions.ForDocument(id))
    {
        session.Cancellation.Cancel();
        sessions.Remove(session.Session.Id);
        files.DeleteSessionAudio(session.Session.Id);
    }
    if (docs.Remove(id) is not { } removed)
        return Results.NotFound(new ErrorResponse("DOCUMENT_NOT_FOUND", $"No document {id}."));
    files.DeleteOriginal(id, removed.Document.Type);
    return Results.NoContent();
});

// --- Sessions -------------------------------------------------------------

app.MapPost("/api/documents/{id:guid}/sessions", (Guid id, CreateSessionRequest body,
    IDocumentStore docs, ISessionStore sessions, SessionPipeline pipeline) =>
{
    if (docs.Get(id) is not { } stored)
        return Results.NotFound(new ErrorResponse("DOCUMENT_NOT_FOUND", $"No document {id}."));
    if (stored.Document.Status != DocumentStatus.Ready)
        return Results.Conflict(new ErrorResponse("DOCUMENT_NOT_READY",
            $"Document is still {stored.Document.Status}."));
    if (string.IsNullOrWhiteSpace(body.Voice))
        return Results.BadRequest(new ErrorResponse("INVALID_VOICE", "A voice is required."));

    // A new session supersedes any in-flight one for this document: cancel the
    // others so their synthesis stops (the reader jumped to a new position / speed)
    // instead of wastefully competing for the GPU.
    foreach (var other in sessions.ForDocument(id))
        other.Cancellation.Cancel();

    var startWord = Math.Max(0, body.StartWordIndex);
    var session = new TtsSession(Guid.NewGuid(), id, body.Voice, body.Speed,
        string.IsNullOrWhiteSpace(body.Language) ? "en" : body.Language, SessionStatus.Processing, 0);
    sessions.Add(session, startWord);
    pipeline.Start(session.Id);

    return Results.Created($"/api/sessions/{session.Id}", session);
});

app.MapGet("/api/sessions/{id:guid}", (Guid id, ISessionStore sessions) =>
    sessions.Get(id) is { } s ? Results.Ok(s.Session) : Results.NotFound());

app.MapGet("/api/sessions/{id:guid}/chunks", (Guid id, ISessionStore sessions) =>
    sessions.Get(id) is { } s ? Results.Ok(s.SnapshotChunks()) : Results.NotFound());

app.MapGet("/api/sessions/{id:guid}/chunks/{index:int}/audio", (Guid id, int index,
    ISessionStore sessions, IFileStorage files) =>
{
    if (sessions.Get(id) is null) return Results.NotFound();
    string path = files.AudioFullPath(id, index);
    return File.Exists(path)
        ? Results.File(path, "audio/wav", enableRangeProcessing: true)
        : Results.NotFound();
});

// HLS playlist + segments — the iOS background-playback path. Safari plays this
// natively via AVPlayer, which keeps audio alive with the screen locked and drives
// the lock-screen controls. Segments are transcoded from the WAV chunks on demand.
app.MapGet("/api/sessions/{id:guid}/hls/{name}", async (Guid id, string name, HttpContext ctx,
    ISessionStore sessions, HlsTranscoder hls) =>
{
    if (sessions.Get(id) is not { } s)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (name == "playlist.m3u8")
    {
        // Don't hand back an empty playlist — Safari caches it and gives up. Wait
        // for the first segment to exist (or synthesis to end / client to leave).
        await hls.WaitForFirstSegmentAsync(s, ctx.RequestAborted);
        // The playlist grows as chunks arrive, so it must never be cached.
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
        await ctx.Response.WriteAsync(hls.BuildPlaylist(s), ctx.RequestAborted);
        return;
    }

    if (name.EndsWith(".ts", StringComparison.Ordinal) &&
        int.TryParse(name[..^3], out int index))
    {
        string? path = await hls.EnsureSegmentAsync(id, index, ctx.RequestAborted);
        if (path is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Segments are immutable once produced — let the client/CDN cache them.
        ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        await Results.File(path, "video/mp2t", enableRangeProcessing: true).ExecuteAsync(ctx);
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.MapDelete("/api/sessions/{id:guid}", (Guid id, ISessionStore sessions, IFileStorage files) =>
{
    if (sessions.Get(id) is not { } s) return Results.NotFound();
    s.Cancellation.Cancel();
    sessions.Remove(id);
    files.DeleteSessionAudio(id);
    return Results.NoContent();
});

// --- Catalogue / health ---------------------------------------------------

app.MapGet("/api/voices", async (KokoroV1.KokoroTts.KokoroTtsClient kokoro, CancellationToken ct) =>
{
    try
    {
        var resp = await kokoro.ListVoicesAsync(new CommonV1.ListVoicesRequest(), cancellationToken: ct);
        return Results.Ok(resp.Voices.Select(v => new Voice(v.Id, v.Label, v.Language, v.Gender)));
    }
    catch
    {
        return Results.Ok(Array.Empty<Voice>());
    }
});

app.MapGet("/healthz", async (KokoroV1.KokoroTts.KokoroTtsClient kokoro,
    AlignV1.Alignment.AlignmentClient whisperx, CancellationToken ct) =>
{
    var deadline = DateTime.UtcNow.AddSeconds(2);
    try
    {
        var k = await kokoro.HealthAsync(new CommonV1.HealthRequest(), deadline: deadline, cancellationToken: ct);
        var w = await whisperx.HealthAsync(new CommonV1.HealthRequest(), deadline: deadline, cancellationToken: ct);
        bool ok = k.Status == CommonV1.HealthResponse.Types.Status.Serving
               && w.Status == CommonV1.HealthResponse.Types.Status.Serving;
        return ok ? Results.Ok(new { status = "healthy" })
                  : Results.Json(new { status = "degraded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        return Results.Json(new { status = "workers_unreachable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapHub<ReaderHub>("/hubs/reader");

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
