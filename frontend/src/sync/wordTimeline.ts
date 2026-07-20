import type { BoundingBox, ProcessedChunk } from "../api/types";

/**
 * A word placed on a continuous global timeline across all played chunks
 * (design §6.5). Chunk-local times are offset by the cumulative duration of
 * preceding chunks so playback time maps directly to a word.
 */
export interface TimelineWord {
  chunkIndex: number;
  wordIndex: number; // source document word index
  text: string;
  globalStartMs: number;
  globalEndMs: number;
  page: number | null;
  bbox: BoundingBox | null;
}

export interface Timeline {
  words: TimelineWord[]; // sorted by globalStartMs
  /** Global start time (ms) of each chunk, indexed by chunkIndex. */
  chunkOffsets: Map<number, number>;
  totalMs: number;
}

/**
 * Flatten contiguous chunks (assumed starting at chunkIndex 0) into a single
 * timeline. Chunks are sorted defensively; each word's window is shifted by its
 * chunk's global offset.
 */
export function buildTimeline(chunks: ProcessedChunk[]): Timeline {
  const ordered = [...chunks].sort((a, b) => a.chunkIndex - b.chunkIndex);
  const words: TimelineWord[] = [];
  const chunkOffsets = new Map<number, number>();
  let offset = 0;

  for (const chunk of ordered) {
    chunkOffsets.set(chunk.chunkIndex, offset);
    for (const w of chunk.words) {
      words.push({
        chunkIndex: chunk.chunkIndex,
        wordIndex: w.index,
        text: w.text,
        globalStartMs: offset + w.startMs,
        globalEndMs: offset + w.endMs,
        page: w.page,
        bbox: w.bbox,
      });
    }
    offset += chunk.durationMs;
  }

  return { words, chunkOffsets, totalMs: offset };
}

/**
 * Index of the active word at time `tMs` via binary search: the last word whose
 * start is at or before `tMs`. Returns -1 before the first word. On a gap
 * (between a word's end and the next word's start) the previous word is held,
 * which keeps highlighting from flickering (design §6.5).
 */
export function activeWordIndex(words: TimelineWord[], tMs: number): number {
  let lo = 0;
  let hi = words.length - 1;
  let ans = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (words[mid].globalStartMs <= tMs) {
      ans = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return ans;
}

/** Find the word nearest a click (for click-to-seek): exact hit, else nearest start. */
export function wordAtSourceIndex(words: TimelineWord[], sourceIndex: number): TimelineWord | undefined {
  return words.find((w) => w.wordIndex === sourceIndex);
}
