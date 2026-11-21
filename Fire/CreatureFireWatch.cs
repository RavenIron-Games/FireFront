using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// 1.0.5: a creature that is on fire lights what it passes. A bear hit with a torch, a skeleton
    /// shot with a fire arrow, a boar that ran through a blaze: vanilla gives each the Burning status
    /// effect, which until now only hurt the creature itself.
    ///
    /// Runs on every machine, for the creatures THAT machine owns, because a creature is simulated by
    /// its owner (the nearby player's game, not a dedicated server, which holds no instance). About
    /// once a second per burning creature the owner reports the creature's position:
    ///  - on the server or a host, straight to <see cref="FireManager.IgniteFromBurningCreature"/>;
    ///  - on a client, over FireFront_CreatureFire, which the server checks (reach, rate) first.
    /// The server decides everything else: whether the feature is on, the radius, and every rule any
    /// other fire obeys (Ashlands, water, dungeons, rain, BurnPlayerBuildings).
    ///
    /// Who lit it: vanilla's Burning effect does not record an attacker (StatusEffect.SetAttacker is
    /// empty and SE_Burning does not override it). So the owner remembers the last player whose fire
    /// hit each creature (Patches/CharacterRpcDamagePatch.cs, which sees the same RPC_Damage the
    /// owner applies) and passes that id along. A creature lit by anything else, a FireFront fire
    /// included, is booked to nobody, like lightning.
    /// </summary>
    public static class CreatureFireWatch
    {
        private const float ReportIntervalSeconds = 1f;
        private const float AttackerMemorySeconds = 60f; // a burn lasts seconds; re-hits refresh it
        private const float PruneIntervalSeconds = 10f;
        private const float InteriorHeight = 3000f;   // same line FireManager uses for dungeons
        private const int MaxReportsPerTick = 24;     // a hard bound on one machine's sends per second

        private struct FireHit { public long PlayerId; public float At; }

        private static readonly Dictionary<ZDOID, FireHit> _lastFireHit = new Dictionary<ZDOID, FireHit>();
        private static readonly List<Character> _burning = new List<Character>();
        private static readonly List<ZDOID> _scratch = new List<ZDOID>();
        private static float _nextReportAt;
        private static float _nextPruneAt;

        /// <summary>New world or new connection: nothing from the old one carries over.</summary>
        public static void Reset()
        {
            _lastFireHit.Clear();
            _burning.Clear();
            _nextReportAt = 0f;
            _nextPruneAt = 0f;
        }

        /// <summary>
        /// From the Character.RPC_Damage prefix, on the creature's owner: a player's fire hit this
        /// creature, so if it is burning a moment from now, that player lit it.
        /// </summary>
        public static void NoteFireHit(Character creature, HitData hit)
        {
            if (creature == null || hit == null) return;
            if (hit.m_damage.m_fire <= 0f) return;
            if (creature.IsPlayer() || !creature.IsOwner()) return;
            long playerId = ValheimBridge.AttackerPlayerId(hit);
            if (playerId == 0L) return; // a creature's fire, or the world's: keep any player hit already noted
            _lastFireHit[creature.GetZDOID()] = new FireHit { PlayerId = playerId, At = Time.time };
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
                if (c == null) continue;
                Vector3 pos = c.transform.position;
                if (pos.y > InteriorHeight) continue; // dungeons: the server refuses these anyway
                if (c.InWater()) continue;            // nothing to light, and vanilla douses it soon

                long igniter = 0L;
                if (_lastFireHit.TryGetValue(c.GetZDOID(), out FireHit hit) && now - hit.At <= AttackerMemorySeconds)
                    igniter = hit.PlayerId;

                if (isServer) FireManager.Instance.IgniteFromBurningCreature(pos, igniter);
                else ValheimBridge.SendCreatureFireToServer(pos, igniter);
                sent++;
            }
            if (sent > 0) FireLogger.Debug($"[CREATURE-FIRE] {sent} burning creature(s) reported{(isServer ? " (handled here)" : " to the server")}.");
            _burning.Clear();
        }

        private static void Prune(float now)
        {
            _nextPruneAt = now + PruneIntervalSeconds;
            if (_lastFireHit.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<ZDOID, FireHit> kv in _lastFireHit)
                if (now - kv.Value.At > AttackerMemorySeconds) _scratch.Add(kv.Key);
            foreach (ZDOID id in _scratch) _lastFireHit.Remove(id);
            _scratch.Clear();
        }
    }
}
