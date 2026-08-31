using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using Spectre.Console;
using Spectre.Console.Rendering;
using ListenerSound.Models;
using ListenerSound.Common;

namespace ListenerSound.Server;

public class ServerApp
{
    private readonly ServerConfig _config;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ClientState> _clients = [];
    private readonly List<string> _logEntries = [];
    private readonly object _logLock = new();
    private string[] _localIps = [];
    private readonly ConcurrentDictionary<string, DateTime> _lastSchedulePlay = [];
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private WindowsTray? _tray;

    public ServerApp(ServerConfig config, string configPath, string settingsPath)
    {
        _config = config;
        _configPath = configPath;
        _settingsPath = settingsPath;
    }

    public async Task RunAsync()
    {
        _tray = new WindowsTray();
        _tray.ExitRequested += () => _cts.Cancel();
        _tray.Start();

        _localIps = GetLocalIPv4Addresses();

        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start();

        var ipList = string.Join(", ", _localIps.Select(ip => $"[cyan]{ip}[/]"));
        AddLog($"[cyan]Servidor iniciado[/] en puerto [yellow]{_config.Port}[/]");
        AddLog($"IPs locales: {ipList}");
        AddLog($"[cyan]{_config.Clients.Count}[/] cliente(s) configurado(s)");

        var acceptTask = AcceptClientsAsync();
        var scheduleTask = RunSchedulesAsync();
        await ShowTuiAsync();

        _cts.Cancel();
        _tray.Dispose();
        _listener.Stop();
        AddLog("[red]Servidor detenido[/]");
    }

    private static string[] GetLocalIPv4Addresses()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();
        }
        catch
        {
            return ["127.0.0.1"];
        }
    }

    private async Task AcceptClientsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync();
            }
            catch (ObjectDisposedException) { continue; }
            catch (NullReferenceException) { continue; }

            if (tcpClient != null)
                _ = HandleClientAsync(tcpClient);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        var remoteEp = tcpClient.Client.RemoteEndPoint as IPEndPoint;
        var ip = remoteEp?.Address.ToString() ?? "?";

        using (tcpClient)
        using (var reader = new StreamReader(tcpClient.GetStream()))
        using (var writer = new StreamWriter(tcpClient.GetStream()) { AutoFlush = true })
        {
            string? registerLine;
            try { registerLine = await reader.ReadLineAsync(); }
            catch { return; }

            if (registerLine == null || !registerLine.StartsWith(Protocol.RegisterPrefix))
                return;

            var (token, clientId) = Protocol.ParseRegister(registerLine);
            if (clientId == null)
            {
                await writer.WriteLineAsync($"{Protocol.ErrorPrefix}:Registro inválido");
                return;
            }

            // Validación de token (si está definido en server-config.json).
            if (!string.IsNullOrEmpty(_config.AuthToken) && token != _config.AuthToken)
            {
                AddLog($"[red]Conexión rechazada[/]: token inválido desde [yellow]{ip}[/]");
                await writer.WriteLineAsync($"{Protocol.ErrorPrefix}:Token inválido");
                return;
            }

            // Validación de IP permitida (si se configuró allow-list).
            if (_config.AllowedIps.Count > 0 && !_config.AllowedIps.Contains(ip))
            {
                AddLog($"[red]Conexión rechazada[/]: IP [yellow]{ip}[/] no permitida");
                await writer.WriteLineAsync($"{Protocol.ErrorPrefix}:IP no permitida");
                return;
            }

            var mapping = _config.Clients.Find(c => c.Id == clientId);

            if (mapping == null)
            {
                await writer.WriteLineAsync($"{Protocol.ErrorPrefix}:Cliente no configurado");
                return;
            }

            var state = new ClientState
            {
                Id = clientId,
                Ip = ip,
                AudioFile = mapping.AudioFile,
                Status = "[green]Conectado[/]",
                LastTrigger = "—"
            };
            _clients[clientId] = state;

            AddLog($"Cliente '[yellow]{clientId}[/]' conectado desde [cyan]{ip}[/]");
            await writer.WriteLineAsync($"{Protocol.OkPrefix}:{Path.GetFileName(mapping.AudioFile)}");

            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch { break; }

                if (line == null) break;

                if (line == Protocol.TriggerCommand)
                {
                    _ = PlayAudioAsync(mapping, clientId, writer);
                }
                else if (line == Protocol.ByeCommand)
                {
                    break;
                }
            }

            _clients.TryRemove(clientId, out _);
            AddLog($"Cliente '[yellow]{clientId}[/]' [red]desconectado[/]");
        }
    }

    private async Task PlayAudioAsync(ClientMapping mapping, string clientId, StreamWriter writer)
    {
        if (_clients.TryGetValue(clientId, out var state))
        {
            state.Status = "[yellow]⏳ En cola[/]";
            state.LastTrigger = DateTime.Now.ToString("HH:mm:ss");
        }

        var audioName = Path.GetFileName(mapping.AudioFile);
        var fullPath = _config.GetFullAudioPath(mapping);

        try
        {
            await _audioGate.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_clients.TryGetValue(clientId, out state))
            {
                state.Status = "[yellow]▶ Reproduciendo[/]";
            }

            AddLog($"'{clientId}' [yellow]▶[/] [cyan]{audioName}[/]");
            await writer.WriteLineAsync(Protocol.OkPrefix);

            if (!File.Exists(fullPath))
            {
                AddLog($"[red]Error[/]: archivo no encontrado [yellow]{fullPath}[/]");
                return;
            }

            // Silencia/reduce las demás aplicaciones de Windows mientras se reproduce el audio
            using (SystemAudioDucker.DuckOtherApplications())
            {
                using var reader = new AudioFileReader(fullPath);
                using var waveOut = new WaveOutEvent();
                waveOut.Init(reader);
                waveOut.Play();

                while (waveOut.PlaybackState == PlaybackState.Playing && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(50);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog($"[red]Error[/] reproduciendo '{audioName}': {ex.Message}");
        }
        finally
        {
            if (_clients.TryGetValue(clientId, out var s) && s.Status == "[yellow]▶ Reproduciendo[/]")
            {
                s.Status = "[green]Conectado[/]";
            }
            _audioGate.Release();
        }
    }

    private async Task RunSchedulesAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            foreach (var s in _config.Schedules.Where(s => s.Enabled))
            {
                var lastPlay = _lastSchedulePlay.GetValueOrDefault(s.Id, DateTime.MinValue);
                var interval = ServerConfig.GetScheduleInterval(s);

                if (DateTime.Now - lastPlay >= interval)
                {
                    _lastSchedulePlay[s.Id] = DateTime.Now;
                    _ = PlayScheduledAudioAsync(s);
                }
            }
            await Task.Delay(1000);
        }
    }

    private async Task PlayScheduledAudioAsync(AudioSchedule schedule)
    {
        var audioName = Path.GetFileName(schedule.AudioFile);
        var fullPath = _config.GetFullAudioPathByName(schedule.AudioFile);

        if (!File.Exists(fullPath))
        {
            AddLog($"[red]Error[/] '{schedule.Description}': archivo no encontrado [yellow]{fullPath}[/]");
            schedule.Enabled = false;
            return;
        }

        try
        {
            await _audioGate.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            AddLog($"⏰ [cyan]{schedule.Description}[/]");

            using (SystemAudioDucker.DuckOtherApplications())
            {
                using var reader = new AudioFileReader(fullPath);
                using var waveOut = new WaveOutEvent();
                waveOut.Init(reader);
                waveOut.Play();

                while (waveOut.PlaybackState == PlaybackState.Playing && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(50);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Split('\n')[0].Trim();
            AddLog($"[red]Error[/] '{schedule.Description}': {msg}");
            schedule.Enabled = false;
        }
        finally
        {
            _audioGate.Release();
        }
    }

    private void AddLog(string message)
    {
        var entry = $"[dim]{DateTime.Now:HH:mm:ss}[/] {message}";
        lock (_logLock)
        {
            _logEntries.Add(entry);
            if (_logEntries.Count > 100)
                _logEntries.RemoveAt(0);
        }
        LogFile.Append(LogFile.StripMarkup($"{DateTime.Now:HH:mm:ss} {message}"));
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
                                if (key.Key == ConsoleKey.Escape)
                                {
                                    _cts.Cancel();
                                    break;
                                }
                                if (key.Key == ConsoleKey.C)
                                {
                                    break;
                                }
                            }
                        }
                        catch (InvalidOperationException) { }

                        // Si la consola está oculta en la bandeja, no intentar redibujar (evita errores).
                        if (_tray != null && !_tray.IsConsoleVisible)
                        {
                            await Task.Delay(200);
                            continue;
                        }

                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                        await Task.Delay(200);
                    }
                });

            if (_cts.IsCancellationRequested) break;

            await ShowConfigEditorAsync();
            _clients.Clear();
            AddLog("[yellow]Configuración actualizada[/]");
            _listener?.Stop();
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start();
        }
    }

    private async Task ShowConfigEditorAsync()
    {
        var choices = new[] { "Agregar cliente", "Editar cliente", "Eliminar cliente", "Agregar programación", "Editar programación", "Eliminar programación", "Cambiar carpeta de audios", "Cambiar puerto", "Cambiar token de acceso", "Cambiar IPs permitidas", "Cambiar modo de inicio (Servidor/Cliente)", "Guardar y volver", "Salir sin guardar" };

        while (true)
        {
            AnsiConsole.Clear();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold cyan]Configuración del Servidor[/]")
                    .PageSize(10)
                    .AddChoices(choices));

            switch (choice)
            {
                case "Agregar cliente":
                    await AddClientAsync();
                    break;
                case "Editar cliente":
                    await EditClientAsync();
                    break;
                case "Eliminar cliente":
                    await DeleteClientAsync();
                    break;
                case "Agregar programación":
                    await AddScheduleAsync();
                    break;
                case "Editar programación":
                    await EditScheduleAsync();
                    break;
                case "Eliminar programación":
                    await DeleteScheduleAsync();
                    break;
                case "Cambiar carpeta de audios":
                    await ChangeAudioFolderAsync();
                    break;
                case "Cambiar puerto":
                    await ChangePortAsync();
                    break;
                case "Cambiar token de acceso":
                    await ChangeAuthTokenAsync();
                    break;
                case "Cambiar IPs permitidas":
                    await ChangeAllowedIpsAsync();
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

    private async Task AddClientAsync()
    {
        var id = AnsiConsole.Ask<string>("[bold]ID del cliente[/] (ej: PC16):");
        if (_config.Clients.Any(c => c.Id == id))
        {
            AnsiConsole.MarkupLine("[red]Ya existe un cliente con ese ID[/]");
            await WaitForKeyAsync();
            return;
        }

        var folderHint = string.IsNullOrEmpty(_config.AudioFolder) ? "" : $" (en {_config.AudioFolder})";
        var audioFile = AnsiConsole.Ask<string>($"[bold]Nombre del archivo de audio[/]{folderHint}:");
        var description = AnsiConsole.Ask<string>("[bold]Descripción[/] (opcional):", "");

        _config.Clients.Add(new ClientMapping
        {
            Id = id,
            AudioFile = audioFile,
            Description = description
        });

        AnsiConsole.MarkupLine($"[green]✓ Cliente '{id}' agregado[/]");
        await WaitForKeyAsync();
    }

    private async Task EditClientAsync()
    {
        if (_config.Clients.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay clientes configurados[/]");
            await WaitForKeyAsync();
            return;
        }

        var ids = _config.Clients.Select(c => $"{c.Id} — {c.Description} ({Path.GetFileName(c.AudioFile)})").ToList();
        ids.Add("Cancelar");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Seleccione cliente a editar[/]")
                .PageSize(10)
                .AddChoices(ids));

        if (selected == "Cancelar") return;

        var index = ids.IndexOf(selected);
        var client = _config.Clients[index];

        var folderHint = string.IsNullOrEmpty(_config.AudioFolder) ? "" : $" en {_config.AudioFolder}";
        var newAudio = AnsiConsole.Ask<string>($"[bold]Archivo de audio[/]{folderHint} ({client.AudioFile}):", client.AudioFile);
        var newDesc = AnsiConsole.Ask<string>($"[bold]Descripción[/] ({client.Description}):", client.Description);

        _config.Clients[index] = client with { AudioFile = newAudio, Description = newDesc };
        AnsiConsole.MarkupLine($"[green]✓ Cliente '{client.Id}' actualizado[/]");
        await WaitForKeyAsync();
    }

    private async Task DeleteClientAsync()
    {
        if (_config.Clients.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay clientes para eliminar[/]");
            await WaitForKeyAsync();
            return;
        }

        var ids = _config.Clients.Select(c => $"{c.Id} — {c.Description}").ToList();
        ids.Add("Cancelar");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Seleccione cliente a eliminar[/]")
                .PageSize(10)
                .AddChoices(ids));

        if (selected == "Cancelar") return;

        var index = ids.IndexOf(selected);
        var client = _config.Clients[index];

        if (AnsiConsole.Confirm($"¿Eliminar cliente '{client.Id}'?"))
        {
            _config.Clients.RemoveAt(index);
            AnsiConsole.MarkupLine($"[red]✓ Cliente '{client.Id}' eliminado[/]");
        }
        await WaitForKeyAsync();
    }

    private async Task ChangeAudioFolderAsync()
    {
        var current = _config.AudioFolder ?? "(sin carpeta base)";
        var folder = AnsiConsole.Ask<string>($"[bold]Ruta de la carpeta de audios[/] ({current}):", _config.AudioFolder ?? "");
        _config.AudioFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        AnsiConsole.MarkupLine($"[green]✓ Carpeta de audios cambiada a '{_config.AudioFolder ?? "(sin carpeta)"}'[/]");
        await WaitForKeyAsync();
    }

    private async Task ChangePortAsync()
    {
        var port = AnsiConsole.Ask<int>("[bold]Puerto[/]:", _config.Port);
        _config.Port = port;
        AnsiConsole.MarkupLine($"[green]✓ Puerto cambiado a {port}[/]");
        await WaitForKeyAsync();
    }

    private async Task ChangeAuthTokenAsync()
    {
        // Si ya hay un token configurado, ofrecer cambiar o desactivar de forma explícita
        // (para que el usuario pueda vaciarlo sin tocar el JSON).
        if (!string.IsNullOrEmpty(_config.AuthToken))
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]Token actual: [yellow]{Markup.Escape(_config.AuthToken)}[/][/]")
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

    private async Task ChangeAllowedIpsAsync()
    {
        var hint = _config.AllowedIps.Count > 0 ? string.Join(", ", _config.AllowedIps) : "(vacío = permitir todas)";
        var input = AnsiConsole.Ask<string>("[bold]IPs permitidas[/] (separadas por coma):", hint);
        _config.AllowedIps = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        AnsiConsole.MarkupLine(_config.AllowedIps.Count == 0
            ? "[yellow]Se permiten todas las IPs[/]"
            : $"[green]✓ IPs permitidas actualizadas[/]");
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
    private async Task AddScheduleAsync()
    {
        var id = AnsiConsole.Ask<string>("[bold]ID de la programación[/] (ej: S1):");
        if (_config.Schedules.Any(s => s.Id == id))
        {
            AnsiConsole.MarkupLine("[red]Ya existe una programación con ese ID[/]");
            await WaitForKeyAsync();
            return;
        }

        var folderHint = string.IsNullOrEmpty(_config.AudioFolder) ? "" : $" (en {_config.AudioFolder})";
        var audioFile = AnsiConsole.Ask<string>($"[bold]Nombre del archivo de audio[/]{folderHint}:");
        var description = AnsiConsole.Ask<string>("[bold]Descripción[/]:");
        var intervalValue = AnsiConsole.Ask<int>("[bold]Intervalo[/] (cada cuánto):", 5);
        var intervalUnit = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Unidad de tiempo[/]")
                .AddChoices("segundos", "minutos", "horas"));

        _config.Schedules.Add(new AudioSchedule
        {
            Id = id,
            AudioFile = audioFile,
            Description = description,
            IntervalValue = intervalValue,
            IntervalUnit = intervalUnit,
            Enabled = true
        });

        AnsiConsole.MarkupLine($"[green]✓ Programación '{id}' agregada[/]");
        await WaitForKeyAsync();
    }

    private async Task EditScheduleAsync()
    {
        if (_config.Schedules.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay programaciones configuradas[/]");
            await WaitForKeyAsync();
            return;
        }

        var list = _config.Schedules.Select(s => $"{s.Id} — {s.Description} (cada {s.IntervalValue} {s.IntervalUnit})").ToList();
        list.Add("Cancelar");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Seleccione programación a editar[/]")
                .PageSize(10)
                .AddChoices(list));

        if (selected == "Cancelar") return;

        var index = list.IndexOf(selected);
        var sched = _config.Schedules[index];

        var folderHint = string.IsNullOrEmpty(_config.AudioFolder) ? "" : $" en {_config.AudioFolder}";
        var newAudio = AnsiConsole.Ask<string>($"[bold]Archivo de audio[/]{folderHint} ({sched.AudioFile}):", sched.AudioFile);
        var newDesc = AnsiConsole.Ask<string>($"[bold]Descripción[/] ({sched.Description}):", sched.Description);
        var newInterval = AnsiConsole.Ask<int>($"[bold]Intervalo[/] ({sched.IntervalValue}):", sched.IntervalValue);
        var newUnit = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Unidad de tiempo[/]")
                .AddChoices("segundos", "minutos", "horas"));

        _config.Schedules[index] = new AudioSchedule
        {
            Id = sched.Id,
            AudioFile = newAudio,
            Description = newDesc,
            IntervalValue = newInterval,
            IntervalUnit = newUnit,
            Enabled = sched.Enabled
        };

        AnsiConsole.MarkupLine($"[green]✓ Programación '{sched.Id}' actualizada[/]");
        await WaitForKeyAsync();
    }

    private async Task DeleteScheduleAsync()
    {
        if (_config.Schedules.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay programaciones para eliminar[/]");
            await WaitForKeyAsync();
            return;
        }

        var list = _config.Schedules.Select(s => $"{s.Id} — {s.Description}").ToList();
        list.Add("Cancelar");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Seleccione programación a eliminar[/]")
                .PageSize(10)
                .AddChoices(list));

        if (selected == "Cancelar") return;

        var index = list.IndexOf(selected);
        var sched = _config.Schedules[index];

        if (AnsiConsole.Confirm($"¿Eliminar programación '{sched.Id}'?"))
        {
            _config.Schedules.RemoveAt(index);
            _lastSchedulePlay.TryRemove(sched.Id, out _);
            AnsiConsole.MarkupLine($"[red]✓ Programación '{sched.Id}' eliminada[/]");
        }
        await WaitForKeyAsync();
    }

    private async Task SaveConfigAsync()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_configPath, json);
        AnsiConsole.MarkupLine("[green]✓ Configuración guardada[/]");
        await Task.Delay(500);
    }

    private static async Task WaitForKeyAsync()
    {
        AnsiConsole.Markup("\n[dim]Presione cualquier tecla para continuar...[/]");
        while (!Console.KeyAvailable) await Task.Delay(50);
        Console.ReadKey(true);
    }

    private Table BuildClientTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("IP")
            .AddColumn("Estado")
            .AddColumn("Audio")
            .AddColumn("Último Disparo");

        foreach (var mapping in _config.Clients)
        {
            _clients.TryGetValue(mapping.Id, out var state);
            var ip = state?.Ip ?? "—";
            var status = state?.Status ?? "[red]Desconectado[/]";
            var lastTrigger = state?.LastTrigger ?? "—";
            var audioName = Path.GetFileName(mapping.AudioFile);

            table.AddRow(mapping.Id, ip, status, audioName, lastTrigger);
        }

        return table;
    }

    private Panel BuildLogPanel()
    {
        string logText;
        lock (_logLock)
        {
            logText = string.Join("\n", _logEntries.TakeLast(5));
        }

        return new Panel(new Markup(logText))
        {
            Header = new PanelHeader("Log"),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
    }

    private Panel? BuildSchedulePanel()
    {
        if (_config.Schedules.Count == 0) return null;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Audio")
            .AddColumn("Intervalo")
            .AddColumn("Próximo")
            .AddColumn("Estado");

        foreach (var s in _config.Schedules)
        {
            var lastPlay = _lastSchedulePlay.GetValueOrDefault(s.Id, DateTime.MinValue);
            var interval = ServerConfig.GetScheduleInterval(s);
            var next = lastPlay + interval;
            var nextText = s.Enabled
                ? (next <= DateTime.Now ? "[yellow]Ya[/]" : next.ToString("HH:mm:ss"))
                : "[red]—[/]";
            var status = s.Enabled ? "[green]Activo[/]" : "[red]Inactivo[/]";
            var audioName = Path.GetFileName(s.AudioFile);
            var intervalText = $"Cada {s.IntervalValue} {s.IntervalUnit}";

            table.AddRow(audioName, intervalText, nextText, status);
        }

        return new Panel(table)
        {
            Header = new PanelHeader("Programación"),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
    }

    private Rows BuildLayout()
    {
        var items = new List<IRenderable>
        {
            new FigletText("ListenerSound") { Color = Color.Aqua }.Centered(),
            new Rule(),
            new Columns(BuildClientTable(), BuildLogPanel()),
        };

        var schedPanel = BuildSchedulePanel();
        if (schedPanel != null)
        {
            items.Add(new Rule());
            items.Add(schedPanel);
        }

        items.Add(new Rule());
        items.Add(new Markup("[dim]C = Config  |  ESC = Salir\nMinimizar o cerrar oculta la ventana a la bandeja. Doble clic en el icono para volver.[/]").Centered());

        return new Rows([.. items]);
    }
}

public class ClientState
{
    public string Id { get; set; } = "";
    public string Ip { get; set; } = "";
    public string AudioFile { get; set; } = "";
    public string Status { get; set; } = "";
    public string LastTrigger { get; set; } = "";
}
