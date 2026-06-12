import re
from types import SimpleNamespace

from worker.backend import (
    STYLE_ROWS,
    cap_phoneme_length,
    sanitize_text,
    synthesis_variants,
)


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


def test_caps_phoneme_length_below_style_array_size():
    # kokoro_onnx ships MAX_PHONEME_LENGTH == STYLE_ROWS (510), which lets a batch
    # index voice[510] out of bounds. It must be capped strictly below the row
    # count so voice[len(tokens)] is always valid.
    mod = SimpleNamespace(MAX_PHONEME_LENGTH=STYLE_ROWS)

    effective = cap_phoneme_length(mod)

    assert effective == STYLE_ROWS - 1
    assert mod.MAX_PHONEME_LENGTH == STYLE_ROWS - 1
    assert mod.MAX_PHONEME_LENGTH < STYLE_ROWS  # the actual invariant


def test_leaves_already_safe_length_untouched():
    mod = SimpleNamespace(MAX_PHONEME_LENGTH=400)

    assert cap_phoneme_length(mod) == 400
    assert mod.MAX_PHONEME_LENGTH == 400


def test_cap_is_idempotent():
    mod = SimpleNamespace(MAX_PHONEME_LENGTH=STYLE_ROWS)
    cap_phoneme_length(mod)
    cap_phoneme_length(mod)
    assert mod.MAX_PHONEME_LENGTH == STYLE_ROWS - 1
