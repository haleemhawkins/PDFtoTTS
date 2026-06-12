## ADDED Requirements

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
