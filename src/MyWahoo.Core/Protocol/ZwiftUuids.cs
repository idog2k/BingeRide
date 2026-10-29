namespace MyWahoo.Core.Protocol;

/// <summary>
/// Zwift's BLE channel, shared by Zwift-protocol trainers (virtual shifting) and Zwift controllers (Click/Play/Ride).
/// Newer firmware moved the service to the 16-bit FC82 UUID; characteristics kept their 128-bit UUIDs.
/// </summary>
public static class ZwiftUuids
{
    public static readonly Guid ServiceFc82 = BleUuid.From16(0xFC82);
    public static readonly Guid ServiceLegacy = Guid.Parse("00000001-19CA-4651-86E5-FA29DCDD09D1");
    public static readonly IReadOnlyList<Guid> Services = [ServiceFc82, ServiceLegacy];

    /// <summary>Notify: riding data, button frames, logs.</summary>
    public static readonly Guid Async = Guid.Parse("00000002-19CA-4651-86E5-FA29DCDD09D1");
    /// <summary>Write: all commands.</summary>
    public static readonly Guid SyncRx = Guid.Parse("00000003-19CA-4651-86E5-FA29DCDD09D1");
    /// <summary>Indicate: RideOn reply and request responses.</summary>
    public static readonly Guid SyncTx = Guid.Parse("00000004-19CA-4651-86E5-FA29DCDD09D1");

    public const ushort ZwiftCompanyId = 0x094A;
}

public static class BleUuid
{
    /// <summary>Expands a 16-bit SIG UUID onto the Bluetooth base UUID.</summary>
    public static Guid From16(ushort id) => Guid.Parse($"0000{id:X4}-0000-1000-8000-00805F9B34FB");
}
