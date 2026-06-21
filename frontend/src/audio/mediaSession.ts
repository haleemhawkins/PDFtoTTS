/**
 * Lock-screen / Control Center integration via the Media Session API. Lets iOS
 * show the document as the now-playing item and control playback while the screen
 * is locked, mapping system transport controls onto the reader's actions.
 *
 * All calls are guarded — on browsers without Media Session they are silent no-ops.
 */

type Handlers = {
  play: () => void;
  pause: () => void;
  seekTo: (ms: number) => void;
  seekBy: (deltaMs: number) => void;
  nextTrack: () => void;
  prevTrack: () => void;
};

function ms(): MediaSession | null {
  return "mediaSession" in navigator ? navigator.mediaSession : null;
}

export function setMediaMetadata(title: string, artworkUrl?: string): void {
  const session = ms();
  if (!session || typeof MediaMetadata === "undefined") return;
  session.metadata = new MediaMetadata({
    title,
    artist: "PDFtoTTS",
    artwork: artworkUrl ? [{ src: artworkUrl, sizes: "512x512", type: "image/png" }] : [],
  });
}

export function setMediaHandlers(h: Handlers): void {
  const session = ms();
  if (!session) return;
  const set = (action: MediaSessionAction, fn: ((d: MediaSessionActionDetails) => void) | null) => {
    try {
      session.setActionHandler(action, fn);
    } catch {
      /* unsupported action on this browser */
    }
  };
  set("play", () => h.play());
  set("pause", () => h.pause());
  set("seekforward", (d) => h.seekBy((d.seekOffset ?? 10) * 1000));
  set("seekbackward", (d) => h.seekBy(-(d.seekOffset ?? 10) * 1000));
  set("seekto", (d) => {
    if (d.seekTime != null) h.seekTo(d.seekTime * 1000);
  });
  set("nexttrack", () => h.nextTrack());
  set("previoustrack", () => h.prevTrack());
}

export function setMediaPlaybackState(state: MediaSessionPlaybackState): void {
  const session = ms();
  if (session) session.playbackState = state;
}

export function setMediaPositionState(durationMs: number, positionMs: number, rate = 1): void {
  const session = ms();
  if (!session || !session.setPositionState) return;
  const duration = Math.max(0, durationMs) / 1000;
  const position = Math.min(Math.max(0, positionMs) / 1000, duration);
  try {
    session.setPositionState({ duration, position, playbackRate: rate || 1 });
  } catch {
    /* invalid state (e.g. position > duration mid-update) — ignore this frame */
  }
}

export function clearMediaSession(): void {
  const session = ms();
  if (!session) return;
  session.metadata = null;
  const actions: MediaSessionAction[] = [
    "play", "pause", "seekforward", "seekbackward", "seekto", "nexttrack", "previoustrack",
  ];
  for (const a of actions) {
    try {
      session.setActionHandler(a, null);
    } catch {
      /* ignore */
    }
  }
  setMediaPlaybackState("none");
}
