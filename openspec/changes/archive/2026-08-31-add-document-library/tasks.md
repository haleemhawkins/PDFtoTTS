## 1. Backend persistence

- [x] 1.1 Extend `IFileStorage`/`LocalFileStorage` with catalogue + words persistence: write/read `catalogue.json`, `words/{id}.json`, `OriginalFullPath(id, type)`, and `DeleteOriginalAndWords(id)`. Atomic writes (temp file + move) behind a lock.
- [x] 1.2 Add `PersistentDocumentStore` (replacing `InMemoryDocumentStore` in DI) that loads the manifest on construction, exposes `All()` (most-recent-first), `Rename(id, name)`, `Remove(id)`, and writes through `Add`/`SetExtraction`/`Rename`/`Remove`. Throttle `SetProgress` writes (in-memory only; persist on terminal status).
- [x] 1.3 Startup reconciliation: drop catalogue entries whose original file is missing; demote a `Ready` doc with no words file to `Error`.

## 2. Backend CRUD endpoints

- [x] 2.1 `GET /api/documents` → list of `Document` (most-recent-first).
- [x] 2.2 `GET /api/documents/{id}/original` → stream the original with `application/pdf` or `application/epub+zip`; 404 when missing.
- [x] 2.3 `PATCH /api/documents/{id}` with `{ name }` → rename (reject blank with 400, 404 unknown), return updated `Document`; add `RenameDocumentRequest` to `Dtos.cs`.
- [x] 2.4 `DELETE /api/documents/{id}` → cancel sessions via `ISessionStore.ForDocument`, delete their audio, delete original + words, remove from catalogue; 204 on success, 404 unknown.
- [x] 2.5 Backend tests (WebApplicationFactory): list/rename/delete happy paths + 400/404 cases; persistence reload across a fresh store instance; delete cascades to session audio.

## 3. Frontend API + state

- [x] 3.1 Add client calls in `api/client.ts`: `listDocuments`, `renameDocument`, `deleteDocument`, `getOriginal` (returns a `File`/`Blob`), and lift `getVoices`.
- [x] 3.2 Rework `persist.ts` into a per-document map (`{ [id]: { page, word, voice, speed } }`) + `lastOpenedId`; best-effort clear of the legacy single-slot IndexedDB cache on first run.
- [x] 3.3 Add `reader.open(documentId, voice, speed, startPage?, startWord?)` to `useReader` that fetches original bytes + words from the server and opens a session (reuse `openSession`); keep `start(file, …)` for fresh uploads.
- [x] 3.4 Add `reader.changeVoice(voice)` to `useReader` (set `voiceRef`, `restartAt(currentSourceWord, speedRef.current)`).

## 4. Frontend library + reader UI

- [x] 4.1 New `LibraryView` component: fetch `listDocuments`, render cards (name · type · status), open on click, rename + delete actions (with confirm), and an upload control (reuse file input + voice/speed). Empty state invites upload.
- [x] 4.2 Rework `App.tsx` navigation: `view: "library" | "reader"`; upload → add + open; open-by-id from the library; Home returns to the library (not full reset); restore `lastOpenedId` on load instead of the IndexedDB file.
- [x] 4.3 Add a voice `<select>` to `ReaderChrome` (voices passed from `App`), wired to `reader.changeVoice` and persisted per document.
- [x] 4.4 Styling for the library grid/cards and the voice picker in `App.css`.

## 5. Verification

- [x] 5.1 `dotnet build` + backend tests green; `npm run build` (tsc) + `vitest` green; lint clean.
- [x] 5.2 Mock run against a live API (`USE_MOCK_WORKERS=true`), over HTTP rather than the browser: uploaded two PDFs and saw both listed most-recent-first; renamed one (blank name -> 400); deleted the other mid-synthesis and confirmed its audio dir, original, and words file were removed and a repeat DELETE returned 404; restarted the API against the same `DATA_DIR` and the renamed document came back Ready with its 16 words and no re-extraction; created a second session with a new voice, speed, and `startWordIndex: 8`, which 404'd the first session, deleted its audio, and began chunk 0 at source word 8 ("India").

## 6. Shipped follow-ups (added after the initial change)

- [x] 6.1 Cross-device resume: `ReadingPosition` on `Document`, `PUT /api/documents/{id}/position` (last-writer-wins by client timestamp, future-clamped), client pushes on leave-events + a 15s playing interval, open picks the newer of local/server position (backend test: `Saves_reading_position_with_last_writer_wins_and_survives_restart`).
- [x] 6.2 Library cover thumbnails: `LibraryCover` renders PDF page 1 / EPUB cover lazily on card visibility, caches a small WebP data URL in localStorage (evicted on delete), reused as Media Session artwork.
- [x] 6.3 Drag-and-drop upload dropzone + per-card "last read" resume hint.
