using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// 1.0.5: a creature set alight lights what it passes. A bear hit with a torch, a skeleton shot
    /// with a fire arrow: vanilla gives each the Burning status effect, which until now only hurt the
    /// creature itself.
    ///
    /// Runs on every machine, for the creatures THAT machine owns, because a creature is simulated by
    /// its owner (the nearby player's game, not a dedicated server, which holds no instance). About
    /// once a second per qualifying creature the owner reports the creature's position:
    ///  - on the server or a host, straight to <see cref="FireManager.IgniteFromBurningCreature"/>;
    ///  - on a client, over FireFront_CreatureFire, which the server checks first (a real creature,
    ///    owned by the sender, where the report says it is; per-creature and per-sender rates).
    /// The server decides everything else: whether the feature is on, the radius, and every rule any
    /// other fire obeys (Ashlands, water, dungeons, rain, BurnPlayerBuildings).
    ///
    /// Which creatures carry fire: a non-player, untamed creature with vanilla's Burning effect, for
    /// <see cref="CarryWindowSeconds"/> after a FIRE HIT (Character.RPC_Damage with fire damage: a
    /// torch, a fire arrow, a hearth, a creature's fire attack). FireFront's own flames set a
    /// creature burning without any hit (ValheimBridge.ApplyFireDamageTick adds the effect
    /// directly), so they hurt it but never make it a carrier. Found by the 1.0.5 review and seen in
    /// the first test: a creature standing in the fire it had started was set alight again every
    /// tick, carried fire for minutes and started a fresh fire event every ~56 m. Tames are left out
    /// on purpose: a wolf that steps through a hearth would otherwise light the floor around it.
    ///
    /// Who lit it: vanilla's Burning effect records no attacker (StatusEffect.SetAttacker is empty
    /// and SE_Burning does not override it), so the hit is remembered here
    /// (Patches/CharacterRpcDamagePatch.cs) and its player id, if any, goes along with the report.
    /// </summary>
    public static class CreatureFireWatch
    {
        public const float CarryWindowSeconds = 8f;   // vanilla's Burning runs a few seconds; re-hits refresh it
        private const float ReportIntervalSeconds = 1f;
        private const float PruneIntervalSeconds = 10f;
        private const float InteriorHeight = 3000f;   // same line FireManager uses for dungeons
        private const int MaxReportsPerTick = 8;      // one machine's sends per second, at most

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
        /// From the Character.RPC_Damage prefix, on the creature's owner: something's fire hit this
        /// creature. A player's id is kept over a later hit that names no player, within the window,
        /// so a bear torched by a player and then singed by a hearth stays that player's fire.
        /// </summary>
        public static void NoteFireHit(Character creature, HitData hit)
        {
            if (creature == null || hit == null) return;
            if (hit.m_damage.m_fire <= 0f) return;
            if (creature.IsPlayer() || !creature.IsOwner()) return;

            float now = Time.time;
            ZDOID id = creature.GetZDOID();
            long playerId = ValheimBridge.AttackerPlayerId(hit);
            if (playerId == 0L && _lastFireHit.TryGetValue(id, out FireHit prev) && now - prev.At <= CarryWindowSeconds)
                playerId = prev.PlayerId;
            _lastFireHit[id] = new FireHit { PlayerId = playerId, At = now };
        }

        /// <summary>Every frame from FireManager.Update, on every machine. Cheap between reports.</summary>
        public static void Tick()
        {
            float now = Time.time;
            if (now >= _nextPruneAt) Prune(now);
            if (now < _nextReportAt) return;
            _nextReportAt = now + ReportIntervalSeconds;

            if (_lastFireHit.Count == 0) return; // no recent fire hit on anything this machine owns
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
                ZDOID id = c.GetZDOID();
                if (!_lastFireHit.TryGetValue(id, out FireHit hit) || now - hit.At > CarryWindowSeconds) continue;

                Vector3 pos = c.transform.position;
                if (pos.y > InteriorHeight) continue; // dungeons: the server refuses these anyway
                if (c.InWater()) continue;            // nothing to light, and vanilla douses it soon

                if (isServer) FireManager.Instance.IgniteFromBurningCreature(pos, hit.PlayerId);
                else ValheimBridge.SendCreatureFireToServer(id, pos, hit.PlayerId);
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
                if (now - kv.Value.At > CarryWindowSeconds) _scratch.Add(kv.Key);
            foreach (ZDOID id in _scratch) _lastFireHit.Remove(id);
            _scratch.Clear();
        }
    }
}
