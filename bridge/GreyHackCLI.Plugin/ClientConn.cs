using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GreyHackCLI
{
    // One TCP client == one headless shell session.
    //
    // We *are* the shell loop (the game's default bash loop is client-side InternalBash, which
    // doesn't exist headlessly). Per command we resolve the program file the way the server's
    // PrepareCommandServerRpc does, then call RunScriptFin directly under our PID — bypassing the
    // client round-trip. Output is captured at SendPrintToClient; PostEndScript tells us a command
    // finished (-> next prompt); WaitInput tells us a running program wants stdin.
    internal class ClientConn
    {
        private enum SState { Idle, Running, AwaitingInput }

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BlockingCollection<string> _outbound =
            new BlockingCollection<string>(new ConcurrentQueue<string>());
        private volatile bool _closed;

        // Session identity (set on the main thread at open).
        private volatile int _pid = -1;
        private string _user = "";
        private string _host = "";
        private volatile string _cwd = "";   // updated by cd via UpdateTermStatusClient

        // State machine + pending command queue — only touched on the main thread.
        private SState _state = SState.Idle;
        private readonly Queue<string> _pending = new Queue<string>();

        public ClientConn(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
        }

        public void Begin()
        {
            StartThread(SenderLoop, "GreyHackCLI-Send");
            StartThread(ReadLoop, "GreyHackCLI-Read");
            GameRefs.OnMainThread(OpenSession);
        }

        // ---------------- main-thread session lifecycle ----------------

        private void OpenSession()
        {
            if (!GameRefs.Ready)
            {
                SendOutput("[bridge] Not ready yet. Open an in-game terminal once (so the bridge can " +
                           "learn your user), then reconnect.\n");
                Close();
                return;
            }
            try
            {
                PlayerComputer pc = GameRefs.Player.GetComputer();
                _user = GameRefs.LastActiveUser;
                _host = pc.GetNombreMaquina();
                _cwd = pc.GetFileSystem().GetCarpetaUser(_user).GetRuta();
                int pid = pc.AddProcess("Terminal", _user, 0.1f, false, true, pc.GetID(), -1, true);
                _pid = pid;
                Bridge.Register(pid, this);
                Log("session opened: PID=" + pid + " user=" + _user + " cwd=" + _cwd);
                SendOutput("Connected to " + _host + " as " + _user + ".\n");
                _state = SState.Idle;
                SendPrompt();
            }
            catch (Exception e)
            {
                Log("OpenSession failed: " + e);
                SendOutput("[bridge] failed to open session: " + e.Message + "\n");
                Close();
            }
        }

        // Called on the main thread for each line received from the socket.
        private void FeedLine(string line)
        {
            if (_pid < 0) return;
            switch (_state)
            {
                case SState.AwaitingInput:
                    _state = SState.Running;
                    try { GameRefs.Player.greyScriptHelper.InputUserServerRpc(GameRefs.Zip(line), _pid); }
                    catch (Exception e) { Log("feed stdin failed: " + e); }
                    break;
                case SState.Running:
                    _pending.Enqueue(line);
                    break;
                default: // Idle
                    RunCommand(line);
                    break;
            }
        }

        private void RunCommand(string line)
        {
            line = line.Trim();
            if (line.Length == 0) { SendPrompt(); return; }

            string cmd0 = FirstToken(line);
            if (cmd0 == "exit" || cmd0 == "logout") { SendOutput("logout\n"); Close(); return; }
            if (cmd0 == "clear") { SendOutput("\x1b[2J\x1b[H"); SendPrompt(); return; }

            try
            {
                PlayerComputer pc = GameRefs.Player.GetComputer();
                FileSystem fs = pc.GetFileSystem();

                // Tokenize like InternalBash: split on spaces, drop empties.
                List<string> toks = new List<string>(line.Split(' '));
                toks.RemoveAll(string.IsNullOrEmpty);
                string[] argv = toks.ToArray();

                bool absolute = argv[0].Length > 0 && argv[0][0] == '/';
                string searchFolder = _cwd;

                // If the command name carries a path, split it off.
                List<string> nameParts = new List<string>(argv[0].Split('/'));
                nameParts.RemoveAll(string.IsNullOrEmpty);
                if (nameParts.Count > 1)
                {
                    argv[0] = nameParts[nameParts.Count - 1];
                    nameParts.RemoveAt(nameParts.Count - 1);
                    string sub = "/" + string.Join("/", nameParts.ToArray());
                    searchFolder = absolute ? sub : (_cwd + sub);
                }

                List<string> folders = absolute
                    ? new List<string> { searchFolder }
                    : new List<string> { searchFolder, "/bin", "/usr/bin" };

                Computer.User user = pc.GetUser(_user);
                FileSystem.Archivo prog = null;
                for (int i = 0; i < folders.Count; i++)
                {
                    if (fs.GetCarpeta(folders[i]) == null) continue;
                    FileSystem.Archivo a = fs.GetArchivo(folders[i] + "/" + argv[0]);
                    if (a == null) continue;
                    if (!a.IsBinario()) { Fail(a.nombre + ": not a binary file"); return; }
                    if (!a.IsEjecutable()) { Fail(a.nombre + ": not an executable file"); return; }
                    if (a.TienePermisoEjecucion(user)) { prog = a; break; }
                    Fail("Can't launch program. Permission denied."); return;
                }
                if (prog == null) { Fail(string.Join(" ", argv) + ": command not found"); return; }

                Log("RUN '" + line + "' -> " + prog.GetRuta() + " (pid=" + _pid + ")");
                _state = SState.Running;
                // Fire-and-forget: output streams via our hook; PostEndScript signals completion.
                var _ = GameRefs.Player.greyScriptHelper.RunScriptFin(
                    new List<string>(argv), prog.ID, 0f, pc, _user, _cwd, prog.GetRuta(),
                    "", 0, _pid, null, false, null, false, false);
            }
            catch (Exception e)
            {
                Log("RunCommand failed: " + e);
                Fail("error: " + e.Message);
            }
        }

        private void Fail(string msg)
        {
            SendOutput(msg + "\n");
            _state = SState.Idle;
            SendPrompt();
        }

        // PostEndScript fired for our PID — the command finished.
        public void HandleCommandComplete()
        {
            _state = SState.Idle;
            if (_pending.Count > 0) RunCommand(_pending.Dequeue());
            else SendPrompt();
        }

        // A running program is blocking for input.
        public void OnReadyForInput(string askMessage, bool isPassword)
        {
            _state = SState.AwaitingInput;
            SendFrame(isPassword ? 'W' : 'A', askMessage == null ? "" : askMessage);
        }

        // Ctrl-C from the client (ETX byte): cancel the running command / input.
        public void CancelCurrent()
        {
            if (_pid < 0) return;
            if (_state != SState.Running && _state != SState.AwaitingInput) return;
            try { GameRefs.Player.greyScriptHelper.CancelScriptServerRpc(_pid); }
            catch (Exception e) { Log("cancel failed: " + e); }
        }

        private void CloseSession()
        {
            int pid = _pid;
            if (pid < 0) return;
            _pid = -1;
            Bridge.Unregister(pid);
            try
            {
                if (GameRefs.Player != null)
                {
                    GameRefs.Player.greyScriptHelper.KillScript(pid);
                    GameRefs.Player.GetComputer().CloseTerminal(pid);
                }
                Log("session closed: PID=" + pid);
            }
            catch (Exception e) { Log("CloseSession failed: " + e); }
        }

        // ---------------- hook callback (any thread) ----------------

        public void UpdatePrompt(string user, string host, string cwd)
        {
            if (!string.IsNullOrEmpty(user)) _user = user;
            if (!string.IsNullOrEmpty(host)) _host = host;
            if (!string.IsNullOrEmpty(cwd)) _cwd = cwd;
        }

        // ---------------- socket I/O ----------------
        // Framed protocol to the client: "<TYPE> <LEN>\n<payload-bytes>".
        // TYPE: O=output, P=prompt, A=input request, W=password request.

        private void SendPrompt() { SendFrame('P', BuildPrompt()); }

        public void SendOutput(string text) { SendFrame('O', text); }

        private string BuildPrompt()
        {
            string sep = (_user == "root") ? "# " : "$ ";
            if (string.IsNullOrEmpty(_user) && string.IsNullOrEmpty(_cwd)) return "$ ";
            return _user + "@" + _host + ":" + _cwd + sep;
        }

        private void SendFrame(char type, string payload)
        {
            if (_closed) return;
            if (payload == null) payload = "";
            int len = Encoding.UTF8.GetByteCount(payload);
            try { _outbound.Add(type + " " + len + "\n" + payload); } catch { }
        }

        private void ReadLoop()
        {
            byte[] buf = new byte[4096];
            StringBuilder line = new StringBuilder();
            try
            {
                while (!_closed)
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == 3) // ETX = client Ctrl-C: cancel the running command
                        {
                            GameRefs.OnMainThread(CancelCurrent);
                            continue;
                        }
                        char ch = (char)b;
                        if (ch == '\n')
                        {
                            string s = line.ToString().TrimEnd('\r');
                            line.Length = 0;
                            string captured = s;
                            GameRefs.OnMainThread(delegate { FeedLine(captured); });
                        }
                        else line.Append(ch);
                    }
                }
            }
            catch { }
            Close();
        }

        private void SenderLoop()
        {
            try
            {
                foreach (string s in _outbound.GetConsumingEnumerable())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(s);
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
            }
            catch { }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            GameRefs.OnMainThread(CloseSession);
            try { _outbound.CompleteAdding(); } catch { }
            try { _client.Close(); } catch { }
        }

        private static string FirstToken(string line)
        {
            int sp = line.IndexOf(' ');
            return sp < 0 ? line : line.Substring(0, sp);
        }

        private static void StartThread(ThreadStart body, string name)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        private static void Log(string msg)
        {
            if (Bridge.Log != null) Bridge.Log.LogInfo("[conn] " + msg);
        }
    }
}
