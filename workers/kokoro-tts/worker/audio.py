"""WAV writing for synthesized audio (design §3.4: PCM float32, mono)."""
from __future__ import annotations

import os

import numpy as np
import soundfile as sf


def write_wav(path: str, samples, sample_rate: int) -> float:
    """
    Write mono float32 PCM to `path` and return its duration in seconds.
    Parent directories are created as needed.
    """
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)

    data = np.asarray(samples, dtype=np.float32).reshape(-1)
    sf.write(path, data, sample_rate, subtype="FLOAT")
    return len(data) / float(sample_rate)
