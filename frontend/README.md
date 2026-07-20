# PDFtoTTS frontend

React + TypeScript + Vite app: the document library and the PDF/EPUB reader with
synchronized word highlighting. See the root [README](../README.md) for the full
architecture and how to run the whole stack.

```bash
npm install
npm run dev        # Vite dev server (proxies /api + /hubs to localhost:8080)
npm test           # vitest unit tests
npm run lint       # eslint
npm run build      # tsc -b && vite build

# e2e smoke test against an ALREADY-RUNNING stack (see root README):
npx playwright install chromium && npm run test:e2e
```

Layout highlights:

| Path | What |
|---|---|
| `src/components/` | LibraryView (home), PdfReader / EpubReader, shared chrome |
| `src/hooks/useReader.ts` | the reader state machine (upload → synthesize → play) |
| `src/audio/` | playback engines: Web Audio queue, iOS HLS media element |
| `src/sync/` | word timeline + active-word lookup (highlight sync) |
| `src/signalr/` | live chunk/progress stream from the API |
| `src/persist.ts` | per-document resume state + cover cache (localStorage) |
