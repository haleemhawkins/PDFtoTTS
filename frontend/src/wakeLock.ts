/**
 * Keeps the mobile screen awake while the reader is actively reading, via the
 * Screen Wake Lock API (Android Chrome, iOS Safari 16.4+). No-ops gracefully
 * where the API is unavailable (older iOS, desktop Firefox) — there's simply
 * no lock and the screen dims as usual.
 *
 * The OS auto-releases the sentinel whenever the page is hidden (screen lock,
 * tab switch), so a held lock can vanish under us. We track intent (`wanted`)
 * separately and expose reacquire() for callers to re-request when the page
 * returns to the foreground.
 */
export class WakeLockManager {
  private sentinel: WakeLockSentinel | null = null;
  private wanted = false;

  /** Acquire (and remember we want) a screen wake lock. */
  async acquire(): Promise<void> {
    this.wanted = true;
    await this.request();
  }

  /** Release the lock and stop wanting one. */
  release(): void {
    this.wanted = false;
    const s = this.sentinel;
    this.sentinel = null;
    void s?.release().catch(() => {});
  }

  /** Re-request the lock if we still want one (e.g. after the page becomes
   *  visible again and the OS dropped it). No-ops when not wanted. */
  async reacquire(): Promise<void> {
    if (this.wanted) await this.request();
  }

  private async request(): Promise<void> {
    if (!("wakeLock" in navigator) || (this.sentinel && !this.sentinel.released)) return;
    try {
      const sentinel = await navigator.wakeLock.request("screen");
      // The OS can drop the lock on its own; clear our handle so reacquire()
      // knows to request a fresh one rather than assume the old one is live.
      sentinel.addEventListener("release", () => {
        if (this.sentinel === sentinel) this.sentinel = null;
      });
      this.sentinel = sentinel;
    } catch {
      // Request rejects when the page isn't visible or the OS declines (low
      // battery). Not an error for us — just carry on without a lock.
      this.sentinel = null;
    }
  }
}
