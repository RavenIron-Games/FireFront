using System.Reflection;
using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Patches
{
    /// <summary>
    /// 1.0.6: a player's fire projectile (fire arrow, fire staff bolt, anything carrying fire damage)
    /// that lands on bare terrain lights the grass there. Before this, grass caught only from a
    /// burning tree, log, piece or creature, so a fire arrow into open grass (most of the Plains)
    /// did nothing.
    ///
    /// Vanilla runs Projectile.OnHit on the projectile's owner only (FixedUpdate returns early on
    /// every other peer), so each landing is reported once. A bounce returns from OnHit before any
    /// hit logic and is skipped here too; the projectile reports where it finally lands. Terrain
    /// only (a Heightmap collider): a hit on a tree, log or building already lights that object
    /// through its own damage RPC. Players only: a Fuling shaman's or Surtling's fire never lights
    /// grass, or a village fight would burn the village down (the 1.0.6 village fix).
    ///
    /// Fields are read by reflection per the bridge's publicized-DLL rule.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "OnHit")]
    public static class ProjectileOnHitGroundFirePatch
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo DamageField = typeof(Projectile).GetField("m_damage", Any);
        private static readonly FieldInfo OwnerField = typeof(Projectile).GetField("m_owner", Any);
        private static readonly FieldInfo DidBounceField = typeof(Projectile).GetField("m_didBounce", Any);
        private static bool _thrown;

        private static void Postfix(Projectile __instance, Collider collider, Vector3 hitPoint, bool water)
        {
            try
            {
                if (water || collider == null || __instance == null) return;
                if (!FireConfig.Enabled.Value || !FireConfig.FireProjectilesLightGrass.Value) return;
                if (collider.GetComponent<Heightmap>() == null) return;
                if (DidBounceField != null && (bool)DidBounceField.GetValue(__instance)) return;
                if (DamageField == null || OwnerField == null) return;

                var damage = (HitData.DamageTypes)DamageField.GetValue(__instance);
                if (damage.m_fire <= 0f) return;
                if (!(OwnerField.GetValue(__instance) is Player player)) return;

                long igniter = player.GetPlayerID();
                if (ValheimBridge.IsServer())
                    FireManager.Instance?.IgniteGroundFromProjectile(hitPoint, igniter);
                else
                    ValheimBridge.SendGroundIgniteToServer(hitPoint, igniter);
                FireLogger.Debug($"[GROUND-IGNITE] fire projectile from player {igniter} landed on the ground at " +
                                 $"({hitPoint.x:F1},{hitPoint.y:F1},{hitPoint.z:F1}), fire {damage.m_fire:F0}.");
            }
            catch (System.Exception ex)
            {
                if (_thrown) return;
                _thrown = true;
                FireLogger.Warn($"[GROUND-IGNITE] OnHit postfix threw: {ex.Message}; fire projectiles will not light grass this session.");
            }
        }
    }
}
