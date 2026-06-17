using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PDFtoTTS.Api.Documents;

/// <summary>
/// Adds a searchable text layer to a scanned (image-only) PDF by shelling out to
/// <c>ocrmypdf</c> (Tesseract under the hood). The output is a normal PDF with an
/// invisible per-word text layer, so the existing PdfPig extractor reads words +
/// positions from it unchanged — OCR'd scans get the same highlight sync as native
/// text PDFs. No-op (returns false) when ocrmypdf isn't installed.
/// </summary>
public sealed class PdfOcr
{
    private readonly ILogger<PdfOcr> _logger;
    private readonly string _language;
    private readonly int _timeoutSeconds;
    private readonly bool _deskew;
    private readonly bool _rotatePages;
    private readonly bool _clean;
    private readonly int _oversample;
    private readonly Lazy<bool> _available;

    public PdfOcr(ILogger<PdfOcr> logger, IConfiguration config)
    {
        _logger = logger;
        _language = config.GetValue("OCR_LANGUAGE", "eng")!;
        // Whole-document OCR of a long scan is minutes of CPU work; bound it so a
        // pathological file can't pin the box forever.
        _timeoutSeconds = config.GetValue("OCR_TIMEOUT_SECONDS", 1800);
        // Image preprocessing that improves OCR accuracy. Deskew (straighten tilted
        // scans) and rotate-pages (fix sideways/upside-down pages) are cheap and
        // high-impact, so they default on. Clean (unpaper denoise of the OCR input
        // image — the visible page is unchanged) and oversampling low-DPI pages cost
        // more time, so they're opt-in via env.
        _deskew = config.GetValue("OCR_DESKEW", true);
        _rotatePages = config.GetValue("OCR_ROTATE_PAGES", true);
        _clean = config.GetValue("OCR_CLEAN", false);
        _oversample = config.GetValue("OCR_OVERSAMPLE", 0);
        _available = new Lazy<bool>(() => ResolveOnPath("ocrmypdf") is not null);
    }

    /// <summary>True when ocrmypdf is on PATH (so OCR can be attempted).</summary>
    public bool Available => _available.Value;

    /// <summary>
    /// OCR <paramref name="inputPath"/> into <paramref name="outputPath"/>.
    /// Returns false (and logs) if ocrmypdf is missing or the run fails.
    /// <paramref name="progress"/>, when given, is reported a fraction in [0,1] as
    /// each page's text layer is grafted, so the client can show a real progress bar.
    /// </summary>
    public async Task<bool> TryOcrAsync(string inputPath, string outputPath, CancellationToken ct,
        IProgress<double>? progress = null)
    {
        if (!Available)
        {
            _logger.LogWarning("ocrmypdf not found on PATH; cannot OCR scanned PDF {Input}", inputPath);
            return false;
        }

        var psi = new ProcessStartInfo("ocrmypdf")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // --skip-text leaves any already-text pages untouched (so a mixed PDF isn't
        // rasterized); pure scans get every page OCR'd. --optimize 0 skips the slow
        // lossy image optimization — we only need the text layer, not a smaller file.
        psi.ArgumentList.Add("--skip-text");
        psi.ArgumentList.Add("--optimize");
        psi.ArgumentList.Add("0");
        // --output-type pdf skips the PDF/A standardization pass (a whole-document
        // Ghostscript rewrite that is typically the single slowest stage). We only
        // need a searchable text layer, not an archival PDF/A, so this is a big,
        // safe speedup on the path to first audio.
        psi.ArgumentList.Add("--output-type");
        psi.ArgumentList.Add("pdf");
        // Accuracy preprocessing (see ctor). rotate-pages needs the osd model.
        if (_rotatePages) psi.ArgumentList.Add("--rotate-pages");
        if (_deskew) psi.ArgumentList.Add("--deskew");
        if (_clean) psi.ArgumentList.Add("--clean");
        if (_oversample > 0)
        {
            psi.ArgumentList.Add("--oversample");
            psi.ArgumentList.Add(_oversample.ToString());
        }
        psi.ArgumentList.Add("--language");
        psi.ArgumentList.Add(_language);
        psi.ArgumentList.Add("--jobs");
        psi.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount, 1, 8).ToString());
        // -v 1 makes ocrmypdf log one "<page> Grafting" line as each page's text
        // layer is attached; we count those against the "Start processing N pages"
        // total to drive the progress bar (parsed in ReadProgressAsync below).
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add(outputPath);

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        try
        {
            proc.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            // Drain stdout so a chatty child can't deadlock on a full pipe; stderr
            // is parsed line-by-line for progress and tailed for error reporting.
            var drainStdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            string errTail = await ReadProgressAsync(proc.StandardError, progress, timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            await drainStdout;

            if (proc.ExitCode != 0)
            {
                _logger.LogError("ocrmypdf exited {Code} for {Input}: {Stderr}",
                    proc.ExitCode, inputPath, errTail);
                return false;
            }
            progress?.Report(1);
            _logger.LogInformation("OCR completed for {Input} in {Seconds:F0}s", inputPath, sw.Elapsed.TotalSeconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ocrmypdf failed to run for {Input}", inputPath);
            TryKill(proc);
            return false;
        }
    }

    // Matches ocrmypdf's (-v 1) "Start processing N pages concurrently" total and
    // the per-page "   <page> Grafting" completion line. Format is stable for the
    // pinned ocrmypdf in our image; if it ever changes, progress just stays at 0
    // (an indeterminate bar) while OCR still completes normally.
    private static readonly Regex TotalPagesRe = new(@"Start processing (\d+) pages", RegexOptions.Compiled);
    private static readonly Regex GraftedRe = new(@"^\s*\d+\s+Grafting\b", RegexOptions.Compiled);

    /// <summary>Streams ocrmypdf stderr, reporting page-graft progress, and returns
    /// the last few lines (for diagnostics if the run fails).</summary>
    private static async Task<string> ReadProgressAsync(
        StreamReader stderr, IProgress<double>? progress, CancellationToken ct)
    {
        var tail = new Queue<string>();
        int total = 0, grafted = 0;
        string? line;
        while ((line = await stderr.ReadLineAsync(ct)) is not null)
        {
            tail.Enqueue(line);
            while (tail.Count > 30) tail.Dequeue();

            if (total == 0 && TotalPagesRe.Match(line) is { Success: true } m)
                total = int.Parse(m.Groups[1].Value);
            else if (progress is not null && total > 0 && GraftedRe.IsMatch(line))
                progress.Report(Math.Min(1.0, (double)(++grafted) / total));
        }
        return string.Join('\n', tail).Trim();
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static string? ResolveOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
