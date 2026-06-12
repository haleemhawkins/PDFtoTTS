/**
 * Streamed audio playback via Web Audio (design §6.4/§6.5). Chunks are decoded
 * and played in order; the global playback position (in unscaled audio ms) is
 * exposed for the sync engine. On an underrun (next chunk not yet decoded) it
 * pauses and resumes automatically when the chunk arrives — it never skips.
 */
export class AudioQueuePlayer {
  private readonly ctx: AudioContext;
  private readonly buffers = new Map<number, AudioBuffer>();
  private readonly durationsMs = new Map<number, number>();

  private playing = false;
  private rate = 1;
  private cursor = 0; // next chunk index to play
  private currentChunk = -1;
  private source: AudioBufferSourceNode | null = null;
  private chunkStartCtxTime = 0;
  private chunkStartOffsetMs = 0; // offset into the current chunk we started at
  private playedBeforeMs = 0; // global ms of fully-played chunks

  onUnderrun?: () => void;
  onResumed?: () => void;

  constructor() {
    this.ctx = new AudioContext();
  }

  /** Decode and buffer a chunk; recover from an underrun if we were waiting on it. */
  async ingest(chunkIndex: number, url: string, durationMs: number): Promise<void> {
    this.durationsMs.set(chunkIndex, durationMs);
    const data = await fetch(url).then((r) => r.arrayBuffer());
    this.buffers.set(chunkIndex, await this.ctx.decodeAudioData(data));
    if (this.playing && this.source === null && chunkIndex === this.cursor) {
      this.onResumed?.();
      this.startCurrent(0);
    }
  }

  play(): void {
    if (this.playing) return;
    this.playing = true;
    void this.ctx.resume();
    if (this.source === null) this.startCurrent(0);
  }

  pause(): void {
    if (!this.playing) return;
    // Capture position, tear down the source so resume restarts cleanly.
    const within = this.currentWithinMs();
    this.playedBeforeMs = this.cumulativeOffsetMs(this.currentChunk);
    this.chunkStartOffsetMs = within;
    this.stopSource();
    this.playing = false;
  }

  setRate(rate: number): void {
    this.rate = rate;
    if (this.source) this.source.playbackRate.value = rate;
  }

  /** Current global position in unscaled audio milliseconds. */
  currentMs(): number {
    if (this.currentChunk < 0) return this.playedBeforeMs;
    return this.cumulativeOffsetMs(this.currentChunk) + this.currentWithinMs();
  }

  /** Seek to a global position; needs the per-chunk offsets from the timeline. */
  seek(globalMs: number, chunkOffsets: Map<number, number>): void {
    let target = 0;
    let offset = 0;
    for (const [index, start] of [...chunkOffsets.entries()].sort((a, b) => a[0] - b[0])) {
      if (start <= globalMs) {
        target = index;
        offset = start;
      }
    }
    this.stopSource();
    this.cursor = target;
    this.playedBeforeMs = offset;
    const within = globalMs - offset;
    if (this.playing) this.startCurrent(within);
    else this.chunkStartOffsetMs = within;
  }

  dispose(): void {
    this.stopSource();
    void this.ctx.close();
  }

  // --- internals ----------------------------------------------------------

  private startCurrent(withinMs: number): void {
    const buffer = this.buffers.get(this.cursor);
    if (!buffer) {
      this.source = null;
      this.currentChunk = -1;
      this.onUnderrun?.();
      return;
    }

    const src = this.ctx.createBufferSource();
    src.buffer = buffer;
    src.playbackRate.value = this.rate;
    src.connect(this.ctx.destination);
    src.onended = () => {
      if (src !== this.source) return; // superseded by seek/pause
      this.playedBeforeMs = this.cumulativeOffsetMs(this.cursor) +
        (this.durationsMs.get(this.cursor) ?? buffer.duration * 1000);
      this.cursor += 1;
      this.source = null;
      this.currentChunk = -1;
      if (this.playing) this.startCurrent(0);
    };

    this.currentChunk = this.cursor;
    this.chunkStartCtxTime = this.ctx.currentTime;
    this.chunkStartOffsetMs = withinMs;
    src.start(0, withinMs / 1000);
    this.source = src;
  }

  private stopSource(): void {
    if (this.source) {
      this.source.onended = null;
      try {
        this.source.stop();
      } catch {
        /* not started */
      }
      this.source = null;
    }
  }

  private currentWithinMs(): number {
    if (!this.source) return this.chunkStartOffsetMs;
    const elapsed = (this.ctx.currentTime - this.chunkStartCtxTime) * 1000 * this.rate;
    return this.chunkStartOffsetMs + elapsed;
  }

  private cumulativeOffsetMs(chunkIndex: number): number {
    let total = 0;
    for (let i = 0; i < chunkIndex; i++) total += this.durationsMs.get(i) ?? 0;
    return total;
  }
}
