"""Tests for the pure coordinate/word-splitting logic (no torch/surya needed)."""
from worker.backend import split_line_into_words


def test_splits_line_into_words_left_to_right():
    # A line "Hello world" spanning pixels x:100..300, y:50..80 at 2x scale (144dpi)
    # on a 1000pt-tall page. Words must come out left-to-right with PDF (bottom-left)
    # coords and a flipped y.
    words = split_line_into_words(
        "Hello world", (100, 50, 300, 80),
        page_height_pt=1000.0, scale=2.0, page=3, confidence=0.9)

    assert [w.text for w in words] == ["Hello", "world"]
    assert all(w.page == 3 for w in words)
    # x increases left-to-right; second word starts right of the first.
    assert words[1].x > words[0].x
    # y is flipped from the top-left pixel box: page_height - y2/scale = 1000 - 40.
    assert abs(words[0].y - 960.0) < 1e-6
    # height is the line height in points: (80-50)/2 = 15.
    assert abs(words[0].height - 15.0) < 1e-6
    # Boxes stay within the line's horizontal pixel extent (100..300 -> 50..150 pt).
    assert words[0].x >= 50.0 - 1e-6
    assert words[-1].x + words[-1].width <= 150.0 + 1e-6


def test_empty_line_yields_no_words():
    assert split_line_into_words("   ", (0, 0, 10, 10), 100.0, 1.0, 1, 1.0) == []


def test_wider_word_gets_more_width():
    words = split_line_into_words(
        "a longword", (0, 0, 220, 20), page_height_pt=100.0, scale=1.0, page=1,
        confidence=1.0)
    by_text = {w.text: w for w in words}
    assert by_text["longword"].width > by_text["a"].width
