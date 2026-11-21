using FireFront.Fire;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// 1.0.5: remembers which player's fire last hit a creature, so a creature set alight by a torch
    /// or a fire arrow books the fire it carries to that player (Fire/CreatureFireWatch.cs).
    ///
    /// A prefix that only reads: it changes nothing and never skips the original. It reads the raw
    /// fire component before vanilla applies resistances to the shared HitData, the same reason the
    /// tree and piece patches are prefixes. RPC_Damage is private in the real assembly, so it is
    /// named by string, not nameof.
    ///
    /// Signature verified against the 1.0.21 assembly: Character.RPC_Damage(long sender, HitData hit).
    /// </summary>
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    public static class CharacterRpcDamagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Character __instance, HitData hit)
        {
            try { CreatureFireWatch.NoteFireHit(__instance, hit); }
            catch (System.Exception) { /* bookkeeping only: never break vanilla damage */ }
        }
    }
}
