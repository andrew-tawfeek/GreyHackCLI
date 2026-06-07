# Architecture

## Goal

Type into a real OS terminal; commands run as an **independent headless session** on your
in-game Grey Hack computer; output streams back. Single-player. Transport: raw TCP on
`127.0.0.1`. (Both decisions made up front; see git history.)

## Two processes + a socket

```
┌──────────────────────────┐      TCP 127.0.0.1:<port>       ┌──────────────────────────────┐
│  cli/  (Python)          │  ◄────── line-framed proto ─────►│  Grey Hack.exe                │
│  prompt_toolkit client   │   C→S: INPUT, SIGNAL             │  + bridge/ (BepInEx plugin)   │
│  "ssh greyhack"          │   S→C: OUTPUT, PROMPT, EXIT      │                               │
└──────────────────────────┘                                  │  TCP server (bg thread)        │
                                                              │  ConcurrentQueue<cmd>          │
                                                              │  Update() drains → main thread │
                                                              │  Harmony hook: SendPrintToClient│
                                                              └──────────────┬────────────────┘
                                                                             │ reuses, by PID
                                                              ┌──────────────▼────────────────┐
                                                              │ GreyScriptHelperServer          │
                                                              │  AddProcess → PID               │
                                                              │  PrepareTerminalServerRpc        │
                                                              │  PrepareCommandServerRpc         │
                                                              │  InputUserServerRpc (stdin)      │
                                                              │  → GreyInterpreter, FS, commands │
                                                              └─────────────────────────────────┘
```

The headless session reuses the game's real interpreter, filesystem, users, and the full
command set. We only swap the **I/O endpoint** (socket instead of an on-screen Terminal),
keyed by the session's PID. See [RECON.md](RECON.md) for the exact methods.

## bridge/ — the C# plugin (the hard part)

Responsibilities:
1. **TCP server** on `127.0.0.1` (background thread): accept clients, frame messages.
2. **Main-thread marshalling**: Unity/game calls must run on the main thread. Incoming
   commands go into a `ConcurrentQueue`; the plugin's `Update()` (a BepInEx MonoBehaviour,
   runs each frame on the main thread) drains it and invokes the game methods.
3. **Session manager**: per TCP connection, open a headless session —
   `AddProcess(...)` → reserve PID → `PrepareTerminalServerRpc(user, PID)`. Track
   `PID → connection`. Tear down on disconnect (`KillScript` + drop `Proceso`).
4. **Drive commands**: socket INPUT → `PrepareCommandServerRpc(cwd, Zip(JSON(argv)), user,
   PID, parentPID, absolutePath)`; or, if a program is mid-run and awaiting input,
   `InputUserServerRpc(Zip(line), PID)`.
5. **Capture output**: Harmony **prefix** on `GreyScriptHelperServer.SendPrintToClient`
   (+ sibling `IdClient` sends carrying a `windowPID`). If `windowPID` ∈ our PIDs:
   `Unzip` → enqueue to that session's out-buffer → skip original (`return false`). A writer
   path flushes the out-buffer to the socket. Treat `ResumeTextScript(enablePrompt:true)` as
   the "command done → send PROMPT" signal.

Build: a netstandard2.0/net472 class library referencing `BepInEx`, `0Harmony`, and the game's
`Assembly-CSharp.dll` + UnityEngine DLLs (referenced from the install, not committed). Output
DLL drops into `BepInEx/plugins/`.

### Threading model (the one genuinely tricky bit)

- Net thread: blocking socket accept/read/write. Never touches Unity APIs.
- `Update()` (main thread): drains inbound command queue → game calls; safe.
- Harmony hook fires on the main thread (interpreter runs there) → push bytes to a
  thread-safe per-session out-queue; net thread (or a per-client writer thread) flushes.
- Each session has: inbound queue (net→main), outbound queue (main→net). Both `Concurrent`.

## cli/ — the Python client (the fun part)

- `prompt_toolkit` for line editing, history, ctrl-C/ctrl-D handling.
- Connect to `127.0.0.1:<port>`; on connect, server sends a PROMPT.
- Read loop renders OUTPUT/PROMPT; input loop sends INPUT/SIGNAL.
- Stretch: tab-completion proxied to the game, `scp`-like file transfer, multiple sessions.

## Protocol (v0, line-framed)

Newline-delimited frames, `TYPE<TAB>payload`. Payload is UTF-8 (base64 only if it contains
control bytes). Minimal and easy to debug with `nc`.

| Dir | Type     | Payload                         | Meaning                                  |
|-----|----------|---------------------------------|------------------------------------------|
| C→S | `IN`     | command line text               | run a command / answer a prompt          |
| C→S | `SIG`    | `INT` \| `EOF`                  | ctrl-C / ctrl-D                          |
| S→C | `OUT`    | text (may be partial)           | program output; render verbatim          |
| S→C | `PROMPT` | `user@host:cwd$ `               | session ready for next command           |
| S→C | `ASK`    | prompt text (+ `pw` flag)       | program is requesting interactive input  |
| S→C | `EXIT`   | reason                          | session closed                           |

Versioned by a `HELLO` handshake so we can evolve framing (length-prefix, resize, etc.) later.

## Milestones

1. **Toolchain** ✅ — decompiler up, internals mapped.
2. **BepInEx + empty plugin** — proves injection + logging.
3. **Output tap (read-only)** — hook `SendPrintToClient`, mirror the *existing* main terminal's
   output to a connected socket. Lowest-risk way to validate the hook + threading.
4. **Headless session open** — `AddProcess`+`PrepareTerminalServerRpc` with our PID; confirm a
   `bash` prompt comes back through the hook (no UI window involved).
5. **Bidirectional** — drive `PrepareCommandServerRpc` / `InputUserServerRpc` from the socket.
6. **Python CLI polish** — real ssh-like UX.

## Risks / unknowns

- Some `IdClient` messages may bypass `SendPrintToClient` (cursor, clear, input-enable); we
  enumerate and intercept those too.
- Game updates can shift method signatures → keep the Harmony hooks narrow and named; re-decompile
  on patch. (`tools/decompile.ps1` regenerates the reference.)
- Anti-cheat: single-player, local-only, no PvP — low concern, but document that this is a mod.
