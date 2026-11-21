using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
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
    /// the radius, how long one burn may carry fire (BurningCreatureCarrySeconds), lighting the path
    /// between reports, and every rule any other fire obeys (Ashlands, water, dungeons, rain,
    /// BurnPlayerBuildings). The per-burn cap lives on the server, not here, so it holds whatever a
    /// client sends: before it, a creature standing in the fire it had started was set burning again
    /// every tick and carried fire for minutes (1.0.5 review, and the first test).
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

        private struct FireHit { public long PlayerId; public float At; }

        private static readonly Dictionary<ZDOID, FireHit> _lastPlayerFireHit = new Dictionary<ZDOID, FireHit>();
        private static readonly List<Character> _burning = new List<Character>();
        private static readonly List<ZDOID> _scratch = new List<ZDOID>();
        private static float _nextReportAt;
        private static float _nextPruneAt;

        /// <summary>New world or new connection: nothing from the old one carries over.</summary>
        public static void Reset()
        {
            _lastPlayerFireHit.Clear();
            _burning.Clear();
            _nextReportAt = 0f;
            _nextPruneAt = 0f;
        }

        /// <summary>
        /// From the Character.RPC_Damage prefix, on the creature's owner: a player's fire hit this
        /// creature, so if it is burning now or soon, that player lit it.
        /// </summary>
        public static void NoteFireHit(Character creature, HitData hit)
        {
            if (creature == null || hit == null) return;
            if (hit.m_damage.m_fire <= 0f) return;
            if (creature.IsPlayer() || !creature.IsOwner()) return;
            long playerId = ValheimBridge.AttackerPlayerId(hit);
            if (playerId == 0L) return;
            _lastPlayerFireHit[creature.GetZDOID()] = new FireHit { PlayerId = playerId, At = Time.time };
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
            int sent = 0;
            for (int i = 0; i < _burning.Count && sent < MaxReportsPerTick; i++)
            {
                Character c = _burning[i];
                if (c == null || c.IsTamed()) continue;
                Vector3 pos = c.transform.position;
                if (pos.y > InteriorHeight) continue; // dungeons: the server refuses these anyway
                if (c.InWater()) continue;            // nothing to light, and vanilla douses it soon

                ZDOID id = c.GetZDOID();
                long igniter = 0L;
                if (_lastPlayerFireHit.TryGetValue(id, out FireHit hit) && now - hit.At <= IgniterMemorySeconds)
                    igniter = hit.PlayerId;

                if (isServer) FireManager.Instance.IgniteFromBurningCreature(id, pos, igniter);
                else ValheimBridge.SendCreatureFireToServer(id, pos, igniter);
                sent++;
            }
            if (sent > 0) FireLogger.Debug($"[CREATURE-FIRE] {sent} burning creature(s) reported{(isServer ? " (handled here)" : " to the server")}.");
            _burning.Clear();
        }

        private static void Prune(float now)
        {
            _nextPruneAt = now + PruneIntervalSeconds;
            if (_lastPlayerFireHit.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<ZDOID, FireHit> kv in _lastPlayerFireHit)
                if (now - kv.Value.At > IgniterMemorySeconds) _scratch.Add(kv.Key);
            foreach (ZDOID id in _scratch) _lastPlayerFireHit.Remove(id);
            _scratch.Clear();
        }
    }
}
