using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Tp358.Ble.Abstractions;
using Tp358.Core;

namespace Tp358.Backend;

public sealed class ScannerWorker(
    IAdvertisementSource source,
    IHubContext<LiveHub> hub,
    DatabaseService databaseService,
    ILogger<ScannerWorker> logger
) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _lastSentPerDevice = new();
    private readonly Dictionary<string, Tp358ReadingDto> _latestReadings = new();
    private readonly TimeSpan _sendInterval = TimeSpan.FromSeconds(30);

    public IReadOnlyDictionary<string, Tp358ReadingDto> GetLatestReadings() => _latestReadings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sourceType = source.GetType().FullName ?? source.GetType().Name;
        var sourceLabel = sourceType.Contains("FakeAdvertisementSource", StringComparison.OrdinalIgnoreCase)
            ? "FAKE (simuliert)"
            : sourceType.Contains("BlueZ", StringComparison.OrdinalIgnoreCase)
                ? "BlueZ (echt)"
                : sourceType.Contains("WindowsAdvertisementSource", StringComparison.OrdinalIgnoreCase)
                    ? "Windows BLE (echt)"
                    : sourceType.Contains("FallbackAdvertisementSource", StringComparison.OrdinalIgnoreCase)
                        ? "Fallback (echt oder Fake bei Fehlern)"
                        : sourceType;

        if (sourceLabel.StartsWith("FAKE", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("BLE-Quelle: {Source}", sourceLabel);
        }
        else
        {
            logger.LogInformation("BLE-Quelle: {Source}", sourceLabel);
        }

        logger.LogInformation("ScannerWorker gestartet. Warte auf BLE Advertisements...");
        logger.LogInformation("SignalR- und Datenbank-Interval: {Interval} Sekunden pro Gerät", _sendInterval.TotalSeconds);
        
        await foreach (var frame in source.WatchAsync(stoppingToken))
        {
            var payloadHex = BitConverter.ToString(frame.ManufacturerPayload);
            logger.LogDebug("Advertisement empfangen: MAC={Mac}, CompanyId=0x{CompanyId:X4}, Payload Length={Length}, RSSI={Rssi}, Raw={Raw}", 
                frame.DeviceMac, frame.CompanyId, frame.ManufacturerPayload.Length, frame.Rssi, payloadHex);

            // Accept both TP358 (4 bytes) and TP358S (5 bytes)
            if (frame.ManufacturerPayload.Length != 4 && frame.ManufacturerPayload.Length != 5)
            {
                logger.LogDebug("Payload-Länge {Length} übersprungen (erwartet: 4 oder 5)", frame.ManufacturerPayload.Length);
                continue;
            }

            // Check if enough time has passed since last send for this device
            var now = DateTimeOffset.UtcNow;
            if (_lastSentPerDevice.TryGetValue(frame.DeviceMac, out var lastSent))
            {
                var elapsed = now - lastSent;
                if (elapsed < _sendInterval)
                {
                    logger.LogTrace("Gerät {Mac}: Übersprungen, letztes Senden vor {Elapsed:F1}s (Interval: {Interval}s)", 
                        frame.DeviceMac, elapsed.TotalSeconds, _sendInterval.TotalSeconds);
                    continue;
                }
            }

            Tp358Reading reading;
            try
            {
                reading = Tp358AdvertisingParser.Parse(frame.ManufacturerPayload, frame.CompanyId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fehler beim Parsen des Payloads");
                continue;
            }

            var dto = new Tp358ReadingDto(
                Timestamp: frame.Timestamp,
                DeviceMac: frame.DeviceMac,
                Rssi: frame.Rssi,
                TemperatureC: reading.TemperatureC,
                HumidityPercent: reading.HumidityPercent,
                BatteryPercent: reading.BatteryPercent,
                RawPayloadHex: BitConverter.ToString(frame.ManufacturerPayload)
            );

            if (frame.ManufacturerPayload.Length == 4)
            {
                logger.LogInformation("TP358 4B RAW: MAC={Mac} CompanyId=0x{CompanyId:X4} Payload={Payload}",
                    frame.DeviceMac, frame.CompanyId, dto.RawPayloadHex);
            }

            string deviceType = frame.ManufacturerPayload.Length == 5 ? "TP358S" : "TP358";
            logger.LogInformation("[{Type}] Daten gesendet via SignalR: {Mac} | Temp={Temp}°C, Humidity={Hum}%, Battery={Bat}%",
                deviceType, dto.DeviceMac, dto.TemperatureC, dto.HumidityPercent, dto.BatteryPercent);

            await hub.Clients.All.SendAsync("reading", dto, cancellationToken: stoppingToken);

            // Save to database
            await databaseService.InsertMeasurementAsync(
                dto.DeviceMac,
                dto.TemperatureC,
                dto.HumidityPercent,
                dto.Timestamp,
                stoppingToken);

            // Update last sent timestamp and latest reading for this device
            _lastSentPerDevice[frame.DeviceMac] = now;
            _latestReadings[frame.DeviceMac] = dto;
        }
    }
}
