"""ONNX Runtime execution-provider selection with fallback (design §3.2)."""
from __future__ import annotations

import logging
from typing import Callable, Sequence

logger = logging.getLogger(__name__)

DEFAULT_PRIORITY = [
    "ROCMExecutionProvider",
    "CUDAExecutionProvider",
    "CPUExecutionProvider",
]


def select_providers(
    available: Sequence[str],
    priority: Sequence[str] | None = None,
) -> list[str]:
    """Return available providers in priority order, always keeping a CPU fallback."""
    priority = list(priority) if priority is not None else DEFAULT_PRIORITY
    ordered = [p for p in priority if p in available]
    if "CPUExecutionProvider" in available and "CPUExecutionProvider" not in ordered:
        ordered.append("CPUExecutionProvider")
    return ordered


def create_session(
    available: Sequence[str],
    session_factory: Callable[[list[str]], object],
    priority: Sequence[str] | None = None,
) -> tuple[object, str]:
    """
    Try each provider in priority order until a session is created.

    `session_factory` takes a one-element provider list and returns an
    InferenceSession (injected so this is testable without onnxruntime).
    Returns (session, chosen_provider) or raises if none work.
    """
    last_error: Exception | None = None
    for provider in select_providers(available, priority):
        try:
            session = session_factory([provider])
            logger.info("ONNX session created with %s", provider)
            return session, provider
        except Exception as exc:  # noqa: BLE001 — fall through to the next provider
            last_error = exc
            logger.warning("provider %s unavailable: %s", provider, exc)
    raise RuntimeError(f"no usable ONNX execution provider (last error: {last_error})")
