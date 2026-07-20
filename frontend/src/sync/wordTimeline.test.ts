import { describe, expect, it } from "vitest";
import type { ProcessedChunk } from "../api/types";
import { activeWordIndex, buildTimeline } from "./wordTimeline";

function chunk(index: number, durationMs: number, words: [string, number, number][]): ProcessedChunk {
  return {
    chunkIndex: index,
    audioUrl: `/audio/${index}`,
    durationMs,
    words: words.map(([text, startMs, endMs], i) => ({
      index: index * 100 + i,
      text,
      startMs,
      endMs,
      page: 1,
      bbox: { x: 0, y: 0, width: 5, height: 5 },
    })),
  };
}

describe("buildTimeline", () => {
  it("offsets each chunk by the cumulative duration of earlier chunks", () => {
    const t = buildTimeline([
      chunk(0, 1000, [["a", 0, 500], ["b", 500, 1000]]),
      chunk(1, 800, [["c", 0, 400], ["d", 400, 800]]),
    ]);

    expect(t.words.map((w) => [w.text, w.globalStartMs, w.globalEndMs])).toEqual([
      ["a", 0, 500],
      ["b", 500, 1000],
      ["c", 1000, 1400], // offset by chunk 0's 1000ms duration
      ["d", 1400, 1800],
    ]);
    expect(t.totalMs).toBe(1800);
    expect(t.chunkOffsets.get(1)).toBe(1000);
  });

  it("sorts chunks defensively before flattening", () => {
    const t = buildTimeline([
      chunk(1, 500, [["c", 0, 500]]),
      chunk(0, 500, [["a", 0, 500]]),
    ]);
    expect(t.words.map((w) => w.text)).toEqual(["a", "c"]);
  });
});

describe("activeWordIndex", () => {
  const t = buildTimeline([chunk(0, 1500, [["a", 0, 500], ["b", 500, 1000], ["c", 1000, 1500]])]);

  it("returns -1 before the first word", () => {
    // first word starts at 0, so a negative time is before it
    expect(activeWordIndex(t.words, -1)).toBe(-1);
  });

  it("selects the word whose window contains the time", () => {
    expect(activeWordIndex(t.words, 600)).toBe(1); // within b
    expect(activeWordIndex(t.words, 1000)).toBe(2); // start of c (inclusive)
  });

  it("holds the previous word during a gap", () => {
    const gapped = buildTimeline([chunk(0, 2000, [["a", 0, 400], ["b", 1000, 1400]])]);
    // 700ms is after a ends (400) but before b starts (1000) → hold a
    expect(activeWordIndex(gapped.words, 700)).toBe(0);
  });

  it("holds the last word past the end", () => {
    expect(activeWordIndex(t.words, 99999)).toBe(2);
  });
});
