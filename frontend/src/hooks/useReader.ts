import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "../api/client";
import { AudioQueuePlayer } from "../audio/audioPlayer";
import { ChunkQueue } from "../audio/chunkQueue";
import type { ProcessedChunk } from "../api/types";
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
  seekToWord: (timelineIndex: number) => void;
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

  const rebuildTimeline = useCallback(() => {
    setTimeline(buildTimeline(queue.current.contiguous()));
  }, []);

  const onChunk = useCallback((chunk: ProcessedChunk) => {
    if (queue.current.has(chunk.chunkIndex)) return; // dedupe backfill vs live
    queue.current.add(chunk);
    void player.current?.ingest(chunk.chunkIndex, chunk.audioUrl, chunk.durationMs);
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

  // Keep a ref of the latest timeline for the rAF loop.
  const timelineRef = useRef(timeline);
  useEffect(() => {
    timelineRef.current = timeline;
  }, [timeline]);

  const start = useCallback(async (file: File, voice: string, speed: number) => {
    try {
      setError(null);
      setState("uploading");
      const doc = await api.uploadDocument(file);
      const session = await api.createSession(doc.id, voice, speed);
      setState("processing");

      player.current = new AudioQueuePlayer();
      player.current.onUnderrun = () => setState((s) => (s === "playing" ? "processing" : s));
      player.current.onResumed = () => setState((s) => (wasPlaying.current ? "playing" : s));

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
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setState("error");
    }
  }, [onChunk]);

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

  // Seek to a word and START reading from there (used by page/section navigation
  // and click-to-seek), so the voice reads the page you jumped to.
  const seekToWord = useCallback((timelineIndex: number) => {
    const word = timelineRef.current.words[timelineIndex];
    if (!word) return;
    player.current?.seek(word.globalStartMs, timelineRef.current.chunkOffsets);
    setActiveIndex(timelineIndex);
    play();
  }, [play]);

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
    start, play, pause, setRate, seekToWord, seekToMs, getPositionMs,
  };
}
