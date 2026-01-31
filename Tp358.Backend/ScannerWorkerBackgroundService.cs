using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Tp358.Ble.Abstractions;
using Tp358.Core;

namespace Tp358.Backend;

public sealed class ScannerWorker(
    IAdvertisementSource source,
    IHubContext<LiveHub> hub,
    ILogger<ScannerWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ScannerWorker gestartet. Warte auf BLE Advertisements...");
        
        await foreach (var frame in source.WatchAsync(stoppingToken))
        {
            var payloadHex = BitConverter.ToString(frame.ManufacturerPayload);
            logger.LogInformation("Advertisement empfangen: MAC={Mac}, CompanyId=0x{CompanyId:X4}, Payload Length={Length}, RSSI={Rssi}, Raw={Raw}", 
                frame.DeviceMac, frame.CompanyId, frame.ManufacturerPayload.Length, frame.Rssi, payloadHex);

            // Accept both TP358 (4 bytes) and TP358S (5 bytes)
            if (frame.ManufacturerPayload.Length != 4 && frame.ManufacturerPayload.Length != 5)
            {
                logger.LogDebug("Payload-Länge {Length} übersprungen (erwartet: 4 oder 5)", frame.ManufacturerPayload.Length);
                continue;
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

            string deviceType = frame.ManufacturerPayload.Length == 5 ? "TP358S" : "TP358";
            logger.LogInformation("[{Type}] Daten gesendet via SignalR: {Mac} | Temp={Temp}°C, Humidity={Hum}%, Battery={Bat}%",
                deviceType, dto.DeviceMac, dto.TemperatureC, dto.HumidityPercent, dto.BatteryPercent);

            await hub.Clients.All.SendAsync("reading", dto, cancellationToken: stoppingToken);
        }
    }
}
