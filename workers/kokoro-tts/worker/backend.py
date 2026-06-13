"""Synthesis backend abstraction + the real Kokoro implementation.

The gRPC layer depends only on the SynthBackend protocol, so it can be tested
with a fake. The real backend uses the PyTorch `kokoro` package (KPipeline) so it
runs on the GPU via torch-rocm; heavy imports (torch/kokoro) are deferred to
load()/synthesize() so importing this module for the unit tests stays light.
"""
from __future__ import annotations

import logging
import os
import re
from dataclasses import dataclass, field
from typing import Iterator, Protocol, runtime_checkable

import numpy as np

logger = logging.getLogger(__name__)

# True non-speech symbols (bullets, list/section marks): no pronunciation and a
# run of them breaks the phonemizer ("number of lines ... must be equal"). Dropped.
_DROP = "•◦▪▫‣⁃∙·●○◆◇■□▶▷§¶†‡※"

# Fancy punctuation -> plain forms the voice reads with the RIGHT prosody. Smart
# apostrophes must become straight or contractions ("don't") mispronounce; em/en
# dashes become a comma pause (also un-gluing "word—word"); ellipsis trails off.
_PUNCT = {
    "‘": "'", "’": "'", "‚": "'", "‛": "'",      # ‘ ’ ‚ ‛ -> '
    "“": '"', "”": '"', "„": '"', "‟": '"',      # “ ” „ ‟ -> "
    "–": ", ", "—": ", ", "―": ", ", "−": "-",   # – — ― −
    "…": "...",                                                  # … -> ...
    " ": " ", " ": " ", " ": " ", "​": "",        # nbsp/thin/zwsp
}
_PUNCT_TABLE = str.maketrans(_PUNCT)


def normalize_punctuation(text: str) -> str:
    """Map smart quotes / dashes / ellipsis / exotic spaces to plain equivalents so
    the voice gets correct prosody (and contractions aren't mangled)."""
    return text.translate(_PUNCT_TABLE)


def _collapse(text: str) -> str:
    """Tidy whitespace and punctuation spacing: non-printables -> space, collapse
    runs, no space before a mark, and dedupe stacked separators (from dash->comma)."""
    text = "".join(ch if ch.isprintable() else " " for ch in text)
    text = re.sub(r"\s+", " ", text)
    text = re.sub(r"\s+([,.;:!?])", r"\1", text)          # no space before punctuation
    text = re.sub(r"([,;:])(?:\s*[,;:])+", r"\1", text)   # collapse stacked separators
    return text.strip()


def sanitize_text(text: str) -> str:
    """Normalize punctuation for natural prosody, drop non-speech symbols, and tidy
    whitespace so the phonemizer gets clean, single-line text."""
    text = normalize_punctuation(text)
    text = re.sub("[" + re.escape(_DROP) + "]", " ", text)
    return _collapse(text)


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


# Pause inserted after each sentence so a period gets a natural beat instead of
# rushing into the next sentence. Tunable via env (milliseconds).
_SENTENCE_GAP_MS = max(0, int(os.environ.get("SENTENCE_GAP_MS", "260")))


def trim_silence(samples: np.ndarray, sample_rate: int = 24000, threshold: float = 0.01,
                 head_keep_ms: int = 20, tail_keep_ms: int = 140) -> np.ndarray:
    """Trim a chunk's leading/trailing near-silence (Kokoro adds ~300ms head and
    ~490ms tail) so chunks don't stack ~0.8s of dead air at every boundary and the
    reading flows. A small natural pad is kept on each side."""
    if samples.size == 0:
        return samples
    loud = np.flatnonzero(np.abs(samples) > threshold)
    if loud.size == 0:
        return samples
    start = max(0, int(loud[0] - head_keep_ms / 1000 * sample_rate))
    end = min(samples.size, int(loud[-1] + tail_keep_ms / 1000 * sample_rate))
    return samples[start:end]


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


# --- Real Kokoro backend (not exercised by unit tests) --------------------

_HF_REPO = "hexgrad/Kokoro-82M"
_CORE_VOICES = ["af_heart", "af_bella", "af_nicole", "am_adam", "am_michael",
                "bf_emma", "bm_george"]


def _region_gender(voice_id: str) -> tuple[str, str]:
    # Kokoro voice ids like "af_heart": region letter + gender letter.
    region = {"a": "en-us", "b": "en-gb", "e": "es", "f": "fr", "h": "hi",
              "i": "it", "j": "ja", "p": "pt-br", "z": "zh"}
    gender = {"f": "female", "m": "male"}
    lang = region.get(voice_id[:1], "en-us") if voice_id else "en-us"
    sex = gender.get(voice_id[1:2], "unknown") if len(voice_id) > 1 else "unknown"
    return lang, sex


def _fetch_voice_ids() -> list[str]:
    """List the available Kokoro voices from the model repo (falls back to a
    core set if the listing isn't reachable)."""
    try:
        from huggingface_hub import HfApi

        ids = sorted(
            os.path.basename(f)[:-3]
            for f in HfApi().list_repo_files(_HF_REPO)
            if f.startswith("voices/") and f.endswith(".pt")
        )
        if ids:
            return ids
    except Exception:  # noqa: BLE001
        logger.warning("could not list Kokoro voices from HF; using core set")
    return _CORE_VOICES


class KokoroBackend:
    """Kokoro TTS via the PyTorch `kokoro` package (KPipeline). Construct via
    :meth:`load`. A KPipeline is created per language code (the voice's first
    letter) and cached; all run on the selected torch device."""

    def __init__(self, kpipeline_cls, device: str, voice_ids, sample_rate: int = 24000):
        self._KPipeline = kpipeline_cls
        self._device = device
        self._sample_rate = sample_rate
        self._pipelines: dict[str, object] = {}
        self._voices = {v: VoiceInfo(v, v, *_region_gender(v)) for v in voice_ids}

    @classmethod
    def load(cls):
        """Build the backend on the GPU (torch-rocm) when available, else CPU, and
        warm the common pipeline so the first request doesn't pay kernel JIT."""
        import torch
        from kokoro import KPipeline

        device = "cuda" if torch.cuda.is_available() else "cpu"
        backend = cls(KPipeline, device, _fetch_voice_ids())

        # Warm up American English (the default) — the first GPU inference compiles
        # MIOpen kernels (tens of seconds on ROCm); do it before serving.
        try:
            list(backend._pipeline_for("af_heart")("Warm up the synthesis kernels.",
                                                    voice="af_heart"))
        except Exception as exc:  # noqa: BLE001 — warmup is best-effort
            logger.warning("warmup synthesis failed: %s", exc)
        logger.info("kokoro backend ready on %s with %d voices",
                    device, len(backend._voices))
        return backend

    def _pipeline_for(self, voice_id: str):
        code = (voice_id[:1] or "a")
        if code not in self._pipelines:
            self._pipelines[code] = self._KPipeline(lang_code=code, device=self._device)
        return self._pipelines[code]

    @property
    def provider(self) -> str:
        return self._device

    def list_voices(self) -> list[VoiceInfo]:
        return list(self._voices.values())

    def is_voice(self, voice_id: str) -> bool:
        return voice_id in self._voices

    def synthesize(self, text: str, voice_id: str, speed: float, language: str) -> Synthesis:
        # Synthesize sentence by sentence and insert a controlled pause after each,
        # so periods get a natural beat (Kokoro's own inter-sentence pause is too
        # short) and each sentence keeps its own intonation. A stubborn sentence
        # (phonemizer failure) degrades to a short silence instead of breaking the
        # document.
        sr = self._sample_rate
        gap = np.zeros(int(_SENTENCE_GAP_MS / 1000 * sr), dtype=np.float32)
        # A slightly longer beat after a question/exclamation reads more naturally.
        gap_strong = np.zeros(int(_SENTENCE_GAP_MS * 1.4 / 1000 * sr), dtype=np.float32)
        parts: list[np.ndarray] = []
        for sentence in _split_sentences(text):
            audio = self._try_create(sentence, voice_id, speed)
            if audio is None:
                words = max(1, len(sentence.split()))
                seconds = min(words * 0.4, 8.0)
                logger.warning("sentence failed all variants; %d words -> %.1fs silence",
                               words, seconds)
                audio = np.zeros(int(seconds * sr), dtype=np.float32)
            else:
                # Trim tight; the controlled gap below supplies the sentence pause.
                audio = trim_silence(audio, sr, head_keep_ms=10, tail_keep_ms=40)
            parts.append(audio)
            parts.append(gap_strong if sentence.rstrip()[-1:] in "?!" else gap)

        merged = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
        return Synthesis(merged, sr, [])

    def _try_create(self, text: str, voice_id: str, speed: float):
        """Synthesize `text`, trying progressively safer renderings. Returns the
        float32 samples (concatenated across KPipeline segments), or None if every
        variant failed to phonemize."""
        import torch

        pipeline = self._pipeline_for(voice_id)
        for cleaned in synthesis_variants(text):
            if not cleaned:
                continue
            try:
                audios = [r.audio for r in pipeline(cleaned, voice=voice_id, speed=speed)
                          if r.audio is not None and len(r.audio) > 0]
                if not audios:
                    continue
                return torch.cat(audios).detach().to("cpu").numpy().astype(np.float32)
            except Exception:  # noqa: BLE001 — fall back to a safer rendering
                continue
        return None
