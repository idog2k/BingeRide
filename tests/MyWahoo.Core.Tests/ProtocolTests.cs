using MyWahoo.Core.Protocol;
using Xunit;

namespace MyWahoo.Core.Tests;

/// <summary>Expected bytes come from Zwift/qz/Makinolo captures documented in docs/protocol-notes.md.</summary>
public class ZwiftTrainerProtocolTests
{
    [Fact]
    public void Gear_12_matches_capture() =>
        Assert.Equal("042A0410C0BB01", Hex.Of(ZwiftTrainerProtocol.SetPhysical(2.40)));

    [Fact]
    public void Gear_with_weights_matches_zwift_capture() =>
        Assert.Equal("042A0A10C0BB0120BF0628B442", Hex.Of(ZwiftTrainerProtocol.SetPhysical(2.40, 8.31, 85.0)));

    [Fact]
    public void Full_simulation_matches_capture() =>
        Assert.Equal("04220B080010DA0218EC27209003", Hex.Of(ZwiftTrainerProtocol.SetSimulation(1.73)));

    [Theory]
    [InlineData(-0.85, "042203 10A901")]
    [InlineData(0.0, "042202 1000")]
    [InlineData(5.0, "042203 10E807")]
    public void Incline_only(double grade, string expected) =>
        Assert.Equal(expected.Replace(" ", ""), Hex.Of(ZwiftTrainerProtocol.SetIncline(grade)));

    [Theory]
    [InlineData(200, "0418C801")]
    [InlineData(150, "04189601")]
    public void Erg_power(int watts, string expected) =>
        Assert.Equal(expected, Hex.Of(ZwiftTrainerProtocol.SetPower(watts)));

    [Fact]
    public void Gear_table_frames_match_zwift_for_every_gear()
    {
        // Frames from Zwift traffic (qz handleZwiftGear / SHIFTR), rounded ratios.
        string[] expected =
        [
            "042A0310CC3A", "042A0310FC43", "042A0310AC4D", "042A0310DC56", "042A03108C60", "042A0310E86B",
            "042A0310C477", "042A0410A08301", "042A0410A89101", "042A0410B09F01", "042A0410B8AD01", "042A0410C0BB01",
            "042A0410F4CB01", "042A0410A8DC01", "042A0410DCEC01", "042A041090FD01", "042A0410D49002", "042A041098A402",
            "042A0410DCB702", "042A0410A0CB02", "042A0410D8E202", "042A041090FA02", "042A0410C89103", "042A0410F4AC03",
        ];
        for (var gear = 1; gear <= 24; gear++)
            Assert.Equal(expected[gear - 1], Hex.Of(ZwiftTrainerProtocol.SetPhysical(GearTable.Ratio(gear))));
    }

    [Fact]
    public void RideOn_and_requests()
    {
        Assert.Equal("526964654F6E0201", Hex.Of(ZwiftTrainerProtocol.RideOn));
        Assert.Equal("00088804", Hex.Of(ZwiftTrainerProtocol.RequestGearRatio));
        Assert.Equal("00088004", Hex.Of(ZwiftTrainerProtocol.RequestSettings));
        Assert.True(ZwiftTrainerProtocol.IsRideOnReply(Hex.Parse("5269 6465 4F6E 0200")));
        Assert.False(ZwiftTrainerProtocol.IsRideOnReply(Hex.Parse("2A08")));
    }

    [Fact]
    public void Parses_riding_data_capture()
    {
        Assert.True(ZwiftTrainerProtocol.TryParseRidingData(Hex.Parse("03 08 BE 01 10 50 18 BD 06 20 00 28 E2 BA 01 30 8B EB 01"), out var d));
        Assert.Equal(190, d.PowerW);
        Assert.Equal(80, d.CadenceRpm);
        Assert.Equal(829, d.SpeedX100);
        Assert.Equal(0, d.HeartRate);
    }

    [Fact]
    public void Parses_gear_readback()
    {
        Assert.True(ZwiftTrainerProtocol.TryParseGearRatio(Hex.Parse("3C 08 88 04 12 06 0A 04 40 C0 BB 01"), out var ratio));
        Assert.Equal(2.4, ratio, 4);
    }

    [Fact]
    public void Parses_log_frame()
    {
        var frame = Hex.Parse("2A 08 03 12 11 22 0F").Concat("ATX 08, STX 00\0"u8.ToArray()).ToArray();
        Assert.Equal("ATX 08, STX 00", ZwiftTrainerProtocol.TryParseLog(frame));
    }
}

public class FtmsTests
{
    [Fact]
    public void Target_power() => Assert.Equal("05C800", Hex.Of(Ftms.SetTargetPower(200)));

    [Fact]
    public void Simulation_5_percent_matches_trainerhud() =>
        Assert.Equal("110000F4012833", Hex.Of(Ftms.SetSimulation(5.0, 0.004, 0.51)));

    [Fact]
    public void Response_parsing()
    {
        Assert.True(Ftms.TryParseResponse([0x80, 0x00, 0x01], out var op, out var result));
        Assert.Equal(0, op);
        Assert.Equal(1, result);
    }

    [Fact]
    public void Indoor_bike_data_with_speed_cadence_power()
    {
        // flags 0x0044: speed present (bit0 clear), cadence (bit2), power (bit6).
        Assert.True(Ftms.TryParseIndoorBikeData(Hex.Parse("4400 E80B A000 C800"), out var d));
        Assert.Equal(30.48, d.SpeedKmh!.Value, 2);
        Assert.Equal(80, d.CadenceRpm);
        Assert.Equal(200, d.PowerW);
        Assert.Null(d.HeartRate);
    }

    [Fact]
    public void Gear_scaling_is_physically_consistent()
    {
        var (grade, crr, cw) = Ftms.ScaleForVirtualGear(4, 0.004, 0.51, 1.5);
        Assert.Equal(6, grade, 6);
        Assert.Equal(0.006, crr, 6);
        Assert.Equal(0.51 * 3.375, cw, 6);
        Assert.Equal(Ftms.MaxCw, Ftms.ScaleForVirtualGear(0, 0.004, 0.51, 2.0).Cw);
    }
}

public class ZwiftClickProtocolTests
{
    [Theory]
    [InlineData("23 08 FF FF FF FF 0F", ClickButtons.None)]
    [InlineData("23 08 FF DF FF FF 0F", ClickButtons.Plus)]
    [InlineData("23 08 FF FD FF FF 0F", ClickButtons.Minus)]
    [InlineData("23 08 FF DD FF FF 0F", ClickButtons.Plus | ClickButtons.Minus)]
    [InlineData("23 08 DF FF FF FF 0F", ClickButtons.B)]
    [InlineData("37 08 01 10 01", ClickButtons.None)]
    [InlineData("37 08 00 10 01", ClickButtons.Plus)]
    [InlineData("37 08 01 10 00", ClickButtons.Minus)]
    public void Button_frames(string hex, ClickButtons expected)
    {
        var m = ZwiftClickProtocol.Parse(Hex.Parse(hex));
        Assert.Equal(ClickMessageKind.Buttons, m.Kind);
        Assert.Equal(expected, m.Pressed);
    }

    [Fact]
    public void Lock_status_capture_is_locked()
    {
        var m = ZwiftClickProtocol.Parse(Hex.Parse("ff 05 00 ea 05 19 0a 0c 35 38 44 31 35 41 42 42 34 33 36 33 10 01 18 84 07 20 08 28 09 30 19"));
        Assert.Equal(ClickMessageKind.LockStatus, m.Kind);
        Assert.True(m.Locked);
    }

    [Fact]
    public void Lock_status_mode_zero_is_unlocked()
    {
        var m = ZwiftClickProtocol.Parse(Hex.Parse("ff 05 00 ea 05 04 10 00 18 01"));
        Assert.False(m.Locked);
    }

    [Fact]
    public void Challenge_is_detected() =>
        Assert.Equal(ClickMessageKind.Challenge, ZwiftClickProtocol.Parse(Hex.Parse("FF 03 00 0A 21 00")).Kind);

    [Fact]
    public void Identifies_devices_from_manufacturer_data()
    {
        Assert.Equal(ZwiftControllerKind.ClickV1, ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]> { [0x094A] = [0x09, 0x12, 0x34] }, null));
        Assert.Equal(ZwiftControllerKind.ClickV2Right, ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]> { [0x094A] = [0x0A] }, "Zwift Click"));
        Assert.Equal(ZwiftControllerKind.ClickV2Left, ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]> { [0x094A] = [0x0B] }, null));
        Assert.Null(ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]> { [0x094A] = [0x01] }, "Zwift Hub"));
        Assert.Equal(ZwiftControllerKind.Unknown, ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]>(), "Zwift Click"));
        Assert.Null(ZwiftClickProtocol.Identify(new Dictionary<ushort, byte[]>(), "KICKR CORE CD5E"));
    }
}
