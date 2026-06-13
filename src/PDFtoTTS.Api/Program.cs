using System.Text.Json.Serialization;
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
builder.Services.AddGrpcClient<KokoroV1.KokoroTts.KokoroTtsClient>(o => o.Address = new Uri(kokoroAddr));
builder.Services.AddGrpcClient<AlignV1.Alignment.AlignmentClient>(o => o.Address = new Uri(whisperxAddr));

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
builder.Services.AddSingleton<ChunkMerger>();
builder.Services.AddSingleton<SynthesisOrchestrator>();
builder.Services.AddSingleton<PdfExtractor>();
builder.Services.AddSingleton<EpubExtractor>();

// Storage + pipeline runner.
string dataDir = builder.Configuration["DATA_DIR"]
    ?? Path.Combine(Path.GetTempPath(), "pdftotts-data");
builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(dataDir));
builder.Services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<SessionPipeline>();

builder.Services.AddSignalR();

var app = builder.Build();

// --- Documents ------------------------------------------------------------

app.MapPost("/api/documents", async (IFormFile file, IFileStorage files, IDocumentStore docs,
    PdfExtractor pdf, EpubExtractor epub, ILogger<Program> logger, CancellationToken ct) =>
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

    ExtractionResult extraction;
    try
    {
        extraction = type == DocumentType.Pdf ? pdf.Extract(bytes) : epub.Extract(bytes);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Extraction failed for {FileName} ({Type})", file.FileName, type);
        return Results.UnprocessableEntity(new ErrorResponse("UNREADABLE_DOCUMENT", ex.Message));
    }

    var id = Guid.NewGuid();
    await files.SaveOriginalAsync(id, FileTypeDetector.Extension(type.Value), new MemoryStream(bytes), ct);

    var document = new Document(id, file.FileName, type.Value,
        extraction.PageCount, extraction.Words.Count, DocumentStatus.Ready);
    docs.Add(document, extraction.Words);

    return Results.Created($"/api/documents/{id}", document);
}).DisableAntiforgery();

app.MapGet("/api/documents/{id:guid}", (Guid id, IDocumentStore docs) =>
    docs.Get(id) is { } d ? Results.Ok(d.Document) : Results.NotFound());

app.MapGet("/api/documents/{id:guid}/words", (Guid id, IDocumentStore docs) =>
    docs.Get(id) is { } d ? Results.Ok(d.Words) : Results.NotFound());

// --- Sessions -------------------------------------------------------------

app.MapPost("/api/documents/{id:guid}/sessions", (Guid id, CreateSessionRequest body,
    IDocumentStore docs, ISessionStore sessions, SessionPipeline pipeline) =>
{
    if (docs.Get(id) is null)
        return Results.NotFound(new ErrorResponse("DOCUMENT_NOT_FOUND", $"No document {id}."));
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
