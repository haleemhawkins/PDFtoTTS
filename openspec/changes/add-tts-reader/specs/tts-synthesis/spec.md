## ADDED Requirements

### Requirement: Kokoro ONNX model synthesis

The TTS worker SHALL load the Kokoro ONNX model and voice pack once at startup
and SHALL synthesize a provided normalized text chunk into mono WAV PCM
float32 audio at the model's native sample rate (24000 Hz), exposing the result
over a gRPC service.

#### Scenario: Chunk synthesized to audio

- **WHEN** the worker receives a `SynthesizeRequest` with non-empty `text` and a
  valid `voice_id`
- **THEN** it returns audio whose duration is greater than zero and whose format
  is PCM float32, mono, 24000 Hz

#### Scenario: Unknown voice rejected

- **WHEN** a `SynthesizeRequest` specifies a `voice_id` not present in the loaded
  voice pack
- **THEN** the worker returns a gRPC `INVALID_ARGUMENT` status naming the unknown
  voice and does not synthesize

### Requirement: Phoneme and word durations returned

The TTS worker SHALL return, alongside audio, the per-token timing it used so the
orchestrator has a synthesis-time prior, including phoneme durations and the
total audio duration in seconds.

#### Scenario: Durations accompany audio

- **WHEN** synthesis completes
- **THEN** the response includes `duration_seconds` equal (within 10 ms) to the
  produced audio length and a non-empty list of phoneme durations

### Requirement: Execution provider selection and fallback

The TTS worker SHALL select the ONNX Runtime execution provider in priority
order ROCm → CUDA → CPU, SHALL log the chosen provider at startup, and SHALL
fall back to the next provider if session creation on a higher-priority provider
fails.

#### Scenario: ROCm preferred when available

- **WHEN** the worker starts with a working ROCm execution provider
- **THEN** the InferenceSession is created with `ROCMExecutionProvider` and the
  health detail reports the active provider and GPU device

#### Scenario: Fallback to CPU when GPU unavailable

- **WHEN** neither ROCm nor CUDA providers can create a session
- **THEN** the worker creates a CPU session, reports degraded mode in health
  detail, and still serves synthesis requests

### Requirement: Streaming synthesis output

The TTS worker SHALL stream audio back in chunks via server-streaming gRPC, with
the first message carrying the audio format and subsequent messages carrying
audio bytes, marking the final message with `is_final = true`.

#### Scenario: First message carries format

- **WHEN** a synthesis stream begins
- **THEN** the first `SynthesizeResponse` populates `format` (encoding, sample
  rate, channels) and the last message has `is_final = true`

### Requirement: GPU memory management and concurrency

The TTS worker SHALL bound concurrent inference to a configurable limit (default
1 concurrent synthesis) to avoid VRAM exhaustion when sharing the GPU with the
alignment worker, and SHALL queue additional requests rather than failing.

#### Scenario: Concurrent requests are serialized

- **WHEN** more synthesis requests arrive than the concurrency limit
- **THEN** excess requests wait in a bounded queue and are served in order
  without out-of-memory failures

### Requirement: Synthesis error handling and retry

The TTS worker SHALL classify failures as retryable (transient GPU/runtime
errors) or non-retryable (invalid input), returning appropriate gRPC status
codes, and the orchestrator SHALL retry retryable failures up to a configurable
limit with backoff.

#### Scenario: Transient GPU error is retryable

- **WHEN** synthesis fails with a transient runtime error
- **THEN** the worker returns `UNAVAILABLE` and the orchestrator retries the
  chunk up to the configured maximum before marking it failed

#### Scenario: Empty text is non-retryable

- **WHEN** a `SynthesizeRequest` has empty `text`
- **THEN** the worker returns `INVALID_ARGUMENT` and the orchestrator does not
  retry
