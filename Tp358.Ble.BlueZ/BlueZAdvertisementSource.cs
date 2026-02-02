using System.Threading.Channels;
using Tp358.Ble.Abstractions;
using Tmds.DBus;

namespace Tp358.Ble.BlueZ;

public sealed class BlueZAdvertisementSource : IAdvertisementSource
{
    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AdvertisementFrame>();
        using var connection = new Connection(Address.System);
        await connection.ConnectAsync();

        var objectManager = connection.CreateProxy<IObjectManager>("org.bluez", "/");
        var initialObjects = await objectManager.GetManagedObjectsAsync();

        var adapterPath = FindAdapterPath(initialObjects);
        if (adapterPath == null)
        {
            throw new InvalidOperationException("No Bluetooth adapter found.");
        }

        var adapter = connection.CreateProxy<IAdapter1>("org.bluez", adapterPath.Value);
        await TryResetDiscoveryAsync(adapter, ct);

        ct.Register(() =>
        {
            try
            {
                _ = adapter.StopDiscoveryAsync();
            }
            catch
            {
                // ignore stop errors
            }

            channel.Writer.TryComplete();
        });

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> managedObjects;
                try
                {
                    managedObjects = await objectManager.GetManagedObjectsAsync();
                }
                catch
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                foreach (var (_, interfaces) in managedObjects)
                {
                    if (interfaces.TryGetValue("org.bluez.Device1", out var props))
                    {
                        TryEmitFrame(props, channel, ct);
                    }
                }

                await Task.Delay(1000, ct);
            }
        }, ct);

        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
        {
            yield return frame;
        }
    }

    private static async Task TryResetDiscoveryAsync(IAdapter1 adapter, CancellationToken ct)
    {
        try
        {
            await adapter.StopDiscoveryAsync();
        }
        catch
        {
            // ignore stop errors (not discovering or adapter busy)
        }

        try
        {
            await adapter.StartDiscoveryAsync();
        }
        catch (DBusException ex) when (IsInProgress(ex))
        {
            // Discovery already running; treat as success.
        }
    }

    private static bool IsInProgress(DBusException ex)
        => string.Equals(ex.ErrorName, "org.bluez.Error.InProgress", StringComparison.OrdinalIgnoreCase);

    private static ObjectPath? FindAdapterPath(IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> managedObjects)
    {
        foreach (var (path, interfaces) in managedObjects)
        {
            if (interfaces.ContainsKey("org.bluez.Adapter1"))
            {
                return path;
            }
        }

        return null;
    }

    private static void TryEmitFrame(IDictionary<string, object> props, Channel<AdvertisementFrame> channel, CancellationToken ct)
    {
        if (!props.TryGetValue("ManufacturerData", out var manufacturerDataObj) || manufacturerDataObj == null)
        {
            return;
        }

        var address = props.TryGetValue("Address", out var addressObj) ? addressObj as string : null;
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var rssi = 0;
        if (props.TryGetValue("RSSI", out var rssiObj))
        {
            if (rssiObj is short s)
            {
                rssi = s;
            }
            else if (rssiObj is int i)
            {
                rssi = i;
            }
        }

        foreach (var (companyId, payload) in EnumerateManufacturerData(manufacturerDataObj))
        {
            bool looksLikeTp = ((companyId & 0x00FF) == 0x00C2) || ((companyId & 0xFF00) == 0xC200);
            if (!looksLikeTp) continue;
            if (payload.Length != 4 && payload.Length != 5) continue;

            var frame = new AdvertisementFrame(
                Timestamp: DateTimeOffset.UtcNow,
                DeviceMac: address,
                Rssi: rssi,
                ManufacturerPayload: payload,
                CompanyId: companyId
            );

            channel.Writer.TryWrite(frame);
        }
    }

    private static IEnumerable<(ushort CompanyId, byte[] Payload)> EnumerateManufacturerData(object manufacturerDataObj)
    {
        if (manufacturerDataObj is IDictionary<ushort, object> objDict)
        {
            foreach (var kvp in objDict)
            {
                if (TryGetBytes(kvp.Value, out var bytes))
                {
                    yield return (kvp.Key, bytes);
                }
            }
            yield break;
        }

        if (manufacturerDataObj is IDictionary<ushort, byte[]> bytesDict)
        {
            foreach (var kvp in bytesDict)
            {
                yield return (kvp.Key, kvp.Value);
            }
        }
    }

    private static bool TryGetBytes(object? value, out byte[] bytes)
    {
        switch (value)
        {
            case null:
                bytes = Array.Empty<byte>();
                return false;
            case byte[] b:
                bytes = b;
                return true;
            case ReadOnlyMemory<byte> rom:
                bytes = rom.ToArray();
                return true;
            case Memory<byte> mem:
                bytes = mem.ToArray();
                return true;
            case IEnumerable<byte> enumerable:
                bytes = enumerable.ToArray();
                return true;
            default:
                bytes = Array.Empty<byte>();
                return false;
        }
    }
}
