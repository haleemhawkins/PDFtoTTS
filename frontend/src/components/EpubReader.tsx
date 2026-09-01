import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from "react";
import { EpubView } from "react-reader";
import ePub from "epubjs";
import type { NavItem, Rendition } from "epubjs";
import { collectWords, injectWordSpans } from "../epub/wordSpans";
import type { Timeline } from "../sync/wordTimeline";

/** A flattened table-of-contents entry for the app's chapters drawer. */
export interface TocItem {
  label: string;
  href: string;
  depth: number;
}

/** Imperative nav surface so the app's shared chrome (pager + chapters drawer)
 *  drives the EPUB exactly like it drives the PDF. */
export interface EpubReaderHandle {
  next: () => void;
  prev: () => void;
  display: (href: string) => void;
}

interface Props {
  file: File;
  timeline: Timeline;
  activeIndex: number;
  /** Tap a word → start reading there (PDF parity with tap-to-read). Takes the
   *  SOURCE word index: if that word is already synthesized the reader seeks to
   *  it; otherwise it cancels and re-synthesizes from there — so tapping a word
   *  outside the current session's timeline (before its start word, or ahead of
   *  synthesis) jumps there instead of being silently ignored. */
  onJumpToWord: (sourceWordIndex: number) => void;
  /** Navigate to a section's first word (Prev/Next/chapter/swipe): position +
   *  preload but stay PAUSED, exactly like the PDF pager. Takes a SOURCE word
   *  index (the section's global base), not a timeline index. */
  onNavigate: (sourceWordIndex: number) => void;
  /** Tap on the page background (not a word) toggles the chrome — PDF parity. */
  onToggleChrome: () => void;
  /** Surfaced so the app can render a chapters drawer (the EPUB's ☰ menu). */
  onToc?: (toc: TocItem[]) => void;
  /** Current chapter label, for the pager's center text. */
  onChapter?: (label: string) => void;
}

interface Section {
  href: string;
  base: number; // global index of this section's first word
}

/** Path part of an href (drop any #anchor) for tolerant TOC ↔ section matching. */
const hrefPath = (h: string) => h.split("#")[0];

function flattenToc(items: NavItem[], depth: number, out: TocItem[]) {
  for (const it of items) {
    out.push({ label: it.label?.trim() || "Untitled", href: it.href, depth });
    if (it.subitems?.length) flattenToc(it.subitems, depth + 1, out);
  }
}

/**
 * EPUB reader built on react-reader's bare <EpubView> (no built-in chrome), with
 * TTS word-sync layered on top, so it matches the PDF reader's UX — the app's
 * shared chrome (play/seek/menu) plus a bottom pager and chapters drawer drive it
 * (design §6.3/§6.7). Specifically it:
 * - injects addressable word spans whose indices match the server (see wordSpans),
 * - highlights the active word by toggling .tts-active on its span (styled via the
 *   rendition theme), advancing the section as playback crosses into it,
 * - seeks playback to a section's first word when the user navigates there,
 * - toggles the chrome on a background tap and seeks on a word tap (PDF parity).
 */
export const EpubReader = forwardRef<EpubReaderHandle, Props>(function EpubReader(
  { file, timeline, activeIndex, onJumpToWord, onNavigate, onToggleChrome, onToc, onChapter },
  ref,
) {
  const [buffer, setBuffer] = useState<ArrayBuffer | null>(null);
  // Section global bases must be ready before EpubView mounts — the content
  // hook injects word spans with those bases, and a `?? 0` fallback while the
  // spine scan is still running collides every chapter onto base 0.
  const [sectionsReady, setSectionsReady] = useState(false);
  const [location, setLocation] = useState<string | number | null>(null);

  const rootRef = useRef<HTMLDivElement>(null);
  const renditionRef = useRef<Rendition | null>(null);
  const sectionsRef = useRef<Section[]>([]);
  const tocRef = useRef<TocItem[]>([]);
  const currentHref = useRef<string>("");
  const programmaticNav = useRef(false);
  // The very first `relocated` is epub.js displaying the opening section, not a
  // user navigation — it must NOT reposition/seek playback (that auto-started the
  // voice on load). Genuine navigations flip this true.
  const navInitialized = useRef(false);
  // The currently-highlighted word span, so we can clear its class before the next.
  const lastHighlightedSpan = useRef<HTMLElement | null>(null);
  // Latest timeline / callbacks for use inside epub.js event handlers (which
  // fire asynchronously, so updating these in effects is timely enough).
  const timelineRef = useRef(timeline);
  const onJumpRef = useRef(onJumpToWord);
  const onNavigateRef = useRef(onNavigate);
  const onToggleRef = useRef(onToggleChrome);
  const onChapterRef = useRef(onChapter);
  useEffect(() => {
    timelineRef.current = timeline;
  }, [timeline]);
  useEffect(() => {
    onJumpRef.current = onJumpToWord;
  }, [onJumpToWord]);
  useEffect(() => {
    onNavigateRef.current = onNavigate;
  }, [onNavigate]);
  useEffect(() => {
    onToggleRef.current = onToggleChrome;
  }, [onToggleChrome]);
  useEffect(() => {
    onChapterRef.current = onChapter;
  }, [onChapter]);

  // epub.js (scrolled-doc flow) renders the section into a full-height iframe inside
  // a scrollable `.epub-container`; THAT element is what scrolls, so we page through
  // it directly.
  const scrollContainer = useCallback(
    () => rootRef.current?.querySelector<HTMLElement>(".epub-container") ?? null,
    [],
  );

  // The first addressable word whose top sits within the container's visible band,
  // in document order — so a page turn can reposition the (paused) audio to where
  // reading would resume (PDF pager parity: a page flip moves the audio there).
  const firstVisibleWordIndex = useCallback((): number | null => {
    const rendition = renditionRef.current;
    const container = scrollContainer();
    if (!rendition || !container) return null;
    const cRect = container.getBoundingClientRect();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const raw = (rendition as any).getContents();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const list: any[] = Array.isArray(raw) ? raw : [raw];
    let best: { wi: number; top: number } | null = null;
    for (const c of list) {
      const ifr: HTMLIFrameElement | undefined = c.iframe ?? c.document?.defaultView?.frameElement;
      const iframeTop = ifr?.getBoundingClientRect().top ?? cRect.top;
      const spans = c.document.querySelectorAll(".epub-word");
      for (const s of spans as Iterable<HTMLElement>) {
        const top = iframeTop + s.getBoundingClientRect().top;
        if (top >= cRect.top - 2 && top <= cRect.bottom) {
          best = { wi: Number(s.getAttribute("data-wi")), top };
          break; // spans are in document order; the first in-band one wins
        }
      }
      if (best) break;
    }
    return best?.wi ?? null;
  }, [scrollContainer]);

  // Turn one "page": scroll the container by ~90% of its height (PDF-like), and at
  // the top/bottom boundary cross into the adjacent spine section. After a within-
  // section scroll, move the paused audio to the first now-visible word.
  const pageBy = useCallback((dir: 1 | -1) => {
    const container = scrollContainer();
    const rendition = renditionRef.current;
    if (!container || !rendition) return;
    const max = container.scrollHeight - container.clientHeight;
    const step = Math.max(120, container.clientHeight * 0.9);
    const target = Math.min(max, Math.max(0, container.scrollTop + dir * step));
    if (Math.abs(target - container.scrollTop) >= 4) {
      container.scrollTo({ top: target, behavior: "smooth" });
      window.setTimeout(() => {
        const wi = firstVisibleWordIndex();
        if (wi != null) onNavigateRef.current(wi);
      }, 350);
    } else {
      // At the section edge — cross into the next/prev section (the relocated
      // handler repositions the audio to that section's start).
      void (dir > 0 ? rendition.next() : rendition.prev());
    }
  }, [scrollContainer, firstVisibleWordIndex]);

  useImperativeHandle(ref, () => ({
    next: () => pageBy(1),
    prev: () => pageBy(-1),
    display: (href: string) => {
      programmaticNav.current = false; // a chapter pick SHOULD reposition playback there
      void renditionRef.current?.display(href);
    },
  }), [pageBy]);

  const sectionForWord = (globalWordIndex: number): Section | undefined => {
    let found: Section | undefined;
    for (const s of sectionsRef.current) {
      if (s.base <= globalWordIndex) found = s;
      else break;
    }
    return found;
  };

  // A user-driven section change (Prev/Next/chapter/swipe) positions playback at
  // that section's first word, PAUSED — exactly like the PDF pager. The reader
  // preloads the audio if the section hasn't been synthesized yet; reading begins
  // only on an explicit Play tap.
  const navigateToSection = useCallback((href: string) => {
    const section = sectionsRef.current.find((s) => s.href === href);
    if (!section) return;
    onNavigateRef.current(section.base);
  }, []);

  // Load the EPUB once. EpubView takes the ArrayBuffer as its url.
  useEffect(() => {
    let cancelled = false;
    setSectionsReady(false);
    sectionsRef.current = [];
    setBuffer(null);
    file.arrayBuffer().then((buf) => {
      if (!cancelled) setBuffer(buf);
    });
    return () => {
      cancelled = true;
    };
  }, [file]);

  // Per-section base index = cumulative word count of earlier sections. Computed
  // from a throwaway Book (a copy of the buffer) so counting never loads/unloads
  // the sections the rendition is actively displaying. EpubView mounts only after
  // this finishes (sectionsReady), so injectWordSpans never falls back to base 0.
  useEffect(() => {
    if (!buffer) return;
    let cancelled = false;
    setSectionsReady(false);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let book: any;
    (async () => {
      book = ePub(buffer.slice(0));
      // Wait for the FULL open (incl. the archive `replacements` chain), not just
      // `ready`: destroying while open() is still settling races epub.js into a
      // "reading 'displayOptions'" throw it logs to the console.
      await book.opened;
      if (cancelled) return;
      const sections: Section[] = [];
      let running = 0;
      // Iterate `spineItems` — the real epub.js Section objects (with `.href` and
      // `.load()`). `spine.items` is the raw package manifest data (no href/load),
      // which silently produced href=undefined/base=0 for EVERY section, breaking
      // section↔word mapping (highlight jumped to the last chapter, chapter nav
      // failed, word indices collided).
      for (const item of book.spine.spineItems) {
        sections.push({ href: item.href, base: running });
        try {
          // section.load() resolves to the section's documentElement (<html>), so
          // reach into its <body> for the word count — matching injectWordSpans,
          // which numbers each section's spans from this same base.
          const html = await item.load(book.load.bind(book));
          const body = (html as Element).querySelector?.("body") ?? html;
          running += collectWords(body).length;
          item.unload();
        } catch {
          /* skip unreadable section */
        }
      }
      if (!cancelled) {
        sectionsRef.current = sections;
        setSectionsReady(true);
      }
      book.destroy?.();
    })();
    return () => {
      cancelled = true;
      book?.destroy?.();
    };
  }, [buffer]);

  // EpubView hands us the epub.js rendition; wire span injection, tap handling,
  // and user-navigation → playback-seek onto it.
  const getRendition = useCallback((rendition: Rendition) => {
    renditionRef.current = rendition;

    // Accessibility: epub.js renders each section in a transparent iframe, so a
    // book that doesn't set its own colors shows its default black text over the
    // app's dark background — black-on-black. Force a readable light "page" (like
    // the PDF canvas) and dark text, overriding the book's CSS so it's legible
    // regardless of how the EPUB was authored.
    rendition.themes.default({
      "html, body": {
        background: "#ffffff !important",
        color: "#1a1a1a !important",
      },
      "p, li, dd, dt, blockquote, td, th, figcaption, h1, h2, h3, h4, h5, h6, span, div, em, strong, i, b, small, sub, sup":
        { color: "#1a1a1a !important" },
      a: { color: "#1a4fc0 !important" },
      // The active word is highlighted by toggling .tts-active on its injected
      // span (see highlightWord) rather than via epub.js's CFI annotations API,
      // whose marks-pane overlay only painted the word's first character. A span
      // background covers the whole word and wraps cleanly across a line break.
      ".epub-word.tts-active": {
        background: "#ffd54a !important",
        "border-radius": "2px",
        "box-decoration-break": "clone",
        "-webkit-box-decoration-break": "clone",
      },
    });

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (rendition as any).hooks.content.register((contents: any) => {
      // epub.js fires this with a Contents object that exposes only `sectionIndex`
      // (the spine position) — NOT a `.section`/`.href`. sectionsRef is built in
      // spine order, so resolve the section (and its global word base) by index.
      // EpubView only mounts after sectionsReady, so a missing section is a real
      // mismatch (not a race) — skip injection rather than defaulting to base 0.
      const section = sectionsRef.current[contents.sectionIndex];
      if (!section) return;
      injectWordSpans(contents.document.body, section.base);

      // PDF parity: tap a word → read from there; tap the background → toggle
      // chrome. The SOURCE index goes up as-is — jumpToWord seeks when the word
      // is already synthesized and re-synthesizes from it when it isn't, so a
      // tap outside the current session's timeline (before its start word, or
      // ahead of synthesis) jumps there instead of being silently dropped
      // (which left the voice reading on from wherever it already was).
      contents.document.body.addEventListener("click", (e: Event) => {
        const span = (e.target as HTMLElement).closest?.(".epub-word");
        if (span) {
          const wi = Number(span.getAttribute("data-wi"));
          if (Number.isInteger(wi)) onJumpRef.current(wi);
        } else {
          onToggleRef.current();
        }
      });
    });

    // A section change the user caused (swipe / chapter pick / scroll) seeks
    // playback there; one we caused (auto-advance) does not.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    rendition.on("relocated", (loc: any) => {
      const newHref: string | undefined = loc?.start?.href;
      if (!newHref) return;
      const changed = newHref !== currentHref.current;
      currentHref.current = newHref;
      // Report the chapter for the pager's center label.
      const chapter = tocRef.current.find((t) => hrefPath(t.href) === hrefPath(newHref))
        ?? tocRef.current.find((t) => hrefPath(newHref).endsWith(hrefPath(t.href)));
      onChapterRef.current?.(chapter?.label ?? "");
      // The opening display is not a navigation — don't seek/repositon (which used
      // to auto-start the voice on load). Subsequent relocations are real.
      if (!navInitialized.current) {
        navInitialized.current = true;
        return;
      }
      // Auto-advance (playback crossing into the next section) is ours, not the
      // user's — it must not reposition playback.
      if (programmaticNav.current) {
        programmaticNav.current = false;
        return;
      }
      if (changed) navigateToSection(newHref);
    });
  }, [navigateToSection]);

  const onTocChanged = useCallback((nav: NavItem[]) => {
    const flat: TocItem[] = [];
    flattenToc(nav, 0, flat);
    tocRef.current = flat;
    onToc?.(flat);
  }, [onToc]);

  // Highlight the active word by toggling .tts-active on its injected span (styled
  // by the theme above), scrolling it into view whenever the word changes.
  // We style the span directly rather than via epub.js's annotations API: its CFI
  // marks-pane overlay resolved a single-word range to just the first character,
  // so only the word's first letter was highlighted. A span background is exact and
  // wraps across line breaks for free.
  const highlightWord = useCallback((wordIndex: number) => {
    const rendition = renditionRef.current;
    if (!rendition) return;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const contents = (rendition as any).getContents();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const list: any[] = Array.isArray(contents) ? contents : [contents];
    let target: HTMLElement | null = null;
    for (const c of list) {
      const doc: Document = c.document;
      const found = doc.querySelector<HTMLElement>(`.epub-word[data-wi="${wordIndex}"]`);
      if (found) {
        target = found;
        break;
      }
    }
    if (!target) return;

    if (lastHighlightedSpan.current === target) return; // already highlighted
    lastHighlightedSpan.current?.classList.remove("tts-active");
    target.classList.add("tts-active");
    lastHighlightedSpan.current = target;

    // The dedupe above means we only reach here on a NEW word, so during playback
    // this is the highlight advancing — always snap the reader back to the spoken
    // word, following the voice even if the user scrolled away.
    target.scrollIntoView({ block: "center", behavior: "smooth" });
  }, []);

  // Drive the view from the active word: advance the section if needed, then
  // highlight and scroll it into view. When navigation has set activeIndex to -1
  // (jumped to a not-yet-synthesized section) there's no active word, so this
  // no-ops and never yanks the view back.
  useEffect(() => {
    const word = timeline.words[activeIndex];
    const rendition = renditionRef.current;
    if (!word || !rendition) return;

    const section = sectionForWord(word.wordIndex);
    // Show the section the active word lives in if it isn't already on screen
    // (playback crossing a section boundary, or a navigation that set activeIndex).
    if (section && section.href !== currentHref.current) {
      programmaticNav.current = true;
      rendition.display(section.href).then(() => highlightWord(word.wordIndex));
    } else {
      highlightWord(word.wordIndex);
    }
  }, [activeIndex, timeline, highlightWord]);

  return (
    <div className="epub-reader" ref={rootRef}>
      {buffer && sectionsReady && (
        <EpubView
          url={buffer}
          location={location}
          locationChanged={(loc) => setLocation(loc)}
          getRendition={getRendition}
          tocChanged={onTocChanged}
          epubOptions={{ flow: "scrolled-doc", allowScriptedContent: true }}
        />
      )}
    </div>
  );
});
