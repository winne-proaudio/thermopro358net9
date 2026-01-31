using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tp358.Core;

namespace Tp358.Desktop.ViewModels;

public partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty]
    private string deviceName = string.Empty;
    
    [ObservableProperty]
    private string deviceMac = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Tp358ReadingDto> readings = new();
}
