# Third-party notices

BingeRide's own code is under the MIT License (see `LICENSE`). The Windows build also contains the
components below, each under its own license.

| Component | Used for | License | Copyright |
|---|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | User interface | MIT | © 2013-2026 The AvaloniaUI Project |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View models | MIT | © .NET Foundation and Contributors |
| [QRCoder](https://github.com/codebude/QRCoder) | Phone pairing QR code | MIT | © 2013-2025 Raffael Herrmann, © 2024-2025 Shane Krueger |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | Rendering (via Avalonia); icon generator | MIT | © Microsoft Corporation |
| [.NET runtime](https://github.com/dotnet/runtime) and [ASP.NET Core](https://github.com/dotnet/aspnetcore) | Runtime; phone web server | MIT | © .NET Foundation and Contributors |
| [NoSleep.js](https://github.com/richtr/NoSleep.js) v0.12.0, vendored in `src/BingeRide.App/Web/nosleep.min.js` | Keeps the phone screen on | MIT | © Rich Tibbett |
| Microsoft Windows SDK .NET projection | Bluetooth LE (WinRT) | [Microsoft Windows SDK license](https://aka.ms/WinSDKLicenseURL) | © Microsoft Corporation |
| [Garmin FIT SDK](https://developer.garmin.com/fit/) (`Garmin.FIT.Sdk` NuGet package) | Reading .fit workouts, writing .fit rides | **FIT Protocol License Agreement**, not MIT: see the `LICENSE.txt` in the NuGet package | © Garmin International, Inc. |

Test-only packages (xUnit, Avalonia.Headless, Microsoft.NET.Test.Sdk) are not shipped.

## Protocols and trademarks

- **Protocols:** the Zwift trainer and Zwift Click protocols are implemented from publicly documented,
  community reverse-engineering notes, listed in `docs/protocol-notes.md`. No third-party source code
  was copied.
- **Trademarks:** Wahoo, KICKR, Zwift, Zwift Click, Zwift Cog, Garmin, Forerunner, Netflix, Strava and
  iPhone are trademarks of their respective owners. They are named only to describe compatibility.
- **No affiliation:** this project is not affiliated with, endorsed by, or sponsored by any of those
  companies.
