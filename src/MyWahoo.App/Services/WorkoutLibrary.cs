using System.Reflection;
using System.Text;
using MyWahoo.Core.Workouts;
using MyWahoo.Workouts;

namespace MyWahoo.App.Services;

public sealed record LibraryEntry(string Path, Workout? Workout, string? Error, DateTime Modified)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Name => Workout?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool HasError => Workout is null;

    public string Subtitle
    {
        get
        {
            if (Workout is not { } w) return Error ?? "Unreadable";
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
            var length = w.TotalDurationSec > 0 ? TimeSpan.FromSeconds(w.TotalDurationSec).ToString(w.TotalDurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";
            if (w.TotalDistanceM > 0) length = (length.Length > 0 ? length + " + " : "") + $"{w.TotalDistanceM / 1000:0.0} km";
            return $"{length} · {ext}";
        }
    }
}

/// <summary>
/// The workouts folder (Documents\MyWahoo\Workouts by default). Anything dropped there, including files
/// Claude writes, is imported automatically; <see cref="WorkoutArrived"/> fires for files that appear while running.
/// Events are raised on a background thread.
/// </summary>
public sealed class WorkoutLibrary : IDisposable
{
    private readonly Dictionary<string, LibraryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private FileSystemWatcher? _watcher;

    public WorkoutLibrary(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public event EventHandler? Changed;
    public event EventHandler<LibraryEntry>? WorkoutArrived;

    public IReadOnlyList<LibraryEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.Values.OrderByDescending(e => e.Modified).ToList();
        }
    }

    /// <summary>Creates the folder (seeding example workouts the first time), loads it and starts watching.</summary>
    public void Start(bool watch = true)
    {
        if (!Directory.Exists(Folder))
        {
            Directory.CreateDirectory(Folder);
            SeedSamples();
        }
        foreach (var file in Directory.EnumerateFiles(Folder).Where(IsSupported)) Load(file);
        Changed?.Invoke(this, EventArgs.Empty);
        if (!watch) return;

        _watcher = new FileSystemWatcher(Folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, e) => Debounce(e.FullPath, isNew: true);
        _watcher.Changed += (_, e) => Debounce(e.FullPath, isNew: false);
        _watcher.Renamed += (_, e) =>
        {
            Remove(e.OldFullPath);
            Debounce(e.FullPath, isNew: true);
        };
        _watcher.Deleted += (_, e) => Remove(e.FullPath);
        _watcher.Error += (_, _) => Rescan();
        _watcher.EnableRaisingEvents = true;
    }

    public void Rescan()
    {
        lock (_lock) _entries.Clear();
        foreach (var file in Directory.EnumerateFiles(Folder).Where(IsSupported)) Load(file);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves a pasted/imported workout into the library and returns its entry.</summary>
    public LibraryEntry Save(ImportResult result, string? sourcePath = null)
    {
        var ext = WorkoutImporter.GetFileExtension(result.Format);
        var path = UniquePath(Slug(result.Workout.Name) + ext);
        if (sourcePath is not null && File.Exists(sourcePath))
            File.Copy(sourcePath, path);
        else
            File.WriteAllText(path, result.RawText, new UTF8Encoding(false));
        return Load(path);
    }

    private LibraryEntry Load(string path)
    {
        LibraryEntry entry;
        try
        {
            var result = WorkoutImporter.ImportFile(path);
            entry = new LibraryEntry(path, result.Workout with { SourcePath = path }, null, File.GetLastWriteTime(path));
        }
        catch (Exception ex)
        {
            // Any unreadable file (bad format, locked, half-written) is listed with its error instead of crashing.
            entry = new LibraryEntry(path, null, ex.Message, File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now);
        }
        lock (_lock) _entries[path] = entry;
        return entry;
    }

    private void Debounce(string path, bool isNew)
    {
        if (!IsSupported(path)) return;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_pending.Remove(path, out var old)) old.Cancel();
            _pending[path] = cts = new CancellationTokenSource();
            isNew |= !_entries.ContainsKey(path);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Writers (an editor, Claude) may still be holding the file: wait, then retry a few times.
                await Task.Delay(400, cts.Token);
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(300, cts.Token);
                    }
                }
                if (!File.Exists(path)) return;
                var entry = Load(path);
                Changed?.Invoke(this, EventArgs.Empty);
                if (isNew && entry.Workout is not null) WorkoutArrived?.Invoke(this, entry);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_lock)
                {
                    if (_pending.TryGetValue(path, out var current) && current == cts) _pending.Remove(path);
                }
            }
        });
    }

    private void Remove(string path)
    {
        bool removed;
        lock (_lock) removed = _entries.Remove(path);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSupported(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return !name.StartsWith('.') && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               WorkoutImporter.SupportedExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    }

    private void SeedSamples()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("samples/", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var file = File.Create(System.IO.Path.Combine(Folder, name["samples/".Length..]));
            stream.CopyTo(file);
        }
    }

    private string UniquePath(string fileName)
    {
        var path = System.IO.Path.Combine(Folder, fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(Folder, $"{stem}-{i}{ext}");
        return path;
    }

    internal static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "workout" : slug[..Math.Min(slug.Length, 60)];
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_lock)
        {
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();
        }
    }
}
