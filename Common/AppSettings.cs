using System.Text.Json;

namespace ListenerSound.Common;

// Modo de inicio persistido entre ejecuciones (Servidor/Cliente).
// La primera vez que el usuario elige el modo, se guarda aquí; los siguientes
// arranques abren directo en ese modo sin volver a preguntar.
public static class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // Devuelve el modo guardado ("server" / "client") o "" si no está definido.
    public static string GetMode(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettingsFile>(json, JsonOptions);
            return Normalize(settings?.Mode ?? "");
        }
        catch { return ""; }
    }

    // Guarda un modo ("server" / "client"). No lanza si falla el guardado.
    public static void SaveMode(string path, string mode)
    {
        try
        {
            var settings = new AppSettingsFile { Mode = Normalize(mode) };
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static string Normalize(string mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "server" => "server",
            "client" => "client",
            _        => ""
        };

    private sealed class AppSettingsFile
    {
        public string Mode { get; set; } = "";
    }
}