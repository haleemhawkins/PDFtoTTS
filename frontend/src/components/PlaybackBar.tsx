import type { UiState } from "../hooks/useReader";

interface Props {
  state: UiState;
  progress: number;
  speed: number;
  onHome: () => void;
  onPlay: () => void;
  onPause: () => void;
  onSpeed: (speed: number) => void;
}

const SPEEDS = [0.75, 1, 1.25, 1.5, 2];

export function PlaybackBar({ state, progress, speed, onHome, onPlay, onPause, onSpeed }: Props) {
  const isPlaying = state === "playing";
  const isProcessing = state === "processing";
  const canPlay = state !== "idle" && state !== "uploading" && state !== "error";
  const pct = Math.round(progress * 100);

  // While processing, the player is waiting for the audio at the current position
  // to finish synthesizing; it auto-starts when ready. Make that legible instead
  // of looking like nothing happened.
  const label = isProcessing
    ? `Synthesizing… ${pct}% — playback starts automatically`
    : `${pct}% synthesized`;

  return (
    <div className="playback-bar">
      <button className="home-btn" onClick={onHome} title="Back to upload">
        🏠 Home
      </button>

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

      <div className={"progress" + (isProcessing ? " processing" : "")} title={label}>
        <div className="progress-fill" style={{ width: `${pct}%` }} />
      </div>

      <span className="progress-label">{label}</span>

      <span className={`state-chip state-${state}`}>{state}</span>
    </div>
  );
}
