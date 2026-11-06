# BingeRide

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
- **Heart rate** comes from any Bluetooth strap, or a watch broadcasting HR (e.g. a Garmin Forerunner 970). It shows live, with average and max.
- **Food burned 🍕:** ride energy turned into food (kcal ≈ kJ of work). Workouts show 🍎🍌🍪🍕🍔 on the chart where you'll burn each one. Free rides pop up "🍕 Slice of pizza burned!" as you go. Rides list them, the overlay can show them, and *Settings* turns it off.
- **Rides are recorded** in any mode: Start ride, then Finish. Each ride is saved as a `.fit` activity for Strava, Garmin Connect or intervals.icu.

Built with Avalonia 12 on .NET 10, using Bluetooth LE through WinRT. No Zwift account or app is needed.

## Run it

`%LOCALAPPDATA%\Programs\BingeRide\BingeRide.exe` is a single file with no installer. To rebuild it from WSL, run `scripts/publish-windows.sh`.

BingeRide used to be called **MyWahoo**. On its first start it renames the old `MyWahoo` folders in Documents, `%APPDATA%` and `%LOCALAPPDATA%`, so rides, workouts, settings and paired phones carry over. Close the old MyWahoo first.

| Option | What it does |
|---|---|
| `--overlay` | Start in the compact overlay. |
| `--no-bluetooth` | UI only; use *Devices → Demo trainer* to try it out. |

**First ride checklist**
1. Update the KICKR CORE 2 firmware in the Wahoo app. Virtual shifting needs 2.2.44 / 3.2.44 or newer.
2. **Close Zwift, the Wahoo app and anything else using the trainer or the Click.** Each device obeys one app at a time.
3. Start BingeRide and pedal so the KICKR wakes up.
   - It connects automatically. The header shows `KICKR CORE … · virtual shifting`, or `· FTMS` if the trainer refused Zwift's protocol.
   - Press any Click button so it advertises.
   - On the Garmin, hold **LIGHT** → **Broadcast Heart Rate**. Or enable *Broadcast During Activity* and start an Indoor Bike activity. The app connects to the first heart-rate sensor it sees and remembers it; *Devices → Connect* switches to another one.
4. In **Free**, shift through a few gears and check that the resistance changes. Then try **ERG** at 120 W.
5. If something misbehaves, the Bluetooth trace is in `%LOCALAPPDATA%\BingeRide\logs\ble-*.log`, with every byte sent and received. *Settings → Trainer (advanced)* can force FTMS or send ERG over FTMS.

## Riding

**Device chips (top right):** green means connected, orange means connecting (or the Click v2 "−" puck is locked), grey means not found. Hover a chip for details. The *Devices* tab marks connected devices **✓ Connected**.

**Recording a ride**
- Press **Start ride**, the red button, or Space.
- Time, distance, kJ, power and heart-rate totals only count while recording. They pause on **❚❚** and auto-pause when you stop pedalling.
- **Finish** saves the ride to `Documents\BingeRide\Rides` as a `.fit` file plus a summary, listed in the *Rides* tab. Upload the file at strava.com/upload, in Garmin Connect (Import data) or at intervals.icu.
- Starting a workout starts recording automatically.

**Workouts** start from the *Workouts* panel (or "Start now" when Claude delivers one). While a workout runs, it drives the trainer: the ERG / Slope / Free switch is replaced by a banner with the workout's name and step, and **End workout** returns you to your previous mode. The ride keeps recording.
- Closing the app saves an unfinished ride. Rides under a minute are discarded.
- If your Garmin also records the session, for example with *Broadcast During Activity*, you get two activities. Keep one.

| Control | Action |
|---|---|
| Click `+` / `−` (and **B** = down) | Gears in Slope/Free and in slope/free workout steps; watts in ERG; intensity ±2 % in ERG workout steps |
| Keyboard `+`/`−`/↑/↓, Space | Same, plus start / pause / resume the ride |
| **Overlay** button / `Ctrl+Alt+K` | Switch between the full window and the compact overlay |
| `Ctrl+Alt+L` | Mouse click-through on the overlay (so you can click Netflix underneath) |
| Overlay `−` / ☀ | Background more / less transparent; drag anywhere to move |
| Overlay sliders icon | Choose and order the overlay's fields: power, target, cadence, speed, heart rate, gear, grade, distance, ride time, avg power, kJ, avg/max HR. The first two are big. Also in *Settings*. |

**Zwift Click v2.** The "−" puck stops sending presses about a minute after connecting, unless the Zwift app has unlocked it in the last ~24 h. This is Zwift's lock, and no third-party app can undo it. That is why **B** on the "+" puck also shifts down. *Settings* has an option to keep rebooting the "−" puck instead, which leaves 2–3 s gaps.

Workouts pause automatically when you stop pedalling. Button mappings, FTP and steps are in *Settings*, saved to `%APPDATA%\BingeRide\settings.json`.

## iPhone remote

The PC app keeps talking Bluetooth to the trainer. Your phone becomes a live dashboard and remote over home Wi-Fi: power, heart rate, food, workout step and time left, plus gears/watts ±, modes, start, pause and finish a ride, starting workouts, and ride history.

**Pairing (one time)**
1. *Devices → 📱 Pair a phone*. The first time, Windows Firewall asks: allow BingeRide on **private** networks.
2. Scan the QR with the iPhone Camera, then tap the link.
3. In Safari: Share → **Add to Home Screen**.

**Security**
- The QR holds a one-time code that expires after 5 minutes.
- Pairing gives the phone its own random key, an HttpOnly cookie. The app stores only its hash.
- Anything else on your network gets "Not paired". *Devices → Forget* removes a phone, and *Turn off* stops the web server.
- Traffic is plain HTTP inside your home network, like most smart-home gear.

The page keeps the phone awake after your first tap.

## Workouts from Claude

The app watches `Documents\BingeRide\Workouts`. A file that appears there shows up as **"New workout: … Start now"**.

**Claude Code (this machine):**
- Run `scripts/install-skill.sh` once.
- Then just ask, e.g. *"give me a 60 minute VO2 workout, my FTP is 140"*. Claude writes the JSON, checks it with `skills/bingeride-workout/scripts/check_workout.py`, and delivers it into the folder.

**claude.ai:**
- Upload `dist/bingeride-workout.zip` (built by `scripts/install-skill.sh --zip`) under Settings → Capabilities → Skills.
- Or paste the contents of `skills/bingeride-workout/SKILL.md` into a Project's instructions.
- Copy Claude's answer and press **Paste** (or Ctrl+V) in the app. It finds the workout inside the text.

**Other files:** use **Import**, drag them onto the window, or drop them into the folder. The format is documented in [`docs/workout-format.md`](docs/workout-format.md).

## Layout

```
src/BingeRide.Core       protocols (Zwift trainer, FTMS, Zwift Click), devices, ride controller, workout player
src/BingeRide.Workouts   importers: json, zwo, erg/mrc, intervals.icu, fit, gpx/tcx, paste extraction, stats
src/BingeRide.App        Avalonia UI: main window, overlay, workout chart (platform-neutral)
src/BingeRide.Windows    WinRT Bluetooth, click-through/topmost/hotkeys, exe entry point
tests/                 xunit; App.Tests renders the UI headlessly into ./screenshots
tools/IconGen          regenerates the icons: dotnet run --project tools/IconGen -- src/BingeRide.App/Assets src/BingeRide.App/Web
skills/                the Claude skill
docs/                  workout format, protocol notes and sources
```

Run `dotnet test` from WSL to run everything. Plain Linux works too; only the Windows head needs `EnableWindowsTargeting`.

## License

MIT; see [`LICENSE`](LICENSE). Bundled third-party components and their licenses are listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The Garmin FIT SDK has its own license, not MIT.

Not affiliated with, endorsed by, or sponsored by Wahoo Fitness, Zwift, Garmin or Netflix. Their names are trademarks of their owners and are used only to describe compatibility.

Exercise at your own risk: this app controls the resistance of a trainer you ride. If in doubt about your health, check with a doctor before training hard.
