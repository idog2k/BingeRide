using MyWahoo.Core.Ble;

namespace MyWahoo.Core.Tests;

/// <summary>In-memory BLE peripheral: records writes and lets a responder script the device's replies.</summary>
internal sealed class FakePeripheral(ulong address = 0xAABBCCDDEEFF, string? name = "KICKR CORE CD5E") : IBlePeripheral
{
    private readonly Dictionary<(Guid Service, Guid Char), FakeCharacteristic> _chars = [];

    public ulong Address { get; } = address;
    public string? Name { get; } = name;
    public bool IsConnected { get; private set; } = true;
    public event EventHandler? Disconnected;

    /// <summary>Called for every write: (characteristic, data).</summary>
    public Action<FakeCharacteristic, byte[]>? Responder { get; set; }

    public List<(Guid Char, byte[] Data)> Writes { get; } = [];

    public FakeCharacteristic Add(Guid service, Guid uuid, GattProperties props)
    {
        var c = new FakeCharacteristic(this, uuid, props);
        _chars[(service, uuid)] = c;
        return c;
    }

    public FakeCharacteristic Get(Guid uuid) => _chars.First(kv => kv.Key.Char == uuid).Value;

    public IEnumerable<byte[]> WritesTo(Guid uuid) => Writes.Where(w => w.Char == uuid).Select(w => w.Data);

    public Task<IGattCharacteristic?> GetCharacteristicAsync(IReadOnlyList<Guid> services, Guid characteristic, CancellationToken ct = default)
    {
        foreach (var s in services)
        {
            if (_chars.TryGetValue((s, characteristic), out var c)) return Task.FromResult<IGattCharacteristic?>(c);
        }
        return Task.FromResult<IGattCharacteristic?>(null);
    }

    public void Drop()
    {
        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    internal void OnWrite(FakeCharacteristic c, byte[] data)
    {
        Writes.Add((c.Uuid, data));
        Responder?.Invoke(c, data);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCharacteristic(FakePeripheral owner, Guid uuid, GattProperties props) : IGattCharacteristic
{
    private readonly List<Action<byte[]>> _subscribers = [];

    public Guid Uuid { get; } = uuid;
    public GattProperties Properties { get; } = props;
    public bool Subscribed => _subscribers.Count > 0;
    public List<bool> WriteModes { get; } = [];

    public Task WriteAsync(byte[] data, bool withResponse, CancellationToken ct = default)
    {
        WriteModes.Add(withResponse);
        owner.OnWrite(this, data);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(Action<byte[]> onValue, CancellationToken ct = default)
    {
        _subscribers.Add(onValue);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());

    /// <summary>Device → app notification/indication.</summary>
    public void Push(byte[] data)
    {
        foreach (var s in _subscribers.ToList()) s(data);
    }
}

internal sealed class FakeAdapter(Func<ulong, IBlePeripheral> factory) : IBleAdapter
{
    public event EventHandler<BleAdvertisement>? AdvertisementReceived;
    public bool IsScanning { get; private set; }
    public List<ulong> ConnectCalls { get; } = [];

    public void StartScan() => IsScanning = true;
    public void StopScan() => IsScanning = false;

    public Task<IBlePeripheral> ConnectAsync(ulong address, CancellationToken ct = default)
    {
        ConnectCalls.Add(address);
        return Task.FromResult(factory(address));
    }

    public void Advertise(BleAdvertisement adv) => AdvertisementReceived?.Invoke(this, adv);
}

internal static class Hex
{
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    public static string Of(byte[] data) => Convert.ToHexString(data);
}
