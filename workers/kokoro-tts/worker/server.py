"""Kokoro TTS gRPC service (design §3)."""
from __future__ import annotations

import logging
import os
import threading

import grpc

import common_pb2
import kokoro_pb2
import kokoro_pb2_grpc

from .audio import write_wav
from .backend import SynthBackend
from .errors import InvalidInput, ResourceExhausted, TransientError
from .health import HealthState

logger = logging.getLogger(__name__)


class KokoroServicer(kokoro_pb2_grpc.KokoroTtsServicer):
    def __init__(self, backend: SynthBackend, data_dir: str = "/data",
                 max_concurrency: int = 1, health: HealthState | None = None):
        self._backend = backend
        self._data_dir = data_dir
        # Bound concurrent inference so we don't exhaust shared VRAM (§3.5).
        self._sem = threading.Semaphore(max(1, max_concurrency))
        self._health = health or HealthState()

    def Synthesize(self, request, context):
        text = (request.text or "").strip()
        if not text:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "text is empty")
        if not request.out_path:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "out_path is required")
        if not self._backend.is_voice(request.voice_id):
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"unknown voice '{request.voice_id}'")

        speed = request.speed if request.speed > 0 else 1.0
        language = request.language or "en"

        self._sem.acquire()
        try:
            result = self._backend.synthesize(text, request.voice_id, speed, language)
        except InvalidInput as exc:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
        except ResourceExhausted as exc:
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, str(exc))
        except TransientError as exc:
            context.abort(grpc.StatusCode.UNAVAILABLE, str(exc))
        except Exception as exc:  # noqa: BLE001
            logger.exception("synthesis failed")
            context.abort(grpc.StatusCode.INTERNAL, str(exc))
        finally:
            self._sem.release()

        full_path = os.path.join(self._data_dir, request.out_path)
        try:
            duration = write_wav(full_path, result.samples, result.sample_rate)
        except OSError as exc:
            context.abort(grpc.StatusCode.INTERNAL, f"failed to write audio: {exc}")

        response = kokoro_pb2.SynthesizeResponse(
            audio_path=request.out_path,
            sample_rate=result.sample_rate,
            channels=1,
            duration_seconds=duration,
        )
        for p in result.phonemes:
            response.phonemes.add(
                phoneme=p.phoneme, start_seconds=p.start_seconds, end_seconds=p.end_seconds)
        return response

    def ListVoices(self, request, context):
        response = common_pb2.ListVoicesResponse()
        for v in self._backend.list_voices():
            response.voices.add(id=v.id, label=v.label, language=v.language, gender=v.gender)
        return response

    def Health(self, request, context):
        return common_pb2.HealthResponse(status=self._health.status, detail=self._health.detail)


def serve() -> None:
    from concurrent import futures

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    port = os.environ.get("GRPC_PORT", "50051")
    data_dir = os.environ.get("DATA_DIR", "/data")
    max_concurrency = int(os.environ.get("MAX_CONCURRENCY", "1"))
    model_dir = os.environ.get("MODEL_DIR", "/models/kokoro")

    health = HealthState()
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=max_concurrency + 2))
    server.add_insecure_port(f"[::]:{port}")

    # Load the model, then start serving and mark healthy.
    from .backend import KokoroBackend

    backend = KokoroBackend.load(
        os.path.join(model_dir, "kokoro-v1.0.onnx"),
        os.path.join(model_dir, "voices-v1.0.bin"),
    )
    kokoro_pb2_grpc.add_KokoroTtsServicer_to_server(
        KokoroServicer(backend, data_dir, max_concurrency, health), server)

    server.start()
    health.set_serving(f"{backend.provider}, model loaded")
    logger.info("kokoro-tts serving on :%s (%s)", port, backend.provider)
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
