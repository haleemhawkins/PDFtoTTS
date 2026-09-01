# reader-frontend Specification

## Purpose

The React reader: render the document, play the streamed narration, and keep the spoken word highlighted and in view — across PDF and EPUB, desktop and mobile.

## Requirements

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

The frontend SHALL connect to the SignalR reader hub and receive `ChunkReady`
events to build the word-timing timeline in `chunkIndex` order. Audio playback
SHALL be performed by a pluggable engine: a **media-element engine** (an
`<audio>` element fed by the per-session stitched stream) is used where
background/locked playback is required, and the existing **Web Audio queue
engine** remains as a fallback. Either engine SHALL play audio gaplessly in
document order and SHALL allow playback to begin before the whole document is
processed. Receiving chunk metadata to extend the timeline SHALL be independent
of the audio decode path.

#### Scenario: Playback starts on first chunk

- **WHEN** the first `ChunkReady` arrives and the user has pressed play
- **THEN** audio playback begins for chunk 0 without waiting for later chunks

#### Scenario: Out-of-order arrival is reordered

- **WHEN** chunk 2 arrives before chunk 1
- **THEN** the timeline holds chunk 2 and the stream/queue plays chunk 1 first,
  preserving document order

#### Scenario: Underrun pauses rather than skips

- **WHEN** the next chunk has not yet arrived at the end of the current chunk
- **THEN** playback waits at the boundary and resumes automatically when the next
  chunk arrives, without skipping words

### Requirement: Real-time word sync engine

The frontend SHALL run a `requestAnimationFrame` loop that, on each frame, maps
the current global playback time to the active word via binary search over the
ordered word-timing array, and SHALL highlight exactly one active word at a time
(or the merged source-word set for multi-token words). The current playback time
SHALL be read from the active playback engine — the media element's `currentTime`
for the media-element engine, or the Web Audio clock for the queue engine.

#### Scenario: Active word resolved by binary search

- **WHEN** playback time advances to a value within word N's `[startMs, endMs]`
- **THEN** the engine selects word N in O(log n) and applies the active highlight

#### Scenario: Highlight tracks the media element

- **WHEN** the media-element engine is active and playing
- **THEN** the active word is computed from the element's `currentTime` and stays
  in sync, including while the screen is locked and after returning to foreground

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
`extracting`, `processing`, `playing`, `paused`, `reconnecting`, and `error` —
driven by document status, session status, and playback state, and SHALL surface
extraction/OCR and synthesis progress and recoverable errors.

#### Scenario: Extraction shows its own progress

- **WHEN** an uploaded document is still extracting (including an OCR pass)
- **THEN** the UI shows an `extracting` state driven by the document's
  `progress`, distinct from synthesis `processing`

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

The reader SHALL preserve playback position when interrupted. When the
media-element engine is active, backgrounding or locking the screen SHALL NOT
pause playback (see the background-audio capability). When a genuine OS
interruption occurs (incoming call, audio session lost), or when the Web Audio
fallback engine is active, the reader SHALL pause cleanly at the current position
and remain paused on return (no auto-resume). It SHALL persist the exact word
being read so that a full reload resumes at that word (re-synthesizing from
there), not merely the page.

#### Scenario: Background no longer force-pauses with the media engine

- **WHEN** the app is backgrounded or the screen is locked mid-playback while the
  media-element engine is active
- **THEN** playback continues, and on return the reader is still playing at the
  current position

#### Scenario: Genuine interruption resumes from the same spot

- **WHEN** an incoming call interrupts playback
- **THEN** the reader is paused at the word it left off on, and tapping Play
  resumes from exactly there without having auto-played

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

### Requirement: Library home view

The frontend SHALL present a library as the app's home: a list of all documents
from `GET /api/documents`, each showing its name, type, and status — plus a
cover thumbnail (PDF page 1 / the EPUB's declared cover, rendered lazily and
cached locally; a neutral fallback when unavailable) and a "last read" hint when
a resume position exists — with an upload affordance that accepts both file
picking and drag-and-drop. The library replaces the single-document upload
screen as the default landing view.

#### Scenario: Library lists documents

- **WHEN** the app loads and documents exist
- **THEN** each document is shown with its name and type, and selecting one opens
  it in the reader

#### Scenario: Empty library invites upload

- **WHEN** the app loads with no documents
- **THEN** an empty state with an upload control is shown

#### Scenario: Upload adds to the library

- **WHEN** the user uploads a new PDF/EPUB from the library
- **THEN** the document appears in the library and (once `Ready`) can be opened

### Requirement: Open a document by id

The frontend SHALL open a document from the library by fetching its original bytes
from `GET /api/documents/{id}/original` for rendering and starting a TTS session,
without requiring the user to re-select the file.

#### Scenario: Open from library

- **WHEN** the user selects a `Ready` document in the library
- **THEN** the reader renders the document from the server-provided original and
  begins a session at the saved position (or the start)

#### Scenario: Home returns to the library

- **WHEN** the user taps Home in the reader
- **THEN** the reader closes and the library is shown, with the document still
  present in the library

### Requirement: Rename and delete from the library

The frontend SHALL let the user rename and delete documents from the library,
calling `PATCH` and `DELETE /api/documents/{id}` and reflecting the result.

#### Scenario: Rename a document

- **WHEN** the user renames a document and confirms
- **THEN** the new name is sent via `PATCH` and shown in the library

#### Scenario: Delete a document

- **WHEN** the user deletes a document and confirms
- **THEN** the document is removed via `DELETE` and disappears from the library

### Requirement: Cross-device resume

The frontend SHALL push the current reading position (page, word, voice, speed,
timestamped) to the backend on the discrete leave-events (pause, page turn,
voice/speed change, returning home, tab hidden, page unload) and periodically
while playing, and on open SHALL resume from whichever of the locally cached and
server-stored positions is newer. Position saves SHALL be best-effort and never
disrupt reading.

#### Scenario: Resume on another device

- **WHEN** the user reads on one device and later opens the same document on
  another
- **THEN** the reader lands paused at the position the first device last
  reported, with its voice and speed

#### Scenario: Stale local cache loses

- **WHEN** the server's stored position is newer than this device's local cache
- **THEN** the server position is used and seeded into the local cache

### Requirement: In-reader voice switcher

The frontend SHALL provide a voice picker in the reader so the user can change the
narration voice mid-document. Changing the voice SHALL re-synthesize from the
current word at the new voice and remember the choice for that document.

#### Scenario: Change voice while reading

- **WHEN** the user picks a different voice from the reader menu
- **THEN** synthesis restarts from the current word using the new voice and
  playback continues from that word

#### Scenario: Voice choice is remembered

- **WHEN** the user reopens a document whose voice was changed
- **THEN** the previously chosen voice is used for the new session
