namespace MyWahoo.Core.Ble;

public sealed record BleAdvertisement(
    ulong Address,
    string? LocalName,
    short Rssi,
    IReadOnlyList<Guid> ServiceUuids,
    IReadOnlyDictionary<ushort, byte[]> ManufacturerData,
    DateTimeOffset Timestamp);

[Flags]
public enum GattProperties
{
    None = 0,
    Read = 1,
    Write = 2,
    WriteWithoutResponse = 4,
    Notify = 8,
    Indicate = 16,
}

public sealed class BleException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The platform Bluetooth LE radio (WinRT on Windows, fakes in tests).</summary>
public interface IBleAdapter
{
    /// <summary>Raised on a background thread for every advertisement received while scanning.</summary>
    event EventHandler<BleAdvertisement>? AdvertisementReceived;

    bool IsScanning { get; }

    /// <summary>Active scan (needed to get names and full manufacturer data).</summary>
    void StartScan();

    void StopScan();

    Task<IBlePeripheral> ConnectAsync(ulong address, CancellationToken ct = default);
}

public interface IBlePeripheral : IAsyncDisposable
{
    ulong Address { get; }
    string? Name { get; }
    bool IsConnected { get; }

    /// <summary>Raised once when the link drops (not when disposed by us).</summary>
    event EventHandler? Disconnected;

    /// <summary>Finds a characteristic in the first of <paramref name="services"/> that has it; null if absent.</summary>
    Task<IGattCharacteristic?> GetCharacteristicAsync(IReadOnlyList<Guid> services, Guid characteristic, CancellationToken ct = default);
}

public interface IGattCharacteristic
{
    Guid Uuid { get; }
    GattProperties Properties { get; }

    /// <summary>Writes a value; throws <see cref="BleException"/> on failure.</summary>
    Task WriteAsync(byte[] data, bool withResponse, CancellationToken ct = default);

    /// <summary>Enables notifications (or indications when that's all the characteristic supports).</summary>
    Task SubscribeAsync(Action<byte[]> onValue, CancellationToken ct = default);

    Task<byte[]> ReadAsync(CancellationToken ct = default);
}

public static class GattCharacteristicExtensions
{
    /// <summary>Writes with response when the characteristic supports it (so errors surface), else without.</summary>
    public static Task WritePreferResponseAsync(this IGattCharacteristic c, byte[] data, CancellationToken ct = default) =>
        c.WriteAsync(data, c.Properties.HasFlag(GattProperties.Write) || !c.Properties.HasFlag(GattProperties.WriteWithoutResponse), ct);

    /// <summary>Writes without response when supported (Click v2 only supports that), else with.</summary>
    public static Task WritePreferNoResponseAsync(this IGattCharacteristic c, byte[] data, CancellationToken ct = default) =>
        c.WriteAsync(data, !c.Properties.HasFlag(GattProperties.WriteWithoutResponse), ct);
}
