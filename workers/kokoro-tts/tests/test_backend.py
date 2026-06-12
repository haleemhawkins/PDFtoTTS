import re

from worker.backend import sanitize_text, synthesis_variants


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
