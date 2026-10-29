# MyWahoo

A small Windows app for riding a **Wahoo KICKR CORE 2 with Zwift Cog** while watching Netflix.

**Modes**
- **ERG:** the trainer holds the target watts.
- **Slope:** a simulated climb, with virtual gears.
- **Free:** a flat road where you just shift.
- **Workout:** structured sessions that mix all three.

**Workout import**
- Files: `.json` (own format), `.zwo`, `.erg`, `.mrc`, `.fit`, intervals.icu text, and `.gpx`/`.tcx` routes.
- Ask Claude for one and it lands in the app.

**Control**
- A **Zwift Click** shifts gears, or nudges watts in ERG.
- A compact **always-on-top, see-through overlay** sits over the video.

Built with Avalonia 12 on .NET 10, using Bluetooth LE through WinRT. No Zwift account or app is needed.

## Run it

`%LOCALAPPDATA%\Programs\MyWahoo\MyWahoo.exe` is a single file with no installer. To rebuild it from WSL, run `scripts/publish-windows.sh`.

| Option | What it does |
|---|---|
| `--overlay` | Start in the compact overlay. |
| `--no-bluetooth` | UI only; use *Devices → Demo trainer* to try it out. |

**First ride checklist**
1. Update the KICKR CORE 2 firmware in the Wahoo app. Virtual shifting needs 2.2.44 / 3.2.44 or newer.
2. **Close Zwift, the Wahoo app and anything else using the trainer or the Click.** Each device obeys one app at a time.
3. Start MyWahoo and pedal so the KICKR wakes up.
   - It connects automatically. The header shows `KICKR CORE … · virtual shifting`, or `· FTMS` if the trainer refused Zwift's protocol.
   - Press any Click button so it advertises.
4. In **Free**, shift through a few gears and check that the resistance changes. Then try **ERG** at 120 W.
5. If something misbehaves, the Bluetooth trace is in `%LOCALAPPDATA%\MyWahoo\logs\ble-*.log`, with every byte sent and received. *Settings → Trainer (advanced)* can force FTMS or send ERG over FTMS.

## Riding

| Control | Action |
|---|---|
| Click `+` / `−` (and **B** = down) | Gears in Slope/Free and in slope/free workout steps; watts in ERG; intensity ±2 % in ERG workout steps |
| Keyboard `+`/`−`/↑/↓, Space | Same, plus pause/resume a workout |
| **Overlay** button / `Ctrl+Alt+K` | Switch between the full window and the compact overlay |
| `Ctrl+Alt+L` | Mouse click-through on the overlay (so you can click Netflix underneath) |
| Overlay `−` / ☀ | Background more / less transparent; drag anywhere to move |

**Zwift Click v2.** The "−" puck stops sending presses about a minute after connecting, unless the Zwift app has unlocked it in the last ~24 h. This is Zwift's lock, and no third-party app can undo it. That is why **B** on the "+" puck also shifts down. *Settings* has an option to keep rebooting the "−" puck instead, which leaves 2–3 s gaps.

Workouts pause automatically when you stop pedalling. Button mappings, FTP and steps are in *Settings*, saved to `%APPDATA%\MyWahoo\settings.json`.

## Workouts from Claude

The app watches `Documents\MyWahoo\Workouts`. A file that appears there shows up as **"New workout: … Start now"**.

**Claude Code (this machine):**
- Run `scripts/install-skill.sh` once.
- Then just ask, e.g. *"give me a 60 minute VO2 workout, my FTP is 140"*. Claude writes the JSON, checks it with `skills/mywahoo-workout/scripts/check_workout.py`, and delivers it into the folder.

**claude.ai:**
- Upload `dist/mywahoo-workout.zip` (built by `scripts/install-skill.sh --zip`) under Settings → Capabilities → Skills.
- Or paste the contents of `skills/mywahoo-workout/SKILL.md` into a Project's instructions.
- Copy Claude's answer and press **Paste** (or Ctrl+V) in the app. It finds the workout inside the text.

**Other files:** use **Import**, drag them onto the window, or drop them into the folder. The format is documented in [`docs/workout-format.md`](docs/workout-format.md).

## Layout

```
src/MyWahoo.Core       protocols (Zwift trainer, FTMS, Zwift Click), devices, ride controller, workout player
src/MyWahoo.Workouts   importers: json, zwo, erg/mrc, intervals.icu, fit, gpx/tcx, paste extraction, stats
src/MyWahoo.App        Avalonia UI: main window, overlay, workout chart (platform-neutral)
src/MyWahoo.Windows    WinRT Bluetooth, click-through/topmost/hotkeys, exe entry point
tests/                 xunit; App.Tests renders the UI headlessly into ./screenshots
skills/                the Claude skill
docs/                  workout format, protocol notes and sources
```

Run `dotnet test` from WSL to run everything. Plain Linux works too; only the Windows head needs `EnableWindowsTargeting`.
