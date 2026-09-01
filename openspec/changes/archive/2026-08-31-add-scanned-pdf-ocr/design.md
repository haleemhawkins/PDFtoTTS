## Context

Scanned/image-only PDFs extract no text via PdfPig, so they yielded empty
sessions. A first pass added an ocrmypdf/Tesseract step that grafts an invisible
text layer back onto the PDF and re-extracts through PdfPig. Accuracy and reading
order were poor (Tesseract is skew/orientation-sensitive and the recovered text
layer ordered words oddly for some scans). The system already runs two Python
GPU workers (Kokoro TTS — CPU-only here due to the RDNA3/onnxruntime limit — and
WhisperX alignment on the GPU via torch-rocm), with a shared `/data` volume and a
gRPC contract pattern. Separately, sentence terminators were dropped before
synthesis, so the voice produced run-on sentences with no pause or intonation.

## Goals / Non-Goals

**Goals:**
- Highest practical scanned-PDF accuracy while keeping everything on-box and
  preserving per-word boxes for highlight sync.
- Reuse the existing worker/gRPC/compose pattern; no change to the native-PDF path.
- Graceful degradation: scans still work if the GPU OCR worker is absent.
- Restore sentence intonation and a natural pause after each sentence.

**Non-Goals:**
- Cloud OCR (privacy/cost) — explicitly rejected with the user.
- Re-architecting highlight sync; OCR feeds the existing `SourceWord` stream.
- Multi-column reading-order beyond top-to-bottom line ordering (sufficient for
  the single-column body text that dominates books).

## Decisions

- **Surya (PyTorch) as the primary engine, not PaddleOCR or cloud.** Surya is
  near the top of accuracy benchmarks, returns word/line boxes, and — decisively —
  runs on PyTorch/torch-rocm, so it uses this AMD GPU exactly like WhisperX.
  PaddleOCR relies on Paddle/onnxruntime, which is the same stack that forces
  Kokoro to CPU here, so it would not use the GPU. Cloud was rejected for privacy.
- **OCR returns positioned words directly; no PDF text layer is rebuilt.** The
  frontend renders page images and overlays highlights from API word boxes, so a
  searchable text layer is unnecessary. The Surya worker converts pixel boxes
  (top-left origin) to PDF user space (bottom-left origin, points) using the page
  size and render scale, matching PdfPig output 1:1. Word boxes are split from
  recognized line boxes proportionally to character width (Surya word boxes are
  not reliable across versions).
- **Surya-first, ocrmypdf-fallback in `DocumentPipeline`.** A new `IOcrEngine`
  abstraction wraps the Surya gRPC client; `DisabledOcrEngine` is bound in mock
  mode. On disabled/unreachable/empty-result, the pipeline falls back to the
  existing ocrmypdf path (now upgraded with the tessdata_best model plus
  `--rotate-pages`/`--deskew`).
- **Terminators re-attached at chunk build, not kept on tokens.** `NormToken`
  gains a `Terminator` char; `Chunker` appends it to the chunk text. Token text
  stays clean so `ChunkMerger` alignment (which matches on token text) is
  unaffected, and `Chunk.Text` (used for synthesis and the alignment transcript)
  carries punctuation the voice and pause logic need.
- **Pause inserted in the worker, per sentence.** Kokoro synthesizes sentence by
  sentence and appends a configurable silence (`SENTENCE_GAP_MS`), longer after
  `?`/`!`. This keeps each sentence's intonation and isolates phonemizer failures.

## Risks / Trade-offs

- [Surya pulls numpy 2.x, breaking the base image's numpy-1.x-compiled ROCm
  torch ("Numpy is not available")] → pin `numpy<2`, with a build-time
  `torch.ones(2).numpy()` assertion that fails the build loudly.
- [Surya under-pins `transformers`, so pip grabs 4.57 which removed
  `QuantizedCacheConfig` and breaks `surya.recognition`] → pin
  `transformers>=4.45,<4.52` (resolves to 4.51.x).
- [Surya/torch could be swapped for a CUDA wheel, silently disabling the GPU] →
  a generated pip constraint pins the preinstalled ROCm torch; the build asserts
  `torch.version.hip`.
- [GPU OCR is the slowest single call (render+detect+recognize per page)] →
  generous (bounded) deadline; OCR runs once at upload and the result is persisted.
- [VRAM contention with WhisperX] → OCR happens at upload, alignment during
  playback; they rarely overlap, and concurrency is bounded per worker.
- [Surya is fast-moving; API drifts between releases] → pin `surya-ocr==0.14.1`
  and write the backend defensively (task-name/predictor-arg fallbacks, getattr on
  result fields).
- [Multi-column scans may mis-order] → accepted; line top-to-bottom ordering is
  correct for body text; layout-aware ordering is a future refinement.

## Migration Plan

- Build `surya-ocr` and the updated `api` images; `docker compose up -d` adds the
  `surya-ocr` service. The api does not hard-depend on it (no startup coupling),
  so the stack comes up even while Surya downloads models on first boot.
- Rollback: set `USE_SURYA_OCR=false` (or stop the worker) to revert to the
  ocrmypdf/Tesseract fallback with no code change. Set `SENTENCE_GAP_MS=0` to
  disable the added pause.

## Open Questions

- Per-page OCR progress streaming from the Surya worker (the ocrmypdf path had a
  progress bar) is not yet implemented — worth adding for long scanned books.
- Whether to adopt Surya's layout/reading-order model for multi-column documents.
