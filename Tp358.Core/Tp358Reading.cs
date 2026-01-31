namespace Tp358.Core;

public sealed class Tp358Reading
{
    public double? TemperatureC { get; init; }
    public int? HumidityPercent { get; init; }
    public int? BatteryPercent { get; init; }

    public override string ToString()
        => $"Temp={TemperatureC?.ToString("F1") ?? "n/a"} °C, " +
           $"RH={HumidityPercent?.ToString() ?? "n/a"} %, " +
           $"Bat={BatteryPercent?.ToString() ?? "n/a"} %";
}
public sealed record Tp358ReadingDto(
    DateTimeOffset Timestamp,
    string DeviceMac,
    int Rssi,
    double? TemperatureC,
    int? HumidityPercent,
    int? BatteryPercent,
    string? RawPayloadHex
);