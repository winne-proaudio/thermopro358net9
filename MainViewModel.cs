using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using Tp358.Core;

namespace Tp358.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private HubConnection? _conn;

    public ObservableCollection<Tp358ReadingDto> Readings { get; } = new();

    [ObservableProperty]
    private string status = "Disconnected";

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (_conn is not null)
            return;

        Status = "Connecting...";

        _conn = new HubConnectionBuilder()
            .WithUrl("http://localhost:5055/live")
            .WithAutomaticReconnect()
            .Build();

        _conn.On<Tp358ReadingDto>("reading", dto =>
        {
            // UI-thread: Avalonia hat Dispatcher.UIThread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Readings.Insert(0, dto);
                while (Readings.Count > 200)
                    Readings.RemoveAt(Readings.Count - 1);
            });
        });

        await _conn.StartAsync();
        Status = "Connected";
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        if (_conn is null)
            return;

        Status = "Disconnecting...";
        await _conn.StopAsync();
        await _conn.DisposeAsync();
        _conn = null;
        Status = "Disconnected";
    }
}
