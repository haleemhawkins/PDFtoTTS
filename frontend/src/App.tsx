import { useCallback, useEffect, useRef, useState } from "react";
import { EpubReader, type EpubReaderHandle, type TocItem } from "./components/EpubReader";
import { PdfReader } from "./components/PdfReader";
import { LibraryView } from "./components/LibraryView";
import { NavDrawer } from "./components/NavDrawer";
import { ReaderChrome } from "./components/ReaderChrome";
import { useReader } from "./hooks/useReader";
import { setMediaMetadata } from "./audio/mediaSession";
import { usePdfDocument } from "./pdf/usePdfDocument";
import * as api from "./api/client";
import type { DocumentInfo, Voice } from "./api/types";
import {
  clearLegacyStorage, forgetDoc, getDocState, getLastOpenedId, patchDocState,
  setLastOpenedId, updateSavedPage, updateSavedWord,
} from "./persist";
import "./App.css";

const FALLBACK_VOICES: Voice[] = [
  { id: "af_heart", label: "Heart (US, female)", language: "en-us", gender: "female" },
  { id: "am_adam", label: "Adam (US, male)", language: "en-us", gender: "male" },
];

export default function App() {
  const reader = useReader();
  const [view, setView] = useState<"library" | "reader">("library");
  const [documents, setDocuments] = useState<DocumentInfo[]>([]);
  const [voices, setVoices] = useState<Voice[]>(FALLBACK_VOICES);
  const [file, setFile] = useState<File | null>(null);
  const [voice, setVoice] = useState(FALLBACK_VOICES[0].id);
  const [speed, setSpeed] = useState(1);
  const [restoredPage, setRestoredPage] = useState(1);
  const [restoring, setRestoring] = useState(true);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [chromeVisible, setChromeVisible] = useState(true);
  const [nav, setNav] = useState({ page: 1, seq: 0 });
  // EPUB nav surface (parity with the PDF pager + chapters drawer).
  const epubRef = useRef<EpubReaderHandle>(null);
  const [epubToc, setEpubToc] = useState<TocItem[]>([]);
  const [epubChapter, setEpubChapter] = useState("");

  // Render PDF vs EPUB from the original's MIME type (set correctly by the server
  // for library opens, so a rename that drops the extension can't fool us), falling
  // back to the filename for fresh uploads where type may be blank.
  const isPdf = file
    ? file.type
      ? file.type === "application/pdf"
      : file.name.toLowerCase().endsWith(".pdf")
    : false;
  const isEpub = file !== null && !isPdf;
  const pdf = usePdfDocument(file, isPdf);

  const loadDocuments = useCallback(async () => {
    setDocuments(await api.listDocuments().catch(() => []));
  }, []);

  // Open a library document: fetch its original (for rendering) + start a session
  // at the saved position, landing paused. Returns to the library on failure.
  const openDoc = useCallback(async (doc: DocumentInfo) => {
    // Resume from whichever resume point is newer: the local cache or the server's
    // stored position (which may have been written from another device/browser).
    const local = getDocState(doc.id);
    const remote = doc.position ?? undefined;
    const useRemote = !!remote && (remote.updatedAtMs ?? 0) > (local?.updatedAt ?? 0);
    const page = (useRemote ? remote!.page : local?.page) ?? 1;
    const word = useRemote ? remote!.word : local?.word;
    const v = (useRemote ? remote!.voice : local?.voice) || voices[0]?.id || FALLBACK_VOICES[0].id;
    const sp = (useRemote ? remote!.speed : local?.speed) ?? 1;
    // When the server's position won, seed the local cache with it (keeping the
    // server's timestamp) so this device now agrees and won't push a stale value back.
    if (useRemote && remote) {
      patchDocState(doc.id, { page, word: word ?? 0, voice: v, speed: sp, updatedAt: remote.updatedAtMs });
    }
    try {
      const f = await api.getOriginal(doc);
      setFile(f);
      setVoice(v);
      setSpeed(sp);
      setRestoredPage(page);
      setNav({ page, seq: 0 });
      setView("reader");
      setLastOpenedId(doc.id);
      setMediaMetadata(doc.filename);
      await reader.open(doc.id, v, sp, page, word);
    } catch (e) {
      console.error("Failed to open document", e);
      setView("library");
      setFile(null);
    }
    // reader.open is stable; voices read at call time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // First load: clear pre-library storage, fetch voices + library, and restore the
  // last-opened document (by id) across a reload.
  useEffect(() => {
    clearLegacyStorage();
    let cancelled = false;
    (async () => {
      const [vs, docs] = await Promise.all([
        api.getVoices(),
        api.listDocuments().catch(() => []),
      ]);
      if (cancelled) return;
      if (vs.length) {
        setVoices(vs);
        setVoice((cur) => (vs.some((v) => v.id === cur) ? cur : vs[0].id));
      }
      setDocuments(docs);
      const last = getLastOpenedId();
      const doc = last ? docs.find((d) => d.id === last) : undefined;
      if (doc && doc.status === "Ready") await openDoc(doc);
      if (!cancelled) setRestoring(false);
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // While anything is still extracting/OCR'ing, refresh the library so its status
  // (and openability) updates without a manual reload.
  useEffect(() => {
    if (!documents.some((d) => d.status === "Extracting" || d.status === "Queued")) return;
    const t = window.setTimeout(() => void loadDocuments(), 1500);
    return () => clearTimeout(t);
  }, [documents, loadDocuments]);

  // Immersive reading (PDF and EPUB alike): the chrome auto-hides a few seconds into
  // playback and shows again whenever paused/loading or the drawer is open. A tap on
  // the page toggles it (EpubReader forwards in-iframe taps via onToggleChrome).
  // "Immersive" = actively playing with no drawer; everything else keeps the chrome up.
  const immersive = reader.state === "playing" && !drawerOpen;
  // Re-show the chrome the moment we leave immersive playback. Done during render
  // (guarded by a transition check) rather than in an effect, so it can't trigger the
  // cascading renders react-hooks/set-state-in-effect warns about.
  const [wasImmersive, setWasImmersive] = useState(immersive);
  if (wasImmersive !== immersive) {
    setWasImmersive(immersive);
    if (!immersive) setChromeVisible(true);
  }
  // While immersive AND the chrome is up, arm a 3s auto-hide. It re-runs (re-arming)
  // whenever a tap re-shows the chrome, and clears on hide / on leaving immersive. The
  // setState lives in the timer callback (asynchronous), never in the effect body.
  useEffect(() => {
    if (!immersive || !chromeVisible) return;
    const t = window.setTimeout(() => setChromeVisible(false), 3000);
    return () => clearTimeout(t);
  }, [immersive, chromeVisible]);

  // A tap toggles the chrome; the auto-hide effect above re-arms itself when shown.
  const toggleChrome = useCallback(() => setChromeVisible((v) => !v), []);

  // Upload from the library: create the document on the server and start reading it
  // immediately (renders from the local file — no need to re-fetch the original).
  const onUpload = (f: File, v: string, s: number) => {
    setFile(f);
    setVoice(v);
    setSpeed(s);
    setRestoredPage(1);
    setNav({ page: 1, seq: 0 });
    setView("reader");
    setMediaMetadata(f.name);
    void reader.start(f, v, s);
  };

  // Persist defaults + remember the just-uploaded/opened document, and refresh the
  // library so a freshly uploaded doc is listed when we return home.
  useEffect(() => {
    if (!reader.documentId) return;
    setLastOpenedId(reader.documentId);
    patchDocState(reader.documentId, { voice, speed });
    // Refresh the library list (so a freshly-uploaded doc shows up). The setState
    // runs after the await — deferred, not synchronous in the effect body.
    let cancelled = false;
    void (async () => {
      const docs = await api.listDocuments().catch(() => []);
      if (!cancelled) setDocuments(docs);
    })();
    return () => { cancelled = true; };
    // Only when the loaded document changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reader.documentId]);

  // Push the latest locally-cached position to the server so it resumes on any
  // device. Reads the freshest values straight from the cache (the page/word
  // effects keep it current); best-effort, never blocks reading.
  const pushPosition = useCallback((id: string | null) => {
    if (!id) return;
    const st = getDocState(id);
    if (!st) return;
    void api.savePosition(id, {
      page: st.page, word: st.word, voice: st.voice, speed: st.speed,
      updatedAtMs: st.updatedAt ?? Date.now(),
    });
  }, []);

  const onSpeed = (s: number) => {
    setSpeed(s);
    if (reader.documentId) {
      patchDocState(reader.documentId, { speed: s });
      pushPosition(reader.documentId);
    }
    // Re-synthesize at Kokoro's native speed so the pace changes with a natural
    // pitch (not the resampled "chipmunk" effect of changing playback rate).
    void reader.changeSpeed(s);
  };

  const onVoiceChange = (v: string) => {
    setVoice(v);
    if (reader.documentId) {
      patchDocState(reader.documentId, { voice: v });
      pushPosition(reader.documentId);
    }
    void reader.changeVoice(v);
  };

  const onHome = () => {
    pushPosition(reader.documentId); // capture the spot before tearing down
    reader.reset();
    setLastOpenedId(undefined);
    setFile(null);
    setRestoredPage(1);
    setDrawerOpen(false);
    setView("library");
    void loadDocuments();
  };

  const onRename = async (doc: DocumentInfo, name: string) => {
    await api.renameDocument(doc.id, name).catch((e) => console.error("rename failed", e));
    await loadDocuments();
  };

  const onDelete = async (doc: DocumentInfo) => {
    await api.deleteDocument(doc.id).catch((e) => console.error("delete failed", e));
    forgetDoc(doc.id);
    await loadDocuments();
  };

  const onPageChange = useCallback((page: number) => {
    setNav((n) => (n.page === page ? n : { ...n, page }));
    if (reader.documentId) {
      updateSavedPage(reader.documentId, page);
      pushPosition(reader.documentId);
    }
  }, [reader.documentId, pushPosition]);

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
    if (activeWordIndex != null && reader.documentId) updateSavedWord(reader.documentId, activeWordIndex);
  }, [activeWordIndex, reader.documentId]);

  // While actively reading, sync the position to the server every 15s so a crash or
  // dropped connection still leaves a recent resume point (discrete leave-events —
  // pause, page turn, voice/speed change, Home, tab hidden, unload — cover the rest).
  useEffect(() => {
    if (reader.state !== "playing" || !reader.documentId) return;
    const id = reader.documentId;
    const t = window.setInterval(() => pushPosition(id), 15000);
    return () => clearInterval(t);
  }, [reader.state, reader.documentId, pushPosition]);

  // Flush the position when the tab is hidden or the page is unloading, so closing
  // the app (or switching away on mobile) captures the latest spot server-side.
  useEffect(() => {
    const flush = () => pushPosition(reader.documentId);
    const onVisibility = () => { if (document.hidden) flush(); };
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("pagehide", flush);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("pagehide", flush);
    };
  }, [reader.documentId, pushPosition]);

  if (restoring) return <div className="app" />;

  return (
    <div className="app">
      {reader.error && <div className="error-banner">{reader.error}</div>}

      {view === "library" ? (
        <LibraryView
          documents={documents}
          voices={voices}
          busy={reader.state === "uploading" || reader.state === "extracting"}
          statusText={
            reader.state === "extracting"
              ? "Preparing document… scanned PDFs are run through OCR first, which can take a few minutes."
              : undefined
          }
          progress={reader.state === "extracting" ? reader.extractProgress : undefined}
          onOpen={openDoc}
          onUpload={onUpload}
          onRename={onRename}
          onDelete={onDelete}
        />
      ) : (
        <div className="reader-view">
          <div className={"chrome-wrap" + (chromeVisible ? "" : " hidden")}>
            <ReaderChrome
              state={reader.state}
              ready={reader.ready}
              progress={reader.progress}
              speed={speed}
              voices={voices}
              voice={voice}
              totalMs={reader.timeline.totalMs}
              showMenu={isPdf ? pdf !== null : isEpub}
              getPositionMs={reader.getPositionMs}
              onMenu={() => {
                setChromeVisible(true);
                setDrawerOpen(true);
              }}
              onHome={onHome}
              onPlay={reader.play}
              onPause={() => { reader.pause(); pushPosition(reader.documentId); }}
              onSpeed={onSpeed}
              onVoice={onVoiceChange}
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

          {/* EPUB pager — same control as the PDF, paging via the rendition. */}
          {isEpub && (
            <div className={"pdf-pager floating" + (chromeVisible ? "" : " hidden")}>
              <button onClick={() => epubRef.current?.prev()}>‹ Prev</button>
              <span>{epubChapter || "EPUB"}</span>
              <button onClick={() => epubRef.current?.next()}>Next ›</button>
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
              ref={epubRef}
              file={file}
              timeline={reader.timeline}
              activeIndex={reader.activeIndex}
              onSeekToWord={reader.seekToWord}
              onNavigate={reader.jumpToSourceWord}
              onToggleChrome={toggleChrome}
              onToc={setEpubToc}
              onChapter={setEpubChapter}
            />
          ) : null}
        </div>
      )}

      {view === "reader" && pdf && (
        <NavDrawer
          open={drawerOpen}
          pdf={pdf}
          numPages={pdf.numPages}
          currentPage={nav.page}
          onNavigate={onNavigate}
          onClose={() => setDrawerOpen(false)}
        />
      )}

      {/* EPUB chapters drawer — the ☰ menu's counterpart to the PDF NavDrawer. */}
      {view === "reader" && isEpub && (
        <>
          <div className={"drawer-scrim" + (drawerOpen ? " open" : "")} onClick={() => setDrawerOpen(false)} />
          <aside className={"nav-drawer" + (drawerOpen ? " open" : "")} aria-hidden={!drawerOpen}>
            <div className="nav-drawer-head">
              <div className="nav-tabs">
                <button className="active">Chapters</button>
              </div>
              <button className="nav-close" onClick={() => setDrawerOpen(false)} aria-label="Close">
                ✕
              </button>
            </div>
            <div className="nav-drawer-scroll">
              {epubToc.length === 0 ? (
                <p className="nav-empty">This book has no chapter list.</p>
              ) : (
                <ul className="chapter-list">
                  {epubToc.map((c, i) => (
                    <li key={i}>
                      <button
                        style={{ paddingLeft: `${0.8 + c.depth}rem` }}
                        onClick={() => {
                          setDrawerOpen(false);
                          epubRef.current?.display(c.href);
                        }}
                      >
                        {c.label}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </aside>
        </>
      )}
    </div>
  );
}
