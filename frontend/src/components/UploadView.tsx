import { useEffect, useRef, useState } from "react";
import { getVoices } from "../api/client";
import type { Voice } from "../api/types";

interface Props {
  busy: boolean;
  /** Optional phase message shown while busy (e.g. OCR of a scanned PDF). */
  statusText?: string;
  /** OCR/extraction progress in [0,1]; undefined hides the bar. A value of 0
   *  renders an indeterminate (animated) bar until the first page is reported. */
  progress?: number;
  onStart: (file: File, voice: string, speed: number) => void;
}

const FALLBACK_VOICES: Voice[] = [
  { id: "af_heart", label: "Heart (US, female)", language: "en-us", gender: "female" },
  { id: "am_adam", label: "Adam (US, male)", language: "en-us", gender: "male" },
];

export function UploadView({ busy, statusText, progress, onStart }: Props) {
  const [voices, setVoices] = useState<Voice[]>(FALLBACK_VOICES);
  const [voice, setVoice] = useState("af_heart");
  const [speed, setSpeed] = useState(1);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    getVoices().then((v) => {
      if (v.length) {
        setVoices(v);
        setVoice(v[0].id);
      }
    });
  }, []);

  const handleStart = () => {
    const file = fileRef.current?.files?.[0];
    if (file) onStart(file, voice, speed);
  };

  return (
    <div className="upload-view">
      <h1>PDF / EPUB Reader</h1>
      <p>Upload a document to have it read aloud with synced word highlighting.</p>

      <input ref={fileRef} type="file" accept=".pdf,.epub,application/pdf,application/epub+zip" />

      <label>
        Voice
        <select value={voice} onChange={(e) => setVoice(e.target.value)}>
          {voices.map((v) => (
            <option key={v.id} value={v.id}>
              {v.label}
            </option>
          ))}
        </select>
      </label>

      <label>
        Speed
        <input
          type="range"
          min={0.75}
          max={2}
          step={0.25}
          value={speed}
          onChange={(e) => setSpeed(Number(e.target.value))}
        />
        {speed}×
      </label>

      <button onClick={handleStart} disabled={busy}>
        {busy ? "Starting…" : "Start reading"}
      </button>

      {busy && statusText && <p className="upload-status">{statusText}</p>}

      {busy && progress != null && (
        <div className="ocr-progress" role="progressbar" aria-valuemin={0} aria-valuemax={1}
             aria-valuenow={progress > 0 ? progress : undefined}>
          <div className={"ocr-progress-track" + (progress > 0 ? "" : " indeterminate")}>
            <div className="ocr-progress-fill" style={progress > 0 ? { width: `${Math.round(progress * 100)}%` } : undefined} />
          </div>
          <span className="ocr-progress-label">
            {progress > 0 ? `${Math.round(progress * 100)}%` : "Starting…"}
          </span>
        </div>
      )}
    </div>
  );
}
