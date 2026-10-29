using System.Collections.Concurrent;

namespace MyWahoo.Windows;

/// <summary>
/// Hex trace of every GATT write and notification, one file per run in %LOCALAPPDATA%\MyWahoo\logs.
/// The KICKR/Click protocols are reverse-engineered, so this is what to send when something misbehaves.
/// </summary>
internal static class BleTrace
{
    private static readonly BlockingCollection<string> Queue = new(10_000);
    private static Thread? _writer;

    public static void Start(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in new DirectoryInfo(folder).GetFiles("ble-*.log").OrderByDescending(f => f.Name).Skip(20)) old.Delete();
            var path = Path.Combine(folder, $"ble-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new Thread(() =>
            {
                using var file = new StreamWriter(path) { AutoFlush = true };
                foreach (var line in Queue.GetConsumingEnumerable()) file.WriteLine(line);
            }) { IsBackground = true, Name = "BLE trace" };
            _writer.Start();
        }
        catch (IOException)
        {
            // Tracing is best effort.
        }
    }

    public static void Log(string message)
    {
        if (_writer is not null) Queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public static void Data(ulong address, string direction, Guid characteristic, ReadOnlySpan<byte> data) =>
        Log($"{address:X12} {direction} {ShortUuid(characteristic)} {Convert.ToHexString(data)}");

    private static string ShortUuid(Guid uuid)
    {
        var s = uuid.ToString();
        return s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ? s[4..8].ToUpperInvariant() : s[..8];
    }

    public static void Stop()
    {
        Queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
    }
}
