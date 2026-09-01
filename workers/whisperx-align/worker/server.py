"""WhisperX forced-alignment gRPC service (design §4)."""
from __future__ import annotations

import logging
import os
import threading

import grpc

import alignment_pb2
import alignment_pb2_grpc
import common_pb2

from .backend import AlignBackend
from .confidence import is_low_confidence
from .errors import InvalidInput, ResourceExhausted, TransientError
from .health import HealthState
from shared.paths import UnsafePath, resolve_under_data

logger = logging.getLogger(__name__)


class AlignmentServicer(alignment_pb2_grpc.AlignmentServicer):
    def __init__(self, backend: AlignBackend, data_dir: str = "/data",
                 threshold: float = 0.30, max_concurrency: int = 1,
                 health: HealthState | None = None):
        self._backend = backend
        self._data_dir = data_dir
        self._threshold = threshold
        self._sem = threading.Semaphore(max(1, max_concurrency))
        self._health = health or HealthState()

    def Align(self, request, context):
        if not (request.transcript or "").strip():
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "transcript is empty")

        try:
            full_path = resolve_under_data(self._data_dir, request.audio_path)
        except UnsafePath as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        if not os.path.exists(full_path):
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"audio not found: {request.audio_path}")

        language = request.language or "en"

        self._sem.acquire()
        try:
            result = self._backend.align(full_path, request.transcript, language)
        except InvalidInput as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        except ResourceExhausted as exc:
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, str(exc))
        except TransientError as exc:
            context.abort(grpc.StatusCode.UNAVAILABLE, str(exc))
        except Exception as exc:  # noqa: BLE001
            logger.exception("alignment failed")
            context.abort(grpc.StatusCode.INTERNAL, str(exc))
        finally:
            self._sem.release()

        response = alignment_pb2.AlignResponse(audio_duration_ms=result.audio_duration_ms)
        for w in result.words:
            response.words.add(
                text=w.text,
                start_ms=w.start_ms,
                end_ms=w.end_ms,
                confidence=w.confidence,
                low_confidence=is_low_confidence(w.confidence, self._threshold),
            )
        return response

    def Health(self, request, context):
        return common_pb2.HealthResponse(status=self._health.status, detail=self._health.detail)


def serve() -> None:
    from concurrent import futures

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    port = os.environ.get("GRPC_PORT", "50052")
    data_dir = os.environ.get("DATA_DIR", "/data")
    threshold = float(os.environ.get("ALIGN_CONFIDENCE_THRESHOLD", "0.30"))
    language = os.environ.get("ALIGN_LANGUAGE", "en")
    max_concurrency = int(os.environ.get("MAX_CONCURRENCY", "1"))

    health = HealthState()
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=max_concurrency + 2))
    server.add_insecure_port(f"[::]:{port}")

    from .backend import WhisperXBackend

    device = os.environ.get("ALIGN_DEVICE", "cuda")  # ROCm presents as "cuda" to torch
    backend = WhisperXBackend(device)
    backend.preload(language)  # keep wav2vec2 resident before serving
    backend.warmup(language)  # compile GPU kernels so the first align is fast

    alignment_pb2_grpc.add_AlignmentServicer_to_server(
        AlignmentServicer(backend, data_dir, threshold, max_concurrency, health), server)

    server.start()
    health.set_serving(f"{device}, wav2vec2 loaded ({language})")
    logger.info("whisperx-align serving on :%s (%s)", port, device)
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
