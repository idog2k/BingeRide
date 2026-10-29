using MyWahoo.Core.Ble;
using MyWahoo.Core.Devices;
using MyWahoo.Core.Protocol;
using Xunit;

namespace MyWahoo.Core.Tests;

public class KickrTrainerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
    private static readonly TrainerOptions Opts = new() { ManualTick = true, HandshakeTimeout = TimeSpan.FromMilliseconds(30), RiderKg = 85, BikeKg = 8.31 };

    /// <summary>A KICKR CORE 2 as seen in a real debug log: FTMS + Zwift on FC82.</summary>
    private static FakePeripheral Kickr(bool answersRideOn = true, bool hasZwift = true, byte ftmsResult = 0x01)
    {
        var p = new FakePeripheral();
        p.Add(Ftms.Service, Ftms.IndoorBikeDataChar, GattProperties.Notify);
        var cp = p.Add(Ftms.Service, Ftms.ControlPointChar, GattProperties.Write | GattProperties.Indicate);
        p.Add(Ftms.Service, Ftms.MachineStatusChar, GattProperties.Notify);
        if (hasZwift)
        {
            p.Add(ZwiftUuids.ServiceFc82, ZwiftUuids.Async, GattProperties.Notify);
            p.Add(ZwiftUuids.ServiceFc82, ZwiftUuids.SyncRx, GattProperties.Write | GattProperties.WriteWithoutResponse);
            p.Add(ZwiftUuids.ServiceFc82, ZwiftUuids.SyncTx, GattProperties.Indicate);
        }
        p.Responder = (c, data) =>
        {
            if (c.Uuid == ZwiftUuids.SyncRx && answersRideOn && data.AsSpan().StartsWith("RideOn"u8))
                p.Get(ZwiftUuids.SyncTx).Push([.. "RideOn"u8, 0x02, 0x00]);
            if (c.Uuid == Ftms.ControlPointChar)
                cp.Push([0x80, data[0], ftmsResult]);
        };
        return p;
    }

    private static Task<KickrTrainer> Connect(FakePeripheral p, TrainerOptions? o = null) =>
        KickrTrainer.ConnectAsync(new FakeAdapter(_ => p), p.Address, o ?? Opts);

    [Fact]
    public async Task Zwift_handshake_then_full_sim_and_gear_on_first_sim_target()
    {
        var p = Kickr();
        var t = await Connect(p);
        Assert.Equal(ControlPlane.Zwift, t.ControlPlane);
        Assert.Equal(["526964654F6E0201", "410805"], p.WritesTo(ZwiftUuids.SyncRx).Select(Hex.Of));
        Assert.Empty(p.WritesTo(Ftms.ControlPointChar)); // never fight over FTMS while Zwift controls

        t.SetTarget(new SimTarget(1.73, 12));
        await t.TickAsync(T0);
        var writes = p.WritesTo(ZwiftUuids.SyncRx).Skip(2).Select(Hex.Of).ToList();
        Assert.Equal(["04220B080010DA0218EC27209003", "042A0A10C0BB0120BF0628B442"], writes);

        // Gear readback one second after the shift.
        await t.TickAsync(T0.AddSeconds(1));
        Assert.Equal("00088804", Hex.Of(p.WritesTo(ZwiftUuids.SyncRx).Last()));
        p.Get(ZwiftUuids.SyncTx).Push(Hex.Parse("3C 08 88 04 12 06 0A 04 40 C0 BB 01"));
        Assert.Equal(2.4, t.ReportedGearRatio!.Value, 4);
    }

    [Fact]
    public async Task Shifts_are_sent_immediately_but_grade_is_rate_limited()
    {
        var p = Kickr();
        var t = await Connect(p);
        t.SetTarget(new SimTarget(0, 12));
        await t.TickAsync(T0);
        var before = p.Writes.Count;

        t.SetTarget(new SimTarget(0, 13));
        await t.TickAsync(T0.AddMilliseconds(300));
        Assert.Equal("042A0410F4CB01", Hex.Of(p.Writes[before].Data));

        t.SetTarget(new SimTarget(3, 13));
        await t.TickAsync(T0.AddMilliseconds(600)); // < 1 s since the last grade write
        Assert.Equal(before + 1, p.Writes.Count);
        await t.TickAsync(T0.AddMilliseconds(1100));
        Assert.Equal("04220310D804", Hex.Of(p.Writes.Last().Data));

        // Only the gear readback is due at 3 s; at 6.2 s (> 5 s idle) the incline is re-sent as keep-alive.
        var count = p.Writes.Count;
        await t.TickAsync(T0.AddSeconds(3));
        Assert.Equal(["00088804"], p.Writes.Skip(count).Select(w => Hex.Of(w.Data)));
        await t.TickAsync(T0.AddSeconds(6.2));
        Assert.Equal("04220310D804", Hex.Of(p.Writes.Last().Data));
    }

    [Fact]
    public async Task Erg_on_zwift_channel_then_back_to_sim_resends_full_state()
    {
        var p = Kickr();
        var t = await Connect(p);
        t.SetTarget(new ErgTarget(200));
        await t.TickAsync(T0);
        Assert.Equal("0418C801", Hex.Of(p.Writes.Last().Data));

        t.SetTarget(new ErgTarget(150));
        await t.TickAsync(T0.AddMilliseconds(500));
        Assert.Equal("0418C801", Hex.Of(p.Writes.Last().Data)); // rate limited
        await t.TickAsync(T0.AddSeconds(1));
        Assert.Equal("04189601", Hex.Of(p.Writes.Last().Data));

        t.SetTarget(new SimTarget(0, 12));
        await t.TickAsync(T0.AddSeconds(1.1));
        var last2 = p.Writes.TakeLast(2).Select(w => Hex.Of(w.Data)).ToList();
        Assert.Equal("04220A0800100018EC27209003", last2[0]);
        Assert.StartsWith("042A0A10C0BB01", last2[1]);
    }

    [Fact]
    public async Task Falls_back_to_ftms_when_trainer_ignores_rideon()
    {
        var p = Kickr(answersRideOn: false);
        var t = await Connect(p);
        Assert.Equal(ControlPlane.Ftms, t.ControlPlane);
        Assert.Equal(3, p.WritesTo(ZwiftUuids.SyncRx).Count());
        Assert.Equal(["00", "07"], p.WritesTo(Ftms.ControlPointChar).Select(Hex.Of));

        // Gear 18 (3.74) over a physical 3.0 ratio: r = 1.2467 → grade·r, Crr·r, Cw·r³.
        t.SetTarget(new SimTarget(4, 18));
        await t.TickAsync(T0);
        var expected = Ftms.SetSimulation(4 * 3.74 / 3.0, 0.004 * 3.74 / 3.0, 0.51 * Math.Pow(3.74 / 3.0, 3));
        Assert.Equal(Hex.Of(expected), Hex.Of(p.Writes.Last().Data));

        t.SetTarget(new ErgTarget(180));
        await t.TickAsync(T0.AddSeconds(1));
        Assert.Equal("05B400", Hex.Of(p.Writes.Last().Data));
    }

    [Fact]
    public async Task Trainer_without_zwift_service_uses_ftms()
    {
        var t = await Connect(Kickr(hasZwift: false));
        Assert.Equal(ControlPlane.Ftms, t.ControlPlane);
    }

    [Fact]
    public async Task Ftms_refusal_is_an_error()
    {
        await Assert.ThrowsAsync<BleException>(() => Connect(Kickr(hasZwift: false, ftmsResult: 0x05)));
    }

    [Fact]
    public async Task Reports_riding_data_from_both_channels()
    {
        var p = Kickr();
        var t = await Connect(p);
        var got = new List<TrainerData>();
        t.DataReceived += (_, d) => got.Add(d);
        p.Get(ZwiftUuids.Async).Push(Hex.Parse("03 08 BE 01 10 50 18 BD 06 20 00"));
        p.Get(Ftms.IndoorBikeDataChar).Push(Hex.Parse("4400 E80B A000 C800"));
        Assert.Equal([190, 200], got.Select(d => d.PowerW!.Value));
        Assert.Equal([80.0, 80.0], got.Select(d => d.CadenceRpm!.Value));
    }

    [Fact]
    public async Task Disconnect_is_reported_once()
    {
        var p = Kickr();
        var t = await Connect(p);
        var n = 0;
        t.Disconnected += (_, _) => n++;
        p.Drop();
        p.Drop();
        Assert.Equal(1, n);
        Assert.False(t.IsConnected);
    }
}

public class ZwiftClickDeviceTests
{
    private static readonly ClickOptions Opts = new() { ManualTick = true };

    private static FakePeripheral Click(bool fc82)
    {
        var service = fc82 ? ZwiftUuids.ServiceFc82 : ZwiftUuids.ServiceLegacy;
        var p = new FakePeripheral(0x1234, "Zwift Click");
        p.Add(service, ZwiftUuids.Async, GattProperties.Notify);
        p.Add(service, ZwiftUuids.SyncRx, fc82 ? GattProperties.WriteWithoutResponse : GattProperties.Write | GattProperties.WriteWithoutResponse);
        p.Add(service, ZwiftUuids.SyncTx, GattProperties.Indicate | GattProperties.Read);
        return p;
    }

    private static Task<ZwiftClickDevice> Connect(FakePeripheral p, ZwiftControllerKind kind, ClickOptions? o = null) =>
        ZwiftClickDevice.ConnectAsync(new FakeAdapter(_ => p), p.Address, kind, o ?? Opts);

    [Fact]
    public async Task V1_handshake_and_edge_triggered_presses()
    {
        var p = Click(fc82: false);
        var c = await Connect(p, ZwiftControllerKind.ClickV1);
        Assert.Equal(["526964654F6E"], p.WritesTo(ZwiftUuids.SyncRx).Select(Hex.Of));
        Assert.All(p.Get(ZwiftUuids.SyncRx).WriteModes, Assert.False); // write without response

        var presses = new List<ClickButtons>();
        c.ButtonsPressed += (_, b) => presses.Add(b);
        var async = p.Get(ZwiftUuids.Async);
        async.Push(Hex.Parse("37 08 00 10 01")); // + down
        async.Push(Hex.Parse("37 08 00 10 01")); // repeated while held
        async.Push(Hex.Parse("37 08 01 10 01")); // released
        async.Push(Hex.Parse("37 08 01 10 00")); // − down
        Assert.Equal([ClickButtons.Plus, ClickButtons.Minus], presses);
    }

    [Fact]
    public async Task V2_handshake_sequence()
    {
        var p = Click(fc82: true);
        await Connect(p, ZwiftControllerKind.ClickV2Right);
        Assert.Equal(["526964654F6E0203", "000800", "000810"], p.WritesTo(ZwiftUuids.SyncRx).Select(Hex.Of));
    }

    [Fact]
    public async Task Unknown_kind_on_legacy_service_is_treated_as_v1()
    {
        var p = Click(fc82: false);
        var c = await Connect(p, ZwiftControllerKind.Unknown);
        Assert.Equal(ZwiftControllerKind.ClickV1, c.Kind);
    }

    [Fact]
    public async Task V2_pucks_only_report_their_own_buttons()
    {
        var right = Click(fc82: true);
        var r = await Connect(right, ZwiftControllerKind.ClickV2Right);
        var left = Click(fc82: true);
        var l = await Connect(left, ZwiftControllerKind.ClickV2Left);
        var presses = new List<ClickButtons>();
        r.ButtonsPressed += (_, b) => presses.Add(b);
        l.ButtonsPressed += (_, b) => presses.Add(b);

        // The left puck relays the right puck's "+": only the right connection may report it.
        var plus = Hex.Parse("23 08 FF DF FF FF 0F");
        right.Get(ZwiftUuids.Async).Push(plus);
        left.Get(ZwiftUuids.Async).Push(plus);
        left.Get(ZwiftUuids.Async).Push(Hex.Parse("23 08 FF FD FF FF 0F")); // "−" (and + released)
        Assert.Equal([ClickButtons.Plus, ClickButtons.Minus], presses);
    }

    [Fact]
    public async Task Answers_challenge_and_reports_lock()
    {
        var p = Click(fc82: true);
        var c = await Connect(p, ZwiftControllerKind.ClickV2Left);
        bool? locked = null;
        c.LockChanged += (_, v) => locked = v;
        p.Get(ZwiftUuids.Async).Push(Hex.Parse("FF 03 00 0A 21 01 02"));
        p.Get(ZwiftUuids.Async).Push(Hex.Parse("ff 05 00 ea 05 04 10 01 18 01"));
        await c.TickAsync(DateTimeOffset.UtcNow);
        Assert.Equal("FF0400", Hex.Of(p.WritesTo(ZwiftUuids.SyncRx).Last()));
        Assert.True(locked);
        Assert.True(c.IsLocked);
    }

    [Fact]
    public async Task Idle_right_puck_is_rebooted_after_55_seconds()
    {
        var p = Click(fc82: true);
        var c = await Connect(p, ZwiftControllerKind.ClickV2Right);
        var now = DateTimeOffset.UtcNow;
        await c.TickAsync(now.AddSeconds(4));
        Assert.Equal("526964654F6E0203", Hex.Of(p.WritesTo(ZwiftUuids.SyncRx).Last())); // keep-alive
        await c.TickAsync(now.AddSeconds(56));
        Assert.Equal("18", Hex.Of(p.WritesTo(ZwiftUuids.SyncRx).Last()));
        var count = p.Writes.Count;
        await c.TickAsync(now.AddSeconds(60));
        Assert.Equal(count, p.Writes.Count); // waits for the reconnect, no more writes
    }

    [Fact]
    public async Task Locked_left_puck_reboots_only_when_enabled()
    {
        var p = Click(fc82: true);
        var c = await Connect(p, ZwiftControllerKind.ClickV2Left, Opts with { RebootLockedLeftPuck = true });
        p.Get(ZwiftUuids.Async).Push(Hex.Parse("ff 05 00 ea 05 04 10 01 18 01"));
        await c.TickAsync(DateTimeOffset.UtcNow);
        Assert.Equal("18", Hex.Of(p.WritesTo(ZwiftUuids.SyncRx).Last()));
    }
}

public class DeviceManagerTests
{
    private static BleAdvertisement Adv(ulong address, string? name, Dictionary<ushort, byte[]>? mfr = null, Guid[]? services = null) =>
        new(address, name, -60, services ?? [], mfr ?? [], DateTimeOffset.UtcNow);

    [Fact]
    public void Classifies_trainers_and_clicks()
    {
        Assert.Equal(DeviceRole.Trainer, DeviceManager.Classify(Adv(1, "KICKR CORE CD5E", services: [Ftms.Service]), out _));
        Assert.Equal(DeviceRole.Trainer, DeviceManager.Classify(Adv(1, "Some Trainer", services: [Ftms.Service]), out _));
        Assert.Equal(DeviceRole.Click, DeviceManager.Classify(Adv(2, "Zwift Click", new() { [0x094A] = [0x0A] }), out var kind));
        Assert.Equal(ZwiftControllerKind.ClickV2Right, kind);
        Assert.Null(DeviceManager.Classify(Adv(3, "Galaxy Buds"), out _));
    }
}
