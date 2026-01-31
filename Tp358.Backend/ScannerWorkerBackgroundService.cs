using Microsoft.AspNetCore.SignalR;
using Tp358.Ble.Abstractions;
using Tp358.Core;

namespace Tp358.Backend;

public sealed class ScannerWorker(
    IAdvertisementSource source,
    IHubContext<LiveHub> hub
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var frame in source.WatchAsync(stoppingToken))
        {
            // TP358S Fokus: payload length 5
            if (frame.ManufacturerPayload.Length != 5)
                continue;

            Tp358Reading reading;
            try
            {
                reading = Tp358AdvertisingParser.Parse(frame.ManufacturerPayload);
            }
            catch
            {
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

            await hub.Clients.All.SendAsync("reading", dto, cancellationToken: stoppingToken);
        }
    }
}
