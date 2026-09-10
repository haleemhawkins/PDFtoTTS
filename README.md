# PDFtoTTS

Upload a PDF or EPUB, hear it read aloud, and watch the spoken word highlight in
the page as it's spoken. Self-hosted, runs entirely on your own GPU.

<!-- Record a 20-30s screen capture of a page reading with the highlight tracking
     the voice, save it as docs/demo.gif, and swap this comment for:
     ![PDFtoTTS reading a PDF with the spoken word highlighted](docs/demo.gif) -->

## Why this is harder than it looks

Most TTS readers estimate word timings from the synthesizer — how long *should*
this word take — and the highlight drifts a few words off within a paragraph.
PDFtoTTS doesn't estimate. It synthesizes the audio first, then force-aligns the
known transcript against that audio with WhisperX and wav2vec2, so the timings
come from the sound that actually got produced.

That leaves three problems worth solving:

- **The spoken text isn't the printed text.** "1999" is read as three words and
  "Dr." as one; the printed page has running headers and page numbers a person
  would skip. So every normalized token carries the range of source words it came
  from, and [`ChunkMerger`](src/PDFtoTTS.Orchestration/ChunkMerger.cs) projects
  alignment timings back onto the original words — matching by LCS over fuzzy-equal
  tokens, interpolating anything unmatched rather than dropping it.
- **Audio has to start in under a second.** A full document can be hundreds of
  chunks. The pipeline synthesizes the first chunk small and alone for fast
  time-to-first-audio, then opens to bounded parallelism, streaming each chunk to
  the browser over SignalR as it finishes while preserving document order.
- **Scanned PDFs have no text at all.** Image-only PDFs go through a Surya GPU OCR
  pass that returns words already positioned in PDF user space, falling back to
  ocrmypdf when the GPU worker isn't there.

Built and tuned for an AMD RX 7800 XT on ROCm.

## Try it

```bash
docker compose up --build
```

Then open <http://localhost:5173>. First boot downloads the models and warms the
GPU kernels, so give it a few minutes; later boots are fast.

No GPU? `USE_MOCK_WORKERS=true` swaps both workers for in-process fakes and the
whole pipeline still runs end to end.

## Architecture

```
Browser (React, PDF.js/epub.js, SignalR)
   │  REST + SignalR
PDFtoTTS.Api (.NET 10 Minimal API + ReaderHub)
   │  gRPC (paths over the wire, not bytes)
   ├── kokoro-tts      (Python, PyTorch/torch-rocm)  text → WAV on /data
   ├── whisperx-align  (Python, torch-rocm)          WAV + transcript → word timings
   └── surya-ocr       (Python, torch-rocm)          scanned pages → positioned words
         shared volume /data ── audio + originals
```

Audio never crosses gRPC as bytes — workers write to a shared volume and return a
path. Full specification lives in OpenSpec under `openspec/`; the capability specs
in `openspec/specs/` describe what the system does today.

## Prerequisites

- **AMD GPU** (built for the RX 7800 XT / gfx1101) with ROCm, on Linux.
- Docker + Docker Compose with access to `/dev/kfd` and `/dev/dri`.
- For local .NET/Python development: .NET 10 SDK, Python 3.11+.

The workers spoof the RX 7800 XT as the supported gfx1100 via
`HSA_OVERRIDE_GFX_VERSION=11.0.0` (already set in `docker-compose.yml`).

## First boot

On first start each worker downloads its model from Hugging Face into the
`modelcache` volume (Kokoro's TTS weights, WhisperX's wav2vec2) and warms up GPU
kernels; the model and MIOpen kernel caches persist across restarts, so later
boots are fast. Workers report healthy once their models are resident and warmed
(the API waits for this via `depends_on: condition: service_healthy`); the first
boot can take a few minutes (`start_period` is generous).

- API: <http://localhost:8080>  (`GET /healthz`, `GET /api/voices`)
- Frontend: <http://localhost:5173>

### Quick API smoke test

```bash
# upload a PDF
curl -F file=@book.pdf http://localhost:8080/api/documents
# create a session (use the returned document id)
curl -X POST http://localhost:8080/api/documents/<docId>/sessions \
  -H 'content-type: application/json' -d '{"voice":"af_heart","speed":1.0,"language":"en"}'
# poll the session, then fetch chunks (words carry timings + bboxes)
curl http://localhost:8080/api/sessions/<sessionId>/chunks
```

## Local development

```bash
# .NET: build + test the whole solution
DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test        # 104 tests

# Python workers: generate stubs, then run the (GPU-free) unit tests
./workers/gen_proto.sh
cd workers/kokoro-tts     && pip install -r requirements-dev.txt && pytest   # incl. WAV-format + text-sanitizer regression guards
cd workers/whisperx-align && pip install -r requirements-dev.txt && pytest
cd workers/shared         && pytest        # shared path guard (no deps beyond pytest)

# Frontend unit tests
cd frontend && npm test

# Frontend e2e smoke test — runs against an ALREADY-RUNNING stack and exercises
# the full path (upload -> synthesize -> SignalR -> playback -> decode -> highlight).
# It hits the nginx-served bundle, so it catches failures the Vite dev server hides
# (e.g. .mjs MIME, float32 WAV decode). First time: install the browser.
docker compose up --build -d                     # or point E2E_BASE_URL at a running stack
cd frontend && npx playwright install chromium && npm run test:e2e
```

> Note: on networks where IPv6 egress is broken, prefix .NET restores with
> `DOTNET_SYSTEM_NET_DISABLEIPV6=1` (NuGet otherwise hangs preferring IPv6).

## Project layout

| Path | What |
|---|---|
| `proto/` | gRPC contracts (source of truth for .NET + Python) |
| `src/PDFtoTTS.Core` | shared records + text pipeline (normalize, map, chunk) |
| `src/PDFtoTTS.Ingestion` | PDF/EPUB extraction (PdfPig, VersOne.Epub) |
| `src/PDFtoTTS.Orchestration` | merge algorithm + synthesis pipeline |
| `src/PDFtoTTS.Api` | REST + SignalR host, gRPC client adapters |
| `workers/kokoro-tts` | Kokoro TTS gRPC worker |
| `workers/whisperx-align` | WhisperX alignment gRPC worker |
| `workers/surya-ocr` | Surya GPU OCR worker for scanned PDFs |
| `workers/shared` | code shared by the workers (copied to `/app/shared` in each image) |
| `tests/` | .NET unit + integration tests |
