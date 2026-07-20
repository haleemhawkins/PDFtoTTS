import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AudioQueuePlayer } from "./audioPlayer";

/**
 * Web Audio isn't available in jsdom, so we stub a minimal, clock-controllable
 * AudioContext. `clock.now` is the context time (seconds); advancing it
 * simulates playback elapsing. These tests pin the seek/resume position math —
 * the bug where play() restarted the current chunk at offset 0 and discarded
 * the seeked word position.
 */
const clock = { now: 0 };

class FakeBufferSource {
  buffer: { duration: number } | null = null;
  playbackRate = { value: 1 };
  onended: (() => void) | null = null;
  startedAtOffset = -1;
  connect(): void {}
  start(_when: number, offset = 0): void {
    this.startedAtOffset = offset;
  }
  stop(): void {}
}

class FakeAudioContext {
  destination = {};
  get currentTime(): number {
    return clock.now;
  }
  resume(): Promise<void> {
    return Promise.resolve();
  }
  close(): Promise<void> {
    return Promise.resolve();
  }
  createBufferSource(): FakeBufferSource {
    return new FakeBufferSource();
  }
  // The player uses the CALLBACK form (iOS/Safari compat), so the fake must
  // invoke the success callback. Duration is irrelevant; the player trusts the
  // per-chunk durationMs from ingest().
  decodeAudioData(
    _data: ArrayBuffer,
    success?: (b: { duration: number }) => void,
  ): Promise<{ duration: number }> | void {
    const buf = { duration: 1 };
    if (success) return void success(buf);
    return Promise.resolve(buf);
  }
}

beforeEach(() => {
  clock.now = 0;
  // @ts-expect-error — install fakes on the test global.
  globalThis.AudioContext = FakeAudioContext;
  globalThis.fetch = vi.fn(async () => ({ arrayBuffer: async () => new ArrayBuffer(8) })) as never;
});

afterEach(() => {
  vi.restoreAllMocks();
});

// Three chunks: [0..1000), [1000..2000), [2000..4000) ms on the global timeline.
const DURATIONS = [1000, 1000, 2000];
const OFFSETS = new Map<number, number>([
  [0, 0],
  [1, 1000],
  [2, 2000],
]);

async function newPlayerWithChunks(indices = [0, 1, 2]): Promise<AudioQueuePlayer> {
  const p = new AudioQueuePlayer();
  for (const i of indices) await p.ingest(i, `chunk-${i}.wav`, DURATIONS[i]);
  return p;
}

describe("AudioQueuePlayer seek", () => {
  it("starts playback at the seeked word, not the start of its chunk", async () => {
    const p = await newPlayerWithChunks();

    // Seek to 2500ms — 500ms INTO chunk 2 (chunk 2 starts at 2000ms).
    p.seek(2500, OFFSETS);
    p.play();

    // With no time elapsed, position must equal the seek target exactly.
    expect(p.currentMs()).toBeCloseTo(2500, 5);

    // And it must keep advancing from there, not from the chunk boundary.
    clock.now = 0.4; // 400ms elapsed
    expect(p.currentMs()).toBeCloseTo(2900, 5);
  });

  it("resumes from the paused position rather than restarting the chunk", async () => {
    const p = await newPlayerWithChunks();
    p.play(); // starts chunk 0 at 0
    clock.now = 0.6; // 600ms into chunk 0
    p.pause();
    expect(p.currentMs()).toBeCloseTo(600, 5);

    clock.now = 5; // wall-clock moves on while paused
    p.play();
    // Resume must pick up at 600ms, not 0.
    expect(p.currentMs()).toBeCloseTo(600, 5);
  });

  it("resumes at the seeked offset even if the target chunk decodes later", async () => {
    // Only chunks 0 and 1 are loaded; chunk 2 arrives after the seek (underrun).
    const p = await newPlayerWithChunks([0, 1]);

    p.seek(2500, OFFSETS); // into not-yet-decoded chunk 2
    p.play(); // underruns — no buffer for chunk 2 yet

    await p.ingest(2, "chunk-2.wav", DURATIONS[2]); // recovery fires here
    expect(p.currentMs()).toBeCloseTo(2500, 5);
  });
});
