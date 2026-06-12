## ADDED Requirements

### Requirement: Document rendering with word overlay

The frontend SHALL render PDFs with PDF.js and EPUBs with epub.js, and SHALL
position a per-word highlight overlay aligned to each rendered word, using
PDF.js text-layer geometry for PDFs and injected word `<span>`s for EPUBs.

#### Scenario: PDF word overlay aligns to text

- **WHEN** a PDF page renders at a given scale
- **THEN** each word's highlight box is an absolutely positioned element whose
  position and size match the rendered glyph run after applying the page scale
  factor

#### Scenario: EPUB words wrapped in addressable spans

- **WHEN** an EPUB chapter renders
- **THEN** each word is wrapped in a span addressable by document word index so
  the sync engine can toggle its highlight class

### Requirement: Streamed chunk reception and audio queue

The frontend SHALL connect to the SignalR reader hub, receive `ChunkReady`
events, enqueue each chunk's audio in `chunkIndex` order, and play them
gaplessly via WaveSurfer.js / Web Audio so playback can begin before the whole
document is processed.

#### Scenario: Playback starts on first chunk

- **WHEN** the first `ChunkReady` arrives and the user has pressed play
- **THEN** audio playback begins for chunk 0 without waiting for later chunks

#### Scenario: Out-of-order arrival is reordered

- **WHEN** chunk 2 arrives before chunk 1
- **THEN** the queue holds chunk 2 and plays chunk 1 first, preserving document
  order

#### Scenario: Underrun pauses rather than skips

- **WHEN** the next chunk has not yet arrived at the end of the current chunk
- **THEN** playback pauses at the boundary and resumes automatically when the
  next chunk arrives, without skipping words

### Requirement: Real-time word sync engine

The frontend SHALL run a `requestAnimationFrame` loop that, on each frame, maps
the current global playback time to the active word via binary search over the
ordered word-timing array, and SHALL highlight exactly one active word at a
time (or the merged source-word set for multi-token words).

#### Scenario: Active word resolved by binary search

- **WHEN** playback time advances to a value within word N's `[startMs, endMs]`
- **THEN** the engine selects word N in O(log n) and applies the active highlight
  to it, clearing the previous word's highlight

#### Scenario: Seek updates highlight immediately

- **WHEN** the user clicks a word or seeks the audio
- **THEN** playback time jumps to that word's `startMs` and the highlight updates
  on the next frame without scanning linearly from the start

### Requirement: Auto-scroll to active word

The frontend SHALL keep the active word visible, scrolling the document viewport
(or turning the EPUB page) when the active word moves outside a configurable
visible margin, using smooth scrolling that does not fight manual user scrolling.

#### Scenario: Off-screen word scrolls into view

- **WHEN** the active word advances below the visible area
- **THEN** the viewport smoothly scrolls so the active word is back within the
  margin

#### Scenario: Manual scroll temporarily suspends auto-scroll

- **WHEN** the user manually scrolls away during playback
- **THEN** auto-scroll is suspended for a short grace period and then resumes
  following the active word

### Requirement: Playback controls

The frontend SHALL provide play/pause, playback speed selection, voice
selection, and seek-by-clicking-a-word controls, and selecting a different voice
SHALL re-trigger synthesis for the session via the backend.

#### Scenario: Speed change preserves sync

- **WHEN** the user changes playback speed during playback
- **THEN** audio rate and the sync engine's time mapping both update so the
  highlighted word stays aligned with the audio

#### Scenario: Click-to-seek

- **WHEN** the user clicks a rendered word
- **THEN** playback seeks to that word and continues from there

### Requirement: UI state machine

The frontend SHALL present distinct UI states — `idle`, `uploading`,
`processing`, `playing`, `paused`, and `error` — driven by session status and
playback state, and SHALL surface processing progress and recoverable errors.

#### Scenario: Processing shows progress

- **WHEN** the session is `processing`
- **THEN** the UI shows a progress indicator reflecting `Progress` events and
  enables play once the first chunk is ready

#### Scenario: Connection loss is recoverable

- **WHEN** the SignalR connection drops during streaming
- **THEN** the UI enters a reconnecting state, automatically re-subscribes on
  reconnect, backfills missed chunks, and resumes without losing playback
  position
