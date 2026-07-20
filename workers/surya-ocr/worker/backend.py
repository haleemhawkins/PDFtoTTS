"""OCR backend abstraction + the real Surya implementation.

The gRPC layer depends only on the OcrBackend protocol, so it can be tested with a
fake. The real backend renders each PDF page with pypdfium2 and runs Surya
(detection + recognition) on PyTorch — which sees the AMD GPU as "cuda" via
torch-rocm, exactly like the WhisperX worker. Heavy imports (torch/surya/pdfium)
are deferred to load()/recognize() so importing this module for the unit tests
stays light.

Surya returns line-level boxes in *image pixel* coordinates with a top-left origin.
We convert them to PDF user space (origin bottom-left, points) here so the words
line up 1:1 with PdfPig-extracted words and the frontend highlight overlay, and we
split each line into word boxes proportionally to character widths (Surya does not
emit per-word boxes reliably across versions).
"""
from __future__ import annotations

import logging
import os
from dataclasses import dataclass, field
from typing import Protocol, runtime_checkable

logger = logging.getLogger(__name__)

# Render DPI for OCR. Higher = better recognition on small/faint print, at the cost
# of memory and time. 300 is the accuracy sweet spot for book scans (low-DPI raster
# is the top OCR error source); lower via env if memory-tight.
_RENDER_DPI = max(72, int(os.environ.get("OCR_RENDER_DPI", "300")))


@dataclass(frozen=True)
class OcrWord:
    text: str
    page: int  # 1-based
    # PDF user space, origin bottom-left, points.
    x: float
    y: float
    width: float
    height: float
    confidence: float


@dataclass(frozen=True)
class OcrResult:
    words: list[OcrWord] = field(default_factory=list)
    page_count: int = 0


@runtime_checkable
class OcrBackend(Protocol):
    def recognize(self, pdf_path: str, language: str) -> OcrResult: ...


def split_line_into_words(
    text: str,
    bbox_px: tuple[float, float, float, float],
    page_height_pt: float,
    scale: float,
    page: int,
    confidence: float,
) -> list[OcrWord]:
    """Split a recognized line (text + pixel bbox x1,y1,x2,y2 top-left origin) into
    word boxes in PDF points. Horizontal extent is allocated proportionally to each
    word's character count (plus the spaces between), which tracks left-to-right
    text closely enough for highlighting. Vertical extent is the whole line."""
    x1, y1, x2, y2 = bbox_px
    words = text.split()
    if not words:
        return []

    # PDF y is measured from the bottom; the line's pixel bottom edge is y2 (larger).
    pdf_y = page_height_pt - y2 / scale
    pdf_h = (y2 - y1) / scale
    line_left_pt = x1 / scale
    line_width_pt = (x2 - x1) / scale

    # Character "slots": each word contributes len(word), each gap one space.
    total_slots = sum(len(w) for w in words) + (len(words) - 1)
    total_slots = max(1, total_slots)
    pt_per_slot = line_width_pt / total_slots

    out: list[OcrWord] = []
    cursor = 0  # slots consumed so far
    for i, w in enumerate(words):
        word_x = line_left_pt + cursor * pt_per_slot
        word_w = len(w) * pt_per_slot
        out.append(OcrWord(
            text=w, page=page,
            x=word_x, y=pdf_y, width=word_w, height=pdf_h,
            confidence=confidence,
        ))
        cursor += len(w) + 1  # word + the following space
    return out


class SuryaBackend:
    """Real Surya OCR backend (PyTorch / torch-rocm)."""

    def __init__(self, device: str = "cuda") -> None:
        self._device = device
        self._det = None
        self._rec = None
        self._task = "ocr_with_boxes"  # resolved in load()

    @property
    def provider(self) -> str:
        return self._device

    def load(self) -> None:
        """Construct Surya predictors so their weights are resident before serving.
        Surya reads its device from the torch default / env; torch-rocm presents the
        AMD GPU as cuda, matching the WhisperX worker."""
        from surya.detection import DetectionPredictor
        from surya.recognition import RecognitionPredictor

        self._det = DetectionPredictor()
        self._rec = RecognitionPredictor()
        # Surya 0.14 takes a per-image task name (the old `langs` arg is gone). The
        # OCR-with-boxes task gives line text + boxes. Use the enum when importable,
        # else its string value.
        try:
            from surya.common.surya.schema import TaskNames
            self._task = TaskNames.ocr_with_boxes
        except Exception:  # noqa: BLE001
            self._task = "ocr_with_boxes"
        logger.info("surya predictors loaded (%s, render %ddpi)", self._device, _RENDER_DPI)

    def warmup(self) -> None:
        """Run one tiny recognition so GPU kernels compile before the first request."""
        try:
            from PIL import Image
            img = Image.new("RGB", (320, 64), "white")
            self._recognize_images([img], "en")
        except Exception:  # noqa: BLE001 — warmup is best-effort
            logger.warning("surya warmup failed (continuing)", exc_info=True)

    def recognize(self, pdf_path: str, language: str) -> OcrResult:
        import pypdfium2 as pdfium

        from .preprocess import preprocess

        scale = _RENDER_DPI / 72.0
        pdf = pdfium.PdfDocument(pdf_path)
        try:
            page_count = len(pdf)
            images = []
            page_sizes_pt = []
            for i in range(page_count):
                page = pdf[i]
                w_pt, h_pt = page.get_size()  # points
                page_sizes_pt.append((w_pt, h_pt))
                bitmap = page.render(scale=scale)
                # Deskew + contrast-normalize before recognition. Boxes come back in
                # this image's pixel space; deskew keeps the page axis-aligned so the
                # bbox->PDF-point conversion stays valid.
                images.append(preprocess(bitmap.to_pil().convert("RGB")))

            ocr_pages = self._recognize_images(images, language)

            words: list[OcrWord] = []
            for page_idx, page_ocr in enumerate(ocr_pages):
                _, h_pt = page_sizes_pt[page_idx]
                lines = list(getattr(page_ocr, "text_lines", []) or [])
                # Reading order: top-to-bottom by the line's vertical position. Words
                # within a line keep Surya's left-to-right order. Correct for the
                # single-column body text that dominates scanned books.
                lines.sort(key=lambda ln: _bbox(ln)[1])
                for ln in lines:
                    text = (getattr(ln, "text", "") or "").strip()
                    if not text:
                        continue
                    conf = float(getattr(ln, "confidence", 1.0) or 1.0)
                    words.extend(split_line_into_words(
                        text, _bbox(ln), h_pt, scale, page_idx + 1, conf))

            logger.info("OCR'd %d page(s) -> %d words", page_count, len(words))
            return OcrResult(words=words, page_count=page_count)
        finally:
            pdf.close()

    def _recognize_images(self, images, language: str):
        # Surya 0.14: recognition runs detect-then-recognize given the detection
        # predictor. The 2nd arg is a per-image task name (not langs); Surya is
        # multilingual without a hint. Older releases used `detection_predictor=`,
        # so fall back to that keyword if `det_predictor` isn't accepted.
        task_names = [self._task] * len(images)
        try:
            return self._rec(images, task_names=task_names, det_predictor=self._det)
        except TypeError:
            return self._rec(images, task_names, detection_predictor=self._det)


def _bbox(line) -> tuple[float, float, float, float]:
    """Surya TextLine bbox as (x1, y1, x2, y2) in image pixels (top-left origin)."""
    b = getattr(line, "bbox", None)
    if b is not None and len(b) == 4:
        return float(b[0]), float(b[1]), float(b[2]), float(b[3])
    # Fall back to the polygon's axis-aligned bounds.
    poly = getattr(line, "polygon", None) or []
    xs = [float(p[0]) for p in poly]
    ys = [float(p[1]) for p in poly]
    return (min(xs), min(ys), max(xs), max(ys)) if xs else (0.0, 0.0, 0.0, 0.0)
