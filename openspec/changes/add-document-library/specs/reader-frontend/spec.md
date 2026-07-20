## ADDED Requirements

### Requirement: Library home view

The frontend SHALL present a library as the app's home: a list of all documents
from `GET /api/documents`, each showing its name, type, and status — plus a
cover thumbnail (PDF page 1 / the EPUB's declared cover, rendered lazily and
cached locally; a neutral fallback when unavailable) and a "last read" hint when
a resume position exists — with an upload affordance that accepts both file
picking and drag-and-drop. The library replaces the single-document upload
screen as the default landing view.

#### Scenario: Library lists documents

- **WHEN** the app loads and documents exist
- **THEN** each document is shown with its name and type, and selecting one opens
  it in the reader

#### Scenario: Empty library invites upload

- **WHEN** the app loads with no documents
- **THEN** an empty state with an upload control is shown

#### Scenario: Upload adds to the library

- **WHEN** the user uploads a new PDF/EPUB from the library
- **THEN** the document appears in the library and (once `Ready`) can be opened

### Requirement: Open a document by id

The frontend SHALL open a document from the library by fetching its original bytes
from `GET /api/documents/{id}/original` for rendering and starting a TTS session,
without requiring the user to re-select the file.

#### Scenario: Open from library

- **WHEN** the user selects a `Ready` document in the library
- **THEN** the reader renders the document from the server-provided original and
  begins a session at the saved position (or the start)

#### Scenario: Home returns to the library

- **WHEN** the user taps Home in the reader
- **THEN** the reader closes and the library is shown, with the document still
  present in the library

### Requirement: Rename and delete from the library

The frontend SHALL let the user rename and delete documents from the library,
calling `PATCH` and `DELETE /api/documents/{id}` and reflecting the result.

#### Scenario: Rename a document

- **WHEN** the user renames a document and confirms
- **THEN** the new name is sent via `PATCH` and shown in the library

#### Scenario: Delete a document

- **WHEN** the user deletes a document and confirms
- **THEN** the document is removed via `DELETE` and disappears from the library

### Requirement: Cross-device resume

The frontend SHALL push the current reading position (page, word, voice, speed,
timestamped) to the backend on the discrete leave-events (pause, page turn,
voice/speed change, returning home, tab hidden, page unload) and periodically
while playing, and on open SHALL resume from whichever of the locally cached and
server-stored positions is newer. Position saves SHALL be best-effort and never
disrupt reading.

#### Scenario: Resume on another device

- **WHEN** the user reads on one device and later opens the same document on
  another
- **THEN** the reader lands paused at the position the first device last
  reported, with its voice and speed

#### Scenario: Stale local cache loses

- **WHEN** the server's stored position is newer than this device's local cache
- **THEN** the server position is used and seeded into the local cache

### Requirement: In-reader voice switcher

The frontend SHALL provide a voice picker in the reader so the user can change the
narration voice mid-document. Changing the voice SHALL re-synthesize from the
current word at the new voice and remember the choice for that document.

#### Scenario: Change voice while reading

- **WHEN** the user picks a different voice from the reader menu
- **THEN** synthesis restarts from the current word using the new voice and
  playback continues from that word

#### Scenario: Voice choice is remembered

- **WHEN** the user reopens a document whose voice was changed
- **THEN** the previously chosen voice is used for the new session
