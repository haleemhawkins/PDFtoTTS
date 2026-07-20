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

The frontend SHALL keep the active word visible while it advances: whenever the
active word CHANGES (playback advancing, or a seek/tap), the viewport SHALL
smoothly re-center on it — following the voice wins over a recent manual scroll,
so the reader can never drift away from where reading is happening. When the
active word has NOT changed but its on-screen boxes were rebuilt (a streamed
chunk arriving, a page-turn settling), the viewport SHALL scroll only if the word
is off-screen and the user has not manually scrolled within a short grace period.

#### Scenario: Playback always follows the voice

- **WHEN** the active word advances during playback, even after the user scrolled
  away to skim elsewhere
- **THEN** the viewport smoothly re-centers on the newly spoken word

#### Scenario: Manual scroll while paused is respected

- **WHEN** the user scrolls away while paused and a streamed chunk redraws the
  word boxes (no active-word change)
- **THEN** the viewport stays where the user put it for the grace period rather
  than snapping back

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
- **THEN** the UI shows a progress indicator reflecting `Progress` events, shows
  a loading state on the play control while audio at the current position is not
  yet buffered, and enables (and visually cues) the play control once it is ready

#### Scenario: Connection loss is recoverable

- **WHEN** the SignalR connection drops during streaming
- **THEN** the UI enters a reconnecting state, automatically re-subscribes on
  reconnect, backfills missed chunks, and resumes without losing playback
  position

### Requirement: Playback starts only on explicit intent

Audio SHALL begin only on an explicit user Play tap or a playback-initiated
continuation (auto page-turn while already playing); it SHALL NOT auto-start when
a document first finishes/produces audio, when newly synthesized chunks arrive
while paused, on restore, or when manually navigating. The play control SHALL be
disabled while the audio at the current position is not yet buffered, and SHALL
indicate the loading-vs-ready transition so a single tap starts read-out.

#### Scenario: Ready audio does not auto-play

- **WHEN** synthesis produces playable audio for the current position and the
  user has not pressed Play
- **THEN** playback does not start; the play control becomes enabled and cues
  "ready", and one tap begins read-out from that position

#### Scenario: Chunk completing while paused stays silent

- **WHEN** the reader is paused and a freshly synthesized chunk arrives
- **THEN** no audio plays until the user taps Play

### Requirement: Resume position across backgrounding and reload

The reader SHALL preserve playback position when interrupted. When the app is
backgrounded or its audio context is interrupted, it SHALL pause cleanly at the
current position and remain paused on return (no auto-resume). It SHALL persist
the exact word being read so that a full reload resumes at that word (re-synthesizing
from there), not merely the page.

#### Scenario: Return from background resumes from the same spot

- **WHEN** the app is backgrounded mid-playback and later reopened
- **THEN** it is paused at the word it left off on, and tapping Play resumes from
  exactly there without having auto-played on return

#### Scenario: Full reload resumes at the exact word

- **WHEN** the app is reopened after the page was unloaded
- **THEN** it restores the document and lands paused at the exact saved word
  (highlighted), synthesizing from that word

### Requirement: Manual navigation preloads without auto-playing

Manually flipping pages (Prev/Next) or selecting a page/chapter SHALL position
the reader at the target page's first word and preload its audio, but SHALL NOT
start playback. Rapid flipping SHALL be debounced so it does not start and cancel
a synthesis for every intermediate page. Playback-initiated page turns (the
active word crossing into a new page) SHALL continue reading uninterrupted.

#### Scenario: Manual page flip stays paused

- **WHEN** the user taps Next while paused
- **THEN** the view shows the next page, audio for it is prepared, and reading
  does not begin until the user taps Play

#### Scenario: Auto page-turn keeps reading

- **WHEN** playback advances past the end of a page
- **THEN** the next page is shown and reading continues without interruption

### Requirement: Page and chapter navigation surface

The frontend SHALL provide a navigation surface to jump to a location visually:
a grid of page thumbnails (rendered lazily for performance) and, when the
document exposes an outline, a list of chapters that resolve to pages. Selecting
a thumbnail or chapter SHALL navigate the reader there (paused, preloaded).

#### Scenario: Jump by page thumbnail

- **WHEN** the user opens the navigation surface and selects a page thumbnail
- **THEN** the reader navigates to that page and the surface closes

#### Scenario: Jump by chapter

- **WHEN** the document has an embedded outline and the user selects a chapter
- **THEN** the reader navigates to that chapter's page; if no outline exists, the
  chapter list is absent/empty and page navigation remains available

### Requirement: Immersive, low-chrome reading

The frontend SHALL consolidate transport controls into a single compact bar and
SHALL maximize reading area by auto-hiding chrome during playback, revealing it
on a tap, so document content occupies the screen while reading.

#### Scenario: Chrome auto-hides while reading

- **WHEN** playback is underway on a PDF
- **THEN** the control chrome hides after a short delay to give the page the full
  screen, and tapping the page reveals the controls again
