using System.Text.Json;
using System.Text.Json.Serialization;
using AerControl.Protocol;

namespace AerControl.Core;

public sealed class SpraySchedule
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public ScheduleWindow Window { get; set; } = new(9 * 60, 21 * 60);
}

/// <summary>
/// Persisted settings. Missing JSON properties keep these initializer defaults, so adding a
/// setting never resets the others.
/// </summary>
public sealed class AerSettings
{
    /// <summary>BLE address (Windows exposes it, unlike CoreBluetooth).</summary>
    public ulong? DeviceAddress { get; set; }
    public string? DeviceName { get; set; }
    public ScanFilter ScanFilter { get; set; } = ScanFilter.ServiceOrName;
    public string NameFilter { get; set; } = ScanMatcher.DefaultNameFilter;
    /// <summary>0 = stay connected; otherwise disconnect so the phone app can connect.</summary>
    public int IdleDisconnectSeconds { get; set; } = 20;
    public bool AutoReconnect { get; set; } = true;
    public WriteMode WriteMode { get; set; } = WriteMode.Automatic;
    /// <summary>On by default as a safeguard (see protocol/README.md field notes).</summary>
    public bool AutoIncrementMessageNumber { get; set; } = true;
    public uint MessageNumber { get; set; } = SprayPayload.CapturedMessageNumber;
    public SpraySchedule Schedule { get; set; } = new();
    public bool HotkeyEnabled { get; set; }

    public AerSettings Clone() =>
        JsonSerializer.Deserialize<AerSettings>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Atomic JSON file persistence (write temp file, then replace).</summary>
public sealed class JsonFileStore<T>(string path) where T : class, new()
{
    private readonly object _lock = new();
    public string Path { get; } = path;

    public T Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(Path))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Json.Options) ?? new T();
            }
            catch (JsonException) { /* corrupt file: start fresh rather than crash */ }
            return new T();
        }
    }

    public void Save(T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
            File.Move(tmp, Path, overwrite: true);
        }
    }
}

/// <summary>Where settings, history and logs live. The packaged app points this at its LocalFolder.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AerControl");

    public static string SettingsFile => System.IO.Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => System.IO.Path.Combine(DataDirectory, "history.json");
    public static string LogFile => System.IO.Path.Combine(DataDirectory, "logs", "aercontrol.log");
}
