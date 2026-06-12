/**
 * EPUB word-span injection (design §6.3). Mirrors the server's per-text-node
 * word splitting (PDFtoTTS.Ingestion.EpubExtractor) so the global word index is
 * reproducible in the browser: walk text nodes in document order, split each on
 * whitespace, and wrap each word in a `<span class="epub-word" data-wi="N">`.
 * The active word is then a simple `[data-wi]` lookup.
 */

const TEXT_NODE = 3;
const ELEMENT_NODE = 1;

/** Words of a subtree, in the same order/splitting the server uses. */
export function collectWords(root: Node): string[] {
  const out: string[] = [];
  const walk = (node: Node) => {
    node.childNodes.forEach((child) => {
      if (child.nodeType === TEXT_NODE) {
        for (const w of (child.textContent ?? "").split(/\s+/)) {
          if (w) out.push(w);
        }
      } else if (child.nodeType === ELEMENT_NODE) {
        walk(child);
      }
    });
  };
  walk(root);
  return out;
}

/**
 * Wrap each word under `root` in an addressable span, numbering from
 * `startIndex`. Returns the next free index so callers can thread a running
 * global index across spine sections.
 */
export function injectWordSpans(root: Element, startIndex: number): number {
  const doc = root.ownerDocument;
  const textNodes: Text[] = [];
  const collect = (node: Node) => {
    node.childNodes.forEach((child) => {
      if (child.nodeType === TEXT_NODE) textNodes.push(child as Text);
      else if (child.nodeType === ELEMENT_NODE) collect(child);
    });
  };
  collect(root);

  let index = startIndex;
  for (const node of textNodes) {
    const text = node.textContent ?? "";
    if (!text.trim()) continue;

    const fragment = doc.createDocumentFragment();
    for (const part of text.split(/(\s+)/)) {
      if (part === "") continue;
      if (/^\s+$/.test(part)) {
        fragment.appendChild(doc.createTextNode(part));
      } else {
        const span = doc.createElement("span");
        span.className = "epub-word";
        span.setAttribute("data-wi", String(index));
        span.textContent = part;
        fragment.appendChild(span);
        index += 1;
      }
    }
    node.parentNode?.replaceChild(fragment, node);
  }
  return index;
}
