using System;
using HarmonyLib;

namespace GreyHackCLI
{
    // Capture the host PlayerServer when it registers (single-player => one player).
    [HarmonyPatch(typeof(ServerListener), "AddPlayer")]
    internal static class CapturePlayerPatch
    {
        private static void Postfix(PlayerServer player)
        {
            GameRefs.Player = player;
            if (Plugin.Log != null) Plugin.Log.LogInfo("Captured PlayerServer.");
        }
    }

    // Learn a valid active user from real in-game terminals (needed to start headless sessions).
    [HarmonyPatch(typeof(GreyScriptHelperServer), "PrepareTerminalServerRpc")]
    internal static class CaptureUserPatch
    {
        private static void Prefix(string propActiveUser, int terminalPID)
        {
            if (!string.IsNullOrEmpty(propActiveUser) && !Bridge.IsHeadless(terminalPID))
                GameRefs.LastActiveUser = propActiveUser;
        }
    }

    // Headless output is delivered via the AddPendingOutput hook below (a one-shot command often
    // exits before the throttled 200ms SendPendingPrint flush ever fires). Here we just suppress
    // the client send for headless PIDs so nothing goes to a non-existent window.
    [HarmonyPatch(typeof(GreyScriptHelperServer), "SendPrintToClient")]
    internal static class OutputSuppressPatch
    {
        private static bool Prefix(int windowPID)
        {
            return !Bridge.IsHeadless(windowPID); // false => skip original (suppress)
        }
    }

    // Server-authoritative prompt info (user / host / cwd). Captured per PID; suppressed for ours.
    [HarmonyPatch(typeof(PlayerUtils), "UpdateTermStatusClient")]
    internal static class TermStatusPatch
    {
        private static bool Prefix(GreyInterpreter interpreter, string activeUser,
                                   string publicIP, string deviceName, string currentPath)
        {
            try
            {
                int pid = interpreter.terminalPID;
                if (Bridge.RouteTermStatus(pid, activeUser, deviceName, currentPath))
                    return false;
            }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("TermStatus: " + e); }
            return true;
        }
    }

    // Primary output path for headless sessions: every line a script prints lands here. We route
    // it straight to the socket (reliable, unlike the throttled flush). Only for our PIDs.
    [HarmonyPatch(typeof(GreyInterpreter), "AddPendingOutput")]
    internal static class OutputRoutePatch
    {
        private static void Postfix(GreyInterpreter __instance, string output)
        {
            try
            {
                if (!string.IsNullOrEmpty(output) && Bridge.IsHeadless(__instance.terminalPID))
                    Bridge.RouteRawText(__instance.terminalPID, output + "\n");
            }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("OutputRoute: " + e); }
        }
    }

    // cd sends its own client RPC with the new path. Capture it so our tracked cwd (and prompt,
    // and the cwd passed to the next command) stays correct. Suppress for headless PIDs.
    [HarmonyPatch(typeof(PlayerClientMethods), "CdTerminalClientRpc")]
    internal static class CdCapturePatch
    {
        private static bool Prefix(string path, int terminalPID)
        {
            try { if (Bridge.UpdateCwd(terminalPID, path)) return false; }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("Cd: " + e); }
            return true;
        }
    }

    // A script/command finished in a session. For headless PIDs this advances our shell loop
    // (run the next queued command or show the prompt).
    [HarmonyPatch(typeof(GreyScriptHelperServer), "PostEndScript")]
    internal static class PostEndScriptPatch
    {
        private static void Postfix(int terminalPID)
        {
            try
            {
                if (Plugin.Log != null && Bridge.IsHeadless(terminalPID))
                    Plugin.Log.LogInfo("POSTEND pid=" + terminalPID);
                Bridge.OnCommandComplete(terminalPID);
            }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("PostEndScript: " + e); }
        }
    }

    // A running program is about to block for a line of input. For headless PIDs this is our cue
    // to forward the program's input request to the socket.
    [HarmonyPatch(typeof(GreyInterpreter), "WaitInput")]
    internal static class WaitInputPatch
    {
        private static void Prefix(GreyInterpreter __instance, string msgInput, bool isPassword)
        {
            try
            {
                int pid = __instance.terminalPID;
                if (Bridge.IsHeadless(pid))
                    Bridge.NotifyReadyForInput(pid, msgInput, isPassword);
            }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("WaitInput: " + e); }
        }
    }
}
