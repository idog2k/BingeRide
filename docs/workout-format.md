# BingeRide workout format (`bingeride-workout` v1)

BingeRide's native workout format is JSON. It is the only format that can mix the trainer's three modes in
one workout:

- **ERG**: the trainer holds a power target.
- **Slope**: SIM mode at a road grade; the rider shifts virtual gears.
- **Free**: gears only.

It is written to be easy for people and for Claude to produce. A JSON Schema is in
[`bingeride-workout.schema.json`](bingeride-workout.schema.json).

## Example

```json
{
  "format": "bingeride-workout",
  "version": 1,
  "name": "VO2max 5x3' @120%",
  "description": "Ramp warm-up, then 5 x 3 min at 120% FTP with 3 min easy. Aim for 95+ rpm on the reps.",
  "author": "Claude",
  "ftp": 140,
  "power_unit": "pct_ftp",
  "tags": ["vo2max", "intervals"],
  "steps": [
    { "type": "ramp", "sec": 600, "from": 45, "to": 75, "text": "Warm-up: build gradually" },
    { "type": "repeat", "times": 5, "text": "Main set", "steps": [
      { "type": "steady", "sec": 180, "power": 120, "rpm": 95, "text": "VO2 rep",
        "cues": [{ "at": 90, "text": "Halfway, stay smooth" }] },
      { "type": "steady", "sec": 180, "power": 50, "text": "Recover" }
    ]},
    { "type": "slope", "sec": 300, "grade": 6, "gear": 9, "text": "Seated climb" },
    { "type": "free", "sec": 300, "grade": 0, "text": "Free spin, any gear" }
  ]
}
```

## Top level

| field | required | meaning |
|---|---|---|
| `format` | yes | Always `"bingeride-workout"`. Files from before the app was renamed say `"mywahoo-workout"`, which is still read. |
| `version` | yes | Always `1`. |
| `name` | yes | Short title shown in the library. |
| `description` | no | One or two sentences: purpose and how to ride it. |
| `author` | no | e.g. `"Claude"`. |
| `ftp` | no | FTP (W) the author assumed; informational only. |
| `power_unit` | yes | `"pct_ftp"`: 75 means 75 % FTP. Prefer this, so the workout scales to the rider. `"watts"`: absolute watts. |
| `tags` | no | Array of strings. |
| `steps` | yes | Non-empty array of steps, played in order. |

## Steps

Every step has a `type`. Time is in `sec` (or `min`, which may be fractional). Slope and free steps may use
a distance in metres (`m`) instead.

| type | trainer | required | optional |
|---|---|---|---|
| `steady` | ERG holds `power` | `sec`, `power` | `rpm`, `text`, `cues` |
| `ramp` | ERG moves linearly from `from` to `to` | `sec`, `from`, `to` | `rpm`, `text`, `cues` |
| `slope` | SIM at `grade` %; rider shifts gears | `sec` or `m`, `grade` | `gear`, `rpm`, `text`, `cues` |
| `free` | no target; SIM at `grade` (default 0 %) | `sec` or `m` | `grade`, `gear`, `text`, `cues` |
| `repeat` | repeats its `steps` `times` times | `times`, `steps` | `text` |

### Fields

- **`text`**: the headline cue shown when the step starts. On a `repeat`, it is shown once, at the start of
  the first repetition.
- **`cues`**: `[{"at": seconds, "text": "..."}]`, extra messages shown that many seconds into the step.
- **`rpm`**: a cadence suggestion. It is not enforced.
- **`gear`**: the virtual gear (1 = easiest, 24 = hardest, 12 ≈ neutral) selected when the step starts.
  Leave it out to keep the rider's current gear.
- **`repeat`**: a repeat may contain only plain steps, not another repeat. It is expanded when the workout
  is imported, and each copy is labelled "2/5" and so on.

### Limits

Imports that break these limits are rejected:

- Steps are at least 1 s long; the whole workout is at most 6 h.
- Power is at most 300 % FTP or 2000 W. ERG steps (steady, ramp) must be time-based.
- Grade is between -20 and 25 %. Gear is between 1 and 24.

### Writing tips

- Use whole numbers: `"power": 75`, not `0.75`.
- Make `sec` values add up to the requested length (60 min = 3600 s).
- Keep cues short; the rider is busy.

## What the importer forgives

Write to the schema. When reading, the importer is lenient and reports these fixes as warnings:

- Comments, trailing commas and any capitalisation of property names.
- `"duration"` as an alias for `sec`. `type` can be inferred from the fields.
- A missing `format` or `version`.
- Power values of 3 or less with `pct_ftp`: read as fractions (0.75 → 75 %).
- A missing `power_unit`: % FTP is assumed if every value is 300 or less, otherwise watts.
- Unknown properties are ignored.

## Pasting from a chat

**Paste** in BingeRide accepts a whole chat answer. It checks every fenced code block (any language tag) and
the full text, and finds a workout by its content:

- JSON with `"steps"`
- `<workout_file>` (Zwift)
- `[COURSE HEADER]` (ERG/MRC)
- two or more intervals.icu step lines

If several parse, the **last** code block wins. So when correcting a workout, put the fixed version last.
Smart quotes, non-breaking spaces and Windows line endings are cleaned up first.

## Other formats the app imports

| extension | format | notes |
|---|---|---|
| `.zwo` | Zwift XML | `SteadyState`, `Warmup`/`Cooldown`, `Ramp`, `IntervalsT`, `FreeRide`, `MaxEffort`, `textevent`. Power values are FTP fractions. Auuki extension: `Slope="5"` on `SteadyState`/`FreeRide`, and `OnSlope`/`OffSlope` on `IntervalsT`. |
| `.erg` / `.mrc` | TrainerRoad / GoldenCheetah | `[COURSE DATA]` is a list of `minutes value` points (watts for `MINUTES WATTS`, % FTP for `MINUTES PERCENT`). `[COURSE TEXT]` lines are `seconds<TAB>text`. |
| `.txt` | intervals.icu text | See below. |
| `.fit` | Garmin workout | Power targets (custom %FTP or watts, or zones 1-7) become ERG steps. Open or HR targets become free riding. |
| `.gpx` / `.tcx` | GPS route with elevation | Becomes about 100 m distance-based slope steps (smoothed, grade clamped to -15…20 %). |

### intervals.icu text

```text
Warmup
- 10m ramp 45-75% 90rpm
- 3m 55%

Main set 4x
- 2m 95% Under
- 1m 105% Over
- 3m Z1 Recover

- 5m slope 6% gear 9
- 5m freeride
```

**Step lines.** Each step line starts with `-`.

- **Durations:** `1h`, `10m`, `30s`, `1m30`, `90s`, `5'`, `30"`.
- **Power targets:**
  - `75%`, `200w` or `Z1`…`Z7`. Zones use the Coggan midpoints: 50, 65, 83, 98, 113, 135 and 160 % FTP.
  - A range such as `95-105%` means a steady step at the midpoint.
  - `ramp 50-75%` is a ramp.
- **Freeride:** `freeride` makes a free step.
- **Slope extension:** `slope 6%` (or `6% grade`), with an optional `gear N`.
- **Cue text:** any other words on the line become the step's cue.

**Repeats and section titles.**

- A line ending in `Nx` repeats the step lines that follow it, up to the next blank line.
- A word-only line such as `Warmup` is a section title. It becomes the cue on the next step.

**Not supported:** distance steps (`2km`, `500mtr`) and heart-rate or pace targets.
