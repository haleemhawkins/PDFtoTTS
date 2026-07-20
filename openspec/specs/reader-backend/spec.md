# reader-backend Specification

## Purpose

The .NET API: document library CRUD + persistence, TTS session lifecycle and streaming orchestration, and audio delivery (per-chunk WAV + per-session HLS).

## Requirements

### Requirement: Document upload endpoint

The backend SHALL expose `POST /api/documents` accepting a multipart file
upload, validate it by content, persist the original to the shared volume, and
return a `Document` with a generated id and status `queued`.

#### Scenario: Valid upload accepted

- **WHEN** a client POSTs a valid PDF to `/api/documents`
- **THEN** the response is HTTP 201 with a `Document` body containing `id`,
  `filename`, `type`, and `status="queued"`, and the file is stored under the
  shared volume keyed by document id

#### Scenario: Oversized upload rejected

- **WHEN** an upload exceeds the configured maximum size
- **THEN** the response is HTTP 413 and no document is created

### Requirement: Session creation and lifecycle

The backend SHALL expose `POST /api/documents/{id}/sessions` to start a
`TtsSession` with a chosen voice, speed, and language, and SHALL advance the
session through the states `queued → processing → streaming → complete`, or to
`error`, exposing current state and progress.

#### Scenario: Session starts processing

- **WHEN** a client creates a session for a queued document
- **THEN** the response is HTTP 201 with a `TtsSession` whose status is
  `processing` and the orchestration pipeline begins

#### Scenario: Session progress is queryable

- **WHEN** a client GETs `/api/sessions/{id}`
- **THEN** the response includes the current `status` and a `progress` value in
  `[0, 1]` reflecting chunks completed over total chunks

#### Scenario: Pipeline failure transitions to error

- **WHEN** an unrecoverable error occurs during processing
- **THEN** the session status becomes `error` with a machine-readable reason and
  the error is pushed to subscribed clients

### Requirement: SignalR reader hub

The backend SHALL host a SignalR hub at `/hubs/reader` allowing a client to
subscribe to a session and receive `ChunkReady`, `Progress`, `SessionStatus`,
and `Error` events; clients SHALL be able to join only sessions they own.

#### Scenario: Client receives chunks in order

- **WHEN** a subscribed client is connected and chunks complete the pipeline
- **THEN** the client receives `ChunkReady` events carrying `ProcessedChunk`
  payloads with monotonically increasing `chunkIndex`

#### Scenario: Late subscriber is backfilled

- **WHEN** a client subscribes after some chunks already completed
- **THEN** the hub replays already-completed `ChunkReady` events for that session
  before live events resume

### Requirement: Async pipeline orchestration

The backend SHALL orchestrate extraction, synthesis, and alignment as a bounded
asynchronous pipeline (`System.Threading.Channels` and/or `IAsyncEnumerable`),
processing chunks with bounded concurrency and preserving output order to
clients.

#### Scenario: Bounded concurrency respected

- **WHEN** a document yields more chunks than the configured concurrency
- **THEN** at most the configured number of chunks are in synthesis/alignment at
  once and chunk results are delivered to clients in ascending `chunkIndex`

#### Scenario: Backpressure prevents unbounded buffering

- **WHEN** a client consumes slower than chunks are produced
- **THEN** the pipeline applies backpressure via bounded channels rather than
  growing memory without limit

### Requirement: Timestamp and bounding-box merge with fuzzy matching

The backend SHALL merge alignment word timings with source `WordData` bounding
boxes by re-projecting normalized token timings onto source word indices via the
normalization map, using token-level fuzzy matching to recover from minor
tokenization differences, producing per-source-word `{startMs, endMs, page,
bbox, index}` entries.

#### Scenario: Expanded number merged to one source word

- **WHEN** "nineteen ninety nine" timings are merged for source word "1999"
- **THEN** the merged `WordData` for that source index spans from the first
  token's `startMs` to the last token's `endMs` and carries the original word's
  page and bounding box

#### Scenario: Token mismatch resolved by fuzzy match

- **WHEN** an aligned token does not exactly equal its mapped source token
- **THEN** the merge uses normalized edit-distance matching within the mapped
  source word range to assign timing, and unmatched tokens are interpolated from
  neighbors rather than dropped

#### Scenario: Word count mismatch handled

- **WHEN** the number of aligned tokens differs from the expected normalized
  token count for a chunk
- **THEN** the merge aligns by anchor tokens and interpolates the remainder, and
  the chunk is marked degraded rather than failed

### Requirement: Audio chunk file naming on shared volume

The backend SHALL store each chunk's audio on the shared volume under a
deterministic path `audio/{sessionId}/{chunkIndex:D5}.wav` and SHALL expose it
to clients via a stable URL `GET /api/sessions/{id}/chunks/{index}/audio`.

#### Scenario: Deterministic chunk path

- **WHEN** chunk 7 of a session completes synthesis
- **THEN** its audio is written to `audio/{sessionId}/00007.wav` and the
  `ProcessedChunk.audioUrl` resolves to that chunk's audio endpoint

#### Scenario: Audio served with range support

- **WHEN** a client requests a chunk's audio with a Range header
- **THEN** the endpoint responds with HTTP 206 partial content to support
  seeking

### Requirement: Persistent document catalogue

The backend SHALL persist every uploaded document's metadata and extracted words
to the shared volume and reload them on startup, so the document library survives
API restarts. Synthesized audio SHALL NOT be persisted across restarts — it
remains per-session and generated on demand.

#### Scenario: Catalogue survives a restart

- **WHEN** a document has reached status `Ready` and the API process is restarted
- **THEN** the document and its extracted words are available again from the store
  without re-uploading or re-extracting, and re-opening it does not re-run OCR

#### Scenario: Audio is not persisted

- **WHEN** the API restarts after sessions produced audio
- **THEN** no session audio is restored; new playback re-synthesizes on the fly

#### Scenario: Extraction result is written through

- **WHEN** background extraction (and any OCR) completes for a document
- **THEN** the persisted catalogue is updated atomically with the final status,
  page/word counts, and the extracted words

### Requirement: List documents

The backend SHALL expose `GET /api/documents` returning the catalogue of
documents (most-recently-added first) as `Document` records.

#### Scenario: Library is listed

- **WHEN** a client GETs `/api/documents`
- **THEN** the response is HTTP 200 with an array of `Document` records including
  `id`, `filename`, `type`, `pageCount`, `wordCount`, and `status`

#### Scenario: Empty library

- **WHEN** no documents have been uploaded
- **THEN** the response is HTTP 200 with an empty array

### Requirement: Delete a document

The backend SHALL expose `DELETE /api/documents/{id}` that removes the document
from the catalogue, deletes its original file and persisted words, and cancels and
cleans up any sessions and audio belonging to it.

#### Scenario: Document and its artifacts removed

- **WHEN** a client DELETEs `/api/documents/{id}` for an existing document
- **THEN** the response is HTTP 204, the document no longer appears in
  `GET /api/documents`, its original file is deleted, and any in-flight session for
  it is cancelled with its audio removed

#### Scenario: Deleting an unknown document

- **WHEN** a client DELETEs `/api/documents/{id}` for an id that does not exist
- **THEN** the response is HTTP 404

### Requirement: Rename a document

The backend SHALL expose `PATCH /api/documents/{id}` accepting a new display name
and SHALL update the document's `filename`/title, persisting the change.

#### Scenario: Rename succeeds

- **WHEN** a client PATCHes `/api/documents/{id}` with a non-empty name
- **THEN** the response is HTTP 200 with the updated `Document` and the new name is
  persisted across restarts

#### Scenario: Empty name rejected

- **WHEN** a client PATCHes `/api/documents/{id}` with a blank name
- **THEN** the response is HTTP 400 and the name is unchanged

### Requirement: Save the reading position (cross-device resume)

The backend SHALL expose `PUT /api/documents/{id}/position` accepting the
reader's resume point (page, word, voice, speed, and the client's Unix-ms
timestamp) and SHALL persist it on the document with last-writer-wins ordering
by that timestamp, so a stale writer cannot clobber a newer position from
another device. Timestamps far in the future SHALL be clamped so a skewed clock
cannot permanently freeze the resume point. The stored position SHALL be
returned with the document and survive API restarts.

#### Scenario: Position round-trips

- **WHEN** a client PUTs a position for an existing document
- **THEN** the response is HTTP 200 with the updated `Document` and later reads
  of the document include that position

#### Scenario: Stale write is ignored

- **WHEN** a client PUTs a position whose timestamp is older than the stored one
- **THEN** the stored position is unchanged

#### Scenario: Unknown document

- **WHEN** a client PUTs a position for an id that does not exist
- **THEN** the response is HTTP 404

### Requirement: Serve original document bytes

The backend SHALL expose `GET /api/documents/{id}/original` returning the stored
original file with its correct content type, so the client can render the PDF/EPUB
without re-uploading it.

#### Scenario: Original is served

- **WHEN** a client GETs `/api/documents/{id}/original` for an existing document
- **THEN** the response is HTTP 200 with the original bytes and a content type of
  `application/pdf` or `application/epub+zip` matching the document type

#### Scenario: Original missing

- **WHEN** the document id is unknown or its original file is absent
- **THEN** the response is HTTP 404

### Requirement: Per-session HLS endpoint

The backend SHALL expose a per-session HLS stream that iOS can play natively:
`GET /api/sessions/{id}/hls/playlist.m3u8` returning an EVENT playlist, and
`GET /api/sessions/{id}/hls/{index}.ts` returning the audio segment for that
chunk. Each segment SHALL be the chunk's audio transcoded to AAC in an MPEG-TS
container, produced on demand and cached. The playlist SHALL list the contiguous
produced chunks (prefix from 0) with their durations, SHALL be available before
synthesis completes, and SHALL be finalized with `#EXT-X-ENDLIST` once synthesis
ends. The existing per-chunk endpoint `GET /api/sessions/{id}/chunks/{index}/audio`
SHALL remain available for the Web Audio (non-iOS) engine.

#### Scenario: Playlist lists produced segments in order

- **WHEN** a client requests the playlist for a session with chunks 0..N produced
- **THEN** the playlist contains `#EXTINF` entries for segments 0..N in order with
  each chunk's duration

#### Scenario: Playlist is available before synthesis completes

- **WHEN** the playlist is requested while later chunks are still synthesizing
- **THEN** it returns the segments produced so far without `#EXT-X-ENDLIST`, and is
  finalized with `#EXT-X-ENDLIST` only once synthesis ends

#### Scenario: Segment transcoded on demand

- **WHEN** a client requests `…/hls/{index}.ts` for a produced chunk
- **THEN** the chunk's WAV is transcoded to an AAC/MPEG-TS segment (if not already
  cached) and returned with content type `video/mp2t`

#### Scenario: Empty playlist is never served

- **WHEN** the playlist is requested before the first chunk is produced
- **THEN** the request waits until the first segment exists (or synthesis ends)
  rather than returning an empty playlist the player would give up on
