## Why

Readers with dyslexia, visual impairment, or who simply prefer audio currently
have no self-hosted tool that reads their own PDF/EPUB library aloud *while
showing which word is being spoken*. Cloud TTS services are expensive at book
length, leak private documents, and cannot highlight the source text. We have
the GPU (AMD RX 7800 XT, 16 GB) and open models (Kokoro TTS, WhisperX) to do
this locally with high quality and exact word-level sync.

## What Changes

- Add a web application where a user uploads a PDF or EPUB and gets it read
  aloud with the currently spoken word highlighted in the rendered document, in
  real time.
- Add a **.NET 10 Minimal API orchestrator** that extracts text (with word
  bounding boxes), normalizes it for TTS, chunks it, and coordinates two GPU
  workers, streaming results to the browser over SignalR.
- Add a **Kokoro TTS gRPC worker** (Python, `onnxruntime-rocm`) that turns a
  text chunk into WAV PCM float32 audio plus phoneme durations.
- Add a **WhisperX forced-alignment gRPC worker** (Python, wav2vec2) that maps a
  known transcript onto synthesized audio to produce per-word timestamps.
- Add a **React + TypeScript reader** (PDF.js, epub.js, WaveSurfer.js, SignalR)
  that renders the document, plays streamed audio, and runs a
  `requestAnimationFrame` sync engine to highlight the active word.
- Add **Docker Compose** topology with AMD ROCm passthrough
  (`/dev/kfd`, `/dev/dri`, `HSA_OVERRIDE_GFX_VERSION=11.0.0`) and a shared audio
  volume.

## Capabilities

### New Capabilities
- `document-processing`: Extract text + word bounding boxes from PDF/EPUB,
  normalize text for TTS (numbers, abbreviations, URLs, currency, punctuation),
  chunk it within token limits, and maintain a mapping from original source
  words to normalized TTS tokens.
- `tts-synthesis`: Synthesize a normalized text chunk into WAV PCM float32 audio
  with phoneme/word durations via Kokoro ONNX, selecting the best available
  ONNX Runtime execution provider (ROCm → CUDA → CPU).
- `forced-alignment`: Produce per-word `{startMs, endMs, confidence}` timestamps
  by force-aligning a known transcript against synthesized audio with WhisperX +
  wav2vec2, handling low-confidence and normalized-token edge cases.
- `reader-backend`: Orchestrate the pipeline (upload → extract → synthesize →
  align → merge → stream), expose REST endpoints and a SignalR hub, manage TTS
  session lifecycle, and merge word timestamps with source bounding boxes.
- `reader-frontend`: Render PDF/EPUB, manage the streamed audio queue, and run
  the real-time word-highlight sync engine with auto-scroll and playback
  controls (play/pause, speed, voice, seek-by-word).

### Modified Capabilities
<!-- None. Greenfield project; no existing specs in openspec/specs/. -->

## Impact

- **New services**: `PDFtoTTS.Api` (.NET 10), `kokoro-tts` worker (Python),
  `whisperx-align` worker (Python), `frontend` (React/Vite).
- **New contracts**: gRPC `.proto` files under `proto/` (Kokoro + Alignment),
  SignalR hub contract, REST API surface.
- **Dependencies**: PdfPig, EpubNet, Grpc.Net, ASP.NET SignalR (.NET);
  kokoro-onnx, onnxruntime-rocm, soundfile (TTS); whisperx, torch-rocm,
  transformers (alignment); pdfjs-dist, epubjs, wavesurfer.js,
  @microsoft/signalr (frontend).
- **Infrastructure**: Docker Compose with ROCm device passthrough; a shared
  named volume for audio chunks; GPU is shared (not partitioned) between the two
  Python workers.
- **Hardware/OS**: AMD RX 7800 XT (gfx1101) on Arch Linux, ROCm with
  `HSA_OVERRIDE_GFX_VERSION=11.0.0`.
