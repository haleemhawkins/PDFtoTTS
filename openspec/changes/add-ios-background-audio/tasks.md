## 1. Backend: HLS stream

> The WAV stitched-stream approach (originally tasked here) was abandoned — iOS
> Safari won't stream a growing WAV. Shipped via HLS instead (see design.md note).

- [x] 1.1 Add `ffmpeg` to the API Docker image.
- [x] 1.2 Add `Audio/HlsTranscoder.cs`: transcode a chunk's WAV → AAC/MPEG-TS segment on demand (ffmpeg), cached on disk with a per-segment lock.
- [x] 1.3 Build the EVENT `.m3u8` from the contiguous produced chunks' durations; finalize with `#EXT-X-ENDLIST` when synthesis ends.
- [x] 1.4 Add endpoints `GET …/hls/playlist.m3u8` and `GET …/hls/{index}.ts`; `AudioSessionDir` storage helper for the segment directory.
- [x] 1.5 Wait for the first segment before serving the playlist so the player never loads an empty playlist; segment dir is removed by the existing `DeleteSessionAudio` teardown.
- [x] 1.6 Verified end-to-end in-container: playlist correct, segments are valid AAC/24 kHz/mono MPEG-TS, on-demand transcode + caching work.

## 2. Frontend: playback engine abstraction

- [x] 2.1 Extract a `PlaybackEngine` interface and make `AudioQueuePlayer` implement it (added `continuesInBackground=false`, no-op `setSource`; no behavior change).
- [x] 2.2 Implement `MediaElementPlayer` backed by one `<audio>` element fed by the HLS playlist URL; element attached to the DOM (required for iOS background/lock-screen); sets `audioSession.type='playback'` in the constructor and in `play()` (within the gesture).
- [x] 2.3 Map engine methods: `currentMs()` = `audio.currentTime*1000`; `setRate` → `audio.playbackRate`; `seek(globalMs)` → set `audio.currentTime`; `reset/dispose` keep the element, drop the src.
- [x] 2.4 Map media events to callbacks: `canplay`/`canplaythrough`→`onReady`, `waiting`→`onUnderrun`, `playing`→`onResumed`, OS `pause` (intent still playing)→`onInterrupted`.
- [x] 2.5 Add iOS/iPadOS detection (`isIosWebkit`) and `createPlaybackEngine()`; default to `AudioQueuePlayer` elsewhere.

## 3. Frontend: wire engine into the reader

- [x] 3.1 `useReader`/`openSession` instantiate the selected engine and call `setSource(streamUrl(session.id))` (the HLS playlist URL) after `createSession`; timeline building (`onChunk`→`buildTimeline`) unchanged.
- [x] 3.2 `ingest` still feeds the timeline; the media engine's `ingest` is a no-op (the element fetches HLS itself) — no per-chunk decode required.
- [x] 3.3 Route `seek`: within the stream → `currentTime`; not-yet-produced word → existing re-synth (`restartAt`/`openSession`) producing a new playlist `src`.
- [x] 3.4 Confirmed on-device: background playback works with the screen locked; lock-screen controls appear (device test by the user).

## 4. Background playback behavior

- [x] 4.1 When the engine `continuesInBackground`, the `visibilitychange` handler no longer pauses on `document.hidden`; the Web Audio fallback still pauses.
- [x] 4.2 Backgrounding is not treated as an interruption on the media path; only an OS `pause` while intent is still playing fires `onInterrupted`.
- [x] 4.3 Wake-lock logic left harmless on the media path (foreground re-acquire only; never forces pause).

## 5. Media Session controls

- [x] 5.1 Add `mediaSession` module: `setMediaMetadata` (title from filename, artwork from the library's cached cover when available — OQ3 resolved) and `setMediaPositionState` from the timeline each tick.
- [x] 5.2 Register action handlers: seekbackward/forward + seekto → `seekToMs`; previous/next → `jumpToPage`. Play/pause handlers are registered only on the Web Audio path — on iOS they're left to the native media-element transport (a lock-screen JS `play()` is silent in the background); the `playing`/`pause` element events sync app state instead.
- [x] 5.3 Update position each tick and playback state on play/pause; clear handlers/metadata on reset and unmount.

## 6. Verification

- [x] 6.1 Frontend unit tests for `MediaElementPlayer` (currentMs/seek/rate/event→callback, self-pause vs OS-interrupt).
- [x] 6.2 Manual iOS check: play, lock screen → audio continues; confirmed on the user's iPhone.
- [ ] 6.3 Known follow-up: ~80 ms/chunk AAC priming drift between audio and the word highlight on long sessions (not yet addressed).
- [x] 6.4 `openspec validate add-ios-background-audio --strict` passes; frontend (26) + backend (102) test suites pass; tsc + eslint clean; vite + dotnet builds succeed.
