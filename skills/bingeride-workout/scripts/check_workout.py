#!/usr/bin/env python3
"""Validate a bingeride-workout JSON file, summarise it, and deliver it to the BingeRide app.

  check_workout.py info                 rider FTP/weight and the Workouts folder the app watches
  check_workout.py check   FILE [--ftp N]
  check_workout.py deliver FILE [--ftp N] [--name NAME]

Works on Windows and inside WSL (the app runs on Windows). Standard library only.
"""
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

STEP_TYPES = {"steady", "ramp", "slope", "free", "repeat"}
ALLOWED = {
    "steady": {"type", "sec", "min", "power", "rpm", "text", "cues"},
    "ramp": {"type", "sec", "min", "from", "to", "rpm", "text", "cues"},
    "slope": {"type", "sec", "min", "m", "grade", "gear", "rpm", "text", "cues"},
    "free": {"type", "sec", "min", "m", "grade", "gear", "text", "cues"},
    "repeat": {"type", "times", "steps", "text"},
}


def windows_paths():
    """(Documents, AppData\\Roaming) of the Windows user, as local paths; None when not on Windows/WSL."""
    if os.name == "nt":
        return Path.home() / "Documents", Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    try:
        if "microsoft" not in Path("/proc/version").read_text().lower():
            return None
        def win(var):
            out = subprocess.run(["/mnt/c/Windows/System32/cmd.exe", "/c", f"echo %{var}%"],
                                 capture_output=True, text=True, cwd="/mnt/c", timeout=10).stdout.strip()
            return Path(subprocess.run(["wslpath", out], capture_output=True, text=True, timeout=10).stdout.strip())
        return win("USERPROFILE") / "Documents", win("APPDATA")
    except (OSError, subprocess.SubprocessError):
        return None


def to_local(path_str):
    """Converts a Windows path from the app's settings to a local path when running in WSL."""
    if os.name != "nt" and re.match(r"^[A-Za-z]:\\", path_str or ""):
        return Path(subprocess.run(["wslpath", path_str], capture_output=True, text=True).stdout.strip())
    return Path(path_str)


def app_folder(parent):
    """parent/BingeRide, or parent/MyWahoo (the app's old name) until the app has moved it."""
    if not (parent / "BingeRide").is_dir() and (parent / "MyWahoo").is_dir():
        return parent / "MyWahoo"
    return parent / "BingeRide"


def app_settings():
    paths = windows_paths()
    settings, folder = {}, None
    if paths:
        docs, appdata = paths
        try:
            settings = json.loads((app_folder(appdata) / "settings.json").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            settings = {}
        folder = to_local(settings["WorkoutsFolder"]) if settings.get("WorkoutsFolder") else app_folder(docs) / "Workouts"
    else:
        folder = app_folder(Path.home() / "Documents") / "Workouts"
    return settings, folder


def seconds(step, errors, where):
    if "min" in step and "sec" not in step:
        return float(step["min"]) * 60
    sec = step.get("sec")
    if not isinstance(sec, (int, float)) or sec <= 0:
        errors.append(f"{where}: 'sec' must be a positive number")
        return 0
    return float(sec)


def flatten(steps, errors, where="steps", depth=0):
    out = []
    if not isinstance(steps, list) or not steps:
        errors.append(f"{where}: must be a non-empty list")
        return out
    for i, step in enumerate(steps):
        at = f"{where}[{i}]"
        if not isinstance(step, dict):
            errors.append(f"{at}: must be an object")
            continue
        kind = step.get("type")
        if kind not in STEP_TYPES:
            errors.append(f"{at}: unknown type {kind!r} (use {', '.join(sorted(STEP_TYPES))})")
            continue
        extra = set(step) - ALLOWED[kind]
        if extra:
            errors.append(f"{at}: unexpected field(s) {', '.join(sorted(extra))} for '{kind}'")
        if kind == "repeat":
            if depth > 0:
                errors.append(f"{at}: repeats can't be nested")
                continue
            times = step.get("times")
            if not isinstance(times, int) or not 1 <= times <= 50:
                errors.append(f"{at}: 'times' must be an integer 1..50")
                continue
            inner = flatten(step.get("steps"), errors, f"{at}.steps", depth + 1)
            out.extend(inner * times)
            continue
        if kind in ("slope", "free") and "m" in step:
            length = ("m", float(step["m"]))
        else:
            length = ("sec", seconds(step, errors, at))
        if kind in ("steady", "ramp"):
            keys = ["power"] if kind == "steady" else ["from", "to"]
            for k in keys:
                v = step.get(k)
                if not isinstance(v, (int, float)) or v < 0:
                    errors.append(f"{at}: '{k}' must be a number")
        if kind == "slope" and not isinstance(step.get("grade"), (int, float)):
            errors.append(f"{at}: 'grade' is required for slope steps")
        g = step.get("grade")
        if isinstance(g, (int, float)) and not -20 <= g <= 25:
            errors.append(f"{at}: grade {g}% is outside -20..25")
        gear = step.get("gear")
        if gear is not None and (not isinstance(gear, int) or not 1 <= gear <= 24):
            errors.append(f"{at}: gear must be 1..24")
        out.append((kind, length, step))
    return out


def analyse(doc, ftp):
    errors, warnings = [], []
    if not isinstance(doc, dict):
        return ["top level must be a JSON object"], [], None
    if doc.get("format") not in ("bingeride-workout", "mywahoo-workout"):
        warnings.append('missing "format": "bingeride-workout"')
    if not doc.get("name"):
        errors.append("missing 'name'")
    unit = doc.get("power_unit", "pct_ftp")
    if unit not in ("pct_ftp", "watts"):
        errors.append("power_unit must be 'pct_ftp' or 'watts'")
    steps = flatten(doc.get("steps"), errors)

    def watts(v):
        return v * ftp / 100 if unit == "pct_ftp" else v

    per_second, total_sec, total_m, max_target = [], 0.0, 0.0, 0.0
    for kind, (unit_len, length), step in steps:
        if unit_len == "m":
            total_m += length
            continue
        total_sec += length
        n = int(round(length))
        if kind == "steady":
            p = step.get("power", 0)
            if unit == "pct_ftp" and 0 < p <= 3:
                warnings.append(f"power {p} looks like a fraction; write {p * 100:g} (percent of FTP)")
            per_second += [watts(p)] * n
            max_target = max(max_target, watts(p))
        elif kind == "ramp":
            a, b = watts(step.get("from", 0)), watts(step.get("to", 0))
            max_target = max(max_target, a, b)
            per_second += [a + (b - a) * (i + 0.5) / max(n, 1) for i in range(n)]
        else:
            per_second += [ftp * (0.65 if kind == "slope" else 0.5)] * n
    if total_sec > 6 * 3600:
        errors.append("longer than 6 hours")
    summary = None
    if per_second:
        avg = sum(per_second) / len(per_second)
        # Standard NP: 4th-power mean of full 30 s rolling averages (plain average under 30 s), as the app computes it.
        if len(per_second) >= 30:
            window = sum(per_second[:30])
            rolling = [window / 30]
            for i in range(30, len(per_second)):
                window += per_second[i] - per_second[i - 30]
                rolling.append(window / 30)
            np_ = (sum(x ** 4 for x in rolling) / len(rolling)) ** 0.25
        else:
            np_ = avg
        if_ = np_ / ftp
        summary = {
            "steps": len(steps), "seconds": total_sec, "metres": total_m,
            "avg_w": avg, "np_w": np_, "if": if_, "tss": total_sec * np_ * if_ / (ftp * 3600) * 100,
            "max_w": max_target,
        }
    elif total_m:
        summary = {"steps": len(steps), "seconds": 0, "metres": total_m}
    return errors, warnings, summary


def fmt_time(s):
    s = int(round(s))
    return f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60}:{s % 60:02d}"


def slug(name):
    s = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return (s or "workout")[:60]


def main(argv):
    if not argv or argv[0] not in ("info", "check", "deliver"):
        print(__doc__)
        return 2
    settings, folder = app_settings()
    if argv[0] == "info":
        print(json.dumps({
            "ftp": settings.get("Ftp"), "rider_kg": settings.get("RiderKg"),
            "workouts_folder": str(folder), "folder_exists": folder.exists(),
        }, indent=2))
        return 0

    if len(argv) < 2:
        print("missing FILE")
        return 2
    path = Path(argv[1])
    ftp = None
    name = None
    rest = argv[2:]
    for i, a in enumerate(rest):
        if a == "--ftp" and i + 1 < len(rest):
            ftp = float(rest[i + 1])
        if a == "--name" and i + 1 < len(rest):
            name = rest[i + 1]
    try:
        doc = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as e:
        print(f"ERROR: can't read JSON: {e}")
        return 1
    ftp = ftp or doc.get("ftp") or settings.get("Ftp") or 200
    errors, warnings, summary = analyse(doc, float(ftp))
    for w in warnings:
        print(f"warning: {w}")
    for e in errors:
        print(f"ERROR: {e}")
    if summary:
        parts = [f"{summary['steps']} steps"]
        if summary["seconds"]:
            parts.append(f"duration {fmt_time(summary['seconds'])}")
        if summary["metres"]:
            parts.append(f"distance {summary['metres'] / 1000:.1f} km")
        if "avg_w" in summary:
            parts += [f"avg {summary['avg_w']:.0f} W", f"NP {summary['np_w']:.0f} W", f"IF {summary['if']:.2f}",
                      f"TSS {summary['tss']:.0f}", f"max target {summary['max_w']:.0f} W"]
        print(f"{doc.get('name', '?')} @ FTP {ftp:g}: " + ", ".join(parts))
    if errors:
        return 1
    if argv[0] == "deliver":
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / f"{slug(name or doc['name'])}.json"
        n = 2
        while target.exists():
            target = folder / f"{slug(name or doc['name'])}-{n}.json"
            n += 1
        tmp = target.with_suffix(".json.tmp")
        shutil.copyfile(path, tmp)
        os.replace(tmp, target)
        print(f"Delivered: {target}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
