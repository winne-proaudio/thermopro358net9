using Microsoft.Extensions.Logging;
using Tp358.Ble.Abstractions;

namespace Tp358.Backend;

public sealed class FallbackAdvertisementSource(
    IAdvertisementSource primary,
    IAdvertisementSource fallback,
    ILogger<FallbackAdvertisementSource> logger
) : IAdvertisementSource
{
    public IAdvertisementSource Primary { get; } = primary;
    public IAdvertisementSource Fallback { get; } = fallback;

    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<AdvertisementFrame>();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in primary.WatchAsync(ct))
                {
                    await channel.Writer.WriteAsync(frame, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Primary BLE source failed; switching to fallback.");
                try
                {
                    await foreach (var frame in fallback.WatchAsync(ct))
                    {
                        await channel.Writer.WriteAsync(frame, ct);
                    }
                }
                catch (Exception innerEx) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(innerEx, "Fallback BLE source failed.");
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
        {
            yield return frame;
        }
    }
}

public sealed class FakeAdvertisementSource : IAdvertisementSource
{
    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sensors = new[]
        {
            ("FB:B9:30:BB:5E:55", 1.0, 0.5),
            ("F4:B8:2A:C1:37:AF", 0.0, 0.0),
            ("FA:C2:7D:1A:C3:EA", -0.6, 1.0),
        };

        var rnd = new Random(1);

        while (!ct.IsCancellationRequested)
        {
            foreach (var (mac, tempBias, rhBias) in sensors)
            {
                var rh = 41.0 + rnd.NextDouble() * 2.0 + rhBias;        // ~41..43 %
                ushort rawRh = (ushort)Math.Round(rh * 256.0);          // /256

                var temp = 22.0 + rnd.NextDouble() * 1.0 + tempBias;    // ~22..23 °C
                const int tempOffset = 1200;
                ushort rawTemp = (ushort)Math.Round(temp * 256.0 + tempOffset);

                var payload = new byte[5];
                payload[0] = (byte)(rawRh & 0xFF);
                payload[1] = (byte)(rawRh >> 8);
                payload[2] = (byte)(rawTemp & 0xFF);
                payload[3] = (byte)(rawTemp >> 8);
                // Marker for fake data: set an unrealistic battery/status value.
                payload[4] = 0xFF;

                // Encode temperature in CompanyId high byte: temp * 10
                ushort companyId = (ushort)(0x00C2 | ((byte)(temp * 10) << 8));

                yield return new AdvertisementFrame(
                    Timestamp: DateTimeOffset.UtcNow,
                    DeviceMac: mac,
                    Rssi: -55 - rnd.Next(0, 8),
                    ManufacturerPayload: payload,
                    CompanyId: companyId
                );
            }

            await Task.Delay(1000, ct);
        }
    }
}
