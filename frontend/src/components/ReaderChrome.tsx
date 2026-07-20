import type { UiState } from "../hooks/useReader";
import type { Voice } from "../api/types";
import { Scrubber } from "./Scrubber";

interface Props {
  state: UiState;
  ready: boolean;
  progress: number;
  speed: number;
  totalMs: number;
  showMenu: boolean;
  voices: Voice[];
  voice: string;
  getPositionMs: () => number;
  onMenu: () => void;
  onHome: () => void;
  onPlay: () => void;
  onPause: () => void;
  onSpeed: (speed: number) => void;
  onVoice: (voice: string) => void;
  onSeek: (ms: number) => void;
}

const SPEEDS = [0.75, 1, 1.25, 1.5, 2];

/**
 * One compact control strip (replaces the separate playback bar + scrubber):
 * menu · play/pause · speed · seek · home, with a slim synthesis line. The Play
 * button doubles as the loading/ready indicator — it shows a spinner while the
 * current position is still synthesizing, then pulses "ready" so a single tap
 * starts read-out.
 */
export function ReaderChrome({
  state, ready, progress, speed, totalMs, showMenu, voices, voice,
  getPositionMs, onMenu, onHome, onPlay, onPause, onSpeed, onVoice, onSeek,
}: Props) {
  const isPlaying = state === "playing";
  const pct = Math.round(progress * 100);
  const synthesizing = state === "processing" || state === "uploading" || state === "reconnecting";
  // Until audio at the current position is buffered there's nothing to play.
  const playable = ready && state !== "idle" && state !== "uploading" && state !== "error";

  const status = !ready
    ? (state === "uploading" ? "Uploading…" : `Loading audio… ${pct}%`)
    : isPlaying
      ? (pct < 100 ? `Playing • synthesizing ${pct}%` : "Playing")
      : (pct < 100 ? `Ready — tap play • synthesizing ${pct}%` : "Ready — tap play");

  return (
    <div className="reader-chrome">
      <div className="chrome-row">
        {showMenu && (
          <button className="icon-btn" onClick={onMenu} title="Contents" aria-label="Contents">
            ☰
          </button>
        )}

        <button
          className={"play-btn" + (playable && !isPlaying ? " ready" : "") + (!ready ? " loading" : "")}
          onClick={isPlaying ? onPause : onPlay}
          disabled={!playable && !isPlaying}
          title={status}
        >
          {isPlaying ? "⏸" : !ready ? <span className="spinner" aria-hidden /> : "▶"}
        </button>

        <label className="speed-select">
          <select value={speed} onChange={(e) => onSpeed(Number(e.target.value))}>
            {SPEEDS.map((s) => (
              <option key={s} value={s}>{s}×</option>
            ))}
          </select>
        </label>

        {voices.length > 0 && (
          <label className="voice-select" title="Narration voice">
            <select value={voice} onChange={(e) => onVoice(e.target.value)}>
              {voices.map((v) => (
                <option key={v.id} value={v.id}>{v.label}</option>
              ))}
            </select>
          </label>
        )}

        <Scrubber getPositionMs={getPositionMs} totalMs={totalMs} onSeek={onSeek} />

        <button className="icon-btn" onClick={onHome} title="Back to library" aria-label="Home">
          🏠
        </button>
      </div>

      <div className={"synth-status" + (synthesizing ? " on" : "")}>
        <span className="synth-text">{status}</span>
      </div>
      <div className={"synth-line" + (synthesizing ? " on" : "")} style={{ width: `${pct}%` }} />
    </div>
  );
}
