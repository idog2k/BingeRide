namespace MyWahoo.Core.Protocol;

/// <summary>Buttons in Zwift Ride/Click v2 bitmap layout (also used to normalise Click v1 frames).</summary>
[Flags]
public enum ClickButtons : uint
{
    None = 0,
    Left = 0x0001,
    Up = 0x0002,
    Right = 0x0004,
    Down = 0x0008,
    A = 0x0010,
    B = 0x0020,
    Y = 0x0040,
    Z = 0x0080,
    Minus = 0x0100,
    Plus = 0x1000,
}

public enum ZwiftControllerKind
{
    Unknown,
    ClickV1,
    /// <summary>Click v2 right puck: "+" and A/B/Y/Z. Never locks.</summary>
    ClickV2Right,
    /// <summary>Click v2 left puck: "−" and the D-pad. Locks ~60 s after connect unless Zwift unlocked it recently.</summary>
    ClickV2Left,
}

public enum ClickMessageKind { None, Buttons, Challenge, LockStatus, Other }

public readonly record struct ClickMessage(ClickMessageKind Kind, ClickButtons Pressed = ClickButtons.None, bool Locked = false);

/// <summary>Zwift Click v1 / v2 controller protocol (plain-text mode; no encryption needed).</summary>
public static class ZwiftClickProtocol
{
    public const byte TypeClickV1Buttons = 0x37;
    public const byte TypeRideButtons = 0x23;
    public const byte TypeVendor = 0xFF;
    public const byte OpReset = 0x18;

    public static readonly byte[] RideOnV1 = [.. "RideOn"u8];
    public static readonly byte[] RideOnV2 = [.. "RideOn"u8, 0x02, 0x03];
    public static readonly byte[] GetDeviceInfo = [0x00, 0x08, 0x00];
    public static readonly byte[] GetConfigPage = [0x00, 0x08, 0x10];
    public static readonly byte[] ChallengeReply = [0xFF, 0x04, 0x00];
    public static readonly byte[] Reset = [OpReset];

    /// <summary>Buttons each Click v2 puck reports for itself (the left puck also relays the right puck's).</summary>
    public const ClickButtons LeftPuckButtons = ClickButtons.Left | ClickButtons.Up | ClickButtons.Right | ClickButtons.Down | ClickButtons.Minus;
    public const ClickButtons RightPuckButtons = ClickButtons.A | ClickButtons.B | ClickButtons.Y | ClickButtons.Z | ClickButtons.Plus;

    private const uint KnownMask = 0x11FF;

    /// <summary>Identifies a Zwift controller from manufacturer data (company 0x094A) or, failing that, its name.</summary>
    public static ZwiftControllerKind? Identify(IReadOnlyDictionary<ushort, byte[]> manufacturerData, string? name)
    {
        if (manufacturerData.TryGetValue(ZwiftUuids.ZwiftCompanyId, out var payload) && payload.Length >= 1)
        {
            return payload[0] switch
            {
                0x09 => ZwiftControllerKind.ClickV1,
                0x0A => ZwiftControllerKind.ClickV2Right,
                0x0B => ZwiftControllerKind.ClickV2Left,
                _ => null, // Hub trainers, Play, Ride: not handled here
            };
        }
        return name?.StartsWith("Zwift Click", StringComparison.OrdinalIgnoreCase) == true ? ZwiftControllerKind.Unknown : null;
    }

    public static ClickMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return new(ClickMessageKind.None);
        switch (data[0])
        {
            case TypeClickV1Buttons:
            {
                // ClickKeyPadStatus: field 1 = plus, field 2 = minus; 0 = pressed, 1 = released.
                var pressed = ClickButtons.None;
                foreach (var f in ProtoReader.Read(data[1..].ToArray()))
                {
                    if (f.WireType != 0 || f.Varint != 0) continue;
                    if (f.Number == 1) pressed |= ClickButtons.Plus;
                    if (f.Number == 2) pressed |= ClickButtons.Minus;
                }
                return new(ClickMessageKind.Buttons, pressed);
            }
            case TypeRideButtons:
            {
                // RideKeyPadStatus: field 1 = button bitmap, a CLEARED bit means pressed.
                foreach (var f in ProtoReader.Read(data[1..].ToArray()))
                {
                    if (f.Number == 1 && f.WireType == 0)
                        return new(ClickMessageKind.Buttons, (ClickButtons)(~(uint)f.Varint & KnownMask));
                }
                return new(ClickMessageKind.Other);
            }
            case TypeVendor when data.Length >= 2 && data[1] == 0x03:
                return new(ClickMessageKind.Challenge);
            case TypeVendor when data.Length >= 6 && data[1] == 0x05 && data[2] == 0x00 && data[4] == 0x05:
                return new(ClickMessageKind.LockStatus, Locked: IsLocked(data));
            default:
                return new(ClickMessageKind.Other);
        }
    }

    /// <summary>
    /// Left-puck status "FF 05 00 EA|FA 05 [len] {1: partner address, 2: mode}": mode 1 = locked (no button frames),
    /// and the FA variant is treated as stopped too.
    /// </summary>
    private static bool IsLocked(ReadOnlySpan<byte> data)
    {
        if (data[3] == 0xFA) return true;
        foreach (var f in ProtoReader.Read(data[6..].ToArray()))
        {
            if (f.Number == 2 && f.WireType == 0) return f.Varint == 1;
        }
        return false;
    }
}
