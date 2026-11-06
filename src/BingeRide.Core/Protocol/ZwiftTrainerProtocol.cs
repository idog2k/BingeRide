using System.Text;

namespace BingeRide.Core.Protocol;

/// <summary>Live data a Zwift-protocol trainer pushes on the async characteristic (message 0x03).</summary>
public readonly record struct HubRidingData(int PowerW, int CadenceRpm, double SpeedX100, int HeartRate);

/// <summary>
/// The trainer side of Zwift's "Hub" protocol (KICKR CORE / CORE 2 virtual shifting):
/// every frame is [type byte][protobuf]. See docs/protocol-notes.md for sources.
/// </summary>
public static class ZwiftTrainerProtocol
{
    public const byte TypeRequest = 0x00;
    public const byte TypeRidingData = 0x03;
    public const byte TypeCommand = 0x04;
    public const byte TypeLog = 0x2A;
    public const byte TypeResponse = 0x3C;
    public const byte TypeLostControl = 0xFE;

    /// <summary>Zwift's crr and CWa defaults, scaled as the trainer expects.</summary>
    public const double DefaultCrr = 0.004;
    public const double DefaultCwa = 0.51;

    /// <summary>"RideOn" + 02 01, as Zwift/qz/Incyclist send to trainers.</summary>
    public static readonly byte[] RideOn = [.. "RideOn"u8, 0x02, 0x01];

    /// <summary>Unknown-purpose init message every working client sends right after RideOn.</summary>
    public static readonly byte[] Init41 = [0x41, 0x08, 0x05];

    /// <summary>Ask for the current gear ratio (DataId 520); answered with a 0x3C response.</summary>
    public static readonly byte[] RequestGearRatio = Request(520);

    /// <summary>Ask for all current settings (DataId 512).</summary>
    public static readonly byte[] RequestSettings = Request(512);

    public static byte[] Request(uint dataId) => Frame(TypeRequest, new ProtoWriter().UInt(1, dataId));

    public static bool IsRideOnReply(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && data[..6].SequenceEqual("RideOn"u8);

    /// <summary>ERG: HubCommand.PowerTarget (field 3).</summary>
    public static byte[] SetPower(int watts) =>
        Frame(TypeCommand, new ProtoWriter().UInt(3, (ulong)Math.Max(0, watts)));

    /// <summary>Full SIM parameters: HubCommand.Simulation (field 4) with wind, incline, CWa and Crr.</summary>
    public static byte[] SetSimulation(double gradePct, double windMps = 0, double cwa = DefaultCwa, double crr = DefaultCrr) =>
        Frame(TypeCommand, new ProtoWriter().Message(4, new ProtoWriter()
            .SInt(1, (long)Math.Round(windMps * 100))
            .SInt(2, (long)Math.Round(gradePct * 100))
            .UInt(3, (ulong)Math.Round(cwa * 10000))
            .UInt(4, (ulong)Math.Round(crr * 100000))));

    /// <summary>Incline-only SIM update (what Zwift sends while riding).</summary>
    public static byte[] SetIncline(double gradePct) =>
        Frame(TypeCommand, new ProtoWriter().Message(4, new ProtoWriter().SInt(2, (long)Math.Round(gradePct * 100))));

    /// <summary>
    /// HubCommand.Physical (field 5): the absolute virtual gear ratio (chainring/cog, ×10000) and optionally weights.
    /// Ratio 0 releases virtual shifting.
    /// </summary>
    public static byte[] SetPhysical(double gearRatio, double? bikeKg = null, double? riderKg = null)
    {
        var p = new ProtoWriter().UInt(2, (ulong)Math.Round(Math.Max(0, gearRatio) * 10000));
        if (bikeKg is { } b) p.UInt(4, (ulong)Math.Round(b * 100));
        if (riderKg is { } r) p.UInt(5, (ulong)Math.Round(r * 100));
        return Frame(TypeCommand, new ProtoWriter().Message(5, p));
    }

    public static bool TryParseRidingData(ReadOnlySpan<byte> data, out HubRidingData result)
    {
        result = default;
        if (data.Length < 1 || data[0] != TypeRidingData) return false;
        int power = 0, cadence = 0, hr = 0;
        double speed = 0;
        foreach (var f in ProtoReader.Read(data[1..].ToArray()))
        {
            if (f.WireType != 0) continue;
            switch (f.Number)
            {
                case 1: power = (int)f.Varint; break;
                case 2: cadence = (int)f.Varint; break;
                case 3: speed = f.Varint; break;
                case 4: hr = (int)f.Varint; break;
            }
        }
        result = new HubRidingData(power, cadence, speed, hr);
        return true;
    }

    /// <summary>Gear-ratio readback: 0x3C {1: 520, 2: {1: {8: ratio×10000}}}.</summary>
    public static bool TryParseGearRatio(ReadOnlySpan<byte> data, out double ratio)
    {
        ratio = 0;
        if (data.Length < 2 || data[0] != TypeResponse) return false;
        var fields = ProtoReader.Read(data[1..].ToArray()).ToList();
        var type = fields.FirstOrDefault(f => f.Number == 1 && f.WireType == 0);
        var payload = fields.FirstOrDefault(f => f.Number == 2 && f.WireType == 2);
        if (type.Number == 0 || payload.Number == 0 || type.Varint is < 512 or > 525) return false;
        foreach (var settings in ProtoReader.Read(payload.Bytes).Where(f => f.Number == 1 && f.WireType == 2))
        foreach (var f in ProtoReader.Read(settings.Bytes))
        {
            if (f.Number == 8 && f.WireType == 0)
            {
                ratio = f.Varint / 10000.0;
                return true;
            }
        }
        return false;
    }

    /// <summary>Text of a 0x2A log frame ({1: n, 2: {4: text}}), or null.</summary>
    public static string? TryParseLog(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != TypeLog) return null;
        foreach (var outer in ProtoReader.Read(data[1..].ToArray()).Where(f => f.Number == 2 && f.WireType == 2))
        foreach (var f in ProtoReader.Read(outer.Bytes).Where(f => f.Number == 4 && f.WireType == 2))
            return Encoding.UTF8.GetString(f.Bytes.Span).TrimEnd('\0');
        return null;
    }

    private static byte[] Frame(byte type, ProtoWriter body) => [type, .. body.ToArray()];
}
