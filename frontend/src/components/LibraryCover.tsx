import { useEffect, useRef, useState } from "react";
import * as pdfjsLib from "pdfjs-dist";
import ePub from "epubjs";
import type { DocumentInfo } from "../api/types";
import { PDFJS_ASSET_OPTS } from "../pdf/usePdfDocument";
import { getCachedCover, setCachedCover } from "../persist";

// Render width in device px (cards show it ~64 CSS px wide, so this is ~2× for
// crisp covers). Stored as a small WebP data URL in localStorage (see persist.ts),
// keyed by document id.
const COVER_W = 150;

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = reject;
    img.src = src;
  });
}

// Render page 1 of the PDF. `disableAutoFetch` keeps pdf.js from pulling the whole
// file — with the server's range support it fetches only the bytes page 1 needs,
// so a 300-page book costs a few hundred KB, not the entire document.
async function renderPdfCover(id: string): Promise<string | null> {
  const task = pdfjsLib.getDocument({
    url: `/api/documents/${id}/original`,
    disableAutoFetch: true,
    ...PDFJS_ASSET_OPTS,
  });
  try {
    const pdf = await task.promise;
    const page = await pdf.getPage(1);
    const base = page.getViewport({ scale: 1 });
    const vp = page.getViewport({ scale: COVER_W / base.width });
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(vp.width);
    canvas.height = Math.round(vp.height);
    const ctx = canvas.getContext("2d");
    if (!ctx) return null;
    ctx.fillStyle = "#fff"; // PDFs are transparent; paint a page behind them
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    await page.render({ canvasContext: ctx, viewport: vp, canvas }).promise;
    return canvas.toDataURL("image/webp", 0.7);
  } finally {
    void task.destroy();
  }
}

// Pull the EPUB's cover image (if it declares one) and downscale it.
async function renderEpubCover(id: string): Promise<string | null> {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const book: any = ePub(`/api/documents/${id}/original`);
  try {
    await book.ready;
    const url: string | null = await book.coverUrl();
    if (!url) return null;
    const img = await loadImage(url);
    URL.revokeObjectURL(url);
    const canvas = document.createElement("canvas");
    canvas.width = COVER_W;
    canvas.height = Math.round((img.height / img.width) * COVER_W);
    const ctx = canvas.getContext("2d");
    if (!ctx) return null;
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL("image/webp", 0.7);
  } finally {
    book.destroy?.();
  }
}

/**
 * A document's cover for the library card: page 1 of a PDF, or the EPUB's
 * declared cover image. Rendered lazily (only once the card scrolls into view)
 * and cached as a small data URL in localStorage, so it's a one-time cost per
 * document. Falls back to a neutral page glyph when there's no cover (or it
 * can't be rendered). The type badge overlays the corner either way.
 */
export function LibraryCover({ doc }: { doc: DocumentInfo }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [src, setSrc] = useState<string | null>(() => getCachedCover(doc.id));
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (src || failed || doc.status !== "Ready") return;
    const el = ref.current;
    if (!el) return;
    let cancelled = false;
    const io = new IntersectionObserver(
      (entries) => {
        if (!entries.some((e) => e.isIntersecting)) return;
        io.disconnect();
        void (async () => {
          try {
            const dataUrl =
              doc.type === "Epub" ? await renderEpubCover(doc.id) : await renderPdfCover(doc.id);
            if (cancelled) return;
            if (dataUrl) {
              setSrc(dataUrl);
              setCachedCover(doc.id, dataUrl);
            } else {
              setFailed(true);
            }
          } catch {
            if (!cancelled) setFailed(true);
          }
        })();
      },
      { rootMargin: "200px" },
    );
    io.observe(el);
    return () => {
      cancelled = true;
      io.disconnect();
    };
  }, [doc.id, doc.type, doc.status, src, failed]);

  const isEpub = doc.type === "Epub";
  return (
    <span className="library-cover" ref={ref}>
      {src ? (
        <img className="library-cover-img" src={src} alt="" loading="lazy" />
      ) : (
        <span className="library-cover-fallback" aria-hidden>
          {isEpub ? "📖" : "📄"}
        </span>
      )}
      <span className={"doc-type-badge " + (isEpub ? "epub" : "pdf")}>{isEpub ? "EPUB" : "PDF"}</span>
    </span>
  );
}
