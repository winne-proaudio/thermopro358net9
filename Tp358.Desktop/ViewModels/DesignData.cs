using System;
using System.Collections.ObjectModel;
using Tp358.Core;

namespace Tp358.Desktop.ViewModels;

public static class DesignData
{
    public static MainViewModel MainViewModel { get; } = CreateMainViewModel();

    private static MainViewModel CreateMainViewModel()
    {
        var vm = new MainViewModel();

        // Create sample devices
        var plantageReadings = new ObservableCollection<Tp358ReadingDto>
        {
            new Tp358ReadingDto(
                Timestamp: DateTimeOffset.Now,
                DeviceMac: "FB:B9:30:BB:5E:55",
                Rssi: -52,
                TemperatureC: 20.4,
                HumidityPercent: 55,
                BatteryPercent: 100,
                RawPayloadHex: "00-37-02-2C"
            )
        };

        var katzenzimmerReadings = new ObservableCollection<Tp358ReadingDto>
        {
            new Tp358ReadingDto(
                Timestamp: DateTimeOffset.Now,
                DeviceMac: "F4:B8:2A:C1:37:AF",
                Rssi: -48,
                TemperatureC: 21.9,
                HumidityPercent: 39,
                BatteryPercent: 100,
                RawPayloadHex: "00-27-22-1B-01"
            )
        };

        var marieReadings = new ObservableCollection<Tp358ReadingDto>
        {
            new Tp358ReadingDto(
                Timestamp: DateTimeOffset.Now,
                DeviceMac: "FA:C2:7D:1A:C3:EA",
                Rssi: -45,
                TemperatureC: 22.3,
                HumidityPercent: 47,
                BatteryPercent: 100,
                RawPayloadHex: "00-2F-00-2C"
            )
        };

        vm.Devices.Add(new DeviceViewModel
        {
            DeviceName = "Plantage",
            DeviceMac = "FB:B9:30:BB:5E:55",
            Readings = plantageReadings
        });

        vm.Devices.Add(new DeviceViewModel
        {
            DeviceName = "Katzenzimmer",
            DeviceMac = "F4:B8:2A:C1:37:AF",
            Readings = katzenzimmerReadings
        });

        vm.Devices.Add(new DeviceViewModel
        {
            DeviceName = "Marie",
            DeviceMac = "FA:C2:7D:1A:C3:EA",
            Readings = marieReadings
        });

        // Add to global readings
        foreach (var device in vm.Devices)
        {
            foreach (var reading in device.Readings)
            {
                vm.Readings.Add(reading);
            }
        }

        return vm;
    }
}
