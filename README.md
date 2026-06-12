# PDFtoTTS

Self-hosted PDF/EPUB text-to-speech reader with synchronized word highlighting.
Upload a document, hear it read aloud by [Kokoro](https://github.com/thewh1teagle/kokoro-onnx),
and watch the spoken word highlight in the rendered page in real time — word
timings come from [WhisperX](https://github.com/m-bain/whisperX) forced
alignment, not synthesis estimates.

## Architecture

```
Browser (React, PDF.js/epub.js, SignalR)
   │  REST + SignalR
PDFtoTTS.Api (.NET 10 Minimal API + ReaderHub)
   │  gRPC (paths over the wire, not bytes)
   ├── kokoro-tts      (Python, onnxruntime-rocm)   text → WAV on /data
   └── whisperx-align  (Python, torch-rocm)          WAV + transcript → word timings
         shared volume /data ── audio + originals
```

The full specification lives in OpenSpec:
`openspec/changes/add-tts-reader/` (`design.md` is the exhaustive technical doc).

## Prerequisites

- **AMD GPU** (built for the RX 7800 XT / gfx1101) with ROCm, on Linux.
- Docker + Docker Compose with access to `/dev/kfd` and `/dev/dri`.
- For local .NET/Python development: .NET 10 SDK, Python 3.11+.

The workers spoof the RX 7800 XT as the supported gfx1100 via
`HSA_OVERRIDE_GFX_VERSION=11.0.0` (already set in `docker-compose.yml`).

## Run the stack

```bash
docker compose up --build
```

On first start the Kokoro worker downloads its ONNX model into the `modelcache`
volume and WhisperX downloads wav2vec2 into `HF_HOME`; both persist across
restarts. Workers report healthy once their models are resident (the API waits
for this via `depends_on: condition: service_healthy`).

- API: <http://localhost:8080>  (`GET /healthz`, `GET /api/voices`)
- Frontend: <http://localhost:5173> (added in group 6)

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
DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test        # 82 tests

# Python workers: generate stubs, then run the (GPU-free) unit tests
./workers/gen_proto.sh
cd workers/kokoro-tts     && pip install -r requirements-dev.txt && pytest   # 12 tests
cd workers/whisperx-align && pip install -r requirements-dev.txt && pytest   # 6 tests
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
| `tests/` | .NET unit + integration tests |
