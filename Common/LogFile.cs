using System.Text.RegularExpressions;

namespace ListenerSound.Common;

// Log persistente a archivo (listenersound.log) con rotación automática.
// Escribe junto al ejecutable (AppContext.BaseDirectory) y, si no tiene permiso,
// en el directorio de trabajo actual. Nunca lanza excepciones que rompan la app.
public static partial class LogFile
{
    private static readonly object Sync = new();
    private const long MaxBytes = 1_048_576; // 1 MB
    private static string? _resolvedPath;

    public static void Append(string message)
    {
        var path = ResolvePath();
        if (path == null) return;

        try
        {
            lock (Sync)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                    Rotate(path);

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* el logging nunca debe romper la ejecución */ }
    }

    // Quita las etiquetas de markup de Spectre ([color]...[/]) para dejar texto plano en el log.
    public static string StripMarkup(string text)
        => MarkupTagRegex().Replace(text, "");

    private static string? ResolvePath()
    {
        if (_resolvedPath != null) return _resolvedPath;

        var primary = Path.Combine(AppContext.BaseDirectory, "listenersound.log");
        try
        {
            using var fs = File.Open(primary, FileMode.OpenOrCreate, FileAccess.Write);
            _resolvedPath = primary;
            return _resolvedPath;
        }
        catch { }

        var fallback = Path.Combine(Directory.GetCurrentDirectory(), "listenersound.log");
        try
        {
            using var fs = File.Open(fallback, FileMode.OpenOrCreate, FileAccess.Write);
            _resolvedPath = fallback;
            return _resolvedPath;
        }
        catch { return null; }
    }

    private static void Rotate(string path)
    {
        var old = path + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(path, old);
    }

    [GeneratedRegex(@"\[/\]|\[([a-z][a-z0-9_]*)\]", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex MarkupTagRegex();
}