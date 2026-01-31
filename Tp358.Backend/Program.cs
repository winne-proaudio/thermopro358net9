using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using Tp358.Ble.Abstractions;

namespace Tp358.Backend;

internal static class BackendHost
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.UseUrls("http://0.0.0.0:5055");
        builder.Services.AddSignalR();

        builder.Services.AddSingleton<IAdvertisementSource>(sp =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ActivatorUtilities.CreateInstance<Tp358.Ble.Windows.WindowsAdvertisementSource>(sp);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
            }

            return ActivatorUtilities.CreateInstance<FakeAdvertisementSource>(sp);
        });

        builder.Services.AddHostedService<ScannerWorker>();

            var app = builder.Build();

            app.MapGet("/", () =>
                Results.Text("TP358 Backend läuft. Endpoints: /health, SignalR: /live", "text/plain; charset=utf-8"));

            app.MapGet("/health", () => Results.Ok(new { ok = true }));
            app.MapHub<LiveHub>("/live");

            app.Run();
    }
}