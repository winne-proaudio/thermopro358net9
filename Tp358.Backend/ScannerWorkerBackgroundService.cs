using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading;
using Tp358.Ble.Abstractions;
using Tp358.Core;

namespace Tp358.Backend;

public sealed record BleActivityStatus(
    bool Warning,
    string Message,
    DateTimeOffset? LastReceivedUtc,
    DateTimeOffset? LastProcessedUtc);

public sealed class ScannerWorker(
    IAdvertisementSource source,
    IHubContext<LiveHub> hub,
    DatabaseService databaseService,
    IntervalSettingsStore intervalSettings,
    ILogger<ScannerWorker> logger
) : BackgroundService
{
    private readonly HashSet<string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastSignalRSentPerDevice = new();
    private readonly Dictionary<string, DateTimeOffset> _lastDbSavedPerDevice = new();
    private readonly Dictionary<string, Tp358ReadingDto> _latestReadings = new();
    private readonly IntervalSettingsStore _intervalSettings = intervalSettings;
    private long _lastAdvertisementReceivedTicks;
    private long _lastAdvertisementProcessedTicks;
    private bool _warningActive;
    private string _warningMessage = string.Empty;

    public IReadOnlyDictionary<string, Tp358ReadingDto> GetLatestReadings() => _latestReadings;
    public BleActivityStatus GetBleActivityStatus() => BuildBleActivityStatus(DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warningTask = RunBleWarningLoopAsync(stoppingToken);

        await foreach (var frame in source.WatchAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _lastAdvertisementReceivedTicks, now.UtcTicks);

            // Accept both TP358 (4 bytes) and TP358S (5 bytes)
            if (frame.ManufacturerPayload.Length != 4 && frame.ManufacturerPayload.Length != 5)
            {
                continue;
            }

            Tp358Reading reading;
            try
            {
                reading = Tp358AdvertisingParser.Parse(frame.ManufacturerPayload, frame.CompanyId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Timestamp:yyyy-MM-dd HH:mm:ss} Fehler beim Parsen des Payloads", DateTimeOffset.Now);
                continue;
            }

            Interlocked.Exchange(ref _lastAdvertisementProcessedTicks, now.UtcTicks);

            var dto = new Tp358ReadingDto(
                Timestamp: frame.Timestamp,
                DeviceMac: frame.DeviceMac,
                Rssi: frame.Rssi,
                TemperatureC: reading.TemperatureC,
                HumidityPercent: reading.HumidityPercent,
                BatteryPercent: reading.BatteryPercent,
                RawPayloadHex: BitConverter.ToString(frame.ManufacturerPayload)
            );

            var deviceType = frame.ManufacturerPayload.Length == 5 ? "TP358S" : "TP358";

            if (_knownDevices.Add(frame.DeviceMac))
            {
                Console.WriteLine($"Sensor erkannt: {frame.DeviceMac} ({deviceType})");
            }

            var signalRInterval = _intervalSettings.SignalRInterval;
            var dbInterval = _intervalSettings.DbInterval;
            var shouldSendSignalR = true;
            var shouldSaveDb = true;
            if (_lastSignalRSentPerDevice.TryGetValue(frame.DeviceMac, out var lastSignalR))
            {
                var elapsed = now - lastSignalR;
                shouldSendSignalR = elapsed >= signalRInterval;
            }
            if (_lastDbSavedPerDevice.TryGetValue(frame.DeviceMac, out var lastDb))
            {
                var elapsed = now - lastDb;
                shouldSaveDb = elapsed >= dbInterval;
            }
            if (shouldSendSignalR)
            {
                await hub.Clients.All.SendAsync("reading", dto, cancellationToken: stoppingToken);
                _lastSignalRSentPerDevice[frame.DeviceMac] = now;
            }

            if (shouldSaveDb)
            {
                await databaseService.InsertMeasurementAsync(
                    dto.DeviceMac,
                    dto.TemperatureC,
                    dto.HumidityPercent,
                    dto.Timestamp,
                    stoppingToken);
                _lastDbSavedPerDevice[frame.DeviceMac] = now;
            }

            // Update latest reading for this device
            _latestReadings[frame.DeviceMac] = dto;
        }

        await warningTask;
    }

    private async Task RunBleWarningLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var status = BuildBleActivityStatus(DateTimeOffset.UtcNow);
            if (status.Warning != _warningActive || !string.Equals(status.Message, _warningMessage, StringComparison.Ordinal))
            {
                _warningActive = status.Warning;
                _warningMessage = status.Message;
                if (status.Warning)
                {
                    logger.LogWarning("####### WARNING ############## {Timestamp:yyyy-MM-dd HH:mm:ss} {Message} ####### WARNING ##############",
                        DateTimeOffset.Now, status.Message);
                }

                await hub.Clients.All.SendAsync("bleStatus", status, cancellationToken: stoppingToken);
            }
        }
    }

    private BleActivityStatus BuildBleActivityStatus(DateTimeOffset nowUtc)
    {
        var lastReceivedTicks = Interlocked.Read(ref _lastAdvertisementReceivedTicks);
        var lastProcessedTicks = Interlocked.Read(ref _lastAdvertisementProcessedTicks);
        var lastReceived = lastReceivedTicks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(lastReceivedTicks, TimeSpan.Zero);
        var lastProcessed = lastProcessedTicks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(lastProcessedTicks, TimeSpan.Zero);

        var threshold = _intervalSettings.BleWarningThreshold;
        var receivedOk = lastReceived.HasValue && (nowUtc - lastReceived.Value) <= threshold;
        var processedOk = lastProcessed.HasValue && (nowUtc - lastProcessed.Value) <= threshold;

        if (receivedOk && processedOk)
        {
            return new BleActivityStatus(false, string.Empty, lastReceived, lastProcessed);
        }

        var issues = new List<string>();
        if (!receivedOk)
        {
            var last = lastReceived?.ToLocalTime().ToString("HH:mm:ss") ?? "nie";
            var minutes = Math.Max(1, Math.Round(threshold.TotalMinutes));
            issues.Add($"kein BLE Signal seit {minutes:0} min (letztes: {last})");
        }
        if (!processedOk)
        {
            var last = lastProcessed?.ToLocalTime().ToString("HH:mm:ss") ?? "nie";
            var minutes = Math.Max(1, Math.Round(threshold.TotalMinutes));
            issues.Add($"kein BLE Signal verarbeitet seit {minutes:0} min (letztes: {last})");
        }

        var message = $"BLE WARNUNG: {string.Join(" | ", issues)}";
        return new BleActivityStatus(true, message, lastReceived, lastProcessed);
    }
}
