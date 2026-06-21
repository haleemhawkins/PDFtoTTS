## Why

On iOS, the reader stops the moment the screen locks or Safari/the PWA is backgrounded. The playback engine (`AudioQueuePlayer`) uses the Web Audio API, and iOS **deliberately suspends `AudioContext` on lock/background** — there is no flag to override this. The app already detects the suspension and pauses cleanly (`audioPlayer.ts` `onstatechange`; `useReader.ts` visibility handler), so the behavior is correct but the capability is missing: you cannot listen with the screen off. Only an HTML media element (`<audio>`/`<video>`) is allowed to keep playing while locked, so background playback requires routing audio through a media element instead of Web Audio.

## What Changes

- Add a **per-session HLS endpoint** (playlist + AAC/MPEG-TS segments) that exposes the session's chunks as a growing HLS stream. (HLS is the format iOS plays natively via AVPlayer — the original WAV stitched-stream attempt was abandoned because Safari's range-based media loader won't stream a growing WAV; see `design.md`.)
- Add a **media-element playback engine** (`<audio>`-backed) that exposes the same interface as `AudioQueuePlayer` (`play/pause/setRate/seek/currentMs/reset/dispose` + the `onReady/onUnderrun/onResumed/onInterrupted` callbacks) so the rest of the reader is unchanged. Position/highlight sync is driven by the element's `currentTime` instead of the Web Audio clock.
- Add **Media Session API** integration: lock-screen / Control Center metadata (title, author, cover) and transport handlers (play, pause, seek, skip) wired to the existing reader controls.
- **Relax the background-pause behavior** so that, when the media-element engine is active, locking the screen or backgrounding the app does **not** pause — playback continues. The existing clean-pause-on-interruption stays as the fallback path for the Web Audio engine and for genuine OS interruptions (incoming call).
- Keep the existing **word-highlight timeline** (built from SignalR chunk metadata, `buildTimeline`) **unchanged** — it is independent of the audio decode path, which is what keeps regression minimal.
- Keep **all existing reader features** working through the new engine: play/pause, speed (Kokoro re-synth + playback-rate), voice change, seek-to-word, click-to-seek, jump-to-word/page/section, cross-device resume, mute-switch override, and the wake lock.

## Capabilities

### New Capabilities
- `background-audio`: TTS playback continues while the iOS screen is locked or the app is backgrounded, with lock-screen transport controls and metadata, driven by a media-element engine fed by a per-session HLS stream.

### Modified Capabilities
- `reader-frontend`: The playback engine and position/sync source change (Web Audio queue → media element + `currentTime`), and background/lock behavior changes from "pause" to "continue" when the media engine is active.
- `reader-backend`: Adds a per-session stitched audio stream endpoint alongside the existing per-chunk audio endpoint.

## Impact

- **Frontend**
  - `frontend/src/audio/playbackEngine.ts` — `PlaybackEngine` interface + iOS detection + engine selector.
  - `frontend/src/audio/mediaElementPlayer.ts` — `<audio>`-backed engine loading the session's HLS playlist; `frontend/src/audio/audioPlayer.ts` — `AudioQueuePlayer` now implements the shared interface (unchanged behavior, non-iOS default).
  - `frontend/src/audio/mediaSession.ts` — Media Session metadata + transport handlers.
  - `frontend/src/hooks/useReader.ts` — select engine, drive sync from `currentTime`, relax background pause when the media engine is active, wire Media Session.
- **Backend (`PDFtoTTS.Api`)**
  - `ffmpeg` added to the API image.
  - `Audio/HlsTranscoder.cs` + endpoints `GET /api/sessions/{id}/hls/playlist.m3u8` and `…/hls/{index}.ts` — growing EVENT playlist; chunks transcoded WAV→AAC/MPEG-TS on demand and cached.
  - `Storage/FileStorage.cs` — `AudioSessionDir` helper for the HLS segment directory.
- **Sync engine** (`frontend/src/sync/wordTimeline.ts`) — unchanged.
- **No data model / persistence changes**; audio remains per-session and ephemeral.
- **Risks**: small (~80 ms/chunk) AAC encoder-priming drift between audio and the word highlight on long sessions; on-demand transcode latency must stay under segment duration (it does — ~0.2–1 s for 10–50 s chunks). Preserving the iOS "playback" audio-session category + user-gesture start. See `design.md`.
