using Tp358.Ble.Abstractions;

namespace Tp358.Backend;

public sealed class FakeAdvertisementSource : IAdvertisementSource
{
    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rnd = new Random(1);
        var mac = "F4:B8:2A:C1:37:AF";

        while (!ct.IsCancellationRequested)
        {
            var rh = 41.0 + rnd.NextDouble() * 2.0;        // 41..43 %
            ushort rawRh = (ushort)Math.Round(rh * 256.0); // /256

            var temp = 22.0 + rnd.NextDouble() * 1.0;      // 22..23 °C
            const int tempOffset = 1200;
            ushort rawTemp = (ushort)Math.Round(temp * 256.0 + tempOffset);

            var payload = new byte[5];
            payload[0] = (byte)(rawRh & 0xFF);
            payload[1] = (byte)(rawRh >> 8);
            payload[2] = (byte)(rawTemp & 0xFF);
            payload[3] = (byte)(rawTemp >> 8);
            payload[4] = 0x01;

            // Encode temperature in CompanyId high byte: temp * 10
            ushort companyId = (ushort)(0x00C2 | ((byte)(temp * 10) << 8));
            
            yield return new AdvertisementFrame(
                Timestamp: DateTimeOffset.UtcNow,
                DeviceMac: mac,
                Rssi: -55 - rnd.Next(0, 8),
                ManufacturerPayload: payload,
                CompanyId: companyId
            );

            await Task.Delay(1000, ct);
        }
    }
}
