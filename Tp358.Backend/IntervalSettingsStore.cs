using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace Tp358.Backend;

public sealed record IntervalSettingsSnapshot(
    int SignalRSeconds,
    int DbSeconds,
    int BleWarningSeconds,
    int MinSeconds,
    int MaxSeconds,
    int StepSeconds);

public sealed record IntervalUpdateRequest(int? SignalRSeconds, int? BleWarningSeconds);

public sealed class IntervalSettingsStore
{
    private const int MinSeconds = 30;
    private const int MaxSeconds = 15 * 60;
    private const int StepSeconds = 30;
    private const int FixedDbSeconds = 3 * 60;

    private readonly object _sync = new();
    private readonly ILogger<IntervalSettingsStore> _logger;
    private int _signalRSeconds;
    private int _bleWarningSeconds;
    private readonly string _settingsPath;

    public IntervalSettingsStore(IConfiguration configuration, IHostEnvironment hostEnvironment, ILogger<IntervalSettingsStore> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(hostEnvironment.ContentRootPath, "appsettings.json");
        _signalRSeconds = Normalize(configuration.GetValue<int?>("Intervals:SignalRSeconds") ?? 60);
        _bleWarningSeconds = Normalize(configuration.GetValue<int?>("Intervals:BleWarningSeconds") ?? 300);

        _logger.LogInformation(
            "Intervalle initialisiert: SignalR={SignalRSeconds}s, DB={DbSeconds}s",
            _signalRSeconds,
            FixedDbSeconds);
    }

    public TimeSpan SignalRInterval
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromSeconds(_signalRSeconds);
            }
        }
    }

    public TimeSpan DbInterval
    {
        get
        {
            return TimeSpan.FromSeconds(FixedDbSeconds);
        }
    }

    public TimeSpan BleWarningThreshold
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromSeconds(_bleWarningSeconds);
            }
        }
    }

    public IntervalSettingsSnapshot Get()
    {
        lock (_sync)
        {
            return new IntervalSettingsSnapshot(
                _signalRSeconds,
                FixedDbSeconds,
                _bleWarningSeconds,
                MinSeconds,
                MaxSeconds,
                StepSeconds);
        }
    }

    public IntervalSettingsSnapshot Update(IntervalUpdateRequest request)
    {
        lock (_sync)
        {
            if (request.SignalRSeconds.HasValue)
            {
                _signalRSeconds = Normalize(request.SignalRSeconds.Value);
                TryPersistSettings(_signalRSeconds, _bleWarningSeconds);
            }

            if (request.BleWarningSeconds.HasValue)
            {
                _bleWarningSeconds = Normalize(request.BleWarningSeconds.Value);
                TryPersistSettings(_signalRSeconds, _bleWarningSeconds);
            }

            _logger.LogInformation(
                "Intervalle aktualisiert: SignalR={SignalRSeconds}s, DB={DbSeconds}s",
                _signalRSeconds,
                FixedDbSeconds);

            return new IntervalSettingsSnapshot(
                _signalRSeconds,
                FixedDbSeconds,
                _bleWarningSeconds,
                MinSeconds,
                MaxSeconds,
                StepSeconds);
        }
    }

    private static int Normalize(int value)
    {
        var clamped = Math.Clamp(value, MinSeconds, MaxSeconds);
        var stepped = (int)Math.Round(clamped / (double)StepSeconds) * StepSeconds;
        return Math.Clamp(stepped, MinSeconds, MaxSeconds);
    }

    private void TryPersistSettings(int signalRSeconds, int bleWarningSeconds)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _logger.LogWarning("appsettings.json nicht gefunden: {Path}", _settingsPath);
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteUpdatedSettings(document.RootElement, signalRSeconds, bleWarningSeconds, writer);
            }
            File.WriteAllBytes(_settingsPath, stream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Konnte Intervalle nicht in appsettings.json speichern.");
        }
    }

    private static void WriteUpdatedSettings(JsonElement root, int signalRSeconds, int bleWarningSeconds, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("Intervals"))
            {
                writer.WritePropertyName(property.Name);
                WriteIntervalsObject(property.Value, signalRSeconds, bleWarningSeconds, writer);
            }
            else
            {
                property.WriteTo(writer);
            }
        }
        if (!root.TryGetProperty("Intervals", out _))
        {
            writer.WritePropertyName("Intervals");
            writer.WriteStartObject();
            writer.WriteNumber("SignalRSeconds", signalRSeconds);
            writer.WriteNumber("DbSeconds", FixedDbSeconds);
            writer.WriteNumber("BleWarningSeconds", bleWarningSeconds);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteIntervalsObject(JsonElement intervals, int signalRSeconds, int bleWarningSeconds, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        var hadSignalR = false;
        var hadDb = false;
        var hadBleWarning = false;
        foreach (var property in intervals.EnumerateObject())
        {
            if (property.NameEquals("SignalRSeconds"))
            {
                writer.WriteNumber("SignalRSeconds", signalRSeconds);
                hadSignalR = true;
            }
            else if (property.NameEquals("DbSeconds"))
            {
                writer.WriteNumber("DbSeconds", FixedDbSeconds);
                hadDb = true;
            }
            else if (property.NameEquals("BleWarningSeconds"))
            {
                writer.WriteNumber("BleWarningSeconds", bleWarningSeconds);
                hadBleWarning = true;
            }
            else
            {
                property.WriteTo(writer);
            }
        }
        if (!hadSignalR)
        {
            writer.WriteNumber("SignalRSeconds", signalRSeconds);
        }
        if (!hadDb)
        {
            writer.WriteNumber("DbSeconds", FixedDbSeconds);
        }
        if (!hadBleWarning)
        {
            writer.WriteNumber("BleWarningSeconds", bleWarningSeconds);
        }
        writer.WriteEndObject();
    }
}
