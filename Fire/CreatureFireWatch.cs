using System.Collections.Generic;
using System.Reflection;
using FireFront.Config;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// 1.0.5: a creature on fire spreads fire as it runs. A bear hit with a torch, a skeleton shot
    /// with a fire arrow, a boar that ran through a blaze: vanilla gives each the Burning status
    /// effect, which until now only hurt the creature itself.
    ///
    /// Runs on every machine, for the creatures THAT machine owns, because a creature is simulated by
    /// its owner (the nearby player's game, not a dedicated server, which holds no instance). Once a
    /// second, every non-player, untamed creature it owns that is burning is reported:
    ///  - on the server or a host, straight to <see cref="FireManager.IgniteFromBurningCreature"/>;
    ///  - on a client, over FireFront_CreatureFire, which the server checks first (a real creature,
    ///    owned by the sender, where the report says it is; per-creature and per-sender rates).
    /// The server decides everything else, and bounds it per creature: whether the feature is on,
    /// the radii, how long one burn may carry fire (BurningCreatureCarrySeconds), lighting the path
    /// between reports, and every rule any other fire obeys (Ashlands, water, dungeons, rain,
    /// BurnPlayerBuildings). The per-burn cap lives on the server, not here, so it holds whatever a
    /// client sends: before it, a creature standing in the fire it had started was set burning again
    /// every tick and carried fire for minutes (1.0.5 review, and the first test).
    ///
    /// The longer burn (BurningCreatureBurnSeconds, 12 s): vanilla's Burning lasts about 5 s, too short
    /// for a creature to carry fire anywhere (the second test: every burn ended at 4 s, after 1-4 m).
    /// So for that long after a real FIRE HIT (Character.RPC_Damage with fire damage: a torch, a fire
    /// arrow, a hearth) the owner keeps the effect from running out. It adds no damage: SE_Burning
    /// keeps dealing m_fireDamagePerHit every tick for as long as it lasts, so once vanilla's damage
    /// pool (m_fireDamageLeft) is spent the per-tick damage is set to 0, which Character.ApplyDamage
    /// drops silently (anything at or under 0.1 shows no number). A new fire hit refills the pool the
    /// vanilla way. FireFront's own flames set a creature burning with no hit, so they are never
    /// lengthened. Wet or swimming creatures are left to vanilla, which puts them out faster.
    ///
    /// Tames are left out on purpose: a wolf that steps through a hearth would otherwise light the
    /// floor around it.
    ///
    /// Who lit it: vanilla's Burning effect records no attacker (StatusEffect.SetAttacker is empty
    /// and SE_Burning does not override it), so the last player whose fire hit each creature is
    /// remembered here (Patches/CharacterRpcDamagePatch.cs) and goes along with the report. A
    /// creature nobody's fire hit lately (one that ran into a blaze, say) is booked to nobody.
    /// </summary>
    public static class CreatureFireWatch
    {
        private const float ReportIntervalSeconds = 1f;
        private const float IgniterMemorySeconds = 60f;
        private const float PruneIntervalSeconds = 10f;
        private const float InteriorHeight = 3000f;   // same line FireManager uses for dungeons
        private const int MaxReportsPerTick = 8;      // one machine's sends per second, at most

        private struct FireHits
        {
            public float AnyAt;      // last fire hit of any kind (for the longer burn); 0 = none
            public long PlayerId;    // last player whose fire hit it (for the blame)
            public float PlayerAt;
        }

        private static readonly Dictionary<ZDOID, FireHits> _hits = new Dictionary<ZDOID, FireHits>();
        private static readonly List<Character> _burning = new List<Character>();
        private static readonly List<ZDOID> _scratch = new List<ZDOID>();
        private static float _nextReportAt;
        private static float _nextPruneAt;

        // Private and protected in the real assembly, so by reflection. Missing -> no longer burn
        // (vanilla's ~5 s), logged once; never a throw.
        private static readonly FieldInfo SeTimeField = AccessTools.Field(typeof(StatusEffect), "m_time");
        private static readonly FieldInfo FireDamageLeftField = AccessTools.Field(typeof(SE_Burning), "m_fireDamageLeft");
        private static readonly FieldInfo FireDamagePerHitField = AccessTools.Field(typeof(SE_Burning), "m_fireDamagePerHit");
        private static bool _burnExtendFailureLogged;

        /// <summary>New world or new connection: nothing from the old one carries over.</summary>
        public static void Reset()
        {
            _hits.Clear();
            _burning.Clear();
            _nextReportAt = 0f;
            _nextPruneAt = 0f;
        }

        /// <summary>
        /// From the Character.RPC_Damage prefix, on the creature's owner: something's fire hit this
        /// creature. Any fire hit starts the longer burn; a player's also books the fire to them.
        /// </summary>
        public static void NoteFireHit(Character creature, HitData hit)
        {
            if (creature == null || hit == null) return;
            if (hit.m_damage.m_fire <= 0f) return;
            if (creature.IsPlayer() || !creature.IsOwner()) return;

            float now = Time.time;
            ZDOID id = creature.GetZDOID();
            _hits.TryGetValue(id, out FireHits h);
            h.AnyAt = now;
            long playerId = ValheimBridge.AttackerPlayerId(hit);
            if (playerId != 0L) { h.PlayerId = playerId; h.PlayerAt = now; }
            _hits[id] = h;
        }

        /// <summary>Every frame from FireManager.Update, on every machine. Cheap between reports.</summary>
        public static void Tick()
        {
            float now = Time.time;
            if (now >= _nextPruneAt) Prune(now);
            if (now < _nextReportAt) return;
            _nextReportAt = now + ReportIntervalSeconds;

            if (!FireConfig.Enabled.Value) return;
            bool isServer = ValheimBridge.IsServer();
            if (!isServer && !ValheimBridge.CanReachServer()) return;
            if (isServer && FireManager.Instance == null) return;

            ValheimBridge.CollectOwnedBurningCreatures(_burning);
            float burnSeconds = Mathf.Clamp(FireConfig.BurningCreatureBurnSeconds.Value, 0f, 30f);
            int sent = 0;
            for (int i = 0; i < _burning.Count && sent < MaxReportsPerTick; i++)
            {
                Character c = _burning[i];
                if (c == null || c.IsTamed()) continue;
                Vector3 pos = c.transform.position;
                if (pos.y > InteriorHeight) continue; // dungeons: the server refuses these anyway
                if (c.InWater()) continue;            // nothing to light, and vanilla douses it soon

                ZDOID id = c.GetZDOID();
                _hits.TryGetValue(id, out FireHits h);
                if (h.AnyAt > 0f && now - h.AnyAt < burnSeconds && FireConfig.BurningCreaturesSpreadFire.Value)
                    KeepBurning(c, h.AnyAt + burnSeconds - now);

                long igniter = h.PlayerAt > 0f && now - h.PlayerAt <= IgniterMemorySeconds ? h.PlayerId : 0L;
                if (isServer) FireManager.Instance.IgniteFromBurningCreature(id, pos, igniter);
                else ValheimBridge.SendCreatureFireToServer(id, pos, igniter);
                sent++;
            }
            if (sent > 0) FireLogger.Debug($"[CREATURE-FIRE] {sent} burning creature(s) reported{(isServer ? " (handled here)" : " to the server")}.");
            _burning.Clear();
        }

        /// <summary>
        /// Keeps vanilla's Burning effect on <paramref name="c"/> from ending for <paramref name="left"/>
        /// more seconds (never more than its own ttl at a time; this runs every second), and stops it
        /// dealing more than vanilla's own damage. Leaves a wet creature alone.
        /// </summary>
        private static void KeepBurning(Character c, float left)
        {
            if (SeTimeField == null || FireDamageLeftField == null || FireDamagePerHitField == null)
            {
                if (!_burnExtendFailureLogged)
                {
                    _burnExtendFailureLogged = true;
                    FireLogger.Warn("[CREATURE-FIRE] could not reach SE_Burning's fields; creatures burn for vanilla's time only.");
                }
                return;
            }
            try
            {
                SEMan seman = c.GetSEMan();
                if (seman == null || seman.HaveStatusEffect(SEMan.s_statusEffectWet)) return;
                if (!(seman.GetStatusEffect(SEMan.s_statusEffectBurning) is SE_Burning se)) return;

                // Vanilla's damage is spent: the rest of the burn is for show and for spreading.
                if ((float)FireDamageLeftField.GetValue(se) <= 0f) FireDamagePerHitField.SetValue(se, 0f);

                float remaining = se.GetRemaningTime();
                float want = Mathf.Min(left, se.m_ttl);
                if (remaining < want) SeTimeField.SetValue(se, se.m_ttl - want);
            }
            catch (System.Exception ex)
            {
                if (!_burnExtendFailureLogged)
                {
                    _burnExtendFailureLogged = true;
                    FireLogger.Warn($"[CREATURE-FIRE] could not lengthen a burn ({ex.GetType().Name}: {ex.Message}); creatures burn for vanilla's time only.");
                }
            }
        }

        private static void Prune(float now)
        {
            _nextPruneAt = now + PruneIntervalSeconds;
            if (_hits.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<ZDOID, FireHits> kv in _hits)
                if (now - kv.Value.AnyAt > 30f && now - kv.Value.PlayerAt > IgniterMemorySeconds) _scratch.Add(kv.Key);
            foreach (ZDOID id in _scratch) _hits.Remove(id);
            _scratch.Clear();
        }
    }
}
