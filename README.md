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

> **Single-player mod.** It drives **your own** in-game machine over `localhost` only. It is not a
> PvP cheat and makes no real network connections beyond the loopback bridge.

## How it works

Grey Hack is a Unity (Mono) game, and its scripting language is sandboxed — so the bridge lives
one layer down: a **BepInEx 5 + Harmony** plugin injected into the game process.

- On each TCP connection the plugin opens a real shell session on your computer: it allocates a
  process/PID and runs every command through the game's own interpreter (`RunScriptFin`) and
  filesystem, then redirects that session's I/O to a `localhost` TCP socket — no on-screen window
  involved.
- A small Python client (`cli/ghcli.py`) is the front end: a real prompt with history and line
  editing.

The execution model is subtler than the decompiled code first suggests (the default shell loop is
*client-side*, so we drive the server interpreter directly and capture output at
`GreyInterpreter.AddPendingOutput`). The full reverse-engineering write-up — including the exact
Harmony hook list — is in [docs/RECON.md](docs/RECON.md); the high-level design is in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Repository layout

| Path        | What                                                                          |
|-------------|-------------------------------------------------------------------------------|
| `bridge/`   | C# BepInEx 5 plugin: TCP server, headless session manager, Harmony hooks       |
| `cli/`      | Python client `ghcli.py` (+ `requirements.txt`); `tap.py` is a legacy viewer    |
| `docs/`     | `ARCHITECTURE.md` (design), `RECON.md` (reverse-engineering reference)          |
| `tools/`    | `decompile.ps1` + `ilspycmd` for regenerating the decompiled reference          |

Game IP (decompiled source, game DLLs) and build output are git-ignored — see `.gitignore`.

## Requirements

- **Grey Hack** (Steam), Windows. Verified against **Unity 2022.3.62f3**, **Mono** backend.
- **BepInEx 5 (x64)** installed into the game folder ([releases](https://github.com/BepInEx/BepInEx/releases)).
- **Python 3.10+** with `prompt_toolkit` (`pip install -r cli/requirements.txt`).
- To **build the plugin**: the .NET Framework `csc` (present on Windows by default). **No .NET SDK
  required** — `build.ps1` invokes `csc` directly against the game's managed DLLs + BepInEx core.

## Setup & install

1. **Install BepInEx 5 (x64)** into `…/steamapps/common/Grey Hack/` (so `winhttp.dll` and a
   `BepInEx/` folder sit next to `Grey Hack.exe`). Launch the game once so BepInEx generates its
   config, then quit.
2. *(Optional, for debugging)* In `BepInEx/config/BepInEx.cfg` set `[Logging.Console] Enabled =
   true` and `[Logging.Disk] WriteUnityLog = true` so plugin + game logs are visible.
3. **Build & deploy the plugin:**
   ```powershell
   pwsh bridge/GreyHackCLI.Plugin/build.ps1
   ```
   Compiles to `bridge/GreyHackCLI.Plugin/bin/` and copies `GreyHackCLI.dll` into
   `…/Grey Hack/BepInEx/plugins/GreyHackCLI/`. Use `-NoDeploy` to compile only, or
   `-Game "<path>"` if Grey Hack is installed elsewhere.
4. **Install the client deps:** `pip install -r cli/requirements.txt`.
5. Launch Grey Hack into single-player. On load you should see (BepInEx console / `LogOutput.log`):
   ```
   GreyHackCLI Bridge vX.Y.Z ready — headless sessions on 127.0.0.1:8642
   ```

## Usage

1. After launching the game, **open an in-game terminal once** — this lets the bridge learn your
   username (required to start a session).
2. Run the client:
   ```
   python cli/ghcli.py
   ```
   Options: `--host`/`--port` (default `127.0.0.1:8642`), `--raw` (don't strip rich-text tags).
3. You'll get a prompt that tracks your working directory. Run commands (`pwd`, `ls`, `cat`, `cd`,
   …); they execute on your in-game box with output streamed back.

**Keys**

| Key      | At an idle prompt        | While a command is running |
|----------|--------------------------|----------------------------|
| `Up`/`Down` | history / auto-suggest |                            |
| `Ctrl-C` | clear the current line   | cancel the running command |
| `Ctrl-D` | quit                     |                            |

History is persisted to `~/.greyhackcli_history`.

**Wire protocol** (for anyone writing another client): bridge → client is length-framed
`"<TYPE> <LEN>\n<payload>"` where TYPE is `O` output, `P` prompt, `A` input request, `W` password
request. Client → bridge is UTF-8 command lines terminated by `\n`; a lone `0x03` byte cancels.

## Development loop

The game **locks** the deployed DLL while running and only loads plugins at startup, so:

```
edit C#  →  close Grey Hack  →  pwsh bridge/GreyHackCLI.Plugin/build.ps1  →  relaunch  →  test
```

Diagnostics: the BepInEx console and `…/Grey Hack/BepInEx/LogOutput.log` (look for `[conn]`
session lines and any exceptions). Regenerate the decompiled reference after a game update with
`pwsh tools/decompile.ps1`.

## Status

Working end-to-end:

- ✅ BepInEx plugin injects; TCP bridge on `127.0.0.1:8642`
- ✅ Independent headless session per connection (own PID, no on-screen window)
- ✅ Command execution via the game's real interpreter + filesystem
- ✅ Streaming output; `cd` navigation; prompt tracks the working directory
- ✅ **v0.6.0 (built, in-game verification pending):** length-framed protocol; `prompt_toolkit`
  client with history, line editing, and auto-suggest; `Ctrl-C` cancels a running command;
  interactive program input/password prompts (`A`/`W`)

## Testing stages

- [x] **M1** — toolchain + reverse-engineering of the session/message protocol
- [x] **M2** — BepInEx plugin loads (empty plugin logs on startup)
- [x] **M3** — read-only output tap (mirror in-game output to a socket)
- [x] **M4** — independent headless session: run commands, stream output, `cd` works
- [ ] **M5 (in progress)** — client polish: verify v0.6.0 framing + `prompt_toolkit` in-game;
      confirm `Ctrl-C` cancel and interactive-input (`A`/`W`) flows end-to-end
- [ ] **M6** — robustness: output/column formatting, ANSI/color handling, edge-case commands,
      auto-derive the active user (drop the "open a terminal once" step)

## Roadmap / expansions

- Multiple concurrent sessions from one client (tabs / multiplexing)
- `ssh` from within a session into other in-game machines (the game already models in-game SSH)
- File transfer (`scp`-like) over the bridge
- Tab-completion proxied to the in-game filesystem
- Optional cross-platform packaging of the client

## Troubleshooting

- **Client can't connect** — Grey Hack isn't running, the plugin didn't load, or you're on the
  wrong port. Check the BepInEx console for the `ready — headless sessions on 127.0.0.1:8642` line.
- **"Open an in-game terminal once…"** — the bridge hasn't learned a username yet; open a terminal
  in-game, then reconnect.
- **Plugin won't rebuild** (`could not write … user-mapped section open`) — Grey Hack is running
  and holding the DLL; close the game, then `build.ps1`.
- **No plugin logs on disk** — set `[Logging.Disk] WriteUnityLog = true` in `BepInEx/config/BepInEx.cfg`.

## License / disclaimer

A fan-made, single-player modding/learning project. Grey Hack is © its developer (Trodes). This
repo contains no game assets or decompiled game code (both are git-ignored).
