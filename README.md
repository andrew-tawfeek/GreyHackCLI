# GreyHackCLI

A real OS terminal that "SSHes" into the in-game shell of **Grey Hack** (Steam) — a
single-player mod. Type commands in a normal Windows/Linux/Mac terminal and they run on
your in-game computer, with output streamed back, as an independent headless session
(not a mirror of the on-screen terminal window).

> Single-player only. This drives **your own** in-game machine. It is not a cheat against
> other players and does no real network access beyond `localhost`.

## How it works (one paragraph)

Grey Hack is a Unity (Mono) game whose terminal is already a **client/server system with a
PID-keyed message protocol**, even in single-player. A C# mod (BepInEx + Harmony) injected
into the game process spins up a *headless* shell session on your computer — reusing the
game's real interpreter, filesystem, and commands — and re-points that session's I/O at a
`localhost` TCP socket instead of an on-screen terminal window. A Python CLI connects to
that socket and gives you a real prompt. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
for the full design and [docs/RECON.md](docs/RECON.md) for the reverse-engineering notes.

## Components

| Path        | What                                                                    |
|-------------|-------------------------------------------------------------------------|
| `bridge/`   | C# BepInEx 5 plugin: TCP server + headless session + Harmony hooks       |
| `cli/`      | Python `prompt_toolkit` client — the "ssh greyhack" terminal             |
| `docs/`     | Architecture + reverse-engineering reference                            |
| `tools/`    | Decompiler (ilspycmd) and analysis scripts (git-ignored output)         |

## Environment (verified)

- Grey Hack install: `C:\Program Files (x86)\Steam\steamapps\common\Grey Hack`
- Unity **2022.3.62f3**, **Mono** backend (not IL2CPP) → BepInEx 5 + Harmony
- `Assembly-CSharp.dll` is **unobfuscated**
- Python 3.12, .NET 6 runtime present (used to run the decompiler; no SDK needed)

## Status

Early. Reverse-engineering of the session/message protocol is mapped (see docs). Next:
stand up BepInEx + an empty plugin, then the read-only output tap, then full bidirectional.
