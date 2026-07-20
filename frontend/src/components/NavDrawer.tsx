import { useEffect, useRef, useState } from "react";
import type { PdfDoc } from "../pdf/usePdfDocument";

interface Props {
  open: boolean;
  pdf: PdfDoc;
  numPages: number;
  currentPage: number;
  onNavigate: (page: number) => void;
  onClose: () => void;
}

interface OutlineEntry {
  title: string;
  depth: number;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  dest: any;
}

// Flatten pdf.js's nested outline into an indented list we can render flatly.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function flattenOutline(items: any[], depth: number, out: OutlineEntry[]) {
  for (const it of items) {
    out.push({ title: it.title ?? "Untitled", depth, dest: it.dest });
    if (it.items?.length) flattenOutline(it.items, depth + 1, out);
  }
}

/** Slide-out navigation: a Pages thumbnail grid and a Chapters (PDF outline)
 *  list. Selecting either navigates the reader to that page and closes. */
export function NavDrawer({ open, pdf, numPages, currentPage, onNavigate, onClose }: Props) {
  const [tab, setTab] = useState<"pages" | "chapters">("chapters");
  const [outline, setOutline] = useState<OutlineEntry[] | null>(null);

  // Load the chapter outline once the drawer is first opened.
  useEffect(() => {
    if (!open || outline !== null) return;
    let cancelled = false;
    pdf.getOutline().then((items) => {
      if (cancelled) return;
      const flat: OutlineEntry[] = [];
      if (items?.length) flattenOutline(items, 0, flat);
      setOutline(flat);
      // If there are no chapters, default to the Pages tab.
      if (flat.length === 0) setTab("pages");
    });
    return () => {
      cancelled = true;
    };
  }, [open, outline, pdf]);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const go = async (dest: any) => {
    const page = await destToPage(pdf, dest);
    if (page) onNavigate(page);
  };

  return (
    <>
      <div className={"drawer-scrim" + (open ? " open" : "")} onClick={onClose} />
      <aside className={"nav-drawer" + (open ? " open" : "")} aria-hidden={!open}>
        <div className="nav-drawer-head">
          <div className="nav-tabs">
            <button
              className={tab === "chapters" ? "active" : ""}
              onClick={() => setTab("chapters")}
            >
              Chapters
            </button>
            <button className={tab === "pages" ? "active" : ""} onClick={() => setTab("pages")}>
              Pages
            </button>
          </div>
          <button className="nav-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        <div className="nav-drawer-scroll">
          {tab === "chapters" ? (
            outline === null ? (
              <p className="nav-empty">Loading…</p>
            ) : outline.length === 0 ? (
              <p className="nav-empty">This document has no chapter list. Try the Pages tab.</p>
            ) : (
              <ul className="chapter-list">
                {outline.map((e, i) => (
                  <li key={i}>
                    <button style={{ paddingLeft: `${0.8 + e.depth * 1}rem` }} onClick={() => go(e.dest)}>
                      {e.title}
                    </button>
                  </li>
                ))}
              </ul>
            )
          ) : (
            <div className="thumb-grid">
              {Array.from({ length: numPages }, (_, i) => i + 1).map((p) => (
                <PageThumb
                  key={p}
                  pdf={pdf}
                  page={p}
                  active={p === currentPage}
                  onClick={() => onNavigate(p)}
                />
              ))}
            </div>
          )}
        </div>
      </aside>
    </>
  );
}

// Resolve a pdf.js outline destination (named or explicit) to a 1-based page.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function destToPage(pdf: PdfDoc, dest: any): Promise<number | null> {
  try {
    const explicit = typeof dest === "string" ? await pdf.getDestination(dest) : dest;
    const ref = Array.isArray(explicit) ? explicit[0] : null;
    if (!ref) return null;
    const index = await pdf.getPageIndex(ref);
    return index + 1;
  } catch {
    return null;
  }
}

/** A single page thumbnail, rendered lazily only once it scrolls into view so a
 *  several-hundred-page book doesn't render every page up front. */
function PageThumb({
  pdf,
  page,
  active,
  onClick,
}: {
  pdf: PdfDoc;
  page: number;
  active: boolean;
  onClick: () => void;
}) {
  const btnRef = useRef<HTMLButtonElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [visible, setVisible] = useState(false);

  // Reveal-on-scroll: render the thumbnail when it (nearly) enters the viewport.
  useEffect(() => {
    const el = btnRef.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          io.disconnect();
        }
      },
      { root: el.closest(".nav-drawer-scroll"), rootMargin: "300px" },
    );
    io.observe(el);
    return () => io.disconnect();
  }, []);

  useEffect(() => {
    if (!visible) return;
    let task: { cancel?: () => void; promise: Promise<void> } | null = null;
    (async () => {
      const p = await pdf.getPage(page);
      const base = p.getViewport({ scale: 1 });
      const scale = 130 / base.width;
      const vp = p.getViewport({ scale });
      const canvas = canvasRef.current;
      if (!canvas) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.round(vp.width * dpr);
      canvas.height = Math.round(vp.height * dpr);
      canvas.style.width = `${vp.width}px`;
      canvas.style.height = `${vp.height}px`;
      const ctx = canvas.getContext("2d");
      if (!ctx) return;
      const renderVp = dpr === 1 ? vp : p.getViewport({ scale: scale * dpr });
      task = p.render({ canvasContext: ctx, viewport: renderVp, canvas });
      try {
        await task.promise;
      } catch {
        /* render cancelled (drawer closed / re-rendered) */
      }
    })();
    return () => task?.cancel?.();
  }, [visible, pdf, page]);

  return (
    <button ref={btnRef} className={"thumb" + (active ? " active" : "")} onClick={onClick}>
      <canvas ref={canvasRef} />
      <span className="thumb-num">{page}</span>
    </button>
  );
}
