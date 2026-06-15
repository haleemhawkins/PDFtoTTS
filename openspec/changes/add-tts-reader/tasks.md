## 1. Contracts & scaffold (foundation — everything depends on these)

- [x] 1.1 Replace streaming `proto/kokoro.proto` and `proto/alignment.proto` with the path-based contracts from design §3.3/§4.3; keep `proto/common.proto` (Health, Voice). Validate with `protoc`.
- [x] 1.2 Wire .NET codegen: `PDFtoTTS.Grpc` generates gRPC **clients** from `proto/`; `PDFtoTTS.Core` holds the shared C# records from design §7.2.
- [x] 1.3 Install `aspnet-runtime-10.0` + `aspnet-targeting-pack-10.0`; restore + build the solution end to end (resolve the pending NuGet network issue).
- [x] 1.4 Set up Python codegen (`grpc_tools.protoc`) shared by both workers from `proto/` (`workers/gen_proto.sh`).

## 2. Document processing (isolated, pure, fully unit-testable)

- [x] 2.1 PdfPig extraction → `WordData[]` with bounding boxes, reading-order, ligature/hyphen handling (design §2.1). Test against fixture PDFs.
- [x] 2.2 EPUB extraction (VersOne.Epub + AngleSharp) → `SourceWord[]` with spine locators, `page=null` (design §2.2). Test against fixture EPUBs.
- [x] 2.3 Text normalization passes (numbers, ordinals, currency, percent, abbreviations, acronyms, URLs, units, punctuation) with source-index tagging (design §2.3). Golden-file unit tests per rule.
- [x] 2.4 Source↔token mapping (`tokenToSource`, `sourceToTokens`) for 1→N and N→1 cases (design §2.4). Property test: full coverage, no gaps.
- [x] 2.5 Chunking within `MaxTokensPerChunk` at sentence/paragraph boundaries (design §2.5). Test: contiguous, ordered, within limit.
- [x] **Checkpoint A:** given a PDF/EPUB, produce ordered chunks + word map + bboxes with no GPU involved.

## 3. TTS worker (isolated — drive over gRPC, no api needed)

- [x] 3.1 gRPC server skeleton + `Health` + `ListVoices` (design §3.3).
- [x] 3.2 ONNX provider selection ROCm→CUDA→CPU with fallback + logging (design §3.2). Unit-tested.
- [x] 3.3 Kokoro load + synth → WAV PCM float32 24 kHz mono written to `/data` path; return duration + phoneme durations (design §3.1/§3.4). WAV write unit-tested; real Kokoro inference needs GPU (Checkpoint B).
- [x] 3.4 Concurrency semaphore + error/status mapping + retryable classification (design §3.5/§3.6). Unit-tested.
- [x] 3.5 Dockerfile (ROCm base) + healthcheck module.
- [ ] **Checkpoint B:** `grpcurl`/test client synthesizes a chunk to a playable WAV on GPU and on CPU fallback. (needs your RX 7800 XT — code ready)

## 4. Alignment worker (isolated — fixture WAV + transcript)

- [x] 4.1 gRPC server skeleton + `Health` reporting model-resident state (design §4.3).
- [x] 4.2 Preload wav2vec2 at startup, resident in VRAM, per-language cache (design §4.2). Real load needs GPU (Checkpoint C).
- [x] 4.3 WhisperX forced alignment (single full-clip segment, no ASR) → `WordTiming[]` with confidence (design §4.1). Implemented; needs GPU to validate.
- [x] 4.4 Low-confidence flagging + threshold; VRAM/concurrency bounds (design §4.5/§4.7). Unit-tested.
- [x] 4.5 Dockerfile (ROCm PyTorch base) + healthcheck module.
- [ ] **Checkpoint C:** given fixture WAV + transcript, return per-word ms timings; verify monotonic, in-range, word-count = transcript. (needs your RX 7800 XT — code ready)

## 5. Backend orchestrator (integrates 2–4)

- [x] 5.1 Minimal API REST endpoints + error envelope (design §5.1); upload validation by magic bytes; document storage.
- [x] 5.2 gRPC clients for both workers from DI; `/voices` proxies Kokoro `ListVoices`; `/healthz` aggregates worker health.
- [x] 5.3 Session lifecycle state machine `queued→processing→streaming→complete|error` (design §5.4). In-memory stores + `SessionPipeline` runner.
- [x] 5.4 Channels pipeline: bounded concurrency, synth→align per chunk, ordering buffer by `chunkIndex`, backpressure, cancellation (design §5.5). Sliding-window orchestrator in `PDFtoTTS.Orchestration`, tested with fakes.
- [x] 5.5 Timestamp + bbox merge with anchor/fuzzy matching + interpolation + `degraded` marking (design §5.3). `ChunkMerger` with LCS/fuzzy alignment, tested.
- [x] 5.6 Audio file naming + Range-enabled audio endpoint (design §5.6).
- [x] 5.7 `ReaderHub` SignalR: `Subscribe`/`Unsubscribe`, `ChunkReady`/`Progress`/`SessionStatus`/`Error`, late-subscriber backfill (design §5.2).
- [x] **Checkpoint D:** end-to-end over gRPC (real or mocked workers): upload → ProcessedChunks streamed in order with merged timings + bboxes.

## 6. Frontend reader (integrates with api)

- [x] 6.1 Upload view + format validation + `Document` creation (design §6.1/§6.8).
- [x] 6.2 PDF.js renderer + scale-aware bbox overlay layer (design §6.2/§6.6).
- [x] 6.3 epub.js renderer + word-span injection matching server word indices (design §6.3). Index alignment is Vitest-tested (mirrors server splitting); epub.js iframe rendering is browser-validated at Checkpoint E.
- [x] 6.4 SignalR client: connect, `Subscribe`, event fan-out, reconnect + backfill (design §6.4/§6.8).
- [x] 6.5 Audio queue: ordered chunk buffer, Web Audio playback, pause-on-underrun (design §6.4). Chunk-sequential; sample-accurate gapless is a refinement.
- [x] 6.6 rAF sync engine: binary search active word, continuous cross-chunk time, hold-on-gap (design §6.5). Vitest-tested.
- [~] 6.7 Auto page-turn to the active word done; manual-scroll grace period still to add (design §6.7).
- [x] 6.8 Playback controls: play/pause, speed (audio + sync scale), voice (new session), click-to-seek (design §6.9).
- [ ] **Checkpoint E:** upload a real PDF and EPUB, hear playback, see the spoken word highlight and auto-scroll in sync. (needs browser + running backend on your GPU)

## 7. Containerization & integration

- [x] 7.1 `docker-compose.yml` with ROCm passthrough, `HSA_OVERRIDE_GFX_VERSION=11.0.0`, shared `appdata` + `modelcache` volumes, healthchecks, dependency order (design §8). `docker compose config` validates.
- [x] 7.2 Per-service Dockerfiles (design §8.3); model cache warmup (Kokoro entrypoint download, WhisperX `HF_HOME`).
- [ ] 7.3 Full-stack bring-up: `docker compose up`, upload → synchronized playback through nginx proxy. (needs your GPU; frontend service is group 6)
- [ ] 7.4 Edge-case pass: GPU-off (CPU fallback), low-confidence interpolation, word-count mismatch, corrupted file, network drop mid-stream (design §9). (needs running stack)

## 8. Validation & docs

- [x] 8.1 `openspec validate add-tts-reader --strict` passes.
- [x] 8.2 README run instructions (Arch + ROCm prerequisites, `HSA_OVERRIDE`, first-run model download).
- [ ] 8.3 Resolve design Open Questions (default voice, in-memory vs SQLite, multi-language scope, cleanup TTL).

## 9. Reader UX refinements (post-plan, from real-device use)

- [x] 9.1 Running header/footer/page-number removal in PDF extraction: recurrence + margin-band detection, plus a bare page-number rule (arabic/roman). Unit-tested in `PdfTextProcessing`.
- [x] 9.2 Explicit-start playback: play control disabled until audio at the current position is buffered; loading→ready cue; no auto-play on first-ready, chunk-arrival-while-paused, restore, or navigation.
- [x] 9.3 Resume position across backgrounding (clean pause on visibility/interruption, no auto-resume) and exact-word persistence across full reload.
- [x] 9.4 Manual navigation (Prev/Next, drawer) positions + preloads the page paused, debounced; playback-driven page turns keep reading.
- [x] 9.5 Page/chapter navigation drawer: lazy page thumbnails + PDF outline chapters resolving to pages.
- [x] 9.6 Immersive, low-chrome reading: merged transport bar + auto-hide chrome on a tap.
