# document-processing Specification

## Purpose

Turn an uploaded PDF/EPUB into an ordered stream of source words (with page + bounding box where the format has them), cleaned of boilerplate and normalized/chunked for synthesis — including OCR for scanned PDFs.

## Requirements

### Requirement: PDF text extraction with word bounding boxes

The system SHALL extract text from PDF documents using PdfPig, capturing for
every word its text, 1-based page number, and bounding box in PDF user-space
coordinates (origin bottom-left), and SHALL assign each word a monotonically
increasing zero-based document word index.

#### Scenario: Word with bounding box is extracted

- **WHEN** a PDF page containing the word "Hello" is processed
- **THEN** the system produces a `WordData` entry with `text="Hello"`, the page
  number, a non-degenerate bounding box `{x, y, width, height}` with
  `width > 0` and `height > 0`, and a unique `index`

#### Scenario: Reading order is preserved across columns

- **WHEN** a multi-column page is extracted
- **THEN** words are ordered by PdfPig reading-order segmentation (column, then
  top-to-bottom, then left-to-right) and word `index` values follow that order

#### Scenario: Ligatures and hyphenation are normalized

- **WHEN** a word contains a typographic ligature (e.g. "ﬁ") or a soft hyphen at
  a line break
- **THEN** the extracted `text` uses canonical characters ("fi") and a
  line-break-hyphenated word is rejoined into a single `WordData` spanning both
  fragments' bounding boxes

### Requirement: Running header, footer, and page-number removal

The system SHALL omit running headers, running footers, and page numbers from
the extracted PDF word stream so the TTS reads body content the way a person
would, while preserving genuine headings (including chapter-only pages). The
discriminator SHALL be recurrence and margin position, not appearance: a line in
a page's top/bottom margin band whose normalized signature (lowercased,
whitespace-collapsed, digit-runs replaced) recurs across a meaningful share of
pages is treated as boilerplate, whereas a line that appears once is kept.
Additionally, a margin line that is solely a page number (bare digits or a valid
roman numeral, optionally labelled "Page"/"p." or wrapped in dashes/brackets)
SHALL be removed regardless of recurrence.

#### Scenario: Recurring running header is dropped, body kept

- **WHEN** the same chapter-title line appears in the top margin of many pages
- **THEN** those header words are removed from the extracted stream while the
  body words of each page are retained in reading order

#### Scenario: Unique heading and chapter-only pages are read

- **WHEN** a heading appears on a single page (e.g. a chapter-only page) — even in
  the top/bottom margin band
- **THEN** it is NOT classified as boilerplate and its words are kept and read

#### Scenario: Page numbers are removed

- **WHEN** a margin line consists only of a page number — arabic ("42", "Page 42",
  "— 42 —") or roman ("xiv"), including on short documents
- **THEN** that line is removed, while a numeric-looking real word (e.g. "did",
  "mill") in the body is never mistaken for a page number

### Requirement: EPUB text extraction with word position mapping

The system SHALL extract reading-order text from EPUB documents using EpubNet,
recording for each word its text, owning spine item (chapter href), a CFI-style
locator, and a zero-based document word index. Because EPUB is reflowable, the
system SHALL NOT attempt to capture absolute pixel bounding boxes server-side;
on-screen position SHALL be resolved client-side via injected word spans.

#### Scenario: Word mapped to spine locator

- **WHEN** an EPUB chapter paragraph is extracted
- **THEN** each word has a `text`, a `page` value of `null`, a stable
  `spineHref`, and a locator sufficient for the frontend to find the word span

#### Scenario: Markup is stripped but boundaries preserved

- **WHEN** a paragraph contains inline markup (`<em>`, `<a>`)
- **THEN** the markup is removed from `text`, word boundaries at tag edges are
  preserved, and no HTML entities remain unescaped in the output

### Requirement: Text normalization before TTS

The system SHALL normalize extracted text into a spoken form before synthesis,
applying deterministic rules for numbers, ordinals, currency, abbreviations,
acronyms, URLs/emails, and punctuation, and SHALL record for every normalized
output token the range of source word indices it derives from.

#### Scenario: Cardinal and ordinal numbers expanded

- **WHEN** the source contains "in 1999" and "the 3rd time"
- **THEN** the normalized text reads "in nineteen ninety nine" and "the third
  time", and each produced token maps back to the source word index of the
  original numeral

#### Scenario: Currency and symbols expanded

- **WHEN** the source contains "$1,250.50" and "50%"
- **THEN** the normalized text reads "one thousand two hundred fifty dollars and
  fifty cents" and "fifty percent"

#### Scenario: Abbreviations and acronyms handled

- **WHEN** the source contains "Dr. Smith", "e.g.", and "NASA"
- **THEN** "Dr." expands to "Doctor", "e.g." to "for example", and "NASA" is
  emitted as a single pronounceable token, with the period after "Dr" not
  treated as a sentence boundary

#### Scenario: URLs and emails spoken readably

- **WHEN** the source contains "https://example.com/docs"
- **THEN** the normalized text is a readable spoken form (e.g. "example dot com
  slash docs") rather than literal punctuation characters

#### Scenario: Sentence-terminating punctuation retained as boundaries

- **WHEN** normalization completes
- **THEN** punctuation that ends sentences ("." "?" "!") is retained as chunk
  boundary hints while non-spoken punctuation is dropped from the token stream

### Requirement: Source-word to TTS-token index mapping

The system SHALL maintain a bidirectional mapping between original source word
indices and normalized TTS token indices so that a timestamp produced for a
TTS token can be projected back onto one or more source words for highlighting.

#### Scenario: One source word expands to many tokens

- **WHEN** "1999" (source index 42) normalizes to "nineteen ninety nine"
- **THEN** all three normalized tokens map back to source index 42, and the
  merged highlight for source word 42 spans the union of the three tokens' time
  windows

#### Scenario: Many source words collapse to one token

- **WHEN** "New York" is treated as a single normalized token
- **THEN** that token maps back to both source indices and highlighting
  activates both source words during its time window

### Requirement: Chunking within token limits

The system SHALL split normalized text into ordered chunks at sentence
boundaries, never exceeding a configurable maximum token count per chunk
(default 350 tokens), preferring paragraph and then sentence breaks, and SHALL
record each chunk's source character offset and contained source word range.

#### Scenario: Chunk respects max token limit

- **WHEN** a paragraph exceeds the max token limit
- **THEN** it is split at the nearest preceding sentence boundary so no chunk
  exceeds the limit; if a single sentence exceeds the limit it is split at a
  clause boundary

#### Scenario: Chunks are contiguous and ordered

- **WHEN** chunking completes for a document
- **THEN** chunk `index` values are contiguous from 0 and the concatenation of
  chunk source word ranges exactly covers the document's word sequence with no
  gaps or overlaps

### Requirement: Unsupported or corrupted files are rejected

The system SHALL validate uploaded files by content (magic bytes), not extension
alone, and SHALL reject unsupported or corrupted files with a structured error
before any processing begins.

#### Scenario: Corrupted PDF rejected

- **WHEN** a file with a `.pdf` extension fails PdfPig structural parsing
- **THEN** processing is aborted and the document status becomes `error` with a
  machine-readable reason `UNREADABLE_DOCUMENT`

#### Scenario: Unsupported format rejected

- **WHEN** a `.docx` file is uploaded
- **THEN** the upload is rejected with HTTP 415 and reason `UNSUPPORTED_FORMAT`
  and no session is created

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
chunk text submitted for synthesis, even though normalization strips terminators
from spoken token text and records them only as a boundary flag, so the voice
receives sentence (period/question/exclamation) intonation and a sentence
boundary it can pause on. The terminator-free token text SHALL still be used for
forced alignment so alignment is not disturbed by punctuation.

#### Scenario: Terminator re-attached for the voice

- **WHEN** a chunk ends a sentence on the word "stones" followed by a period
- **THEN** the text sent to the synthesizer contains "stones." while the stored
  token text remains "stones"

#### Scenario: Question and exclamation marks preserved

- **WHEN** sentences end with "?" or "!"
- **THEN** the synthesized chunk text carries the exact terminator (not a generic
  period) so the voice uses the matching intonation

#### Scenario: Alignment text stays terminator-free

- **WHEN** a chunk is force-aligned against its audio
- **THEN** the transcript used for alignment contains no sentence terminators
  appended by this step
