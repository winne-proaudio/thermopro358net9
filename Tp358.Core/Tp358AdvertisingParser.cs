namespace Tp358.Core;

public static class Tp358AdvertisingParser
{
    /// <summary>
    /// Parses ThermoPro TP358 / TP358S manufacturer payload (bytes AFTER the 16-bit company ID).
    /// Observed payload lengths:
    /// - TP358  : 4 bytes  -> humidity present, temperature typically NOT present
    /// - TP358S : 5 bytes  -> temperature present in bytes [2..3] (uint16 LE, /256 °C)
    /// </summary>
    public static Tp358Reading Parse(ReadOnlySpan<byte> payload)
    {
        return payload.Length switch
        {
            4 => ParseTp358(payload),
            5 => ParseTp358S(payload),
            _ => throw new ArgumentException($"Unsupported TP358 payload length: {payload.Length}", nameof(payload))
        };
    }

    private static Tp358Reading ParseTp358(ReadOnlySpan<byte> d)
    {
        // Observed:
        // d[2] = humidity %RH (0..100)
        // d[0], d[1], d[3] appear to be flags/counter/other (device/firmware specific)
        int humidity = d[2];

        return new Tp358Reading
        {
            HumidityPercent = humidity
        };
    }

    private static Tp358Reading ParseTp358S(ReadOnlySpan<byte> d)
    {
        // TP358S observed (payload length 5):
        // d[0..1] : humidity as uint16 little-endian, scaled by 1/256 %RH
        // d[2..3] : temperature as uint16 little-endian, scaled by 1/256 °C, with an observed offset
        // d[4]    : status/battery-ish byte (heuristic)
        ushort rawRh = (ushort)(d[0] | (d[1] << 8));
        int humidityPercent = (int)Math.Round(rawRh / 256.0);

        ushort rawTemp = (ushort)(d[2] | (d[3] << 8));

        // Empirically derived from your device:
        // raw=0x1B22 -> 27.1°C by /256, but display shows ~22.4°C
        // (raw - 1200)/256 ~= 22.4°C
        const int TempOffset = 1200;
        double temperatureCExact = (rawTemp - TempOffset) / 256.0;

        // Optional: "Display-like" truncation to 0.1°C (instead of rounding)
        double temperatureC = Math.Truncate(temperatureCExact * 10.0) / 10.0;

        int? battery = TryDecodeBatteryHeuristic(d[4]);

        return new Tp358Reading
        {
            TemperatureC = temperatureC,
            HumidityPercent = humidityPercent,
            BatteryPercent = battery
        };
    }

    private static int? TryDecodeBatteryHeuristic(byte status)
    {
        // Heuristic placeholder (your logs showed 0x01). Return null for unknown values.
        return status == 0x01 ? 100 : null;
    }
}
