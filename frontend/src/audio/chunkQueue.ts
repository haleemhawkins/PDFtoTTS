/**
 * Ordered chunk buffer for streamed audio (design §6.4). Chunks may arrive out
 * of order over SignalR; this hands them back strictly in `chunkIndex` order and
 * reports an underrun (gap at the cursor) so playback can pause rather than skip.
 */
export class ChunkQueue<T extends { chunkIndex: number }> {
  private readonly byIndex = new Map<number, T>();
  private cursor = 0;

  /** Buffer a received chunk (idempotent on chunkIndex). */
  add(chunk: T): void {
    this.byIndex.set(chunk.chunkIndex, chunk);
  }

  has(index: number): boolean {
    return this.byIndex.has(index);
  }

  /** Is the next-to-play chunk available? */
  get ready(): boolean {
    return this.byIndex.has(this.cursor);
  }

  /** Position of the next chunk to play. */
  get position(): number {
    return this.cursor;
  }

  /**
   * Take the next chunk in order and advance, or undefined if it hasn't arrived
   * yet (an underrun — the caller should pause and retry on the next arrival).
   */
  next(): T | undefined {
    const chunk = this.byIndex.get(this.cursor);
    if (chunk === undefined) return undefined;
    this.cursor += 1;
    return chunk;
  }

  /** All chunks contiguously available from index 0 (for timeline building). */
  contiguous(): T[] {
    const out: T[] = [];
    let i = 0;
    let chunk = this.byIndex.get(i);
    while (chunk !== undefined) {
      out.push(chunk);
      i += 1;
      chunk = this.byIndex.get(i);
    }
    return out;
  }

  /** Rewind the play cursor (e.g. after a seek to an earlier chunk). */
  seekTo(index: number): void {
    this.cursor = Math.max(0, index);
  }
}
