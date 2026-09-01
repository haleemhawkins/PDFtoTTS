# PDFtoTTS

Self-hosted PDF/EPUB reader that speaks the document and highlights the spoken
word. .NET 10 API + React frontend + three Python GPU workers over gRPC.

## Hard constraints

These are load-bearing. Breaking one produces a silent failure, not a build error.

- **WAV output is PCM_16, never float32.** Browser `decodeAudioData` fails
  silently on IEEE-float WAV, so the reader plays nothing at all. See
  `workers/kokoro-tts/worker/audio.py`.
- **Audio crosses gRPC as a path, never as bytes.** Workers write to the shared
  `/data` volume and return the relative path. Paths from a client must go
  through `shared/paths.py:resolve_under_data` so they cannot escape the data dir.
- **Alignment is forced alignment only.** The transcript is authoritative;
  WhisperX never re-transcribes and must never invent or drop a word.
- **EPUB words are split per text node.** The browser reproduces the global word
  index by walking the rendered DOM the same way, so changing the split silently
  misaligns every EPUB highlight.
- **The deployment is single-user and self-hosted. There is no auth.** Don't add
  ownership or permission checks that imply one exists.

## Hardware

Built for an AMD RX 7800 XT (gfx1101) on ROCm. The workers spoof it as the
supported gfx1100 via `HSA_OVERRIDE_GFX_VERSION=11.0.0`, already set in
`docker-compose.yml`. torch-rocm presents the GPU as `cuda`, so `device="cuda"`
in worker code is correct and is not a bug.

## What persists

Documents and extracted words persist as JSON on the shared volume
(`catalogue.json`, `words/{id}.json`) so the library and any OCR result survive a
restart. Sessions are in-memory and their audio is purged at startup — a session
is a disposable render, not a record. No SQLite; don't add one without changing
the spec first.

## Running things

```bash
DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test     # 104 tests; the env var is needed
                                                # because NuGet hangs preferring IPv6
cd frontend && npm test                          # vitest
cd workers/<worker> && pytest                    # GPU-free unit tests
```

No GPU? `USE_MOCK_WORKERS=true` swaps both workers for in-process fakes so the
whole pipeline runs. `USE_MOCK_TTS` / `USE_MOCK_ALIGNER` override individually.

The Playwright e2e suite runs against an already-running stack and hits the
nginx-served bundle on purpose — it catches failures the Vite dev server hides,
like `.mjs` MIME types and the float32 WAV decode above.

## Specs

OpenSpec, and the specs are meant to be true about the code, not aspirational.
`openspec/specs/<capability>/spec.md` are the living specs; changes under
`openspec/changes/` carry ADDED/MODIFIED deltas against them.

- `openspec validate` checks structure, not truth. It passes happily while a
  requirement lies about the code. When you change behaviour, change the spec.
- Archive a finished change with `openspec archive <name>`. Use `--skip-specs`
  when its deltas are already folded into the capability specs, or the sync will
  overwrite newer spec text with the change's stale copy.

## Conventions

- Comments explain why, not what. One line is usually enough.
- Don't commit or push unless asked.
