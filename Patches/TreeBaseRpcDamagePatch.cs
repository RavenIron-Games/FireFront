using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Patches
{
    /// <summary>
    /// Three jobs on one prefix, in priority order, because Harmony runs every prefix of a
    /// method and only ONE of them may decide to skip the original:
    ///
    /// 1. The fire's own unseen damage tick (a HitData marked with
    ///    <see cref="CharredTreeLifecycle.TickMarker"/>, routed here by the server because this
    ///    peer owns the tree). Applied to the ZDO health directly and nothing else — vanilla's
    ///    RPC_Damage would zero it (every tree is fire-Immune), and would broadcast floating
    ///    damage text to every peer within 30 m even if it did not. Logged under firedebug.
    ///    The original is skipped.
    ///
    /// 2. A hit on a CHARRED tree. Vanilla's body is replayed for the visible parts (resistance,
    ///    tool tier, damage text, shake, hit effect, noise) but the death goes to the charred
    ///    lifecycle: coal only, no felled log, no stub, no seeds. The original is skipped.
    ///
    /// 3. Ignition, exactly as before (0.7.x): read the raw fire component BEFORE resistance —
    ///    which is why this must be a prefix — and light the tree on the server. The original
    ///    runs.
    ///
    /// Signature verified: TreeBase.RPC_Damage(long sender, HitData hit)
    /// </summary>
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.RPC_Damage))]
    public static class TreeBaseRpcDamagePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(TreeBase __instance, long sender, HitData hit)
        {
            if (hit == null) return true;
            ZNetView nv = __instance.GetComponent<ZNetView>();

            // 1. Unseen fire tick.
            if (hit.m_statusEffectHash == CharredTreeLifecycle.TickMarker)
            {
                // The same two guards as HandleFireDamage, for the same reasons. The sender
                // check is a filter, not an authenticator - ZRoutedRpc never stamps the real
                // sender - but it stops ordinary client traffic. The finite check is the one
                // that matters: this writes ZDOVars.s_health, which replicates to every peer
                // and is SAVED with the world, and `NaN < floor` is false so the clamp in
                // ApplyTickToZdo would wave a NaN straight through.
                if (!ValheimBridge.IsFromServer(sender)) return false;
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) return false; // vanilla's own owner gate
                float damage = hit.m_damage.m_fire;
                if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return false;
                float max = __instance.m_health;
                float next = CharredTreeLifecycle.ApplyTickToZdo(nv.GetZDO(), damage, max);
                FireLogger.Debug($"[TREE-HP] {ValheimBridge.NameOf(__instance)}: -{damage:F2} -> {next:F1}/{max:F0} (owner-side tick)");
                return false;
            }

            // 2. Charred tree: vanilla's visible hit, our death.
            if (CharredTreeLifecycle.IsCharred(nv))
            {
                CharredHit(__instance, nv, hit);
                return false;
            }

            // 3. Ignition.
            if (FireManager.Instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] TreeBase.RPC_Damage: FireManager.Instance is null, bailing.");
                return true;
            }
            if (ValheimBridge.SuppressIgnition.Contains(__instance))
            {
                FireLogger.Debug($"[IGNITE-TRACE] TreeBase.RPC_Damage on {ValheimBridge.NameOf(__instance)}: suppressed (already being killed by us).");
                return true;
            }

            float fireDamage = ValheimBridge.FireDamageOf(hit);
            FireLogger.Debug($"[IGNITE-TRACE] TreeBase.RPC_Damage on {ValheimBridge.NameOf(__instance)}: fire damage = {fireDamage}");
            if (fireDamage <= 0f) return true;

            // See WearNTearRpcDamagePatch for why this branches on server
            // authority — RPC_Damage runs on whichever peer owns the ZDO, which
            // is very often a client, not the server.
            long igniter = ValheimBridge.AttackerPlayerId(hit);   // attacker, not sender — see WearNTear patch

            if (ValheimBridge.IsServer())
            {
                FireLogger.Debug($"[IGNITE-TRACE] IsServer=True — igniting {ValheimBridge.NameOf(__instance)} directly (igniter={igniter}).");
                FireManager.Instance.TryIgnite(__instance, igniter);
            }
            else
            {
                ZDOID? id = ValheimBridge.ZDOIDOf(__instance);
                if (id.HasValue)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — forwarding ignite request for {ValheimBridge.NameOf(__instance)}, ZDOID={id.Value}.");
                    ValheimBridge.SendIgniteRequestToServer(id.Value, igniter);
                }
                else
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — couldn't resolve ZDOID for {ValheimBridge.NameOf(__instance)}, request NOT sent.");
                }
            }
            return true;
        }

        /// <summary>
        /// TreeBase.RPC_Damage (Decompiled_1.0.15 TreeBase.cs:101-166) minus its death branch.
        /// Kept line-for-line where it is visible to the player so a charred tree chops like a
        /// tree; the stats bookkeeping is left out (it is not a wood chop).
        /// </summary>
        private static void CharredHit(TreeBase tree, ZNetView nv, HitData hit)
        {
            if (!nv.IsOwner()) return;
            ZDO zdo = nv.GetZDO();
            float max = tree.m_health;
            float health = zdo.GetFloat(ZDOVars.s_health, max);
            if (health <= 0f)
            {
                nv.Destroy();
                return;
            }

            bool majorityFire = hit.m_damage.GetMajorityDamageType() == HitData.DamageType.Fire;
            bool cinder = hit.m_hitType == HitData.HitType.CinderFire;
            hit.ApplyResistance(tree.m_damageModifiers, out HitData.DamageModifier significant);
            float total = hit.GetTotalDamage();
            if (!hit.CheckToolTier(tree.m_minToolTier, alwaysAllowTierZero: true))
            {
                if (DamageText.instance != null) DamageText.instance.ShowText(DamageText.TextType.TooHard, hit.m_point, 0f);
                return;
            }
            if (DamageText.instance != null) DamageText.instance.ShowText(significant, hit.m_point, total);
            if (total <= 0f) return;

            health -= total;
            zdo.Set(ZDOVars.s_health, health);
            if (!majorityFire && !cinder) nv.InvokeRPC(ZNetView.Everybody, "RPC_Shake");
            if (!cinder)
            {
                tree.m_hitEffect.Create(hit.m_point, Quaternion.identity, tree.transform);
                Player closest = Player.GetClosestPlayer(tree.transform.position, 10f);
                if (closest != null) closest.AddNoise(100f);
            }

            if (health > 0f) return;

            // Death: coal, the fall, gone — never SpawnLog.
            CharredTreeController controller = CharredTreeController.Ensure(tree.gameObject);
            if (controller != null) controller.OnChoppedDown(hit.m_dir);
            else CharredTreeLifecycle.CollapseStandingTree(tree, nv, hit.m_dir);
        }
    }
}
