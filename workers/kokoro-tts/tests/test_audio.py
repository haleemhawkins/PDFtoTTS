import os

import numpy as np
import soundfile as sf

from worker.audio import write_wav


def test_writes_float32_mono_wav(tmp_path):
    samples = np.linspace(-0.5, 0.5, 24000, dtype=np.float32)
    path = os.path.join(tmp_path, "nested", "00001.wav")

    duration = write_wav(path, samples, 24000)

    assert os.path.exists(path)
    assert duration == 1.0  # 24000 samples / 24000 Hz

    data, sr = sf.read(path, dtype="float32")
    info = sf.info(path)
    assert sr == 24000
    assert info.channels == 1
    assert info.subtype == "FLOAT"
    assert len(data) == 24000
