# Bluetooth Recovery via systemd

Diese Vorlage koppelt den neuen Backend-Endpunkt `POST /ops/bluetooth/restart` an ein Script, das den Bluetooth-Dienst mit Schutzlogik neu startet.

## 1) Recovery-Script

Datei: `/usr/local/bin/bt-recover.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail

LOCK_FILE="/run/bt-recover.lock"
STATE_FILE="/var/lib/tp358/bt-recover.state"
LOG_TAG="bt-recover"
COOLDOWN_SECONDS=600
MAX_RESTARTS_PER_HOUR=3

mkdir -p /var/lib/tp358

exec 9>"${LOCK_FILE}"
flock -n 9 || exit 0

now=$(date +%s)
last_restart=0
history=""

if [[ -f "${STATE_FILE}" ]]; then
  # shellcheck disable=SC1090
  source "${STATE_FILE}" || true
fi

if (( now - last_restart < COOLDOWN_SECONDS )); then
  logger -t "${LOG_TAG}" "Cooldown aktiv, kein Restart"
  exit 0
fi

recent_count=0
new_history=""
for ts in ${history:-}; do
  if (( now - ts < 3600 )); then
    recent_count=$((recent_count + 1))
    new_history="${new_history} ${ts}"
  fi
done

if (( recent_count >= MAX_RESTARTS_PER_HOUR )); then
  logger -t "${LOG_TAG}" "Restart-Limit erreicht (${MAX_RESTARTS_PER_HOUR}/h)"
  exit 0
fi

logger -t "${LOG_TAG}" "Starte bluetooth.service neu"
systemctl restart bluetooth
sleep 5
systemctl is-active --quiet bluetooth

last_restart="${now}"
history="${new_history} ${now}"
cat > "${STATE_FILE}" <<EOF
last_restart=${last_restart}
history="${history}"
EOF

logger -t "${LOG_TAG}" "Bluetooth-Recovery erfolgreich"
```

Dann:

```bash
sudo chmod +x /usr/local/bin/bt-recover.sh
```

## 2) One-shot Service

Datei: `/etc/systemd/system/bt-recover.service`

```ini
[Unit]
Description=TP358 Bluetooth Recovery
After=bluetooth.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/bt-recover.sh
```

## 3) Optional: Trigger per Datei

Datei: `/etc/systemd/system/bt-recover.path`

```ini
[Unit]
Description=Watch trigger file for BT recovery

[Path]
PathExists=/run/tp358/bt-recover.trigger
PathChanged=/run/tp358/bt-recover.trigger
Unit=bt-recover.service

[Install]
WantedBy=multi-user.target
```

Aktivieren:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now bt-recover.path
```

## 4) Backend-Konfiguration

In `Tp358.Backend/appsettings.json`:

```json
"Operations": {
  "BluetoothRestartCommand": "sudo /usr/bin/systemctl start bt-recover.service",
  "BluetoothRestartTimeoutSeconds": 30
}
```

Wenn dein Backend als `root`-Service läuft, geht auch:

```json
"BluetoothRestartCommand": "/usr/bin/systemctl start bt-recover.service"
```

## 5) Frontend-Button

Der Settings-Button ruft jetzt `POST /ops/bluetooth/restart` auf und zeigt den Trigger-Status im UI.
