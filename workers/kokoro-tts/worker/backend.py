"""Synthesis backend abstraction + the real Kokoro ONNX implementation.

The gRPC layer depends only on the SynthBackend protocol, so it can be tested
with a fake. The real backend lazily imports kokoro_onnx so importing this
module (and running the unit tests) never requires onnxruntime.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol, runtime_checkable

import numpy as np


@dataclass(frozen=True)
class Phoneme:
    phoneme: str
    start_seconds: float
    end_seconds: float


@dataclass(frozen=True)
class Synthesis:
    samples: np.ndarray  # mono float32
    sample_rate: int
    phonemes: list[Phoneme] = field(default_factory=list)


@dataclass(frozen=True)
class VoiceInfo:
    id: str
    label: str
    language: str
    gender: str


@runtime_checkable
class SynthBackend(Protocol):
    @property
    def provider(self) -> str: ...
    def list_voices(self) -> list[VoiceInfo]: ...
    def is_voice(self, voice_id: str) -> bool: ...
    def synthesize(self, text: str, voice_id: str, speed: float, language: str) -> Synthesis: ...


# Kokoro's per-voice style array has STYLE_ROWS rows; _create_audio selects the
# style with voice[len(tokens)], so a token count equal to the row count indexes
# out of bounds. We cap below it.
STYLE_ROWS = 510


def cap_phoneme_length(kokoro_onnx_module, cap: int = STYLE_ROWS - 1) -> int:
    """Cap ``kokoro_onnx.MAX_PHONEME_LENGTH`` so a phoneme batch can never index
    the voice style array out of bounds.

    kokoro_onnx 0.5.0 has an off-by-one: ``_create_audio`` truncates a batch to
    ``MAX_PHONEME_LENGTH`` (510) then does ``voice[len(tokens)]`` on the 510-row
    style array (valid indices 0..509). A batch that tokenizes to exactly 510
    hits ``voice[510]`` -> "index 510 is out of bounds for axis 0 with size 510".
    Capping at 509 keeps both the internal batch-split and the truncation in range.

    Returns the effective ``MAX_PHONEME_LENGTH`` after capping.
    """
    if kokoro_onnx_module.MAX_PHONEME_LENGTH > cap:
        kokoro_onnx_module.MAX_PHONEME_LENGTH = cap
    return kokoro_onnx_module.MAX_PHONEME_LENGTH


# --- Real Kokoro backend (not exercised by unit tests) --------------------

def _region_gender(voice_id: str) -> tuple[str, str]:
    # Kokoro voice ids like "af_heart": region letter + gender letter.
    region = {"a": "en-us", "b": "en-gb", "e": "es", "f": "fr", "h": "hi",
              "i": "it", "j": "ja", "p": "pt-br", "z": "zh"}
    gender = {"f": "female", "m": "male"}
    lang = region.get(voice_id[:1], "en-us") if voice_id else "en-us"
    sex = gender.get(voice_id[1:2], "unknown") if len(voice_id) > 1 else "unknown"
    return lang, sex


class KokoroBackend:
    """Wraps kokoro_onnx. Construct via :meth:`load`."""

    def __init__(self, kokoro, provider: str, sample_rate: int = 24000):
        self._kokoro = kokoro
        self._provider = provider
        self._sample_rate = sample_rate
        self._voices = {v: VoiceInfo(v, v, *_region_gender(v)) for v in kokoro.get_voices()}

    @classmethod
    def load(cls, model_path: str, voices_path: str, provider_priority=None):
        """Load the ONNX model, selecting the best execution provider."""
        import onnxruntime as ort
        import kokoro_onnx
        from kokoro_onnx import Kokoro

        from .providers import create_session

        cap_phoneme_length(kokoro_onnx)

        def factory(providers):
            return ort.InferenceSession(model_path, providers=providers)

        session, provider = create_session(ort.get_available_providers(), factory, provider_priority)
        kokoro = Kokoro.from_session(session, voices_path)
        return cls(kokoro, provider)

    @property
    def provider(self) -> str:
        return self._provider

    def list_voices(self) -> list[VoiceInfo]:
        return list(self._voices.values())

    def is_voice(self, voice_id: str) -> bool:
        return voice_id in self._voices

    def synthesize(self, text: str, voice_id: str, speed: float, language: str) -> Synthesis:
        from .errors import TransientError

        # Kokoro expects locale codes (e.g. "en-us"); fall back to the voice's
        # language for generic 2-letter codes like "en".
        lang = language if language and "-" in language else self._voices[voice_id].language
        try:
            samples, sr = self._kokoro.create(text, voice=voice_id, speed=speed, lang=lang)
        except Exception as exc:  # noqa: BLE001
            raise TransientError(str(exc)) from exc
        return Synthesis(np.asarray(samples, dtype=np.float32), int(sr), [])
