using System.Diagnostics;

namespace AerControl.Core;

/// <summary>Thread-safe append-only log file with 1 MB rotation; also mirrors to Debug output.</summary>
public sealed class FileLogger
{
    private static FileLogger? _shared;
    public static FileLogger Shared => _shared ??= new FileLogger(AppPaths.LogFile);

    private readonly object _lock = new();
    private const long MaxBytes = 1_000_000;

    public string FilePath { get; }
    public bool EchoToConsole { get; set; }

    public FileLogger(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level} {message}";
        Debug.WriteLine(line);
        if (EchoToConsole) Console.Error.WriteLine(line);
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never take the app down */ }
        }
    }
}
