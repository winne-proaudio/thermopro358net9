#!/usr/bin/env bash
set -euo pipefail

PID_PATH=${PID_PATH:-"$HOME/tp358-backend.pid"}

if [[ ! -f "$PID_PATH" ]]; then
  echo "PID file not found: $PID_PATH"
  exit 1
fi

PID=$(cat "$PID_PATH")
if [[ -z "$PID" ]]; then
  echo "PID file is empty: $PID_PATH"
  exit 1
fi

if kill -0 "$PID" 2>/dev/null; then
  echo "Stopping TP358 backend (PID $PID)..."
  kill "$PID"
  exit 0
fi

echo "Process not running (PID $PID)."
exit 1
