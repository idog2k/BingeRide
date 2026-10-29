using System.Buffers.Binary;

namespace MyWahoo.Core.Protocol;

public readonly record struct IndoorBikeData(double? SpeedKmh, double? CadenceRpm, int? PowerW, int? HeartRate);

/// <summary>Bluetooth SIG Fitness Machine Service (FTMS): the standard fallback control plane.</summary>
public static class Ftms
{
    public static readonly Guid Service = BleUuid.From16(0x1826);
    public static readonly Guid IndoorBikeDataChar = BleUuid.From16(0x2AD2);
    public static readonly Guid ControlPointChar = BleUuid.From16(0x2AD9);
    public static readonly Guid MachineStatusChar = BleUuid.From16(0x2ADA);

    public const byte OpRequestControl = 0x00;
    public const byte OpSetTargetPower = 0x05;
    public const byte OpStartResume = 0x07;
    public const byte OpSetSimulation = 0x11;
    public const byte OpResponse = 0x80;

    /// <summary>Machine Status op code: control permission lost (another app took over).</summary>
    public const byte StatusControlLost = 0xFF;

    /// <summary>FTMS uint8 limits for Crr (0.0001 resolution) and Cw (0.01 kg/m resolution).</summary>
    public const double MaxCrr = 0.0255;
    public const double MaxCw = 2.55;

    public static byte[] RequestControl() => [OpRequestControl];
    public static byte[] StartResume() => [OpStartResume];

    public static byte[] SetTargetPower(int watts)
    {
        var b = new byte[3];
        b[0] = OpSetTargetPower;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(watts, 0, short.MaxValue));
        return b;
    }

    /// <summary>Set Indoor Bike Simulation Parameters (wind m/s, grade %, Crr, Cw kg/m).</summary>
    public static byte[] SetSimulation(double gradePct, double crr, double cw, double windMps = 0)
    {
        var b = new byte[7];
        b[0] = OpSetSimulation;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(Math.Round(windMps * 1000), short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), (short)Math.Clamp(Math.Round(gradePct * 100), short.MinValue, short.MaxValue));
        b[5] = (byte)Math.Clamp(Math.Round(crr * 10000), 0, 255);
        b[6] = (byte)Math.Clamp(Math.Round(cw * 100), 0, 255);
        return b;
    }

    /// <summary>Parses a control point indication "80 [request op] [result]"; result 1 = success.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> data, out byte requestOp, out byte result)
    {
        requestOp = result = 0;
        if (data.Length < 3 || data[0] != OpResponse) return false;
        requestOp = data[1];
        result = data[2];
        return true;
    }

    /// <summary>Decodes the Indoor Bike Data characteristic (0x2AD2).</summary>
    public static bool TryParseIndoorBikeData(ReadOnlySpan<byte> d, out IndoorBikeData result)
    {
        result = default;
        if (d.Length < 2) return false;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var p = 2;
        double? speed = null, cadence = null;
        int? power = null, hr = null;

        var length = d.Length;
        bool Has(int bit) => (flags & (1 << bit)) != 0;
        bool Fits(int n) => p + n <= length;

        // Bit 0 is "More Data": instantaneous speed is present when it is CLEAR.
        if (!Has(0)) { if (!Fits(2)) return false; speed = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]) / 100.0; p += 2; }
        if (Has(1)) p += 2; // average speed
        if (Has(2)) { if (!Fits(2)) return false; cadence = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]) / 2.0; p += 2; }
        if (Has(3)) p += 2; // average cadence
        if (Has(4)) p += 3; // total distance
        if (Has(5)) p += 2; // resistance level
        if (Has(6)) { if (!Fits(2)) return false; power = BinaryPrimitives.ReadInt16LittleEndian(d[p..]); p += 2; }
        if (Has(7)) p += 2; // average power
        if (Has(8)) p += 5; // expended energy
        if (Has(9)) { if (!Fits(1)) return false; hr = d[p]; p += 1; }

        result = new IndoorBikeData(speed, cadence, power, hr);
        return true;
    }

    /// <summary>
    /// Virtual shifting emulated over FTMS SIM: with r = virtual/physical gear ratio, sending
    /// grade·r, Crr·r and Cw·r³ makes the trainer resist with r·F_road(r·v), i.e. exactly the road force
    /// felt at the virtual speed in the virtual gear (approach tested on a KICKR CORE by ORQWA).
    /// </summary>
    public static (double Grade, double Crr, double Cw) ScaleForVirtualGear(double gradePct, double crr, double cw, double r) =>
        (gradePct * r, Math.Min(crr * r, MaxCrr), Math.Min(cw * r * r * r, MaxCw));
}
