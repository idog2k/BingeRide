namespace BingeRide.Workouts;

/// <summary>
/// A workout could not be read or failed validation. <see cref="Exception.Message"/> is user-readable
/// and includes line, element or step information where available.
/// </summary>
public sealed class WorkoutFormatException : Exception
{
    /// <summary>Creates an exception with a single user-readable error.</summary>
    public WorkoutFormatException(string message) : this(message, null) { }

    /// <summary>Creates an exception with a single user-readable error and its cause.</summary>
    public WorkoutFormatException(string message, Exception? innerException) : base(message, innerException)
        => Errors = [message];

    /// <summary>Creates an exception from several user-readable errors (e.g. validation results).</summary>
    public WorkoutFormatException(IReadOnlyList<string> errors) : base(string.Join(Environment.NewLine, errors))
        => Errors = errors.Count > 0 ? errors : ["The workout is invalid."];

    /// <summary>The individual error messages (at least one).</summary>
    public IReadOnlyList<string> Errors { get; }
}
