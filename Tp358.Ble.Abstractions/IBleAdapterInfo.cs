namespace Tp358.Ble.Abstractions;

public interface IBleAdapterInfo
{
    string? AdapterId { get; }
    string? AdapterAddress { get; }
    string? AdapterName { get; }
}
