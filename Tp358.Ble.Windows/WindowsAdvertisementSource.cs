using System.Runtime.InteropServices.WindowsRuntime;
using Tp358.Ble.Abstractions;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Tp358.Ble.Windows;

public sealed class WindowsAdvertisementSource : IAdvertisementSource
{
    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<AdvertisementFrame>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, e) =>
        {
            try
            {
                var m = e.Advertisement.ManufacturerData;
                if (m.Count == 0) return;

                foreach (var md in m)
                {
                    ushort companyId = md.CompanyId;
                    bool looksLikeTp = ((companyId & 0x00FF) == 0x00C2) || ((companyId & 0xFF00) == 0xC200);
                    
                    var payload = ReadAll(md.Data);
                    
                    // Debug logging: show ALL manufacturer data
                    if (looksLikeTp)
                    {
                        var addr = FormatBluetoothAddress(e.BluetoothAddress);
                        System.Console.WriteLine($"[BLE] Device={addr}, CompanyId=0x{companyId:X4}, PayloadLength={payload.Length}, Payload={BitConverter.ToString(payload.ToArray())}");
                    }
                    
                    if (!looksLikeTp) continue;
                    if (payload.Length != 4 && payload.Length != 5) continue;

                    var addr2 = FormatBluetoothAddress(e.BluetoothAddress);

                    var frame = new AdvertisementFrame(
                        Timestamp: DateTimeOffset.UtcNow,
                        DeviceMac: addr2,
                        Rssi: e.RawSignalStrengthInDBm,
                        ManufacturerPayload: payload.ToArray(),
                        CompanyId: companyId
                    );

                    channel.Writer.TryWrite(frame);
                }
            }
            catch
            {
                // ignore errors
            }
        };

        watcher.Start();

        ct.Register(() =>
        {
            watcher.Stop();
            channel.Writer.Complete();
        });

        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
        {
            yield return frame;
        }
    }

    private static ReadOnlySpan<byte> ReadAll(IBuffer buffer)
    {
        var data = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(data);
        return data;
    }

    private static string FormatBluetoothAddress(ulong address)
    {
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
}
