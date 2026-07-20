import { useEffect, useState } from "react";
import * as pdfjsLib from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";

// One place to configure the pdf.js worker. Both the page renderer and the
// thumbnail/outline drawer share a SINGLE parsed document (this hook), rather
// than each parsing the file separately — important for memory on large books.
pdfjsLib.GlobalWorkerOptions.workerSrc = workerUrl;

// Runtime assets pdf.js loads on demand, served at /pdfjs/* (see the pdfjsAssets
// plugin in vite.config.ts). `wasmUrl` is the important one: scanned PDFs store
// each page as a JBIG2 image with an invisible OCR text layer; without the wasm
// decoder those pages render BLANK (text still extracts, so the highlight floats
// on an empty page). cMaps/standard fonts cover CID-keyed and non-embedded fonts.
export const PDFJS_ASSET_OPTS = {
  cMapUrl: "/pdfjs/cmaps/",
  cMapPacked: true,
  standardFontDataUrl: "/pdfjs/standard_fonts/",
  wasmUrl: "/pdfjs/wasm/",
} as const;

export type PdfDoc = pdfjsLib.PDFDocumentProxy;

/** Load (and own the lifecycle of) a pdf.js document for the given file. Returns
 *  null until ready, or when disabled (e.g. the file is an EPUB). */
export function usePdfDocument(file: File | null, enabled: boolean): PdfDoc | null {
  const [pdf, setPdf] = useState<PdfDoc | null>(null);

  useEffect(() => {
    // No reset needed here: on any deps change the previous run's cleanup already
    // ran setPdf(null) (and the initial state is null), so a synchronous reset in
    // the effect body would be redundant — and it cascades renders.
    if (!file || !enabled) return;
    let cancelled = false;
    // Keep the loading task so we can release the document (and its worker
    // resources) on cleanup — destroy() lives on the task, not the proxy.
    let task: pdfjsLib.PDFDocumentLoadingTask | null = null;
    file.arrayBuffer().then(async (buf) => {
      task = pdfjsLib.getDocument({ data: buf, ...PDFJS_ASSET_OPTS });
      const loaded = await task.promise;
      if (cancelled) {
        void task.destroy();
        return;
      }
      setPdf(loaded);
    });
    return () => {
      cancelled = true;
      setPdf(null);
      void task?.destroy();
    };
  }, [file, enabled]);

  return pdf;
}
