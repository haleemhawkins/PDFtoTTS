## ADDED Requirements

### Requirement: Natural inter-sentence pause

The TTS worker SHALL insert a brief, configurable silence after each sentence so
a sentence-ending terminator yields an audible beat instead of rushing into the
next sentence, and SHALL give a slightly longer beat after a question or
exclamation than after a period. The pause duration SHALL be configurable
(default ~260 ms) via environment, and a value of zero SHALL disable the added
pause. The worker SHALL synthesize sentence by sentence so each sentence keeps its
own intonation, degrading a sentence that fails phonemization to a short silence
rather than failing the whole chunk.

#### Scenario: Pause follows a period

- **WHEN** a chunk containing multiple sentences is synthesized
- **THEN** the produced audio contains an added silence of approximately the
  configured gap after each sentence boundary

#### Scenario: Stronger beat after question or exclamation

- **WHEN** a sentence ends with "?" or "!"
- **THEN** the silence inserted after it is longer than the silence after a
  period

#### Scenario: Pause is configurable and can be disabled

- **WHEN** the sentence-gap configuration is set to zero
- **THEN** no extra inter-sentence silence is added

#### Scenario: A failing sentence does not break the chunk

- **WHEN** one sentence in a chunk cannot be phonemized
- **THEN** that sentence degrades to a short silence and the remaining sentences
  are still synthesized and returned
