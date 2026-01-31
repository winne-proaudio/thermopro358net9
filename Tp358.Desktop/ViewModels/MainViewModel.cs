using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using Tp358.Core;

namespace Tp358.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private HubConnection? _connection;

    public ObservableCollection<Tp358ReadingDto> Readings { get; } = new();

    [ObservableProperty]
    private string status = "Disconnected";

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (_connection is not null)
            return;

        Status = "Connecting...";

        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:5055/live")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<Tp358ReadingDto>("reading", dto =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Readings.Insert(0, dto);
                while (Readings.Count > 200)
                    Readings.RemoveAt(Readings.Count - 1);
            });
        });

        await _connection.StartAsync();
        Status = "Connected";
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        if (_connection is null)
            return;

        Status = "Disconnecting...";
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;

        Status = "Disconnected";
    }
}
