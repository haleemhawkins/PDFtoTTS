import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { PdfDoc } from "../pdf/usePdfDocument";
import type { Timeline } from "../sync/wordTimeline";

interface OverlayBox {
  timelineIndex: number;
  wordIndex: number; // source document word index
  left: number;
  top: number;
  width: number;
  height: number;
}

interface Props {
  pdf: PdfDoc;
  timeline: Timeline;
  activeIndex: number;
  scale?: number;
  initialPage?: number;
  /** External navigation command (chrome pager / drawer): bumping the seq jumps
   *  to gotoPage. */
  gotoSeq: number;
  gotoPage: number;
  onJumpToWord: (sourceWordIndex: number) => void;
  onPageChange?: (page: number) => void;
  onToggleChrome: () => void;
}

export function PdfReader({
  pdf, timeline, activeIndex, scale = 1.5, initialPage = 1,
  gotoSeq, gotoPage,
  onJumpToWord, onPageChange, onToggleChrome,
}: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const activeBoxRef = useRef<HTMLDivElement | null>(null);
  const lastManualScroll = useRef(0);
  // The last activeIndex we auto-scrolled to — so an overlay rebuild (chunk /
  // page-turn) can tell "the active word changed" from "same word, boxes redrawn".
  const lastScrolledIndex = useRef(-1);
  const [page, setPage] = useState(initialPage);
  const [viewport, setViewport] = useState<import("pdfjs-dist").PageViewport | null>(null);
  const [overlays, setOverlays] = useState<OverlayBox[]>([]);
  const [containerWidth, setContainerWidth] = useState(0);

  const SCROLL_GRACE_MS = 2500;

  // Report the visible page up so it can be persisted across reloads.
  useEffect(() => {
    onPageChange?.(page);
  }, [page, onPageChange]);

  // External navigation (a thumbnail / chapter pick): show that page. The seq
  // bumps even when re-picking the same page, so it always takes effect.
  useEffect(() => {
    if (gotoSeq > 0) setPage(gotoPage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gotoSeq]);

  // Measure the available width BEFORE first paint (useLayoutEffect) so the page
  // renders fit-to-width immediately — never a momentary full-size render that
  // overflows the viewport and makes a mobile browser shrink-to-fit ("zoom out").
  useLayoutEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const update = () => setContainerWidth(el.clientWidth);
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    window.addEventListener("orientationchange", update);
    return () => {
      ro.disconnect();
      window.removeEventListener("orientationchange", update);
    };
  }, []);

  // Render the page canvas. Depends on pdf/page/scale and the measured width —
  // never on the timeline — so streaming chunks don't thrash the render. The
  // in-flight render task is cancelled on change to avoid pdfjs "canvas in use"
  // conflicts (e.g. React StrictMode double-invoke).
  useEffect(() => {
    if (!containerWidth) return; // wait until measured — avoid a full-size flash
    let cancelled = false;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let task: any = null;

    (async () => {
      const pdfPage = await pdf.getPage(page);
      if (cancelled) return;
      const canvas = canvasRef.current;
      if (!canvas) return;

      // Fit the page to the available WIDTH so it fills the screen at a readable
      // zoom (tall pages scroll vertically). PDF text is vector, so up-scaling
      // stays crisp — the `scale` cap exists only so a small page isn't blown up
      // huge on a WIDE desktop container. On a narrow screen (phone/PWA) we
      // ALWAYS fill the width, otherwise a small-page PDF renders as a tiny
      // island with big margins (looks "zoomed out"). The CSS-px viewport drives
      // the overlay; the canvas is rendered at devicePixelRatio for crisp retina.
      const base = pdfPage.getViewport({ scale: 1 });
      const fitToWidth = (containerWidth - 2) / base.width;
      const narrow = containerWidth <= 760; // phone / small tablet portrait
      const fit = Math.max(0.4, narrow ? fitToWidth : Math.min(scale, fitToWidth));
      const vp = pdfPage.getViewport({ scale: fit });
      const dpr = Math.min(window.devicePixelRatio || 1, 2);

      canvas.width = Math.round(vp.width * dpr);
      canvas.height = Math.round(vp.height * dpr);
      canvas.style.width = `${vp.width}px`;
      canvas.style.height = `${vp.height}px`;
      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      const renderVp = dpr === 1 ? vp : pdfPage.getViewport({ scale: fit * dpr });
      task = pdfPage.render({ canvasContext: ctx, viewport: renderVp, canvas });
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
  }, [pdf, page, scale, containerWidth]);

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
        wordIndex: w.wordIndex,
        left: Math.min(r[0], r[2]),
        top: Math.min(r[1], r[3]),
        width: Math.abs(r[2] - r[0]),
        height: Math.abs(r[3] - r[1]),
      });
    });
    setOverlays(boxes);
  }, [viewport, timeline, page]);

  // Follow the active word across pages while playing (auto page-turn).
  useEffect(() => {
    const active = timeline.words[activeIndex];
    if (active?.page && active.page !== page) setPage(active.page);
  }, [activeIndex, timeline, page]);

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

  // Keep the active word in view, unless the user scrolled recently. Also depend
  // on `overlays`: when playback turns the page, the active word's box for the NEW
  // page doesn't exist yet on the render where activeIndex changes (the canvas
  // re-renders async, then overlays rebuild) — so keying on activeIndex alone left
  // the highlight off-screen until the next word.
  //   - When the active word CHANGES, re-center it (smooth, continuous following).
  //   - When only `overlays` rebuilt (a page-turn settling, or a streamed chunk),
  //     scroll only if the active word is actually off-screen. That brings the
  //     highlight onto a freshly-turned page, without a chunk arriving while paused
  //     yanking the page away from where the user is reading.
  useEffect(() => {
    if (Date.now() - lastManualScroll.current < SCROLL_GRACE_MS) return;
    const el = activeBoxRef.current;
    if (!el) return;
    const activeChanged = lastScrolledIndex.current !== activeIndex;
    lastScrolledIndex.current = activeIndex;
    const r = el.getBoundingClientRect();
    const offscreen = r.top < 0 || r.bottom > window.innerHeight;
    if (activeChanged || offscreen) el.scrollIntoView({ block: "center", behavior: "smooth" });
  }, [activeIndex, overlays]);

  return (
    <div className="pdf-reader" ref={containerRef}>
      {/* Tapping the page background toggles the chrome (immersive reading). */}
      <div className="pdf-stage" style={{ position: "relative" }} onClick={onToggleChrome}>
        <canvas ref={canvasRef} />
        <div className="word-overlay">
          {overlays.map((b) => (
            <div
              key={b.timelineIndex}
              ref={b.timelineIndex === activeIndex ? activeBoxRef : undefined}
              className={"word-box" + (b.timelineIndex === activeIndex ? " active" : "")}
              style={{ left: b.left, top: b.top, width: b.width, height: b.height }}
              onClick={(e) => {
                e.stopPropagation(); // don't also toggle chrome
                onJumpToWord(b.wordIndex);
              }}
            />
          ))}
        </div>
      </div>
    </div>
  );
}
