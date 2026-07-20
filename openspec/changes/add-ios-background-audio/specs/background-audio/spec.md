## ADDED Requirements

### Requirement: Playback continues while screen is locked or app is backgrounded

When the media-element playback engine is active, the reader SHALL continue
playing TTS audio while the iOS screen is locked or the app/tab is backgrounded,
rather than pausing. Playback SHALL have been started by a user gesture and the
audio session SHALL use the "playback" category so audio is not silenced by the
mute switch or the lock screen.

#### Scenario: Audio continues when the screen locks

- **WHEN** the user is playing and locks the iOS screen (or switches to another app)
- **THEN** narration keeps playing uninterrupted and the playback position keeps
  advancing

#### Scenario: Returning to foreground keeps playing in sync

- **WHEN** the user returns to the app after listening with the screen locked
- **THEN** the reader is still in the playing state and the highlighted word
  matches the audio position (no forced pause, no rewind)

#### Scenario: Genuine OS interruption still pauses cleanly

- **WHEN** a hard interruption occurs (incoming call, another app takes the audio session)
- **THEN** the reader pauses cleanly at the current position and resumes only on an
  explicit Play, without losing the position

### Requirement: Lock-screen transport controls and metadata

The reader SHALL publish Media Session metadata (document title, author when
known, and cover artwork when available) and SHALL wire the lock screen /
Control Center transport to the reader. Seek and track-skip actions SHALL map to
the existing reader actions via Media Session handlers. Play/pause MAY be left
to the platform's native media-element transport where a JS handler would break
background audio (iOS: a lock-screen JS `play()` advances time but plays
silently until foregrounded); the app state SHALL still reflect a native
play/pause when the user returns.

#### Scenario: Lock screen shows the document

- **WHEN** playback is active
- **THEN** the lock screen / Control Center shows the document title and cover
  artwork as the now-playing item

#### Scenario: Lock-screen play/pause controls the reader

- **WHEN** the user taps play or pause on the lock screen
- **THEN** the reader pauses or resumes exactly as if the in-app control were used,
  and the in-app state reflects it on return

#### Scenario: Lock-screen seek/skip moves position

- **WHEN** the user uses the lock-screen scrubber or skip-forward/back controls
- **THEN** playback position moves accordingly and the active word updates to match

### Requirement: Per-session HLS stream

The backend SHALL expose the session's audio as an HLS stream (a growing EVENT
playlist plus AAC/MPEG-TS segments) suitable for native iOS playback by a single
media element, growing as chunks are synthesized. Playback SHALL be able to start
before synthesis completes, and the native player SHALL be able to seek within the
produced portion.

#### Scenario: Single source plays the whole session

- **WHEN** a media element loads the session's HLS playlist URL and plays
- **THEN** it plays segments 0, 1, 2, … in document order as one continuous track

#### Scenario: Stream grows as synthesis proceeds

- **WHEN** later chunks are still being synthesized while earlier audio plays
- **THEN** the playlist gains the new segments and playback continues into them
  without failing at the current end

#### Scenario: Seek within produced audio

- **WHEN** the player seeks to a position whose audio has already been produced
- **THEN** the corresponding segment is served and playback resumes there
