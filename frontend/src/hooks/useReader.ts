import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "../api/client";
import { ChunkQueue } from "../audio/chunkQueue";
import {
  createPlaybackEngine, usesNativeMediaTransport, type PlaybackEngine,
} from "../audio/playbackEngine";
import {
  clearMediaSession, setMediaHandlers, setMediaPlaybackState, setMediaPositionState,
} from "../audio/mediaSession";
import type { ProcessedChunk, SourceWordData } from "../api/types";
import { ReaderConnection } from "../signalr/readerConnection";
import { activeWordIndex, buildTimeline, type Timeline } from "../sync/wordTimeline";
import { WakeLockManager } from "../wakeLock";

export type UiState =
  | "idle" | "uploading" | "extracting" | "processing" | "playing" | "paused" | "error" | "reconnecting";

const EMPTY_TIMELINE: Timeline = { words: [], chunkOffsets: new Map(), totalMs: 0 };

export interface ReaderController {
  state: UiState;
  /** Id of the document currently loaded in the reader (null when idle). */
  documentId: string | null;
  progress: number;
  timeline: Timeline;
  activeIndex: number; // index into timeline.words, or -1
  /** True once audio at the current position is buffered and playback can begin.
   *  Drives whether the Play button is enabled. */
  ready: boolean;
  /** Extraction/OCR progress in [0,1] while state is "extracting" (drives the
   *  "Preparing document…" bar). 0 when the backend isn't OCR'ing. */
  extractProgress: number;
  error: string | null;
  start: (file: File, voice: string, speed: number, startPage?: number, startWordIndex?: number) => Promise<void>;
  /** Open an already-uploaded library document by id: fetch its original + words
   *  from the server and start a session, landing paused at the saved position. */
  open: (documentId: string, voice: string, speed: number, startPage?: number, startWordIndex?: number) => Promise<void>;
  /** Tear everything down and return to the idle (upload) state. */
  reset: () => void;
  play: () => void;
  pause: () => void;
  setRate: (rate: number) => void;
  /** Re-synthesize at a new narration voice (Kokoro), resuming at the current word. */
  changeVoice: (voice: string) => Promise<void>;
  /** Re-synthesize at a new speaking pace (Kokoro native speed = natural pitch)
   *  and resume at the current word. */
  changeSpeed: (speed: number) => Promise<void>;
  seekToWord: (timelineIndex: number) => void;
  /** Jump to a source word: seek if it's already synthesized, otherwise cancel
   *  and re-synthesize from there so you don't wait for everything before it. */
  jumpToWord: (sourceWordIndex: number) => void;
  /** Jump to a page (its first word) — same cancel-and-resynthesize behavior. */
  jumpToPage: (page: number) => void;
  /** Position at a source word and preload, staying PAUSED (manual section/page
   *  navigation). The format-agnostic primitive behind jumpToPage and EPUB nav. */
  jumpToSourceWord: (sourceWordIndex: number) => void;
  seekToMs: (ms: number) => void;
  getPositionMs: () => number;
}

export function useReader(): ReaderController {
  const [state, setState] = useState<UiState>("idle");
  const [documentId, setDocumentId] = useState<string | null>(null);
  const [progress, setProgress] = useState(0);
  const [extractProgress, setExtractProgress] = useState(0);
  const [timeline, setTimeline] = useState<Timeline>(EMPTY_TIMELINE);
  const [activeIndex, setActiveIndex] = useState(-1);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const player = useRef<PlaybackEngine | null>(null);
  const queue = useRef(new ChunkQueue<ProcessedChunk>());
  const connection = useRef<ReaderConnection | null>(null);
  const raf = useRef<number | null>(null);
  const wasPlaying = useRef(false);
  // Debounces preloading when manually flipping pages, so rapid Prev/Next doesn't
  // kick off (and immediately cancel) a synthesis for every page passed through.
  const navTimer = useRef<number | null>(null);
  // Identity of the loaded doc + the source-word to resume at after a re-synth.
  const docId = useRef<string | null>(null);
  const voiceRef = useRef("af_heart");
  const speedRef = useRef(1);
  const docWords = useRef<SourceWordData[]>([]);
  const activeIndexRef = useRef(-1);
  const resumeSourceWord = useRef<number | null>(null);
  // Keeps the mobile screen awake while reading (released on pause/reset).
  const wakeLock = useRef(new WakeLockManager());
  // Latest timeline for the rAF highlight loop + re-sync. Declared before `tick`
  // (which reads it); kept current by the effect further down.
  const timelineRef = useRef(timeline);

  const rebuildTimeline = useCallback(() => {
    setTimeline(buildTimeline(queue.current.contiguous()));
  }, []);

  const onChunk = useCallback((chunk: ProcessedChunk) => {
    if (queue.current.has(chunk.chunkIndex)) return; // dedupe backfill vs live
    queue.current.add(chunk);
    // Surface (don't swallow) fetch/decode failures: a silently-rejected ingest
    // leaves the chunk un-buffered and the player stuck "processing" with no
    // audio and no clue why (this is how the float32-WAV bug hid).
    player.current?.ingest(chunk.chunkIndex, chunk.audioUrl, chunk.durationMs).catch((e) => {
      console.error("audio ingest failed for chunk", chunk.chunkIndex, e);
      setError(`Audio decode failed (chunk ${chunk.chunkIndex}): ${e instanceof Error ? e.message : String(e)}`);
    });
    rebuildTimeline();
  }, [rebuildTimeline]);

  // Named function expression `step` so the rAF loop re-schedules itself without
  // referencing the `tick` const inside its own initializer (use-before-declared).
  const tick = useCallback(function step() {
    const ms = player.current?.currentMs() ?? 0;
    setActiveIndex((prev) => {
      const next = activeWordIndex(timelineRef.current.words, ms);
      return next === prev ? prev : next;
    });
    // Keep the lock-screen scrubber tracking playback.
    setMediaPositionState(timelineRef.current.totalMs, ms);
    raf.current = requestAnimationFrame(step);
  }, []);

  // Keep the timeline + active-index refs current for the rAF loop and re-sync.
  useEffect(() => {
    timelineRef.current = timeline;
  }, [timeline]);
  useEffect(() => {
    activeIndexRef.current = activeIndex;
  }, [activeIndex]);

  // Create a session for the already-uploaded doc and wire up streaming. Reuses
  // the existing AudioQueuePlayer (reset, not recreated) so iOS audio stays
  // unlocked across a re-synth.
  const openSession = useCallback(async (voice: string, speed: number, startWordIndex = 0) => {
    if (!docId.current) return;
    voiceRef.current = voice;
    speedRef.current = speed;
    await connection.current?.stop();
    queue.current = new ChunkQueue<ProcessedChunk>();
    setTimeline(EMPTY_TIMELINE);
    setProgress(0);
    setReady(false); // no audio buffered yet for the new stream

    if (player.current) player.current.reset();
    else player.current = createPlaybackEngine();
    player.current.onUnderrun = () => {
      setReady(false); // audio at the current position isn't buffered yet
      setState((s) => (s === "playing" ? "processing" : s));
    };
    player.current.onResumed = () => {
      if (player.current?.continuesInBackground) {
        // Native (lock-screen) resume of the media element: the OS restarted
        // playback, so restore the playing state the preceding onInterrupted
        // (lock-screen pause) cleared, and re-arm the highlight loop.
        wasPlaying.current = true;
        setMediaPlaybackState("playing");
        if (raf.current === null) raf.current = requestAnimationFrame(tick);
        setState("playing");
      } else {
        setState((s) => (wasPlaying.current ? "playing" : s));
      }
    };
    player.current.onReady = () => setReady(true);
    // OS interruption (backgrounding the PWA, an incoming call) already captured
    // the position in a clean pause; reflect paused in the UI and do NOT auto-resume
    // — resuming is an explicit Play tap from exactly where we left off.
    player.current.onInterrupted = () => {
      wasPlaying.current = false;
      setState((s) => (s === "playing" || s === "processing" ? "paused" : s));
    };

    setState("processing");
    const session = await api.createSession(docId.current, voice, speed, "en", startWordIndex);
    // Point the media-element engine at this session's stitched stream (no-op for
    // the Web Audio engine, which receives audio per-chunk via ingest()).
    player.current.setSource(api.streamUrl(session.id));
    connection.current = new ReaderConnection(session.id, {
      onChunk,
      onProgress: (p) => setProgress(p.progress),
      onStatus: (status) => {
        if (status === "Error") setState("error");
      },
      onError: (e) => {
        setError(e.message);
        setState("error");
      },
      onReconnecting: () => setState("reconnecting"),
      onReconnected: () => setState(wasPlaying.current ? "playing" : "processing"),
    });
    await connection.current.start();
  }, [onChunk, tick]);

  const start = useCallback(
    async (file: File, voice: string, speed: number, startPage = 1, startWordIndex?: number) => {
      try {
        setError(null);
        setState("uploading");
        const doc = await api.uploadDocument(file);
        docId.current = doc.id;
        setDocumentId(doc.id);
        voiceRef.current = voice;
        // Extraction runs server-side; a scanned PDF is OCR'd first (can take a few
        // minutes). Wait for Ready before fetching words / opening a session.
        if (doc.status !== "Ready") {
          setState("extracting");
          setExtractProgress(doc.progress ?? 0);
          await api.waitForDocumentReady(doc.id, (d) => setExtractProgress(d.progress ?? 0));
        }
        // Source words (with page) power position-aware jumps without re-fetching.
        docWords.current = await api.getWords(doc.id).catch(() => []);
        // Resume at an exact saved word when given; otherwise the page's first word.
        const startWord = startWordIndex != null
          ? Math.max(0, startWordIndex)
          : startPage > 1
            ? (docWords.current.find((w) => w.page === startPage)?.index ?? 0)
            : 0;
        // Restoring a saved session: land PAUSED at exactly that word (highlighted),
        // never auto-play. The resume effect seeks + highlights once it streams in.
        if (startWordIndex != null) {
          resumeSourceWord.current = startWord;
          wasPlaying.current = false;
        }
        await openSession(voice, speed, startWord);
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
        setState("error");
      }
    },
    [openSession],
  );

  // Open an already-uploaded document by id (from the library). The original is
  // rendered by the caller via GET /…/original; here we just fetch its words and
  // open a session, landing PAUSED at the saved position (highlighted, no auto-play).
  const open = useCallback(
    async (documentId: string, voice: string, speed: number, startPage = 1, startWordIndex?: number) => {
      try {
        setError(null);
        setState("processing");
        docId.current = documentId;
        setDocumentId(documentId);
        voiceRef.current = voice;
        docWords.current = await api.getWords(documentId).catch(() => []);
        const startWord = startWordIndex != null
          ? Math.max(0, startWordIndex)
          : startPage > 1
            ? (docWords.current.find((w) => w.page === startPage)?.index ?? 0)
            : 0;
        resumeSourceWord.current = startWord;
        wasPlaying.current = false;
        await openSession(voice, speed, startWord);
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
        setState("error");
      }
    },
    [openSession],
  );

  // Tear down and return to the upload screen.
  const reset = useCallback(() => {
    void connection.current?.stop();
    connection.current = null;
    player.current?.dispose();
    player.current = null;
    wakeLock.current.release();
    setMediaPlaybackState("none");
    if (raf.current !== null) {
      cancelAnimationFrame(raf.current);
      raf.current = null;
    }
    if (navTimer.current !== null) {
      clearTimeout(navTimer.current);
      navTimer.current = null;
    }
    queue.current = new ChunkQueue<ProcessedChunk>();
    docId.current = null;
    setDocumentId(null);
    docWords.current = [];
    resumeSourceWord.current = null;
    wasPlaying.current = false;
    setTimeline(EMPTY_TIMELINE);
    setActiveIndex(-1);
    setProgress(0);
    setExtractProgress(0);
    setReady(false);
    setError(null);
    setState("idle");
  }, []);

  // When the PWA is backgrounded (tab hidden, app switched, screen locked), pause
  // cleanly so the position is captured and the player's internal `playing` flag
  // is cleared. Without this, iOS interrupts the AudioContext while `playing` stays
  // true: Play becomes a no-op on return and freshly-synthesized chunks auto-start.
  // We stay paused on return — resuming is an explicit Play tap from this spot.
  useEffect(() => {
    const onVisibility = () => {
      if (!document.hidden) {
        // Back in the foreground: re-request the lock the OS dropped while
        // hidden, if we're still actively reading.
        if (state === "playing") void wakeLock.current.reacquire();
        return;
      }
      // The media-element engine keeps playing while backgrounded/locked — that's
      // the whole feature — so don't force a pause. (Genuine OS interruptions still
      // route through the engine's onInterrupted.)
      if (player.current?.continuesInBackground) return;
      if (!wasPlaying.current && state !== "playing") return;
      player.current?.pause();
      wasPlaying.current = false;
      // The OS auto-drops the screen lock while hidden; sync our intent so a
      // later return-to-foreground doesn't think a lock is still held.
      wakeLock.current.release();
      setState((s) => (s === "playing" || s === "processing" ? "paused" : s));
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, [state]);

  const play = useCallback(() => {
    player.current?.play();
    wasPlaying.current = true;
    setState("playing");
    setMediaPlaybackState("playing");
    void wakeLock.current.acquire();
    if (raf.current === null) raf.current = requestAnimationFrame(tick);
  }, [tick]);

  const pause = useCallback(() => {
    player.current?.pause();
    wasPlaying.current = false;
    setMediaPlaybackState("paused");
    wakeLock.current.release();
    // Stop the highlight loop while paused; otherwise it keeps recomputing the
    // active word from the player's (frozen) position and can fight navigation.
    if (raf.current !== null) {
      cancelAnimationFrame(raf.current);
      raf.current = null;
    }
    setState("paused");
  }, []);

  const setRate = useCallback((rate: number) => player.current?.setRate(rate), []);

  // Cancel the current synthesis and re-synthesize FROM a source word at the given
  // speed, resuming playback there once it streams in.
  const restartAt = useCallback(async (sourceWordIndex: number, speed: number) => {
    if (!docId.current) return;
    resumeSourceWord.current = Math.max(0, sourceWordIndex);
    setActiveIndex(-1);
    try {
      await openSession(voiceRef.current, speed, Math.max(0, sourceWordIndex));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setState("error");
    }
  }, [openSession]);

  // Re-synthesize at a new Kokoro speed (natural pitch, unlike the playback-rate
  // trick which shifts pitch), continuing from the current word.
  const changeSpeed = useCallback(async (speed: number) => {
    const current = timelineRef.current.words[activeIndexRef.current];
    await restartAt(current?.wordIndex ?? 0, speed);
  }, [restartAt]);

  // Re-synthesize at a new narration voice, continuing from the current word.
  // restartAt creates the new session with voiceRef.current, so set it first.
  const changeVoice = useCallback(async (voice: string) => {
    voiceRef.current = voice;
    const current = timelineRef.current.words[activeIndexRef.current];
    await restartAt(current?.wordIndex ?? 0, speedRef.current);
  }, [restartAt]);

  // After a re-synth, jump to the word we were on as soon as it streams in.
  useEffect(() => {
    const target = resumeSourceWord.current;
    if (target == null) return;
    const idx = timeline.words.findIndex((w) => w.wordIndex === target);
    if (idx < 0) return;
    resumeSourceWord.current = null;
    const word = timeline.words[idx];
    player.current?.seek(word.globalStartMs, timeline.chunkOffsets);
    setActiveIndex(idx);
    if (wasPlaying.current) play();
    else setState("paused");
  }, [timeline, play]);

  // Seek to a word and START reading from there (used by page/section navigation
  // and click-to-seek), so the voice reads the page you jumped to.
  const seekToWord = useCallback((timelineIndex: number) => {
    const word = timelineRef.current.words[timelineIndex];
    if (!word) return;
    player.current?.seek(word.globalStartMs, timelineRef.current.chunkOffsets);
    setActiveIndex(timelineIndex);
    play();
  }, [play]);

  // Jump to a source word. If it's already synthesized in the current session,
  // seek instantly; otherwise cancel and re-synthesize FROM there so you don't
  // wait for everything before it.
  const jumpToWord = useCallback((sourceWordIndex: number) => {
    const idx = timelineRef.current.words.findIndex((w) => w.wordIndex === sourceWordIndex);
    if (idx >= 0) {
      seekToWord(idx);
    } else {
      wasPlaying.current = true;
      void restartAt(sourceWordIndex, speedRef.current);
    }
  }, [seekToWord, restartAt]);

  // Manual navigation (PDF Prev/Next, EPUB section/chapter changes): position at a
  // source word and PRELOAD its audio, but do NOT auto-play — reading starts on an
  // explicit Play tap. (Playback-driven page turns continue reading; they don't come
  // through here.) This is the shared, format-agnostic navigation primitive: the PDF
  // pager turns a page into its first word, the EPUB reader passes a section's first
  // word, and both land paused — identical behavior across formats.
  const jumpToSourceWord = useCallback((sourceWordIndex: number) => {
    if (navTimer.current !== null) {
      clearTimeout(navTimer.current);
      navTimer.current = null;
    }
    // Stop the highlight loop so it can't recompute the active word from the
    // player's old position and drag the visible page back (blink-then-snap-back).
    if (raf.current !== null) {
      cancelAnimationFrame(raf.current);
      raf.current = null;
    }
    wasPlaying.current = false; // a manual flip is not an intent to play
    const idx = timelineRef.current.words.findIndex((w) => w.wordIndex === sourceWordIndex);
    if (idx >= 0) {
      // Already synthesized — stop any audio, seek + highlight, stay paused.
      const word = timelineRef.current.words[idx];
      player.current?.pause();
      player.current?.seek(word.globalStartMs, timelineRef.current.chunkOffsets);
      setActiveIndex(idx);
      setState((s) => (s === "playing" || s === "processing" ? "paused" : s));
    } else {
      // Not synthesized yet: disable Play and preload from here after a short settle
      // delay (debounced above) so flipping through sections/pages stays cheap.
      setReady(false);
      setActiveIndex(-1);
      setState("processing");
      navTimer.current = window.setTimeout(() => {
        navTimer.current = null;
        void restartAt(sourceWordIndex, speedRef.current);
      }, 500);
    }
  }, [restartAt]);

  const jumpToPage = useCallback((page: number) => {
    const first = docWords.current.find((w) => w.page === page);
    if (!first) return;
    jumpToSourceWord(first.index);
  }, [jumpToSourceWord]);

  const seekToMs = useCallback((ms: number) => {
    player.current?.seek(ms, timelineRef.current.chunkOffsets);
    setActiveIndex(activeWordIndex(timelineRef.current.words, ms));
  }, []);

  const getPositionMs = useCallback(() => player.current?.currentMs() ?? 0, []);

  // Wire lock-screen / Control Center transport controls to the reader. Handlers
  // are stable useCallbacks, so this registers once. Next/Prev map to page turns.
  useEffect(() => {
    // On iOS, leave play/pause to native media-element control so a lock-screen
    // resume restarts the <audio> element itself (audible in the background); a
    // JS play() there advances time but stays silent until foreground. Elsewhere
    // (Web Audio engine, no background element) we must drive them ourselves.
    const native = usesNativeMediaTransport();
    setMediaHandlers({
      play: native ? undefined : play,
      pause: native ? undefined : pause,
      seekTo: (ms) => seekToMs(ms),
      seekBy: (delta) => seekToMs(Math.max(0, getPositionMs() + delta)),
      nextTrack: () => {
        const page = timelineRef.current.words[activeIndexRef.current]?.page;
        if (page != null) jumpToPage(page + 1);
      },
      prevTrack: () => {
        const page = timelineRef.current.words[activeIndexRef.current]?.page;
        if (page != null && page > 1) jumpToPage(page - 1);
      },
    });
  }, [play, pause, seekToMs, getPositionMs, jumpToPage]);

  useEffect(() => {
    const wl = wakeLock.current; // stable instance; capture for the cleanup closure
    return () => {
      if (raf.current !== null) cancelAnimationFrame(raf.current);
      if (navTimer.current !== null) clearTimeout(navTimer.current);
      void connection.current?.stop();
      player.current?.dispose();
      wl.release();
      clearMediaSession();
    };
  }, []);

  return {
    state, documentId, progress, extractProgress, timeline, activeIndex, ready, error,
    start, open, reset, play, pause, setRate, changeSpeed, changeVoice,
    seekToWord, jumpToWord, jumpToPage, jumpToSourceWord, seekToMs, getPositionMs,
  };
}
