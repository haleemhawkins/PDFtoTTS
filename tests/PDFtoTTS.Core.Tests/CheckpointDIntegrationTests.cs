using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PDFtoTTS.Orchestration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PDFtoTTS.Core.Tests;

/// <summary>
/// Checkpoint D: upload → extract → normalize → chunk → synth → align → merge →
/// stream, end-to-end through the real ASP.NET host with fake gRPC workers.
/// </summary>
public class CheckpointDIntegrationTests
{
    static CheckpointDIntegrationTests() => QuestPDF.Settings.License = LicenseType.Community;

    // Fake workers: synth writes nothing, aligner fabricates one timing per word.
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
            var aligned = words.Select((w, i) =>
                new AlignedWord(w, i * 100, (i + 1) * 100, 0.9f, false)).ToList();
            return Task.FromResult(new AlignmentResult(aligned, words.Length * 100L));
        }
    }

    private sealed class ReaderAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pdftotts-test-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DATA_DIR", _dataDir);
            builder.UseSetting("MAX_TOKENS_PER_CHUNK", "3"); // force multiple chunks
            builder.UseSetting("PIPELINE_CONCURRENCY", "2");
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<ISpeechSynthesizer>();
                s.AddSingleton<ISpeechSynthesizer, FakeSynth>();
                s.RemoveAll<IForcedAligner>();
                s.AddSingleton<IForcedAligner, FakeAligner>();
            });
        }
    }

    private static byte[] MakePdf(string text) =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.Content().Text(text);
        })).GeneratePdf();

    private sealed record DocDto(string Id, string Filename, string Type, int PageCount, int WordCount, string Status);
    private sealed record SessionDto(string Id, string Status, double Progress);

    [Fact]
    public async Task Upload_to_streamed_chunks_end_to_end()
    {
        await using var factory = new ReaderAppFactory();
        var client = factory.CreateClient();

        // 1. Upload a four-sentence PDF.
        var pdf = MakePdf("Sentence one. Sentence two. Sentence three. Sentence four.");
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdf);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "test.pdf");

        var uploadResp = await client.PostAsync("/api/documents", form);
        Assert.Equal(HttpStatusCode.Created, uploadResp.StatusCode);
        var doc = await uploadResp.Content.ReadFromJsonAsync<DocDto>();
        Assert.NotNull(doc);
        Assert.Equal("Ready", doc!.Status);
        Assert.Equal(8, doc.WordCount); // "Sentence X." × 4 → 8 words

        // 2. Create a session — the pipeline starts immediately.
        var sessionResp = await client.PostAsJsonAsync($"/api/documents/{doc.Id}/sessions",
            new { voice = "af_heart", speed = 1.0f, language = "en" });
        Assert.Equal(HttpStatusCode.Created, sessionResp.StatusCode);
        var session = await sessionResp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(session);

        // 3. Poll until the pipeline completes.
        SessionDto? state = null;
        for (int i = 0; i < 100; i++)
        {
            state = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{session!.Id}");
            if (state!.Status is "Complete" or "Error") break;
            await Task.Delay(100);
        }
        Assert.Equal("Complete", state!.Status);

        // 4. REST: chunks are stored in order, each merged with timings + bbox.
        var chunks = await client.GetFromJsonAsync<List<JsonElement>>($"/api/sessions/{session!.Id}/chunks");
        Assert.NotNull(chunks);
        Assert.Equal(4, chunks!.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, chunks.Select(c => c.GetProperty("chunkIndex").GetInt32()));

        var firstWords = chunks[0].GetProperty("words");
        Assert.True(firstWords.GetArrayLength() >= 1);
        var w0 = firstWords[0];
        Assert.Equal(1, w0.GetProperty("page").GetInt32());            // from PDF extraction
        Assert.False(w0.GetProperty("bbox").ValueKind == JsonValueKind.Null);
        Assert.True(w0.GetProperty("endMs").GetInt64() > w0.GetProperty("startMs").GetInt64()); // from alignment

        // 5. SignalR: a late subscriber is backfilled with all chunks in order.
        var received = new List<int>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/reader"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        hub.On<JsonElement>("ChunkReady", pc =>
        {
            lock (received) received.Add(pc.GetProperty("chunkIndex").GetInt32());
        });

        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe", session!.Id);

        // Backfill is sent on subscribe; give it a moment to arrive.
        for (int i = 0; i < 50 && received.Count < 4; i++) await Task.Delay(50);
        await hub.DisposeAsync();

        Assert.Equal(new[] { 0, 1, 2, 3 }, received);
    }
}
