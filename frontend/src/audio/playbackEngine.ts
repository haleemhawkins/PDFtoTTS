import { AudioQueuePlayer } from "./audioPlayer";
import { MediaElementPlayer } from "./mediaElementPlayer";

/**
 * The playback contract `useReader` drives, with two implementations:
 *  - {@link AudioQueuePlayer} — Web Audio, precise/gapless, but iOS suspends it on
 *    lock/background (the default off iOS);
 *  - {@link MediaElementPlayer} — a single `<audio>` fed by the server-stitched
 *    session stream, which keeps playing while the iOS screen is locked.
 * Both expose the same global-ms timeline contract so the sync engine is unchanged.
 */
export interface PlaybackEngine {
  /** Which implementation this is — for diagnostics/logging. */
  readonly kind: "media" | "webaudio";
  /** True if this engine keeps playing while the screen is locked / app backgrounded. */
  readonly continuesInBackground: boolean;

  /** Point the engine at a session's audio (media-element engine only; no-op otherwise). */
  setSource(url: string): void;
  /** Buffer/await a chunk. Web Audio decodes; the media engine only tracks readiness. */
  ingest(chunkIndex: number, url: string, durationMs: number): Promise<void>;

  play(): void;
  pause(): void;
  setRate(rate: number): void;
  /** Current global position in unscaled audio milliseconds (session-relative). */
  currentMs(): number;
  seek(globalMs: number, chunkOffsets: Map<number, number>): void;
  /** Drop buffered audio, rewind to start, KEEP the underlying context/element. */
  reset(): void;
  dispose(): void;

  onUnderrun?: () => void;
  onResumed?: () => void;
  onReady?: () => void;
  onInterrupted?: () => void;
}

/** iOS/iPadOS WebKit, where Web Audio is killed on lock and only a media element
 *  continues in the background. iPadOS 13+ reports as "Mac", so also sniff touch;
 *  every browser on iOS is WebKit, so a Mac-with-touchscreen heuristic is safe. */
export function isIosWebkit(): boolean {
  const ua = navigator.userAgent;
  const iOS = /iP(hone|ad|od)/.test(ua);
  const iPadOS = /Mac/.test(ua) && navigator.maxTouchPoints > 1;
  const legacyIPad = navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1;
  return iOS || iPadOS || legacyIPad;
}

/** iOS drives a media element's lock-screen transport natively. Resuming the
 *  `<audio>` element via the system remote keeps its audio session active in the
 *  background; a JS `play()` from the lock screen advances time but plays SILENTLY
 *  until the app is foregrounded. So on iOS we leave play/pause to the OS and only
 *  wire the richer transport actions (seek, page turns) through Media Session. */
export function usesNativeMediaTransport(): boolean {
  return isIosWebkit();
}

/** Pick the playback engine: media element on iOS (background audio), Web Audio
 *  elsewhere (proven gapless/seek behavior, unchanged). */
export function createPlaybackEngine(): PlaybackEngine {
  return isIosWebkit() ? new MediaElementPlayer() : new AudioQueuePlayer();
}
