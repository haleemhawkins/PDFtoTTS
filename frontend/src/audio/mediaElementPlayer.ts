import type { PlaybackEngine } from "./playbackEngine";

/**
 * Playback through a single HTML `<audio>` element fed by the session's HLS
 * playlist (`/api/sessions/{id}/hls/playlist.m3u8`). Safari plays HLS natively via
 * AVPlayer, so audio keeps going while the iOS screen is locked / the app is
 * backgrounded, and the lock-screen controls work — which is the point of this
 * engine (Web Audio is suspended on lock).
 *
 * Segments are the session's chunks in order, so the element's `currentTime` maps
 * onto the session timeline (chunk 0 = session start). That lets the existing sync
 * engine (`buildTimeline`/`activeWordIndex`) work unchanged: `currentMs()` is just
 * `currentTime * 1000`.
 */
export class MediaElementPlayer implements PlaybackEngine {
  readonly kind = "media" as const;
  readonly continuesInBackground = true;

  private readonly audio: HTMLAudioElement;
  // `playing` is our intent. Every self-initiated pause (pause/reset/dispose/
  // setSource) clears it synchronously BEFORE the element's async `pause` event
  // fires, so the handler can tell our pause (intent already false) from an OS
  // interruption (intent still true) without a separate flag.
  private playing = false;

  onUnderrun?: () => void;
  onResumed?: () => void;
  onReady?: () => void;
  onInterrupted?: () => void;

  constructor() {
    setPlaybackSession();

    const audio = new Audio();
    audio.preload = "auto";
    audio.controls = false;
    audio.setAttribute("x-webkit-airplay", "allow");
    // Mirror the Web Audio engine's pitch-shifting rate semantics (real speed
    // changes go through Kokoro re-synth, not this).
    audio.preservesPitch = false;
    // iOS will NOT grant background playback or lock-screen controls to a detached
    // media element — it must live in the document. Keep it present but invisible.
    audio.style.display = "none";
    document.body.appendChild(audio);
    audio.addEventListener("canplay", () => this.onReady?.());
    audio.addEventListener("canplaythrough", () => this.onReady?.());
    audio.addEventListener("waiting", () => {
      if (this.playing) this.onUnderrun?.();
    });
    audio.addEventListener("playing", () => this.onResumed?.());
    // A `pause` while we still intend to be playing is an OS interruption (call /
    // another app grabbed the session): we never cleared `playing`. Capture it as a
    // clean pause; resume is an explicit Play tap. Backgrounding/locking does NOT
    // fire this — the element keeps playing — which is exactly what we want.
    audio.addEventListener("pause", () => {
      if (this.playing && !this.audio.ended) {
        this.playing = false;
        this.onInterrupted?.();
      }
    });
    this.audio = audio;
  }

  setSource(url: string): void {
    this.playing = false; // clear intent first: any pause from the src swap is ours
    this.audio.src = url;
    this.audio.load();
  }

  // The element fetches the stream itself; ingest only needs to keep the timeline
  // moving (handled by the caller). Resolve immediately.
  ingest(): Promise<void> {
    return Promise.resolve();
  }

  play(): void {
    if (this.playing) return;
    this.playing = true;
    // Re-assert the playback category inside the gesture — some iOS versions only
    // honor it when set close to a user-initiated play.
    setPlaybackSession();
    // Must run inside the user gesture (it does — play() is the Play handler).
    void this.audio.play().catch(() => {
      this.playing = false;
      this.onInterrupted?.();
    });
  }

  pause(): void {
    if (!this.playing && this.audio.paused) return;
    this.playing = false; // clear intent BEFORE the async pause event
    this.audio.pause();
  }

  setRate(rate: number): void {
    this.audio.playbackRate = rate;
  }

  currentMs(): number {
    return this.audio.currentTime * 1000;
  }

  // Note: the PlaybackEngine `seek(globalMs, chunkOffsets)` contract passes chunk
  // offsets, but the stitched stream is session-relative so global ms maps straight
  // to currentTime — this implementation needs only the first argument.
  seek(globalMs: number): void {
    // A seek into not-yet-produced audio is handled upstream by re-synthesis
    // (new session ⇒ new source); here we only move within the stream.
    try {
      this.audio.currentTime = Math.max(0, globalMs / 1000);
    } catch {
      /* element not ready to seek yet; currentTime set will retry on next seek */
    }
  }

  reset(): void {
    this.playing = false;
    this.audio.pause();
    this.audio.removeAttribute("src");
    this.audio.load();
    this.audio.playbackRate = 1;
  }

  dispose(): void {
    this.playing = false;
    this.audio.pause();
    this.audio.removeAttribute("src");
    this.audio.load();
    this.audio.remove();
  }
}

/** Opt the audio session into the "playback" category (Safari 16.4+): plays
 *  through the mute switch and keeps audio alive while the screen is locked. */
function setPlaybackSession(): void {
  const nav = navigator as Navigator & { audioSession?: { type: string } };
  if (nav.audioSession) nav.audioSession.type = "playback";
}
