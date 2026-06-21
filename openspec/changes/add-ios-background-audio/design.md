> **Implementation note (shipped):** The audio-delivery decision below (D2/D3 —
> a server-stitched, range-capable WAV stream) was **abandoned during testing**.
> iOS Safari's media loader is range-based and will not stream a *growing* WAV: it
> leads with a `bytes=0-1` probe, locks onto whatever total length the first
> response reports, and refuses to play when the live stream's headers contradict
> that. **The shipped solution is HLS** — the format iOS plays natively via
> AVPlayer, which is what actually delivers background/locked playback and
> lock-screen controls. The backend adds `ffmpeg` and transcodes each WAV chunk to
> an AAC/MPEG-TS segment on demand, served via a growing EVENT `.m3u8` playlist
> (`/api/sessions/{id}/hls/…`). The frontend `MediaElementPlayer` loads that
> playlist. Everything else below (D1 two-engine selection, D4 sync via
> `currentTime`, D5 relaxed background pause, D6 Media Session) shipped as designed.
> Known follow-up: ~80 ms/chunk AAC priming drift between audio and the word
> highlight on long sessions.

## Context

Playback today runs through `AudioQueuePlayer` (`frontend/src/audio/audioPlayer.ts`),
which decodes each WAV chunk and schedules `AudioBufferSourceNode`s on a single
`AudioContext`. iOS **suspends `AudioContext` on screen-lock/background by design**;
`useReader.ts` and the player already detect this and pause cleanly. The
word-highlight engine is decoupled from audio decode: `onChunk` (SignalR) feeds
`ChunkQueue` → `buildTimeline`, and a `requestAnimationFrame` loop maps
`player.currentMs()` to the active word. Chunks are 16-bit mono 24 kHz WAV files
(`{chunkIndex:D5}.wav`, standard 44-byte header) served at
`GET /api/sessions/{id}/chunks/{index}/audio` with Range support.

Only an HTML media element (`<audio>`) may keep playing while the iOS screen is
locked, and only if started by a user gesture under the "playback" audio-session
category (already set via `navigator.audioSession.type = "playback"`). So we add a
media-element engine fed by a single, server-stitched session stream, and drive
sync from the element's `currentTime`. The timeline/highlight engine is untouched —
that is what keeps regression minimal.

## Goals / Non-Goals

**Goals:**
- TTS keeps playing on iOS with the screen locked / app backgrounded.
- Lock-screen / Control Center metadata and transport controls.
- Preserve every existing reader feature: play/pause, Kokoro speed change, voice
  change, seek-to-word, click-to-seek, jump-to-word/page/section, cross-device
  resume, mute-switch override, wake lock, gapless streaming start-before-complete.
- Keep the word-highlight timeline and sync math unchanged.

**Non-Goals:**
- Replacing the proven Web Audio engine on non-iOS platforms (it stays the default
  there; this change is additive and iOS-gated).
- Transcoding to MP3/AAC or adopting `ManagedMediaSource`/MSE (considered, deferred).
- Offline download / persistent audio caching.
- Changing the synthesis pipeline, chunk format, or persistence model.

## Decisions

### D1: Two playback engines behind one interface; media element on iOS
Extract the methods `useReader` calls — `play/pause/setRate/seek/currentMs/reset/
dispose` plus the `onReady/onUnderrun/onResumed/onInterrupted` callbacks — into a
`PlaybackEngine` interface. `AudioQueuePlayer` implements it (unchanged); add
`MediaElementPlayer` backed by one `<audio>` element with `src` = the session
stream. `useReader` picks `MediaElementPlayer` on iOS/iPadOS Safari (and the PWA),
`AudioQueuePlayer` elsewhere.
- *Why:* Background playback only requires a media element on iOS; gating keeps
  desktop behavior byte-for-byte identical (minimal regression) while still letting
  us flip the default later.
- *Alternative — media element everywhere:* one code path, more test coverage, but
  changes proven desktop seek/gapless behavior. Rejected for now (Open Question OQ1).

### D2: Server-stitched WAV stream, gapless via PCM concatenation
New `GET /api/sessions/{id}/stream` emits **one** WAV header then the raw PCM of
chunk 0,1,2,… (each chunk's 44-byte header stripped). All chunks share the same
format (mono/16-bit/24 kHz), so concatenated PCM is sample-accurate and gapless.
- *Why WAV:* zero new dependency, sample-accurate gapless (no encoder
  delay/padding gap between chunks), iOS plays/streams WAV in `<audio>` and
  continues in background. Global ms == stream playback time because stream order
  == timeline order, so `audio.currentTime*1000` maps directly onto the existing
  timeline.
- *Alternative — per-chunk MP3 concat:* ~10× smaller, but frame padding risks
  audible gaps at chunk seams and needs a transcode step. Deferred.
- *Alternative — ManagedMediaSource (iOS 17.1+) + fMP4:* client-side append/seek
  control, but needs an fMP4/AAC segmenter and browser-version gating. Higher cost,
  deferred (OQ2).

### D3: Live growth via chunked transfer; seek via Range over the produced snapshot
The endpoint serves two ways off the same concatenation:
- **Progressive (no Range):** `Transfer-Encoding: chunked`; write the WAV header
  with a maximal placeholder data length, flush each chunk's PCM as it appears, and
  keep the response open — awaiting newly produced chunks — until the session is
  `Complete` or the client disconnects. This is what lets one `<audio>` play seam-
  lessly across chunk boundaries, including in background (the open media stream
  keeps the connection alive).
- **Range request:** compute total length from the chunks produced *so far*
  (header + Σ data sizes), map the byte range to chunk files, and return `206`.
  This backs in-buffer seeking.

Backward / within-produced seeks set `audio.currentTime`, which makes the element
issue a Range request the endpoint satisfies. Forward seeks into not-yet-produced
audio keep the **existing** behavior: cancel and re-synthesize from that source
word (a new session ⇒ new stream URL ⇒ new `src`). This reuses `openSession`/
`restartAt` almost verbatim.

### D4: Sync reads `currentTime`; engine exposes it as `currentMs()`
`MediaElementPlayer.currentMs()` returns `audio.currentTime*1000` (already in the
global timeline frame). The rAF loop, `buildTimeline`, `activeWordIndex`,
auto-scroll, and `seek(globalMs, chunkOffsets)` are unchanged. `setRate` maps to
`audio.playbackRate` (the playback-rate fast path); Kokoro-native speed change
still goes through re-synth as today. `ingest()` becomes a readiness signal only —
the media element fetches audio itself via the stream, so per-chunk decode is gone
on the iOS path; `onReady` fires when the element can play (`canplay`/`progress`),
`onUnderrun` on `waiting`, `onResumed` on `playing`.

### D5: Relax background pause; keep it for genuine interruptions
When the media engine is active, the `visibilitychange` handler in `useReader` must
**not** pause on `document.hidden`, and `MediaElementPlayer` must not treat
backgrounding as an interruption. We still honor real interruptions: the `<audio>`
`pause` event fired by the OS for an incoming call (and the audio-session interrupt
where available) routes to `onInterrupted` → clean pause, position kept. The wake
lock is unnecessary for keeping audio alive on this path but is harmless; keep
re-acquiring it in foreground for screen-on reading.

### D6: Media Session integration
A small `mediaSession` module sets `navigator.mediaSession.metadata`
(title = filename, artwork from the library cover / `GET /…/original` thumbnail)
and registers handlers: `play`→`play()`, `pause`→`pause()`,
`seekbackward`/`seekforward`→`seekToMs(currentMs ± step)`,
`previoustrack`/`nexttrack`→`jumpToPage(±1)` (or section for EPUB),
`seekto`→`seekToMs(details.seekTime*1000)`. `setPositionState` is updated from the
timeline so the lock-screen scrubber tracks progress.

## Risks / Trade-offs

- **WAV bandwidth (~173 MB/hour, 48 KB/s).** → Acceptable for home/LAN use; MP3
  transcode can be added later (D2 alternative) without changing the client contract.
- **Long-lived chunked response per active session.** → One stream per playing
  client; close on disconnect, on session `Complete`+fully-sent, and on a server
  timeout. Reuse existing session teardown/`DeleteSessionAudio`.
- **Seeking against a still-growing stream is awkward.** → Constrain seek targets:
  in-produced → Range; not-yet-produced → re-synth (existing path). Don't rely on
  the element knowing total duration; the UI scrubber is driven by the timeline.
- **iOS silently pausing background audio if a gesture/category is missing.** →
  Ensure `play()` runs in the user gesture, `<audio>` is `playsinline` + preloaded,
  and `audioSession.type='playback'` is set before play.
- **Gap at chunk seams.** → Avoided by concatenating raw PCM under one header (D2);
  add a test asserting concatenated length == Σ chunk PCM samples.
- **Two engines drift in behavior.** → Shared `PlaybackEngine` interface + reuse the
  same `seek/currentMs` timeline contract; engine-agnostic tests in `useReader`.

## Migration Plan

1. Land the backend `/stream` endpoint additively (per-chunk endpoint stays). No
   data migration; audio is ephemeral per session.
2. Land `PlaybackEngine` interface + `MediaElementPlayer`; keep `AudioQueuePlayer`
   as default. iOS detection gates engine selection.
3. Wire Media Session + relax background pause only when the media engine is active.
4. Rollback = engine selector returns `AudioQueuePlayer` everywhere (one flag); the
   `/stream` endpoint can remain unused with no effect.

## Open Questions

- **OQ1:** Make the media-element engine the default on *all* platforms (one path,
  better coverage) instead of iOS-only? Default here is iOS-only to minimize
  regression.
- **OQ2:** If chunk-seam gaps or bandwidth prove problematic, adopt MP3 streaming
  or `ManagedMediaSource` + fMP4. Out of scope for this change.
- **OQ3:** Cover artwork source for Media Session — reuse `LibraryCover` rendering
  vs a dedicated thumbnail endpoint. Prefer reusing existing cover data if exposable.
