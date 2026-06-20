import { useEffect, useLayoutEffect, useMemo, useRef, useState, type MouseEvent } from "react";
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
  const [containerWidth, setContainerWidth] = useState(0);

  const SCROLL_GRACE_MS = 2500;

  // Report the visible page up so it can be persisted across reloads.
  useEffect(() => {
    onPageChange?.(page);
  }, [page, onPageChange]);

  // External navigation (a thumbnail / chapter pick): show that page. The seq bumps
  // even when re-picking the same page, so it always takes effect. Handled as a
  // guarded render-time transition (not an effect) to avoid cascading renders.
  const [prevGotoSeq, setPrevGotoSeq] = useState(gotoSeq);
  if (prevGotoSeq !== gotoSeq) {
    setPrevGotoSeq(gotoSeq);
    if (gotoSeq > 0) setPage(gotoPage);
  }

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

  // Overlay boxes for the current page, derived from the viewport + streamed words.
  // Memoized (not state+effect) so it recomputes only when those inputs change and
  // doesn't cascade a render. Cheap and independent of canvas rendering.
  const overlays = useMemo<OverlayBox[]>(() => {
    if (!viewport) return [];
    const pageW = viewport.width;
    const pageH = viewport.height;
    // PdfPig reports word boxes in the page's DISPLAYED (rotated-upright) frame with
    // the origin at the CropBox lower-left. pdf.js's convertToViewportRectangle wants
    // coordinates in UNROTATED PDF user space (it applies the page's /Rotate itself).
    // Map each box back to user space per the page rotation, then add the CropBox
    // origin — so the highlight sits on the words whether the page is cropped (a trim
    // margin) and/or rotated. For the common rotation==0 case this is just "+origin".
    const [X0, Y0, X1, Y1] = viewport.viewBox; // CropBox in unrotated user space
    const cw = X1 - X0, ch = Y1 - Y0;
    const rot = (((viewport.rotation ?? 0) % 360) + 360) % 360;
    const toUserSpace = (dx: number, dy: number): [number, number] => {
      switch (rot) {
        case 90: return [X0 + cw - dy, Y0 + dx];
        case 180: return [X0 + cw - dx, Y0 + ch - dy];
        case 270: return [X0 + dy, Y0 + ch - dx];
        default: return [X0 + dx, Y0 + dy];
      }
    };
    const boxes: OverlayBox[] = [];
    timeline.words.forEach((w, i) => {
      if (w.page !== page || !w.bbox) return;
      const [ax0, ay0] = toUserSpace(w.bbox.x, w.bbox.y);
      const [ax1, ay1] = toUserSpace(w.bbox.x + w.bbox.width, w.bbox.y + w.bbox.height);
      const r = viewport.convertToViewportRectangle([
        Math.min(ax0, ax1), Math.min(ay0, ay1), Math.max(ax0, ax1), Math.max(ay0, ay1),
      ]);
      // Clamp the box to the page rectangle so the highlight can never spill into
      // the margins around the page — e.g. a word whose bbox sits slightly outside
      // the mediabox (common in OCR'd text layers). Words fully off-page are dropped.
      const left = Math.max(0, Math.min(r[0], r[2], pageW));
      const top = Math.max(0, Math.min(r[1], r[3], pageH));
      const right = Math.min(pageW, Math.max(r[0], r[2], 0));
      const bottom = Math.min(pageH, Math.max(r[1], r[3], 0));
      const width = right - left;
      const height = bottom - top;
      if (width <= 0 || height <= 0) return; // fully outside the page
      boxes.push({ timelineIndex: i, wordIndex: w.wordIndex, left, top, width, height });
    });
    return boxes;
  }, [viewport, timeline, page]);

  // Follow the active word across pages during playback (auto page-turn). Keyed
  // on the active word's page TRANSITIONS only — deliberately NOT on `page` — so a
  // manual jump (Prev/Next/drawer) to a page the reader hasn't reached yet isn't
  // instantly snapped back to the active word's page (which made Next look stuck).
  const lastActivePage = useRef<number | null>(null);
  useEffect(() => {
    const ap = timeline.words[activeIndex]?.page;
    if (ap && ap !== lastActivePage.current) {
      lastActivePage.current = ap;
      setPage(ap);
    }
  }, [activeIndex, timeline]);

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

  // A tap on the page: seek to the nearest word if the tap is on or near one
  // (forgiving — word boxes are tiny touch targets), otherwise toggle the chrome.
  // This makes "tap a word to start reading" reliable without stealing taps in
  // genuinely empty space (margins, paragraph gaps), which still toggle.
  const onStageTap = (e: MouseEvent<HTMLDivElement>) => {
    e.stopPropagation(); // the stage decides; don't also fire .pdf-reader's toggle
    const rect = e.currentTarget.getBoundingClientRect();
    // Map screen px → overlay coordinate space (defensive against CSS scaling).
    const sx = viewport && rect.width ? viewport.width / rect.width : 1;
    const sy = viewport && rect.height ? viewport.height / rect.height : 1;
    const x = (e.clientX - rect.left) * sx;
    const y = (e.clientY - rect.top) * sy;

    let best: OverlayBox | null = null;
    let bestDist = Infinity;
    for (const b of overlays) {
      // Distance from the tap to the box (0 when inside it).
      const dx = Math.max(b.left - x, 0, x - (b.left + b.width));
      const dy = Math.max(b.top - y, 0, y - (b.top + b.height));
      const d = Math.hypot(dx, dy);
      if (d < bestDist) {
        bestDist = d;
        best = b;
      }
    }
    // Tolerance ~1.5 lines so a tap just off a word still reads it.
    if (best && bestDist <= Math.max(16, best.height * 1.5)) onJumpToWord(best.wordIndex);
    else onToggleChrome();
  };

  return (
    // Tapping the margins around the page toggles the chrome (immersive reading);
    // taps on the page itself are handled by onStageTap (nearest-word or toggle).
    <div className="pdf-reader" ref={containerRef} onClick={onToggleChrome}>
      <div className="pdf-stage" style={{ position: "relative" }} onClick={onStageTap}>
        <canvas ref={canvasRef} />
        <div className="word-overlay">
          {overlays.map((b) => (
            <div
              key={b.timelineIndex}
              ref={b.timelineIndex === activeIndex ? activeBoxRef : undefined}
              className={"word-box" + (b.timelineIndex === activeIndex ? " active" : "")}
              style={{ left: b.left, top: b.top, width: b.width, height: b.height }}
            />
          ))}
        </div>
      </div>
    </div>
  );
}
