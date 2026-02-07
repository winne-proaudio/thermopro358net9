using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Tp358.Ble.Abstractions;

namespace Tp358.Backend;

internal static class BackendHost
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.UseUrls("http://0.0.0.0:5055");
        builder.Services.AddSignalR();

        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<IntervalSettingsStore>();

        builder.Services.AddSingleton<IAdvertisementSource>(sp =>
        {
#if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ActivatorUtilities.CreateInstance<Tp358.Ble.Windows.WindowsAdvertisementSource>(sp);
            }
#endif

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (!Directory.Exists("/sys/class/bluetooth"))
                {
                    return ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
                }

                var config = sp.GetRequiredService<IConfiguration>();
                var (preferredId, preferredAddress) = ResolveBleAdapterConfig(config);

                var primary = new Tp358.Ble.BlueZ.BlueZAdvertisementSource(preferredId, preferredAddress);
                var fallback = ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
                return ActivatorUtilities.CreateInstance<FallbackAdvertisementSource>(sp, primary, fallback);
            }

            return ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
        });

        builder.Services.AddSingleton<ScannerWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerWorker>());

        var app = builder.Build();

        app.UseStaticFiles();

        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(BackendHost).FullName!);

        // Initialize database (optional)
        var dbService = app.Services.GetRequiredService<DatabaseService>();
        try
        {
            await dbService.InitializeDatabaseAsync();
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILogger<DatabaseService>>();
            logger.LogWarning(ex, "Datenbank konnte nicht initialisiert werden. Backend läuft ohne Datenbankanbindung weiter.");
        }

        await dbService.LogDbServerInfoAsync();

        startupLogger.LogInformation("Backend gestartet. Urls: {Urls}.", string.Join(", ", app.Urls));

        app.MapGet("/", () =>
            Results.Text("TP358 Backend läuft. Endpoints: /health, /live/data, /BackendMonitor, SignalR: /live", "text/plain; charset=utf-8"));

        app.MapGet("/BackendMonitor", () => Results.Redirect("/monitor.html"));

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/status/ble", (IAdvertisementSource source) =>
        {
            var resolvedSource = source is FallbackAdvertisementSource fallbackSource ? fallbackSource.Primary : source;
            var adapterInfo = resolvedSource as IBleAdapterInfo;
            var adapterLabel = BuildAdapterLabel(adapterInfo);

            return Results.Ok(new { adapter = adapterLabel });
        });
        app.MapGet("/status/ble/activity", (ScannerWorker worker) =>
        {
            return Results.Ok(worker.GetBleActivityStatus());
        });
        app.MapGet("/status/db", (DatabaseService databaseService) =>
        {
            return Results.Ok(new
            {
                esp32Host = databaseService.Esp32DbHost,
                tp358Host = databaseService.Tp358DbHost
            });
        });
        app.MapGet("/config/devices", (IConfiguration config) =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in config.GetSection("DeviceNames").GetChildren())
            {
                var mac = child["Mac"] ?? child["mac"];
                var name = child["Name"] ?? child["name"];
                if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                map[mac.Trim()] = name.Trim();
            }
            return Results.Ok(map);
        });
        app.MapGet("/config/intervals", (IntervalSettingsStore store) =>
        {
            return Results.Ok(store.Get());
        });
        app.MapPost("/config/intervals", async (HttpRequest request, IntervalSettingsStore store) =>
        {
            var update = await request.ReadFromJsonAsync<IntervalUpdateRequest>();
            if (update is null)
            {
                return Results.BadRequest(new { error = "Invalid payload" });
            }

            var snapshot = store.Update(update);
            return Results.Ok(snapshot);
        });

        app.MapGet("/live/data", (ScannerWorker worker) =>
        {
            var readings = worker.GetLatestReadings();
            if (readings.Count == 0)
            {
                return Results.Text("Noch keine Sensordaten empfangen.\n", "text/plain; charset=utf-8");
            }

            var sb = new System.Text.StringBuilder();
            foreach (var (mac, data) in readings.OrderBy(x => x.Key))
            {
                sb.AppendLine($"Sensor: {mac}");
                sb.AppendLine($"  Temperatur: {data.TemperatureC?.ToString("F1") ?? "n/a"} °C");
                sb.AppendLine($"  Luftfeuchtigkeit: {data.HumidityPercent?.ToString() ?? "n/a"} %");
                sb.AppendLine($"  Batterie: {data.BatteryPercent?.ToString() ?? "n/a"} %");
                sb.AppendLine($"  Signalstärke: {data.Rssi} dBm");
                sb.AppendLine($"  Letzte Aktualisierung: {data.Timestamp:HH:mm:ss}");
                sb.AppendLine();
            }
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        app.MapGet("/measurements/temperature", async (DatabaseService databaseService, int? hours, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var effectiveHours = Math.Clamp(hours ?? 24, 1, 168);
            var to = DateTimeOffset.Now;
            var from = to.AddHours(-effectiveHours);

            var measurements = await databaseService.GetTemperatureMeasurementsAsync(from, to, cancellationToken);
            return Results.Ok(measurements);
        });
        app.MapGet("/measurements/temperature/stats", async (DatabaseService databaseService, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var stats = await databaseService.GetTemperatureStatsAsync(cancellationToken);
            return Results.Ok(stats);
        });

        app.MapGet("/measurements/external", async (DatabaseService databaseService, int? hours, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var effectiveHours = Math.Clamp(hours ?? 24, 1, 168);
            var to = DateTimeOffset.Now;
            var from = to.AddHours(-effectiveHours);

            var deviceIds = new[] { "Vorlauf", "Rücklauf", "EtagenRL" };
            var measurements = await databaseService.GetExternalTemperatureMeasurementsAsync(from, to, deviceIds, cancellationToken);
            return Results.Ok(measurements);
        });
        app.MapGet("/measurements/external/old", async (DatabaseService databaseService, int? hours, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var effectiveHours = Math.Clamp(hours ?? 24, 1, 168);
            var to = DateTimeOffset.Now;
            var from = to.AddHours(-effectiveHours);

            var deviceIds = new[] { "Steigleitung", "Rücklauf" };
            var measurements = await databaseService.GetOldExternalTemperatureMeasurementsAsync(from, to, deviceIds, cancellationToken);
            return Results.Ok(measurements);
        });

        app.MapGet("/measurements/external/stats", async (DatabaseService databaseService, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var stats = await databaseService.GetExternalTemperatureStatsAsync(cancellationToken);
            return Results.Ok(stats);
        });

        app.MapPost("/shutdown", (IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Shutdown");
            logger.LogWarning("Shutdown requested via /shutdown.");

            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                lifetime.StopApplication();
            });

            return Results.Ok(new { ok = true });
        });

        app.MapHub<LiveHub>("/live");

        app.Run();
    }

    private static string? BuildAdapterLabel(IBleAdapterInfo? adapterInfo)
    {
        if (adapterInfo is null)
        {
            return null;
        }

        var name = adapterInfo.AdapterName;
        var id = adapterInfo.AdapterId;
        string? label = null;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
        {
            label = $"{name} ({id})";
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            label = name;
        }
        else if (!string.IsNullOrWhiteSpace(id))
        {
            label = id;
        }

        return label;
    }

    private static (string? AdapterId, string? AdapterAddress) ResolveBleAdapterConfig(IConfiguration config)
    {
        var host = Environment.MachineName;
        var hostSection = config.GetSection("Ble:Hosts");
        if (hostSection.Exists())
        {
            foreach (var child in hostSection.GetChildren())
            {
                if (!string.Equals(child.Key, host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = child["AdapterId"];
                var address = child["AdapterAddress"];
                return (
                    string.IsNullOrWhiteSpace(id) ? null : id,
                    string.IsNullOrWhiteSpace(address) ? null : address
                );
            }
        }

        var fallbackId = config["Ble:AdapterId"];
        var fallbackAddress = config["Ble:AdapterAddress"];
        return (
            string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId,
            string.IsNullOrWhiteSpace(fallbackAddress) ? null : fallbackAddress
        );
    }
}
