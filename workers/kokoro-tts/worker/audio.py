"""WAV writing for synthesized audio (mono PCM_16 — browsers cannot reliably
decode IEEE-float WAV via decodeAudioData)."""
from __future__ import annotations

import os

import numpy as np
import soundfile as sf


def write_wav(path: str, samples, sample_rate: int) -> float:
    """
    Write mono PCM_16 WAV to `path` and return its duration in seconds.
    Parent directories are created as needed.

    Input samples may be float in [-1, 1]; soundfile scales them to int16.
    Do NOT switch to float32 subtype — decodeAudioData fails silently on it.
    """
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)

    data = np.asarray(samples, dtype=np.float32).reshape(-1)
    # 16-bit PCM, NOT 32-bit float: browser Web Audio decodeAudioData is
    # unreliable with IEEE-float WAV (silent decode failure), so float32 audio
    # never plays in the reader. PCM_16 is universally decodable and matches the
    # mock writer. soundfile scales the [-1, 1] float samples to int16.
    sf.write(path, data, sample_rate, subtype="PCM_16")
    return len(data) / float(sample_rate)
