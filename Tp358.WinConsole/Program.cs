using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Tp358.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        Args.Parse(args);

        Console.WriteLine("TP358/TP358S Scanner (.NET 9, Windows BLE)");
        Console.WriteLine("Options: --gatt (periodically connect & read GATT), --interval=SECONDS (default 30)");
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();

        var lastGattRead = new ConcurrentDictionary<ulong, DateTimeOffset>();
        var knownNames = new ConcurrentDictionary<ulong, string>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += async (_, e) =>
        {
            try
            {
                var m = e.Advertisement.ManufacturerData;
                if (m.Count == 0) return;

                string? name = e.Advertisement.LocalName;
                if (!string.IsNullOrWhiteSpace(name))
                    knownNames[e.BluetoothAddress] = name;

                foreach (var md in m)
                {
                    ushort companyId = md.CompanyId;
                    bool looksLikeTp = ((companyId & 0x00FF) == 0x00C2) || ((companyId & 0xFF00) == 0xC200);
                    if (!looksLikeTp) continue;

                    var payload = ReadAll(md.Data);

                    // TP358S-Fokus: wir nehmen erstmal nur 5-Byte Payloads
                    if (payload.Length != 5)
                        continue;

                    // Rohbytes einmal sichtbar machen + Kandidaten-Decodes
                    var p = payload.ToArray(); // 5 bytes

                    ushort tU16 = (ushort)(p[2] | (p[3] << 8));
                    short tS16 = unchecked((short)tU16);

                    double tDiv256 = tU16 / 256.0;
                    double tSignedDiv100 = tS16 / 100.0;

                    byte rhByte = p[0];
                    ushort rhU16 = (ushort)(p[0] | (p[1] << 8));
                    double rhDiv256 = rhU16 / 256.0;

                    byte status = p[4];

                    var addr = FormatBluetoothAddress(e.BluetoothAddress);
                    var devName = knownNames.TryGetValue(e.BluetoothAddress, out var n) ? n : "(no name)";

                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] {devName} {addr} RSSI={e.RawSignalStrengthInDBm,4} dBm  " +
                        $"TP358S raw={BitConverter.ToString(p)}  " +
                        $"T(u16)=0x{tU16:X4} T/256={tDiv256:F1}C T(s16)/100={tSignedDiv100:F1}C  " +
                        $"RH(b0)={rhByte}% RH(u16)/256={rhDiv256:F1}%  " +
                        $"status=0x{status:X2}");

                    Tp358Reading? advReading = null;
                    try
                    {
                        advReading = Tp358AdvertisingParser.Parse(payload);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    if (advReading is not null)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {devName} {addr} ADV(parsed): {advReading}");

                    if (Args.ConnectGatt)
                    {
                        var now = DateTimeOffset.Now;
                        var last = lastGattRead.GetOrAdd(e.BluetoothAddress, DateTimeOffset.MinValue);
                        if ((now - last).TotalSeconds >= Args.ConnectEverySeconds)
                        {
                            lastGattRead[e.BluetoothAddress] = now;

                            Task.Run(() => TryReadGattAsync(e.BluetoothAddress, devName));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Watcher error: {ex.Message}");
            }
        };

        // --- Ab hier ergänze ich die fehlenden Namen, ohne deinen Main oben umzubauen ---

        static async Task TryReadGattAsync(ulong bluetoothAddress, string devName)
        {
            try
            {
                var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
                if (device is null) return;

                using (device)
                {
                    // Environmental Sensing (Temp/Humidity)
                    double? tempC = null;
                    double? rh = null;

                    var env = await device.GetGattServicesForUuidAsync(Uuids.EnvironmentalSensingService, BluetoothCacheMode.Uncached);
                    if (env.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var svc in env.Services)
                        {
                            using (svc)
                            {
                                var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                                if (chars.Status != GattCommunicationStatus.Success) continue;

                                foreach (var ch in chars.Characteristics)
                                {
                                   
                                        if (ch.Uuid == Uuids.Temperature)
                                        {
                                            var v = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                                            if (v.Status == GattCommunicationStatus.Success)
                                                tempC = Tp358GattParsers.ParseTemperatureC(ReadAll(v.Value));
                                        }
                                        else if (ch.Uuid == Uuids.Humidity)
                                        {
                                            var v = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                                            if (v.Status == GattCommunicationStatus.Success)
                                                rh = Tp358GattParsers.ParseHumidityPercent(ReadAll(v.Value));
                                        }
                                    
                                }
                            }
                        }
                    }

                    // Battery
                    int? bat = null;
                    var batSvc = await device.GetGattServicesForUuidAsync(Uuids.BatteryService, BluetoothCacheMode.Uncached);
                    if (batSvc.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var svc in batSvc.Services)
                        {
                            using (svc)
                            {
                                var chars = await svc.GetCharacteristicsForUuidAsync(Uuids.BatteryLevel, BluetoothCacheMode.Uncached);
                                if (chars.Status != GattCommunicationStatus.Success) continue;

                                foreach (var ch in chars.Characteristics)
                                {
                                   
                                        var v = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                                        if (v.Status == GattCommunicationStatus.Success)
                                            bat = Tp358GattParsers.ParseBatteryPercent(ReadAll(v.Value));
                                    
                                }
                            }
                        }
                    }

                    var addr = FormatBluetoothAddress(bluetoothAddress);
                    var parts = new List<string>();
                    if (tempC is not null) parts.Add($"Temp={tempC:F2} °C");
                    if (rh is not null) parts.Add($"RH={rh:F2} %");
                    if (bat is not null) parts.Add($"Bat={bat}%");

                    if (parts.Count > 0)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {devName} {addr} GATT: {string.Join(", ", parts)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GATT read failed: {devName} {FormatBluetoothAddress(bluetoothAddress)} : {ex.Message}");
            }
        }

        static ReadOnlySpan<byte> ReadAll(IBuffer buffer)
        {
            var data = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(data);
            return data;
        }

        // ... existing code ...
        // hier endet bei dir irgendwo watcher.Received += ... ;  (WICHTIG: mit Semikolon)

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            watcher.Stop();
            Environment.Exit(0);
        };

        watcher.Start();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    // ---- Ab hier ist wieder KLASSEN-SCOPE (nicht mehr Main) ----

    private static string FormatBluetoothAddress(ulong address)
    {
        // address is 48-bit (Bluetooth MAC), format as AA:BB:CC:DD:EE:FF
        Span<char> chars = stackalloc char[17];
        for (int i = 5; i >= 0; i--)
        {
            byte b = (byte)((address >> (i * 8)) & 0xFF);
            int pos = (5 - i) * 3;
            chars[pos] = GetHex(b >> 4);
            chars[pos + 1] = GetHex(b & 0xF);
            if (i != 0) chars[pos + 2] = ':';
        }
        return new string(chars);
    }

    private static char GetHex(int v) => (char)(v < 10 ? ('0' + v) : ('A' + (v - 10)));

    private static class Uuids
    {
        public static readonly Guid EnvironmentalSensingService = Guid.Parse("0000181A-0000-1000-8000-00805F9B34FB");
        public static readonly Guid Temperature = Guid.Parse("00002A6E-0000-1000-8000-00805F9B34FB");
        public static readonly Guid Humidity = Guid.Parse("00002A6F-0000-1000-8000-00805F9B34FB");

        public static readonly Guid BatteryService = Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB");
        public static readonly Guid BatteryLevel = Guid.Parse("00002A19-0000-1000-8000-00805F9B34FB");
    }

    private static class Args
    {
        public static bool ConnectGatt { get; private set; }
        public static int ConnectEverySeconds { get; private set; } = 30;

        public static void Parse(string[] args)
        {
            foreach (var a in args)
            {
                if (a.Equals("--gatt", StringComparison.OrdinalIgnoreCase)) ConnectGatt = true;

                if (a.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(a.Split('=', 2)[1], out var s) && s >= 5)
                        ConnectEverySeconds = s;
                }
            }
        }
    }
}

// ... existing code ...