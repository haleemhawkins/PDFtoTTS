"""Worker health/readiness state (design §3.3)."""
from __future__ import annotations

import common_pb2


class HealthState:
    """Reports NOT_READY until the model is resident, then SERVING."""

    def __init__(self) -> None:
        self._status = common_pb2.HealthResponse.STATUS_NOT_READY
        self._detail = "starting"

    @property
    def status(self):
        return self._status

    @property
    def detail(self) -> str:
        return self._detail

    def set_serving(self, detail: str) -> None:
        self._status = common_pb2.HealthResponse.STATUS_SERVING
        self._detail = detail

    def set_not_ready(self, detail: str) -> None:
        self._status = common_pb2.HealthResponse.STATUS_NOT_READY
        self._detail = detail
