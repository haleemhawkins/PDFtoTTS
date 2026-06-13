import re

import numpy as np
import pytest

from worker.backend import sanitize_text, synthesis_variants, trim_silence


def test_sanitize_strips_bullets_and_collapses_whitespace():
    # Consecutive bullets were the real trigger for the phonemizer line-count error.
    assert sanitize_text("Paylocity ● ● Software") == "Paylocity Software"
    assert sanitize_text("line one\n\nline\ttwo") == "line one line two"
    assert sanitize_text("keep-the-hyphen here") == "keep-the-hyphen here"


def test_synthesis_variants_get_progressively_safer():
    vs = list(synthesis_variants("Foo — bar (May 2022) ● baz!"))
    assert len(vs) == 3
    assert "●" not in vs[0] and "—" not in vs[0]  # symbols gone first
    assert "(" not in vs[1]  # then non-speech punctuation
    assert re.fullmatch(r"[A-Za-z0-9 ]*", vs[2])  # finally alphanumeric only


def test_trim_silence_removes_excess_lead_and_tail():
    sr = 24000
    speech = np.ones(sr, dtype=np.float32) * 0.5  # 1s of "audio"
    clip = np.concatenate([
        np.zeros(int(0.4 * sr), dtype=np.float32),  # 400ms lead
        speech,
        np.zeros(int(0.5 * sr), dtype=np.float32),  # 500ms tail
    ])
    out = trim_silence(clip, sr)
    # keeps the speech + small pads (head 20ms, tail 140ms), drops the rest.
    assert len(out) / sr == pytest.approx(1.0 + 0.02 + 0.14, abs=0.01)


def test_trim_silence_handles_all_silence():
    sr = 24000
    z = np.zeros(sr, dtype=np.float32)
    assert trim_silence(z, sr) is z  # nothing to trim, returned unchanged
