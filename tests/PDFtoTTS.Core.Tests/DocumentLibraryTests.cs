using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PDFtoTTS.Orchestration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PDFtoTTS.Core.Tests;

/// <summary>
/// Document library CRUD (list / rename / delete / original bytes) plus on-disk
/// persistence of the catalogue across an API restart. Uses the real ASP.NET host
/// with fake gRPC workers; persistence is exercised by pointing two factory
/// instances at the same DATA_DIR.
/// </summary>
public class DocumentLibraryTests
{
    static DocumentLibraryTests() => QuestPDF.Settings.License = LicenseType.Community;

    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest r, CancellationToken ct) =>
            Task.FromResult(new SynthesisResult(r.OutPath, 1000));
    }

    private sealed class FakeAligner : IForcedAligner
    {
        public Task<AlignmentResult> AlignAsync(AlignmentRequest r, CancellationToken ct)
        {
            var words = r.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var aligned = words.Select((w, i) => new AlignedWord(w, i * 100, (i + 1) * 100, 0.9f, false)).ToList();
            return Task.FromResult(new AlignmentResult(aligned, words.Length * 100L));
        }
    }

    private sealed class ReaderAppFactory(string dataDir) : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = dataDir;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", DataDir);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<ISpeechSynthesizer>();
                s.AddSingleton<ISpeechSynthesizer, FakeSynth>();
                s.RemoveAll<IForcedAligner>();
                s.AddSingleton<IForcedAligner, FakeAligner>();
            });
        }
    }

    private static string NewDataDir() =>
        Path.Combine(Path.GetTempPath(), "pdftotts-libtest-" + Guid.NewGuid());

    private static byte[] MakePdf(string text) =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.Content().Text(text);
        })).GeneratePdf();

    private sealed record DocDto(string Id, string Filename, string Type, int PageCount, int WordCount, string Status);

    private static async Task<DocDto> UploadAndWaitReady(HttpClient client, string filename, string text)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(MakePdf(text));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", filename);

        var resp = await client.PostAsync("/api/documents", form);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<DocDto>();

        for (int i = 0; i < 100 && doc!.Status is "Queued" or "Extracting"; i++)
        {
            await Task.Delay(50);
            doc = await client.GetFromJsonAsync<DocDto>($"/api/documents/{doc.Id}");
        }
        Assert.Equal("Ready", doc!.Status);
        return doc;
    }

    [Fact]
    public async Task Lists_documents_most_recent_first()
    {
        await using var factory = new ReaderAppFactory(NewDataDir());
        var client = factory.CreateClient();

        var first = await UploadAndWaitReady(client, "first.pdf", "Alpha beta gamma.");
        var second = await UploadAndWaitReady(client, "second.pdf", "Delta epsilon zeta.");

        var list = await client.GetFromJsonAsync<List<DocDto>>("/api/documents");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Equal(second.Id, list[0].Id); // most-recent-first
        Assert.Equal(first.Id, list[1].Id);
    }

    [Fact]
    public async Task Rename_updates_filename_and_validates()
    {
        await using var factory = new ReaderAppFactory(NewDataDir());
        var client = factory.CreateClient();
        var doc = await UploadAndWaitReady(client, "orig.pdf", "One two three.");

        var ok = await client.PatchAsJsonAsync($"/api/documents/{doc.Id}", new { name = "My Renamed Book" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<DocDto>();
        Assert.Equal("My Renamed Book", updated!.Filename);

        var blank = await client.PatchAsJsonAsync($"/api/documents/{doc.Id}", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var unknown = await client.PatchAsJsonAsync($"/api/documents/{Guid.NewGuid()}", new { name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_document_session_and_original()
    {
        var dataDir = NewDataDir();
        await using var factory = new ReaderAppFactory(dataDir);
        var client = factory.CreateClient();
        var doc = await UploadAndWaitReady(client, "del.pdf", "Cascade delete test words.");

        // Start a session so delete has something to cascade to.
        var sessResp = await client.PostAsJsonAsync($"/api/documents/{doc.Id}/sessions",
            new { voice = "af_heart", speed = 1.0f, language = "en" });
        Assert.Equal(HttpStatusCode.Created, sessResp.StatusCode);
        var session = await sessResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var sessionId = session!["id"].ToString();

        string originalPath = Path.Combine(dataDir, "originals", $"{doc.Id}.pdf");
        Assert.True(File.Exists(originalPath));

        var del = await client.DeleteAsync($"/api/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sessions/{sessionId}")).StatusCode);
        Assert.False(File.Exists(originalPath));
        Assert.Empty((await client.GetFromJsonAsync<List<DocDto>>("/api/documents"))!);

        // Deleting again is a 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/documents/{doc.Id}")).StatusCode);
    }

    [Fact]
    public async Task Serves_original_bytes()
    {
        await using var factory = new ReaderAppFactory(NewDataDir());
        var client = factory.CreateClient();
        var doc = await UploadAndWaitReady(client, "bytes.pdf", "Serve these bytes back.");

        var resp = await client.GetAsync($"/api/documents/{doc.Id}/original");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType!.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 4);
        Assert.Equal("%PDF"u8.ToArray(), bytes[..4]); // it's a real PDF

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/documents/{Guid.NewGuid()}/original")).StatusCode);
    }

    [Fact]
    public async Task Catalogue_survives_a_restart_without_re_extraction()
    {
        var dataDir = NewDataDir();
        string docId;
        int wordCount;

        // First "process lifetime": upload + extract to Ready, then dispose the host.
        await using (var factory = new ReaderAppFactory(dataDir))
        {
            var client = factory.CreateClient();
            var doc = await UploadAndWaitReady(client, "persist.pdf", "Persisted words survive restart.");
            docId = doc.Id;
            wordCount = doc.WordCount;
            Assert.True(wordCount > 0);
        }

        // Second "process lifetime": a fresh host over the same DATA_DIR reloads the
        // catalogue from disk — the document and its words are available with no
        // re-upload and no re-extraction.
        await using (var factory = new ReaderAppFactory(dataDir))
        {
            var client = factory.CreateClient();

            var list = await client.GetFromJsonAsync<List<DocDto>>("/api/documents");
            var reloaded = Assert.Single(list!);
            Assert.Equal(docId, reloaded.Id);
            Assert.Equal("Ready", reloaded.Status);
            Assert.Equal(wordCount, reloaded.WordCount);

            var words = await client.GetFromJsonAsync<List<JsonElementWord>>($"/api/documents/{docId}/words");
            Assert.Equal(wordCount, words!.Count);
        }
    }

    private sealed record JsonElementWord(int Index, string Text);

    private sealed record PositionDto(int Page, int Word, string? Voice, float Speed, long UpdatedAtMs);
    private sealed record DocWithPosition(string Id, string Status, PositionDto? Position);

    [Fact]
    public async Task Saves_reading_position_with_last_writer_wins_and_survives_restart()
    {
        var dataDir = NewDataDir();
        string docId;

        await using (var factory = new ReaderAppFactory(dataDir))
        {
            var client = factory.CreateClient();
            var doc = await UploadAndWaitReady(client, "resume.pdf", "Resume from here later on.");
            docId = doc.Id;

            // A freshly uploaded document has no position.
            var initial = await client.GetFromJsonAsync<DocWithPosition>($"/api/documents/{docId}");
            Assert.Null(initial!.Position);

            // Save a position.
            var put = await client.PutAsJsonAsync($"/api/documents/{docId}/position",
                new { page = 5, word = 42, voice = "am_adam", speed = 1.25f, updatedAtMs = 1000L });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadFromJsonAsync<DocWithPosition>();
            Assert.Equal(5, saved!.Position!.Page);
            Assert.Equal(42, saved.Position.Word);
            Assert.Equal("am_adam", saved.Position.Voice);

            // A stale write (older timestamp) must NOT overwrite the newer position.
            await client.PutAsJsonAsync($"/api/documents/{docId}/position",
                new { page = 1, word = 0, voice = "af_heart", speed = 1f, updatedAtMs = 500L });
            var afterStale = await client.GetFromJsonAsync<DocWithPosition>($"/api/documents/{docId}");
            Assert.Equal(42, afterStale!.Position!.Word); // unchanged

            // A newer write wins.
            await client.PutAsJsonAsync($"/api/documents/{docId}/position",
                new { page = 9, word = 77, voice = "am_adam", speed = 1.5f, updatedAtMs = 2000L });
            var afterNew = await client.GetFromJsonAsync<DocWithPosition>($"/api/documents/{docId}");
            Assert.Equal(77, afterNew!.Position!.Word);

            // Unknown document → 404.
            var unknown = await client.PutAsJsonAsync($"/api/documents/{Guid.NewGuid()}/position",
                new { page = 1, word = 0, updatedAtMs = 1L });
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // A fresh host over the same DATA_DIR reloads the saved position from disk —
        // this is what makes "resume on another device" work after an API restart.
        await using (var factory = new ReaderAppFactory(dataDir))
        {
            var client = factory.CreateClient();
            var reloaded = await client.GetFromJsonAsync<DocWithPosition>($"/api/documents/{docId}");
            Assert.Equal(9, reloaded!.Position!.Page);
            Assert.Equal(77, reloaded.Position.Word);
            Assert.Equal(1.5f, reloaded.Position.Speed);
        }
    }
}
