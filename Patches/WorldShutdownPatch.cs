using FireFront.Fire;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// 1.0.2: saves and forgets the fire simulation when the world closes, so a host who loads
    /// another world does not carry this one's fire into it (FireManager.OnWorldShutdown).
    /// A prefix: ZNet.Shutdown(bool) saves the world and then StopAll shuts ZDOMan down, and
    /// the fire store has to be written while ZDOMan and the world still exist.
    /// Signature verified on 1.0.15: public void ZNet.Shutdown(bool save = true), called by
    /// Game.Shutdown on logout and on quit, headless or not.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    public static class WorldShutdownPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ZNet __instance)
        {
            if (__instance == null || __instance.HaveStopped) return; // StopAll runs once; so do we
            // Never let FireFront stop the game saving and shutting down.
            try { FireManager.Instance?.OnWorldShutdown(); }
            catch (System.Exception ex) { FireFront.Utils.FireLogger.Warn($"[PERSIST] world-close reset failed: {ex.Message}"); }
        }
    }
}
