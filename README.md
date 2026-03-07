# ThermoPro TP358 / TP358S (.NET 9) – Advertising + GATT Parser + Windows Console

This solution contains:

- **Tp358.Core** (net9.0): Parsers for Advertising manufacturer payload and standard GATT characteristics.
- **Tp358.WinConsole** (net9.0-windows10.0.19041.0): Windows console scanner using WinRT BLE APIs:
  - listens to BLE advertisements
  - parses TP358 / TP358S manufacturer payloads (when recognized)
  - optionally connects periodically to read GATT temperature/humidity/battery

## Requirements

- Windows 10/11 with Bluetooth LE
- .NET SDK 9 installed
- Bluetooth enabled; on some systems you may need to enable related privacy permissions.

## Build & Run

From the solution folder:

```bash
dotnet build
dotnet run --project src/Tp358.WinConsole
```

With periodic GATT reads (connect every 30 seconds per device):

```bash
dotnet run --project src/Tp358.WinConsole -- --gatt --interval=30
```

## Notes / Reverse Engineering

Observed manufacturer payloads (bytes after the 16-bit company ID):

- **TP358**: 4 bytes -> humidity in payload[2], temperature typically not present in ADV.
- **TP358S**: 5 bytes -> temperature in payload[2..3] as uint16 LE with scale **/256 °C**.

GATT uses standard BLE UUIDs:
- Environmental Sensing Service: 0x181A
  - Temperature: 0x2A6E (sint16, 0.01 °C)
  - Humidity: 0x2A6F (uint16, 0.01 %RH)
- Battery Service: 0x180F
  - Battery Level: 0x2A19 (uint8 %)

If your devices expose vendor-specific services/characteristics, you can extend `TryReadGattAsync`.

## TP358 Backend API

`Tp358.Backend` exposes an endpoint for wall panels:

- `GET /api/rooms/latest`

Example response:

```json
{
  "generatedAtUtc": "2026-03-07T12:00:00Z",
  "rooms": [
    {
      "roomId": "katzenzimmer",
      "label": "Katzenzimmer",
      "currentTemp": 22.3,
      "timestampUtc": "2026-03-07T11:50:00Z",
      "stale": false
    }
  ]
}
```

Behavior:

- Returns latest available value per configured room (`DeviceNames` in `appsettings.json`).
- Rooms without measurements are still included (`currentTemp = null`, `timestampUtc = null`, `stale = true`).
- `stale = true` if the latest value is older than 20 minutes.
- Returns `500` only for real DB errors.
