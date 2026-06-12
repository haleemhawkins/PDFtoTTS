"""Synthesis backend abstraction + the real Kokoro ONNX implementation.

The gRPC layer depends only on the SynthBackend protocol, so it can be tested
with a fake. The real backend lazily imports kokoro_onnx so importing this
module (and running the unit tests) never requires onnxruntime.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from typing import Iterator, Protocol, runtime_checkable

import numpy as np

logger = logging.getLogger(__name__)

# Non-speech symbols (bullets, dashes, marks) that carry no pronunciation and can
# break the phonemizer — notably consecutive bullets, which produce empty
# segments and the espeak error "number of lines in input and output must be
# equal". Stripped before synthesis.
_SYMBOLS = "•◦▪▫‣⁃∙·●○◆◇■□▶▷–—―§¶†‡※"


def _collapse(text: str) -> str:
    """Replace non-printable chars (newlines/control) with spaces and collapse
    whitespace — so word boundaries survive instead of words getting glued."""
    text = "".join(ch if ch.isprintable() else " " for ch in text)
    return re.sub(r"\s+", " ", text).strip()


def sanitize_text(text: str) -> str:
    """Remove non-speech symbols and tidy whitespace so the phonemizer gets clean,
    single-line text (no empty segments from runs of bullets/dashes)."""
    return _collapse(re.sub("[" + re.escape(_SYMBOLS) + "]", " ", text))


def synthesis_variants(text: str) -> Iterator[str]:
    """Progressively more conservative renderings of `text`. Phonemization can
    still fail on odd token sequences, so we fall back to safe-punctuation-only
    and finally alphanumeric-only rather than letting one chunk break a document."""
    yield sanitize_text(text)
    yield _collapse(re.sub(r"[^A-Za-z0-9 .,!?;:'-]", " ", text))
    yield _collapse(re.sub(r"[^A-Za-z0-9 ]", " ", text))


def _split_sentences(text: str) -> list[str]:
    """Split into sentences (keeping terminators) so one bad sentence can be
    isolated; falls back to the whole text when there's no boundary."""
    parts = [s.strip() for s in re.split(r"(?<=[.!?])\s+", text) if s.strip()]
    return parts or [text.strip()]


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
    def load(cls, model_path: str, voices_path: str, provider_priority=None,
             intra_op_threads: int = 0):
        """Load the ONNX model, selecting the best execution provider.

        intra_op_threads caps the threads per inference so parallel syntheses
        partition the CPU instead of oversubscribing it (0 = onnxruntime default).
        """
        import onnxruntime as ort
        import kokoro_onnx
        from kokoro_onnx import Kokoro

        from .providers import create_session

        cap_phoneme_length(kokoro_onnx)

        def factory(providers):
            so = ort.SessionOptions()
            so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
            if intra_op_threads > 0:
                so.intra_op_num_threads = intra_op_threads
            return ort.InferenceSession(model_path, sess_options=so, providers=providers)

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
        # Kokoro expects locale codes (e.g. "en-us"); fall back to the voice's
        # language for generic 2-letter codes like "en".
        lang = language if language and "-" in language else self._voices[voice_id].language

        # Fast path: synthesize the whole chunk.
        whole = self._try_create(text, voice_id, speed, lang)
        if whole is not None:
            return Synthesis(whole, self._sample_rate, [])

        # The phonemizer (misaki/espeak) can fail on rare token sequences
        # ("number of lines ... must be equal"). Retry sentence by sentence so a
        # stubborn sentence degrades to a SHORT silence instead of silencing the
        # whole chunk — the document keeps reading.
        parts: list[np.ndarray] = []
        for sentence in _split_sentences(text):
            audio = self._try_create(sentence, voice_id, speed, lang)
            if audio is None:
                words = max(1, len(sentence.split()))
                seconds = min(words * 0.4, 8.0)
                logger.warning("sentence failed all variants; %d words -> %.1fs silence",
                               words, seconds)
                audio = np.zeros(int(seconds * self._sample_rate), dtype=np.float32)
            parts.append(audio)

        merged = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
        return Synthesis(merged, self._sample_rate, [])

    def _try_create(self, text: str, voice_id: str, speed: float, lang: str):
        """Synthesize `text`, trying progressively safer renderings. Returns the
        float32 samples, or None if every variant failed to phonemize."""
        for cleaned in synthesis_variants(text):
            if not cleaned:
                continue
            try:
                samples, _ = self._kokoro.create(cleaned, voice=voice_id, speed=speed, lang=lang)
                return np.asarray(samples, dtype=np.float32)
            except Exception:  # noqa: BLE001 — fall back to a safer rendering
                continue
        return None
