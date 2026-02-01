using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        app.MapHub<LiveHub>("/live");

        app.Run();
    }
}
