using Spectre.Console;
using ListenerSound.Models;
using ListenerSound.Server;
using ListenerSound.Client;

Console.Title = "ListenerSound";

if (args.Length == 0)
{
    AnsiConsole.Write(new FigletText("ListenerSound").Color(Color.Aqua).Centered());
    AnsiConsole.MarkupLine("\n[bold]Uso:[/]");
    AnsiConsole.MarkupLine("  [cyan]ListenerSound server[/]   — Inicia el servidor de audio");
    AnsiConsole.MarkupLine("  [cyan]ListenerSound client[/]   — Inicia el cliente");
    return;
}

var mode = args[0].ToLowerInvariant();

if (mode == "server")
{
    if (!File.Exists("server-config.json"))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] No se encuentra [yellow]server-config.json[/]");
        return;
    }

    try
    {
        var config = ConfigLoader.LoadServerConfig();
        var server = new ServerApp(config);
        await server.RunAsync();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
    }
}
else if (mode == "client")
{
    if (!File.Exists("client-config.json"))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] No se encuentra [yellow]client-config.json[/]");
        return;
    }

    try
    {
        var config = ConfigLoader.LoadClientConfig();
        var client = new ClientApp(config);
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
