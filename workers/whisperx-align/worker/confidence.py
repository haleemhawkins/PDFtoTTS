"""Low-confidence flagging for aligned words (design §4.5).

The worker only *flags* low-confidence words (still returning their best-estimate
timing); the orchestrator decides how to interpolate flagged spans.
"""
from __future__ import annotations

DEFAULT_THRESHOLD = 0.30


def is_low_confidence(confidence: float, threshold: float = DEFAULT_THRESHOLD) -> bool:
    return confidence < threshold


def flag(confidences, threshold: float = DEFAULT_THRESHOLD) -> list[bool]:
    return [is_low_confidence(c, threshold) for c in confidences]
