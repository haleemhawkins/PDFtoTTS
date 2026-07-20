"""Worker error taxonomy → gRPC status mapping (design §3.6)."""
from __future__ import annotations


class WorkerError(Exception):
    """Base for backend errors that map to a gRPC status."""


class InvalidInput(WorkerError):
    """Non-retryable bad input → INVALID_ARGUMENT."""


class TransientError(WorkerError):
    """Transient runtime/GPU error → UNAVAILABLE (retryable)."""


class ResourceExhausted(WorkerError):
    """Out of memory → RESOURCE_EXHAUSTED (retryable after delay)."""
