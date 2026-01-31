namespace Tp358.Ble.Abstractions;

public sealed record AdvertisementFrame(
    DateTimeOffset Timestamp,
    string DeviceMac,
    int Rssi,
    byte[] ManufacturerPayload,
    ushort CompanyId
);

public interface IAdvertisementSource
{
    IAsyncEnumerable<AdvertisementFrame> WatchAsync(CancellationToken ct);
}