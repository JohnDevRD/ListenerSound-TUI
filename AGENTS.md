# AGENTS.md

## What this is

Single-project .NET 10 console app (`ListenerSound.csproj`, no `.sln`). One binary, two modes. Mode is chosen either by first CLI arg or, when launched with no args (double-click), by an interactive Spectre menu (`Servidor` / `Cliente` / `Salir`):

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
- Config resolution: each config file is looked up **first next to the exe** (`AppContext.BaseDirectory`), then in the current working directory. If neither exists, a default config is created (bootstrap in `Program.cs`'s `EnsureConfigFile`). The resolved path is threaded into `ServerApp`/`ClientApp` so saving from the editor writes back to the same file.
- Verification is `dotnet build` plus manual runs. **No tests, no CI, no lint/format tooling exists** — don't invent test commands.

## Layout (all of the source is 4 files)

- `Program.cs` — mode selection (arg or interactive menu), config path resolution + default-config bootstrap, top-level error handling.
- `Server/ServerApp.cs` — accept loop, client registry, NAudio playback, scheduler, server TUI + interactive config editor. Saving from the editor rewrites `server-config.json`, clears connected clients, and restarts the listener.
- `Client/ClientApp.cs` — connect-with-retry (3 s backoff), key-trigger sender, client TUI + config editor.
- `Common/Protocol.cs` — newline-delimited TCP text protocol (`REGISTER:<token>:<id>`, `TRIGGER`, `BYE`, `OK`, `ERROR:<msg>`). Shared by both sides; changing a constant silently breaks both ends together. Auth token is optional: empty token means no auth; if the server sets `AuthToken`, every client must match it (the `client-config.json` `AuthToken`). The server also enforces an optional IP allow-list (`AllowedIps` in `server-config.json`; empty = allow all).
- `Models/ConfigModels.cs` — config types + `ConfigLoader` (System.Text.Json, case-insensitive). `ServerConfig` has `AuthToken` + `AllowedIps`; `ClientConfig` has `AuthToken`.

## Gotchas

- Spectre markup: log/UI strings embed `[color]...[/]`. Interpolated user data (client IDs, error text) containing `[` or `]` breaks rendering — escape as `[[` / `]]`.
- Audio paths: `GetFullAudioPath*` uses only `Path.GetFileName()` of each configured file joined to `AudioFolder`. Subdirectories in `AudioFile` values are silently stripped.
- `IntervalUnit` values are Spanish literals `"segundos"` / `"minutos"` / `"horas"` matched by a switch in `GetScheduleInterval` (unknown values fall back to minutes).
- `client-config.json` is **tracked by git despite being listed in `.gitignore`** (committed before the rule was added); edits to it will be committed. `client-config.example.json` is the template. `server-config.json` is intentionally tracked as sample data.
- All user-facing strings are Spanish; keep new UI copy consistent.