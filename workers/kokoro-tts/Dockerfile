# Kokoro TTS worker (PyTorch on the GPU via torch-rocm). Build context = repo root:
#   docker build -f workers/kokoro-tts/Dockerfile -t kokoro-tts .
# Same ROCm/torch base as the aligner; torch sees the AMD GPU as "cuda".
FROM rocm/pytorch:latest AS base

ENV DEBIAN_FRONTEND=noninteractive
# espeak-ng backs misaki's phonemizer fallback.
RUN apt-get update \
 && apt-get install -y --no-install-recommends espeak-ng \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY workers/kokoro-tts/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Contracts + source, then generate the gRPC stubs into /app/generated.
COPY proto/ /proto/
COPY workers/kokoro-tts/ /app/
RUN mkdir -p /app/generated \
 && python -m grpc_tools.protoc -I /proto \
      --python_out=/app/generated --grpc_python_out=/app/generated \
      common.proto kokoro.proto

ENV PYTHONPATH=/app:/app/generated \
    GRPC_PORT=50051 \
    DATA_DIR=/data \
    HSA_OVERRIDE_GFX_VERSION=11.0.0 \
    HF_HOME=/models/hf \
    MIOPEN_USER_DB_PATH=/models/miopen \
    MIOPEN_CUSTOM_CACHE_DIR=/models/miopen \
    # Heuristic kernel selection: chunks vary in length, and MIOpen's default
    # NORMAL mode runs an expensive per-shape tuning search the first time it sees
    # each length (tens of seconds), so synthesis was slower than CPU. FAST picks
    # a good kernel immediately. The MIOpen cache above still persists compiled ones.
    MIOPEN_FIND_MODE=FAST

RUN chmod +x /app/entrypoint.sh

# Generous start period: first boot downloads the model and JIT-compiles GPU
# (MIOpen) kernels during warmup; the MIOpen cache volume makes later boots fast.
HEALTHCHECK --interval=10s --timeout=5s --retries=24 --start-period=300s \
  CMD python3 -m worker.healthcheck || exit 1

CMD ["/app/entrypoint.sh"]
