import { describe, expect, it } from "vitest";
import { ChunkQueue } from "./chunkQueue";

interface C {
  chunkIndex: number;
}

describe("ChunkQueue", () => {
  it("hands chunks back in order despite out-of-order arrival", () => {
    const q = new ChunkQueue<C>();
    q.add({ chunkIndex: 2 });
    q.add({ chunkIndex: 0 });
    q.add({ chunkIndex: 1 });

    expect(q.next()?.chunkIndex).toBe(0);
    expect(q.next()?.chunkIndex).toBe(1);
    expect(q.next()?.chunkIndex).toBe(2);
    expect(q.next()).toBeUndefined();
  });

  it("reports an underrun when the next chunk has not arrived", () => {
    const q = new ChunkQueue<C>();
    q.add({ chunkIndex: 0 });
    q.add({ chunkIndex: 2 }); // gap at 1

    expect(q.next()?.chunkIndex).toBe(0);
    expect(q.ready).toBe(false); // chunk 1 missing
    expect(q.next()).toBeUndefined();

    q.add({ chunkIndex: 1 }); // gap filled
    expect(q.ready).toBe(true);
    expect(q.next()?.chunkIndex).toBe(1);
    expect(q.next()?.chunkIndex).toBe(2);
  });

  it("contiguous() returns only the gapless prefix", () => {
    const q = new ChunkQueue<C>();
    q.add({ chunkIndex: 0 });
    q.add({ chunkIndex: 1 });
    q.add({ chunkIndex: 3 }); // 2 missing
    expect(q.contiguous().map((c) => c.chunkIndex)).toEqual([0, 1]);
  });

  it("seekTo rewinds the cursor", () => {
    const q = new ChunkQueue<C>();
    [0, 1, 2].forEach((i) => q.add({ chunkIndex: i }));
    q.next();
    q.next();
    q.seekTo(0);
    expect(q.next()?.chunkIndex).toBe(0);
  });
});
