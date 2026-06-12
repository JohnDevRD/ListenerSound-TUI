using System.Text.Json;

namespace ListenerSound.Models;

public record ClientMapping
{
    public string Id { get; set; } = "";
    public string AudioFile { get; set; } = "";
    public string Description { get; set; } = "";
}

public class AudioSchedule
{
    public string Id { get; set; } = "";
    public string AudioFile { get; set; } = "";
    public string Description { get; set; } = "";
    public int IntervalValue { get; set; } = 5;
    public string IntervalUnit { get; set; } = "minutos";
    public bool Enabled { get; set; } = true;
}

public class ServerConfig
{
    public int Port { get; set; } = 5000;
    public string? AudioFolder { get; set; }
    public List<ClientMapping> Clients { get; set; } = [];
    public List<AudioSchedule> Schedules { get; set; } = [];

    public string GetFullAudioPath(ClientMapping mapping)
    {
        var fileName = Path.GetFileName(mapping.AudioFile);
        return string.IsNullOrEmpty(AudioFolder)
            ? mapping.AudioFile
            : Path.Combine(AudioFolder, fileName);
    }

    public string GetFullAudioPathByName(string audioFile)
    {
        var fileName = Path.GetFileName(audioFile);
        return string.IsNullOrEmpty(AudioFolder)
            ? audioFile
            : Path.Combine(AudioFolder, fileName);
    }

    public static TimeSpan GetScheduleInterval(AudioSchedule s) => s.IntervalUnit switch
    {
        "segundos" => TimeSpan.FromSeconds(s.IntervalValue),
        "horas" => TimeSpan.FromHours(s.IntervalValue),
        _ => TimeSpan.FromMinutes(s.IntervalValue)
    };
}

public class ClientConfig
{
    public string ServerIp { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 5000;
    public string ClientId { get; set; } = "";
    public string TriggerKey { get; set; } = "F4";
    public string Description { get; set; } = "";
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ServerConfig LoadServerConfig(string path = "server-config.json")
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Error cargando {path}");
    }

    public static ClientConfig LoadClientConfig(string path = "client-config.json")
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Error cargando {path}");
    }
}
