## 1. OCR gRPC contract

- [x] 1.1 Add `proto/ocr.proto` with `Ocr.Recognize(OcrRequest) -> OcrResponse` and a `Health` RPC; `OcrWord` carries text, 1-based page, and PDF user-space box (x, y, width, height) + confidence.
- [x] 1.2 Confirm the proto auto-compiles into the C# client (`PDFtoTTS.Grpc`, namespace `PDFtoTTS.Grpc.Ocr.V1`) via the existing `..\..\proto\*.proto` glob.

## 2. Surya OCR worker

- [x] 2.1 Scaffold `workers/surya-ocr/` mirroring the whisperx worker (`worker/__init__.py`, `errors.py`, `health.py`, `healthcheck.py`, `server.py`, `backend.py`, `tests/`).
- [x] 2.2 Implement the backend: render pages with pypdfium2 at `OCR_RENDER_DPI` (default 200), run Surya detection+recognition, sort lines top-to-bottom, convert pixel boxes (top-left) to PDF points (bottom-left), and split lines into word boxes by character width.
- [x] 2.3 Implement the gRPC servicer (`OcrServicer`) and `serve()` with model `load()` + `warmup()`; report health NOT_READY→SERVING.
- [x] 2.4 Dockerfile on `rocm/pytorch` base: pin the preinstalled ROCm torch via a generated pip constraint, pin `numpy<2` and `transformers>=4.45,<4.52`, assert `torch.version.hip` and a `torch.numpy()` roundtrip at build time, compile protos, set env + HEALTHCHECK.
- [x] 2.5 Unit-test the pure word-splitting / coordinate-flip logic (no torch needed).

## 3. API integration

- [x] 3.1 Add `IOcrEngine` + `SuryaOcr` adapter (gRPC client → `ExtractionResult` of positioned `SourceWord`s) and a `DisabledOcrEngine`.
- [x] 3.2 Register the `Ocr.OcrClient` (`SURYA_GRPC`) and bind `IOcrEngine` (Surya when `USE_SURYA_OCR`, else disabled — default off in mock mode).
- [x] 3.3 Rework `DocumentPipeline` scanned path: try Surya first, fall back to ocrmypdf/Tesseract on disabled/unreachable/empty; log which engine ran.

## 4. Sentence prosody

- [x] 4.1 Add `Terminator` to `NormToken`; capture `.`/`?`/`!` in `TextNormalizer` on all sentence-ending paths.
- [x] 4.2 Re-attach the terminator to chunk text in `Chunker.RenderToken` (clean token text retained for alignment); add a chunker test.
- [x] 4.3 Confirm the Kokoro worker inserts a configurable `SENTENCE_GAP_MS` pause per sentence (longer after `?`/`!`).

## 5. Fallback OCR accuracy

- [x] 5.1 Upgrade the api image: install `tesseract-ocr-osd`, overwrite the English model with `tessdata_best`.
- [x] 5.2 Enable `--rotate-pages` + `--deskew` by default in `PdfOcr`, with `--clean`/`--oversample` opt-in via env.

## 6. Compose + verification

- [x] 6.1 Add the `surya-ocr` ROCm service (modelcache volume) and `SURYA_GRPC` on the api; no hard `depends_on` from api.
- [x] 6.2 Build images, bring up the non-mocked stack, and verify a scanned upload runs through Surya end-to-end (accurate text, correct reading order, populated boxes) with the ocrmypdf fallback intact.
