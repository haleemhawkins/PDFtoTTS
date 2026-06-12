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
    // iOS silences the Web Audio API when the device is in Ring/Silent mode (even
    // though <video> still plays — different audio category), so the reader is
    // mute-switched off with no error. The Audio Session API (Safari 16.4+) lets
    // us opt into the "playback" category so audio plays regardless, like video.
    const nav = navigator as Navigator & { audioSession?: { type: string } };
    if (nav.audioSession) nav.audioSession.type = "playback";
  }

  /** Decode and buffer a chunk; recover from an underrun if we were waiting on it. */
  async ingest(chunkIndex: number, url: string, durationMs: number): Promise<void> {
    this.durationsMs.set(chunkIndex, durationMs);
    const data = await fetch(url).then((r) => r.arrayBuffer());
    this.buffers.set(chunkIndex, await this.decode(data));
    if (this.playing && this.source === null && chunkIndex === this.cursor) {
      this.onResumed?.();
      this.startCurrent(this.chunkStartOffsetMs);
    }
  }

  /**
   * Decode using the CALLBACK form of decodeAudioData. iOS/older Safari do not
   * support the promise-returning overload — `await ctx.decodeAudioData(data)`
   * resolves to undefined there, so chunks never buffer and the player underruns
   * forever (UI stuck on "processing", no audio). The callback form works across
   * all browsers; a decode failure rejects so callers can surface it.
   */
  private decode(data: ArrayBuffer): Promise<AudioBuffer> {
    return new Promise<AudioBuffer>((resolve, reject) =>
      this.ctx.decodeAudioData(data, resolve, reject),
    );
  }

  play(): void {
    if (this.playing) return;
    this.playing = true;
    // iOS starts the context suspended; resume() must run inside the click
    // gesture (it does — play() is called from the Play button handler).
    void this.ctx.resume();
    // Resume from where we paused/seeked to — NOT the start of the chunk.
    if (this.source === null) this.startCurrent(this.chunkStartOffsetMs);
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
    this.currentChunk = -1;
    // Always record the within-chunk offset so play()/underrun recovery resume
    // exactly at the seeked word, even if the target chunk isn't decoded yet.
    this.chunkStartOffsetMs = globalMs - offset;
    if (this.playing) this.startCurrent(this.chunkStartOffsetMs);
  }

  dispose(): void {
    this.stopSource();
    void this.ctx.close();
  }

  // --- internals ----------------------------------------------------------

  private startCurrent(withinMs: number): void {
    // Record the intended start offset up front so underrun recovery (ingest)
    // resumes this chunk at the right place rather than a stale offset.
    this.chunkStartOffsetMs = withinMs;
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
