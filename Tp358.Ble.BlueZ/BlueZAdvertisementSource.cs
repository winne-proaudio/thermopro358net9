using System.Threading.Channels;
using Tp358.Ble.Abstractions;
using Tmds.DBus;

namespace Tp358.Ble.BlueZ;

public sealed class BlueZAdvertisementSource : IAdvertisementSource, IBleAdapterInfo
{
    private readonly string? _preferredAdapterId;
    private readonly string? _preferredAdapterAddress;

    public string? AdapterId { get; private set; }
    public string? AdapterAddress { get; private set; }
    public string? AdapterName { get; private set; }

    public BlueZAdvertisementSource(string? preferredAdapterId = null, string? preferredAdapterAddress = null)
    {
        _preferredAdapterId = NormalizeAdapterId(preferredAdapterId);
        _preferredAdapterAddress = NormalizeAdapterAddress(preferredAdapterAddress);
    }

    public async IAsyncEnumerable<AdvertisementFrame> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AdvertisementFrame>();
        using var connection = new Connection(Address.System);
        await connection.ConnectAsync();

        var objectManager = connection.CreateProxy<IObjectManager>("org.bluez", "/");
        var initialObjects = await objectManager.GetManagedObjectsAsync();

        var adapterPath = FindAdapterPath(initialObjects, _preferredAdapterId, _preferredAdapterAddress);
        if (adapterPath == null)
        {
            throw new InvalidOperationException("No Bluetooth adapter found.");
        }

        SetAdapterInfo(adapterPath.Value, initialObjects);

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

    private void SetAdapterInfo(ObjectPath adapterPath, IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> managedObjects)
    {
        var path = adapterPath.ToString();
        AdapterId = path.Split('/').LastOrDefault();

        if (managedObjects.TryGetValue(adapterPath, out var interfaces)
            && interfaces.TryGetValue("org.bluez.Adapter1", out var props))
        {
            if (props.TryGetValue("Alias", out var aliasObj) && aliasObj is string alias && !string.IsNullOrWhiteSpace(alias))
            {
                AdapterName = alias;
            }
            else if (props.TryGetValue("Name", out var nameObj) && nameObj is string name && !string.IsNullOrWhiteSpace(name))
            {
                AdapterName = name;
            }

            if (props.TryGetValue("Address", out var addressObj) && addressObj is string address && !string.IsNullOrWhiteSpace(address))
            {
                AdapterAddress = address;
            }
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

    private static ObjectPath? FindAdapterPath(
        IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> managedObjects,
        string? preferredAdapterId,
        string? preferredAdapterAddress)
    {
        ObjectPath? firstAdapter = null;
        ObjectPath? addressMatch = null;

        foreach (var (path, interfaces) in managedObjects)
        {
            if (interfaces.ContainsKey("org.bluez.Adapter1"))
            {
                if (firstAdapter is null)
                {
                    firstAdapter = path;
                }

                if (!string.IsNullOrWhiteSpace(preferredAdapterId))
                {
                    var pathString = path.ToString();
                    var id = pathString.Split('/').LastOrDefault();
                    if (string.Equals(id, preferredAdapterId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pathString, preferredAdapterId, StringComparison.OrdinalIgnoreCase)
                        || pathString.EndsWith("/" + preferredAdapterId, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }

                if (!string.IsNullOrWhiteSpace(preferredAdapterAddress)
                    && interfaces.TryGetValue("org.bluez.Adapter1", out var props)
                    && props.TryGetValue("Address", out var addressObj)
                    && addressObj is string address
                    && string.Equals(address.Trim(), preferredAdapterAddress, StringComparison.OrdinalIgnoreCase))
                {
                    addressMatch = path;
                }
            }
        }

        return addressMatch ?? firstAdapter;
    }

    private static string? NormalizeAdapterId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeAdapterAddress(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

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
