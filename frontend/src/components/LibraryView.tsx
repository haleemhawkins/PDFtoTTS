import { useRef, useState } from "react";
import type { DocumentInfo, Voice } from "../api/types";

interface Props {
  documents: DocumentInfo[];
  voices: Voice[];
  /** True while an upload is being prepared (extraction/OCR). */
  busy: boolean;
  /** Optional phase message + progress for the in-flight upload (e.g. OCR). */
  statusText?: string;
  progress?: number;
  onOpen: (doc: DocumentInfo) => void;
  onUpload: (file: File, voice: string, speed: number) => void;
  onRename: (doc: DocumentInfo, name: string) => void;
  onDelete: (doc: DocumentInfo) => void;
}

const SPEEDS = [0.75, 1, 1.25, 1.5, 2];

function statusLabel(d: DocumentInfo): string {
  switch (d.status) {
    case "Ready":
      return `${d.type === "Epub" ? "EPUB" : `${d.pageCount} pages`} · ${d.wordCount.toLocaleString()} words`;
    case "Extracting":
      return "Preparing… (scanned PDFs are OCR'd first)";
    case "Error":
      return "Couldn't read this document";
    default:
      return "Queued…";
  }
}

/**
 * The app's home: the document library. Lists every uploaded PDF/EPUB with
 * open / rename / delete, plus an upload control to add a new one. Documents are
 * server-persisted, so they survive reloads and restarts; audio is never stored.
 */
export function LibraryView({
  documents, voices, busy, statusText, progress, onOpen, onUpload, onRename, onDelete,
}: Props) {
  const [voice, setVoice] = useState(voices[0]?.id ?? "af_heart");
  const [speed, setSpeed] = useState(1);
  const fileRef = useRef<HTMLInputElement>(null);

  const handleUpload = () => {
    const file = fileRef.current?.files?.[0];
    if (file) onUpload(file, voice, speed);
  };

  return (
    <div className="library-view">
      <h1>Your Library</h1>

      <div className="library-upload">
        <input
          ref={fileRef}
          type="file"
          accept=".pdf,.epub,application/pdf,application/epub+zip"
        />
        <label className="lib-field">
          Voice
          <select value={voice} onChange={(e) => setVoice(e.target.value)}>
            {(voices.length ? voices : [{ id: voice, label: voice } as Voice]).map((v) => (
              <option key={v.id} value={v.id}>{v.label}</option>
            ))}
          </select>
        </label>
        <label className="lib-field">
          Speed
          <select value={speed} onChange={(e) => setSpeed(Number(e.target.value))}>
            {SPEEDS.map((s) => <option key={s} value={s}>{s}×</option>)}
          </select>
        </label>
        <button onClick={handleUpload} disabled={busy}>
          {busy ? "Adding…" : "Add document"}
        </button>
      </div>

      {busy && statusText && <p className="upload-status">{statusText}</p>}
      {busy && progress != null && (
        <div className="ocr-progress" role="progressbar" aria-valuemin={0} aria-valuemax={1}
             aria-valuenow={progress > 0 ? progress : undefined}>
          <div className={"ocr-progress-track" + (progress > 0 ? "" : " indeterminate")}>
            <div className="ocr-progress-fill"
                 style={progress > 0 ? { width: `${Math.round(progress * 100)}%` } : undefined} />
          </div>
          <span className="ocr-progress-label">
            {progress > 0 ? `${Math.round(progress * 100)}%` : "Starting…"}
          </span>
        </div>
      )}

      {documents.length === 0 ? (
        <p className="library-empty">No documents yet — add a PDF or EPUB above to start reading.</p>
      ) : (
        <ul className="library-grid">
          {documents.map((d) => {
            const openable = d.status === "Ready";
            return (
              <li key={d.id} className={"library-card" + (openable ? "" : " disabled")}>
                <button
                  className="library-card-main"
                  onClick={() => openable && onOpen(d)}
                  disabled={!openable}
                  title={openable ? "Open" : statusLabel(d)}
                >
                  <span className={"doc-type-badge " + (d.type === "Epub" ? "epub" : "pdf")}>
                    {d.type === "Epub" ? "EPUB" : "PDF"}
                  </span>
                  <span className="library-card-name">{d.filename}</span>
                  <span className="library-card-status">{statusLabel(d)}</span>
                </button>
                <div className="library-card-actions">
                  <button
                    className="icon-btn"
                    title="Rename"
                    aria-label="Rename"
                    onClick={() => {
                      const name = window.prompt("Rename document", d.filename);
                      if (name && name.trim() && name.trim() !== d.filename) onRename(d, name.trim());
                    }}
                  >
                    ✏️
                  </button>
                  <button
                    className="icon-btn"
                    title="Delete"
                    aria-label="Delete"
                    onClick={() => {
                      if (window.confirm(`Delete "${d.filename}"? This can't be undone.`)) onDelete(d);
                    }}
                  >
                    🗑️
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
