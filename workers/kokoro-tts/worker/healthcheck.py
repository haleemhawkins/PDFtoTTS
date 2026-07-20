"""Docker HEALTHCHECK entrypoint: exit 0 only when the worker is SERVING."""
from __future__ import annotations

import os
import sys

import grpc

import common_pb2
import kokoro_pb2_grpc


def main() -> int:
    port = os.environ.get("GRPC_PORT", "50051")
    try:
        with grpc.insecure_channel(f"localhost:{port}") as channel:
            stub = kokoro_pb2_grpc.KokoroTtsStub(channel)
            resp = stub.Health(common_pb2.HealthRequest(), timeout=3)
            return 0 if resp.status == common_pb2.HealthResponse.STATUS_SERVING else 1
    except Exception:  # noqa: BLE001
        return 1


if __name__ == "__main__":
    sys.exit(main())
