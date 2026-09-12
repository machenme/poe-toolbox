using System.IO;
using System.Text;
using PoEToolbox.Sdk;

namespace PoEToolbox.Shared;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Simple file logger: daily rolling, 7-day retention.
/// </summary>
public sealed class FileLogger : IDisposable, ILogger
{
    private const int RetainDays = 7;
    private static readonly object Sync = new();
    private static int _sessionBannerWritten;

    /// <summary>
    /// Shared process-wide logger. Cheap to use anywhere:
    /// one instance, one append handle, daily file.
    /// </summary>
    public static FileLogger App { get; } = new();

    /// <summary>
    /// Fired for every entry written through any logger instance
    /// (including <see cref="WriteCritical"/>). Lets UI surfaces mirror
    /// the file log without touching individual modules.
    /// </summary>
    public static event Action<LogLevel, string, Exception?>? EntryLogged;

    private readonly StreamWriter? _writer;
    private bool _disposed;

    public FileLogger(string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(ConfigService.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            CleanupOldLogs(directory);

            var fileName = $"poe-toolbox-{DateTime.Now:yyyyMMdd}.log";
            var filePath = Path.Combine(directory, fileName);
            _writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch { _writer = null; }
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (_disposed || _writer == null) return;
        try
        {
            lock (Sync)
            {
                // First line every process writes is a session marker, so a
                // daily log can be split into per-run sections when debugging.
                if (Interlocked.Exchange(ref _sessionBannerWritten, 1) == 0)
                {
                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";
                    _writer.WriteLine();
                    _writer.WriteLine($"========== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} | v{version} | {Environment.OSVersion.VersionString} ==========");
                }
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                _writer.WriteLine(line);
            }
        }
        catch { }
        try { EntryLogged?.Invoke(level, message, ex); }
        catch { }
    }

    public void Debug(string msg) => Log(LogLevel.Debug, msg);
    public void Info(string msg) => Log(LogLevel.Info, msg);
    public void Warn(string msg) => Log(LogLevel.Warn, msg);
    public void Warn(string msg, Exception? ex) => Log(LogLevel.Warn, msg, ex);
    public void Error(string msg, Exception? ex = null) => Log(LogLevel.Error, msg, ex);

    public static void WriteCritical(string message, Exception? ex = null)
    {
        try
        {
            using var logger = new FileLogger();
            logger.Error(message, ex);
        }
        catch { }
    }

    private static void CleanupOldLogs(string directory)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var file in Directory.EnumerateFiles(directory, "poe-toolbox-*.log"))
            {
                try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
    }
}
