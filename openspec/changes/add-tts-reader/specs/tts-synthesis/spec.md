## ADDED Requirements

### Requirement: Kokoro speech synthesis

The TTS worker SHALL load the Kokoro model once at startup (the PyTorch
`kokoro` package / `KPipeline`, on the GPU via torch-rocm when available, else
CPU) and SHALL synthesize a provided normalized text chunk into mono **16-bit
PCM** WAV at the model's native 24000 Hz over a unary gRPC call. 16-bit PCM,
not IEEE-float: browsers' Web Audio `decodeAudioData` silently fails on
float WAV, which left the reader with no audio.

#### Scenario: Chunk synthesized to audio

- **WHEN** the worker receives a `SynthesizeRequest` with non-empty `text` and a
  valid `voice_id`
- **THEN** it writes a valid mono 16-bit 24000 Hz WAV and reports a duration
  greater than zero

#### Scenario: Unknown voice rejected

- **WHEN** a `SynthesizeRequest` specifies a `voice_id` the backend doesn't know
- **THEN** the worker returns a gRPC `INVALID_ARGUMENT` status naming the unknown
  voice and does not synthesize

### Requirement: Audio delivered by shared-volume path

The TTS worker SHALL NOT stream audio bytes over gRPC. It SHALL write the WAV to
the shared volume at the request's `out_path` (relative to the data root,
creating parent directories) and return that path plus `duration_seconds`
matching the produced audio length. Word timing comes from forced alignment;
phoneme timings MAY be returned but are not relied upon.

#### Scenario: Response carries the path, not bytes

- **WHEN** synthesis completes
- **THEN** the response's `audio_path` equals the requested `out_path`, the WAV
  exists on the shared volume, and `duration_seconds` matches its length (within
  10 ms)

### Requirement: Device selection and warmup

The TTS worker SHALL run on the GPU when PyTorch reports one (torch-rocm
presents the AMD GPU as `cuda`) and SHALL fall back to CPU otherwise, logging
the selected device and reporting it in health detail. It SHALL warm up the
synthesis pipeline before reporting healthy, so the first request doesn't pay
one-time kernel compilation.

#### Scenario: CPU fallback still serves

- **WHEN** no usable GPU is visible to PyTorch
- **THEN** the worker starts on CPU, reports the device in health detail, and
  serves synthesis requests

### Requirement: Phonemizer-safe text sanitation

The TTS worker SHALL sanitize chunk text before synthesis — smart quotes,
dashes, and ellipses normalized to plain forms with correct prosody, true
non-speech symbols (bullets, section marks) dropped, exotic spaces and
non-printables collapsed — so odd typography can't break phonemization or
mangle contractions, and SHALL trim Kokoro's leading/trailing dead air from
each synthesized piece so chunks don't stack silence at every boundary.

#### Scenario: Smart punctuation reads correctly

- **WHEN** the text contains smart apostrophes ("don't") or em-dash-glued words
- **THEN** contractions are pronounced correctly and the dash reads as a short
  pause rather than a mispronounced glyph

### Requirement: GPU memory management and concurrency

The TTS worker SHALL bound concurrent inference to a configurable limit (default
1 concurrent synthesis) to avoid VRAM exhaustion when sharing the GPU with the
alignment worker, and SHALL queue additional requests rather than failing.

#### Scenario: Concurrent requests are serialized

- **WHEN** more synthesis requests arrive than the concurrency limit
- **THEN** excess requests wait and are served in order without out-of-memory
  failures

### Requirement: Synthesis error handling and degradation

The TTS worker SHALL classify failures with gRPC status codes — invalid input
(empty text, unknown voice) as `INVALID_ARGUMENT`, transient runtime errors as
`UNAVAILABLE` — and one un-synthesizable sentence SHALL NOT fail its chunk or
document: the worker SHALL retry that sentence with progressively safer text
renderings and, if all fail, substitute a short silence proportional to its
word count. The API SHALL bound every worker call with a deadline and surface a
friendly, actionable error when a worker hangs or is unreachable (e.g. a GPU
wedged after suspend/resume).

#### Scenario: Empty text is non-retryable

- **WHEN** a `SynthesizeRequest` has empty `text`
- **THEN** the worker returns `INVALID_ARGUMENT` and the pipeline does not retry

#### Scenario: A stubborn sentence degrades to silence

- **WHEN** one sentence fails phonemization in every fallback rendering
- **THEN** the chunk still synthesizes, with that sentence replaced by a short
  proportional silence

#### Scenario: A wedged worker surfaces an error

- **WHEN** a worker doesn't respond within its deadline
- **THEN** the session surfaces an error telling the user to restart the workers,
  instead of hanging at 0% forever
