#!/usr/bin/env bash
# Start the gRPC server. The PyTorch `kokoro` package downloads its weights from
# Hugging Face on first use (cached under HF_HOME on the model volume), so there
# is no separate model-fetch step.
set -euo pipefail
exec python3 -m worker.server
