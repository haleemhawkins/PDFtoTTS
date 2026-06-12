#!/usr/bin/env bash
# Generate Python gRPC stubs for both workers from the shared /proto contracts.
# Run inside each worker image at build time (and locally for tests).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROTO="$ROOT/proto"

gen() {
  local out="$1"; shift
  mkdir -p "$out"
  python -m grpc_tools.protoc -I "$PROTO" \
    --python_out="$out" --grpc_python_out="$out" "$@"
}

gen "$ROOT/workers/kokoro-tts/generated"     common.proto kokoro.proto
gen "$ROOT/workers/whisperx-align/generated" common.proto alignment.proto

echo "proto stubs generated"
