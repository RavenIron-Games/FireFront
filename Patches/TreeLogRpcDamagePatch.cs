using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Patches
{
    /// <summary>
    /// The TreeLog twin of TreeBaseRpcDamagePatch: the fire's unseen tick, a hit on a charred
    /// log, and ignition, in that order. See that file for why each exists.
    ///
    /// Signature verified: TreeLog.RPC_Damage(long sender, HitData hit)
    /// </summary>
    [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.RPC_Damage))]
    public static class TreeLogRpcDamagePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(TreeLog __instance, HitData hit)
        {
            if (hit == null) return true;
            ZNetView nv = __instance.GetComponent<ZNetView>();

            // 1. Unseen fire tick.
            if (hit.m_statusEffectHash == CharredTreeLifecycle.TickMarker)
            {
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
                float max = CharredTreeLifecycle.LogMaxHealth(__instance.m_health);
                float next = CharredTreeLifecycle.ApplyTickToZdo(nv.GetZDO(), hit.m_damage.m_fire, max);
                FireLogger.Debug($"[TREE-HP] {ValheimBridge.NameOf(__instance)}: -{hit.m_damage.m_fire:F2} -> {next:F1}/{max:F0} (owner-side tick)");
                return false;
            }

            // 2. Charred log: vanilla's visible hit, our death.
            if (CharredTreeLifecycle.IsCharred(nv))
            {
                CharredHit(__instance, nv, hit);
                return false;
            }

            // 3. Ignition.
            if (FireManager.Instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] TreeLog.RPC_Damage: FireManager.Instance is null, bailing.");
                return true;
            }
            if (ValheimBridge.SuppressIgnition.Contains(__instance))
            {
                FireLogger.Debug($"[IGNITE-TRACE] TreeLog.RPC_Damage on {ValheimBridge.NameOf(__instance)}: suppressed (already being killed by us).");
                return true;
            }

            float fireDamage = ValheimBridge.FireDamageOf(hit);
            FireLogger.Debug($"[IGNITE-TRACE] TreeLog.RPC_Damage on {ValheimBridge.NameOf(__instance)}: fire damage = {fireDamage}");
            if (fireDamage <= 0f) return true;

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

        /// <summary>TreeLog.RPC_Damage (Decompiled_1.0.15 TreeLog.cs:155-213) minus Destroy: no half logs, no wood.</summary>
        private static void CharredHit(TreeLog log, ZNetView nv, HitData hit)
        {
            if (!nv.IsOwner()) return;
            ZDO zdo = nv.GetZDO();
            float health = zdo.GetFloat(ZDOVars.s_health);
            if (health <= 0f) return;

            hit.ApplyResistance(log.m_damages, out HitData.DamageModifier significant);
            float total = hit.GetTotalDamage();
            if (!hit.CheckToolTier(log.m_minToolTier, alwaysAllowTierZero: true))
            {
                if (DamageText.instance != null) DamageText.instance.ShowText(DamageText.TextType.TooHard, hit.m_point, 0f);
                return;
            }
            Rigidbody body = log.GetComponent<Rigidbody>();
            if (body != null) body.AddForceAtPosition(hit.m_dir * hit.m_pushForce * 2f, hit.m_point, ForceMode.Impulse);
            if (DamageText.instance != null) DamageText.instance.ShowText(significant, hit.m_point, total);
            if (total <= 0f) return;

            health -= total;
            if (health < 0f) health = 0f;
            zdo.Set(ZDOVars.s_health, health);
            if (hit.m_hitType != HitData.HitType.CinderFire)
            {
                log.m_hitEffect.Create(hit.m_point, Quaternion.identity, log.transform);
                if (log.m_hitNoise > 0f)
                {
                    Player closest = Player.GetClosestPlayer(log.transform.position, 10f);
                    if (closest != null) closest.AddNoise(log.m_hitNoise);
                }
            }

            if (health > 0f) return;

            CharredTreeController controller = CharredTreeController.Ensure(log.gameObject);
            if (controller != null) controller.OnChoppedDown(hit.m_dir);
            else CharredTreeLifecycle.CrumbleLog(log, nv);
        }
    }
}
