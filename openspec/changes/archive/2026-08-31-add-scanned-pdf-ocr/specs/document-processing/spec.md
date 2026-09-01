## ADDED Requirements

### Requirement: Scanned PDF optical character recognition

The system SHALL recover text from a scanned or image-only PDF (one with pages
but zero extractable words) via OCR, emitting the same positioned word stream as
a native PDF: each word carrying its text, 1-based page number, bounding box in
PDF user-space coordinates (origin bottom-left, points), and a monotonically
increasing zero-based document word index in reading order. The system SHALL
persist the OCR result so re-opening the document does not re-run OCR. A native
(text-bearing) PDF SHALL NOT be OCR'd.

#### Scenario: Image-only PDF is OCR'd into positioned words

- **WHEN** a PDF with pages but no extractable text layer is processed
- **THEN** OCR produces `WordData` entries with non-empty `text`, the correct
  page number, a non-degenerate bounding box (`width > 0`, `height > 0`) in PDF
  user space, and reading-order `index` values, and the document becomes `Ready`

#### Scenario: Reading order follows the page

- **WHEN** a scanned page of body text is recognized
- **THEN** the produced words are ordered top-to-bottom as the page reads, not in
  detection or text-layer order

#### Scenario: Native PDF skips OCR

- **WHEN** a PDF already exposes an extractable text layer
- **THEN** extraction uses that layer and OCR is not invoked

#### Scenario: OCR result is not recomputed on re-open

- **WHEN** a previously OCR'd document is re-opened from the library
- **THEN** the persisted words are loaded and OCR does not run again

### Requirement: OCR engine selection with fallback

The system SHALL use a Surya GPU OCR worker as the primary engine and SHALL fall
back to an ocrmypdf/Tesseract pass when the Surya engine is disabled or
unreachable, so scanned PDFs remain readable without the GPU worker. The Surya
worker SHALL render each page at a configurable DPI (default 200) and return
recognized words with boxes already converted to PDF user space. The selected
engine SHALL be observable (logged) per document.

#### Scenario: Surya is used when available

- **WHEN** the Surya worker is enabled and reachable and a scanned PDF is processed
- **THEN** recognition is performed by Surya and the resulting words feed
  extraction unchanged

#### Scenario: Fallback when Surya is unavailable

- **WHEN** the Surya worker is disabled or a recognition call fails or returns no
  words
- **THEN** the system falls back to the ocrmypdf/Tesseract pass and still produces
  a readable document, or marks the document `Error` only if that fallback also
  yields no text

#### Scenario: Configurable render resolution

- **WHEN** the OCR render DPI is configured to a higher value for small print
- **THEN** the Surya worker renders pages at that DPI before recognition

### Requirement: Sentence terminators retained in synthesized chunk text

The system SHALL re-attach sentence-ending punctuation (`.`, `?`, `!`) to the
chunk text, even though normalization strips terminators from spoken token text
and records them only as a boundary flag, so the voice receives sentence
(period/question/exclamation) intonation and a sentence boundary it can pause
on. `.` SHALL be used when a boundary is known but the exact mark was not
recorded. The stored per-token text SHALL remain terminator-free so the merge
can match tokens to aligned words.

#### Scenario: Terminator re-attached for the voice

- **WHEN** a chunk ends a sentence on the word "stones" followed by a period
- **THEN** the text sent to the synthesizer contains "stones." while the stored
  token text remains "stones"

#### Scenario: Question and exclamation marks preserved

- **WHEN** sentences end with "?" or "!"
- **THEN** the synthesized chunk text carries the exact terminator (not a generic
  period) so the voice uses the matching intonation

#### Scenario: Token text stays terminator-free

- **WHEN** the merge matches aligned words back to the chunk's tokens
- **THEN** the token text it matches on carries no sentence terminator, so
  punctuation cannot perturb the fuzzy match
