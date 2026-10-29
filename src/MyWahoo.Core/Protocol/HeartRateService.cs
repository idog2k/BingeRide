using System.Buffers.Binary;

namespace MyWahoo.Core.Protocol;

/// <summary>Bluetooth SIG Heart Rate Service: chest straps and watches that broadcast HR (e.g. Garmin "Broadcast Heart Rate").</summary>
public static class HeartRateService
{
    public static readonly Guid Service = BleUuid.From16(0x180D);
    public static readonly Guid Measurement = BleUuid.From16(0x2A37);

    /// <summary>
    /// Parses a Heart Rate Measurement: flags bit 0 selects uint8/uint16 bpm; bits 1–2 = 0b10 means the sensor
    /// supports contact detection but has no skin contact (reading is meaningless).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out int bpm)
    {
        bpm = 0;
        if (data.Length < 2) return false;
        var flags = data[0];
        if ((flags & 0b110) == 0b100) return false;
        if ((flags & 1) != 0)
        {
            if (data.Length < 3) return false;
            bpm = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        }
        else
        {
            bpm = data[1];
        }
        return bpm is > 0 and < 255;
    }
}
