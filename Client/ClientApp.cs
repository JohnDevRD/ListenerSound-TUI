using System.Net.Sockets;
using Spectre.Console;
using ListenerSound.Models;
using ListenerSound.Common;

namespace ListenerSound.Client;

public class ClientApp
{
    private readonly ClientConfig _config;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private ConsoleKey _triggerKey;
    private TcpClient? _tcpClient;
    private StreamWriter? _writer;
    private CancellationTokenSource _cts = new();

    private string _status = "[yellow]Conectando...[/]";
    private string _assignedAudio = "—";
    private string _lastEvent = "—";
    private bool _isConnected;

    public ClientApp(ClientConfig config, string configPath, string settingsPath)
    {
        _config = config;
        _configPath = configPath;
        _settingsPath = settingsPath;
        _triggerKey = Enum.TryParse<ConsoleKey>(config.TriggerKey, true, out var key)
            ? key
            : ConsoleKey.F4;
    }

    public async Task RunAsync()
    {
        _ = ConnectWithRetryAsync();

        await ShowTuiAsync();

        _cts.Cancel();
        Disconnect();
    }

    private async Task ConnectWithRetryAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Disconnect();
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_config.ServerIp, _config.ServerPort);

                var stream = _tcpClient.GetStream();
                _writer = new StreamWriter(stream) { AutoFlush = true };
                var reader = new StreamReader(stream);

                await _writer.WriteLineAsync(Protocol.BuildRegister(_config.AuthToken, _config.ClientId));
                var response = await reader.ReadLineAsync();

                if (response == null || response.StartsWith(Protocol.ErrorPrefix))
                {
                    _status = $"[red]Error: {response?[6..] ?? "Sin respuesta"}[/]";
                    _tcpClient.Close();
                    await Task.Delay(3000);
                    continue;
                }

                _assignedAudio = response[3..];
                _isConnected = true;
                _status = "[green]Conectado[/]";
                _lastEvent = "[green]Listo[/]";
                LogFile.Append($"Cliente '{_config.ClientId}' conectado. Audio asignado: {response[3..]}");

                _ = ReadLoopAsync(reader);

                return;
            }
            catch (Exception ex)
            {
                _status = $"[red]Sin conexión: {ex.Message}[/]";
                _isConnected = false;
                LogFile.Append($"Error de conexión: {ex.Message}");
                await Task.Delay(3000);
            }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        try
        {
            while (!_cts.IsCancellationRequested && _tcpClient?.Connected == true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                if (line == Protocol.OkPrefix)
                    _lastEvent = "[green]✓ Audio disparado[/]";
                else if (line.StartsWith(Protocol.ErrorPrefix))
                    _lastEvent = $"[red]✗ {line[6..]}[/]";
            }
        }
        catch { }

        _isConnected = false;
        _status = "[yellow]Reconectando...[/]";
    }

    private async Task SendTriggerAsync()
    {
        if (!_isConnected || _writer == null) return;

        try
        {
            await _writer.WriteLineAsync(Protocol.TriggerCommand);
            _lastEvent = $"[yellow]▶ Disparado ({DateTime.Now:HH:mm:ss})[/]";
            LogFile.Append($"TRIGGER enviado ({_config.ClientId})");
        }
        catch
        {
            _isConnected = false;
            _status = "[red]Error de conexión[/]";
        }
    }

    private void Disconnect()
    {
        try
        {
            _writer?.WriteLineAsync(Protocol.ByeCommand).ConfigureAwait(false);
            _tcpClient?.Close();
        }
        catch { }
        _writer = null;
        _tcpClient = null;
        _isConnected = false;
    }

    private async Task ShowTuiAsync()
    {
        AnsiConsole.Clear();

        while (!_cts.IsCancellationRequested)
        {
            await AnsiConsole.Live(BuildLayout())
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        try
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                if (key.Key == _triggerKey)
                                    await SendTriggerAsync();
                                else if (key.Key == ConsoleKey.Escape)
                                    _cts.Cancel();
                                else if (key.Key == ConsoleKey.C)
                                    break;
                            }
                        }
                        catch (InvalidOperationException) { }

                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                        await Task.Delay(100);
                    }
                });

            if (_cts.IsCancellationRequested) break;

            await ShowConfigEditorAsync();
        }
    }

    private async Task ShowConfigEditorAsync()
    {
        var choices = new[] { "Cambiar IP del servidor", "Cambiar puerto", "Cambiar ID de cliente", "Cambiar token de acceso", "Cambiar tecla de disparo", "Cambiar modo de inicio (Servidor/Cliente)", "Guardar y volver", "Salir sin guardar" };

        while (true)
        {
            AnsiConsole.Clear();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold cyan]Configuración del Cliente[/]")
                    .PageSize(10)
                    .AddChoices(choices));

            switch (choice)
            {
                case "Cambiar IP del servidor":
                    var ip = AnsiConsole.Ask<string>("[bold]IP del servidor[/]:", _config.ServerIp);
                    _config.ServerIp = ip;
                    AnsiConsole.MarkupLine($"[green]✓ IP cambiada a {ip}[/]");
                    await WaitForKeyAsync();
                    break;
                case "Cambiar puerto":
                    var port = AnsiConsole.Ask<int>("[bold]Puerto[/]:", _config.ServerPort);
                    _config.ServerPort = port;
                    AnsiConsole.MarkupLine($"[green]✓ Puerto cambiado a {port}[/]");
                    await WaitForKeyAsync();
                    break;
                case "Cambiar ID de cliente":
                    var id = AnsiConsole.Ask<string>("[bold]ID del cliente[/]:", _config.ClientId);
                    _config.ClientId = id;
                    AnsiConsole.MarkupLine($"[green]✓ ID cambiado a '{id}'[/]");
                    await WaitForKeyAsync();
                    break;
                case "Cambiar token de acceso":
                    await ChangeAuthTokenAsync();
                    break;
                case "Cambiar tecla de disparo":
                    AnsiConsole.MarkupLine("\n[bold]Presione la tecla que desea usar para disparar...[/]");
                    while (!Console.KeyAvailable) await Task.Delay(50);
                    var keyInfo = Console.ReadKey(true);
                    _config.TriggerKey = keyInfo.Key.ToString();
                    _triggerKey = keyInfo.Key;
                    AnsiConsole.MarkupLine($"[green]✓ Tecla cambiada a [yellow]{keyInfo.Key}[/][/]");
                    await WaitForKeyAsync();
                    break;
case "Cambiar modo de inicio (Servidor/Cliente)":
                    await ChangeStartModeAsync();
                    break;
                case "Guardar y volver":
                    await SaveConfigAsync();
                    return;
                case "Salir sin guardar":
                    return;
            }
        }
    }

    private async Task ChangeAuthTokenAsync()
    {
        if (!string.IsNullOrEmpty(_config.AuthToken))
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Token actual configurado[/]")
                    .AddChoices("Cambiar token", "Desactivar autenticación (vaciar token)", "Cancelar"));

            if (action == "Cancelar") return;

            if (action.StartsWith("Desactivar"))
            {
                _config.AuthToken = "";
                AnsiConsole.MarkupLine("[yellow]Auth desactivada (token vacío)[/]");
                await WaitForKeyAsync();
                return;
            }
        }

        var token = AnsiConsole.Ask<string>("[bold]Nuevo token de acceso[/] (Enter sin valor = cancelar):");
        if (token.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Acción cancelada, token sin cambios[/]");
            await WaitForKeyAsync();
            return;
        }

        _config.AuthToken = token;
        AnsiConsole.MarkupLine("[green]✓ Token de acceso actualizado[/]");
        await WaitForKeyAsync();
    }

private async Task ChangeStartModeAsync()
    {
        var currentMode = AppSettings.GetMode(_settingsPath);
        var currentLabel = currentMode == "client" ? "Cliente" : "Servidor";

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]Modo de inicio actual: [cyan]{currentLabel}[/][/]")
                .AddChoices("Cambiar a Servidor", "Cambiar a Cliente", "Cancelar"));

        if (action == "Cancelar") return;

        var newMode = action == "Cambiar a Servidor" ? "server" : "client";
        AppSettings.SaveMode(_settingsPath, newMode);

        var newLabel = action == "Cambiar a Servidor" ? "Servidor" : "Cliente";
        AnsiConsole.MarkupLine($"[green]✓ Modo de inicio cambiado a [cyan]{newLabel}[/][/]");
        AnsiConsole.MarkupLine("[yellow]Reinicie ListenerSound para aplicar el cambio.[/]");
        await WaitForKeyAsync();
    }
    private async Task SaveConfigAsync()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_configPath, json);
        AnsiConsole.MarkupLine("[green]✓ Configuración guardada[/]");
        await Task.Delay(500);
        _isConnected = false;
        Disconnect();
    }

    private static async Task WaitForKeyAsync()
    {
        AnsiConsole.Markup("\n[dim]Presione cualquier tecla para continuar...[/]");
        while (!Console.KeyAvailable) await Task.Delay(50);
        Console.ReadKey(true);
    }

    private Panel BuildLayout()
    {
        var content = new Markup($@"
[bold]Cliente:[/] {_config.ClientId}
[bold]Servidor:[/] {_config.ServerIp}:{_config.ServerPort}
[bold]Estado:[/] {_status}
[bold]Audio Asignado:[/] {_assignedAudio}
[bold]Tecla Disparo:[/] {_triggerKey}
[bold]Último Evento:[/] {_lastEvent}

[dim]Presione [yellow]{_triggerKey}[/] para disparar
Presione [yellow]C[/] Config  |  [yellow]ESC[/] Salir[/]
");

        return new Panel(Align.Center(content, VerticalAlignment.Middle))
        {
            Header = new PanelHeader($"[yellow]{_config.ClientId}[/]"),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
    }
}
