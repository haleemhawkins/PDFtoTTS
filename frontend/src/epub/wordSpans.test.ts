import { describe, expect, it } from "vitest";
import { collectWords, injectWordSpans } from "./wordSpans";

function body(html: string): HTMLElement {
  const doc = new DOMParser().parseFromString(`<body>${html}</body>`, "text/html");
  return doc.body;
}

describe("collectWords", () => {
  it("matches the server's per-text-node splitting", () => {
    // Same expectations as the C# EpubExtractor tests.
    expect(collectWords(body("<p>Hello <em>brave</em> new world.</p><p>Second paragraph.</p>")))
      .toEqual(["Hello", "brave", "new", "world.", "Second", "paragraph."]);
  });

  it("splits inline-adjacent text per node (no cross-element merge)", () => {
    expect(collectWords(body("<p>foo<em>bar</em>baz qux</p>"))).toEqual(["foo", "bar", "baz", "qux"]);
  });

  it("decodes entities (browser DOM)", () => {
    expect(collectWords(body("<p>Second &amp; paragraph.</p>"))).toEqual(["Second", "&", "paragraph."]);
  });
});

describe("injectWordSpans", () => {
  it("wraps every word with a sequential global index", () => {
    const el = body("<p>Hello <em>brave</em> new world.</p>");
    const next = injectWordSpans(el, 0);

    const spans = [...el.querySelectorAll(".epub-word")];
    expect(spans.map((s) => s.textContent)).toEqual(["Hello", "brave", "new", "world."]);
    expect(spans.map((s) => s.getAttribute("data-wi"))).toEqual(["0", "1", "2", "3"]);
    expect(next).toBe(4);
  });

  it("threads the index across sections", () => {
    const a = body("<p>one two</p>");
    const b = body("<p>three four five</p>");
    const afterA = injectWordSpans(a, 0);
    const afterB = injectWordSpans(b, afterA);

    expect(afterA).toBe(2);
    expect(afterB).toBe(5);
    expect(b.querySelector('.epub-word[data-wi="2"]')?.textContent).toBe("three");
    expect(b.querySelector('.epub-word[data-wi="4"]')?.textContent).toBe("five");
  });

  it("preserves the indices that match collectWords", () => {
    const html = "<p>Hello <em>brave</em> new world.</p>";
    const words = collectWords(body(html));
    const el = body(html);
    injectWordSpans(el, 0);
    words.forEach((w, i) => {
      expect(el.querySelector(`.epub-word[data-wi="${i}"]`)?.textContent).toBe(w);
    });
  });
});
