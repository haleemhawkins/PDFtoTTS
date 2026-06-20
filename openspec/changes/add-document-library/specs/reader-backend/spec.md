## ADDED Requirements

### Requirement: Persistent document catalogue

The backend SHALL persist every uploaded document's metadata and extracted words
to the shared volume and reload them on startup, so the document library survives
API restarts. Synthesized audio SHALL NOT be persisted across restarts — it
remains per-session and generated on demand.

#### Scenario: Catalogue survives a restart

- **WHEN** a document has reached status `Ready` and the API process is restarted
- **THEN** the document and its extracted words are available again from the store
  without re-uploading or re-extracting, and re-opening it does not re-run OCR

#### Scenario: Audio is not persisted

- **WHEN** the API restarts after sessions produced audio
- **THEN** no session audio is restored; new playback re-synthesizes on the fly

#### Scenario: Extraction result is written through

- **WHEN** background extraction (and any OCR) completes for a document
- **THEN** the persisted catalogue is updated atomically with the final status,
  page/word counts, and the extracted words

### Requirement: List documents

The backend SHALL expose `GET /api/documents` returning the catalogue of
documents (most-recently-added first) as `Document` records.

#### Scenario: Library is listed

- **WHEN** a client GETs `/api/documents`
- **THEN** the response is HTTP 200 with an array of `Document` records including
  `id`, `filename`, `type`, `pageCount`, `wordCount`, and `status`

#### Scenario: Empty library

- **WHEN** no documents have been uploaded
- **THEN** the response is HTTP 200 with an empty array

### Requirement: Delete a document

The backend SHALL expose `DELETE /api/documents/{id}` that removes the document
from the catalogue, deletes its original file and persisted words, and cancels and
cleans up any sessions and audio belonging to it.

#### Scenario: Document and its artifacts removed

- **WHEN** a client DELETEs `/api/documents/{id}` for an existing document
- **THEN** the response is HTTP 204, the document no longer appears in
  `GET /api/documents`, its original file is deleted, and any in-flight session for
  it is cancelled with its audio removed

#### Scenario: Deleting an unknown document

- **WHEN** a client DELETEs `/api/documents/{id}` for an id that does not exist
- **THEN** the response is HTTP 404

### Requirement: Rename a document

The backend SHALL expose `PATCH /api/documents/{id}` accepting a new display name
and SHALL update the document's `filename`/title, persisting the change.

#### Scenario: Rename succeeds

- **WHEN** a client PATCHes `/api/documents/{id}` with a non-empty name
- **THEN** the response is HTTP 200 with the updated `Document` and the new name is
  persisted across restarts

#### Scenario: Empty name rejected

- **WHEN** a client PATCHes `/api/documents/{id}` with a blank name
- **THEN** the response is HTTP 400 and the name is unchanged

### Requirement: Serve original document bytes

The backend SHALL expose `GET /api/documents/{id}/original` returning the stored
original file with its correct content type, so the client can render the PDF/EPUB
without re-uploading it.

#### Scenario: Original is served

- **WHEN** a client GETs `/api/documents/{id}/original` for an existing document
- **THEN** the response is HTTP 200 with the original bytes and a content type of
  `application/pdf` or `application/epub+zip` matching the document type

#### Scenario: Original missing

- **WHEN** the document id is unknown or its original file is absent
- **THEN** the response is HTTP 404
