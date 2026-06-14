import { useCallback, useEffect, useState } from "react";
import { EpubReader } from "./components/EpubReader";
import { PdfReader } from "./components/PdfReader";
import { PlaybackBar } from "./components/PlaybackBar";
import { Scrubber } from "./components/Scrubber";
import { UploadView } from "./components/UploadView";
import { useReader } from "./hooks/useReader";
import {
  clearSaved, loadFile, loadMeta, patchMeta, saveFile, saveMeta, updateSavedPage,
} from "./persist";
import "./App.css";

export default function App() {
  const reader = useReader();
  const [file, setFile] = useState<File | null>(null);
  const [speed, setSpeed] = useState(1);
  const [restoredPage, setRestoredPage] = useState(1);
  const [restoring, setRestoring] = useState(true);

  // Restore the last document (file from IndexedDB, position from localStorage)
  // across a page reload, resuming on the page you were on.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const meta = loadMeta();
      const blob = meta ? await loadFile() : null;
      if (!cancelled && meta && blob) {
        const f = new File([blob], meta.name, { type: blob.type || "application/pdf" });
        setFile(f);
        setSpeed(meta.speed);
        setRestoredPage(meta.page);
        void reader.start(f, meta.voice, meta.speed, meta.page);
      }
      if (!cancelled) setRestoring(false);
    })();
    return () => {
      cancelled = true;
    };
    // Run once on mount; reader.start is stable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const onStart = (f: File, voice: string, s: number) => {
    setFile(f);
    setSpeed(s);
    setRestoredPage(1);
    void saveFile(f);
    saveMeta({ name: f.name, voice, speed: s, page: 1 });
    void reader.start(f, voice, s);
  };

  const onSpeed = (s: number) => {
    setSpeed(s);
    patchMeta({ speed: s });
    // Re-synthesize at Kokoro's native speed so the pace changes with a natural
    // pitch (not the resampled "chipmunk" effect of changing playback rate).
    void reader.changeSpeed(s);
  };

  const onHome = () => {
    void clearSaved();
    reader.reset();
    setFile(null);
    setRestoredPage(1);
  };

  const onPageChange = useCallback((page: number) => updateSavedPage(page), []);

  const inReader =
    file !== null &&
    (reader.state === "processing" ||
      reader.state === "playing" ||
      reader.state === "paused" ||
      reader.state === "reconnecting" ||
      (reader.state === "error" && reader.timeline.words.length > 0));

  const isPdf = file?.name.toLowerCase().endsWith(".pdf") ?? false;

  if (restoring) return <div className="app" />;

  return (
    <div className="app">
      {reader.error && <div className="error-banner">{reader.error}</div>}

      {!inReader ? (
        <UploadView busy={reader.state === "uploading"} onStart={onStart} />
      ) : (
        <div className="reader-view">
          <PlaybackBar
            state={reader.state}
            progress={reader.progress}
            speed={speed}
            onHome={onHome}
            onPlay={reader.play}
            onPause={reader.pause}
            onSpeed={onSpeed}
          />
          <Scrubber
            getPositionMs={reader.getPositionMs}
            totalMs={reader.timeline.totalMs}
            onSeek={reader.seekToMs}
          />
          {file && isPdf ? (
            <PdfReader
              file={file}
              timeline={reader.timeline}
              activeIndex={reader.activeIndex}
              initialPage={restoredPage}
              onJumpToWord={reader.jumpToWord}
              onJumpToPage={reader.jumpToPage}
              onPageChange={onPageChange}
            />
          ) : file ? (
            <EpubReader
              file={file}
              timeline={reader.timeline}
              activeIndex={reader.activeIndex}
              onSeekToWord={reader.seekToWord}
            />
          ) : null}
        </div>
      )}
    </div>
  );
}
