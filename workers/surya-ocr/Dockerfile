# Surya OCR worker. Build context = repo root (needs /proto):
#   docker build -f workers/surya-ocr/Dockerfile -t surya-ocr .
# Base image ships torch built for ROCm; torch sees the AMD GPU as "cuda" (same as
# the whisperx worker), so Surya runs on the GPU.
FROM rocm/pytorch:latest AS base

WORKDIR /app

COPY workers/surya-ocr/requirements.txt .
# Pin the preinstalled ROCm torch via a constraint so `pip install surya-ocr` keeps
# it instead of pulling a CUDA wheel and breaking GPU access. A conflicting hard pin
# fails the build loudly rather than producing a silently CPU-only image.
RUN echo "torch==$(python -c 'import torch; print(torch.__version__)')" > /tmp/constraints.txt \
 && echo "numpy<2" >> /tmp/constraints.txt \
 && PIP_CONSTRAINT=/tmp/constraints.txt pip install --no-cache-dir -r requirements.txt \
 && python -c "import torch, numpy; assert torch.version.hip, 'ROCm torch was clobbered'; assert numpy.__version__[0]=='1', 'numpy 2 leaked in'; print('torch', torch.__version__, 'numpy', numpy.__version__, '- ok'); torch.ones(2).numpy()"

COPY proto/ /proto/
COPY workers/surya-ocr/ /app/
COPY workers/shared/ /app/shared/
RUN mkdir -p /app/generated \
 && python -m grpc_tools.protoc -I /proto \
      --python_out=/app/generated --grpc_python_out=/app/generated \
      common.proto ocr.proto

ENV PYTHONPATH=/app:/app/generated \
    GRPC_PORT=50053 \
    DATA_DIR=/data \
    OCR_DEVICE=cuda \
    OCR_RENDER_DPI=300 \
    OCR_PREPROCESS=1 \
    MAX_CONCURRENCY=1 \
    HSA_OVERRIDE_GFX_VERSION=11.0.0 \
    HF_HOME=/models/hf

# Surya pulls models from HuggingFace on first run; that download (and GPU kernel
# compile) is why the start period is generous.
HEALTHCHECK --interval=10s --timeout=5s --retries=24 --start-period=300s \
  CMD python -m worker.healthcheck || exit 1

CMD ["python", "-m", "worker.server"]
