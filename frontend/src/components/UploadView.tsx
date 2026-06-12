import { useEffect, useRef, useState } from "react";
import { getVoices } from "../api/client";
import type { Voice } from "../api/types";

interface Props {
  busy: boolean;
  onStart: (file: File, voice: string, speed: number) => void;
}

const FALLBACK_VOICES: Voice[] = [
  { id: "af_heart", label: "Heart (US, female)", language: "en-us", gender: "female" },
  { id: "am_adam", label: "Adam (US, male)", language: "en-us", gender: "male" },
];

export function UploadView({ busy, onStart }: Props) {
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
    </div>
  );
}
