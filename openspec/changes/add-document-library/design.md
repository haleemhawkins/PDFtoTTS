## Context

Today the API keeps documents in `InMemoryDocumentStore` (lost on restart) while
`LocalFileStorage` persists originals under `<DATA_DIR>/originals/{id}{ext}`. The
frontend persists a single file in IndexedDB plus one `SessionMeta` in
localStorage, and `App.tsx` toggles between `UploadView` and the reader. Sessions
synthesize WAV chunks on the fly under `<DATA_DIR>/audio/{sessionId}/` and are
deleted when the session ends — that behavior must stay.

This change makes the document catalogue durable, adds REST CRUD over it, lets the
client render from server-held originals, and adds an in-reader voice switch.

## Goals / Non-Goals

**Goals:**
- A persistent, multi-document library that survives API restarts.
- List / open / rename / delete documents via REST; serve original bytes.
- Re-opening a `Ready` document never re-runs extraction/OCR.
- Switch narration voice mid-document, re-synthesizing from the current word.

**Non-Goals:**
- Persisting synthesized audio (explicitly excluded — stays on-the-fly).
- Multi-user accounts / auth / per-user libraries (single local user).
- A database — a JSON manifest on the shared volume is sufficient at this scale.
- Cloud sync of the library.

## Decisions

### Persist the catalogue as a JSON manifest beside the originals
`LocalFileStorage` gains a `catalogue.json` (the list of `Document` records) and a
`words/{id}.json` per document holding the extracted `SourceWord[]`. The store
becomes a `PersistentDocumentStore` that loads the manifest on construction and
writes through on every mutation (`Add`, `SetExtraction`, `SetProgress` throttled,
rename, delete). Words are stored per-document (not in the manifest) because they
are large and only needed when a document is opened.

- *Why not a database (SQLite/LiteDB)?* Overkill for a single-user local app and
  adds a dependency/migration surface; the data is naturally file-shaped and the
  originals already live on the same volume.
- *Why split words into their own files?* Keeps the manifest small and fast to
  load on startup; words load lazily via the existing `/words` endpoint.

Startup reconciliation: on load, drop any catalogue entry whose original file is
missing, and demote a `Ready` doc with no words file back to `Error` (defensive).

### `Document` gains a stable display name; rename edits `Filename`
Rather than add a new field, rename updates the existing `Filename` (used as the
display title throughout the UI). `PATCH /api/documents/{id}` takes
`{ name: string }`; blank is rejected. The `id` and on-disk original path are
unchanged by rename.

### Serve originals for client-side rendering
PDF.js and epub.js render from the original bytes, so the client must obtain them
without re-upload. `GET /api/documents/{id}/original` streams the stored file with
the type-appropriate content type. The frontend fetches it into a `File`/`Blob`
for the existing `PdfReader`/`EpubReader`. `IFileStorage` gains
`OriginalFullPath(id, type)` (or a lookup by id) so the endpoint can locate it.

### Delete cascades to sessions and audio
`DELETE /api/documents/{id}` cancels every `ISessionStore.ForDocument(id)` session,
removes their audio via `DeleteSessionAudio`, deletes the original + words file,
and removes the catalogue entry. Order: cancel sessions → delete audio → delete
original/words → remove from store → persist manifest.

### Frontend: library as home, open-by-id
`App.tsx` gains a `view` state (`"library" | "reader"`). `LibraryView` fetches
`GET /api/documents`, renders cards with open/rename/delete, and an upload control
(reuses the current file input + voice/speed defaults). Opening calls a new
`reader.open(documentId, voice, speed, page, word)` that fetches the original bytes
and words from the server and starts a session — replacing the IndexedDB
file-restore path. `persist.ts` is reduced to a per-document position map
(`{ [id]: { page, word, voice, speed } }`) plus a `lastOpenedId`, so a reload
restores the last document by id and each document remembers its place.

### Voice switch mirrors speed change
`useReader` already re-synthesizes from the current source word in `changeSpeed`
via `restartAt(sourceWord, speed)`. Add `changeVoice(voice)` that sets `voiceRef`
and calls `restartAt(currentSourceWord, speedRef.current)` so the new session is
created with the new voice (the session is created with `voiceRef.current` in
`openSession`). `ReaderChrome` gets a voice `<select>` (voices from
`GET /api/voices`, lifted to `App` to avoid refetching). The chosen voice is saved
to the per-document persist entry.

## Risks / Trade-offs

- **Concurrent manifest writes corrupting the file** → Serialize writes behind a
  lock and write atomically (temp file + move). Mutations are infrequent.
- **Large `words/{id}.json` slowing open** → Words already transit the `/words`
  endpoint at this size today; loading from disk is no worse than holding them in
  memory. Manifest stays words-free so startup is fast.
- **Stale catalogue entries after manual file deletion** → Startup reconciliation
  drops entries whose original is missing.
- **Switching voice mid-sentence loses a little buffered audio** → Acceptable and
  identical to the existing speed-change UX; re-synthesis resumes from the exact
  current word.
- **Old single-slot IndexedDB cache becomes orphaned** → Clear it on first run of
  the new build (best-effort) so it doesn't waste quota; not load-bearing anymore.

## Migration Plan

- Additive: new endpoints and a manifest file; existing `POST /api/documents`,
  sessions, and audio behavior are unchanged.
- First run with no `catalogue.json` starts an empty library; previously uploaded
  originals without a manifest entry are not auto-imported (acceptable — they had
  no durable metadata anyway).
- Rollback: revert the build; the manifest/words files are inert to the old code,
  which simply ignores them.

## Open Questions

- None blocking. Cover-thumbnail rendering in the library is deferred (out of
  scope); cards show name + type + status only.
