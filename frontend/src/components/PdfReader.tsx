import { useEffect, useRef, useState } from "react";
import * as pdfjsLib from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import type { Timeline, TimelineWord } from "../sync/wordTimeline";

pdfjsLib.GlobalWorkerOptions.workerSrc = workerUrl;

interface OverlayBox {
  timelineIndex: number;
  left: number;
  top: number;
  width: number;
  height: number;
}

interface Props {
  file: File;
  timeline: Timeline;
  activeIndex: number;
  scale?: number;
  onSeekToWord: (timelineIndex: number) => void;
}

export function PdfReader({ file, timeline, activeIndex, scale = 1.5, onSeekToWord }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const activeBoxRef = useRef<HTMLDivElement | null>(null);
  const lastManualScroll = useRef(0);
  const [pdf, setPdf] = useState<pdfjsLib.PDFDocumentProxy | null>(null);
  const [page, setPage] = useState(1);
  const [viewport, setViewport] = useState<pdfjsLib.PageViewport | null>(null);
  const [overlays, setOverlays] = useState<OverlayBox[]>([]);
  // Set when the user jumps to a page that hasn't been synthesized yet; the
  // seek+read is performed once that page's words stream in.
  const [pendingPage, setPendingPage] = useState<number | null>(null);

  const SCROLL_GRACE_MS = 2500;

  // Load the PDF document from the uploaded file.
  useEffect(() => {
    let cancelled = false;
    file.arrayBuffer().then(async (buf) => {
      const doc = await pdfjsLib.getDocument({ data: buf }).promise;
      if (!cancelled) setPdf(doc);
    });
    return () => {
      cancelled = true;
    };
  }, [file]);

  // Render the page canvas. Depends ONLY on pdf/page/scale — never on the
  // timeline — so streaming chunks don't thrash the render and blank the canvas.
  // The in-flight render task is cancelled on change to avoid pdfjs "canvas in
  // use" conflicts (e.g. React StrictMode double-invoke).
  useEffect(() => {
    if (!pdf) return;
    let cancelled = false;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let task: any = null;

    (async () => {
      const pdfPage = await pdf.getPage(page);
      if (cancelled) return;
      const vp = pdfPage.getViewport({ scale });
      const canvas = canvasRef.current;
      if (!canvas) return;

      canvas.width = vp.width;
      canvas.height = vp.height;
      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      task = pdfPage.render({ canvasContext: ctx, viewport: vp, canvas });
      try {
        await task.promise;
      } catch (e) {
        if ((e as { name?: string })?.name !== "RenderingCancelledException") console.error(e);
        return;
      }
      if (!cancelled) setViewport(vp);
    })();

    return () => {
      cancelled = true;
      task?.cancel?.();
    };
  }, [pdf, page, scale]);

  // Recompute overlay boxes for the current page when the viewport or the
  // streamed words change. This is cheap and independent of canvas rendering.
  useEffect(() => {
    if (!viewport) return;
    const boxes: OverlayBox[] = [];
    timeline.words.forEach((w, i) => {
      if (w.page !== page || !w.bbox) return;
      const r = viewport.convertToViewportRectangle([
        w.bbox.x,
        w.bbox.y,
        w.bbox.x + w.bbox.width,
        w.bbox.y + w.bbox.height,
      ]);
      boxes.push({
        timelineIndex: i,
        left: Math.min(r[0], r[2]),
        top: Math.min(r[1], r[3]),
        width: Math.abs(r[2] - r[0]),
        height: Math.abs(r[3] - r[1]),
      });
    });
    setOverlays(boxes);
  }, [viewport, timeline, page]);

  // Follow the active word across pages while playing (auto page-turn), unless
  // the user has jumped ahead to a page that isn't ready yet.
  useEffect(() => {
    if (pendingPage != null) return;
    const active = timeline.words[activeIndex];
    if (active?.page && active.page !== page) setPage(active.page);
  }, [activeIndex, timeline, page, pendingPage]);

  // Resolve a deferred page jump: once the target page's words have streamed in,
  // seek there and start reading.
  useEffect(() => {
    if (pendingPage == null) return;
    const first = timeline.words.findIndex((w) => w.page === pendingPage);
    if (first >= 0) {
      onSeekToWord(first);
      setPendingPage(null);
    }
  }, [timeline, pendingPage, onSeekToWord]);

  // Record genuine user scrolls (wheel/touch) — not programmatic scrollIntoView —
  // to grant a grace period during which auto-scroll backs off (design §6.7).
  useEffect(() => {
    const onManual = () => {
      lastManualScroll.current = Date.now();
    };
    window.addEventListener("wheel", onManual, { passive: true });
    window.addEventListener("touchmove", onManual, { passive: true });
    return () => {
      window.removeEventListener("wheel", onManual);
      window.removeEventListener("touchmove", onManual);
    };
  }, []);

  // Keep the active word in view, unless the user scrolled recently.
  useEffect(() => {
    if (Date.now() - lastManualScroll.current < SCROLL_GRACE_MS) return;
    activeBoxRef.current?.scrollIntoView({ block: "center", behavior: "smooth" });
  }, [activeIndex]);

  // Manual page jump: change the page AND seek playback to that page's first
  // word. If that page isn't synthesized yet, defer the seek until it is.
  const goToPage = (target: number) => {
    if (!pdf || target < 1 || target > pdf.numPages) return;
    setPage(target);
    const first = timeline.words.findIndex((w) => w.page === target);
    if (first >= 0) {
      onSeekToWord(first);
      setPendingPage(null);
    } else {
      setPendingPage(target);
    }
  };

  const activeWord: TimelineWord | undefined = timeline.words[activeIndex];

  return (
    <div className="pdf-reader">
      <div className="pdf-stage" style={{ position: "relative" }}>
        <canvas ref={canvasRef} />
        <div className="word-overlay">
          {overlays.map((b) => (
            <div
              key={b.timelineIndex}
              ref={b.timelineIndex === activeIndex ? activeBoxRef : undefined}
              className={"word-box" + (b.timelineIndex === activeIndex ? " active" : "")}
              style={{ left: b.left, top: b.top, width: b.width, height: b.height }}
              onClick={() => onSeekToWord(b.timelineIndex)}
            />
          ))}
        </div>
      </div>
      <div className="pdf-pager">
        <button onClick={() => goToPage(page - 1)} disabled={page <= 1}>
          ‹ Prev
        </button>
        <span>
          Page {page}
          {pdf ? ` / ${pdf.numPages}` : ""}
          {activeWord ? ` — “${activeWord.text}”` : ""}
        </span>
        <button onClick={() => goToPage(page + 1)} disabled={!pdf || page >= pdf.numPages}>
          Next ›
        </button>
      </div>
    </div>
  );
}
