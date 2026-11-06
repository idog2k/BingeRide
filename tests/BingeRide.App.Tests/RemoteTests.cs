using System.Net;
using System.Net.Http.Json;
using System.Text;
using BingeRide.App.Services.Remote;
using BingeRide.Core.Settings;
using Xunit;

namespace BingeRide.App.Tests;

/// <summary>The phone remote over real HTTP: pairing is the only way in.</summary>
public sealed class RemoteTests : IAsyncDisposable
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeBackend : IRemoteBackend
    {
        public List<RemoteCommand> Commands { get; } = [];

        public Task<RemoteState> GetStateAsync() => Task.FromResult(new RemoteState(
            "187", "190 W", "88", "142", "31.2", "12.4 km", "25:00", "160", "≈ 240 kcal", "🍎🍌", "next 🍪 cookie in 10 kcal",
            "12", "2.40", "0.0%", "Erg", "ERG", "#FF8C00", "Recording", "25:00", "watts", "190 W",
            new RemoteDevice(true, "KICKR"), new RemoteDevice(true, "Click"), new RemoteDevice(false, "No heart rate"),
            null, null, true, true, "15% of ⚓ Tel Aviv → Haifa", "All rides: 312 km", "89% of the way to 🗼 London → Paris", 0.89));

        public Task<IReadOnlyList<RemoteWorkout>> GetWorkoutsAsync() =>
            Task.FromResult<IReadOnlyList<RemoteWorkout>>([new RemoteWorkout("vo2.json", "VO2", "1:00:00", true, null)]);

        public Task<IReadOnlyList<RemoteRide>> GetRidesAsync() => Task.FromResult<IReadOnlyList<RemoteRide>>([]);

        public Task<bool> ExecuteAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(command.Action != "bogus");
        }
    }

    private readonly ManualClock _clock = new();
    private readonly AppSettings _settings = new();
    private readonly FakeBackend _backend = new();
    private readonly RemotePairing _pairing;
    private readonly RemoteServer _server = new();
    private int _saves;

    public RemoteTests()
    {
        _pairing = new RemotePairing(_settings, () => _saves++, _clock);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private async Task<(HttpClient Client, CookieContainer Cookies)> Start()
    {
        await _server.StartAsync(0, _pairing, _backend);
        var cookies = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}"),
        };
        return (client, cookies);
    }

    private static HttpRequestMessage Command(string action, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/command")
        {
            Content = new StringContent($$"""{"action":"{{action}}"}""", Encoding.UTF8, "application/json"),
        };
        if (header) request.Headers.Add("X-BingeRide", "1");
        return request;
    }

    [Fact]
    public async Task Unpaired_devices_get_nothing()
    {
        var (client, _) = await Start();
        var page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Contains("Not paired", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/events", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Command("startRide"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(_backend.Commands);
        // Static assets (icons, manifest) are public: the home-screen icon must load before pairing.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/apple-touch-icon.png", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Pairing_gives_a_strict_httponly_cookie_and_stores_only_a_hash()
    {
        var (client, cookies) = await Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/pair?c=guess", TestContext.Current.CancellationToken)).StatusCode); // no code issued yet

        var code = _pairing.NewCode();
        Assert.True(code.Length >= 20);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/pair?c=wrong", TestContext.Current.CancellationToken)).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/pair?c={code}");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) Safari");
        var paired = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, paired.StatusCode);
        var setCookie = paired.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var phone = Assert.Single(_settings.PairedPhones);
        Assert.Equal("iPhone", phone.Name);
        var key = cookies.GetCookies(client.BaseAddress!)[RemoteServer.CookieName]!.Value;
        Assert.NotEqual(key, phone.KeyHash);
        Assert.Equal(RemotePairing.Hash(key), phone.KeyHash);
        Assert.True(_saves > 0);

        var page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>BingeRide</title>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var state = await client.GetFromJsonAsync<RemoteState>("/api/state", TestContext.Current.CancellationToken);
        Assert.Equal("187", state!.Power);
    }

    [Fact]
    public async Task Codes_work_once_and_expire()
    {
        var (client, _) = await Start();
        var code = _pairing.NewCode();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/pair?c={code}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/pair?c={code}", TestContext.Current.CancellationToken)).StatusCode);

        var late = _pairing.NewCode();
        _clock.Now += RemotePairing.CodeLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/pair?c={late}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Single(_settings.PairedPhones);
    }

    [Fact]
    public async Task Commands_need_the_custom_header_and_the_live_feed_streams()
    {
        var (client, _) = await Start();
        await client.GetAsync($"/pair?c={_pairing.NewCode()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Command("startRide", header: false), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(_backend.Commands);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Command("startRide"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("startRide", Assert.Single(_backend.Commands).Action);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Command("bogus"), TestContext.Current.CancellationToken)).StatusCode);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        using var events = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(cts.Token));
        var line = await reader.ReadLineAsync(cts.Token);
        Assert.StartsWith("data: {", line);
        Assert.Contains("\"power\":\"187\"", line);
    }

    [Fact]
    public async Task Forgotten_phone_is_locked_out()
    {
        var (client, _) = await Start();
        await client.GetAsync($"/pair?c={_pairing.NewCode()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state", TestContext.Current.CancellationToken)).StatusCode);
        _pairing.Forget(_settings.PairedPhones[0].Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state", TestContext.Current.CancellationToken)).StatusCode);
    }
}
