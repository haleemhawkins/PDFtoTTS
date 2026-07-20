# forced-alignment Specification

## Purpose

Produce authoritative per-word timings by force-aligning the known transcript against the synthesized audio (WhisperX + wav2vec2 on the GPU).

## Requirements

### Requirement: Forced alignment of known transcript

The alignment worker SHALL perform forced alignment only (never open-vocabulary
transcription): given synthesized audio and the exact transcript that produced
it, it SHALL return per-word timestamps `{text, startMs, endMs, confidence}`
using WhisperX alignment with a wav2vec2 phoneme model.

#### Scenario: Words aligned in order

- **WHEN** the worker receives audio and its known transcript
- **THEN** it returns one timing per transcript word in transcript order, with
  `0 <= startMs < endMs <= audioDurationMs` and monotonically non-decreasing
  `startMs`

#### Scenario: Transcript is authoritative

- **WHEN** the audio is slightly mispronounced relative to the transcript
- **THEN** the worker still emits exactly the transcript's words (it aligns, it
  does not re-transcribe) and never invents or drops words

### Requirement: wav2vec2 preloaded and resident in VRAM

The alignment worker SHALL load the wav2vec2 alignment model once at startup and
keep it resident in VRAM for the process lifetime, so per-request latency
excludes model load time.

#### Scenario: Model warm before serving

- **WHEN** the worker reports `SERVING` health
- **THEN** the wav2vec2 model is already loaded on the selected device and the
  first alignment request does not trigger a model download or load

### Requirement: Low-confidence alignment handling

The alignment worker SHALL attach a per-word confidence score in `[0, 1]` and
SHALL flag words below a configurable threshold (default 0.30); the orchestrator
SHALL fall back to proportional timing interpolation for flagged spans rather
than dropping the words.

#### Scenario: Low-confidence word flagged

- **WHEN** a word aligns with confidence below the threshold
- **THEN** its timing is still returned, marked low-confidence, and the
  orchestrator interpolates its window from neighboring high-confidence anchors
  so highlighting never stalls or skips

#### Scenario: Whole-chunk alignment failure falls back

- **WHEN** alignment fails for an entire chunk
- **THEN** the orchestrator synthesizes evenly distributed timings from the
  chunk's audio duration and word count and marks the chunk degraded

### Requirement: Normalized-token edge cases

The alignment worker SHALL accept transcripts containing expanded numbers,
expanded abbreviations, and acronym tokens, and SHALL align them at the token
granularity provided; the orchestrator SHALL re-project token timings onto
source words using the normalization map.

#### Scenario: Expanded number aligns as multiple tokens

- **WHEN** the transcript contains "nineteen ninety nine" for source "1999"
- **THEN** three word timings are returned and the orchestrator merges them into
  one highlight window covering the original numeral

#### Scenario: Punctuation tokens are not aligned

- **WHEN** the transcript contains no standalone spoken punctuation tokens
- **THEN** the returned word list contains no punctuation-only entries

### Requirement: GPU memory management for 16 GB VRAM

The alignment worker SHALL operate within a configurable VRAM budget appropriate
for the RX 7800 XT (16 GB shared with the TTS worker), bounding batch/segment
size and concurrency (default 1) and releasing intermediate tensors after each
request.

#### Scenario: VRAM stays within budget under load

- **WHEN** alignment runs repeatedly at the configured concurrency
- **THEN** VRAM usage stays within the configured budget and no out-of-memory
  errors occur across a full document
