using System.Collections.Generic;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Hand-rolled fire damage: periodically polls for nearby Characters via
    /// Physics.OverlapSphere (using EffectArea's own verified character layer
    /// mask) and applies fire damage directly via ValheimBridge.ApplyFireDamageTick.
    ///
    /// History: v1 tried vanilla's EffectArea/Type.Burning auto-detection —
    /// silent failure, no damage ever observed (likely gated behind
    /// Character's internal heat-accumulation threshold). v2 (this file's
    /// first version) tried OnTriggerStay/Exit on our own collider — ALSO
    /// silent failure, zero calls ever logged, most likely because our
    /// dynamically-created GameObject's default layer is blocked from
    /// generating trigger callbacks against characters by Valheim's physics
    /// collision matrix. This version polls explicitly instead, which bypasses
    /// that matrix entirely — Physics.OverlapSphere only cares about the
    /// LayerMask parameter, not the pairwise collision matrix that governs
    /// automatic trigger events.
    /// </summary>
    public class FireBurnZone : MonoBehaviour
    {
        public float Radius = 2f;
        public bool PlayerOnly;
        public float DamagePerTick = 5f;
        public float TickInterval = 1f;

        private readonly Dictionary<Character, float> _nextTickTime = new Dictionary<Character, float>();
        private readonly Collider[] _overlapBuffer = new Collider[16];
        private float _nextPollTime;

        // Shared across every FireBurnZone instance (there can be dozens at
        // once) so this stays a one-time diagnostic instead of per-zone spam.
        // Distinguishes "OverlapSphere never finds ANY collider near a fire
        // zone" (detection/mask/position problem) from "it finds colliders but
        // none resolve to a Character" (GetComponentInParent problem) from
        // "a Character IS found" (the poll loop is fine — check
        // ApplyFireDamageTick's own diagnostics instead).
        private static bool _anyOverlapLoggedOnce;
        private static bool _anyCharacterLoggedOnce;

        private void Update()
        {
            // Players are NOT damaged from here any more (0.21.8). This polls Physics.OverlapSphere,
            // and on a dedicated server there is no player Character to find - no instance, no
            // collider, only a ZDO - so fire never hurt anyone there from 0.1 to 0.21.7. Players are
            // now found from their ZDO positions by FireManager.DamagePlayersInFire and burned on
            // their own machine. This zone keeps doing what it CAN do wherever physics is real:
            // creatures, on a listen host. With PlayerOnly set there is nothing left for it at all.
            if (PlayerOnly) return;
            if (Time.time < _nextPollTime) return;
            _nextPollTime = Time.time + 0.25f; // poll a few times per tick interval for responsiveness

            LayerMask mask = ValheimBridge.GetCharacterLayerMask();
            int count = Physics.OverlapSphereNonAlloc(transform.position, Radius, _overlapBuffer, mask);

            if (count > 0 && !_anyOverlapLoggedOnce)
            {
                _anyOverlapLoggedOnce = true;
                FireLogger.Debug($"[IGNITE-TRACE] FireBurnZone: OverlapSphereNonAlloc found {count} collider(s) " +
                                  $"at {transform.position} radius {Radius}, e.g. '{(_overlapBuffer[0] != null ? _overlapBuffer[0].name : "null")}'.");
            }

            for (int i = 0; i < count; i++)
            {
                Collider col = _overlapBuffer[i];
                if (col == null) continue;

                Character character = col.GetComponentInParent<Character>();
                if (character == null) continue;

                if (!_anyCharacterLoggedOnce)
                {
                    _anyCharacterLoggedOnce = true;
                    FireLogger.Debug($"[IGNITE-TRACE] FireBurnZone: resolved a real Character ({character.gameObject.name}) " +
                                      $"from an overlapping collider — detection path works end-to-end.");
                }

                // Skipped even on a listen host, where this zone CAN see the local player, or the
                // new server-side path and this one would both burn them - twice the damage.
                if (ValheimBridge.IsPlayerCharacter(character)) continue;

                if (_nextTickTime.TryGetValue(character, out float next) && Time.time < next) continue;
                _nextTickTime[character] = Time.time + TickInterval;

                ValheimBridge.ApplyFireDamageTick(character, DamagePerTick);
                FireLogger.Debug($"Fire damage tick: {DamagePerTick} to {character.gameObject.name}");
            }
        }
    }
}
