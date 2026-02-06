#!/usr/bin/env bash
set -euo pipefail

LOG_PATH=${LOG_PATH:-"$HOME/tp358-backend.log"}
PID_PATH=${PID_PATH:-"$HOME/tp358-backend.pid"}

cd /home/winne/GitHub/ThermoPro/thermopro358net9/Tp358.Backend

printf "Starting TP358 backend. Logging to %s\n" "$LOG_PATH"
printf "PID file: %s\n" "$PID_PATH"

rm -f "$PID_PATH"

dotnet run 2>&1 | tee -a "$LOG_PATH" &
echo $! > "$PID_PATH"
wait $!
