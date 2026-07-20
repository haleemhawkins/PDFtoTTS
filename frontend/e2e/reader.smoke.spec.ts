import { test, expect } from "@playwright/test";
import { fileURLToPath } from "node:url";

const SAMPLE_PDF = fileURLToPath(new URL("./fixtures/sample.pdf", import.meta.url));

/**
 * End-to-end smoke test of the whole reader pipeline against a running stack:
 * upload -> synthesize -> SignalR delivery -> playback -> highlight. Each
 * assertion guards a real bug class that unit tests could not catch:
 *  - PDF renders with word overlays  -> nginx .mjs MIME / pdf.js worker loading
 *  - a chunk's audio decodeAudioData -> 16-bit-PCM-vs-float32 WAV format
 *  - progress / chunk delivery       -> SignalR hub reaching the browser
 *  - no console errors               -> bundle/runtime regressions
 */
test("uploads a PDF, synthesizes, plays, and decodes audio", async ({ page }) => {
  const consoleErrors: string[] = [];
  page.on("console", (m) => m.type() === "error" && consoleErrors.push(m.text()));
  page.on("pageerror", (e) => consoleErrors.push(`pageerror: ${e.message}`));

  await page.goto("/", { waitUntil: "load" });

  await page.setInputFiles('input[type=file]', SAMPLE_PDF);
  await page.getByRole("button", { name: /start reading/i }).click();

  // The reader view appears (playback bar present) regardless of synthesis speed.
  await expect(page.locator(".state-chip")).toBeVisible();

  // The app must actually fetch a chunk's audio over SignalR + REST. This proves
  // synthesis ran AND the ChunkReady event reached the browser.
  await expect
    .poll(
      () =>
        page.evaluate(() =>
          performance
            .getEntriesByType("resource")
            .some((r) => /\/chunks\/\d+\/audio/.test(r.name)),
        ),
      { message: "no chunk audio was fetched (synthesis or SignalR delivery failed)" },
    )
    .toBe(true);

  // The PDF rendered with word overlays (pdf.js worker loaded -> .mjs MIME ok).
  await expect.poll(() => page.locator(".word-box").count()).toBeGreaterThan(0);

  // The fetched chunk audio must DECODE in the browser. This is the regression
  // guard for the float32-WAV bug: decodeAudioData rejects IEEE-float, so audio
  // silently never played. PCM_16 decodes.
  const decode = await page.evaluate(async () => {
    const url = performance
      .getEntriesByType("resource")
      .map((r) => r.name)
      .find((u) => /\/chunks\/\d+\/audio/.test(u));
    if (!url) return { ok: false, reason: "no chunk url" };
    try {
      const buf = await fetch(url).then((r) => r.arrayBuffer());
      const audio = await new AudioContext().decodeAudioData(buf);
      return { ok: true, seconds: audio.duration };
    } catch (e) {
      return { ok: false, reason: String(e) };
    }
  });
  expect(decode.ok, `audio failed to decode: ${decode.reason}`).toBe(true);
  expect(decode.seconds).toBeGreaterThan(0);

  // Pressing play advances into a playing state (the player started a source).
  await page.getByRole("button", { name: /play/i }).click();
  await expect(page.locator(".state-chip")).toHaveText(/playing|processing/);

  expect(consoleErrors, `console errors: ${consoleErrors.join(" | ")}`).toEqual([]);
});
