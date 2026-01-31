using Tp358.Ble.Abstractions;
using Tp358.Backend;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5055");

builder.Services.AddSignalR();

builder.Services.AddSingleton<IAdvertisementSource, FakeAdvertisementSource>();
builder.Services.AddHostedService<ScannerWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapHub<LiveHub>("/live");

app.Run();
