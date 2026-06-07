using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GreyHackCLI
{
    // Captured references to live game objects + a main-thread work queue + the gzip codec
    // the game uses for terminal payloads (CompressString.StringCompressor == GZip/UTF-8).
    internal static class GameRefs
    {
        // The host's server object (single-player => exactly one). Captured via a hook on
        // ServerListener.AddPlayer. Gives us greyScriptHelper, GetComputer(), etc.
        public static volatile PlayerServer Player;

        // A valid active user, observed from a real in-game terminal opening. We need a real
        // user to start a session (its home folder must exist), so we borrow the one the game uses.
        public static volatile string LastActiveUser;

        public static bool Ready
        {
            get { return Player != null && !string.IsNullOrEmpty(LastActiveUser); }
        }

        // --- main-thread marshalling ---
        // Game APIs must be touched on Unity's main thread. Network threads enqueue work here;
        // Plugin.Update() drains it each frame.
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public static void OnMainThread(Action a)
        {
            if (a != null) _queue.Enqueue(a);
        }

        public static void Pump()
        {
            Action a;
            while (_queue.TryDequeue(out a))
            {
                try { a(); }
                catch (Exception e)
                {
                    if (Plugin.Log != null) Plugin.Log.LogError("main-thread action failed: " + e);
                }
            }
        }

        // --- gzip codec (matches the game's StringCompressor) ---
        public static byte[] Zip(string s)
        {
            byte[] raw = Encoding.UTF8.GetBytes(s == null ? "" : s);
            using (MemoryStream outMs = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(outMs, CompressionMode.Compress))
                {
                    gz.Write(raw, 0, raw.Length);
                }
                return outMs.ToArray();
            }
        }

        public static string Unzip(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            using (MemoryStream input = new MemoryStream(bytes))
            using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) != 0) outMs.Write(buf, 0, n);
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
        }
    }
}
