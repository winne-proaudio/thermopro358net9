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

                var primary = ActivatorUtilities.CreateInstance<Tp358.Ble.BlueZ.BlueZAdvertisementSource>(sp);
                var fallback = ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
                return ActivatorUtilities.CreateInstance<FallbackAdvertisementSource>(sp, primary, fallback);
            }

            return ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
        });

        builder.Services.AddSingleton<ScannerWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerWorker>());

        var app = builder.Build();

        app.UseStaticFiles();

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

        app.MapGet("/", () =>
            Results.Text("TP358 Backend läuft. Endpoints: /health, /live/data, /BackendMonitor, SignalR: /live", "text/plain; charset=utf-8"));

        app.MapGet("/BackendMonitor", () => Results.Redirect("/monitor.html"));

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
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

        app.MapGet("/measurements/external", async (DatabaseService databaseService, int? hours, CancellationToken cancellationToken) =>
        {
            if (!databaseService.IsAvailable)
            {
                return Results.StatusCode(503);
            }

            var effectiveHours = Math.Clamp(hours ?? 24, 1, 168);
            var to = DateTimeOffset.Now;
            var from = to.AddHours(-effectiveHours);

            var deviceIds = new[] { "Steigleitung", "Rücklauf" };
            var measurements = await databaseService.GetExternalTemperatureMeasurementsAsync(from, to, deviceIds, cancellationToken);
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
}
