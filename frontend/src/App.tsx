import { useCallback, useEffect, useRef, useState } from "react";
import { EpubReader } from "./components/EpubReader";
import { PdfReader } from "./components/PdfReader";
import { NavDrawer } from "./components/NavDrawer";
import { ReaderChrome } from "./components/ReaderChrome";
import { UploadView } from "./components/UploadView";
import { useReader } from "./hooks/useReader";
import { usePdfDocument } from "./pdf/usePdfDocument";
import {
  clearSaved, loadFile, loadMeta, patchMeta, saveFile, saveMeta, updateSavedPage, updateSavedWord,
} from "./persist";
import "./App.css";

export default function App() {
  const reader = useReader();
  const [file, setFile] = useState<File | null>(null);
  const [speed, setSpeed] = useState(1);
  const [restoredPage, setRestoredPage] = useState(1);
  const [restoring, setRestoring] = useState(true);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [chromeVisible, setChromeVisible] = useState(true);
  const [nav, setNav] = useState({ page: 1, seq: 0 });
  const chromeTimer = useRef<number | null>(null);

  const isPdf = file?.name.toLowerCase().endsWith(".pdf") ?? false;
  const pdf = usePdfDocument(file, isPdf);

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
        // Resume at the exact saved word (lands paused, highlighted); falls back to
        // the page's first word for sessions saved before word tracking existed.
        void reader.start(f, meta.voice, meta.speed, meta.page, meta.word);
      }
      if (!cancelled) setRestoring(false);
    })();
    return () => {
      cancelled = true;
    };
    // Run once on mount; reader.start is stable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const clearChromeTimer = () => {
    if (chromeTimer.current !== null) {
      clearTimeout(chromeTimer.current);
      chromeTimer.current = null;
    }
  };
  const armHide = useCallback(() => {
    clearChromeTimer();
    chromeTimer.current = window.setTimeout(() => setChromeVisible(false), 3000);
  }, []);

  // Immersive reading: while playing a PDF, auto-hide the chrome; show it whenever
  // paused/loading or the drawer is open. (EPUB keeps chrome visible — its content
  // is in an iframe, so a tap-to-toggle is unreliable there.)
  useEffect(() => {
    if (!isPdf) {
      setChromeVisible(true);
      return;
    }
    if (reader.state === "playing" && !drawerOpen) armHide();
    else {
      clearChromeTimer();
      setChromeVisible(true);
    }
    return clearChromeTimer;
  }, [reader.state, isPdf, drawerOpen, armHide]);

  const toggleChrome = useCallback(() => {
    setChromeVisible((v) => {
      const next = !v;
      if (next && reader.state === "playing") armHide();
      else clearChromeTimer();
      return next;
    });
  }, [reader.state, armHide]);

  const onStart = (f: File, voice: string, s: number) => {
    setFile(f);
    setSpeed(s);
    setRestoredPage(1);
    void saveFile(f);
    saveMeta({ name: f.name, voice, speed: s, page: 1, word: 0 });
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
    setDrawerOpen(false);
  };

  const onPageChange = useCallback((page: number) => {
    setNav((n) => (n.page === page ? n : { ...n, page }));
    updateSavedPage(page);
  }, []);

  // Navigate from the drawer: position + preload that page (paused) and tell the
  // PDF view to display it. The seq bump makes re-picking the same page work too.
  const onNavigate = useCallback((page: number) => {
    setDrawerOpen(false);
    setNav((n) => ({ page, seq: n.seq + 1 }));
    reader.jumpToPage(page);
  }, [reader]);

  // Persist the exact word currently being read so a full reload resumes there.
  const activeWordIndex = reader.timeline.words[reader.activeIndex]?.wordIndex;
  useEffect(() => {
    if (activeWordIndex != null) updateSavedWord(activeWordIndex);
  }, [activeWordIndex]);

  const inReader =
    file !== null &&
    (reader.state === "processing" ||
      reader.state === "playing" ||
      reader.state === "paused" ||
      reader.state === "reconnecting" ||
      (reader.state === "error" && reader.timeline.words.length > 0));

  if (restoring) return <div className="app" />;

  return (
    <div className="app">
      {reader.error && <div className="error-banner">{reader.error}</div>}

      {!inReader ? (
        <UploadView
          busy={reader.state === "uploading" || reader.state === "extracting"}
          statusText={
            reader.state === "extracting"
              ? "Preparing document… scanned PDFs are run through OCR first, which can take a few minutes."
              : undefined
          }
          progress={reader.state === "extracting" ? reader.extractProgress : undefined}
          onStart={onStart}
        />
      ) : (
        <div className="reader-view">
          <div className={"chrome-wrap" + (chromeVisible ? "" : " hidden")}>
            <ReaderChrome
              state={reader.state}
              ready={reader.ready}
              progress={reader.progress}
              speed={speed}
              totalMs={reader.timeline.totalMs}
              showMenu={isPdf && pdf !== null}
              getPositionMs={reader.getPositionMs}
              onMenu={() => {
                clearChromeTimer();
                setChromeVisible(true);
                setDrawerOpen(true);
              }}
              onHome={onHome}
              onPlay={reader.play}
              onPause={reader.pause}
              onSpeed={onSpeed}
              onSeek={reader.seekToMs}
            />
          </div>

          {/* Floating page control at the bottom, auto-hiding with the chrome. */}
          {isPdf && pdf && (
            <div className={"pdf-pager floating" + (chromeVisible ? "" : " hidden")}>
              <button
                onClick={() => onNavigate(Math.max(1, nav.page - 1))}
                disabled={nav.page <= 1}
              >
                ‹ Prev
              </button>
              <span>
                Page {nav.page} / {pdf.numPages}
              </span>
              <button
                onClick={() => onNavigate(Math.min(pdf.numPages, nav.page + 1))}
                disabled={nav.page >= pdf.numPages}
              >
                Next ›
              </button>
            </div>
          )}

          {file && isPdf ? (
            pdf ? (
              <PdfReader
                pdf={pdf}
                timeline={reader.timeline}
                activeIndex={reader.activeIndex}
                initialPage={restoredPage}
                gotoSeq={nav.seq}
                gotoPage={nav.page}
                onJumpToWord={reader.jumpToWord}
                onPageChange={onPageChange}
                onToggleChrome={toggleChrome}
              />
            ) : (
              <p className="nav-empty">Loading document…</p>
            )
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

      {pdf && (
        <NavDrawer
          open={drawerOpen}
          pdf={pdf}
          numPages={pdf.numPages}
          currentPage={nav.page}
          onNavigate={onNavigate}
          onClose={() => setDrawerOpen(false)}
        />
      )}
    </div>
  );
}
