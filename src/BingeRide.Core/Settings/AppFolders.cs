namespace BingeRide.Core.Settings;

/// <summary>
/// The app's folders under Documents, %APPDATA% and %LOCALAPPDATA%. The app used to be called MyWahoo:
/// the first run moves each old folder to its new name, and keeps using the old one if that fails
/// (e.g. the old version is still running with a file open), trying again next start.
/// </summary>
public static class AppFolders
{
    public const string Name = "BingeRide";
    private const string LegacyName = "MyWahoo";

    private static readonly Lazy<string> DocumentsFolder = new(() => Resolve(Environment.SpecialFolder.MyDocuments));
    private static readonly Lazy<string> RoamingFolder = new(() => Resolve(Environment.SpecialFolder.ApplicationData));
    private static readonly Lazy<string> LocalFolder = new(() => Resolve(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Documents\BingeRide: workouts and rides.</summary>
    public static string Documents => DocumentsFolder.Value;

    /// <summary>%APPDATA%\BingeRide: settings.</summary>
    public static string Roaming => RoamingFolder.Value;

    /// <summary>%LOCALAPPDATA%\BingeRide: logs.</summary>
    public static string Local => LocalFolder.Value;

    internal static string Resolve(Environment.SpecialFolder root) => Resolve(Environment.GetFolderPath(root));

    internal static string Resolve(string parent)
    {
        var current = Path.Combine(parent, Name);
        var legacy = Path.Combine(parent, LegacyName);
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }
}
