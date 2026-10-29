using Avalonia;

namespace MyWahoo.Windows;

internal static class Program
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyWahoo");

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.SetObserved();
        };

        BleTrace.Start(Path.Combine(DataFolder, "logs"));
        // --no-bluetooth: UI only (demo trainer), e.g. for trying the app without touching the trainer.
        App.App.Platform = new WindowsPlatformServices(bluetooth: !args.Contains("--no-bluetooth"));
        App.App.StartInOverlay = args.Contains("--overlay");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            BleTrace.Stop();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UsePlatformDetect().LogToTrace();

    private static void WriteCrash(object error)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(Path.Combine(DataFolder, "crash.log"), $"{DateTime.Now:O}\n{error}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
