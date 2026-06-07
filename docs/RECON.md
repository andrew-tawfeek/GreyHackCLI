# Reverse-engineering notes — Grey Hack terminal/session internals

Source: decompiled `Assembly-CSharp.dll` (Unity 2022.3.62f3, Mono, unobfuscated) via
`ilspycmd`. Line numbers refer to the decompiled tree under `tools/decompiled/` (git-ignored;
regenerate with `tools/decompile.ps1`). Names are the game's own (a mix of English/Spanish).

## The big realization

Grey Hack's terminal is a **client/server system with a PID-keyed message protocol**, even
in single-player (the host process is both client and server). The script interpreter never
writes to the UI directly — it emits messages routed to a client **window by PID**. This is
the seam we exploit: create a session with our own PID and re-point its I/O to a socket.

```
 CLIENT (UI side)                         SERVER (sim side)
 Terminal (MonoBehaviour)                 PlayerServer  ── switch(IdServer) ──► helpers
   typed line ──► SendInputUserToServer ─────────────► GreyScriptHelperServer
                  MessageServer(IdServer.*)              - helperScript: Dictionary<PID, HelperScript>
                  + PID                                  - PrepareTerminalServerRpc(user, PID)
                                                         - PrepareCommandServerRpc(cwd, cmd, user, PID, ...)
                                                         - InputUserServerRpc(input, PID)   (stdin)
   ResumeTextScript / ResumeOnlyOutput  ◄───────────── SendPrintToClient(out, replace, PID)
   (rendered to TMP text)                                MessageClient(IdClient.PrintSentClientRpc)
                                                         + PID
                                                              ▲
                                                         GreyInterpreter (Miniscript-based)
                                                         runs the actual GreyScript program
```

## Message transport

- Client→Server: `new MessageServer(IdServer.<X>)`, append args, `PlayerClient.Singleton.SendData(m)`.
- Server→Client: `new MessageClient(IdClient.<X>)`, append args, `player.SendData(m)` (`player` = `PlayerServer`).
- Enums: `NetworkMessages/IdServer.cs`, `NetworkMessages/IdClient.cs`.
- `PlayerServer` dispatches incoming server RPCs in a big `switch(IdServer)` (see ~line 380+).
- Payloads are length/type-tagged (`AddByte`/`AddInt`/`AddString`/`AddBool`, read back with
  `GetByte`/`GetInt`/...). Text args are typically `StringCompressor.Zip(...)`/`Unzip(...)`.

## Session lifecycle (server side) — `GreyScriptHelperServer.cs`

State: `public ConcurrentDictionary<int, HelperScript> helperScript` — **PID → running session**.

1. **Allocate a PID** — `Computer.AddProcess(name, user, ram, isScript, isTerminal, remoteNetID, ...)`
   (`Computer.cs:917`). Picks a random free PID in `[1000,10000)`, registers a `Proceso`, returns it.
   Real terminals do: `AddProcess("Terminal", user, 0.1f, isScript:false, isTerminal:true, pc.GetID(), -1, isProtected:true)`
   (cf. `PlayerHelperServer.cs:143` for the safemode terminal).
2. **Start the shell** — `PrepareTerminalServerRpc(activeUser, PID)` (`:156`). Requires the
   `Proceso` to already exist for `PID`. Resolves the user's home folder, finds `/bin/bash`,
   and calls `RunScriptFin({"bash"}, ...)` → a `GreyInterpreter` bash session for that PID.
   (Client trigger: `Terminal.ConfigureTerminal()` sends `IdServer.PrepareTerminalServerRpc`,
   `Terminal.cs:187`.)
3. **Run a command** — `PrepareCommandServerRpc(pathCurrentFolder, zipComandoCompleto,
   propActiveUser, terminalPID, parentPID, absolutePath)` (`:207`). `zipComandoCompleto` is
   `Zip(JSON(string[]))` — the command pre-tokenized into argv. Resolves the program against
   `[cwd,"/bin","/usr/bin"]`, then runs it.
4. **Stream output** — `SendPrintToClient(zipOutput, replaceText, windowPID)` (`:403`):
   ```csharp
   var m = new MessageClient(IdClient.PrintSentClientRpc);
   m.AddByte(zipOutput); m.AddBool(replaceText); m.AddInt(windowPID);
   player.SendData(m);
   ```
   **This is the single choke point for all script output.** Client renders via
   `Terminal.ResumeOnlyOutput` / `ResumeTextScript` / `ResumeEnableInputUser`
   (`Terminal.cs:728/737/752`). `ResumeTextScript(enablePrompt:true)` = "command finished,
   re-show prompt" — our cue to send a fresh prompt to the socket.
5. **Feed stdin** (while a program is running and `WaitInput`/`PollInput`-ing) —
   `InputUserServerRpc(zipLinea, terminalPID)` (`:517`):
   `helperScript[PID].GetAppLauncher().SetPendingInput(Unzip(zipLinea))`.
6. **Teardown** — `KillScript(PID)` (`:38`) / `CancelScriptServerRpc(PID)`; drop the `Proceso`.

## The interpreter — `GreyInterpreter.cs`

- Extends Miniscript `Interpreter` (`Miniscript/Interpreter.cs`); `RunUntilDone(timeLimit, returnEarly)`
  is the cooperative stepper — the server ticks running sessions across frames.
- Ctor (`:214`): `(source, playerID, computerID, playerComputerID, posNodoPlayer,
  HelperConfigInterpreter, serialCpuSockets, defaultBashID)`.
- `HelperConfigInterpreter` (`:48`) carries `terminalPID, activeUser, currentPath, launchPath,
  pathProgram, prevConnIp, numBounces, ...`; `ConfigInterpreter(interpreter)` wires intrinsics.
- Output/input plumbing on the interpreter: `AddPendingOutput`, `SendPendingPrint`,
  `WaitInput`, `PollInput`, `ProcessControlSignal` (ctrl-c).
- `Shell.cs` is **just a serializable connection descriptor** (ips, user, machine, bounces) —
  NOT the execution engine. Don't confuse it with the session.

## Headless command execution — the WORKING model (v0.5, corrected)

The first-pass model above was partly wrong. What actually works:

- **The default shell loop is client-side.** `InternalBash` (a `Terminal` subclass, Singleton,
  `PID == -10`, `AddTexto` overridden to no-op) tokenizes each line and sends
  `IdServer.PrepareCommandServerRpc`. The server resolves the file then sends
  `IdClient.SendCommandClientRpc` *back to the client*, whose handler bails immediately if
  `GetVentana(terminalPID) as Terminal == null` (`PlayerClientMethods.cs:710`). So the normal
  command path **requires a real Terminal window** — unusable headlessly, and calling
  `PrepareCommandServerRpc` ourselves dead-ends at that null check.
- **`RunScriptFin` is the only server-side interpreter runner** (`GreyScriptHelperServer.cs:62`),
  and the stock game only ever calls it for `bash`. We call it directly per command, under our
  own PID, after resolving the program file the way `PrepareCommandServerRpc` does
  (search `[cwd, /bin, /usr/bin]`, `IsBinario`/`IsEjecutable`/`TienePermisoEjecucion`). Because
  it adds `helperScript[pid]` on start and `PostEndScript` removes it on finish, sequential
  one-command-at-a-time reuse of a single PID works.
- **`scriptFileID` = the file's `Archivo.ID`.** `MainClass.Main` loads source via
  `Database.GetContenido(fileID)` (== `Archivo.GetContenido()`), prepends `params = [...]`, and
  runs a `GreyInterpreter` on its own `scriptThread`.
- **Output: capture at `GreyInterpreter.AddPendingOutput(string)`, NOT `SendPrintToClient`.**
  Output is buffered in `terminalLines` and only flushed by `SendPendingPrint`, which is
  throttled to every 200 ms (`ShouldSendPrint`) or called from `WaitInput`. A one-shot command
  finishes in <1 ms, so for us the flush *never fires* and `SendPrintToClient` is never reached
  (the persistent stock bash flushes on its next loop/input). So we Harmony-postfix
  `AddPendingOutput` and route each line straight to the socket; we still suppress
  `SendPrintToClient` for our PIDs to avoid a stray send to a non-existent window.
- **Prompt / cwd:** `cd` is the `chdir` intrinsic (`PlayerIntrinsics.cs:~299`); it sets
  `greyInterpreter.currentPath` and sends `IdClient.CdTerminalClientRpc(newPath, pid)`. We capture
  it at the client handler `PlayerClientMethods.CdTerminalClientRpc(path, terminalPID)` and update
  our tracked cwd (passed as `currentPath` to the next command, and shown in the prompt).
  Initial cwd = `fs.GetCarpetaUser(user).GetRuta()`. The "ready for next command" signal is
  `PostEndScript(pid)`.
- **Active user:** for the local computer `ServerActiveUsers.GetActiveUser` returns `propUser`
  unchanged, so any valid user works; we capture one from a real terminal's
  `PrepareTerminalServerRpc` (the user must open an in-game terminal once after launch).
- **Interactive program input:** a running program blocking in `GreyInterpreter.WaitInput`
  (it `SetPendingInput`s only while `waitingBlockingUserInput`) → forward the ask; the next
  socket line goes to `InputUserServerRpc` instead of being run as a command (session state
  machine: Idle / Running / AwaitingInput).

Hook set in the plugin: `ServerListener.AddPlayer` (capture player),
`GreyScriptHelperServer.PrepareTerminalServerRpc` (capture user),
`GreyInterpreter.AddPendingOutput` (output → socket),
`GreyScriptHelperServer.SendPrintToClient` (suppress for our PIDs),
`PlayerUtils.UpdateTermStatusClient` (prompt parts),
`PlayerClientMethods.CdTerminalClientRpc` (cwd), `GreyInterpreter.WaitInput` (stdin gate),
`GreyScriptHelperServer.PostEndScript` (command done → next prompt).

## Implications for the bridge

We don't scrape UI text. We:
- call `AddProcess` + `PrepareTerminalServerRpc` to open a headless session with our own PID;
- call `PrepareCommandServerRpc` / `InputUserServerRpc` to drive it;
- **Harmony-patch `SendPrintToClient`** (and the other `IdClient` sends that carry a `windowPID`):
  if `windowPID` ∈ our headless PIDs, divert `Unzip(output)` to the socket and skip the original
  (no real window exists for that PID).

All these methods touch game state → must be invoked on the Unity **main thread**.

## Open items to confirm during implementation

- Exact `IdClient.*` messages (besides `PrintSentClientRpc`) that carry a `windowPID` and must
  be intercepted for a clean session (prompt enable, input enable, cursor, clear screen).
- Where/how the server **ticks** `helperScript` sessions each frame (to be sure our PID gets stepped).
- Whether `propActiveUser`/cwd need exact values from `PrepareTerminalServerRpc`'s resolution.
- PID hygiene: our headless `Proceso` shows up in the in-game process list — decide whether to
  hide it or leave it visible (visible is more honest and simpler).
