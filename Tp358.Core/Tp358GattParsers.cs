namespace Tp358.Core;

public static class Tp358GattParsers
{
    /// <summary>
    /// BLE Environmental Sensing Temperature (UUID 0x2A6E):
    /// sint16 (LE), unit 0.01 °C
    /// </summary>
    public static double ParseTemperatureC(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new ArgumentException("Temperature characteristic expects 2 bytes.");
        short raw = (short)(data[0] | (data[1] << 8));
        return raw / 100.0;
    }

    /// <summary>
    /// BLE Environmental Sensing Humidity (UUID 0x2A6F):
    /// uint16 (LE), unit 0.01 %RH
    /// </summary>
    public static double ParseHumidityPercent(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new ArgumentException("Humidity characteristic expects 2 bytes.");
        ushort raw = (ushort)(data[0] | (data[1] << 8));
        return raw / 100.0;
    }

    /// <summary>
    /// BLE Battery Level (UUID 0x2A19):
    /// uint8 (0..100)
    /// </summary>
    public static int ParseBatteryPercent(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1) throw new ArgumentException("Battery Level characteristic expects 1 byte.");
        return data[0];
    }
}
