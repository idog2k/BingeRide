using BingeRide.Core.Devices;
using BingeRide.Core.Session;
using BingeRide.Core.Settings;
using Xunit;

namespace BingeRide.Core.Tests;

public class MotivationSentenceTests
{
    [Fact]
    public void About_a_thousand_sentences_ship_with_the_app() =>
        Assert.InRange(MotivationSentences.All.Count, 950, 1100);

    [Fact]
    public void Every_sentence_fits_two_overlay_lines() =>
        Assert.All(MotivationSentences.All, s => Assert.InRange(s.Length, 5, MotivationSentences.MaxLength));

    [Fact]
    public void No_sentence_appears_twice()
    {
        var duplicates = MotivationSentences.All
            .GroupBy(s => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Deck_shows_every_sentence_before_repeating_any()
    {
        string[] sentences = ["a", "b", "c", "d", "e"];
        var deck = new MotivationDeck(sentences, new Random(1));
        var firstRound = Enumerable.Range(0, 5).Select(_ => deck.Next()).ToList();
        Assert.Equal(sentences, firstRound.Order());
        var secondRound = Enumerable.Range(0, 5).Select(_ => deck.Next()).ToList();
        Assert.Equal(sentences, secondRound.Order());
    }
}

public class MotivationRideTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A controller on a fake clock (4 ticks/s) that notes each new sentence and the ride time it came at.</summary>
    private sealed class Rider
    {
        private int _ticks;

        public Rider(AppSettings? settings = null)
        {
            Controller = new RideController(settings ?? new AppSettings());
            Controller.Tick(T0);
        }

        public RideController Controller { get; }
        public List<(double RideSeconds, string Text)> Shown { get; } = [];

        public void Pedal(int seconds) => Advance(seconds, pedalling: true);
        public void Coast(int seconds) => Advance(seconds, pedalling: false);

        private void Advance(int seconds, bool pedalling)
        {
            for (var i = 0; i < seconds * 4; i++)
            {
                var now = T0.AddSeconds(++_ticks / 4.0);
                if (pedalling) Controller.OnTrainerData(new TrainerData(200, 90, null, now));
                Controller.Tick(now);
                if (Controller.Motivation is { } text && (Shown.Count == 0 || Shown[^1].Text != text))
                    Shown.Add((Controller.RideSeconds, text));
            }
        }
    }

    [Fact]
    public void First_sentence_comes_with_the_first_pedal_stroke_then_one_every_five_minutes()
    {
        var r = new Rider();
        r.Controller.StartRide();
        r.Pedal(16 * 60);

        Assert.Equal(4, r.Shown.Count);
        Assert.InRange(r.Shown[0].RideSeconds, 0, 1);
        Assert.InRange(r.Shown[1].RideSeconds, 300, 301);
        Assert.InRange(r.Shown[2].RideSeconds, 600, 601);
        Assert.InRange(r.Shown[3].RideSeconds, 900, 901);
        Assert.Equal(4, r.Shown.Select(s => s.Text).Distinct().Count());
    }

    [Fact]
    public void A_sentence_stays_for_30_seconds_then_leaves()
    {
        var r = new Rider();
        r.Controller.StartRide();
        r.Pedal(29);
        Assert.NotNull(r.Controller.Motivation);
        r.Pedal(2);
        Assert.Null(r.Controller.Motivation);
    }

    [Fact]
    public void Nothing_shows_before_a_ride_starts_or_while_not_pedalling()
    {
        var idle = new Rider();
        idle.Pedal(10 * 60);
        Assert.Empty(idle.Shown);

        var stopped = new Rider();
        stopped.Controller.StartRide();
        stopped.Coast(10 * 60);
        Assert.Empty(stopped.Shown);
    }

    [Fact]
    public void Paused_time_does_not_count_towards_the_next_sentence()
    {
        var r = new Rider();
        r.Controller.StartRide();
        r.Pedal(60);
        r.Controller.TogglePause();
        r.Pedal(10 * 60);
        Assert.Single(r.Shown);

        r.Controller.TogglePause();
        r.Pedal(230); // 290 s of riding
        Assert.Single(r.Shown);
        r.Pedal(20); // 310 s of riding
        Assert.Equal(2, r.Shown.Count);
    }

    [Fact]
    public void Finishing_clears_the_sentence_and_a_new_ride_starts_fresh()
    {
        var r = new Rider();
        r.Controller.StartRide();
        r.Pedal(10);
        Assert.NotNull(r.Controller.Motivation);

        r.Controller.FinishRide();
        Assert.Null(r.Controller.Motivation);

        r.Controller.StartRide();
        r.Pedal(1);
        Assert.NotNull(r.Controller.Motivation);
        Assert.Equal(2, r.Shown.Count);
    }

    [Fact]
    public void Can_be_switched_off_even_while_a_sentence_is_showing()
    {
        var settings = new AppSettings();
        var r = new Rider(settings);
        r.Controller.StartRide();
        r.Pedal(5);
        Assert.NotNull(r.Controller.Motivation);

        settings.ShowMotivation = false;
        r.Pedal(1);
        Assert.Null(r.Controller.Motivation);
        r.Pedal(10 * 60);
        Assert.Single(r.Shown);
    }

    [Theory]
    [InlineData(1, 180)] // at 0, 60, 120 and 180 s of riding
    [InlineData(0, 180)] // a hand-edited 0 in settings.json means 1, not a new sentence every tick
    public void The_interval_is_a_setting(int minutes, int rideSeconds)
    {
        var r = new Rider(new AppSettings { MotivationMinutes = minutes });
        r.Controller.StartRide();
        r.Pedal(rideSeconds + 1);
        Assert.Equal(4, r.Shown.Count);
    }

    [Fact]
    public void Sentences_do_not_use_the_cue_line()
    {
        var r = new Rider(new AppSettings { ShowFood = false, ShowLandmarks = false });
        var cues = new List<string>();
        r.Controller.CueRaised += (_, text) => cues.Add(text);
        r.Controller.StartRide();
        r.Pedal(10);
        Assert.NotNull(r.Controller.Motivation);
        Assert.Null(r.Controller.Cue);
        Assert.Empty(cues);
    }

    [Fact]
    public void Settings_saved_before_this_feature_turn_it_on_and_keep_their_overlay_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bingeride-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Ftp": 200, "OverlayFields": [ "Power", "Cadence" ] }""");
            var settings = SettingsStore.Load(path);
            Assert.True(settings.ShowMotivation);
            Assert.True(settings.OverlayMotivation);
            Assert.Equal(5, settings.MotivationMinutes);
            Assert.Equal([OverlayField.Power, OverlayField.Cadence], settings.OverlayFields);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
