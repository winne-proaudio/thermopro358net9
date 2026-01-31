using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using Tp358.Core;

namespace Tp358.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private HubConnection? _connection;
    private readonly Dictionary<string, ObservableCollection<Tp358ReadingDto>> _deviceReadings = new();
    private readonly Dictionary<string, string> _deviceNames = new()
    {
        { "FB:B9:30:BB:5E:55", "Plantage" },
        { "F4:B8:2A:C1:37:AF", "Katzenzimmer" },
        { "FA:C2:7D:1A:C3:EA", "Marie" }
    };

    public ObservableCollection<Tp358ReadingDto> Readings { get; } = new();
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    [ObservableProperty]
    private string status = "Disconnected";
    
    private string GetDeviceName(string mac)
    {
        return _deviceNames.TryGetValue(mac, out var name) ? name : mac;
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (_connection is not null)
            return;

        try
        {
            Status = "Connecting...";
            Console.WriteLine("[Desktop] Verbinde zu http://localhost:5055/live...");

            _connection = new HubConnectionBuilder()
                .WithUrl("http://localhost:5055/live")
                .WithAutomaticReconnect()
                .Build();

            _connection.On<Tp358ReadingDto>("reading", dto =>
            {
                Console.WriteLine($"[Desktop] Daten empfangen: {dto.DeviceMac} | Temp={dto.TemperatureC}°C, Humidity={dto.HumidityPercent}%, Battery={dto.BatteryPercent}%");
                
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Add to global readings list
                    Readings.Insert(0, dto);
                    while (Readings.Count > 200)
                        Readings.RemoveAt(Readings.Count - 1);
                    
                    // Update per-device collections
                    if (!_deviceReadings.ContainsKey(dto.DeviceMac))
                    {
                        _deviceReadings[dto.DeviceMac] = new ObservableCollection<Tp358ReadingDto>();
                        var deviceVm = new DeviceViewModel 
                        { 
                            DeviceName = GetDeviceName(dto.DeviceMac),
                            DeviceMac = dto.DeviceMac,
                            Readings = _deviceReadings[dto.DeviceMac]
                        };
                        Devices.Add(deviceVm);
                        Console.WriteLine($"[Desktop] Neues Gerät hinzugefügt: {GetDeviceName(dto.DeviceMac)} ({dto.DeviceMac}), Devices.Count = {Devices.Count}");
                    }
                    
                    var deviceReadings = _deviceReadings[dto.DeviceMac];
                    deviceReadings.Insert(0, dto);
                    while (deviceReadings.Count > 100)
                        deviceReadings.RemoveAt(deviceReadings.Count - 1);
                    
                    Console.WriteLine($"[Desktop] MAC={dto.DeviceMac}, Readings.Count = {Readings.Count}, Devices.Count = {Devices.Count}");
                });
            });

            _connection.Closed += async (error) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Status = error is not null ? $"Error: {error.Message}" : "Disconnected";
                    Console.WriteLine($"[Desktop] Verbindung geschlossen: {error?.Message ?? "Normal"}");
                });
            };

            Console.WriteLine("[Desktop] Starte SignalR Verbindung...");
            await _connection.StartAsync();
            Status = "Connected";
            Console.WriteLine("[Desktop] Verbindung erfolgreich hergestellt!");
        }
        catch (Exception ex)
        {
            Status = $"Connection failed: {ex.Message}";
            Console.WriteLine($"[Desktop] Verbindungsfehler: {ex}");
            _connection = null;
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        var connection = _connection;
        if (connection is null)
            return;

        try
        {
            Status = "Disconnecting...";
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Status = $"Disconnect error: {ex.Message}";
        }
        finally
        {
            _connection = null;
            Status = "Disconnected";
        }
    }
}
