# GreyHackCLI

A real OS terminal that "SSHes" into the in-game shell of **Grey Hack** (Steam). Type commands
in a normal terminal and they run on your in-game computer — as an **independent headless
session** (a separate shell, not a mirror of any on-screen terminal window), with output streamed
back live.

```
$ python cli/ghcli.py
Connected to again as again.

again@again:/home/again$ ls
Desktop  Downloads  Config  Code
again@again:/home/again$ cd /bin && pwd
/bin
again@again:/bin$
```

> **Single-player mod.** It drives **your own** in-game machine over `localhost` only. It is not
> a PvP cheat and makes no real network connections beyond the loopback bridge.

## How it works

Grey Hack is a Unity (Mono) game. You can't escape its sandboxed scripting language, so the bridge
lives one layer down: a **BepInEx 5 + Harmony** plugin injected into the game process. Per TCP
connection it opens a real shell session on your computer — allocating a PID, then running each
command through the game's own interpreter (`RunScriptFin`) and filesystem — and redirects that
session's I/O to a `localhost` TCP socket instead of an on-screen window. A small Python client is
the front end.

The execution model (which differs from a naive reading of the code) is documented in
[docs/RECON.md](docs/RECON.md); the overall design is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Layout

| Path        | What                                                                       |
|-------------|----------------------------------------------------------------------------|
| `bridge/`   | C# BepInEx 5 plugin: TCP server + headless session + Harmony hooks          |
| `cli/`      | Python client (`ghcli.py`) — the "ssh greyhack" terminal                    |
| `docs/`     | Architecture + reverse-engineering reference                               |
| `tools/`    | Decompiler (`ilspycmd`) + `decompile.ps1` (output is git-ignored)           |

## Requirements

- Grey Hack (Steam), Windows. Verified against Unity **2022.3.62f3**, **Mono** backend.
- [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) installed into the game folder.
- Python 3.10+ for the client.
- To build the plugin: the .NET Framework `csc` (present on Windows by default) — **no .NET SDK
  required**. `build.ps1` uses it directly.

## Build & install

1. Install BepInEx 5 (x64) into the Grey Hack folder and launch the game once so it initializes.
2. Build + deploy the plugin:
   ```powershell
   pwsh bridge/GreyHackCLI.Plugin/build.ps1
   ```
   This compiles to `bridge/GreyHackCLI.Plugin/bin/` and copies `GreyHackCLI.dll` into
   `…/Grey Hack/BepInEx/plugins/GreyHackCLI/`.
   > The game **locks** the deployed DLL while running and only loads plugins at startup, so the
   > dev loop is: **edit → close game → `build.ps1` → relaunch**. Use `-NoDeploy` to compile only.
3. Launch Grey Hack into single-player.

## Use

1. After launching, **open an in-game terminal once** (this lets the bridge learn your username —
   required to start a session).
2. Run the client:
   ```
   python cli/ghcli.py
   ```
3. You'll get a prompt. Run commands (`pwd`, `ls`, `cat`, `cd`, …). Ctrl-D to quit.

The plugin listens on `127.0.0.1:8642`. Watch the BepInEx console (or `BepInEx/LogOutput.log`) for
`[conn]` session messages and any errors.

## Status

Working end-to-end: independent headless sessions, command execution, streaming output, `cd`
navigation, prompt tracking. Active work: polishing the client (line editing, history,
interrupting a running command) and interactive-program input. See the docs for details and the
hook list.
