"""Per-page image preprocessing applied before Surya recognition.

Two defaults that materially help recognition on real scans without hurting a
deep OCR model: deskew (straighten tilted pages) and CLAHE contrast normalization
(even out faint/uneven lighting). Denoise and binarization are available but OFF
by default — unlike Tesseract, neural OCR usually reads grayscale/contrast-
normalized images better than harshly binarized ones, and denoise can blur small
type.

Deskew uses the projection-profile method (rotate by candidate angles, pick the
one that maximizes the sharpness of the horizontal ink profile). It is robust and
independent of OpenCV's version-dependent minAreaRect angle convention. Angle
search runs on a downscaled copy so it stays cheap.
"""
from __future__ import annotations

import logging
import os

import numpy as np

logger = logging.getLogger(__name__)


def _flag(name: str, default: str) -> bool:
    return os.environ.get(name, default).strip().lower() not in ("0", "false", "no", "off")


_PREPROCESS = _flag("OCR_PREPROCESS", "1")
_DESKEW = _flag("OCR_DESKEW", "1")
_CONTRAST = _flag("OCR_CONTRAST", "1")
_DENOISE = _flag("OCR_DENOISE", "0")
_BINARIZE = _flag("OCR_BINARIZE", "0")
_MAX_SKEW_DEG = float(os.environ.get("OCR_MAX_SKEW_DEG", "10"))


def _rotate(img: np.ndarray, angle: float, border_value: float):
    import cv2
    h, w = img.shape[:2]
    m = cv2.getRotationMatrix2D((w / 2.0, h / 2.0), angle, 1.0)
    return cv2.warpAffine(img, m, (w, h), flags=cv2.INTER_LINEAR,
                          borderMode=cv2.BORDER_CONSTANT, borderValue=border_value)


def estimate_skew_angle(gray: np.ndarray, max_deg: float = 10.0, step: float = 0.5) -> float:
    """Return the rotation (degrees) to APPLY to deskew `gray`: the angle whose
    rotation best aligns text rows (maximizes the variance of the row-ink profile).
    0.0 when there is too little ink to judge."""
    import cv2
    h, w = gray.shape
    scale = 1000.0 / max(h, w)
    small = gray
    if scale < 1.0:
        small = cv2.resize(gray, (max(1, int(w * scale)), max(1, int(h * scale))),
                           interpolation=cv2.INTER_AREA)
    ink = cv2.threshold(small, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)[1]
    ink = (ink > 0).astype(np.float32)
    if ink.sum() < 100:
        return 0.0

    best_angle, best_score = 0.0, -1.0
    for a in np.arange(-max_deg, max_deg + step, step):
        rot = _rotate(ink, float(a), 0.0)
        proj = rot.sum(axis=1)
        score = float(np.sum(np.diff(proj) ** 2))  # sharper row bands -> higher
        if score > best_score:
            best_score, best_angle = score, float(a)
    return best_angle


def preprocess(image):
    """Deskew + contrast-normalize a rendered page (PIL RGB) for OCR, returning a
    PIL RGB image. Honors the OCR_* env toggles; a no-op when OCR_PREPROCESS=0."""
    if not _PREPROCESS:
        return image
    import cv2
    from PIL import Image

    gray = np.array(image.convert("L"))

    if _DESKEW:
        angle = estimate_skew_angle(gray, _MAX_SKEW_DEG)
        if abs(angle) >= 0.3:
            gray = _rotate(gray, angle, 255.0)
            logger.debug("deskewed page by %.1f deg", angle)

    if _CONTRAST:
        gray = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)

    if _DENOISE:
        gray = cv2.fastNlMeansDenoising(gray, None, 10, 7, 21)

    if _BINARIZE:
        gray = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                     cv2.THRESH_BINARY, 31, 15)

    return Image.fromarray(gray).convert("RGB")
