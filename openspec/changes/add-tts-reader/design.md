> **Implementation note (shipped):** The Kokoro worker below is designed around
> **Kokoro ONNX via `onnxruntime-rocm`**; that was **abandoned during
> implementation** (onnxruntime's ROCm EP wasn't usable on the RDNA3/gfx1101
> card). The shipped worker uses the **PyTorch `kokoro` package (`KPipeline`) on
> torch-rocm** — GPU when torch sees one, else CPU — so §3's ONNX
> provider-priority machinery doesn't exist; device selection is just torch's.
> Two more deltas from §3 as designed: `Synthesize` is a **unary** RPC that
> writes the WAV to the shared volume and returns its path + duration (no
> server-streamed audio bytes — consistent with the path contract elsewhere in
> this doc), and the WAV is **16-bit PCM, not float32** (browser Web Audio
> `decodeAudioData` silently fails on IEEE-float WAV). Phoneme durations are
> returned empty; WhisperX forced alignment is the sole timing source. There is
> no orchestrator retry loop: the worker degrades per-sentence instead
> (progressively safer text renderings, then a proportional silence), and worker
> calls carry deadlines that surface friendly errors when a worker hangs.
> The `tts-synthesis` delta spec reflects the shipped behavior.

## Context

This is a greenfield, self-hosted web application that reads a user's PDF/EPUB
documents aloud while highlighting the spoken word in the rendered document, in
real time. It runs entirely on local hardware: an AMD Radeon RX 7800 XT
(navi32, `gfx1101`, 16 GB VRAM) on Arch Linux, using ROCm with
`HSA_OVERRIDE_GFX_VERSION=11.0.0` (the RX 7800 XT is not an officially supported
ROCm SKU, so we spoof it as `gfx1100`).

The system is four services:

- **`frontend`** — React + TypeScript (Vite). Renders documents (PDF.js,
  epub.js), plays streamed audio (WaveSurfer.js / Web Audio), and runs the
  word-sync engine. Talks to the backend over REST + SignalR.
- **`api`** — .NET 10 ASP.NET Core Minimal API + SignalR hub. The orchestrator:
  extraction, normalization, chunking, worker coordination, timestamp/bbox
  merge, streaming. Talks to workers over gRPC.
- **`kokoro-tts`** — Python gRPC worker. Kokoro ONNX via `onnxruntime-rocm`.
  Text chunk → WAV PCM float32 on the shared volume + phoneme durations.
- **`whisperx-align`** — Python gRPC worker. WhisperX + wav2vec2. Audio path +
  transcript → per-word `{startMs, endMs, confidence}`.

Storage is the local filesystem exposed as a Docker named volume shared between
`api`, `kokoro-tts`, and `whisperx-align`. Audio never transits gRPC as bytes;
workers read/write WAV files by path on the shared volume, which keeps gRPC
messages small and avoids double-buffering large PCM payloads.

The two Python workers **share** the single GPU. ROCm has no MIG-style hard
partitioning, so they share VRAM cooperatively; each worker bounds its own
concurrency (default 1) and VRAM budget. wav2vec2 (~360 MB) and Kokoro
(~330 MB) both stay resident, leaving ample headroom in 16 GB for activations.

> Naming note: the scaffolded `proto/` files in the repo currently model audio
> as **streamed bytes**. This design supersedes them with the **shared-volume
> path** contract below (matches §3/§4/§5/§8). Reconciling the scaffold to this
> contract is the first task in `tasks.md`.

## Goals / Non-Goals

**Goals:**

- Exact word-level highlight sync (target drift < 50 ms perceived) driven by
  forced alignment, not synthesis-time estimates alone.
- Streaming UX: playback and highlighting begin after the first chunk, not after
  the whole book is processed.
- Robust normalization so numbers/abbreviations/currency are spoken naturally
  *and* still map back to the exact source word for highlighting.
- Run fully offline on one AMD GPU with graceful CPU fallback.
- A spec precise enough that each service can be built and tested in isolation.

**Non-Goals:**

- No cloud TTS/ASR, no multi-tenant accounts/auth (single-user, LAN-trusted).
- No open-vocabulary transcription — alignment only against a known transcript.
- No DOCX/HTML ingestion in v1 (PDF + EPUB only).
- No horizontal scaling / GPU clustering; single-node, single-GPU.
- No persistent database in v1 — session/document state is in-memory + the
  shared volume; durable metadata store is a future change.

## Decisions

### 1. System Architecture

#### 1.1 Service diagram

```
                            ┌──────────────────────────────────────┐
                            │              Browser                  │
                            │  React + TS (Vite)                    │
                            │  ┌────────────┐  ┌─────────────────┐  │
                            │  │ PDF.js /   │  │ Sync engine     │  │
                            │  │ epub.js    │  │ (rAF + bsearch) │  │
                            │  │ + overlay  │  │ WaveSurfer.js   │  │
                            │  └────────────┘  └─────────────────┘  │
                            └───────┬───────────────────┬───────────┘
                          REST (HTTP)            SignalR (WebSocket)
                                    │                   │
                    ┌───────────────▼───────────────────▼──────────────┐
                    │                  api  (.NET 10)                   │
                    │  Minimal API  +  ReaderHub (SignalR)              │
                    │  ┌───────────────────────────────────────────┐   │
                    │  │ Orchestrator pipeline (Channels)          │   │
                    │  │  extract → normalize → chunk →            │   │
                    │  │  synth(gRPC) → align(gRPC) → merge → push  │   │
                    │  └───────────────────────────────────────────┘   │
                    │  PdfPig · EpubNet · Grpc.Net.Client               │
                    └─────────┬───────────────────────┬────────────────┘
                       gRPC   │                        │   gRPC
                              ▼                        ▼
                ┌──────────────────────┐   ┌──────────────────────────┐
                │     kokoro-tts       │   │      whisperx-align       │
                │  Python gRPC         │   │  Python gRPC              │
                │  kokoro-onnx         │   │  whisperx + wav2vec2      │
                │  onnxruntime-rocm    │   │  torch (ROCm)             │
                └──────────┬───────────┘   └────────────┬─────────────┘
                           │ write WAV         read WAV  │  (paths only over gRPC)
                           ▼                             ▼
                    ┌──────────────────────────────────────────────┐
                    │     shared named volume:  /data              │
                    │   /data/originals/{docId}.{ext}              │
                    │   /data/audio/{sessionId}/{chunkIndex:D5}.wav│
                    └──────────────────────────────────────────────┘
                           ▲                             ▲
                           └──────────── /data ──────────┘
                                  (also mounted in api)

   GPU (RX 7800 XT, /dev/kfd + /dev/dri) shared by both Python workers
```

#### 1.2 Data flow: upload → synchronized playback

1. **Upload.** Browser `POST /api/documents` (multipart). `api` validates magic
   bytes, writes `/data/originals/{docId}.{ext}`, returns `Document{status:
   queued}`.
2. **Session.** Browser `POST /api/documents/{docId}/sessions {voice, speed,
   language}` → `TtsSession{status: processing}`. Browser opens SignalR, calls
   `Subscribe(sessionId)`.
3. **Extract.** `api` extracts `WordData[]` with bounding boxes (PdfPig/EpubNet),
   assigns global word indices.
4. **Normalize + map.** Text → normalized token stream; build
   `sourceIndex ↔ tokenIndex` map.
5. **Chunk.** Normalized tokens → ordered chunks (≤ max tokens, sentence
   boundaries), each tagged with its source word range + char offset.
6. **Pipeline (bounded concurrency).** For each chunk, in order out:
   a. `kokoro-tts.Synthesize(text)` → writes `/data/audio/{sid}/{idx}.wav`,
      returns path + phoneme durations.
   b. `whisperx-align.Align(audioPath, transcript)` → per-token timings.
   c. `api` merges token timings + normalization map + `WordData` bboxes →
      `ProcessedChunk{chunkIndex, audioUrl, words[]}`.
   d. `api` pushes `ChunkReady(ProcessedChunk)` over SignalR (in `chunkIndex`
      order) and emits `Progress`.
7. **Playback.** Browser enqueues chunk audio, begins playback on chunk 0, runs
   the rAF sync engine to highlight the active word, auto-scrolls.
8. **Complete.** After the last chunk: `SessionStatus(complete)`.

#### 1.3 Docker Compose topology

Four services on one user-defined bridge network `readernet`:

- `frontend` → depends_on `api` (healthy). Ports `5173:80` (nginx serving the
  built SPA; proxies `/api` and `/hubs` to `api`).
- `api` → depends_on `kokoro-tts` (healthy), `whisperx-align` (healthy). Port
  `8080:8080`. Mounts `appdata:/data`.
- `kokoro-tts` → GPU devices, `HSA_OVERRIDE_GFX_VERSION=11.0.0`, mounts
  `appdata:/data` and `modelcache:/models`.
- `whisperx-align` → same GPU + env, mounts `appdata:/data` and
  `modelcache:/models`.

#### 1.4 Shared volume strategy

- One named volume `appdata` mounted at `/data` in `api`, `kokoro-tts`, and
  `whisperx-align`.
- Layout: `/data/originals/{docId}.{ext}` and
  `/data/audio/{sessionId}/{chunkIndex:D5}.wav`.
- Workers receive/return **paths relative to `/data`** (never absolute host
  paths) so the contract is mount-point independent.
- `api` owns lifecycle: it creates `audio/{sessionId}/` before synthesis and is
  the only writer of `originals/`. Workers only write their own chunk WAV.
- A separate `modelcache` volume holds downloaded ONNX/wav2vec2 weights so
  rebuilds don't re-download multi-hundred-MB models.
- Cleanup: a session's audio dir is deleted when the session is deleted
  (`DELETE /api/sessions/{id}`), when a new session supersedes it (a voice/speed
  change or jump re-synth for the same document), when its document is deleted,
  and — since sessions are in-memory only — all leftover session audio is purged
  at API startup. (A TTL sweep was considered and is unnecessary given these.)

#### 1.5 gRPC service boundaries

- `api` is a **gRPC client** to both workers; workers never call each other.
- Contracts live in `proto/` and are code-generated into the .NET client and
  both Python workers (single source of truth).
- Bytes never cross gRPC: requests/responses carry **paths + small metadata**.
- Each worker also exposes `Health` for compose healthchecks and GPU warmup
  state (`NOT_READY` until the model is resident, then `SERVING`).

### 2. Document Processing Pipeline

#### 2.1 PDF extraction with bounding boxes (PdfPig)

- Use `UglyToad.PdfPig`. For each page, get `Words` via
  `page.GetWords(NearestNeighbourWordExtractor.Instance)` and order with
  `DocstrumBoundingBoxes`/`RecursiveXYCut` for reading order on multi-column
  pages.
- For each `Word`: capture `Text`, `page.Number` (1-based), and
  `word.BoundingBox` (`Left, Bottom, Width, Height`) in PDF user space (origin
  bottom-left). Store raw; the frontend flips Y and applies the render scale.
- Normalize ligatures (`ﬁ→fi`, `ﬂ→fl`) and rejoin soft-hyphen / end-of-line
  hyphenated words into one `WordData` whose bbox is the union of the fragments.
- Assign a global, document-wide zero-based `index` in reading order across all
  pages.

#### 2.2 EPUB extraction and word position mapping (EpubNet)

- Use `EpubNet` (a.k.a. `VersOne.Epub`) to read spine items in order. For each
  XHTML chapter, parse with AngleSharp, walk text nodes in document order, split
  on Unicode word boundaries.
- Each `WordData`: `text`, `page = null`, `spineHref`, and a locator
  `{spineHref, textNodePath, wordOrdinal}` sufficient for the client to find the
  injected span. No server-side pixel bbox (reflowable).
- The frontend injects `<span data-wi="{index}">` around each word at render
  time using the same boundary algorithm, so `index` is the contract between
  server timings and client spans.

#### 2.3 Text normalization rules (before TTS)

Deterministic, ordered passes. Each pass emits tokens tagged with the source
word index range they came from.

| Class | Input | Spoken output |
|---|---|---|
| Cardinal | `1999` | `nineteen ninety nine` (year heuristic) / `one thousand nine hundred ninety nine` |
| Decimal | `3.14` | `three point one four` |
| Ordinal | `3rd`, `21st` | `third`, `twenty first` |
| Currency | `$1,250.50` | `one thousand two hundred fifty dollars and fifty cents` |
| Percent | `50%` | `fifty percent` |
| Abbrev. (title) | `Dr.`, `Mr.`, `St.` | `Doctor`, `Mister`, `Saint`/`Street` (context) |
| Abbrev. (latin) | `e.g.`, `i.e.`, `etc.` | `for example`, `that is`, `et cetera` |
| Acronym | `NASA`, `FBI` | one token; spell-out if not in pronounceable list |
| URL/email | `https://example.com/a` | `example dot com slash a` |
| Units | `5kg`, `10km` | `five kilograms`, `ten kilometers` |
| Punctuation | `,` `;` `—` | dropped from token stream; `. ? !` kept as boundary hints |

Rules:

- Abbreviation periods MUST NOT be treated as sentence terminators.
- Normalization is pure and table-driven so it is unit-testable in isolation.
- Every emitted token carries `sourceStart`/`sourceEnd` (inclusive source word
  indices), enabling the merge in §5.

#### 2.4 Word index mapping strategy

- Build two arrays during normalization:
  `tokenToSource[tokenIndex] = {sourceStart, sourceEnd}` and the inverse
  `sourceToTokens[sourceIndex] = [tokenIndex...]`.
- 1→N (e.g. `1999`→3 tokens): all tokens point to the one source index; the
  merged highlight spans the union of their time windows.
- N→1 (e.g. `New York`→1 token): the token points to a source range; both
  source words highlight during the token's window.
- These maps are serialized per chunk and consumed by the merge step.

#### 2.5 Chunking strategy

- Greedy pack normalized tokens into chunks, breaking at the nearest **paragraph
  boundary**, then **sentence boundary**, never exceeding `MaxTokensPerChunk`
  (default 350; configurable). A lone sentence over the limit splits at clause
  boundaries (`,`/`;`/`—` positions recorded before they were dropped).
- Each `Chunk`: `{index, text, sourceWordStart, sourceWordEnd, charOffset,
  tokenToSource[]}`.
- Chunks are contiguous and cover the full token stream with no gaps/overlaps
  (verified by an assertion in tests).

### 3. TTS Worker Specification (kokoro-tts)

#### 3.1 Model loading & inference

- `kokoro-onnx` loading `kokoro-v1.0.onnx` + `voices-v1.0.bin` from
  `/models/kokoro`. Load once at process start; keep the `InferenceSession`
  resident.
- Inference: text → phonemes (espeak-ng/`misaki` G2P) → model → float32 PCM at
  24 kHz mono.

#### 3.2 Execution provider selection & fallback

Priority `ROCMExecutionProvider` → `CUDAExecutionProvider` →
`CPUExecutionProvider`. Try to create the session on each in order; on failure
log and fall to the next. Report the active provider in `Health.detail`.

```python
PROVIDER_PRIORITY = ["ROCMExecutionProvider",
                     "CUDAExecutionProvider",
                     "CPUExecutionProvider"]

def make_session(model_path: str) -> ort.InferenceSession:
    available = set(ort.get_available_providers())
    for ep in PROVIDER_PRIORITY:
        if ep not in available:
            continue
        try:
            sess = ort.InferenceSession(model_path, providers=[ep])
            logging.info("ONNX provider: %s", ep)
            return sess
        except Exception as e:                       # noqa: BLE001
            logging.warning("provider %s failed: %s", ep, e)
    raise RuntimeError("no usable ONNX execution provider")
```

#### 3.3 gRPC service (path-based contract)

```proto
service KokoroTts {
  rpc Synthesize(SynthesizeRequest) returns (SynthesizeResponse);
  rpc ListVoices(ListVoicesRequest) returns (ListVoicesResponse);
  rpc Health(HealthRequest) returns (HealthResponse);
}

message SynthesizeRequest {
  string text = 1;             // normalized chunk text
  string voice_id = 2;         // e.g. "af_heart"
  float  speed = 3;            // 0.5..2.0, default 1.0
  string language = 4;         // ISO 639-1
  string out_path = 5;         // path under /data to write WAV, e.g.
                               // "audio/{sessionId}/00007.wav"
}

message PhonemeDuration {
  string phoneme = 1;
  double start_seconds = 2;
  double end_seconds = 3;
}

message SynthesizeResponse {
  string audio_path = 1;       // echoes out_path actually written
  uint32 sample_rate = 2;      // 24000
  uint32 channels = 3;         // 1
  double duration_seconds = 4;
  repeated PhonemeDuration phonemes = 5;  // synthesis-time prior
}
```

#### 3.4 Audio format

- WAV, PCM **float32**, **24000 Hz**, **mono**. Written with `soundfile`
  (`subtype="FLOAT"`). This is what both WhisperX and the browser consume; no
  resampling needed before alignment.

#### 3.5 GPU memory management

- One global `asyncio.Semaphore(MAX_CONCURRENCY=1)` guards inference so the TTS
  worker never competes with itself for VRAM while sharing the GPU with
  alignment.
- Release numpy/ORT buffers promptly; do not accumulate per-request tensors.

#### 3.6 Error handling & retry

| Condition | gRPC status | Retryable by api? |
|---|---|---|
| Empty/whitespace `text` | `INVALID_ARGUMENT` | no |
| Unknown `voice_id` | `INVALID_ARGUMENT` | no |
| Transient runtime/GPU error | `UNAVAILABLE` | yes (backoff, max 3) |
| Out-of-memory | `RESOURCE_EXHAUSTED` | yes (after brief delay) |
| Write to `/data` failed | `INTERNAL` | yes (max 1) |

### 4. Alignment Worker Specification (whisperx-align)

#### 4.1 Forced alignment (not transcription)

- Use `whisperx.load_align_model(language_code, device)` to get the wav2vec2
  phoneme model + metadata, then `whisperx.align(segments, model, metadata,
  audio, device, return_char_alignments=False)`.
- The transcript is provided as a **single segment spanning the whole clip**;
  WhisperX aligns the known words — it never runs Whisper ASR. The word list out
  equals the transcript word list (order preserved, none invented/dropped).

#### 4.2 wav2vec2 preload strategy

- Load the align model once at startup for the configured language; keep it
  resident in VRAM for the process lifetime. `Health` returns `NOT_READY` until
  loaded, then `SERVING`. First real request incurs no load/download.
- Multi-language: lazy-load and cache per language code (bounded LRU of 2).

#### 4.3 gRPC service

```proto
service Alignment {
  rpc Align(AlignRequest) returns (AlignResponse);
  rpc Health(HealthRequest) returns (HealthResponse);
}

message AlignRequest {
  string audio_path = 1;       // path under /data to the chunk WAV
  string transcript = 2;       // exact normalized text that was synthesized
  string language = 3;         // ISO 639-1
}

message WordTiming {
  string text = 1;
  int64  start_ms = 2;
  int64  end_ms = 3;
  float  confidence = 4;       // 0..1
  bool   low_confidence = 5;   // below threshold
}

message AlignResponse {
  repeated WordTiming words = 1;
  int64 audio_duration_ms = 2;
}
```

#### 4.4 Input / output

- **In**: `audio_path` (read from shared volume) + `transcript`.
- **Out**: per-word `{text, startMs, endMs, confidence}`, plus
  `audioDurationMs`.

#### 4.5 Low-confidence handling

- Threshold default `0.30` (configurable). Words below it get
  `low_confidence=true` but are still returned with their best-estimate window.
- The api interpolates flagged spans between neighboring high-confidence anchors
  (§5.3) so the highlight never stalls or jumps.

#### 4.6 Edge cases

- Expanded numbers/abbreviations: aligned at token granularity exactly as in the
  transcript; the api re-projects to source words.
- Acronyms: aligned as the single token supplied.
- Punctuation: not present as standalone tokens in the transcript, so no
  punctuation-only timings are emitted.

#### 4.7 GPU memory management (16 GB)

- wav2vec2 stays resident (~360 MB). Bound concurrency to 1 (configurable) and
  cap segment length; for the per-chunk clip sizes here (≤ ~30 s) a single
  forward pass fits comfortably. Free intermediate tensors and call
  `torch.cuda.empty_cache()` (HIP-backed) between requests under memory
  pressure.

### 5. Backend API Specification (api)

#### 5.1 REST endpoints

| Method | Path | Request | Success | Errors |
|---|---|---|---|---|
| POST | `/api/documents` | multipart `file` | 201 `Document` | 413 too large, 415 unsupported, 422 unreadable |
| GET | `/api/documents/{id}` | — | 200 `Document` | 404 |
| GET | `/api/documents/{id}/words` | — | 200 `WordData[]` | 404, 409 not-extracted |
| POST | `/api/documents/{id}/sessions` | `{voice, speed, language}` | 201 `TtsSession` | 404, 400 bad voice |
| GET | `/api/sessions/{id}` | — | 200 `TtsSession` | 404 |
| GET | `/api/sessions/{id}/chunks` | — | 200 `ProcessedChunk[]` | 404 |
| GET | `/api/sessions/{id}/chunks/{index}/audio` | Range | 200/206 `audio/wav` | 404, 416 |
| DELETE | `/api/sessions/{id}` | — | 204 | 404 |
| GET | `/api/voices` | — | 200 `Voice[]` | — |
| GET | `/healthz` | — | 200 | 503 if workers down |

Error body shape: `{ "code": "UNSUPPORTED_FORMAT", "message": "...", "detail":
{...} }`.

#### 5.2 SignalR hub

- Hub at `/hubs/reader`. Hub name `ReaderHub`.
- **Client→Server:** `Subscribe(sessionId)`, `Unsubscribe(sessionId)`.
- **Server→Client:**
  - `SessionStatus({ sessionId, status })` — `queued|processing|streaming|complete|error`
  - `Progress({ sessionId, completedChunks, totalChunks, progress })`
  - `ChunkReady(ProcessedChunk)`
  - `Error({ sessionId, code, message })`
- On `Subscribe`, the hub adds the connection to group `session:{id}` and
  **backfills** already-completed `ChunkReady` events before live events.

#### 5.3 Word timestamp + bounding-box merge (fuzzy)

```
mergeChunk(chunk, alignedTokens[], wordData[]):
  # 1. align token list to expected normalized tokens by anchor matching
  expected = chunk.tokenToSource           # length = N tokens
  matched  = anchorAlign(expected, alignedTokens)   # DTW over normalized text,
                                                     # edit-distance tie-break
  # 2. project token timings onto source words
  bySource = {}                            # sourceIndex -> [WordTiming...]
  for ti, timing in enumerate(matched):
    if timing is None:                     # unmatched -> interpolate later
      continue
    span = expected[ti]                    # {sourceStart, sourceEnd}
    for s in range(span.sourceStart, span.sourceEnd+1):
      bySource.setdefault(s, []).append(timing)
  # 3. build one merged WordData per source word in the chunk
  out = []
  for s in chunk.sourceWordStart..chunk.sourceWordEnd:
    ts = bySource.get(s)
    if ts:                                 # union of token windows
      start = min(t.start_ms for t in ts)
      end   = max(t.end_ms   for t in ts)
      conf  = min(t.confidence for t in ts)
    else:                                  # interpolate from neighbors
      start, end, conf = interpolate(s, out, chunk.audioDurationMs)
    wd = wordData[s]
    out.append(WordData(text=wd.text, index=s, page=wd.page, bbox=wd.bbox,
                        startMs=start, endMs=end, confidence=conf))
  enforceMonotonic(out)                    # clamp overlaps, no negative gaps
  return out
```

- `anchorAlign` uses normalized-text equality first, then bounded
  Levenshtein-ratio fuzzy matching (≥ 0.8) to absorb tokenizer drift.
- Word-count mismatch (aligned ≠ expected) → align by anchors, interpolate the
  rest, mark the chunk `degraded` (not failed).

#### 5.4 Session lifecycle

`queued → processing → streaming → complete`, or `→ error` from any state.
`streaming` begins when chunk 0 is pushed. `progress = completedChunks /
totalChunks`.

#### 5.5 Async pipeline orchestration

- `System.Threading.Channels`: a bounded channel of `Chunk` feeds a fixed number
  of worker tasks (`PipelineConcurrency`, default 2) that call synth→align→merge;
  results go to an **ordering buffer** keyed by `chunkIndex` so `ChunkReady` is
  always emitted in order even when chunks finish out of order.
- Backpressure: bounded channels + `await WriteAsync` apply pressure when a slow
  client or slow GPU backs things up; no unbounded queues.
- Cancellation: deleting a session or a dropped pipeline cancels the
  `CancellationTokenSource`, stopping in-flight gRPC calls.

#### 5.6 Audio file naming

- `/data/audio/{sessionId}/{chunkIndex:D5}.wav` (zero-padded to 5 digits).
- `ProcessedChunk.audioUrl = /api/sessions/{sessionId}/chunks/{index}/audio`
  (served with Range support for seeking).

### 6. Frontend Specification (frontend)

#### 6.1 Component tree

```
<App>
 ├─ <UploadView>            idle/uploading: drop zone, format validation
 ├─ <ReaderView>            processing/playing/paused
 │   ├─ <DocumentCanvas>    PDF.js or epub.js renderer + scale mgmt
 │   │   └─ <WordOverlay>   absolutely-positioned highlight boxes/spans
 │   ├─ <PlaybackBar>       play/pause, speed, voice, seek, progress
 │   └─ <StatusToast>       processing %, errors, reconnecting
 ├─ <SignalRProvider>       connection, Subscribe, event fan-out
 ├─ <AudioQueue>            ordered chunk buffer + WaveSurfer/Web Audio
 └─ <SyncEngine>            rAF loop, binary search, active-word state
```

#### 6.2 PDF.js rendering + bbox overlay

- Render each page to canvas at `scale`. PDF user-space bbox → CSS:
  `left = bbox.x * scale`, `top = (pageHeight - bbox.y - bbox.height) * scale`
  (Y-flip), `width = bbox.width * scale`, `height = bbox.height * scale`.
- Overlay is a transparent absolutely-positioned `<div data-wi={index}>` layer
  above the canvas; the active one gets a `.active` background.

#### 6.3 epub.js rendering + span injection

- After epub.js renders a chapter into its iframe, walk text nodes with the same
  word-boundary algorithm the server used and wrap each word in
  `<span data-wi={index}>`. Active highlight toggles a class on the matching
  span. Position is whatever reflow produced (no manual geometry).

#### 6.4 SignalR reception + audio queue

- On `ChunkReady`, insert into a `Map<chunkIndex, ProcessedChunk>`; a cursor
  plays the lowest unplayed index. WaveSurfer (or raw Web Audio
  `AudioBufferSourceNode`s) schedules chunks back-to-back for gapless playback.
- Underrun: if the next index isn't ready at boundary, pause and resume on its
  arrival (no word skipping).

#### 6.5 rAF sync engine (binary search)

```ts
// words: flat array across all played chunks, sorted by startMs, with a
// running globalOffsetMs per chunk so time is continuous across chunks.
function activeWordIndex(words: TimedWord[], tMs: number): number {
  let lo = 0, hi = words.length - 1, ans = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (words[mid].startMs <= tMs) { ans = mid; lo = mid + 1; }
    else hi = mid - 1;
  }
  // ans = last word whose startMs <= tMs; confirm tMs within its window
  return ans >= 0 && tMs <= words[ans].endMs ? ans : ans; // hold last on gap
}

function frame() {
  const tMs = chunkOffsetMs(currentChunk) + audio.currentTime * 1000;
  const i = activeWordIndex(words, tMs);
  if (i !== lastActive) { setActive(i); maybeAutoScroll(i); lastActive = i; }
  raf = requestAnimationFrame(frame);
}
```

#### 6.6 Highlight overlay rendering

- PDF: absolutely-positioned divs sized by the §6.2 scale transform; re-laid-out
  on zoom/resize via a single `scale` state. Only the active div toggles class
  (no per-frame layout thrash).
- EPUB: class toggle on the active span; no geometry math.

#### 6.7 Auto-scroll

- When the active word CHANGES (playback advancing, seek, tap), always re-center
  it — `scrollIntoView({behavior:"smooth", block:"center"})` on the PDF box or
  the EPUB word span (advancing the section first if needed). Following the
  voice deliberately wins over a recent manual scroll.
- The manual-scroll grace period (~2.5 s) applies only to the PDF's "same word,
  overlay boxes rebuilt" case (a chunk streaming in while paused, a page-turn
  settling): there, scroll only if the word went off-screen and the user hasn't
  scrolled recently — so a paused reader skimming ahead isn't yanked back.

#### 6.8 UI states

`idle → uploading → processing → (playing ⇄ paused) → [complete]`, with `error`
reachable from any state and a `reconnecting` overlay during SignalR drops.

#### 6.9 Playback controls

- Play/pause; speed `{0.75,1,1.25,1.5,2}` (sets `playbackRate` and the sync
  time scale together); voice dropdown (changing voice POSTs a new session);
  click-a-word seeks to `word.startMs`; a scrubber over total estimated
  duration.

### 7. Data Models

#### 7.1 TypeScript

```ts
export type DocumentType = "pdf" | "epub";
export type DocumentStatus = "queued" | "extracting" | "ready" | "error";
export type SessionStatus =
  | "queued" | "processing" | "streaming" | "complete" | "error";

export interface BoundingBox { x: number; y: number; width: number; height: number; }

export interface WordData {
  index: number;          // global document word index
  text: string;
  startMs: number;
  endMs: number;
  page: number | null;    // 1-based for PDF, null for EPUB
  bbox: BoundingBox | null;   // PDF user-space; null for EPUB
  confidence?: number;
}

export interface ProcessedChunk {
  chunkIndex: number;
  audioUrl: string;
  durationMs: number;
  words: WordData[];
  degraded?: boolean;
}

export interface Document {
  id: string;
  filename: string;
  type: DocumentType;
  pageCount: number;
  wordCount: number;
  status: DocumentStatus;
}

export interface TtsSession {
  id: string;
  documentId: string;
  voice: string;
  speed: number;
  language: string;
  status: SessionStatus;
  progress: number;       // 0..1
}

export interface Voice { id: string; label: string; language: string; gender: string; }
```

#### 7.2 C# records

```csharp
public enum DocumentType { Pdf, Epub }
public enum DocumentStatus { Queued, Extracting, Ready, Error }
public enum SessionStatus { Queued, Processing, Streaming, Complete, Error }

public readonly record struct BoundingBox(double X, double Y, double Width, double Height);

public sealed record WordData(
    int Index, string Text, long StartMs, long EndMs,
    int? Page, BoundingBox? Bbox, float Confidence = 1f);

public sealed record ProcessedChunk(
    int ChunkIndex, string AudioUrl, long DurationMs,
    IReadOnlyList<WordData> Words, bool Degraded = false);

public sealed record Document(
    Guid Id, string Filename, DocumentType Type,
    int PageCount, int WordCount, DocumentStatus Status);

public sealed record TtsSession(
    Guid Id, Guid DocumentId, string Voice, float Speed,
    string Language, SessionStatus Status, double Progress);

public sealed record Voice(string Id, string Label, string Language, string Gender);
```

#### 7.3 Proto messages

The authoritative `proto/kokoro.proto` (§3.3) and `proto/alignment.proto`
(§4.3) plus a shared `proto/common.proto`:

```proto
syntax = "proto3";
package pdftotts.common.v1;
option csharp_namespace = "PDFtoTTS.Grpc.Common.V1";

message HealthRequest {}
message HealthResponse {
  enum Status { STATUS_UNSPECIFIED = 0; STATUS_NOT_READY = 1; STATUS_SERVING = 2; }
  Status status = 1;
  string detail = 2;   // active provider / device / model state
}

message ListVoicesRequest {}
message Voice { string id = 1; string label = 2; string language = 3; string gender = 4; }
message ListVoicesResponse { repeated Voice voices = 1; }
```

### 8. Docker Compose Specification

#### 8.1 docker-compose.yml

```yaml
name: pdftotts

x-rocm: &rocm
  devices:
    - /dev/kfd
    - /dev/dri
  group_add:
    - video
    - render
  security_opt:
    - seccomp:unconfined
  environment:
    HSA_OVERRIDE_GFX_VERSION: "11.0.0"   # RX 7800 XT (gfx1101) spoofed as gfx1100
    HIP_VISIBLE_DEVICES: "0"

services:
  kokoro-tts:
    build: { context: ./workers/kokoro-tts }
    <<: *rocm
    environment:
      HSA_OVERRIDE_GFX_VERSION: "11.0.0"
      HIP_VISIBLE_DEVICES: "0"
      ONNX_PROVIDER_PRIORITY: "ROCMExecutionProvider,CPUExecutionProvider"
      MODEL_DIR: "/models/kokoro"
      MAX_CONCURRENCY: "1"
      GRPC_PORT: "50051"
    volumes:
      - appdata:/data
      - modelcache:/models
    healthcheck:
      test: ["CMD", "python", "-m", "worker.healthcheck"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 120s

  whisperx-align:
    build: { context: ./workers/whisperx-align }
    <<: *rocm
    environment:
      HSA_OVERRIDE_GFX_VERSION: "11.0.0"
      HIP_VISIBLE_DEVICES: "0"
      ALIGN_LANGUAGE: "en"
      ALIGN_CONFIDENCE_THRESHOLD: "0.30"
      MODEL_DIR: "/models/whisperx"
      MAX_CONCURRENCY: "1"
      GRPC_PORT: "50052"
    volumes:
      - appdata:/data
      - modelcache:/models
    healthcheck:
      test: ["CMD", "python", "-m", "worker.healthcheck"]
      interval: 10s
      timeout: 5s
      retries: 18
      start_period: 180s

  api:
    build: { context: ., dockerfile: src/PDFtoTTS.Api/Dockerfile }
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      KOKORO_GRPC: "http://kokoro-tts:50051"
      WHISPERX_GRPC: "http://whisperx-align:50052"
      DATA_DIR: "/data"
      PIPELINE_CONCURRENCY: "2"
      MAX_TOKENS_PER_CHUNK: "350"
    ports: ["8080:8080"]
    volumes:
      - appdata:/data
    depends_on:
      kokoro-tts: { condition: service_healthy }
      whisperx-align: { condition: service_healthy }

  frontend:
    build: { context: ./frontend }
    ports: ["5173:80"]
    depends_on:
      api: { condition: service_started }

volumes:
  appdata:
  modelcache:
```

#### 8.2 ROCm passthrough notes

- `/dev/kfd` + `/dev/dri` devices and `group_add: [video, render]` give the
  container access to the compute + render nodes. `seccomp:unconfined` is
  commonly required for ROCm.
- `HSA_OVERRIDE_GFX_VERSION=11.0.0` makes ROCm treat `gfx1101` (RX 7800 XT) as
  the supported `gfx1100`.
- `HIP_VISIBLE_DEVICES=0` pins to the single GPU.

#### 8.3 Dockerfile outlines

- **kokoro-tts**: base `rocm/dev-ubuntu-22.04` (or `onnxruntime-rocm` wheels on
  a slim ROCm base) → install `kokoro-onnx onnxruntime-rocm soundfile
  grpcio grpcio-tools misaki[en]` → `python -m grpc_tools.protoc` codegen from
  `/proto` → entrypoint serves gRPC on `:50051`.
- **whisperx-align**: base ROCm PyTorch image (`rocm/pytorch`) → install
  `whisperx transformers soundfile grpcio` → codegen → entrypoint warms
  wav2vec2 then serves `:50052`.
- **api**: multi-stage `mcr.microsoft.com/dotnet/sdk:10.0` build → `dotnet
  publish` (codegen `proto/` into the client) → runtime
  `mcr.microsoft.com/dotnet/aspnet:10.0`, expose `8080`.
- **frontend**: `node:22` build (`npm ci && npm run build`) → `nginx:alpine`
  serving `dist/` with a proxy for `/api` and `/hubs` to `api:8080`.

#### 8.4 Dependency order & health

`kokoro-tts`+`whisperx-align` (healthy, long `start_period` for model warmup) →
`api` (depends healthy) → `frontend`.

### 9. Error Handling & Edge Cases

| Case | Handling |
|---|---|
| GPU unavailable | Workers fall back ROCm→CPU; report degraded in `Health.detail`; pipeline continues slower. |
| Alignment confidence < threshold | Word returned, flagged; api interpolates window from neighbors (§5.3); never drop/stall. |
| Whole-chunk alignment failure | api distributes timings evenly from audio duration / word count; chunk `degraded`. |
| Word-count mismatch (PDF ↔ TTS normalization) | Anchor-align + fuzzy match + interpolate (§5.3); mark `degraded`, not failed. |
| Large document | Stream chunk-by-chunk; bounded channels + bounded concurrency cap memory; audio is on disk, not RAM; optional page-range sessions. |
| Corrupted / unsupported file | Reject by magic bytes pre-processing → 415/422; document → `error` with code. |
| Network interruption during streaming | SignalR auto-reconnect; client re-`Subscribe`s; hub backfills missed `ChunkReady`; playback resumes at saved position. |
| TTS transient/OOM error | Retry with backoff (max 3 / `RESOURCE_EXHAUSTED` brief delay); after limit, chunk `error`, session continues or fails per policy. |
| Shared-volume write failure | `INTERNAL`; retry once; surface as session error if persistent. |
| Voice changed mid-session | New session created; the old session and its audio are torn down immediately (supersede). |

## Risks / Trade-offs

- **ROCm on an unsupported SKU** → the `HSA_OVERRIDE` spoof can be brittle across
  ROCm versions. *Mitigation:* pin ROCm/onnxruntime-rocm/torch-rocm versions in
  the worker images; CPU fallback keeps the system usable if the GPU path
  breaks.
- **Two workers share 16 GB VRAM** → contention/OOM under parallelism.
  *Mitigation:* concurrency 1 per worker, resident-model budgeting, sequential
  synth→align per chunk, `RESOURCE_EXHAUSTED` retry.
- **Normalization ↔ source mapping drift** breaks highlight accuracy.
  *Mitigation:* token-tagged normalization, fuzzy anchor merge, interpolation
  fallback, `degraded` marking, and unit tests asserting full coverage.
- **Forced alignment imperfect on expanded tokens** → minor highlight jitter.
  *Mitigation:* merge multi-token windows to one source highlight; low-confidence
  interpolation; rAF "hold last word on gap" behavior.
- **No DB (in-memory state)** → state lost on `api` restart mid-session.
  *Mitigation:* acceptable for v1 single-user; audio already persisted on disk;
  durable store is a follow-up change.
- **gAPless browser playback across chunks** is finicky in some browsers.
  *Mitigation:* Web Audio scheduling with small look-ahead; pause-on-underrun
  rather than skip.

## Migration Plan

Greenfield — no migration. Rollout = build order in `tasks.md`. Each service is
independently buildable/testable (mock gRPC for the api, fixture WAV+transcript
for alignment, golden-text fixtures for normalization). Rollback = revert the
change; nothing in production depends on it yet.

## Resolved Questions

All four are settled by what shipped.

- **Default voice + which Kokoro voice pack ships in the image?** Default is
  `af_heart` (`useReader.ts` `voiceRef`, and the warmup pass in
  `KokoroBackend.load`). No voice pack is baked into the image: `_fetch_voice_ids()`
  lists `voices/*.pt` from the `hexgrad/Kokoro-82M` HF repo at startup and falls
  back to a seven-voice core set if the listing is unreachable. Weights land in
  the `modelcache` volume on first use, so the image stays small and the voice
  list follows the upstream repo.
- **Persist session/word metadata to SQLite in v1, or stay in-memory?** Split, and
  no SQLite. Documents and extracted words persist as JSON on the shared volume
  (`catalogue.json`, `words/{id}.json`) through `PersistentDocumentStore`, so the
  library and any OCR result survive a restart. Sessions stay in
  `InMemorySessionStore` and their audio is purged at startup, because a session
  is a disposable render of a document, not a durable record.
- **Multi-language alignment now, or English-only v1 with lazy per-language load?**
  Lazy per-language, with English preloaded. `WhisperXBackend` caches one wav2vec2
  model per language code in `_models`; the server preloads and warms
  `ALIGN_LANGUAGE` (default `en`, set to `en` in compose) before serving, and
  `align()` calls `preload()` for whatever language the request names, so another
  language costs one load on first use. Kokoro mirrors this with a `KPipeline` per
  language code created on demand.
- **Cleanup policy: TTL sweep interval and whether to keep audio for replay across
  restarts.** Audio is never kept across restarts (purged at startup);
  supersede/delete tear down a session's audio immediately, so no TTL sweep.
