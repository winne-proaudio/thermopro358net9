namespace Tp358.Core;

public static class Tp358AdvertisingParser
{
    /// <summary>
    /// Parses ThermoPro TP358 / TP358S manufacturer payload (bytes AFTER the 16-bit company ID).
    /// Observed payload lengths:
    /// - TP358  : 4 bytes  -> humidity present, temperature typically NOT present
    /// - TP358S : 5 bytes  -> temperature encoded in CompanyId high byte, humidity in payload byte[1]
    /// </summary>
    public static Tp358Reading Parse(ReadOnlySpan<byte> payload, ushort companyId)
    {
        return payload.Length switch
        {
            4 => ParseTp358(payload, companyId),
            5 => ParseTp358S(payload, companyId),
            _ => throw new ArgumentException($"Unsupported TP358 payload length: {payload.Length}", nameof(payload))
        };
    }

    private static Tp358Reading ParseTp358(ReadOnlySpan<byte> d, ushort companyId)
    {
        // TP358 observed (payload length 4):
        // CompanyId format: likely 0xTTC2 where TT = temperature * 10 (same as TP358S)
        // Example observations:
        // CompanyId=0xDFC2, Payload=00-31-00-2C
        // CompanyId=0xCDC2, Payload=00-37-02-2C
        // 
        // Testing hypothesis:
        // - Temperature from CompanyId high byte / 10
        // - Humidity from payload byte[1] (like TP358S)
        
        // Extract temperature from CompanyId high byte / 10
        byte tempByte = (byte)(companyId >> 8);
        double temperatureC = tempByte / 10.0;
        
        // Extract humidity from byte[1]
        int humidityPercent = d[1];
        
        // Log for debugging
        System.Console.WriteLine($"[TP358 Parser] CompanyId=0x{companyId:X4}, TempByte=0x{tempByte:X2}={tempByte} → {temperatureC}°C, HumidityByte=0x{d[1]:X2}={d[1]} → {humidityPercent}%");

        int? battery = TryDecodeBatteryStatusTp358(d);

        return new Tp358Reading
        {
            TemperatureC = temperatureC,
            HumidityPercent = humidityPercent,
            BatteryPercent = battery
        };
    }

    private static Tp358Reading ParseTp358S(ReadOnlySpan<byte> d, ushort companyId)
    {
        // TP358S observed (payload length 5):
        // CompanyId format: 0xTTC2 where TT = temperature * 10
        // Example: CompanyId=0xA9C2 → temp = 0xA9 = 169 → 16.9°C
        // Payload format: [RH_low] [RH_high] [??] [??] [Status]
        // Payload example: 00-28-22-1B-01
        //   d[0] = 0x00  (unused)
        //   d[1] = 0x28  → humidity = 0x28 = 40%
        //   d[2] = 0x22  → unknown constant
        //   d[3] = 0x1B  → unknown constant
        //   d[4] = 0x01  → status/battery
        
        // Extract humidity from byte[1] directly (not /256!)
        int humidityPercent = d[1];

        // Extract temperature from CompanyId high byte / 10
        byte tempByte = (byte)(companyId >> 8);
        double temperatureC = tempByte / 10.0;

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
        if (status == 0x01)
        {
            return 100;
        }

        // Debug/fake marker: show an obviously unrealistic battery percent.
        if (status == 0xFF)
        {
            return 255;
        }

        return null;
    }

    private static int? TryDecodeBatteryStatusTp358(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3)
        {
            return null;
        }

        // Heuristic: older TP358 seems to expose only a low/ok flag in byte[2].
        // Observed: 0x02 -> OK, 0x00 -> LOW.
        var status = d[2];
        if ((status & 0x02) != 0)
        {
            return 100;
        }
        if (status == 0x01)
        {
            return 50;
        }
        if (status == 0x00)
        {
            return 0;
        }

        return null;
    }
}
