using Spectre.Console;
using ListenerSound.Models;
using ListenerSound.Server;
using ListenerSound.Client;

Console.Title = "ListenerSound";

string? mode;

if (args.Length == 0)
{
    AnsiConsole.Write(new FigletText("ListenerSound").Color(Color.Aqua).Centered());
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold]Seleccione el modo de inicio:[/]")
            .AddChoices("Servidor", "Cliente", "Salir"));

    mode = choice switch
    {
        "Servidor" => "server",
        "Cliente"  => "client",
        _          => null
    };

    if (mode == null) return;
}
else
{
    mode = args[0].ToLowerInvariant();
}

if (mode == "server")
{
    var configPath = EnsureServerConfig();
    try
    {
        var config = ConfigLoader.LoadServerConfig(configPath);
        var server = new ServerApp(config, configPath);
        await server.RunAsync();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
    }
}
else if (mode == "client")
{
    var configPath = EnsureClientConfig();
    try
    {
        var config = ConfigLoader.LoadClientConfig(configPath);
        var client = new ClientApp(config, configPath);
        await client.RunAsync();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
    }
}
else
{
    AnsiConsole.MarkupLine($"[red]Error:[/] Modo desconocido '[yellow]{mode}[/]'. Use [cyan]server[/] o [cyan]client[/].");
}

// Busca el archivo de configuración primero junto al ejecutable (carpeta del exe)
// y, si no existe, en el directorio de trabajo actual. Así el exe autocontenido
// puede ejecutarse con doble clic desde cualquier carpeta.
static string ResolveConfigPath(string fileName)
{
    var exePath = Path.Combine(AppContext.BaseDirectory, fileName);
    if (File.Exists(exePath)) return exePath;
    return Path.Combine(Directory.GetCurrentDirectory(), fileName);
}

// Crea el archivo de configuración con valores por defecto si no existe,
// permitiendo que un usuario no-técnico arranque de inmediato sin crear el JSON a mano.
static string EnsureConfigFile(string fileName, string defaultJson)
{
    var path = ResolveConfigPath(fileName);
    if (File.Exists(path)) return path;

    try
    {
        File.WriteAllText(path, defaultJson);
        AnsiConsole.MarkupLine($"[yellow]Creado [cyan]{Path.GetFileName(path)}[/] con valores por defecto.[/]");
        return path;
    }
    catch (UnauthorizedAccessException) { }
    catch (IOException) { }

    var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
    File.WriteAllText(cwdPath, defaultJson);
    AnsiConsole.MarkupLine($"[yellow]Creado [cyan]{fileName}[/] (carpeta actual) con valores por defecto.[/]");
    return cwdPath;
}

static string EnsureServerConfig()
{
    var defaultJson = """
    {
      "Port": 5000,
      "AuthToken": "",
      "AllowedIps": [],
      "AudioFolder": "audio",
      "Clients": [],
      "Schedules": []
    }
    """;
    return EnsureConfigFile("server-config.json", defaultJson);
}

static string EnsureClientConfig()
{
    var defaultJson = """
    {
      "ServerIp": "127.0.0.1",
      "ServerPort": 5000,
      "ClientId": "PC1",
      "AuthToken": "",
      "TriggerKey": "F4",
      "Description": "Cliente de ListenerSound"
    }
    """;
    return EnsureConfigFile("client-config.json", defaultJson);
}
