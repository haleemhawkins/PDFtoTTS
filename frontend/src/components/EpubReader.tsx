import { useEffect, useRef } from "react";
import ePub from "epubjs";
import { collectWords, injectWordSpans } from "../epub/wordSpans";
import type { Timeline } from "../sync/wordTimeline";

interface Props {
  file: File;
  timeline: Timeline;
  activeIndex: number;
  onSeekToWord: (timelineIndex: number) => void;
}

interface Section {
  href: string;
  base: number; // global index of this section's first word
}

const SCROLL_GRACE_MS = 2500;

/**
 * Renders an EPUB with epub.js and keeps reading + view in sync (design §6.3/§6.7):
 * - injects addressable word spans (indices match the server, see wordSpans),
 * - auto-advances to the next section as playback crosses into it,
 * - seeks playback to a section's first word when the user navigates there,
 * - keeps the active word in view, backing off while the user scrolls.
 */
export function EpubReader({ file, timeline, activeIndex, onSeekToWord }: Props) {
  const viewerRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const renditionRef = useRef<any>(null);
  const sectionsRef = useRef<Section[]>([]);
  const currentHref = useRef<string>("");
  const programmaticNav = useRef(false);
  const lastManualScroll = useRef(0);
  // Global word index to seek to once it has streamed in (deferred navigation).
  const pendingBase = useRef<number | null>(null);
  const timelineRef = useRef(timeline);
  timelineRef.current = timeline;

  const sectionForWord = (globalWordIndex: number): Section | undefined => {
    let found: Section | undefined;
    for (const s of sectionsRef.current) {
      if (s.base <= globalWordIndex) found = s;
      else break;
    }
    return found;
  };

  const seekToSectionStart = (href: string) => {
    const section = sectionsRef.current.find((s) => s.href === href);
    if (!section) return;
    const ti = timelineRef.current.words.findIndex((w) => w.wordIndex >= section.base);
    if (ti >= 0) {
      onSeekToWord(ti);
      pendingBase.current = null;
    } else {
      pendingBase.current = section.base; // not synthesized yet; seek when ready
    }
  };

  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let book: any;

    (async () => {
      const buffer = await file.arrayBuffer();
      book = ePub(buffer);
      await book.ready;

      // Per-section base index = cumulative word count of earlier sections.
      const sections: Section[] = [];
      let running = 0;
      for (const item of book.spine.items) {
        sections.push({ href: item.href, base: running });
        try {
          const doc = await item.load(book.load.bind(book));
          running += collectWords(doc.body ?? doc).length;
          item.unload();
        } catch {
          /* skip unreadable section */
        }
      }
      if (cancelled || !viewerRef.current) return;
      sectionsRef.current = sections;

      const rendition = book.renderTo(viewerRef.current, {
        width: "100%",
        height: 600,
        flow: "scrolled-doc",
      });
      renditionRef.current = rendition;
      rendition.themes.default({
        ".epub-word.active": {
          background: "rgba(255, 213, 74, 0.45)",
          "box-shadow": "0 0 0 1px rgba(255, 213, 74, 0.7)",
        },
      });

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      rendition.hooks.content.register((contents: any) => {
        const href: string = contents.section?.href ?? "";
        const base = sections.find((s) => s.href === href)?.base ?? 0;
        injectWordSpans(contents.document.body, base);

        contents.document.body.addEventListener("click", (e: Event) => {
          const span = (e.target as HTMLElement).closest?.(".epub-word");
          if (!span) return;
          const wi = Number(span.getAttribute("data-wi"));
          const ti = timelineRef.current.words.findIndex((w) => w.wordIndex === wi);
          if (ti >= 0) onSeekToWord(ti);
        });

        const onManual = () => {
          lastManualScroll.current = Date.now();
        };
        contents.document.addEventListener("wheel", onManual, { passive: true });
        contents.document.addEventListener("touchmove", onManual, { passive: true });
      });

      // Track the displayed section. A section change the user caused (scroll or
      // nav) seeks playback there; one we caused (auto-advance) does not.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      rendition.on("relocated", (location: any) => {
        const newHref: string | undefined = location?.start?.href;
        if (!newHref) return;
        const changed = newHref !== currentHref.current;
        currentHref.current = newHref;
        if (programmaticNav.current) {
          programmaticNav.current = false;
          return;
        }
        if (changed) seekToSectionStart(newHref);
      });

      await rendition.display();
    })();

    return () => {
      cancelled = true;
      book?.destroy?.();
    };
  }, [file, onSeekToWord]);

  // Drive view from the active word: advance the section if needed, then
  // highlight + (grace-permitting) scroll the word into view.
  useEffect(() => {
    const word = timeline.words[activeIndex];
    const rendition = renditionRef.current;
    if (!word || !rendition) return;

    const highlight = () => {
      const contents = rendition.getContents();
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const list: any[] = Array.isArray(contents) ? contents : [contents];
      for (const c of list) {
        const doc: Document = c.document;
        doc.querySelectorAll(".epub-word.active").forEach((el) => el.classList.remove("active"));
        const span = doc.querySelector(`.epub-word[data-wi="${word.wordIndex}"]`);
        if (span) {
          span.classList.add("active");
          if (Date.now() - lastManualScroll.current >= SCROLL_GRACE_MS) {
            span.scrollIntoView({ block: "center", behavior: "smooth" });
          }
        }
      }
    };

    const section = sectionForWord(word.wordIndex);
    // Don't auto-advance while the user has jumped ahead to a not-yet-ready
    // section — that would yank the view back to the current reading position.
    if (pendingBase.current == null && section && section.href !== currentHref.current) {
      programmaticNav.current = true;
      rendition.display(section.href).then(highlight);
    } else {
      highlight();
    }
  }, [activeIndex, timeline]);

  // Resolve a deferred section jump once its words have streamed in.
  useEffect(() => {
    if (pendingBase.current == null) return;
    const ti = timeline.words.findIndex((w) => w.wordIndex >= pendingBase.current!);
    if (ti >= 0) {
      onSeekToWord(ti);
      pendingBase.current = null;
    }
  }, [timeline, onSeekToWord]);

  const navigate = (dir: "prev" | "next") => {
    const rendition = renditionRef.current;
    if (!rendition) return;
    // User navigation → "relocated" handler seeks playback to the new section.
    if (dir === "next") rendition.next();
    else rendition.prev();
  };

  return (
    <div className="epub-reader">
      <div className="epub-viewer" ref={viewerRef} />
      <div className="pdf-pager">
        <button onClick={() => navigate("prev")}>‹ Prev</button>
        <span>EPUB</span>
        <button onClick={() => navigate("next")}>Next ›</button>
      </div>
    </div>
  );
}
