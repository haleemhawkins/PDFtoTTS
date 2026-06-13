# TTS backends — options for more expressive / emphatic narration

The app synthesizes with **Kokoro 82M on the GPU** (PyTorch / `KPipeline`, see
`workers/kokoro-tts`). It's fast, fully self-hosted, and free, but it's a compact
model: no SSML / emphasis markup, so it can't stress arbitrary words on demand.
Naturalness today comes from punctuation normalization, per-sentence intonation,
and controlled sentence pauses (`worker/backend.py`).

If we ever want *better emphasis / expressiveness*, a cloud voice can be a
**drop-in alternate backend**: the worker implements a `SynthBackend` (text →
WAV), so only the synthesizer changes — chunking, WhisperX alignment, streaming,
and word-highlighting all stay the same.

## How "emphasis" is achieved, by mechanism

### 1. Explicit control via SSML (mark up which words to stress)
- **Azure AI Speech (Neural TTS)** — most controllable. Full SSML:
  `<emphasis level="strong">word</emphasis>`, `<prosody rate="-10%" pitch="+8%">`,
  `<break time="400ms"/>`, plus speaking styles (`newscast`, `cheerful`,
  `narration-relaxed`) and role-play. Best for deterministic, fine-grained emphasis.
- **Google Cloud TTS** — SSML `<emphasis>`/`<prosody>`/`<break>`; Chirp 3 HD voices
  are very natural.
- **Amazon Polly** — SSML + Neural voices and Newscaster/Conversational styles.

> SSML emphasis needs *something* to decide which words to stress. Either keep it
> punctuation-driven, or run the chunk text through a small LLM first to insert
> `<emphasis>` tags for "smart" emphasis on key terms.

### 2. Expressive by default (little/no markup)
- **ElevenLabs** — current quality/expressiveness leader; natural and emotive out
  of the box, newer models take emotional/context cues and inline delivery tags.
  Highest cost.
- **Cartesia (Sonic)** — very natural and very low latency; great for streaming a
  reader.

### 3. Instruction-steered (natural-language style prompt)
- **OpenAI `gpt-4o-mini-tts`** — pass a style instruction
  ("narrate calmly, emphasize technical terms") and it adapts delivery. Simple
  API, natural, no SSML needed.

## Recommendation by goal
- **Controllable emphasis** → Azure (SSML + an LLM tagging pass).
- **Most natural-sounding, least effort** → ElevenLabs.
- **Cheap + easy middle ground** → OpenAI `gpt-4o-mini-tts`.

## Tradeoffs vs the current self-hosted Kokoro
- **Cost** — roughly $15–16 per *million* characters for Azure / Google / Polly /
  OpenAI; ElevenLabs is pricier (closer to per-thousand). A book is ~0.5–1M chars.
- **Privacy** — document text leaves the machine for the cloud (today everything
  runs locally on the RX 7800 XT).
- **Latency / offline** — needs internet; network adds a round-trip, though all of
  the above offer streaming to hide it.

## Integration sketch (if pursued)
- Add e.g. `workers/<provider>-tts` (or a backend mode in the existing worker)
  implementing the same gRPC `Synthesize` contract, returning a WAV on `/data`.
- Select it per session/voice (the API already passes `voice`); could expose
  cloud voices in `/api/voices` alongside Kokoro's.
- Keep WhisperX alignment as-is — it aligns whatever audio is produced.
- For SSML providers, optionally insert an emphasis-tagging step (LLM or rules)
  before synthesis.
