import { useRef, useState } from "react";
import type { DocumentInfo, Voice } from "../api/types";
import { LibraryCover } from "./LibraryCover";

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
const ACCEPT = ".pdf,.epub,application/pdf,application/epub+zip";

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

const isDoc = (f: File) =>
  /\.(pdf|epub)$/i.test(f.name) || f.type === "application/pdf" || f.type === "application/epub+zip";

/** Compact "time since" for the card's resume hint. */
function relTime(ms?: number): string | null {
  if (!ms) return null;
  const min = Math.floor((Date.now() - ms) / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min} min ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} hr ago`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `${day} day${day > 1 ? "s" : ""} ago`;
  const mo = Math.floor(day / 30);
  if (mo < 12) return `${mo} mo ago`;
  return `${Math.floor(mo / 12)} yr ago`;
}

/**
 * The app's home: the document library. Lists every uploaded PDF/EPUB with
 * open / rename / delete, plus a drag-and-drop upload zone to add a new one.
 * Documents are server-persisted, so they survive reloads and restarts; audio
 * is never stored.
 */
export function LibraryView({
  documents, voices, busy, statusText, progress, onOpen, onUpload, onRename, onDelete,
}: Props) {
  const [voice, setVoice] = useState(voices[0]?.id ?? "af_heart");
  const [speed, setSpeed] = useState(1);
  const [file, setFile] = useState<File | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  const pick = (f: File | undefined | null) => {
    if (f && isDoc(f)) setFile(f);
  };

  const handleUpload = () => {
    if (file) onUpload(file, voice, speed);
  };

  return (
    <div className="library-view">
      <header className="library-head">
        <h1>Your library</h1>
        <p className="library-sub">
          {documents.length > 0
            ? `${documents.length} document${documents.length > 1 ? "s" : ""} · read aloud with the words highlighted as they're spoken`
            : "Add a PDF or EPUB and have it read aloud, with the words highlighted as they're spoken."}
        </p>
      </header>

      <section className="upload-panel">
        <label
          className={"dropzone" + (dragOver ? " over" : "") + (file ? " has-file" : "")}
          onDragOver={(e) => {
            e.preventDefault();
            setDragOver(true);
          }}
          onDragLeave={() => setDragOver(false)}
          onDrop={(e) => {
            e.preventDefault();
            setDragOver(false);
            pick(e.dataTransfer.files?.[0]);
          }}
        >
          <input
            ref={fileRef}
            type="file"
            accept={ACCEPT}
            className="dropzone-input"
            onChange={(e) => pick(e.target.files?.[0])}
          />
          <span className="dropzone-icon" aria-hidden>
            {file ? "📄" : "⬆"}
          </span>
          <span className="dropzone-text">
            {file ? (
              <>
                <strong>{file.name}</strong>
                <span className="dropzone-hint">Click to choose a different file</span>
              </>
            ) : (
              <>
                <strong>Drop a PDF or EPUB here</strong>
                <span className="dropzone-hint">or click to browse</span>
              </>
            )}
          </span>
        </label>

        <div className="upload-controls">
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
          <button className="btn-primary" onClick={handleUpload} disabled={busy || !file}>
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
      </section>

      {documents.length === 0 ? (
        <p className="library-empty">No documents yet — add a PDF or EPUB above to start reading.</p>
      ) : (
        <ul className="library-grid">
          {documents.map((d) => {
            const openable = d.status === "Ready";
            const lastRead = relTime(d.position?.updatedAtMs);
            return (
              <li key={d.id} className={"library-card" + (openable ? "" : " disabled")}>
                <button
                  className="library-card-main"
                  onClick={() => openable && onOpen(d)}
                  disabled={!openable}
                  title={openable ? "Open" : statusLabel(d)}
                >
                  <LibraryCover doc={d} />
                  <span className="library-card-text">
                    <span className="library-card-name">{d.filename}</span>
                    <span className="library-card-status">{statusLabel(d)}</span>
                    {openable && lastRead && (
                      <span className="library-card-resume">↩ Last read {lastRead}</span>
                    )}
                  </span>
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
                    className="icon-btn danger"
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
