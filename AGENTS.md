# AGENTS.md

## What this is

Single-project .NET 10 console app (`ListenerSound.csproj`, no `.sln`). One binary, two modes. Mode is chosen by first CLI arg (temporary override, never saved) or, when launched with no args, by an interactive Spectre menu (`Servidor` / `Cliente` / `Salir`) on **first run only**. The chosen mode is persisted to `app-settings.json` (`{ "Mode": "server" | "client" }`, resolved next to the exe or CWD) via `Common/AppSettings.cs`, so later double-click launches boot **directly into the saved mode** without asking. The start mode can be changed from either TUI's config editor (`C` → "Cambiar modo de inicio (Servidor/Cliente)"), which saves it and prompts the user to restart to apply; `ServerApp`/`ClientApp` receive the settings path to do so.

- `server` — `TcpListener`, plays audio assigned per client via NAudio, plus time-based scheduled playback.
- `client` — connects to the server, sends a trigger when its configured key is pressed.

TUI rendered with Spectre.Console. Runtime is Windows-only (NAudio `WaveOutEvent`), but builds anywhere with the .NET 10 SDK.

## Build & run

- `dotnet build` — targets `net10.0`.
- **Restore fails with NU1100 on this machine even though the network is fine**: no NuGet source is configured (`dotnet nuget list source` returns none). Per-command workaround:
  `dotnet build --source https://api.nuget.org/v3/index.json`
  Permanent fix: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`
- Run from the repo root: `dotnet run -- server` / `dotnet run -- client`.
- Self-contained single-file publish (the "instalable") is configured in the `.csproj` (`win-x64`, `SelfContained=true`, `PublishSingleFile=true`). Command:
  `dotnet publish -c Release --source https://api.nuget.org/v3/index.json` → output `bin/Release/net10.0/win-x64/publish/ListenerSound.exe` (one `.exe`, no .NET runtime needed).
- App icon: `assets\megaphone.ico` (multi-size, generated from `assets\megaphone.png`) is embedded via `<ApplicationIcon>` in the `.csproj`. Keep the `.csproj` TFM at plain `net10.0` (not `net10.0-windows`) — the icon still embeds and it avoids pulling in WindowsDesktop libs that inflate the self-contained binary.
- Config resolution: each config file is looked up **first next to the exe** (`AppContext.BaseDirectory`), then in the current working directory. If neither exists, a default config is created (bootstrap in `Program.cs`'s `EnsureConfigFile`). The resolved path is threaded into `ServerApp`/`ClientApp` so saving from the editor writes back to the same file.
- Verification is `dotnet build` plus manual runs. **No tests, no CI, no lint/format tooling exists** — don't invent test commands.

## Layout (all of the source is 6 files)

- `Program.cs` — mode selection (arg override, interactive first-run menu, or persisted mode from `app-settings.json`), config path resolution + default-config bootstrap, top-level error handling.
- `Server/ServerApp.cs` — accept loop, client registry, NAudio playback, scheduler, server TUI + interactive config editor. Saving from the editor rewrites `server-config.json`, clears connected clients, and restarts the listener.
- `Client/ClientApp.cs` — connect-with-retry (3 s backoff), key-trigger sender, client TUI + config editor.
- `Common/Protocol.cs` — newline-delimited TCP text protocol (`REGISTER:<token>:<id>`, `TRIGGER`, `BYE`, `OK`, `ERROR:<msg>`). Shared by both sides; changing a constant silently breaks both ends together. Auth token is optional: empty token means no auth; if the server sets `AuthToken`, every client must match it (the `client-config.json` `AuthToken`). The server also enforces an optional IP allow-list (`AllowedIps` in `server-config.json`; empty = allow all).
- `Common/AppSettings.cs` — persisted start mode (`app-settings.json`, `{ "Mode": ... }`). `GetMode` reads it, `SaveMode` writes it; both are hardened (never throw), and `SaveMode` is also called from the TUI config editor. `app-settings.json` is in the `.gitignore`. Its path is pinned to `AppContext.BaseDirectory` (with one-time migration from CWD) so read/write stay consistent regardless of how the app is launched.
- `Common/LogFile.cs` — persistent file logger (`listenersound.log`, written next to the exe or CWD, 1 MB auto-rotation to `.old`). `LogFile.Append` never throws; `LogFile.StripMarkup` removes Spectre tags. `ServerApp.AddLog` mirrors each in-memory log entry to disk; the client logs connect/connect-error/trigger events.
- `Models/ConfigModels.cs` — config types + `ConfigLoader` (System.Text.Json, case-insensitive). `ServerConfig` has `AuthToken` + `AllowedIps`; `ClientConfig` has `AuthToken`.

## Gotchas

- Spectre markup: log/UI strings embed `[color]...[/]`. Interpolated user data (client IDs, error text) containing `[` or `]` breaks rendering — escape as `[[` / `]]`.
- Audio paths: `GetFullAudioPath*` uses only `Path.GetFileName()` of each configured file joined to `AudioFolder`. Subdirectories in `AudioFile` values are silently stripped.
- `IntervalUnit` values are Spanish literals `"segundos"` / `"minutos"` / `"horas"` matched by a switch in `GetScheduleInterval` (unknown values fall back to minutes).
- `client-config.json` is **tracked by git despite being listed in `.gitignore`** (committed before the rule was added); edits to it will be committed. `client-config.example.json` is the template. `server-config.json` is intentionally tracked as sample data.
- All user-facing strings are Spanish; keep new UI copy consistent.