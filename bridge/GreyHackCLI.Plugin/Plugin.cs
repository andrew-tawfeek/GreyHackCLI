using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GreyHackCLI
{
    // Milestone 4: independent headless sessions over TCP.
    // Starts the bridge server, drains the main-thread work queue each frame, and installs the
    // Harmony hooks that capture output / prompt state and gate input for headless PIDs.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.coffeeandproofs.greyhackcli";
        public const string Name = "GreyHackCLI Bridge";
        public const string Version = "0.5.1";

        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo(Name + " v" + Version + " loading...");

            Bridge.Start(Log);

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            Log.LogInfo(Name + " ready — headless sessions on 127.0.0.1:" + Bridge.Port);
        }

        // Runs on Unity's main thread every frame: execute queued game calls here.
        private void Update()
        {
            GameRefs.Pump();
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            Bridge.Stop();
        }
    }
}
