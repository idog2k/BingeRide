using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BingeRide.App.Services.Remote;

/// <summary>
/// The phone web remote, served on the home network. Everything except the static assets and the
/// /pair endpoint requires the paired phone's key (an HttpOnly, SameSite=Strict cookie); commands also
/// require the X-BingeRide header, which other websites can't send without a CORS preflight we never answer.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    // The app's old name, kept so phones paired before the rename stay paired.
    public const string CookieName = "mywahoo_key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(500);

    private WebApplication? _app;

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }

    /// <summary>Starts listening on all interfaces; <paramref name="port"/> 0 picks a free port (tests).</summary>
    public async Task StartAsync(int port, RemotePairing pairing, IRemoteBackend backend)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, port));
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers.XFrameOptions = "DENY";
            await next();
        });

        bool Paired(HttpContext ctx) => pairing.Authenticate(ctx.Request.Cookies[CookieName]) is not null;

        app.MapGet("/pair", (HttpContext ctx) =>
        {
            var key = pairing.Redeem(ctx.Request.Query["c"].ToString(), ctx.Request.Headers.UserAgent.ToString());
            if (key is null)
                return Results.Content(NotPairedPage("This pairing code is wrong, already used or expired. On the PC open BingeRide → Devices → Pair a phone, and scan the new QR code."), "text/html; charset=utf-8", statusCode: 403);
            ctx.Response.Cookies.Append(CookieName, key, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromDays(400),
                IsEssential = true,
            });
            return Results.Redirect("/");
        });

        app.MapGet("/", (HttpContext ctx) => !Paired(ctx)
            ? Results.Content(NotPairedPage("This device isn't paired with BingeRide yet. On the PC open BingeRide → Devices → Pair a phone, and scan the QR code with this phone's camera."), "text/html; charset=utf-8", statusCode: 401)
            : Asset("index.html"));

        foreach (var file in new[] { "nosleep.min.js", "manifest.webmanifest", "apple-touch-icon.png", "icon-192.png", "icon-512.png" })
            app.MapGet("/" + file, () => Asset(file));

        app.MapGet("/api/state", async (HttpContext ctx) =>
            !Paired(ctx) ? Results.Unauthorized() : Results.Json(await backend.GetStateAsync(), Json));
        app.MapGet("/api/workouts", async (HttpContext ctx) =>
            !Paired(ctx) ? Results.Unauthorized() : Results.Json(await backend.GetWorkoutsAsync(), Json));
        app.MapGet("/api/rides", async (HttpContext ctx) =>
            !Paired(ctx) ? Results.Unauthorized() : Results.Json(await backend.GetRidesAsync(), Json));

        app.MapPost("/api/command", async (HttpContext ctx) =>
        {
            if (!Paired(ctx)) return Results.Unauthorized();
            if (ctx.Request.Headers["X-BingeRide"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
            RemoteCommand? command;
            try
            {
                command = await ctx.Request.ReadFromJsonAsync<RemoteCommand>(Json, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (command is null || !await backend.ExecuteAsync(command)) return Results.BadRequest();
            return Results.Json(await backend.GetStateAsync(), Json);
        });

        // Server-sent events: the page's live feed. Stops as soon as the phone is forgotten.
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            var key = ctx.Request.Cookies[CookieName];
            if (pairing.Authenticate(key) is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            var ct = ctx.RequestAborted;
            try
            {
                while (!ct.IsCancellationRequested && pairing.Authenticate(key) is not null)
                {
                    var json = JsonSerializer.Serialize(await backend.GetStateAsync(), Json);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.Delay(PushInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Phone went to sleep or navigated away.
            }
        });

        await app.StartAsync();
        _app = app;
        var address = app.Services.GetService(typeof(IServer)) is IServer server
            ? server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            : null;
        Port = address is not null && Uri.TryCreate(address.Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var uri) ? uri.Port : port;
    }

    public async Task StopAsync()
    {
        if (_app is not { } app) return;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>IPv4 addresses a phone on the same network can reach (adapters with a default gateway first).</summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        var found = new List<(string Ip, bool Gateway)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                var ip = a.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                found.Add((ip, hasGateway));
            }
        }
        return found.OrderByDescending(f => f.Gateway).Select(f => f.Ip).Distinct().ToList();
    }

    private static IResult Asset(string name)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
        if (stream is null) return Results.NotFound();
        var type = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".png" => "image/png",
            ".webmanifest" => "application/manifest+json",
            _ => "application/octet-stream",
        };
        return Results.Stream(stream, type);
    }

    private static string NotPairedPage(string message) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1"><title>BingeRide: not paired</title>
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0B0D12;color:#E8ECF4;
        font:17px -apple-system,system-ui,sans-serif;padding:24px;box-sizing:border-box;text-align:center}
        h1{font-size:22px} p{color:#B8C0D0;line-height:1.5;max-width:420px}</style></head>
        <body><div><h1>📱 Not paired</h1><p>{{WebUtility.HtmlEncode(message)}}</p></div></body></html>
        """;
}
