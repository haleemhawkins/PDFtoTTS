## MODIFIED Requirements

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
