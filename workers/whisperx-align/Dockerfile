# WhisperX forced-alignment worker. Build context = repo root (needs /proto):
#   docker build -f workers/whisperx-align/Dockerfile -t whisperx-align .
# Base image ships torch built for ROCm; torch sees the AMD GPU as "cuda".
FROM rocm/pytorch:latest AS base

WORKDIR /app

# whisperx.load_audio() shells out to the ffmpeg binary to decode audio before
# alignment; without it the worker fails with "[Errno 2] ... 'ffmpeg'".
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*

COPY workers/whisperx-align/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY proto/ /proto/
COPY workers/whisperx-align/ /app/
RUN mkdir -p /app/generated \
 && python -m grpc_tools.protoc -I /proto \
      --python_out=/app/generated --grpc_python_out=/app/generated \
      common.proto alignment.proto

ENV PYTHONPATH=/app:/app/generated \
    GRPC_PORT=50052 \
    DATA_DIR=/data \
    ALIGN_LANGUAGE=en \
    ALIGN_DEVICE=cuda \
    ALIGN_CONFIDENCE_THRESHOLD=0.30 \
    MAX_CONCURRENCY=1 \
    HSA_OVERRIDE_GFX_VERSION=11.0.0

HEALTHCHECK --interval=10s --timeout=5s --retries=18 --start-period=180s \
  CMD python -m worker.healthcheck || exit 1

CMD ["python", "-m", "worker.server"]
