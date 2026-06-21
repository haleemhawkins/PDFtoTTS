import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MediaElementPlayer } from "./mediaElementPlayer";

/**
 * jsdom has no real media pipeline, so stub the element's transport and make
 * `currentTime` a controllable backing value. These tests pin the engine contract
 * `useReader` relies on: position from `currentTime`, seek/rate mapping, and the
 * event→callback wiring (notably that an OS-driven `pause` becomes an interruption
 * while our own pause() does not).
 */
let ct = 0;

beforeEach(() => {
  ct = 0;
  HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve());
  HTMLMediaElement.prototype.pause = vi.fn(function (this: HTMLMediaElement) {
    this.dispatchEvent(new Event("pause"));
  });
  HTMLMediaElement.prototype.load = vi.fn();
  Object.defineProperty(HTMLMediaElement.prototype, "currentTime", {
    configurable: true,
    get: () => ct,
    set: (v: number) => { ct = v; },
  });
});

afterEach(() => vi.restoreAllMocks());

describe("MediaElementPlayer", () => {
  it("reports position from the element's currentTime", () => {
    const p = new MediaElementPlayer();
    ct = 2.5;
    expect(p.currentMs()).toBeCloseTo(2500, 5);
  });

  it("seek maps global ms straight onto currentTime", () => {
    const p = new MediaElementPlayer();
    p.seek(3200);
    expect(p.currentMs()).toBeCloseTo(3200, 5);
  });

  it("continues in the background", () => {
    expect(new MediaElementPlayer().continuesInBackground).toBe(true);
  });

  it("our own pause() does NOT fire onInterrupted", () => {
    const p = new MediaElementPlayer();
    const interrupted = vi.fn();
    p.onInterrupted = interrupted;
    p.play();
    p.pause();
    expect(interrupted).not.toHaveBeenCalled();
  });

  it("an OS-driven pause (incoming call) fires onInterrupted", () => {
    const p = new MediaElementPlayer();
    const interrupted = vi.fn();
    p.onInterrupted = interrupted;
    p.play();
    // Simulate the OS pausing the element without us asking.
    (p as unknown as { audio: HTMLAudioElement }).audio.dispatchEvent(new Event("pause"));
    expect(interrupted).toHaveBeenCalledTimes(1);
  });

  it("maps readiness and underrun events to callbacks", () => {
    const p = new MediaElementPlayer();
    const ready = vi.fn();
    const underrun = vi.fn();
    p.onReady = ready;
    p.onUnderrun = underrun;
    const audio = (p as unknown as { audio: HTMLAudioElement }).audio;

    audio.dispatchEvent(new Event("canplay"));
    expect(ready).toHaveBeenCalled();

    p.play();
    audio.dispatchEvent(new Event("waiting"));
    expect(underrun).toHaveBeenCalled();
  });

  it("setRate drives the element playbackRate", () => {
    const p = new MediaElementPlayer();
    p.setRate(1.5);
    expect((p as unknown as { audio: HTMLAudioElement }).audio.playbackRate).toBe(1.5);
  });
});
