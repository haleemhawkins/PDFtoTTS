import os

import numpy as np
import soundfile as sf

from worker.audio import write_wav


def test_writes_pcm16_mono_wav(tmp_path):
    samples = np.linspace(-0.5, 0.5, 24000, dtype=np.float32)
    path = os.path.join(tmp_path, "nested", "00001.wav")

    duration = write_wav(path, samples, 24000)

    assert os.path.exists(path)  # parent dirs created
    assert duration == 1.0  # 24000 samples / 24000 Hz

    info = sf.info(path)
    assert info.samplerate == 24000
    assert info.channels == 1
    # REGRESSION GUARD: must be 16-bit PCM, NOT 32-bit float. Browser Web Audio
    # decodeAudioData rejects IEEE-float ("FLOAT") WAV, so float32 audio silently
    # failed to play in the reader with no error. Do not change this to FLOAT.
    assert info.subtype == "PCM_16"

    data, sr = sf.read(path, dtype="float32")
    assert sr == 24000
    assert len(data) == 24000
    # 16-bit quantization is lossy but the waveform must survive the round-trip.
    assert np.allclose(data, samples, atol=1e-3)
