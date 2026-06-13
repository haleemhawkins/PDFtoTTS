import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "../api/client";
import { AudioQueuePlayer } from "../audio/audioPlayer";
import { ChunkQueue } from "../audio/chunkQueue";
import type { ProcessedChunk, SourceWordData } from "../api/types";
import { ReaderConnection } from "../signalr/readerConnection";
import { activeWordIndex, buildTimeline, type Timeline } from "../sync/wordTimeline";

export type UiState =
  | "idle" | "uploading" | "processing" | "playing" | "paused" | "error" | "reconnecting";

const EMPTY_TIMELINE: Timeline = { words: [], chunkOffsets: new Map(), totalMs: 0 };

export interface ReaderController {
  state: UiState;
  progress: number;
  timeline: Timeline;
  activeIndex: number; // index into timeline.words, or -1
  error: string | null;
  start: (file: File, voice: string, speed: number) => Promise<void>;
  play: () => void;
  pause: () => void;
  setRate: (rate: number) => void;
  /** Re-synthesize at a new speaking pace (Kokoro native speed = natural pitch)
   *  and resume at the current word. */
  changeSpeed: (speed: number) => Promise<void>;
  seekToWord: (timelineIndex: number) => void;
  /** Jump to a source word: seek if it's already synthesized, otherwise cancel
   *  and re-synthesize from there so you don't wait for everything before it. */
  jumpToWord: (sourceWordIndex: number) => void;
  /** Jump to a page (its first word) — same cancel-and-resynthesize behavior. */
  jumpToPage: (page: number) => void;
  seekToMs: (ms: number) => void;
  getPositionMs: () => number;
}

export function useReader(): ReaderController {
  const [state, setState] = useState<UiState>("idle");
  const [progress, setProgress] = useState(0);
  const [timeline, setTimeline] = useState<Timeline>(EMPTY_TIMELINE);
  const [activeIndex, setActiveIndex] = useState(-1);
  const [error, setError] = useState<string | null>(null);

  const player = useRef<AudioQueuePlayer | null>(null);
  const queue = useRef(new ChunkQueue<ProcessedChunk>());
  const connection = useRef<ReaderConnection | null>(null);
  const raf = useRef<number | null>(null);
  const wasPlaying = useRef(false);
  // Identity of the loaded doc + the source-word to resume at after a re-synth.
  const docId = useRef<string | null>(null);
  const voiceRef = useRef("af_heart");
  const speedRef = useRef(1);
  const docWords = useRef<SourceWordData[]>([]);
  const activeIndexRef = useRef(-1);
  const resumeSourceWord = useRef<number | null>(null);

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

  const tick = useCallback(() => {
    const ms = player.current?.currentMs() ?? 0;
    setActiveIndex((prev) => {
      const next = activeWordIndex(timelineRef.current.words, ms);
      return next === prev ? prev : next;
    });
    raf.current = requestAnimationFrame(tick);
  }, []);

  // Keep refs of the latest timeline + active index for the rAF loop and re-sync.
  const timelineRef = useRef(timeline);
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

    if (player.current) player.current.reset();
    else player.current = new AudioQueuePlayer();
    player.current.onUnderrun = () => setState((s) => (s === "playing" ? "processing" : s));
    player.current.onResumed = () => setState((s) => (wasPlaying.current ? "playing" : s));

    setState("processing");
    const session = await api.createSession(docId.current, voice, speed, "en", startWordIndex);
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
  }, [onChunk]);

  const start = useCallback(async (file: File, voice: string, speed: number) => {
    try {
      setError(null);
      setState("uploading");
      const doc = await api.uploadDocument(file);
      docId.current = doc.id;
      voiceRef.current = voice;
      // Source words (with page) power position-aware jumps without re-fetching.
      docWords.current = await api.getWords(doc.id).catch(() => []);
      await openSession(voice, speed);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setState("error");
    }
  }, [openSession]);

  const play = useCallback(() => {
    player.current?.play();
    wasPlaying.current = true;
    setState("playing");
    if (raf.current === null) raf.current = requestAnimationFrame(tick);
  }, [tick]);

  const pause = useCallback(() => {
    player.current?.pause();
    wasPlaying.current = false;
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

  const jumpToPage = useCallback((page: number) => {
    const first = docWords.current.find((w) => w.page === page);
    if (first) jumpToWord(first.index);
  }, [jumpToWord]);

  const seekToMs = useCallback((ms: number) => {
    player.current?.seek(ms, timelineRef.current.chunkOffsets);
    setActiveIndex(activeWordIndex(timelineRef.current.words, ms));
  }, []);

  const getPositionMs = useCallback(() => player.current?.currentMs() ?? 0, []);

  useEffect(() => {
    return () => {
      if (raf.current !== null) cancelAnimationFrame(raf.current);
      void connection.current?.stop();
      player.current?.dispose();
    };
  }, []);

  return {
    state, progress, timeline, activeIndex, error,
    start, play, pause, setRate, changeSpeed,
    seekToWord, jumpToWord, jumpToPage, seekToMs, getPositionMs,
  };
}
