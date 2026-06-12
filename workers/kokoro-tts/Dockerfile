# Kokoro TTS worker. Build context = repo root (needs /proto):
#   docker build -f workers/kokoro-tts/Dockerfile -t kokoro-tts .
FROM rocm/dev-ubuntu-22.04:6.2 AS base

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-pip espeak-ng curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY workers/kokoro-tts/requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt
# CPU onnxruntime. We deliberately do NOT use onnxruntime-rocm: AMD's prebuilt
# ROCm wheel has no kernels for this RDNA3 GPU (gfx1101, even spoofed to gfx1100),
# so the ROCm provider loads but crashes at inference with hipErrorNoBinaryForGpu.
# Kokoro on CPU runs ~3x faster than realtime, which is ample for streaming; the
# GPU is used where it works (WhisperX alignment via torch-rocm). kokoro-onnx
# already pulls onnxruntime as a dep above; install it explicitly to be safe.
RUN pip3 install --no-cache-dir onnxruntime

# Contracts + source, then generate the gRPC stubs into /app/generated.
COPY proto/ /proto/
COPY workers/kokoro-tts/ /app/
RUN mkdir -p /app/generated \
 && python3 -m grpc_tools.protoc -I /proto \
      --python_out=/app/generated --grpc_python_out=/app/generated \
      common.proto kokoro.proto

ENV PYTHONPATH=/app:/app/generated \
    GRPC_PORT=50051 \
    DATA_DIR=/data \
    MODEL_DIR=/models/kokoro \
    MAX_CONCURRENCY=1 \
    HSA_OVERRIDE_GFX_VERSION=11.0.0

RUN chmod +x /app/entrypoint.sh

HEALTHCHECK --interval=10s --timeout=5s --retries=12 --start-period=120s \
  CMD python3 -m worker.healthcheck || exit 1

# Downloads the model on first run (into the modelcache volume), then serves.
CMD ["/app/entrypoint.sh"]
