---
name: mywahoo-workout
description: Design indoor-cycling workouts for the MyWahoo KICKR trainer app (ERG power intervals, slope/grade climbs, free-ride gear sections) and deliver them straight into the app. Use whenever the user asks for a bike/trainer workout or training session, e.g. "give me a 60 minute VO2 workout", "sweet spot session", "45 min of climbing", "easy recovery spin", "over-unders for FTP 140".
---

# MyWahoo workouts

The MyWahoo app (Windows, controls a Wahoo KICKR CORE 2) loads workouts written in its JSON format.
It watches a folder and pops up "New workout: … Start now" as soon as a file lands there.

## 1. Get the rider's numbers
- **FTP:** use the one the user states. Otherwise run `python3 scripts/check_workout.py info`, which reads the app's settings.
- **Fallback:** if neither gives an FTP, ask the user. A 60-minute workout at the wrong FTP is a bad session.
- **Write power as % of FTP** (`"power_unit": "pct_ftp"`). The app then scales it to the FTP set in the app. Record the FTP you assumed in `"ftp"`.

## 2. Write the workout (format: mywahoo-workout v1)

```json
{
  "format": "mywahoo-workout", "version": 1,
  "name": "VO2max 5x3' @120%",
  "description": "One or two sentences: purpose and how to ride it.",
  "author": "Claude", "ftp": 140, "power_unit": "pct_ftp", "tags": ["vo2max"],
  "steps": [
    { "type": "ramp",   "sec": 600, "from": 45, "to": 75, "text": "Warm-up: build gradually" },
    { "type": "repeat", "times": 5, "steps": [
        { "type": "steady", "sec": 180, "power": 120, "rpm": 95, "text": "VO2 rep: smooth, high cadence" },
        { "type": "steady", "sec": 180, "power": 50, "text": "Recover" } ] },
    { "type": "slope",  "sec": 300, "grade": 6, "gear": 10, "text": "Seated climb, shift as needed" },
    { "type": "free",   "sec": 300, "grade": 0, "text": "Free spin, any gear" }
  ]
}
```

| type | trainer mode | required | optional |
|---|---|---|---|
| `steady` | ERG holds `power` | `sec`, `power` | `rpm`, `text`, `cues` |
| `ramp` | ERG goes linearly from `from` to `to` | `sec`, `from`, `to` | `rpm`, `text`, `cues` |
| `slope` | SIM at `grade` %; rider shifts virtual gears | `sec` **or** `m` (metres), `grade` | `gear` (1–24), `rpm`, `text`, `cues` |
| `free` | no target, rider shifts gears | `sec` **or** `m` | `grade` (default 0), `gear`, `text`, `cues` |
| `repeat` | repeats its steps | `times`, `steps` | `text` |

**Units and values**
- Power values are integers: 75 means 75 % FTP. Never write a fraction like 0.75. If `"power_unit": "watts"`, they are absolute watts.
- `sec` is whole seconds.
- Grade is in % (−10…20): 0–3 % rolling, 4–7 % climb, 8 %+ steep.
- Gears: 1 is easiest, 24 hardest, 12 is neutral. Suggest about 8–11 for 6 % climbs.

**Structure**
- A repeat can only contain plain steps; a repeat inside a repeat is rejected.
- `cues`: `[{ "at": 90, "text": "Halfway!" }]`. `text` on a step is the cue shown when the step starts. The rider is watching Netflix, so keep cues short and useful.

## 3. Design rules
- **Total length:** make it match the request exactly; a "60 minute" session is 3600 s. Check the total with the script.
- **Shape:** always include a warm-up of 8–15 min, ramping 40–50 % → 65–75 %, with openers for hard days. Always end with a cool-down of 5–10 min.
- **Intensity guide (% FTP):**

  | Session | Power | Typical structure |
  |---|---|---|
  | Recovery | < 55 | — |
  | Endurance | 56–75 | — |
  | Tempo | 76–90 | — |
  | Sweet spot | 88–94 | 8–20 min reps |
  | Threshold | 95–105 | 8–20 min reps |
  | VO2max | 106–120 | 2–5 min reps, recovery ≈ rep length at 40–55 % |
  | Anaerobic | 121–150 | 30 s – 2 min |
  | Sprints | 150+ | ≤ 20 s |

- **Climbing sessions:** use `slope` steps with a gear hint, and give cadence cues instead of watts.
- **Reasonable totals:** time-in-zone for VO2 is about 12–20 min; for threshold, 20–60 min.

## 4. Check and deliver
Save the JSON to a scratch file. Then, from this skill's directory:

```bash
python3 scripts/check_workout.py check   /path/to/workout.json   # validates, prints duration / avg W / TSS at FTP
python3 scripts/check_workout.py deliver /path/to/workout.json   # validates, then drops it into the app's Workouts folder
```

- **Fixing errors:** if `check` reports errors, fix them and run it again. Also fix it if the duration is off.
- **What deliver does:** it finds the folder automatically, on Windows or from WSL (`/mnt/c/Users/<user>/Documents/MyWahoo/Workouts`). It writes the file atomically, so the app never reads a half-written file.
- **After delivering:** tell the user the workout's name, its length, and its key targets in watts at their FTP. It is waiting in MyWahoo; they click **Start now**, or pick it in the Workouts list.

**No filesystem access (claude.ai chat):**
- Print the workout as **one** fenced `json` block, with a one-line summary above it.
- Tell the user to copy the answer and press **Paste** (or Ctrl+V) in MyWahoo. The app finds the block inside the copied text.

The app also imports .zwo, .erg/.mrc, .fit, intervals.icu text, GPX and TCX. JSON is the only format that can mix ERG, slope and free steps, so prefer it.
