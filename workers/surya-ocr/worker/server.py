"""Surya OCR gRPC service. Mirrors the WhisperX worker's structure."""
from __future__ import annotations

import logging
import os
import threading

import grpc

import common_pb2
import ocr_pb2
import ocr_pb2_grpc

from .backend import OcrBackend
from .errors import InvalidInput, ResourceExhausted, TransientError
from .health import HealthState
from shared.paths import UnsafePath, resolve_under_data

logger = logging.getLogger(__name__)


class OcrServicer(ocr_pb2_grpc.OcrServicer):
    def __init__(self, backend: OcrBackend, data_dir: str = "/data",
                 max_concurrency: int = 1, health: HealthState | None = None):
        self._backend = backend
        self._data_dir = data_dir
        self._sem = threading.Semaphore(max(1, max_concurrency))
        self._health = health or HealthState()

    def Recognize(self, request, context):
        if not (request.pdf_path or "").strip():
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "pdf_path is empty")

        try:
            full_path = resolve_under_data(self._data_dir, request.pdf_path)
        except UnsafePath as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        if not os.path.exists(full_path):
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"pdf not found: {request.pdf_path}")

        self._sem.acquire()
        try:
            result = self._backend.recognize(full_path, request.language or "")
        except InvalidInput as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        except ResourceExhausted as exc:
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, str(exc))
        except TransientError as exc:
            context.abort(grpc.StatusCode.UNAVAILABLE, str(exc))
        except Exception as exc:  # noqa: BLE001
            logger.exception("OCR failed")
            context.abort(grpc.StatusCode.INTERNAL, str(exc))
        finally:
            self._sem.release()

        response = ocr_pb2.OcrResponse(page_count=result.page_count)
        for w in result.words:
            response.words.add(
                text=w.text, page=w.page,
                x=w.x, y=w.y, width=w.width, height=w.height,
                confidence=w.confidence,
            )
        return response

    def Health(self, request, context):
        return common_pb2.HealthResponse(status=self._health.status, detail=self._health.detail)


def serve() -> None:
    from concurrent import futures

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    port = os.environ.get("GRPC_PORT", "50053")
    data_dir = os.environ.get("DATA_DIR", "/data")
    max_concurrency = int(os.environ.get("MAX_CONCURRENCY", "1"))

    health = HealthState()
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=max_concurrency + 2))
    server.add_insecure_port(f"[::]:{port}")

    from .backend import SuryaBackend

    device = os.environ.get("OCR_DEVICE", "cuda")  # ROCm presents as "cuda" to torch
    backend = SuryaBackend(device)
    backend.load()  # keep Surya weights resident before serving
    backend.warmup()  # compile GPU kernels so the first OCR is fast

    ocr_pb2_grpc.add_OcrServicer_to_server(
        OcrServicer(backend, data_dir, max_concurrency, health), server)

    server.start()
    health.set_serving(f"{device}, surya loaded")
    logger.info("surya-ocr serving on :%s (%s)", port, device)
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
