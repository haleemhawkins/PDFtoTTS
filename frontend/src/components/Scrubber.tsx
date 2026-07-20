import { useEffect, useRef, useState } from "react";

interface Props {
  getPositionMs: () => number;
  totalMs: number;
  onSeek: (ms: number) => void;
}

function fmt(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(s / 60);
  return `${m}:${String(s % 60).padStart(2, "0")}`;
}

/**
 * Document-wide seek bar. Polls the playback position on its own interval so the
 * frequent updates stay isolated to this component (the document view doesn't
 * re-render). The bar spans the processed audio (which grows as chunks stream),
 * so you can seek anywhere already available.
 */
export function Scrubber({ getPositionMs, totalMs, onSeek }: Props) {
  const [pos, setPos] = useState(0);
  const dragging = useRef(false);

  useEffect(() => {
    const id = window.setInterval(() => {
      if (!dragging.current) setPos(getPositionMs());
    }, 200);
    return () => window.clearInterval(id);
  }, [getPositionMs]);

  const max = Math.max(totalMs, pos, 1);

  return (
    <div className="scrubber">
      <span className="time">{fmt(pos)}</span>
      <input
        type="range"
        min={0}
        max={max}
        step={100}
        value={Math.min(pos, max)}
        onMouseDown={() => (dragging.current = true)}
        onTouchStart={() => (dragging.current = true)}
        onChange={(e) => {
          const v = Number(e.target.value);
          setPos(v);
          if (!dragging.current) onSeek(v); // keyboard / track click
        }}
        onMouseUp={(e) => {
          dragging.current = false;
          onSeek(Number((e.target as HTMLInputElement).value));
        }}
        onTouchEnd={(e) => {
          dragging.current = false;
          onSeek(Number((e.target as HTMLInputElement).value));
        }}
      />
      <span className="time">{fmt(totalMs)}</span>
    </div>
  );
}
