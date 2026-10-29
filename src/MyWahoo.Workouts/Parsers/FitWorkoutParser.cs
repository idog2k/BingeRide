using System.Text;
using Dynastream.Fit;
using MyWahoo.Core.Workouts;
using FitFile = Dynastream.Fit.File;

namespace MyWahoo.Workouts.Parsers;

// Reads Garmin .fit workout files (WorkoutMesg + WorkoutStepMesg) with the Garmin FIT SDK.
// Power targets become ERG steps; open/other targets become free-ride steps with a warning.
internal static class FitWorkoutParser
{
    public const string Format = "fit";
    const double OpenStepSeconds = 300;

    // True when the bytes start with a FIT file header (12 or 14 bytes, ".FIT" signature).
    public static bool LooksLikeFit(ReadOnlySpan<byte> data) =>
        data.Length >= 14 && data[0] is 12 or 14 && data.Slice(8, 4).SequenceEqual(".FIT"u8);

    public static Workout Parse(byte[] data, string fallbackName, List<string> warnings)
    {
        if (!LooksLikeFit(data)) throw new WorkoutFormatException("This isn't a FIT file (the \".FIT\" header is missing).");

        FitFile? fileType = null;
        string? name = null, description = null;
        Sport? sport = null;
        var messages = new List<StepMessage>();

        // Once a process has encoded FIT data, the SDK's shared profile templates can carry leftover
        // string values into later decodes (the file's own value is then appended after them). So a
        // string is only read when the file's definition contains the field, and its last value wins.
        var definedFields = new Dictionary<byte, HashSet<byte>>();
        byte localNum = 0;
        string? Text(Mesg mesg, byte fieldNum)
        {
            if (!definedFields.TryGetValue(localNum, out var fields) || !fields.Contains(fieldNum)) return null;
            var count = mesg.GetNumFieldValues(fieldNum);
            return count > 0 && mesg.GetFieldValue(fieldNum, count - 1) is byte[] bytes ? FitString(Encoding.UTF8.GetString(bytes)) : null;
        }

        var decoder = new Decode();
        var broadcaster = new MesgBroadcaster();
        decoder.MesgDefinitionEvent += (_, e) => definedFields[e.mesgDef.LocalMesgNum] = [.. e.mesgDef.GetFields().Select(f => f.Num)];
        decoder.MesgEvent += (_, e) => localNum = e.mesg.LocalNum; // runs before the broadcaster's handlers
        decoder.MesgEvent += broadcaster.OnMesg;
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        broadcaster.FileIdMesgEvent += (_, e) => fileType ??= ((FileIdMesg)e.mesg).GetType();
        broadcaster.WorkoutMesgEvent += (_, e) =>
        {
            var workout = (WorkoutMesg)e.mesg;
            name ??= Text(workout, WorkoutMesg.FieldDefNum.WktName);
            description ??= Text(workout, WorkoutMesg.FieldDefNum.WktDescription);
            sport ??= workout.GetSport();
        };
        broadcaster.WorkoutStepMesgEvent += (_, e) =>
        {
            var step = (WorkoutStepMesg)e.mesg;
            messages.Add(new StepMessage(
                step,
                Text(step, WorkoutStepMesg.FieldDefNum.WktStepName),
                Text(step, WorkoutStepMesg.FieldDefNum.Notes)));
        };

        using (var stream = new MemoryStream(data, writable: false))
        {
            try
            {
                if (!decoder.CheckIntegrity(stream)) warnings.Add("The FIT file's checksum doesn't match; it may be damaged.");
                stream.Position = 0;
                if (!decoder.Read(stream)) throw new WorkoutFormatException("Couldn't read the FIT file.");
            }
            catch (Exception ex) when (ex is not WorkoutFormatException)
            {
                throw new WorkoutFormatException($"Couldn't read the FIT file: {ex.Message}", ex);
            }
        }

        if (fileType is { } type && type != FitFile.Workout)
            throw new WorkoutFormatException($"This FIT file is a \"{type}\" file, not a workout. Export the planned workout, not a ride.");
        if (messages.Count == 0) throw new WorkoutFormatException("The FIT file has no workout steps.");
        if (sport is { } s && s is not (Sport.Cycling or Sport.Generic or Sport.FitnessEquipment or Sport.All))
            warnings.Add($"The workout is for {s}; importing it as a cycling workout.");

        return new Workout
        {
            Name = name ?? fallbackName,
            Description = description,
            Steps = BuildSteps(messages, warnings),
            SourceFormat = Format,
        };
    }

    sealed record StepMessage(WorkoutStepMesg Mesg, string? Name, string? Notes);

    static List<WorkoutStep> BuildSteps(List<StepMessage> messages, List<string> warnings)
    {
        // Steps are addressed by message index; fall back to arrival order when it's missing.
        var ordered = messages
            .Select((m, i) => (Index: m.Mesg.GetMessageIndex() is { } mi ? mi & 0x0FFF : i, Message: m))
            .OrderBy(x => x.Index)
            .ToList();

        var output = new List<WorkoutStep>();
        var startOf = new Dictionary<int, int>();
        foreach (var (index, (m, stepName, notes)) in ordered)
        {
            startOf[index] = output.Count;
            var label = stepName is null ? $"Step {index + 1}" : $"Step {index + 1} (\"{TextUtil.Shorten(stepName)}\")";
            var durationType = m.GetDurationType() ?? WktStepDuration.Open;

            if (durationType == WktStepDuration.RepeatUntilStepsCmplt)
            {
                var from = (long)(m.GetDurationValue() ?? uint.MaxValue);
                var times = (long)(m.GetTargetValue() ?? 0);
                if (from >= index || !startOf.TryGetValue((int)from, out var start))
                    throw new WorkoutFormatException($"{label}: the repeat points at step {from + 1}, which doesn't come before it.");
                if (times is < 1 or > StepBuilder.MaxRepeat)
                    throw new WorkoutFormatException($"{label}: the repeat count {times} must be between 1 and {StepBuilder.MaxRepeat}.");
                var body = output.GetRange(start, output.Count - start);
                if (body.Count == 0) throw new WorkoutFormatException($"{label}: the repeat has no steps to repeat.");
                output.RemoveRange(start, body.Count);
                StepBuilder.AddRepeat(output, body, (int)times, stepName);
                startOf[index] = start;
                continue;
            }
            if (durationType is WktStepDuration.RepeatUntilTime or WktStepDuration.RepeatUntilDistance
                or WktStepDuration.RepeatUntilCalories or WktStepDuration.RepeatUntilHrLessThan
                or WktStepDuration.RepeatUntilHrGreaterThan or WktStepDuration.RepeatUntilPowerLessThan
                or WktStepDuration.RepeatUntilPowerGreaterThan or WktStepDuration.RepeatUntilTrainingPeaksTss
                or WktStepDuration.RepeatUntilPowerLastLapLessThan or WktStepDuration.RepeatUntilMaxPowerLastLapLessThan)
                throw new WorkoutFormatException($"{label}: \"{durationType}\" repeats aren't supported; only \"repeat N times\" blocks are.");

            var cues = Cues(m, stepName, notes);
            var (kind, power) = Target(m, label, warnings);
            double sec = 0, metres = 0;
            switch (durationType)
            {
                case WktStepDuration.Time:
                    sec = (m.GetDurationValue() ?? 0) / 1000.0;
                    if (sec <= 0) throw new WorkoutFormatException($"{label}: the step has no duration.");
                    break;
                case WktStepDuration.Distance:
                    metres = (m.GetDurationValue() ?? 0) / 100.0;
                    if (kind == StepKind.Erg)
                        throw new WorkoutFormatException($"{label}: distance-based power steps can't be ridden in ERG mode; use a time-based step.");
                    if (metres <= 0) throw new WorkoutFormatException($"{label}: the step has no distance.");
                    break;
                case WktStepDuration.Open:
                    (sec, kind) = (OpenStepSeconds, StepKind.Free);
                    warnings.Add($"{label}: an open (lap-button) step became 5 min of free riding.");
                    break;
                default:
                    (sec, kind) = (OpenStepSeconds, StepKind.Free);
                    warnings.Add($"{label}: \"{durationType}\" durations aren't supported; the step became 5 min of free riding.");
                    break;
            }

            output.Add(kind == StepKind.Erg
                ? StepBuilder.Erg(sec, power!.Value, power.Value, null, cues)
                : new WorkoutStep { Kind = StepKind.Free, DurationSec = sec, DistanceM = metres, Cues = cues });
            if (output.Count > StepBuilder.MaxSteps) throw new WorkoutFormatException("The workout has too many steps.");
        }
        return output;
    }

    static (StepKind Kind, PowerTarget? Power) Target(WorkoutStepMesg m, string label, List<string> warnings)
    {
        var targetType = m.GetTargetType() ?? WktStepTarget.Open;
        switch (targetType)
        {
            case WktStepTarget.Power or WktStepTarget.Power3s or WktStepTarget.Power10s or WktStepTarget.Power30s or WktStepTarget.PowerLap:
                var zone = m.GetTargetValue() ?? 0;
                if (zone == 0)
                {
                    var (lo, hi) = (m.GetCustomTargetValueLow(), m.GetCustomTargetValueHigh());
                    if (lo is null && hi is null) throw new WorkoutFormatException($"{label}: the custom power target has no values.");
                    var a = Custom(lo ?? hi!.Value);
                    var b = Custom(hi ?? lo!.Value);
                    if (a.Unit != b.Unit) throw new WorkoutFormatException($"{label}: the power range mixes %FTP and watts.");
                    return (StepKind.Erg, new PowerTarget(Math.Round((a.Value + b.Value) / 2, 2), a.Unit));
                }
                if (zone <= 7 && PowerZones.TryGetMidpoint((int)zone, out var percent))
                    return (StepKind.Erg, PowerTarget.Percent(percent));
                throw new WorkoutFormatException($"{label}: unknown power zone {zone}.");

            case WktStepTarget.Open:
                return (StepKind.Free, null);

            default:
                warnings.Add($"{label}: {targetType} targets can't drive the trainer; the step became free riding.");
                return (StepKind.Free, null);
        }

        // FIT custom power: 0..1000 = %FTP, above 1000 = watts + 1000.
        static PowerTarget Custom(uint value) => value <= 1000 ? PowerTarget.Percent(value) : PowerTarget.Watts(value - 1000);
    }

    // FIT strings are NUL-padded to the field width; cut at the first NUL.
    static string? FitString(string? value)
    {
        if (value is null) return null;
        var nul = value.IndexOf('\0');
        return TextUtil.Blank(nul >= 0 ? value[..nul] : value);
    }

    static IReadOnlyList<WorkoutCue> Cues(WorkoutStepMesg m, string? stepName, string? notes)
    {
        var headline = stepName ?? m.GetIntensity() switch
        {
            Intensity.Warmup => "Warm-up",
            Intensity.Cooldown => "Cool-down",
            Intensity.Rest => "Rest",
            Intensity.Recovery => "Recovery",
            Intensity.Interval => "Interval",
            _ => null,
        };
        return StepBuilder.Cues(headline, notes is null ? null : [new WorkoutCue(0, notes)]);
    }
}
