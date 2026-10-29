using System.Text;

namespace StreamMachine.Core;

/// <summary>
/// Structured, thread-safe, rotating file log. One line per event: time, level, area, message, then key=value fields.
/// Rotates at <see cref="MaxBytes"/>, keeping <see cref="Keep"/> old files (name.1.log is the newest old one).
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;
    public static long MaxBytes { get; set; } = 1024 * 1024;
    public static int Keep { get; set; } = 4;
    public static string? FilePath => _path;

    /// <summary>Also raised for every line, for the overlay's health page and tests.</summary>
    public static event Action<string>? Line;

    public static void Init(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, name + ".log");
    }

    public static void Info(string area, string message, params (string Key, object? Value)[] fields) => Write("INFO", area, message, fields);
    public static void Warn(string area, string message, params (string Key, object? Value)[] fields) => Write("WARN", area, message, fields);
    public static void Error(string area, string message, Exception? ex = null, params (string Key, object? Value)[] fields)
    {
        if (ex != null) fields = [.. fields, ("error", ex.GetType().Name + ": " + ex.Message)];
        Write("ERROR", area, message, fields);
    }

    internal static string Format(DateTime time, string level, string area, string message, (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder(128);
        sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level.PadRight(5)).Append(' ')
          .Append('[').Append(area).Append("] ").Append(message);
        foreach (var (k, v) in fields)
        {
            var s = v?.ToString() ?? "null";
            sb.Append(' ').Append(k).Append('=');
            if (s.Length == 0 || s.Contains(' ') || s.Contains('"')) sb.Append('"').Append(s.Replace("\"", "'")).Append('"');
            else sb.Append(s);
        }
        return sb.Replace('\r', ' ').Replace('\n', ' ').ToString();
    }

    static void Write(string level, string area, string message, (string Key, object? Value)[] fields)
    {
        var line = Format(DateTime.Now, level, area, message, fields);
        try { Line?.Invoke(line); } catch { }
        if (_path == null) return;
        lock (Gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* logging must never take the app down */ }
        }
    }

    static void Rotate()
    {
        var fi = new FileInfo(_path!);
        if (!fi.Exists || fi.Length < MaxBytes) return;
        var dir = fi.DirectoryName!;
        var stem = Path.GetFileNameWithoutExtension(_path);
        for (int i = Keep; i >= 1; i--)
        {
            var older = Path.Combine(dir, $"{stem}.{i}.log");
            if (i == Keep) { if (File.Exists(older)) File.Delete(older); continue; }
            if (File.Exists(older)) File.Move(older, Path.Combine(dir, $"{stem}.{i + 1}.log"), true);
        }
        File.Move(_path!, Path.Combine(dir, $"{stem}.1.log"), true);
    }

    /// <summary>The last <paramref name="count"/> lines of the current log file.</summary>
    public static IReadOnlyList<string> Tail(int count, string? path = null)
    {
        path ??= _path;
        if (path == null || !File.Exists(path)) return [];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - 64 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
        }
        catch { return []; }
    }
}
