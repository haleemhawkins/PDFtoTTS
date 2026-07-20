"""Tests for the deskew angle estimator (needs numpy + opencv; runs in-image)."""
import numpy as np
import pytest

cv2 = pytest.importorskip("cv2")
from worker.preprocess import estimate_skew_angle, preprocess  # noqa: E402


def _text_page(skew_deg: float) -> np.ndarray:
    """White page with evenly spaced horizontal black 'text' bars, rotated by
    skew_deg (positive = tilted)."""
    img = np.full((600, 800), 255, np.uint8)
    for y in range(60, 560, 40):
        img[y:y + 12, 80:720] = 0
    if skew_deg:
        m = cv2.getRotationMatrix2D((400, 300), skew_deg, 1.0)
        img = cv2.warpAffine(img, m, (800, 600), borderValue=255)
    return img


def test_detects_no_skew_on_straight_page():
    assert abs(estimate_skew_angle(_text_page(0.0))) < 1.0


@pytest.mark.parametrize("skew", [-6.0, -3.0, 3.0, 6.0])
def test_recovers_correcting_angle(skew):
    page = _text_page(skew)
    # The estimator returns the angle to APPLY to deskew, i.e. roughly -skew.
    est = estimate_skew_angle(page, max_deg=10.0)
    assert abs(est - (-skew)) < 1.5

    # After applying it, residual skew is near zero.
    from worker.preprocess import _rotate
    residual = estimate_skew_angle(_rotate(page, est, 255.0))
    assert abs(residual) < 1.5


def test_preprocess_returns_rgb_same_size():
    from PIL import Image
    src = Image.fromarray(_text_page(4.0)).convert("RGB")
    out = preprocess(src)
    assert out.mode == "RGB"
    assert out.size == src.size
