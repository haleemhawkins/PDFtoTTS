## Why

Scanned / image-only PDFs (a large share of real books) extract zero text, so
they produced empty, unreadable sessions. The first fix shelled out to
ocrmypdf/Tesseract, but accuracy and reading order were weak. Separately, sentence
terminators were dropped before synthesis, so the voice ran sentences together
with no period intonation or pause. This change documents the implemented
remedy: a high-accuracy GPU OCR engine (Surya) with a Tesseract fallback, and
sentence prosody driven by terminators that survive into the synthesized text.

## What Changes

- Add scanned-PDF OCR: a PDF with pages but no extractable words is OCR'd into
  the same positioned word stream as a native PDF, so it reads and highlights
  identically and is persisted (re-open never re-OCRs).
- Add a **Surya OCR gRPC GPU worker** (PyTorch / torch-rocm) as the primary
  engine: it renders each page at a configurable DPI (default 200), runs
  detection + recognition, and returns words **in reading order** with bounding
  boxes converted to PDF user space (origin bottom-left, points).
- Keep **ocrmypdf / Tesseract** (tessdata_best model, `--rotate-pages`,
  `--deskew`) as the fallback when Surya is disabled (e.g. mock mode) or
  unreachable, so scans still work without the GPU worker.
- Fix sentence prosody: sentence terminators (`.` `?` `!`), which normalization
  strips from token text, are **re-attached to the chunk text sent to synthesis**
  so the voice gets sentence intonation; the clean (terminator-free) token text
  is still used for forced alignment.
- Insert a natural, configurable pause after each sentence in the TTS worker so
  periods get a beat instead of rushing into the next sentence.

## Capabilities

### New Capabilities
<!-- No brand-new capability spec; all deltas land on existing capabilities. -->

### Modified Capabilities
- `document-processing`: ADD scanned-PDF OCR (Surya primary, Tesseract fallback)
  producing positioned words in reading order; MODIFY chunking so sentence
  terminators are re-attached to the synthesized chunk text while alignment text
  stays terminator-free.
- `tts-synthesis`: ADD a natural inter-sentence pause so sentence-ending
  punctuation yields a beat and correct intonation.

## Impact

- New worker: `workers/surya-ocr/` (proto `proto/ocr.proto`, gRPC `Ocr.Recognize`).
- API: `IOcrEngine` + `SuryaOcr` adapter, DI + `SURYA_GRPC` config, and
  `DocumentPipeline` Surya-first-then-ocrmypdf path.
- Text pipeline: `NormToken.Terminator`, `TextNormalizer`, and `Chunker` re-attach
  terminators to chunk text.
- TTS worker: sentence splitting + `SENTENCE_GAP_MS` pause in `kokoro-tts`.
- Compose: `surya-ocr` ROCm service + `SURYA_GRPC` on the api; api Dockerfile ships
  tessdata_best + osd for the fallback.
- Dependency pins on the Surya worker: `numpy<2` and `transformers>=4.45,<4.52`
  to protect the base image's ROCm torch and Surya's import surface.
