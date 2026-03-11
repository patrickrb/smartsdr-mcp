#!/usr/bin/env bash
# Download the ONNX CW decoder model from web-deep-cw-decoder (GPL-3.0)
# Usage: bash tools/download-cw-model.sh

set -euo pipefail

MODELS_DIR="$(cd "$(dirname "$0")/.." && pwd)/models"
MODEL_FILE="$MODELS_DIR/model_en.onnx"
MODEL_URL="https://raw.githubusercontent.com/e04/web-deep-cw-decoder/main/src/model_en.onnx"

mkdir -p "$MODELS_DIR"

if [ -f "$MODEL_FILE" ] && [ "$(wc -c < "$MODEL_FILE")" -gt 1000 ]; then
    echo "Model already exists at $MODEL_FILE"
    exit 0
fi

echo "Downloading CW neural decoder model..."
echo "Source: $MODEL_URL"
echo "License: GPL-3.0 (https://github.com/e04/web-deep-cw-decoder)"
echo ""

curl -L -o "$MODEL_FILE" "$MODEL_URL"

if [ -f "$MODEL_FILE" ]; then
    SIZE=$(wc -c < "$MODEL_FILE")
    echo "Downloaded successfully: $MODEL_FILE ($SIZE bytes)"
else
    echo "Download failed!"
    exit 1
fi
