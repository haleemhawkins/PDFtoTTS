"""Alignment backend abstraction + the real WhisperX implementation.

The gRPC layer depends only on the AlignBackend protocol so it is testable with
a fake. The real backend lazily imports whisperx/torch, so importing this module
(and running the unit tests) never requires the GPU stack.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Protocol, runtime_checkable

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class AlignedWord:
    text: str
    start_ms: int
    end_ms: int
    confidence: float


@dataclass(frozen=True)
class AlignResult:
    words: list[AlignedWord]
    audio_duration_ms: int


@runtime_checkable
class AlignBackend(Protocol):
    @property
    def provider(self) -> str: ...
    def align(self, audio_path: str, transcript: str, language: str) -> AlignResult: ...


class WhisperXBackend:
    """
    Forced alignment with WhisperX + wav2vec2. Alignment only — the transcript is
    authoritative and Whisper ASR is never run (design §4.1). wav2vec2 align
    models are preloaded per language and kept resident (§4.2).
    """

    SAMPLE_RATE = 16000

    def __init__(self, device: str):
        self._device = device
        self._models: dict[str, tuple] = {}

    @property
    def provider(self) -> str:
        return self._device

    def preload(self, language: str) -> None:
        import whisperx

        if language not in self._models:
            model, metadata = whisperx.load_align_model(language_code=language, device=self._device)
            self._models[language] = (model, metadata)

    def warmup(self, language: str = "en") -> None:
        """Run one dummy alignment so the FIRST real request doesn't pay the
        one-time GPU/MIOpen kernel compilation (tens of seconds on ROCm)."""
        import numpy as np
        import whisperx

        self.preload(language)
        model, metadata = self._models[language]
        try:
            audio = np.zeros(self.SAMPLE_RATE, dtype=np.float32)  # 1s of silence
            segments = [{"text": "warm up the alignment kernels now", "start": 0.0, "end": 1.0}]
            whisperx.align(segments, model, metadata, audio, self._device,
                           return_char_alignments=False)
        except Exception as exc:  # noqa: BLE001 — warmup is best-effort
            logger.warning("alignment warmup failed: %s", exc)

    def align(self, audio_path: str, transcript: str, language: str) -> AlignResult:
        import whisperx

        from .errors import TransientError

        self.preload(language)
        model, metadata = self._models[language]
        try:
            audio = whisperx.load_audio(audio_path)
            duration = len(audio) / float(self.SAMPLE_RATE)
            segments = [{"text": transcript, "start": 0.0, "end": duration}]
            result = whisperx.align(segments, model, metadata, audio, self._device,
                                    return_char_alignments=False)
        except Exception as exc:  # noqa: BLE001
            raise TransientError(str(exc)) from exc

        words = []
        for w in result.get("word_segments", []):
            start = w.get("start")
            end = w.get("end")
            if start is None or end is None:
                # Unaligned word: emit a zero-width, zero-confidence marker; the
                # orchestrator interpolates it from neighbours.
                words.append(AlignedWord(w.get("word", ""), 0, 0, 0.0))
            else:
                words.append(AlignedWord(
                    w.get("word", ""), int(start * 1000), int(end * 1000),
                    float(w.get("score", 0.0))))

        return AlignResult(words, int(duration * 1000))
