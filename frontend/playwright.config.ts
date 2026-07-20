import { defineConfig, devices } from "@playwright/test";

/**
 * E2E smoke tests run against an ALREADY-RUNNING stack (they do not boot it).
 *   Real GPU/CPU stack:  docker compose up --build   -> http://localhost:5173
 *   Mock stack (no GPU):  see workers mock mode; point E2E_BASE_URL at it.
 * Override the target with E2E_BASE_URL (e.g. http://acearchlinux:5173).
 *
 * These deliberately hit the nginx-served production bundle, because several
 * real failures (e.g. .mjs MIME type, float32 WAV decode) only appear there and
 * not in the Vite dev server.
 */
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.spec.ts",
  timeout: 180_000, // synthesis of the first chunk can take ~15-20s on CPU
  expect: { timeout: 120_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:5173",
    trace: "retain-on-failure",
    // AudioContext autoplay must not require a gesture for the decode assertion.
    launchOptions: { args: ["--autoplay-policy=no-user-gesture-required"] },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
