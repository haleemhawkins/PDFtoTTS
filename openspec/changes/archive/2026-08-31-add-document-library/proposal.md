## Why

The reader can only hold one document at a time: the original is saved to the
shared volume but the document record lives in an in-memory store that is lost on
every API restart, and the frontend caches only a single file in IndexedDB.
A returning user cannot see, reopen, rename, or delete the books they uploaded
earlier, and re-opening a scanned PDF re-runs minutes of OCR. We also can't change
the narration voice once reading has started — voice is fixed at upload time.

## What Changes

- Add a **document library**: a persistent collection of every uploaded PDF/EPUB
  the user can list, open, rename, and delete. The library replaces the single-doc
  upload screen as the app's home.
- **Persist document metadata + extracted words to disk** (a JSON manifest beside
  the originals already on the shared volume) and reload it on startup, so the
  library survives API restarts and re-opening a document never re-runs OCR.
- Audio is **still not persisted** — synthesized WAV stays per-session and
  generated on the fly. Only originals and extracted text/words are stored.
- Add backend endpoints: `GET /api/documents` (list), `DELETE /api/documents/{id}`
  (remove the document, its words, its original, and clean up any sessions/audio),
  `PATCH /api/documents/{id}` (rename), and `GET /api/documents/{id}/original`
  (serve the original bytes so the client renders without re-uploading).
- Add an **in-reader voice switcher**: change the narration voice from the reader
  menu mid-document; it re-synthesizes from the current word at the new voice and
  remembers the choice per document.

## Capabilities

### New Capabilities
<!-- None: this extends existing reader capabilities rather than introducing new domains. -->

### Modified Capabilities
- `reader-backend`: Adds document list/delete/rename/original-bytes endpoints and
  on-disk persistence of the document catalogue (metadata + words); delete now
  cascades to sessions and audio.
- `reader-frontend`: Adds a library home view with open/rename/delete, opening a
  document by id (fetching its original from the server), and an in-reader voice
  switcher that re-synthesizes from the current word.

## Impact

- Backend: `src/PDFtoTTS.Api/Program.cs` (new routes), `Storage/Stores.cs` +
  `Storage/FileStorage.cs` (persistence of manifest/words, original deletion),
  `Contracts/Dtos.cs` (rename request). New persistence layer for the document
  catalogue. Document delete must cancel in-flight sessions and remove audio.
- Frontend: new `LibraryView` component + library API client calls, reworked
  `App.tsx` navigation (library ⇄ reader), `useReader` opening by document id and
  a `changeVoice` flow, voice picker in `ReaderChrome`, and `persist.ts` reduced to
  per-document position/voice (no longer the sole file cache).
- No new external dependencies. Existing `Document`/`TtsSession` contracts gain a
  rename path; audio storage behavior is unchanged.
