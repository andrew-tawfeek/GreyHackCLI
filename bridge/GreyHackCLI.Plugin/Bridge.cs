using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace GreyHackCLI
{
    // Milestone 4: independent headless sessions.
    // Each TCP connection gets its own bash session on the player's computer:
    //   connect  -> AddProcess (new PID) + PrepareTerminalServerRpc(user, PID)  [starts bash]
    //   line in  -> InputUserServerRpc(zip(line), PID)                          [feeds the shell]
    //   output   -> captured from SendPrintToClient(windowPID==PID) -> socket
    //   ready    -> WaitInput(interpreter PID==our) -> we emit the prompt
    //   close    -> KillScript(PID) + CloseTerminal(PID)
    // No on-screen terminal window is involved — it's a real "ssh into the box" session.
    internal static class Bridge
    {
        public const int Port = 8642;

        private static TcpListener _listener;
        private static volatile bool _running;
        private static ManualLogSource _log;

        // PID -> owning connection. Set on the main thread when a session opens.
        private static readonly ConcurrentDictionary<int, ClientConn> _byPid =
            new ConcurrentDictionary<int, ClientConn>();

        public static ManualLogSource Log { get { return _log; } }

        public static void Start(ManualLogSource log)
        {
            _log = log;
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "GreyHackCLI-Accept";
            t.Start();
            _log.LogInfo("Bridge listening on 127.0.0.1:" + Port + " (headless sessions)");
        }

        public static void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            foreach (var kv in _byPid) { try { kv.Value.Close(); } catch { } }
            _byPid.Clear();
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ClientConn conn = new ClientConn(client);
                    conn.Begin();
                }
                catch (Exception e)
                {
                    if (_running && _log != null) _log.LogWarning("Accept loop ended: " + e.Message);
                    break;
                }
            }
        }

        // --- registry, used by ClientConn (main thread) ---
        internal static void Register(int pid, ClientConn conn) { _byPid[pid] = conn; }
        internal static void Unregister(int pid) { ClientConn _; _byPid.TryRemove(pid, out _); }

        // --- called from Harmony hooks ---
        // Route a chunk of script output (already decoded) to the owning session's socket.
        public static void RouteRawText(int windowPID, string text)
        {
            ClientConn conn;
            if (_byPid.TryGetValue(windowPID, out conn)) conn.SendOutput(text);
        }

        // Prompt info (user/host/cwd) for a headless PID. Returns true if it's ours (skip client send).
        public static bool RouteTermStatus(int pid, string user, string host, string cwd)
        {
            ClientConn conn;
            if (!_byPid.TryGetValue(pid, out conn)) return false;
            conn.UpdatePrompt(user, host, cwd);
            return true;
        }

        // cd changed the working directory for a headless PID. Returns true if it's ours.
        public static bool UpdateCwd(int pid, string newPath)
        {
            ClientConn conn;
            if (!_byPid.TryGetValue(pid, out conn)) return false;
            conn.UpdatePrompt(null, null, newPath);
            return true;
        }

        // A running program for this PID is blocking for stdin -> forward the ask (main thread).
        public static void NotifyReadyForInput(int pid, string askMessage, bool isPassword)
        {
            ClientConn conn;
            if (_byPid.TryGetValue(pid, out conn))
                GameRefs.OnMainThread(delegate { conn.OnReadyForInput(askMessage, isPassword); });
        }

        // PostEndScript fired for this PID -> the command finished; advance the shell (main thread).
        public static void OnCommandComplete(int pid)
        {
            ClientConn conn;
            if (_byPid.TryGetValue(pid, out conn))
                GameRefs.OnMainThread(conn.HandleCommandComplete);
        }

        public static bool IsHeadless(int pid) { return _byPid.ContainsKey(pid); }
    }
}
