import type { UiState } from "../hooks/useReader";

interface Props {
  state: UiState;
  progress: number;
  speed: number;
  onPlay: () => void;
  onPause: () => void;
  onSpeed: (speed: number) => void;
}

const SPEEDS = [0.75, 1, 1.25, 1.5, 2];

export function PlaybackBar({ state, progress, speed, onPlay, onPause, onSpeed }: Props) {
  const isPlaying = state === "playing";
  const canPlay = state !== "idle" && state !== "uploading" && state !== "error";

  return (
    <div className="playback-bar">
      <button onClick={isPlaying ? onPause : onPlay} disabled={!canPlay}>
        {isPlaying ? "⏸ Pause" : "▶ Play"}
      </button>

      <label>
        Speed
        <select value={speed} onChange={(e) => onSpeed(Number(e.target.value))}>
          {SPEEDS.map((s) => (
            <option key={s} value={s}>
              {s}×
            </option>
          ))}
        </select>
      </label>

      <div className="progress" title={`${Math.round(progress * 100)}% processed`}>
        <div className="progress-fill" style={{ width: `${Math.round(progress * 100)}%` }} />
      </div>

      <span className="state-chip">{state}</span>
    </div>
  );
}
