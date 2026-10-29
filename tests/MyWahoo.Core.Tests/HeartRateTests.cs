using MyWahoo.Core.Ble;
using MyWahoo.Core.Devices;
using MyWahoo.Core.Protocol;
using MyWahoo.Core.Session;
using MyWahoo.Core.Settings;
using Xunit;

namespace MyWahoo.Core.Tests;

public class HeartRateServiceTests
{
    [Theory]
    [InlineData("00 48", 72)]            // uint8
    [InlineData("01 A0 00", 160)]        // uint16
    [InlineData("06 8C", 140)]           // contact supported and detected
    [InlineData("16 8A 1C 03", 138)]     // energy/RR fields after the value are ignored
    public void Parses_measurements(string hex, int expected)
    {
        Assert.True(HeartRateService.TryParse(Hex.Parse(hex), out var bpm));
        Assert.Equal(expected, bpm);
    }

    [Theory]
    [InlineData("04 50")] // contact supported but not detected
    [InlineData("00 00")] // zero
    [InlineData("01 48")] // uint16 flag but too short
    [InlineData("00")]
    public void Rejects_invalid(string hex) => Assert.False(HeartRateService.TryParse(Hex.Parse(hex), out _));
}

public class HeartRateMonitorTests
{
    private static FakePeripheral Watch(ulong address = 0x0A0B0C0D0E0F, bool hasService = true)
    {
        var p = new FakePeripheral(address, "Forerunner 970");
        if (hasService) p.Add(HeartRateService.Service, HeartRateService.Measurement, GattProperties.Notify);
        return p;
    }

    private static BleAdvertisement HrAdvert(ulong address) =>
        new(address, "Forerunner 970", -55, [HeartRateService.Service], new Dictionary<ushort, byte[]>(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Monitor_reports_readings()
    {
        var p = Watch();
        var m = await HeartRateMonitor.ConnectAsync(new FakeAdapter(_ => p), p.Address);
        var got = new List<int>();
        m.HeartRateReceived += (_, bpm) => got.Add(bpm);
        p.Get(HeartRateService.Measurement).Push(Hex.Parse("00 7E"));
        p.Get(HeartRateService.Measurement).Push(Hex.Parse("04 7E")); // contact lost: ignored
        Assert.Equal([126], got);
        Assert.Equal(126, m.LastBpm);
    }

    [Fact]
    public void Advert_with_hr_service_is_a_heart_rate_sensor_but_kickr_stays_a_trainer()
    {
        Assert.Equal(DeviceRole.HeartRate, DeviceManager.Classify(HrAdvert(1), out _));
        var kickr = new BleAdvertisement(2, "KICKR CORE CD5E", -60, [Ftms.Service, HeartRateService.Service], new Dictionary<ushort, byte[]>(), DateTimeOffset.UtcNow);
        Assert.Equal(DeviceRole.Trainer, DeviceManager.Classify(kickr, out _));
    }

    [Fact]
    public async Task Manager_auto_connects_and_forwards_heart_rate()
    {
        var watch = Watch();
        var adapter = new FakeAdapter(_ => watch);
        await using var manager = new DeviceManager(adapter, () => new TrainerOptions(), () => new ClickOptions());
        var readings = new List<int>();
        manager.HeartRateReceived += (_, bpm) => readings.Add(bpm);
        manager.Start();

        adapter.Advertise(HrAdvert(watch.Address));
        await WaitFor(() => manager.HeartRate is not null);
        Assert.Equal(watch.Address, manager.PreferredHeartRate);

        watch.Get(HeartRateService.Measurement).Push(Hex.Parse("00 8F"));
        Assert.Equal([143], readings);
        Assert.Contains(manager.Discovered, d => d.Role == DeviceRole.HeartRate && d.CanConnect);
    }

    [Fact]
    public async Task Failing_sensor_is_backed_off_instead_of_hammered()
    {
        var watch = Watch(hasService: false); // e.g. a watch advertising but not broadcasting HR
        var adapter = new FakeAdapter(_ => watch);
        await using var manager = new DeviceManager(adapter, () => new TrainerOptions(), () => new ClickOptions());
        manager.Start();
        adapter.Advertise(HrAdvert(watch.Address));
        await WaitFor(() => adapter.ConnectCalls.Count == 1);
        await Task.Delay(50);
        adapter.Advertise(HrAdvert(watch.Address));
        adapter.Advertise(HrAdvert(watch.Address));
        await Task.Delay(50);
        Assert.Single(adapter.ConnectCalls);
        Assert.Null(manager.HeartRate);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

public class RideHeartRateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sensor_wins_over_trainer_and_trainer_is_the_fallback()
    {
        var c = new RideController(new AppSettings());
        c.OnTrainerData(new TrainerData(150, 85, 120, T0));
        c.OnHeartRate(140, T0);
        c.Tick(T0);
        Assert.Equal(140, c.HeartRate);

        // Sensor goes quiet: after 3 s the trainer's relayed HR takes over.
        c.OnTrainerData(new TrainerData(150, 85, 121, T0.AddSeconds(5)));
        c.Tick(T0.AddSeconds(5));
        Assert.Equal(121, c.HeartRate);
    }

    [Fact]
    public void Average_and_max_while_riding()
    {
        var c = new RideController(new AppSettings());
        c.StartRide(T0);
        for (var i = 0; i <= 120; i++)
        {
            var now = T0.AddSeconds(i * 0.5);
            c.OnTrainerData(new TrainerData(150, 85, null, now));
            c.OnHeartRate(i < 60 ? 130 : 150, now);
            c.Tick(now);
        }
        Assert.InRange(c.AverageHeartRate!.Value, 139, 141);
        Assert.Equal(150, c.MaxHeartRate);
    }

    [Fact]
    public void No_heart_rate_means_no_stats()
    {
        var c = new RideController(new AppSettings());
        c.OnTrainerData(new TrainerData(150, 85, null, T0));
        c.Tick(T0);
        c.Tick(T0.AddSeconds(1));
        Assert.Null(c.HeartRate);
        Assert.Null(c.AverageHeartRate);
    }
}
