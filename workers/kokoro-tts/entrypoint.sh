#!/usr/bin/env bash
# Fetch the Kokoro ONNX model + voices into the model cache on first run, then
# start the gRPC server. Idempotent: skips files that already exist.
set -euo pipefail

MODEL_DIR="${MODEL_DIR:-/models/kokoro}"
BASE="https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0"

mkdir -p "$MODEL_DIR"
[ -f "$MODEL_DIR/kokoro-v1.0.onnx" ] || curl -fsSL -o "$MODEL_DIR/kokoro-v1.0.onnx" "$BASE/kokoro-v1.0.onnx"
[ -f "$MODEL_DIR/voices-v1.0.bin" ]  || curl -fsSL -o "$MODEL_DIR/voices-v1.0.bin"  "$BASE/voices-v1.0.bin"

exec python3 -m worker.server
