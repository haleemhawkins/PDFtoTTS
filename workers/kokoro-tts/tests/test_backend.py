from types import SimpleNamespace

from worker.backend import STYLE_ROWS, cap_phoneme_length


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
