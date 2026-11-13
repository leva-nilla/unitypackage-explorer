using System.IO;
using System.Text;

namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Best-effort file logger for diagnosing surrogate (prevhost) execution. Never throws.</summary>
internal static class PreviewLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _firstWrite = true;

    public static void Write(string message)
    {
        try
        {
            var path = ResolvePath();
            if (path is null) return;
            var summary = string.Empty;
            if (_firstWrite)
            {
                Volatile.Write(ref _firstWrite, false);
                summary = EnvironmentSummary() + Environment.NewLine;
            }
            var line = $"{summary}{DateTime.Now:HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                try
                {
                    if (new FileInfo(path).Length > MaxBytes) File.WriteAllText(path, string.Empty);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the preview handler.
        }
    }

    public static void Write(string message, Exception exception)
    {
        var baseException = exception.GetBaseException();
        var baseText = baseException is null || ReferenceEquals(baseException, exception)
            ? string.Empty
            : $" | base: {baseException.GetType().Name}: {baseException.Message}";
        Write($"{message} :: {exception.GetType().Name}: {exception.Message}{baseText}");
    }

    /// <summary>Environment summary written once per process on the first log call.</summary>
    public static string EnvironmentSummary()
    {
        var integrity = "unknown";
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            integrity = identity.Owner?.ToString() ?? "unknown";
        }
        catch (Exception) { }

        return $"env baseDir={AppContext.BaseDirectory} asm={typeof(PreviewLog).Assembly.Location} "
            + $"owner={integrity} isElevated={Environment.IsPrivilegedProcess}";
    }

    private static string? ResolvePath()
    {
        if (_path is not null) return _path;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(profile, "AppData", "LocalLow", "UnityPackageExplorer", "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityPackageExplorer", "logs"),
        };
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "prevhost.log");
                return _path;
            }
            catch (Exception)
            {
                // Try the next candidate; low-integrity processes may only write to Low locations.
            }
        }

        return null;
    }
}
