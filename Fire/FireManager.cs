using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Owns all FireFront state. Two parallel systems that interact each cycle:
    ///
    ///   OBJECT fire: _burning (ZDOID -> BurningState: expiry/position/prefab,
    ///   captured once at ignition) + _queue (FIFO overflow, also ZDOID-keyed).
    ///   ZDOID-keyed rather than Component-keyed: a dedicated server can tear
    ///   down and later re-instantiate a target's GameObject at any point
    ///   independent of anything we do (confirmed via live testing), so a
    ///   Component reference can't be trusted to stay valid for a whole burn
    ///   duration. A live Component is only resolved just-in-time, at the two
    ///   moments that actually need one: the real kill at expiry, and
    ///   extinguish. Everything else (spread checks, VFX, damage) runs off
    ///   the cached position, no resolution needed. Targets are WearNTear/
    ///   TreeBase/TreeLog Components, dispatched by type in ValheimBridge.
    ///   Killing a target touches the real game world (destroys a GameObject,
    ///   claims ZDO ownership, etc).
    ///
    ///   GROUND fire: _groundBurning (grid cell -> expiry), pure position/timer
    ///   bookkeeping with NO real GameObject, NO ZNetView, NO vanilla object
    ///   interaction at all — zero corruption risk. This is what lets fire
    ///   cross open grassy gaps that are wider than an object could bridge:
    ///   grass itself has no game object to ignite (it's GPU-instanced visual
    ///   clutter), so ground cells are a stand-in that let the FIRE travel
    ///   even where there's nothing real for it to sit on.
    ///
    /// Cycle (every SpreadCheckInterval seconds):
    ///   1. Prune stale object entries (targets destroyed by other means)
    ///   2. Expire object burn timers -> kill targets
    ///   3. Promote queued objects into freed capacity (FIFO)
    ///   4. Expire ground cell timers
    ///   5. Spread pass: every burning object or ground cell tries to ignite
    ///      nearby objects AND nearby ground cells within its radius
    /// </summary>
    /// <summary>Per-cell state: when it goes out, and the Y it ignited at (fixed for its
    /// lifetime so a spawned VFX doesn't jitter as EstimateGroundY's inputs change).</summary>
    internal struct GroundCellState
    {
        public float ExpireAt;
        public float Y;
        public int EventId;   // which fire event this cell belongs to (0 = unassigned/legacy)
    }

    public class FireManager : MonoBehaviour
    {
        public static FireManager Instance { get; private set; }

        /// <summary>
        /// Everything _burning needs to know about a fire, captured ONCE at
        /// ignition. Position/PrefabName never change after that — pieces and
        /// trees are static, they don't move — so caching them here means VFX,
        /// damage zones, and spread checks never need to resolve a live
        /// Component at all, only the kill/extinguish moment does. This is the
        /// fix for a confirmed bug: a force-created object's GameObject can be
        /// torn down by the server's own housekeeping at any point, independent
        /// of anything we do to it (tried claiming ownership — didn't help),
        /// so a Component reference can't be trusted to stay valid for an
        /// entire burn duration on a dedicated server.
        /// </summary>
        private struct BurningState
        {
            public float ExpireAt;
            public float IgnitedAt;  // for the spread-maturity gate: a burner passes fire on only after burning a while
            public Vector3 Position;
            public string PrefabName;
            public int KillAttempts; // bounded retry if resolving a live Component at expiry fails
            public int EventId;      // which fire event this burner belongs to (0 = unassigned/legacy)
            public bool Smouldering; // its VFX has been dropped to embers+smoke (cosmetic only)
        }

        private readonly Dictionary<ZDOID, BurningState> _burning = new Dictionary<ZDOID, BurningState>();

        // Objects that were deliberately extinguished (bomb, G key, stopfire)
        // and can't re-ignite until the value — same "soaked" logic as doused
        // ground cells in ExtinguishGroundNear. Pruned each cycle; in-memory
        // only (a restart forgetting a minute of wetness is acceptable).
        private readonly Dictionary<ZDOID, float> _dousedUntil = new Dictionary<ZDOID, float>();

        // ZDOID-keyed, same reasoning as _burning: if a later re-resolution
        // returns a DIFFERENT Component reference for the same fire (the
        // server tore down and recreated the object), a Component-keyed
        // dictionary would never find the original VFX to remove it, leaking
        // it. Spawned once at ignition at the cached position, unparented —
        // not attached to the target's transform — so it's fully independent
        // of whatever happens to the underlying vanilla instance afterward.
        private readonly Dictionary<ZDOID, GameObject> _vfx = new Dictionary<ZDOID, GameObject>();

        // Non-authoritative VFX shown on peers that are NOT the server, driven by
        // FireEvent broadcasts rather than local simulation. Deliberately a
        // separate dictionary from _vfx (which is the server's real, damage-
        // capable fire) — this mirror is visual only, no damage zone, so a
        // client watching someone else's fire can't accidentally deal damage
        // from a copy that only exists locally on their own machine.
        private readonly Dictionary<Component, GameObject> _remoteVfx = new Dictionary<Component, GameObject>();
        // The ZRoutedRpc INSTANCE the handlers are registered on — not a bool.
        // Valheim creates a fresh ZRoutedRpc per connection, so a client that
        // gets kicked (server restart) and auto-reconnects IN THE SAME PROCESS
        // gets a new instance whose m_functions has no FireFront handlers; the
        // old one-shot bool never re-registered, and every server broadcast
        // was then dropped silently at the hash lookup — fire burned invisibly
        // until the next full client relaunch. Found live 2026-08-27 with the
        // [SYNC-DIAG] instrumentation: send side flawless (peer listed, ready),
        // receipts zero, then everything worked after a fresh launch.
        private ZRoutedRpc _registeredRpcInstance;

        // Ground-fire sync: server-side accumulation of cells that started/
        // stopped burning since the last batched flush (see FlushGroundFireSync),
        // and the client-side visual-only mirror those flushes drive.
        private readonly List<(GroundCellKey key, float y)> _groundIgnitedSinceFlush = new List<(GroundCellKey, float)>();
        private readonly List<GroundCellKey> _groundExpiredSinceFlush = new List<GroundCellKey>();
        private readonly Dictionary<GroundCellKey, GameObject> _remoteGroundVfx = new Dictionary<GroundCellKey, GameObject>();
        private float _nextGroundSyncFlush;
        private const float GroundSyncFlushInterval = 1f;

        // Headless dedicated servers have no interactive console to type
        // 'firestatus' into — this logs the same status line automatically so
        // there's still visibility into what the server-side simulation is
        // actually doing, without needing console access.
        private float _nextStatusHeartbeat;
        private const float StatusHeartbeatInterval = 15f;
        private readonly FireQueue _queue = new FireQueue();

        private readonly Dictionary<GroundCellKey, GroundCellState> _groundBurning = new Dictionary<GroundCellKey, GroundCellState>();
        private readonly Dictionary<GroundCellKey, GameObject> _groundVfx = new Dictionary<GroundCellKey, GameObject>();

        // Cells that have already burned out during the CURRENT fire event and won't
        // reignite until the fire dies out entirely (cleared alongside _fireStartTime
        // reset and in ClearAll). Without this, a fully-consumed cell was free to be
        // re-ignited by a neighbor the very next cycle, producing an endless churn
        // over the same small footprint instead of an advancing front.
        // Cells that have already burned out recently and won't reignite until
        // GroundFuelRegrowSeconds has passed (value = the Time.time they become
        // eligible again). Time-bounded rather than "until the whole fire dies" —
        // a long player-sustained fire that never fully goes out would otherwise
        // grow this collection without limit for the entire session (observed:
        // 9,780+ entries and climbing in a single extended test, never pruned).
        // Pruned each cycle in ExpireGroundTimers alongside the existing sweeps.
        private readonly Dictionary<GroundCellKey, float> _groundExhausted = new Dictionary<GroundCellKey, float>();

        // Cells that have already been really painted (UseVanillaDirtPaint) at
        // least once. Real terrain paint is permanent/persisted-to-disk, so
        // repainting an already-dirt cell is both redundant and, since exhaustion
        // became time-bounded, a genuine leak: a sustained fire can reignite the
        // same cell repeatedly, and each burnout used to spawn a NEW permanent
        // ZNetView-backed piece on top of the last one at the same spot. Painted
        // once, never repainted — deliberately NOT cleared on fire-death (unlike
        // _groundExhausted), since the real paint itself never went away either.
        private readonly HashSet<GroundCellKey> _groundPainted = new HashSet<GroundCellKey>();

        // Burned-cell positions waiting for the next batched real-dirt paint flush.
        // Painting per-burnout was a find+paint+save round trip per cell; batching
        // once per second groups cells by zone TerrainComp and saves once per comp.
        private readonly List<Vector3> _pendingPaint = new List<Vector3>();
        private float _nextPaintFlush;
        private const float PaintFlushInterval = 1f;

        // TTL cache for IsClearedOrCultivated terrain lookups, keyed by ground
        // cell. The firebreak line check samples terrain at half-cell steps along
        // every origin→destination pair during radius ignition — during a heavy
        // burn that's tens of thousands of reflected Heightmap lookups per second
        // re-sampling the same ground (measured 50-60 FPS lost). Terrain paint
        // only changes when someone actively cultivates, so short-TTL caching is
        // safe: worst case, a line cultivated while fire is actively approaching
        // takes up to TTL seconds to register as a break.
        private readonly Dictionary<GroundCellKey, (float expiresAt, bool isBreak)> _firebreakCache =
            new Dictionary<GroundCellKey, (float, bool)>();
        private readonly List<GroundCellKey> _firebreakCacheScratch = new List<GroundCellKey>();
        private float _nextFirebreakCachePrune;
        private const float FirebreakCacheTtl = 5f;

        /// <summary>
        /// One tree pending regrowth: captured at the moment the original tree
        /// finished burning down (name + position), replayed once RegrowAt is
        /// reached. In-memory only — does not survive a server restart.
        /// </summary>
        /// <summary>
        /// An ignite request whose ZDOID resolved to a real, known ZDO but had no
        /// local GameObject instantiated yet on the server — confirmed via a live
        /// dedicated-server test to be a real, recurring race: a freshly-connected
        /// player's surroundings can take a few seconds for the server's
        /// ZNetScene to finish instantiating, even though ZDOMan already has the
        /// object's data. Retried on a short backoff instead of giving up on the
        /// first attempt, same reasoning as the tree-regrowth retry queue below.
        /// </summary>
        private struct PendingIgniteResolution
        {
            public ZDOID Id;
            public float RetryAt;
            public int Attempts;
            public long IgniterPlayerId;   // carried so a delayed resolution still attributes
        }

        private readonly List<PendingIgniteResolution> _pendingIgniteResolutions = new List<PendingIgniteResolution>();
        private readonly List<int> _igniteResolutionScratchIndices = new List<int>();

        private struct PendingRegrowth
        {
            public string PrefabName;
            public Vector3 Position;
            public float RegrowAt;
            public int Attempts; // real spawn failures only (0.21.5): a spot someone built over is
                                 // dropped outright, and nothing is "deferred" any more
        }

        private readonly List<PendingRegrowth> _pendingRegrowth = new List<PendingRegrowth>();
        private readonly List<int> _regrowthScratchIndices = new List<int>();

        // One sector scan serves a whole cluster of due regrowth entries (0.21.5). The scan covers
        // the 3x3 zone block around its centre, 192 m, so any point within 32 m of that centre has
        // its own 3 m neighbourhood comfortably inside the block and can reuse the result. Beyond
        // that the scan is redone and re-centred. Reused only within a single cycle.
        private readonly List<Vector3> _builtNearScratch = new List<Vector3>();
        private Vector3 _builtScanCentre;
        private bool _builtScanValid;

        // A store line names its object by position and prefab (0.21.6), so two lines could resolve
        // onto the same object; this keeps each resolution to one. Live only during a restore.
        private readonly HashSet<ZDOID> _restoreClaimed = new HashSet<ZDOID>();

        // How far from its saved position a burning object may be found again. The save now
        // records the LIVE position and nothing moves while the server is down, so this only has
        // to absorb the float round-trip through the text store. Deliberately tiny: a generous
        // radius relights the neighbour of something that is genuinely gone, and that neighbour
        // would inherit the dead object's burn age at full spread maturity.
        private const float ObjRestoreRadius = 0.75f;

        // One sector scan serves a cluster of store lines - they are all one fire. Same 32 m reuse
        // proof as the regrowth build check: the scan covers the 3x3 zone block, 192 m, so a point
        // within 32 m of its centre has its own metre-scale neighbourhood well inside it.
        private readonly List<ValheimBridge.ZdoBurnable> _restoreScanScratch = new List<ValheimBridge.ZdoBurnable>();
        private Vector3 _restoreScanCentre;
        private bool _restoreScanValid;

        /// <summary>
        /// Names the object a store line meant, by position and prefab. Returns false unless
        /// EXACTLY ONE candidate matches: with two, there is no way to tell which was burning, and
        /// guessing would set fire to a bystander and hand it the dead object's burn age. A refusal
        /// costs one fire; a wrong guess starts one.
        /// </summary>
        private bool ResolveBurnerAt(Vector3 at, string prefabName, out ZDOID found)
        {
            found = default(ZDOID);
            if (string.IsNullOrEmpty(prefabName)) return false;

            if (!_restoreScanValid || (at - _restoreScanCentre).sqrMagnitude > 32f * 32f)
            {
                if (!ValheimBridge.CollectAllZdosNear(at, _restoreScanScratch)) { _restoreScanValid = false; return false; }
                _restoreScanValid = true;
                _restoreScanCentre = at;
            }

            int wantHash = ValheimBridge.PrefabHashOf(prefabName);
            float radiusSqr = ObjRestoreRadius * ObjRestoreRadius;
            int hits = 0;
            for (int i = 0; i < _restoreScanScratch.Count; i++)
            {
                ValheimBridge.ZdoBurnable c = _restoreScanScratch[i];
                if (c.PrefabHash != wantHash) continue;
                if (_restoreClaimed.Contains(c.Id)) continue;
                if ((c.Position - at).sqrMagnitude > radiusSqr) continue;
                found = c.Id;
                if (++hits > 1) break;
            }
            if (hits == 1) return true;
            if (hits > 1)
                FireLogger.Debug($"[PERSIST] {hits}+ '{prefabName}' within {ObjRestoreRadius:F2}m of {at} — refusing to guess which was burning.");
            found = default(ZDOID);
            return false;
        }

        // The blaze age carried by the store's meta line, live only while RestorePersistedFires
        // runs. Events created by the re-ignitions it performs adopt it, so a restored fire
        // resumes at the intensity it had rather than ramping from cold (0.21.5).
        private float _restoringRampAge;

        // One spot must never hold two regrowth entries — a restored sidecar
        // "regrow" line plus the same tree burning down again after the restore
        // would spawn two overlapping trees (seen live 2026-08-28: Beech1 queued
        // twice, one entry at attempts 13, one fresh). Keyed by position alone;
        // the existing entry wins because its attempt count is real history.
        private const float RegrowthDedupeRadiusSqr = 0.25f; // 0.5m — same physical spot

        private bool EnqueueRegrowth(PendingRegrowth entry)
        {
            for (int i = 0; i < _pendingRegrowth.Count; i++)
            {
                if ((_pendingRegrowth[i].Position - entry.Position).sqrMagnitude <= RegrowthDedupeRadiusSqr)
                {
                    FireLogger.Debug($"Regrowth dedupe: {entry.PrefabName} at {entry.Position} already queued — skipped.");
                    return false;
                }
            }
            _pendingRegrowth.Add(entry);
            return true;
        }
        private int _treesRegrownCount; // running total of successful spawns, for firestatus visibility

        // Reused each cycle to avoid per-frame allocation.
        private readonly List<ZDOID> _scratch = new List<ZDOID>();
        private readonly List<Component> _candidates = new List<Component>();

        // ZDO-layer spread candidates. [SPREAD-DIAGNOSTIC] on the live dedicated
        // server (0.17.3) showed the instance scans behind _candidates see
        // NOTHING there — AllPieces.Count=0, total candidates=0 with a forest
        // actively burning around the player — because a headless server tracks
        // world objects as ZDOs without instantiating GameObjects. Same objects,
        // read from the ZDO layer instead; an instance is force-created per
        // actual ignition (ComponentFromZdoid), never per nearby candidate.
        private readonly List<ValheimBridge.ZdoBurnable> _zdoCandidates = new List<ValheimBridge.ZdoBurnable>();

        // --- spatial index over both candidate lists -------------------------
        //
        // Spread used to test EVERY candidate against EVERY burner. That is
        // O(burners x candidates), and a tester's log (2026-08-29, hosting,
        // 0.19.3) showed what that costs at scale: 2176 candidates against 50
        // burning objects and 46 ground cells, every 0.75s cycle — roughly a
        // quarter of a million distance checks a cycle, each carrying an
        // IsBurnable dispatch and a ZDOIDOf lookup. Their frametime spikes
        // measured 101.9-146.3ms, which is where that arithmetic lands.
        //
        // Candidates are static — trees and walls do not move — so they can be
        // bucketed by position once per rebuild and queried by area. A burner
        // then examines only the handful of cells its own reach touches
        // instead of the whole world list, which makes the cost follow the
        // fire's size rather than how much wood is lying around the map.
        //
        // Cell size is comfortably larger than any spread reach (default 8m)
        // so a query normally touches 2x2 cells; the query walks whatever span
        // the radius actually covers, so an unusually large configured radius
        // still returns correct results, just from more cells.
        private const float CandidateCellSize = 16f;
        private readonly Dictionary<long, List<int>> _candidateGrid = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> _zdoCandidateGrid = new Dictionary<long, List<int>>();
        private readonly List<List<int>> _gridBucketPool = new List<List<int>>();
        private readonly List<int> _gridQueryScratch = new List<int>();

        private static long CellKeyOf(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / CandidateCellSize);
            int cz = Mathf.FloorToInt(z / CandidateCellSize);
            return ((long)cx << 32) | (uint)cz;
        }

        /// <summary>Empties a grid, returning its bucket lists to the pool for reuse.</summary>
        private void ClearGrid(Dictionary<long, List<int>> grid)
        {
            foreach (KeyValuePair<long, List<int>> kv in grid)
            {
                kv.Value.Clear();
                _gridBucketPool.Add(kv.Value);
            }
            grid.Clear();
        }

        private List<int> RentBucket()
        {
            int last = _gridBucketPool.Count - 1;
            if (last < 0) return new List<int>();
            List<int> bucket = _gridBucketPool[last];
            _gridBucketPool.RemoveAt(last);
            return bucket;
        }

        private void AddToGrid(Dictionary<long, List<int>> grid, long key, int index)
        {
            if (!grid.TryGetValue(key, out List<int> bucket))
            {
                bucket = RentBucket();
                grid[key] = bucket;
            }
            bucket.Add(index);
        }

        /// <summary>
        /// Collects candidate INDICES whose cell overlaps a radius around origin
        /// into the shared scratch list. Cell membership is coarse, so callers
        /// still range-check each hit — this only avoids visiting the ones that
        /// could not possibly be near.
        /// </summary>
        private void QueryGrid(Dictionary<long, List<int>> grid, Vector3 origin, float radius)
        {
            _gridQueryScratch.Clear();
            if (grid.Count == 0) return;

            int minX = Mathf.FloorToInt((origin.x - radius) / CandidateCellSize);
            int maxX = Mathf.FloorToInt((origin.x + radius) / CandidateCellSize);
            int minZ = Mathf.FloorToInt((origin.z - radius) / CandidateCellSize);
            int maxZ = Mathf.FloorToInt((origin.z + radius) / CandidateCellSize);

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cz = minZ; cz <= maxZ; cz++)
                {
                    if (grid.TryGetValue(((long)cx << 32) | (uint)cz, out List<int> bucket))
                        _gridQueryScratch.AddRange(bucket);
                }
            }
        }

        /// <summary>Re-buckets both candidate lists. Called only when they are rebuilt.</summary>
        private void RebuildCandidateGrids()
        {
            ClearGrid(_candidateGrid);
            ClearGrid(_zdoCandidateGrid);

            for (int i = 0; i < _candidates.Count; i++)
            {
                Component c = _candidates[i];
                if (c == null) continue;
                Vector3 p = ValheimBridge.PositionOf(c);
                AddToGrid(_candidateGrid, CellKeyOf(p.x, p.z), i);
            }

            for (int i = 0; i < _zdoCandidates.Count; i++)
            {
                Vector3 p = _zdoCandidates[i].Position;
                AddToGrid(_zdoCandidateGrid, CellKeyOf(p.x, p.z), i);
            }
        }
        private readonly List<GroundCellKey> _groundScratch = new List<GroundCellKey>();
        private readonly List<GroundCellKey> _exhaustedScratch = new List<GroundCellKey>();

        private float _nextCycle;
        private float _nextCandidateRebuild;
        private const float CandidateRebuildSeconds = 5f;
        private float _fireStartTime = -1f;

        // Where the current fire event first ignited (object or ground), captured
        // once and reused as the leash center for GroundMaxSpreadDistance. Same
        // single-global-event simplification as _fireStartTime — see
        // GroundMaxSpreadDistance's config description.
        private Vector3? _fireOrigin;

        // --- fire events -----------------------------------------------------
        //
        // A fire event is one blaze: its own origin, its own ramp, its own
        // arsonist. Until this was added, all of that was GLOBAL — one
        // _fireOrigin, one _fireStartTime, one igniter — with three
        // consequences, the first of them severe:
        //
        //  1. The candidate sweep centred on the FIRST fire's origin and reached
        //     only a bounded radius, so a second fire lit beyond that radius got
        //     ZERO candidates and could not spread AT ALL. Light a fire, travel
        //     500m, light another: the second just sits there. Reported from
        //     play 2026-08-29 ("tp'd far away... nothing propagates") and then
        //     confirmed in the code.
        //  2. MaxConcurrentBurning was one global budget, so the first big fire
        //     starved every later one until it burned out.
        //  3. A later fire inherited the first's ramp progress and its arsonist,
        //     so a natural fire could be billed to whoever lit something else
        //     across the map.
        //
        // Events form by proximity: an ignition joins the nearest event whose
        // origin is within EventJoinRadius, else it starts its own. An event
        // dies when its last burner and last ground cell go out.
        private class FireEvent
        {
            public int Id;
            public Vector3 Origin;
            public float StartTime;        // ramp clock for THIS blaze
            public float RestoredRampAge;  // carried across a restart
            public long IgniterPlayerId;   // 0 = natural/unknown
        }

        private readonly Dictionary<int, FireEvent> _events = new Dictionary<int, FireEvent>();
        private readonly HashSet<int> _liveEventIds = new HashSet<int>();
        private readonly List<int> _deadEventScratch = new List<int>();
        private readonly List<(int, Vector3)> _sweepCenters = new List<(int, Vector3)>();
        private readonly List<ValheimBridge.ZdoBurnable> _zdoSweepScratch = new List<ValheimBridge.ZdoBurnable>();
        private int _nextEventId = 1;

        // How close an ignition must be to an existing event to count as the
        // same blaze. Anything a fire could plausibly have reached on its own
        // should join it rather than become a rival with its own budget; past
        // that it is genuinely a separate fire. Ground leash (how far one fire
        // travels) plus a spread reach of slack.
        private float EventJoinRadius =>
            (FireConfig.EffectiveGroundMaxSpreadDistanceEnabled ? FireConfig.GroundMaxSpreadDistance.Value : 100f)
            + FireConfig.EffectiveSpreadRadius + 8f;

        /// <summary>
        /// The event this position belongs to, creating one if nothing is near
        /// enough. igniterPlayerId is recorded only when a NEW event is born —
        /// joining an existing blaze never rewrites its culprit.
        /// </summary>
        private int EventForPosition(Vector3 pos, long igniterPlayerId)
        {
            float bestSqr = EventJoinRadius * EventJoinRadius;
            int best = 0;
            foreach (FireEvent ev in _events.Values)
            {
                float d = (ev.Origin - pos).sqrMagnitude;
                if (d <= bestSqr) { bestSqr = d; best = ev.Id; }
            }
            if (best != 0) return best;

            var created = new FireEvent
            {
                Id = _nextEventId++,
                Origin = pos,
                StartTime = Time.time,
                IgniterPlayerId = igniterPlayerId,
                // Set ONLY during a restore, and set here rather than afterwards because the very
                // first re-ignition consults this event's ramp to size its own burner cap: aging
                // the event after the loop would arrive too late to stop the restore truncating.
                RestoredRampAge = _restoringRampAge,
            };
            _events[created.Id] = created;
            _nextCandidateRebuild = 0f; // new blaze, new ground — resweep now
            FireLogger.Debug($"[EVENT] event {created.Id} born at {pos} (igniter {igniterPlayerId}); {_events.Count} active.");
            return created.Id;
        }

        private FireEvent EventById(int id)
        {
            return id != 0 && _events.TryGetValue(id, out FireEvent ev) ? ev : null;
        }

        /// <summary>
        /// Gives an event to anything burning without one. Two sources of
        /// orphans: state restored from the persistence sidecar (which predates
        /// events and stores no id), and anything lit before this ran. Left
        /// alone they would sit at EventId 0 forever — matching no event, given
        /// no sweep, never spreading, which is the very bug events exist to fix.
        /// Clustering by position means one restored blaze comes back as one
        /// event, and two far apart come back as two.
        /// </summary>
        private void AdoptOrphanedFires()
        {
            _orphanBurnerScratch.Clear();
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
                if (kv.Value.EventId == 0) _orphanBurnerScratch.Add(kv.Key);

            for (int i = 0; i < _orphanBurnerScratch.Count; i++)
            {
                ZDOID id = _orphanBurnerScratch[i];
                BurningState s = _burning[id];
                s.EventId = EventForPosition(s.Position, _fireIgniterPlayerId);
                _burning[id] = s;
            }

            _orphanCellScratch.Clear();
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                if (kv.Value.EventId == 0) _orphanCellScratch.Add(kv.Key);

            for (int i = 0; i < _orphanCellScratch.Count; i++)
            {
                GroundCellKey key = _orphanCellScratch[i];
                GroundCellState s = _groundBurning[key];
                s.EventId = EventForPosition(CellCenter(key, s.Y), _fireIgniterPlayerId);
                _groundBurning[key] = s;
            }

            if (_orphanBurnerScratch.Count > 0 || _orphanCellScratch.Count > 0)
            {
                FireLogger.Debug($"[EVENT] adopted {_orphanBurnerScratch.Count} burner(s) and " +
                                 $"{_orphanCellScratch.Count} ground cell(s); {_events.Count} event(s) active.");
            }

            // The restore's blaze age is consumed by EventForPosition, which the adoption above is
            // the LAST caller of on a restore path. Released here, not at the end of the restore.
            _restoringRampAge = 0f;
        }

        private readonly List<ZDOID> _orphanBurnerScratch = new List<ZDOID>();
        private readonly List<GroundCellKey> _orphanCellScratch = new List<GroundCellKey>();

        /// <summary>Drops events with no burner and no ground cell left.</summary>
        private void PruneDeadEvents()
        {
            if (_events.Count == 0) return;
            _liveEventIds.Clear();
            foreach (BurningState s in _burning.Values) _liveEventIds.Add(s.EventId);
            foreach (GroundCellState s in _groundBurning.Values) _liveEventIds.Add(s.EventId);

            _deadEventScratch.Clear();
            foreach (int id in _events.Keys) if (!_liveEventIds.Contains(id)) _deadEventScratch.Add(id);
            for (int i = 0; i < _deadEventScratch.Count; i++)
            {
                _events.Remove(_deadEventScratch[i]);
                FireLogger.Debug($"[EVENT] event {_deadEventScratch[i]} is out; {_events.Count} active.");
            }
        }

        /// <summary>Per-event ramp — a new blaze starts cold even while an old one rages.</summary>
        private float GetRampFraction(int eventId)
        {
            FireEvent ev = EventById(eventId);
            if (ev == null) return GetRampFraction();
            if (!FireConfig.EffectiveFireRampEnabled) return 1f;

            float start = Mathf.Clamp01(FireConfig.FireRampStartFraction.Value);
            float dur = Mathf.Max(1f, FireConfig.FireRampDurationSeconds.Value);
            float age = (Time.time - ev.StartTime) + ev.RestoredRampAge;
            return Mathf.Clamp01(start + (1f - start) * Mathf.Clamp01(age / dur));
        }

        /// <summary>Burners currently belonging to one event — the per-event budget.</summary>
        private int BurningCountForEvent(int eventId)
        {
            int n = 0;
            foreach (BurningState s in _burning.Values) if (s.EventId == eventId) n++;
            return n;
        }

        /// <summary>Ground cells currently belonging to one event.</summary>
        private int GroundCountForEvent(int eventId)
        {
            int n = 0;
            foreach (GroundCellState s in _groundBurning.Values) if (s.EventId == eventId) n++;
            return n;
        }

        // One-shot latch for the wind-intensity fallback notice — see
        // IgniteAdjacentGroundCells. Deliberately never reset: an EnvMan that
        // can't report wind strength won't start doing so mid-session, and this
        // sits on the hot spread path.
        private bool _windIntensityFallbackLogged;

        public int BurningCount => _burning.Count;
        public int QueuedCount => _queue.Count;
        public int GroundBurningCount => _groundBurning.Count;

        // Persistent player id of whoever started the CURRENT fire event, captured once
        // beside _fireOrigin and reset with it. Spread ignitions inherit it implicitly:
        // they join an event whose origin (and culprit) is already set. 0 = natural or
        // unknown (environmental fire, creature attackers, dev commands, ground-started
        // events).
        private long _fireIgniterPlayerId;

        /// <summary>
        /// PUBLIC CROSS-MOD CONTRACT (added 0.17.3), the igniter-side companion to
        /// CollectActiveFirePositions: the persistent player id that started the current
        /// fire event, 0 when there is no fire or no player behind it. Ragnarok's Wrath
        /// resolves this property by reflection to book arson HARM in its rivalry ledger.
        /// Renaming it or changing its meaning silently disarms that attribution — same
        /// rules as the method below, even though nothing in THIS repo reads it.
        /// Meaningful on the simulation authority only, like everything else here.
        /// </summary>
        public long CurrentFireIgniterPlayerId => _fireIgniterPlayerId;

        /// <summary>
        /// External read surface: append the world position of every active fire — burning
        /// objects and burning ground cells — to <paramref name="into"/>.
        ///
        /// PUBLIC CROSS-MOD CONTRACT (added 0.17.2). Ragnarok's Wrath resolves this method by
        /// reflection to raise per-zone Scorch where fires burn, so it stays a soft dependency
        /// that tolerates either mod being absent. Renaming it, changing its signature, or
        /// making it instance-state-dependent in a new way is a breaking change for that mod
        /// even though nothing in THIS repo references it.
        ///
        /// Positions come from the same caches the simulation itself trusts: a burning object's
        /// position is captured once at ignition (see BurningState — live Components can be torn
        /// down under us on a dedicated server), and a ground cell's from CellCenter at its
        /// ignition Y. Meaningful on the simulation authority only: clients hold visual-only
        /// mirrors and will report an empty (or partial) picture by design.
        /// </summary>
        public void CollectActiveFirePositions(List<Vector3> into)
        {
            if (into == null) return;

            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
                into.Add(kv.Value.Position);

            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                into.Add(CellCenter(kv.Key, kv.Value.Y));
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            ZRoutedRpc routedRpc = ZRoutedRpc.instance;
            if (routedRpc != null && !ReferenceEquals(routedRpc, _registeredRpcInstance))
            {
                _registeredRpcInstance = routedRpc; // guarded by reference, so a reconnect's fresh instance re-registers
                ValheimBridge.RegisterFireRpcs(HandleIgniteRequest, HandleFireEventBroadcast, HandleGroundFireSync, HandleExtinguishRequest, HandleConfigSetRequest, HandleStatusRequest, HandleStatusResponse, HandleCommandRelay);
            }

            if (FireConfig.ExtinguishKey.Value.IsDown())
            {
                TryPlayerExtinguish();
            }

            // A config change made before the world loaded (the config manager at the main menu,
            // which is where people actually use it) has nowhere to go; this delivers it once the
            // connection and the admin list exist. No-op when there is nothing held.
            FireFront.Commands.FireDevCommands.FlushPendingConfigSync();

            DrainRemoteVfxSpawnQueue(); // client-side VFX budget — must run before the server gate below
            DrainRemoteObjectVfxQueue(); // same, for object fire — see EnqueueRemoteObjectVfx
            ProcessRemoteSmouldering(); // client-side: the server is headless, THIS is what a player sees

            // Everything below this point is the actual fire simulation, and it
            // must only ever run on the server. Ignition already only populates
            // _burning/_groundBurning server-side (clients forward a request via
            // RPC instead — see the Harmony ignition patches), so this loop was
            // already a no-op on clients in practice, operating on permanently
            // empty collections. But that was incidental, not enforced — any
            // future code path that adds to those collections locally (a dev
            // command, a bug) would silently start a second, un-networked
            // simulation with no warning. Gate it explicitly instead of relying
            // on that accident.
            if (!ValheimBridge.IsServer()) return;

            // Restore persisted fire state exactly once, before the first cycle
            // ever runs — and gate all SAVES behind this flag too, or the empty
            // pre-restore state would clobber the store at every boot.
            if (!_persistenceRestored && ZNet.instance != null && ZDOMan.instance != null)
            {
                _persistenceRestored = true;
                if (FireConfig.PersistFiresEnabled.Value) RestorePersistedFires();
            }

            if (!FireConfig.Enabled.Value) return;
            if (Time.time < _nextCycle) return;
            _nextCycle = Time.time + FireConfig.EffectiveSpreadCheckInterval;

            MaybePersistFires();
            PruneStale();
            AgeFiresInRain();
            ExpireTimers();
            PromoteFromQueue();
            ExpireGroundTimers();
            ProcessTreeRegrowth();
            ProcessPendingIgniteResolutions();
            FlushPendingPaint();
            FlushGroundFireSync();
            PruneFirebreakCache();
            AdoptOrphanedFires();
            PruneDeadEvents();
            ProcessSmouldering();
            LogStatusHeartbeat();

            if (_burning.Count == 0 && _groundBurning.Count == 0)
            {
                _fireStartTime = -1f; _restoredRampAge = 0f; // fully out — next ignition ramps up fresh
                _fireOrigin = null;
                _fireIgniterPlayerId = 0L; // the event's culprit goes out with its fires
                _nextSpreadDiagnosticLog = 0f; // next fire reports its candidate counts on its first cycle
                _groundExhausted.Clear();
            }

            SpreadPass();
        }

        /// <summary>
        /// 0 (just started) to 1 (fully ramped). A brand-new fire starts at
        /// FireRampStartFraction and climbs linearly to 1.0 over
        /// FireRampDurationSeconds. Returns 1.0 outright if ramping is disabled.
        /// </summary>
        // Ramp age carried over from a restored fire. Needed because
        // _fireStartTime doubles as a "no fire" sentinel (-1): restoring a
        // 120s-old fire onto a 20s-old server would set it to -100, which the
        // sentinel check silently read as "no fire" and reset the ramp to its
        // start fraction (found live in the first 0.18.0 restore test). Reset
        // wherever _fireStartTime resets.
        private float _restoredRampAge;

        private float GetRampFraction()
        {
            if (!FireConfig.EffectiveFireRampEnabled) return 1f;
            if (_fireStartTime < 0f) return FireConfig.FireRampStartFraction.Value;

            float elapsed = (Time.time - _fireStartTime) + _restoredRampAge;
            float t = Mathf.Clamp01(elapsed / FireConfig.FireRampDurationSeconds.Value);
            return Mathf.Lerp(FireConfig.FireRampStartFraction.Value, 1f, t);
        }

        /// <summary>
        /// Player-facing manual extinguish: whatever's under the crosshair
        /// (if burning) plus any ground fire within ExtinguishGroundRadius of
        /// the player. This is the in-game equivalent of the stopfire/clearfires
        /// console commands, bound to a real key instead.
        /// </summary>
        private void TryPlayerExtinguish()
        {
            Component target = ValheimBridge.RaycastBurnable();
            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) return;

            if (ValheimBridge.IsServer())
            {
                bool didSomething = false;

                if (target != null && IsBurning(target))
                {
                    Extinguish(target);
                    didSomething = true;
                }

                int cleared = ExtinguishGroundNear(posOrNull.Value, FireConfig.ExtinguishGroundRadius.Value)
                            + ExtinguishObjectsNear(posOrNull.Value, FireConfig.ExtinguishGroundRadius.Value);
                if (cleared > 0) didSomething = true;

                if (didSomething) ValheimBridge.ShowPlayerMessage("Fire extinguished");
            }
            else
            {
                // Same authority problem ignition had: this used to remove from
                // the CALLER's own _burning/_groundBurning, which are always
                // empty on a real client now (nothing populates them locally
                // anymore) — so pressing the key silently did nothing. Forward
                // the request to the server instead. Shown optimistically here
                // rather than waiting for confirmation — same tradeoff as any
                // client-side input feedback in a networked game.
                ZDOID targetId = (target != null) ? (ValheimBridge.ZDOIDOf(target) ?? ZDOID.None) : ZDOID.None;
                ValheimBridge.SendExtinguishRequestToServer(targetId, posOrNull.Value, FireConfig.ExtinguishGroundRadius.Value);
                ValheimBridge.ShowPlayerMessage("Fire extinguished");
            }
        }

        /// <summary>Server-side landing for a client's forwarded fireset — see FireDevCommands.ApplyRemote.</summary>
        private void HandleConfigSetRequest(long sender, string key, string raw)
        {
            if (!ValheimBridge.IsServer()) return;
            FireFront.Commands.FireDevCommands.ApplyRemote(sender, key, raw);
        }

        /// <summary>Server side of a client's firestatus: reply to THAT peer with the real line.</summary>
        private void HandleStatusRequest(long sender)
        {
            if (!ValheimBridge.IsServer()) return;
            // The server's own config migration line first (0.21.5): before this the reply carried
            // only the fire counts, so the one check 0.21.4's handoff asked for - "firestatus must
            // not say REFUSED" - could not be made from a client at all.
            ValheimBridge.SendStatusResponse(sender, ConfigMigration.StatusLine());
            ValheimBridge.SendStatusResponse(sender, StatusLine());
        }

        /// <summary>Client side: the server's authoritative status line arrives — print it.</summary>
        private void HandleStatusResponse(long sender, string statusLine)
        {
            if (ValheimBridge.IsServer()) return;
            ValheimBridge.AddConsoleLine("[server] " + statusLine);
        }

        /// <summary>Server side: a client asked to run a whitelisted dev command here.</summary>
        private void HandleCommandRelay(long sender, string commandLine)
        {
            Commands.FireDevCommands.ExecuteRelayed(sender, commandLine);
        }

        /// <summary>
        /// Server-side handler for a client's extinguish request — the G key
        /// and the Dousing Bomb both land here (the bomb passes ZDOID.None and
        /// its impact point). Since 0.17.5 the radius also clears burning
        /// OBJECTS, not just ground cells: a bomb landing on a burning tree
        /// obviously must put it out, and the same coherence applies to the
        /// key ("clear the fire around me" shouldn't ignore a burning wall
        /// 2m away just because the crosshair missed it).
        /// </summary>
        private void HandleExtinguishRequest(long sender, ZDOID targetId, Vector3 playerPos, float groundRadius)
        {
            if (!ValheimBridge.IsServer()) return;

            if (!targetId.Equals(ZDOID.None))
            {
                Component target = ValheimBridge.ComponentFromZdoid(targetId);
                if (target != null && IsBurning(target)) Extinguish(target);
            }

            ExtinguishAt(playerPos, groundRadius);
        }

        /// <summary>
        /// Extinguish everything — ground cells and burning objects — within
        /// radius of a point. The Dousing Bomb's impact effect; also what the
        /// extinguish radius means since 0.17.5.
        /// </summary>
        public void ExtinguishAt(Vector3 origin, float radius)
        {
            ExtinguishObjectsNear(origin, radius);
            ExtinguishGroundNear(origin, radius);
        }

        /// <summary>
        /// Removes every burning OBJECT within radius of a position. Works from
        /// the cached BurningState positions, so it needs no live instance —
        /// fine headless. Mirrors Extinguish(Component)'s no-broadcast
        /// contract; clients reconcile the same way they do for the key path.
        /// </summary>
        public int ExtinguishObjectsNear(Vector3 origin, float radius)
        {
            float radiusSqr = radius * radius;
            _scratch.Clear();
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                if ((kv.Value.Position - origin).sqrMagnitude <= radiusSqr) _scratch.Add(kv.Key);
            }
            float wetUntil = Time.time + FireConfig.EffectiveDouseImmunitySeconds;
            foreach (ZDOID id in _scratch)
            {
                _burning.Remove(id);
                _queue.Remove(id);
                RemoveVfxFor(id);
                if (FireConfig.EffectiveDouseImmunitySeconds > 0f) _dousedUntil[id] = wetUntil;
            }
            if (_scratch.Count > 0)
                FireLogger.Debug($"Extinguished {_scratch.Count} burning object(s) within {radius:F1}m.");
            return _scratch.Count;
        }

        /// <summary>Removes every ground cell within radius of a position. Returns how many were cleared.</summary>
        public int ExtinguishGroundNear(Vector3 origin, float radius)
        {
            float radiusSqr = radius * radius;
            _groundScratch.Clear();

            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                Vector3 center = CellCenter(kv.Key, kv.Value.Y);
                if ((center - origin).sqrMagnitude <= radiusSqr)
                {
                    _groundScratch.Add(kv.Key);
                }
            }

            float dousedUntil = Time.time + FireConfig.EffectiveDouseImmunitySeconds;
            foreach (GroundCellKey key in _groundScratch)
            {
                float y = _groundBurning[key].Y;
                _groundBurning.Remove(key);
                RemoveGroundVfxFor(key);
                LeaveScorchMark(key, y);
                _groundExpiredSinceFlush.Add(key);

                // Doused ground is soaked, not just dark: without this the
                // surrounding fire re-ignited every extinguished cell within a
                // cycle or two, so a dousing bomb's hole refilled itself in
                // seconds and firefighting a ramped fire was Sisyphean (live
                // report: "the ramp is too aggressive to fight" — the ramp was
                // fine, the dousing just didn't hold). Reuses the exhaustion
                // dictionary; TryIgniteGroundCell already refuses these cells.
                if (FireConfig.EffectiveDouseImmunitySeconds > 0f)
                    _groundExhausted[key] = dousedUntil;
            }

            return _groundScratch.Count;
        }

        // ---------------------------------------------------------------
        // Public API (patches + dev commands call these) — object fire
        // ---------------------------------------------------------------

        /// <summary>
        /// Request ignition with no known igniter — spread, dev commands, and any older
        /// caller. Attribution-aware callers (the ignition patches, the ignite RPC) use
        /// the overload below; spread deliberately passes nothing because it joins an
        /// event whose culprit is already captured.
        /// </summary>
        public void TryIgnite(Component target) => TryIgnite(target, 0L);

        /// <summary>
        /// Request ignition. Respects vanilla burnability, the concurrent cap,
        /// and the overflow queue. Silent drop when both are full.
        /// <paramref name="igniterPlayerId"/> is recorded as the fire EVENT's culprit only
        /// when this ignition starts a fresh event (capture-once, beside _fireOrigin).
        /// </summary>
        public void TryIgnite(Component target, long igniterPlayerId)
        {
            // TEMPORARY DIAGNOSTIC — remove once the server-authority question is
            // settled. Purpose: FireManager has no ZNet.instance.IsServer() gating
            // anywhere, so every peer with this mod loaded runs its own independent
            // simulation from whatever triggers TryIgnite. If RPC_Damage (the only
            // ignition trigger) executes on every connected peer rather than just
            // the object's owner, this line will fire once per peer for a single
            // hit — confirming duplicate simulation. Compare counts across the
            // dedicated server's log and each connected client's log for the same
            // ignition event.
            bool? isServer = ZNet.instance != null ? ZNet.instance.IsServer() : (bool?)null;
            FireLogger.Debug($"[AUTHORITY-CHECK] TryIgnite called on {ValheimBridge.NameOf(target)} " +
                              $"— IsServer={(isServer.HasValue ? isServer.Value.ToString() : "ZNet.instance null")}, " +
                              $"peer={SystemInfo.deviceUniqueIdentifier}");

            if (!FireConfig.Enabled.Value) return;
            if (!ValheimBridge.IsAlive(target)) return;
            if (!ValheimBridge.IsBurnable(target)) return;

            ZDOID? idOrNull = ValheimBridge.ZDOIDOf(target);
            if (!idOrNull.HasValue) return; // can't track what we can't identify
            ZDOID id = idOrNull.Value;

            if (_burning.ContainsKey(id)) return;
            if (_queue.Contains(id)) return;
            if (_dousedUntil.TryGetValue(id, out float wetUntil) && Time.time < wetUntil) return; // soaked — see _dousedUntil

            // The budget is PER EVENT now, not global — a blaze on the far side
            // of the map no longer starves this one. That global cap was the
            // tester's "the first big fire denies any other from existing".
            Vector3 targetPos = ValheimBridge.PositionOf(target);
            int eventId = EventForPosition(targetPos, igniterPlayerId);
            int effectiveMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.EffectiveMaxConcurrentBurning * GetRampFraction(eventId)));

            if (BurningCountForEvent(eventId) < effectiveMax)
            {
                if (_fireStartTime < 0f) _fireStartTime = Time.time;
                if (_fireOrigin == null)
                {
                    _fireOrigin = targetPos;                 // legacy global, still written to the sidecar
                    _fireIgniterPlayerId = igniterPlayerId;
                }
                StartBurning(target, id, eventId);
            }
            else if (_queue.TryEnqueue(id))
            {
                FireLogger.Debug($"Queued ({_queue.Count}/{_queue.Capacity}): {ValheimBridge.NameOf(target)}");
            }
            // else: queue full -> silent drop; spread re-attempts next cycle.
        }

        public bool IsBurning(Component target)
        {
            if (target == null) return false;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            return id.HasValue && _burning.ContainsKey(id.Value);
        }

        public void Extinguish(Component target)
        {
            if (target == null) return;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            if (!id.HasValue) return;

            if (_burning.Remove(id.Value))
                FireLogger.Debug($"Extinguished: {ValheimBridge.NameOf(target)}");
            _queue.Remove(id.Value);
            RemoveVfxFor(id.Value);
            if (FireConfig.EffectiveDouseImmunitySeconds > 0f)
                _dousedUntil[id.Value] = Time.time + FireConfig.EffectiveDouseImmunitySeconds;
        }

        public void ClearAll()
        {
            int n = _burning.Count + _queue.Count + _groundBurning.Count;
            foreach (ZDOID id in _burning.Keys) RemoveVfxFor(id);
            foreach (GameObject instance in _groundVfx.Values) { if (instance != null) Destroy(instance); }
            foreach (GameObject instance in _remoteVfx.Values) { if (instance != null) Destroy(instance); }
            foreach (GameObject instance in _remoteGroundVfx.Values) { if (instance != null) Destroy(instance); }
            _remoteVfx.Clear();
            _remoteGroundVfx.Clear();
            _remoteVfxSpawnQueue.Clear();
            _remoteVfxQueuedKeys.Clear();
            _remoteObjectVfxQueue.Clear();
            _remoteObjectVfxQueued.Clear();
            _groundExpiredSinceFlush.AddRange(_groundBurning.Keys); // so the next flush tells clients to clear these too
            _pendingIgniteResolutions.Clear();
            _burning.Clear();
            _queue.Clear();
            _groundBurning.Clear();
            _groundVfx.Clear();
            _groundExhausted.Clear();
            _dousedUntil.Clear();
            _pendingRegrowth.Clear();
            _fireStartTime = -1f;
            _restoredRampAge = 0f;
            _fireOrigin = null;
            _nextSpreadDiagnosticLog = 0f;
            FireLogger.Info($"Cleared all fires ({n} entries).");

            // A deliberate clear must stick even through a hard kill right
            // after — otherwise the next boot resurrects the fire someone
            // explicitly put out.
            PersistFiresNow();
        }

        // ---------------------------------------------------------------
        // Persistence — fires, spent fuel, and pending regrowth survive a
        // server restart. Times are stored as REMAINING seconds because
        // Time.time restarts with the process. Server-only, like the rest
        // of the simulation. File IO lives in FirePersistence.
        // ---------------------------------------------------------------

        private bool _persistenceRestored;
        private float _nextPersistSave;
        private const float PersistSaveInterval = 60f;

        private void MaybePersistFires()
        {
            if (!FireConfig.PersistFiresEnabled.Value) return;
            if (Time.time < _nextPersistSave) return;
            _nextPersistSave = Time.time + PersistSaveInterval;
            PersistFiresNow();
        }

        private void PersistFiresNow()
        {
            if (!_persistenceRestored) return; // never clobber the store before restoring it
            if (!FireConfig.PersistFiresEnabled.Value) return;
            if (!ValheimBridge.IsServer()) return;

            float now = Time.time;
            var sb = new System.Text.StringBuilder(256);
            sb.Append("version\t").Append(FirePersistence.FormatVersion).Append('\n');

            if (_fireStartTime >= 0f && _fireOrigin.HasValue)
            {
                Vector3 o = _fireOrigin.Value;
                sb.Append("meta\t").Append((now - _fireStartTime) + _restoredRampAge).Append('\t')
                  .Append(o.x).Append('\t').Append(o.y).Append('\t').Append(o.z).Append('\t')
                  .Append(_fireIgniterPlayerId).Append('\n');
            }

            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                // The LIVE position, not BurningState.Position. That field is captured once at
                // ignition on the assumption that "trees and pieces don't move", and a felled
                // TreeLog breaks it: the decompiled 1.0.15 TreeLog carries a Rigidbody, is given
                // force and torque the moment it spawns, and takes AddForceAtPosition on every hit
                // - and FireFront claims ownership of the instance, so this server simulates it.
                // Harmless while the position was only diagnostic; load-bearing now that it is the
                // restore key, and logs are the MAJORITY of what a forest fire leaves burning.
                Vector3 at = ValheimBridge.TryGetZdoPosition(kv.Key, out Vector3 livePos)
                    ? livePos
                    : kv.Value.Position;
                sb.Append("obj\t").Append(kv.Key.UserID).Append('\t').Append(kv.Key.ID).Append('\t')
                  .Append(at.x).Append('\t').Append(at.y).Append('\t').Append(at.z).Append('\t')
                  .Append(kv.Value.ExpireAt - now).Append('\t')
                  .Append(now - kv.Value.IgnitedAt).Append('\t')      // burn age, so restored burners keep their spread maturity
                  .Append(kv.Value.PrefabName).Append('\n');           // field 8, 0.21.6: half of what the entry is keyed on now.
                // Fields 1-2 still carry the ZDOID and are DIAGNOSTIC ONLY - a ZDOID is reassigned
                // on every world load. Kept so a store stays readable by an older build.
            }
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                sb.Append("ground\t").Append(kv.Key.X).Append('\t').Append(kv.Key.Z).Append('\t')
                  .Append(kv.Value.Y).Append('\t').Append(kv.Value.ExpireAt - now).Append('\n');
            }
            foreach (KeyValuePair<GroundCellKey, float> kv in _groundExhausted)
            {
                sb.Append("spent\t").Append(kv.Key.X).Append('\t').Append(kv.Key.Z).Append('\t')
                  .Append(kv.Value - now).Append('\n');
            }
            foreach (PendingRegrowth entry in _pendingRegrowth)
            {
                sb.Append("regrow\t").Append(entry.Position.x).Append('\t').Append(entry.Position.y).Append('\t')
                  .Append(entry.Position.z).Append('\t').Append(entry.RegrowAt - now).Append('\t')
                  .Append(entry.Attempts).Append('\t').Append(entry.PrefabName).Append('\n');
            }

            FirePersistence.Write(sb.ToString());
        }

        /// <summary>
        /// Rebuilds live state from the store. Order matters: meta first (so
        /// TryIgnite sees an existing fire event and neither restarts the ramp
        /// clock nor re-attributes the igniter), then pure-data ground state
        /// (direct dictionary writes — never through TryIgniteGroundCell, whose
        /// live gates like rain or the leash could veto cells that were
        /// legitimately burning at shutdown), then burning objects THROUGH
        /// TryIgnite (so VFX, damage zones, and the client broadcast all
        /// happen) with the stored remaining time patched in after, then
        /// pending regrowth.
        /// </summary>
        private void RestorePersistedFires()
        {
            string[] lines = FirePersistence.ReadLines();
            if (lines == null || lines.Length == 0) return;

            int legacyObjLines = 0;
            float now = Time.time;
            int objects = 0, ground = 0, spent = 0, regrow = 0, skipped = 0;

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                string[] f = line.Split('\t');
                try
                {
                    switch (f[0])
                    {
                        case "version":
                            if (int.Parse(f[1]) != FirePersistence.FormatVersion)
                            {
                                FireLogger.Warn($"[PERSIST] store version {f[1]} != {FirePersistence.FormatVersion} — ignoring the store.");
                                return;
                            }
                            break;

                        case "meta":
                            // The stored age lives in _restoredRampAge, NOT in a
                            // back-dated _fireStartTime: a fire older than the
                            // server's uptime would back-date below 0 and collide
                            // with the -1 "no fire" sentinel.
                            _fireStartTime = now;
                            _restoredRampAge = float.Parse(f[1]);
                            // The store writes version, meta, obj, ground, spent, regrow in that
                            // order, so this lands before the first re-ignition creates an event.
                            _restoringRampAge = _restoredRampAge;
                            _fireOrigin = new Vector3(float.Parse(f[2]), float.Parse(f[3]), float.Parse(f[4]));
                            _fireIgniterPlayerId = long.Parse(f[5]);
                            break;

                        case "ground":
                        {
                            float remaining = float.Parse(f[4]);
                            if (remaining <= 0f) { skipped++; break; }
                            var key = new GroundCellKey(int.Parse(f[1]), int.Parse(f[2]));
                            float y = float.Parse(f[3]);
                            _groundBurning[key] = new GroundCellState { ExpireAt = now + remaining, Y = y };
                            _groundIgnitedSinceFlush.Add((key, y)); // clients learn of it at the next flush
                            ground++;
                            break;
                        }

                        case "spent":
                        {
                            float remaining = float.Parse(f[3]);
                            if (remaining <= 0f) { skipped++; break; }
                            _groundExhausted[new GroundCellKey(int.Parse(f[1]), int.Parse(f[2]))] = now + remaining;
                            spent++;
                            break;
                        }

                        case "obj":
                        {
                            float remaining = float.Parse(f[6]);
                            if (remaining <= 0f) { skipped++; break; }

                            // Field 8 is the prefab name (0.21.6). Without it this line came from a
                            // build that keyed burners by ZDOID, which does not survive a reload, so
                            // there is no way to tell which object it meant - and following the id
                            // would set fire to whatever now holds it. Dropping it is the only
                            // correct move; ground fire, spent cells and regrowth are unaffected.
                            string prefabName = f.Length > 8 ? f[8] : null;
                            if (string.IsNullOrEmpty(prefabName))
                            {
                                legacyObjLines++;
                                skipped++;
                                break;
                            }

                            var at = new Vector3(float.Parse(f[3]), float.Parse(f[4]), float.Parse(f[5]));
                            if (!ResolveBurnerAt(at, prefabName, out ZDOID id))
                            {
                                skipped++; // gone since the save, moved, or too ambiguous to name safely
                                break;
                            }
                            _restoreClaimed.Add(id);

                            Component target = ValheimBridge.ComponentFromZdoid(id);
                            if (target == null) { skipped++; break; }

                            TryIgnite(target);
                            if (_burning.TryGetValue(id, out BurningState st))
                            {
                                st.ExpireAt = now + remaining;
                                // Burn age (field 7) restores spread maturity. A store
                                // written before the field existed omits it — treat
                                // those burners as already mature, matching how they
                                // behaved when they were saved.
                                st.IgnitedAt = f.Length > 7
                                    ? now - float.Parse(f[7])
                                    : now - FireConfig.BurnDurationSeconds.Value;
                                _burning[id] = st;
                                objects++;
                            }
                            else skipped++; // caps full — the queue may still hold it, fine
                            break;
                        }

                        case "regrow":
                        {
                            bool added = EnqueueRegrowth(new PendingRegrowth
                            {
                                Position = new Vector3(float.Parse(f[1]), float.Parse(f[2]), float.Parse(f[3])),
                                RegrowAt = now + Mathf.Max(1f, float.Parse(f[4])),
                                // Deliberately NOT int.Parse(f[5]): before 0.21.5 this counted every
                                // IsAreaReady deferral, which headless meant "every cycle", so a
                                // carried-over count is near its cap for a reason that no longer
                                // exists and would drop the tree on its first real failure. The
                                // store's format version cannot be bumped to tell the two apart
                                // without discarding the whole file, and the count is retry state,
                                // not history worth keeping.
                                Attempts = 0,
                                PrefabName = f[6],
                            });
                            if (added) regrow++; else skipped++;
                            break;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    skipped++;
                    FireLogger.Debug($"[PERSIST] unreadable line skipped ('{line}'): {ex.Message}");
                }
            }

            // FireEvent.RestoredRampAge was declared in 0.19.x and never assigned - a CS0649 the
            // Ragnarok's Wrath session flagged on 2026-09-18 - so every restart dropped a raging
            // blaze back to its ramp-start intensity. Events adopt _restoringRampAge as they are
            // born, above; this only reports it and closes the window.
            if (_restoringRampAge > 0f)
                FireLogger.Info($"[PERSIST] restored fires resume at a ramp age of {_restoringRampAge:F0}s, " +
                                "the blaze age from the store's meta line.");
            // NOT cleared here. Ground cells restore with EventId 0 and only get an event from
            // AdoptOrphanedFires later in this same Update; clearing now left a ground-only restore
            // - the legacy-store case, and any fire whose objects have all burned out - ramping from
            // cold while the log said otherwise. AdoptOrphanedFires clears it once it has run.
            _restoreClaimed.Clear();
            _restoreScanScratch.Clear();
            _restoreScanValid = false;

            if (legacyObjLines > 0)
                FireLogger.Warn($"[PERSIST] dropped {legacyObjLines} burning object(s) written by a build before 0.21.6. " +
                                "Those lines identify their object by a ZDOID, which Valheim reassigns on every world " +
                                "load, so there is no way to tell which object each one meant - and following the id " +
                                "would have set fire to whatever holds it now. Ground fire, scorched ground and tree " +
                                "regrowth were restored normally, and the next save writes the new format.");

            if (objects + ground + spent + regrow > 0)
                FireLogger.Info($"[PERSIST] restored {objects} burning object(s), {ground} ground cell(s), " +
                                $"{spent} spent cell(s), {regrow} pending regrowth — {skipped} entries skipped/expired.");
        }

        private void OnDestroy()
        {
            // Last-chance flush on a graceful shutdown; a hard kill loses at
            // most PersistSaveInterval seconds of fire drift.
            PersistFiresNow();
        }

        /// <summary>Called by the OnDestroy patch (pieces only) when removed by any means.</summary>
        public void HandleTargetRemoved(Component target)
        {
            if (target == null) return;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            if (!id.HasValue) return; // can't resolve at this exact moment — leave tracked, kill-time retry will sort it out
            if (!_burning.ContainsKey(id.Value)) return; // wasn't tracked, nothing to do

            if (ValheimBridge.ZdoExists(id.Value))
            {
                // ZDO still exists — this OnDestroy was just de-instantiation
                // (the server's own housekeeping tearing down the local
                // GameObject, NOT the object actually going away), the exact
                // thing that was silently breaking spread/destruction before.
                // Stay tracked; ExpireTimers force-creates it again via
                // ComponentFromZdoid when its timer is actually up.
                FireLogger.Debug($"[IGNITE-TRACE] HandleTargetRemoved: {ValheimBridge.NameOf(target)} de-instantiated " +
                                  "but its ZDO still exists — staying tracked, not a real destruction.");
                return;
            }

            // ZDO is actually gone — really destroyed (chopped down, burned
            // elsewhere, etc.). Now it's safe to stop tracking it.
            _burning.Remove(id.Value);
            _queue.Remove(id.Value);
            FireLogger.Debug($"[IGNITE-TRACE] HandleTargetRemoved: {ValheimBridge.NameOf(target)} really destroyed — removing from _burning.");
            RemoveVfxFor(id.Value);
        }

        private void LogStatusHeartbeat()
        {
            if (_burning.Count == 0 && _groundBurning.Count == 0) return; // nothing active, stay quiet
            if (Time.time < _nextStatusHeartbeat) return;
            _nextStatusHeartbeat = Time.time + StatusHeartbeatInterval;

            FireLogger.Info($"[HEARTBEAT] {StatusLine()}");
        }

        public string StatusLine()
        {
            // Caps and interval print their EFFECTIVE values so this line never
            // disagrees with what the simulation is actually enforcing — with
            // the low-spec preset on, the configured numbers are not the ones
            // in force, and a status line that reported them would send someone
            // hunting a cap that isn't the real one.
            // Caps are PER EVENT, so the honest ceiling is cap x live events —
            // printing the global total against a per-event cap read as
            // "ground 81/50", which looks like a broken cap and is not.
            int fireCount = Mathf.Max(1, _events.Count);
            return $"FireFront: burning {_burning.Count}/{FireConfig.EffectiveMaxConcurrentBurning * fireCount}, " +
                   $"queued {_queue.Count}/{_queue.Capacity}, " +
                   $"ground {_groundBurning.Count}/{FireConfig.EffectiveGroundMaxConcurrent * fireCount} (enabled {FireConfig.GroundSpreadEnabled.Value}, vfxcap {FireConfig.EffectiveGroundVfxMaxConcurrent}, dmgcap {FireConfig.EffectiveGroundDamageMaxConcurrent}, raining {RainingBurnersForStatus()}), " +
                   $"burn {FireConfig.BurnDurationSeconds.Value}s (maturity {(FireConfig.EffectiveSpreadMaturityFraction * 100f):F0}%), " +
                   $"radius {FireConfig.EffectiveSpreadRadius}m, " +
                   $"groundradius {FireConfig.EffectiveGroundSpreadRadius}m, " +
                   $"interval {FireConfig.EffectiveSpreadCheckInterval}s, " +
                   $"lowspec {FireConfig.LowSpecPreset.Value}, burntheworld {FireConfig.ApocalypseActive}, " +
                   $"fires {_events.Count}, " +
                   $"trees {FireConfig.BurnTreesAndLogs.Value}, " +
                   $"burnbuildings {FireConfig.BurnPlayerBuildings.Value}, " +
                   $"vfx '{FireConfig.VfxPrefabName.Value}', procedural {FireConfig.UseProceduralVfx.Value}, " +
                   $"treeflames {FireConfig.TreeFlameScaling.Value} (max {FireConfig.EffectiveMaxFlameHeight}m, tallcap {FireConfig.EffectiveTallFireMaxConcurrent}, sparks {FireConfig.EffectiveCrownSparksEnabled}), " +
                   $"hurts {FireConfig.FireHurtsEnabled.Value} (playerOnly {FireConfig.FireHurtsPlayerOnly.Value}, {FireConfig.FireDamagePerTick.Value}dmg/{FireConfig.FireDamageTickInterval.Value}s), " +
                   $"dirtpaint {FireConfig.UseVanillaDirtPaint.Value}, " +
                   $"exhaustion {FireConfig.EffectiveGroundFuelExhaustionEnabled} (regrow {FireConfig.GroundFuelRegrowSeconds.Value}s), " +
                   $"treeregrowth {FireConfig.EffectiveTreeRegrowthEnabled} (after {FireConfig.TreeRegrowthSeconds.Value}s, pending {_pendingRegrowth.Count}), " +
                   $"pendingignite {_pendingIgniteResolutions.Count}, " +
                   $"firebreaks {FireConfig.EffectiveGroundFirebreaksEnabled}, " +
                   $"waterblocks {FireConfig.EffectiveGroundWaterBlocksSpreadEnabled}, " +
                   $"realpermanent (paintedcells {_groundPainted.Count}, regrowntrees {_treesRegrownCount}), " +
                   $"wind {FireConfig.WindSpreadBiasEnabled.Value} (upwindchance {FireConfig.WindUpwindIgniteChance.Value:F2}, " +
                   $"influence {FireConfig.WindInfluence.Value:F2}, live intensity {WindIntensityForStatus()}), " +
                   $"dousingradius {FireConfig.DousingBombRadius.Value}m, " +
                   $"douseimmunity {FireConfig.EffectiveDouseImmunitySeconds}s (wet {_dousedUntil.Count}), " +
                   $"rain (ground {FireConfig.EffectiveRainSuppressesGroundFire} x{FireConfig.RainGroundBurnDurationMultiplier.Value:F2}, " +
                   $"objects {FireConfig.EffectiveRainSuppressesObjectFire} x{FireConfig.RainObjectBurnDurationMultiplier.Value:F2}), " +
                   $"persist {FireConfig.PersistFiresEnabled.Value}, " +
                   $"groundleash {FireConfig.EffectiveGroundMaxSpreadDistanceEnabled} ({FireConfig.GroundMaxSpreadDistance.Value}m), " +
                   $"ramp {(GetRampFraction() * 100f):F0}% (enabled {FireConfig.EffectiveFireRampEnabled}, start {(FireConfig.FireRampStartFraction.Value * 100f):F0}%, duration {FireConfig.FireRampDurationSeconds.Value}s), " +
                   $"enabled {FireConfig.Enabled.Value}";
        }

        /// <summary>
        /// Live wind strength for the status line, or "n/a" if it can't be read.
        /// Shown alongside the influence setting because the two MULTIPLY — a
        /// correctly-set influence with near-zero intensity still looks like wind
        /// bias doing nothing, and chasing that without the live number visible is
        /// exactly the kind of invisible-setting debugging this status line exists
        /// to prevent.
        /// </summary>
        private static string WindIntensityForStatus()
        {
            float? intensity = ValheimBridge.GetWindIntensity();
            return intensity.HasValue ? intensity.Value.ToString("F2") : "n/a";
        }

        // ---------------------------------------------------------------
        // Public API — ground fire
        // ---------------------------------------------------------------

        /// <summary>
        /// Ground-to-ground propagation for ONE cycle: only the 8 immediately
        /// adjacent cells, never the full GroundSpreadRadius. This is the fix
        /// for a real bug in 0.4.0 — using the full radius here caused every
        /// burning cell to flood its entire reach in a single cycle, and every
        /// newly-lit cell to do the same the next cycle, compounding into a
        /// near-instant area explosion (and torching far more trees than
        /// intended) instead of a gradual advancing front. GroundSpreadRadius
        /// still governs how far a cell can reach out to ignite a real nearby
        /// object — that direction isn't self-compounding since it's bounded
        /// by how many real objects exist nearby, not by cell count.
        /// </summary>
        private void IgniteAdjacentGroundCells(GroundCellKey originKey, float y)
        {
            if (FireConfig.EffectiveRainSuppressesGroundFire && ValheimBridge.IsRainingAt(CellCenter(originKey, y))) return;

            // Wind is global, not per-zone, so both reads happen once here per
            // spread call rather than once per neighbor.
            Vector3? wind = FireConfig.WindSpreadBiasEnabled.Value ? ValheimBridge.GetWindDirection() : null;
            Vector2 windXZ = wind.HasValue ? new Vector2(wind.Value.x, wind.Value.z).normalized : Vector2.zero;

            // How hard the directional bias is actually applied: the WindInfluence
            // config scaled by vanilla's live wind strength, so weather now changes
            // the front's SHAPE and not just which way it leans. GetWindIntensity
            // returns 0 only before EnvMan's first UpdateWind (its clamp floor in
            // play is 0.05) — treat that, and a failed read, as "no data" and fall
            // back to full strength so the bias behaves as it did pre-0.17.2.
            float? intensity = wind.HasValue ? ValheimBridge.GetWindIntensity() : null;
            bool intensityUsable = intensity.HasValue && intensity.Value > 0f;
            if (wind.HasValue && !intensityUsable && !_windIntensityFallbackLogged)
            {
                _windIntensityFallbackLogged = true;
                FireLogger.Debug($"Wind intensity unusable (read {(intensity.HasValue ? intensity.Value.ToString("F3") : "null")}); " +
                                 "wind bias applying WindInfluence at full strength.");
            }

            float strength = Mathf.Clamp01(FireConfig.WindInfluence.Value) *
                             (intensityUsable ? Mathf.Clamp01(intensity.Value) : 1f);
            bool haveWind = wind.HasValue && windXZ != Vector2.zero && strength > 0f;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;

                    if (haveWind)
                    {
                        // dot: -1 = directly upwind, +1 = directly downwind. Lerp the
                        // ignite chance between the configured upwind floor and a
                        // guaranteed downwind catch, so wind narrows the front's spread
                        // width without ever fully walling off the upwind side. That
                        // directional chance is then faded toward 1 (ignite everything,
                        // i.e. no bias at all) by strength, so WindInfluence 0 or dead
                        // calm reproduces the old unweighted behavior exactly.
                        float dot = Vector2.Dot(windXZ, new Vector2(dx, dz).normalized);
                        float directional = Mathf.Lerp(FireConfig.WindUpwindIgniteChance.Value, 1f, (dot + 1f) * 0.5f);
                        float chance = Mathf.Lerp(1f, directional, strength);
                        if (Random.value > chance) continue;
                    }

                    TryIgniteGroundCell(new GroundCellKey(originKey.X + dx, originKey.Z + dz), y);
                }
            }
        }

        /// <summary>
        /// Ignite every ground cell within radius of a world position. Used
        /// for object-to-ground seeding (a burning tree/piece lighting the
        /// ground around its own fixed position — bounded, not compounding)
        /// and by the firegroundignite dev command for direct testing.
        /// NOT used for ground-to-ground propagation — see
        /// IgniteAdjacentGroundCells for why.
        ///
        /// PUBLIC CROSS-MOD CONTRACT (promoted 2026-08-27): Ragnarok's Wrath
        /// resolves this method by reflection as its storm-lightning igniter —
        /// the bridge's one WRITE, beside the two read surfaces above. Renaming
        /// or re-signing it silently disarms lightning over there (RW logs the
        /// absence once and goes dormant); same rules as
        /// CollectActiveFirePositions. Called standalone it starts a fresh fire
        /// event: TryIgniteGroundCell seeds _fireOrigin from the first cell and
        /// captures no igniter, so lightning fires are natural — attributed to
        /// nobody — by construction.
        /// </summary>
        public void IgniteGroundNear(Vector3 origin, float radius)
        {
            if (!FireConfig.GroundSpreadEnabled.Value) return;

            float size = FireConfig.GroundCellSize.Value;
            int range = Mathf.CeilToInt(radius / size);
            float radiusSqr = radius * radius;
            GroundCellKey originKey = KeyOf(origin);

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dz = -range; dz <= range; dz++)
                {
                    var key = new GroundCellKey(originKey.X + dx, originKey.Z + dz);
                    if (_groundBurning.ContainsKey(key)) continue;

                    Vector3 center = CellCenter(key, origin.y);
                    if ((center - origin).sqrMagnitude > radiusSqr) continue;

                    // Firebreak line check: TryIgniteGroundCell only tests the
                    // DESTINATION cell's terrain, so radius seeding could jump a
                    // narrow cultivated line entirely — fire on one side igniting
                    // grass on the far side without ever touching the break. Sample
                    // the terrain along the origin→destination line; if any point
                    // is cleared/cultivated, the ground path is broken and this
                    // cell can't be reached by ground-level spread from here.
                    if (FireConfig.EffectiveGroundFirebreaksEnabled && GroundPathCrossesFirebreak(origin, center)) continue;

                    TryIgniteGroundCell(key, origin.y);
                }
            }
        }

        /// <summary>
        /// True if the straight ground line between two points crosses cleared or
        /// cultivated terrain. Samples at half-cell steps so a break as narrow as
        /// one cultivator swipe can't fall between sample points. Origin and
        /// destination cells themselves are covered by their own per-cell checks.
        /// </summary>
        /// <summary>
        /// Cached IsClearedOrCultivated lookup, keyed by the ground cell containing
        /// the sample point. One reflected terrain query per cell per TTL window
        /// instead of per sample — the whole point, since line checks re-sample the
        /// same cells constantly during a burn.
        /// </summary>
        private bool IsFirebreakAt(Vector3 samplePoint)
        {
            GroundCellKey key = KeyOf(samplePoint);
            float now = Time.time;

            if (_firebreakCache.TryGetValue(key, out (float expiresAt, bool isBreak) cached) && now < cached.expiresAt)
            {
                return cached.isBreak;
            }

            bool isBreak = ValheimBridge.IsClearedOrCultivated(samplePoint);
            _firebreakCache[key] = (now + FirebreakCacheTtl, isBreak);
            return isBreak;
        }

        /// <summary>Periodic sweep of expired firebreak cache entries so it can't grow unbounded.</summary>
        private void PruneFirebreakCache()
        {
            float now = Time.time;
            if (now < _nextFirebreakCachePrune) return;
            _nextFirebreakCachePrune = now + FirebreakCacheTtl;

            if (_firebreakCache.Count == 0) return;
            _firebreakCacheScratch.Clear();
            foreach (KeyValuePair<GroundCellKey, (float expiresAt, bool isBreak)> kv in _firebreakCache)
            {
                if (now >= kv.Value.expiresAt) _firebreakCacheScratch.Add(kv.Key);
            }
            foreach (GroundCellKey key in _firebreakCacheScratch)
            {
                _firebreakCache.Remove(key);
            }
        }

        private bool GroundPathCrossesFirebreak(Vector3 from, Vector3 to)
        {
            float stepSize = FireConfig.GroundCellSize.Value * 0.5f;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= stepSize) return false; // adjacent — destination's own check suffices

            int steps = Mathf.CeilToInt(distance / stepSize);
            for (int i = 1; i < steps; i++)
            {
                Vector3 samplePoint = from + delta * (i / (float)steps);
                if (IsFirebreakAt(samplePoint)) return true;
            }
            return false;
        }

        private void TryIgniteGroundCell(GroundCellKey key, float y)
        {
            if (_groundBurning.ContainsKey(key)) return;
            // The exhausted dict holds burnout regrow timers AND douse immunity
            // (see ExtinguishGroundNear), so it must stay consultable when
            // either feature is on — not only fuel exhaustion.
            if ((FireConfig.EffectiveGroundFuelExhaustionEnabled || FireConfig.EffectiveDouseImmunitySeconds > 0f) &&
                _groundExhausted.TryGetValue(key, out float exhaustedUntil) && Time.time < exhaustedUntil) return;

            Vector3 approxCenter = CellCenter(key, y);

            // Real firebreak support: a dirt path or tilled/cultivated strip has
            // no grass fuel on it, so ground fire shouldn't cross it. Checked
            // before the ground-max/ramp bookkeeping below since this is a hard
            // "no fuel here" rule, not a capacity limit.
            if (FireConfig.EffectiveGroundFirebreaksEnabled && IsFirebreakAt(approxCenter)) return;

            // Leash: cell-to-adjacent-cell propagation (IgniteAdjacentGroundCells)
            // otherwise has NO distance limit at all, only a cap on how many cells
            // burn at once — a wind-driven front will happily march hundreds of
            // meters away from the origin, silently, since it's pure grid math
            // with no real GameObject required. Checked with the cheap inherited
            // y (not yet the real sampled height below) since only horizontal
            // distance matters here and this should reject before paying for a
            // raycast. Object-to-ground seeding (IgniteGroundNear) is already
            // bounded by GroundSpreadRadius and essentially never trips this.
            // Leashed against THIS cell's own blaze, not one global origin —
            // otherwise a fire far from the first was measured against the first
            // one's origin and refused outright, which is half of why a distant
            // second fire could never spread.
            int cellEventId = EventForPosition(approxCenter, 0L);
            FireEvent cellEvent = EventById(cellEventId);
            if (FireConfig.EffectiveGroundMaxSpreadDistanceEnabled && cellEvent != null)
            {
                float maxDist = FireConfig.GroundMaxSpreadDistance.Value;
                if ((approxCenter - cellEvent.Origin).sqrMagnitude > maxDist * maxDist) return;
            }

            int effectiveGroundMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.EffectiveGroundMaxConcurrent * GetRampFraction(cellEventId)));
            if (GroundCountForEvent(cellEventId) >= effectiveGroundMax) return; // silent drop, natural retry next cycle

            if (_fireStartTime < 0f) _fireStartTime = Time.time;
            if (_fireOrigin == null) _fireOrigin = approxCenter;

            // y here is inherited from whatever ignited this cell (neighbor or
            // object) — only an approximation, used as a reasonable starting
            // point for the real height query below. Over many hops of
            // ground-to-ground spread across sloped terrain, a purely-inherited
            // Y drifts noticeably from the real surface (floating fire). Sample
            // the actual terrain height at this cell's real (x,z) instead, once,
            // at ignition time — same cheap "computed once" cost as before, just
            // accurate now.
            float realY = ValheimBridge.GetGroundHeight(approxCenter);

            // No grass grows on open water — without this, ground fire had no
            // way to tell "actual land" from "ocean/lake", and could spread
            // straight across a shoreline (confirmed: a small island's fire
            // spread clean through the water around it). Checked using the
            // REAL sampled height, not the approximate inherited y, since
            // that's the only value we can trust to be accurate here.
            if (FireConfig.EffectiveGroundWaterBlocksSpreadEnabled)
            {
                float waterLevel = ValheimBridge.GetWaterLevel();
                if (realY <= waterLevel)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] TryIgniteGroundCell({key.X},{key.Z}): blocked, realY={realY:F2} <= waterLevel={waterLevel:F2}.");
                    return;
                }
            }

            // Rain no longer shortens a new cell up front: AgeFiresInRain runs a
            // cell's clock faster for as long as rain falls on it, which lands in
            // the same place for a cell lit in rain and also covers the cells the
            // rain arrives on later.
            float duration = FireConfig.GroundBurnDurationSeconds.Value;

            _groundBurning[key] = new GroundCellState { ExpireAt = Time.time + duration, Y = realY, EventId = cellEventId };
            FireLogger.Debug($"Ground ignited ({_groundBurning.Count}/{effectiveGroundMax}) at cell ({key.X},{key.Z})");
            SpawnGroundVfxFor(key, CellCenter(key, realY));
            _groundIgnitedSinceFlush.Add((key, realY));
        }

        private void SpawnGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            if (_groundVfx.ContainsKey(key)) return;

            bool wantDamage = FireConfig.FireHurtsEnabled.Value;
            bool wantVisual = FireConfig.UseProceduralVfx.Value;
            if (!wantVisual && !wantDamage) return;

            // Overall cap on tracked ground effect objects (visual and/or damage-only
            // combined) — bounded by the higher of the two per-purpose caps, since a
            // damage-only object is cheap (just a polling FireBurnZone, no particles).
            int overallCap = Mathf.Max(FireConfig.EffectiveGroundVfxMaxConcurrent, FireConfig.EffectiveGroundDamageMaxConcurrent);
            if (_groundVfx.Count >= overallCap) return;

            // Visual has its OWN sub-cap, checked independently — this used to share
            // GroundVfxMaxConcurrent with damage entirely, meaning only ~30 of up to
            // 200 burning cells ever got a damage zone at all (objects have their own
            // separate, much higher cap and worked fine — that's why fire only hurt
            // near trees/pieces, never out in open ground).
            if (wantVisual)
            {
                int visualCount = 0;
                foreach (GameObject go in _groundVfx.Values)
                {
                    if (go != null && go.GetComponent<ParticleSystem>() != null) visualCount++;
                }
                if (visualCount >= FireConfig.EffectiveGroundVfxMaxConcurrent) wantVisual = false;
            }

            // Damage gets its own independent check against its own (much higher) cap.
            if (wantDamage)
            {
                int damageCount = 0;
                foreach (GameObject go in _groundVfx.Values)
                {
                    if (go != null && go.GetComponent<FireBurnZone>() != null) damageCount++;
                }
                if (damageCount >= FireConfig.EffectiveGroundDamageMaxConcurrent) wantDamage = false;
            }

            if (!wantVisual && !wantDamage) return;

            GameObject instance = wantVisual
                ? ValheimBridge.CreateProceduralGroundFireVfx(position)
                : new GameObject("FireFrontGroundDamageZone");
            if (!wantVisual) instance.transform.position = position;

            if (wantDamage)
            {
                ValheimBridge.AttachFireDamageZone(instance, FireConfig.GroundCellSize.Value * 0.5f,
                    FireConfig.FireHurtsPlayerOnly.Value, FireConfig.FireDamagePerTick.Value, FireConfig.FireDamageTickInterval.Value);
            }

            _groundVfx[key] = instance;
        }

        private void RemoveGroundVfxFor(GroundCellKey key)
        {
            if (_groundVfx.TryGetValue(key, out GameObject instance))
            {
                _groundVfx.Remove(key);
                if (instance != null) Destroy(instance);
            }
        }

        private GroundCellKey KeyOf(Vector3 pos)
        {
            float size = FireConfig.GroundCellSize.Value;
            return new GroundCellKey(Mathf.FloorToInt(pos.x / size), Mathf.FloorToInt(pos.z / size));
        }

        private Vector3 CellCenter(GroundCellKey key, float y)
        {
            float size = FireConfig.GroundCellSize.Value;
            return new Vector3((key.X + 0.5f) * size, y, (key.Z + 0.5f) * size);
        }

        // ---------------------------------------------------------------
        // Cycle steps — object fire
        // ---------------------------------------------------------------

        private void StartBurning(Component target, ZDOID id, int eventId)
        {
            Vector3 position = ValheimBridge.PositionOf(target);
            _burning[id] = new BurningState
            {
                ExpireAt = Time.time + FireConfig.BurnDurationSeconds.Value,
                IgnitedAt = Time.time,
                Position = position,
                PrefabName = ValheimBridge.PrefabNameOf(target),
                EventId = eventId
            };
            FireLogger.Debug($"Ignited ({_burning.Count}/{FireConfig.MaxConcurrentBurning.Value}): {ValheimBridge.NameOf(target)}");
            SpawnVfxFor(id, position, target);

            // Only ever called on the server (TryIgnite is server-gated — see the
            // Harmony patches), so this is always the real fire starting. Tell
            // every connected peer so they can show it locally too.
            FireLogger.Debug($"[IGNITE-TRACE] StartBurning: broadcasting FireEvent(started=true) for {ValheimBridge.NameOf(target)}, ZDOID={id}.");
            ValheimBridge.BroadcastFireEvent(id, started: true);
        }

        private void SpawnVfxFor(ZDOID id, Vector3 position, Component target = null)
        {
            if (_vfx.ContainsKey(id)) return;

            bool wantVisual = FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value);
            bool wantDamage = FireConfig.FireHurtsEnabled.Value;
            if (!wantVisual && !wantDamage) return;

            GameObject instance = null;
            if (FireConfig.UseProceduralVfx.Value)
            {
                instance = ValheimBridge.CreateProceduralFireVfx(
                    position, ValheimBridge.MeasureBurnerHeight(target), ValheimBridge.MeasureBurnerCrownRadius(target));
            }
            else if (!string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value))
            {
                GameObject prefab = ValheimBridge.FindPrefabByName(FireConfig.VfxPrefabName.Value);
                if (prefab != null) instance = ValheimBridge.SpawnVfx(prefab, position);
            }

            // No visual configured or available, but damage is still wanted —
            // spawn a bare invisible container so FireHurtsEnabled doesn't
            // silently depend on visuals being on.
            if (instance == null && wantDamage)
            {
                instance = new GameObject("FireFrontDamageZone");
                instance.transform.position = position;
            }

            if (instance == null) return;

            if (wantDamage)
            {
                ValheimBridge.AttachFireDamageZone(instance, FireConfig.FireHurtsObjectRadius.Value,
                    FireConfig.FireHurtsPlayerOnly.Value, FireConfig.FireDamagePerTick.Value, FireConfig.FireDamageTickInterval.Value);
            }

            _vfx[id] = instance;
        }

        /// <summary>
        /// Server-side RPC handler: a client's RPC_Damage prefix couldn't ignite
        /// locally (it isn't the server) and forwarded the request here instead.
        /// Defensive IsServer() check even though only the server should ever
        /// receive this — ZRoutedRpc.Register runs the same handler on every peer
        /// that has it registered, and this is deliberately targeted at the
        /// server peer ID, but cheap to double-check.
        /// </summary>
        private void HandleIgniteRequest(long sender, ZDOID id, long igniterPlayerId)
        {
            if (!ValheimBridge.IsServer())
            {
                FireLogger.Debug($"[IGNITE-TRACE] HandleIgniteRequest received on a non-server peer for ZDOID={id} — ignoring (shouldn't normally happen).");
                return;
            }

            FireLogger.Debug($"[IGNITE-TRACE] Server received ignite request from peer {sender} for ZDOID={id}, igniter={igniterPlayerId}.");
            Component target = ValheimBridge.ComponentFromZdoid(id);
            if (target != null)
            {
                FireLogger.Debug($"[IGNITE-TRACE] Resolved ZDOID={id} to {ValheimBridge.NameOf(target)} — calling TryIgnite.");
                TryIgnite(target, igniterPlayerId);
            }
            else
            {
                FireLogger.Debug($"[IGNITE-TRACE] Could NOT resolve ZDOID={id} on first attempt — queuing for retry " +
                                  "(likely just not instantiated in the server's ZNetScene yet).");
                _pendingIgniteResolutions.Add(new PendingIgniteResolution
                {
                    Id = id,
                    RetryAt = Time.time + IgniteResolutionRetryInterval,
                    Attempts = 0,
                    IgniterPlayerId = igniterPlayerId
                });
            }
        }

        private const float IgniteResolutionRetryInterval = 0.5f;
        private const int IgniteResolutionMaxAttempts = 20; // ~10 seconds total before giving up

        /// <summary>
        /// Server-side: retries resolving queued ignite requests whose ZDOID
        /// exists but had no local GameObject yet. Runs every cycle as part of
        /// the main server-only simulation loop.
        /// </summary>
        private void ProcessPendingIgniteResolutions()
        {
            if (_pendingIgniteResolutions.Count == 0) return;

            float now = Time.time;
            _igniteResolutionScratchIndices.Clear();

            for (int i = 0; i < _pendingIgniteResolutions.Count; i++)
            {
                PendingIgniteResolution entry = _pendingIgniteResolutions[i];
                if (now < entry.RetryAt) continue;

                Component target = ValheimBridge.ComponentFromZdoid(entry.Id);
                if (target != null)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] Retry resolved ZDOID={entry.Id} to {ValheimBridge.NameOf(target)} " +
                                      $"after {entry.Attempts + 1} attempt(s) — calling TryIgnite.");
                    TryIgnite(target, entry.IgniterPlayerId);
                    _igniteResolutionScratchIndices.Add(i);
                    continue;
                }

                entry.Attempts++;
                if (entry.Attempts >= IgniteResolutionMaxAttempts)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] Gave up resolving ZDOID={entry.Id} after {entry.Attempts} attempts.");
                    _igniteResolutionScratchIndices.Add(i);
                }
                else
                {
                    entry.RetryAt = now + IgniteResolutionRetryInterval;
                    _pendingIgniteResolutions[i] = entry;
                }
            }

            for (int i = _igniteResolutionScratchIndices.Count - 1; i >= 0; i--)
            {
                _pendingIgniteResolutions.RemoveAt(_igniteResolutionScratchIndices[i]);
            }
        }

        /// <summary>
        /// Broadcast handler: the server just started or stopped a real fire.
        /// The server itself no-ops here — it already has the authoritative VFX
        /// from its own StartBurning/RemoveVfxFor. Every other peer spawns/
        /// removes a local, non-authoritative, no-damage copy so they can see
        /// the fire even though they aren't simulating it.
        /// </summary>

        // When each remote VFX started showing, so a client can drop it to
        // embers on its own clock without any extra network traffic.
        private readonly Dictionary<Component, float> _remoteVfxSpawnedAt = new Dictionary<Component, float>();
        private readonly HashSet<Component> _remoteSmouldering = new HashSet<Component>();
        private readonly List<Component> _remoteSmoulderScratch = new List<Component>();
        private float _nextRemoteSmoulderCheck;

        /// <summary>
        /// Client-side half of the smouldering downgrade, and the half that
        /// actually saves frames: the server is headless, so its own _vfx render
        /// nothing — what a player SEES is _remoteVfx, spawned here from
        /// FireEvent broadcasts. Each client runs this on its own clock from
        /// when it started showing a given fire, so no extra sync is needed.
        /// Cosmetic only, and throttled: nothing here is worth a per-frame pass.
        /// </summary>
        private void ProcessRemoteSmouldering()
        {
            if (!FireConfig.SmoulderingVfxEnabled.Value) return;
            if (_remoteVfx.Count == 0) return;
            if (Time.time < _nextRemoteSmoulderCheck) return;
            _nextRemoteSmoulderCheck = Time.time + 2f;

            float after = FireConfig.BurnDurationSeconds.Value * Mathf.Clamp01(FireConfig.SmoulderAfterFraction.Value);
            float now = Time.time;

            _remoteSmoulderScratch.Clear();
            foreach (KeyValuePair<Component, float> kv in _remoteVfxSpawnedAt)
            {
                if (_remoteSmouldering.Contains(kv.Key)) continue;
                if (now - kv.Value < after) continue;
                _remoteSmoulderScratch.Add(kv.Key);
            }

            for (int i = 0; i < _remoteSmoulderScratch.Count; i++)
            {
                Component c = _remoteSmoulderScratch[i];
                _remoteSmouldering.Add(c); // latch first — a throw must not retry forever
                try
                {
                    if (_remoteVfx.TryGetValue(c, out GameObject go))
                        ValheimBridge.DowngradeVfxToSmoulder(go);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"[SMOULDER] remote downgrade failed: {ex.Message}");
                }
            }
        }
        private void HandleFireEventBroadcast(long sender, ZDOID id, bool started)
        {
            // Receipt trace — kept permanently, see HandleGroundFireSync.
            FireLogger.Debug($"[SYNC-DIAG] FireEvent arrived from {sender} (id={id}, started={started}, IsServer={ValheimBridge.IsServer()}).");
            if (ValheimBridge.IsServer()) return;

            Component target = ValheimBridge.ComponentFromZdoid(id);
            if (target == null) return; // not loaded on this peer (out of range) — nothing to show

            if (started) EnqueueRemoteObjectVfx(target);
            else RemoveRemoteVfxFor(target);
        }

        private readonly List<Component> _remoteObjectVfxQueue = new List<Component>();
        private readonly HashSet<Component> _remoteObjectVfxQueued = new HashSet<Component>();

        /// <summary>
        /// Deliberately smaller than RemoteVfxSpawnsPerFrame: an object fire
        /// builds two or three ParticleSystems plus a realtime Light, where a
        /// ground cell builds one cheap system and no light.
        /// </summary>
        private const int RemoteObjectVfxSpawnsPerFrame = 2;

        /// <summary>
        /// Queues an object fire's VFX rather than building it inside the RPC.
        /// </summary>
        /// <remarks>
        /// Ground fire has been budgeted since 0.18.6; object fire never was, and
        /// it is the more expensive of the two per instance. The server's spread
        /// cycle ignites in batches, so every burner lit in one pass had its
        /// whole rig constructed in a single frame on the client. Measured
        /// 2026-09-12 with 34 objects alight: CPU spikes of 3-4x the median,
        /// arriving about every 1.4s against a 0.75s spread interval.
        ///
        /// 0.20.1 made each of those constructions several times heavier for a
        /// tall burner (a taller particle column, a crown-spark system, a longer
        /// light range), which is what turned a latent cost into a visible one.
        /// The fix is the pattern this file already uses for ground cells rather
        /// than a new one.
        /// </remarks>
        private void EnqueueRemoteObjectVfx(Component target)
        {
            if (target == null) return;
            if (_remoteVfx.ContainsKey(target)) return;
            if (!_remoteObjectVfxQueued.Add(target)) return;
            _remoteObjectVfxQueue.Add(target);
        }

        private void DrainRemoteObjectVfxQueue()
        {
            int spawned = 0;
            while (_remoteObjectVfxQueue.Count > 0 && spawned < RemoteObjectVfxSpawnsPerFrame)
            {
                Component target = _remoteObjectVfxQueue[_remoteObjectVfxQueue.Count - 1];
                _remoteObjectVfxQueue.RemoveAt(_remoteObjectVfxQueue.Count - 1);
                _remoteObjectVfxQueued.Remove(target);

                // It can have been destroyed, unloaded or extinguished while it
                // sat in the queue; Unity reports a destroyed Component as null.
                if (target == null) continue;

                SpawnRemoteVfxOnly(target);
                spawned++;
            }
        }

        private void SpawnRemoteVfxOnly(Component target)
        {
            if (_remoteVfx.ContainsKey(target)) return;

            bool wantVisual = FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value);
            if (!wantVisual) return;

            GameObject instance = null;
            if (FireConfig.UseProceduralVfx.Value)
            {
                instance = ValheimBridge.CreateProceduralFireVfx(
                    ValheimBridge.PositionOf(target), ValheimBridge.MeasureBurnerHeight(target), ValheimBridge.MeasureBurnerCrownRadius(target));
            }
            else
            {
                GameObject prefab = ValheimBridge.FindPrefabByName(FireConfig.VfxPrefabName.Value);
                if (prefab != null) instance = ValheimBridge.SpawnVfx(prefab, ValheimBridge.PositionOf(target));
            }

            if (instance != null)
            {
                _remoteVfx[target] = instance;
                _remoteVfxSpawnedAt[target] = Time.time; // this client's own smoulder clock
            }
        }

        private void RemoveRemoteVfxFor(Component target)
        {
            // Drop it from the queue too, or an extinguished burner still gets a
            // fire built for it a frame or two later and is never cleaned up.
            if (_remoteObjectVfxQueued.Remove(target)) _remoteObjectVfxQueue.Remove(target);

            if (_remoteVfx.TryGetValue(target, out GameObject instance))
            {
                _remoteVfx.Remove(target);
                _remoteVfxSpawnedAt.Remove(target);
                _remoteSmouldering.Remove(target);
                if (instance != null) Destroy(instance);
            }
        }

        private void RemoveVfxFor(ZDOID id)
        {
            if (_vfx.TryGetValue(id, out GameObject instance))
            {
                _vfx.Remove(id);
                if (instance != null) Destroy(instance);
            }

            // Covers burn-out, manual extinguish, ClearAll, and removal-by-other-
            // means — every path that stops a fire routes through here.
            ValheimBridge.BroadcastFireEvent(id, started: false);
        }

        private void PruneStale()
        {
            _scratch.Clear();
            foreach (ZDOID id in _burning.Keys)
            {
                // ZDO-existence poll, NOT a live-Component check. This is the
                // actual fix: the old IsAlive(Component) check treated "no
                // local GameObject right now" the same as "really destroyed" —
                // but a dedicated server can de-instantiate an object (ZDO
                // still exists) independent of whether it's actually gone.
                // Only remove things that are truly, permanently destroyed.
                if (!ValheimBridge.ZdoExists(id)) _scratch.Add(id);
            }
            foreach (ZDOID dead in _scratch)
            {
                _burning.Remove(dead);
                FireLogger.Debug($"[IGNITE-TRACE] PruneStale: removed {dead} (ZDO no longer exists — really destroyed).");
                RemoveVfxFor(dead);
            }
        }

        private const int KillResolutionMaxAttempts = 20; // ~15s at the 0.75s default cycle before giving up

        // Rain aging: while rain falls on a fire its clock runs at 1/multiplier
        // speed - at the 0.3 default, three and a third times faster - so it dies
        // over a minute or two instead of vanishing, and a fire the rain reaches
        // late loses only a share of what it had left. Object and ground fire
        // each on their own multiplier. Persistence stores time-left, so an aged
        // fire restores exactly as aged. IgnitedAt is left alone: the maturity
        // gate asks how long a burner has been alight, which rain does not change.
        private float _lastRainAgeTime = -1f;

        private void AgeFiresInRain()
        {
            float now = Time.time;
            float dt = _lastRainAgeTime < 0f ? 0f : now - _lastRainAgeTime;
            _lastRainAgeTime = now;
            if (dt <= 0f) return;

            if (FireConfig.EffectiveRainSuppressesObjectFire && _burning.Count > 0)
            {
                float extra = dt * (1f / Mathf.Max(0.05f, FireConfig.RainObjectBurnDurationMultiplier.Value) - 1f);
                if (extra > 0f)
                {
                    _scratch.Clear();
                    foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
                        if (ValheimBridge.IsRainingAt(kv.Value.Position)) _scratch.Add(kv.Key);
                    foreach (ZDOID id in _scratch)
                    {
                        BurningState st = _burning[id];
                        st.ExpireAt -= extra;
                        _burning[id] = st;
                    }
                }
            }

            if (FireConfig.EffectiveRainSuppressesGroundFire && _groundBurning.Count > 0)
            {
                float extra = dt * (1f / Mathf.Max(0.05f, FireConfig.RainGroundBurnDurationMultiplier.Value) - 1f);
                if (extra > 0f)
                {
                    _groundScratch.Clear();
                    foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                        if (ValheimBridge.IsRainingAt(CellCenter(kv.Key, kv.Value.Y))) _groundScratch.Add(kv.Key);
                    foreach (GroundCellKey key in _groundScratch)
                    {
                        GroundCellState st = _groundBurning[key];
                        st.ExpireAt -= extra;
                        _groundBurning[key] = st;
                    }
                }
            }
        }

        /// <summary>"3/16": burners currently under rain, for the status line.</summary>
        private string RainingBurnersForStatus()
        {
            int wet = 0;
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
                if (ValheimBridge.IsRainingAt(kv.Value.Position)) wet++;
            return wet + "/" + _burning.Count;
        }

        private void ExpireTimers()
        {
            _scratch.Clear();
            float now = Time.time;
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                if (now >= kv.Value.ExpireAt) _scratch.Add(kv.Key);
            }

            // Throttle: destroying many ZNetView objects in one tight burst
            // within a single frame can race with ZNetScene's own per-frame
            // bookkeeping (observed during the 0.4.0 ground-spread bug, which
            // killed ~15+ trees near-simultaneously and produced the same
            // ZNetScene.RemoveObjects NullReferenceException the 0.3.2 fix was
            // meant to prevent — that time from batch size, not a wrong API).
            // Cap how many actually get destroyed this cycle; anything past
            // the limit just stays in _burning past its nominal expiry and
            // gets caught on a later cycle instead of all in one frame.
            int killed = 0;
            foreach (ZDOID id in _scratch)
            {
                if (killed >= FireConfig.EffectiveMaxKillsPerCycle) break;

                BurningState state = _burning[id];

                // A live Component is only needed at this exact moment (to
                // actually call Destroy on) — resolved fresh here rather than
                // held onto for the whole burn duration, since we've confirmed
                // that reference can go stale independent of anything we do.
                Component target = ValheimBridge.ComponentFromZdoid(id);
                if (target == null)
                {
                    state.KillAttempts++;
                    if (state.KillAttempts >= KillResolutionMaxAttempts)
                    {
                        FireLogger.Debug($"[IGNITE-TRACE] ExpireTimers: gave up resolving {id} to kill after " +
                                          $"{state.KillAttempts} attempts — removing from _burning anyway (possible orphan).");
                        _burning.Remove(id);
                        RemoveVfxFor(id);
                    }
                    else
                    {
                        _burning[id] = state; // write back the incremented attempt count
                    }
                    continue;
                }

                _burning.Remove(id);
                FireLogger.Debug($"Burned down: {ValheimBridge.NameOf(target)}");

                if (FireConfig.EffectiveTreeRegrowthEnabled && ValheimBridge.KindOf(target) == BurnKind.Tree)
                {
                    EnqueueRegrowth(new PendingRegrowth
                    {
                        PrefabName = state.PrefabName,
                        Position = state.Position,
                        RegrowAt = Time.time + FireConfig.TreeRegrowthSeconds.Value
                    });
                }

                ValheimBridge.KillBurningTarget(target);
                RemoveVfxFor(id);
                killed++;
            }
        }


        /// <summary>
        /// Drops long-burning fires to a smouldering visual. COSMETIC ONLY —
        /// nothing here touches burn timers, spread or damage; a smouldering
        /// object burns and spreads exactly as it did with full flames.
        /// Wrapped per-burner so a VFX failure can never abort the cycle
        /// (house rule: cosmetics stay off the gameplay path).
        /// </summary>
        private void ProcessSmouldering()
        {
            if (!FireConfig.SmoulderingVfxEnabled.Value) return;
            if (_burning.Count == 0) return;

            float after = FireConfig.BurnDurationSeconds.Value * Mathf.Clamp01(FireConfig.SmoulderAfterFraction.Value);
            float now = Time.time;

            _smoulderScratch.Clear();
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                if (kv.Value.Smouldering) continue;
                if (now - kv.Value.IgnitedAt < after) continue;
                _smoulderScratch.Add(kv.Key);
            }

            for (int i = 0; i < _smoulderScratch.Count; i++)
            {
                ZDOID id = _smoulderScratch[i];
                if (!_burning.TryGetValue(id, out BurningState s)) continue;
                try
                {
                    if (_vfx.TryGetValue(id, out GameObject instance))
                        ValheimBridge.DowngradeVfxToSmoulder(instance);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"[SMOULDER] downgrade failed for {id}: {ex.Message}");
                }
                finally
                {
                    // Latch regardless: a downgrade that threw must not be retried
                    // every cycle for the rest of the burn.
                    s.Smouldering = true;
                    _burning[id] = s;
                }
            }

            if (_smoulderScratch.Count > 0)
                FireLogger.Debug($"[SMOULDER] {_smoulderScratch.Count} fire(s) dropped to embers.");
        }

        private readonly List<ZDOID> _smoulderScratch = new List<ZDOID>();
        private void PromoteFromQueue()
        {
            // Bounded by the queue rather than by one global burning count: each
            // candidate is admitted only if ITS OWN blaze has room, so a raging
            // fire can no longer eat the promotions belonging to a different one.
            // A candidate whose event is full is dropped exactly as before —
            // spread re-offers it next cycle.
            int guard = _queue.Capacity + 1;
            while (guard-- > 0)
            {
                ZDOID next = _queue.DequeueNextValid();
                if (next.Equals(ZDOID.None)) return;
                if (_burning.ContainsKey(next)) continue;

                Component target = ValheimBridge.ComponentFromZdoid(next);
                if (target == null || !ValheimBridge.IsAlive(target) || !ValheimBridge.IsBurnable(target)) continue;

                // A queued item rejoins whichever blaze is nearest it now.
                int evId = EventForPosition(ValheimBridge.PositionOf(target), 0L);
                int evMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.EffectiveMaxConcurrentBurning * GetRampFraction(evId)));
                if (BurningCountForEvent(evId) >= evMax) continue;

                StartBurning(target, next, evId);
            }
        }

        // ---------------------------------------------------------------
        // Cycle steps — ground fire
        // ---------------------------------------------------------------

        private void ExpireGroundTimers()
        {
            _groundScratch.Clear();
            float now = Time.time;
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                if (now >= kv.Value.ExpireAt) _groundScratch.Add(kv.Key);
            }
            foreach (GroundCellKey key in _groundScratch)
            {
                float y = _groundBurning[key].Y;
                _groundBurning.Remove(key);
                if (FireConfig.EffectiveGroundFuelExhaustionEnabled)
                    _groundExhausted[key] = now + FireConfig.GroundFuelRegrowSeconds.Value;
                RemoveGroundVfxFor(key);
                LeaveScorchMark(key, y);
                FireLogger.Debug($"Ground burned out at cell ({key.X},{key.Z})");
                _groundExpiredSinceFlush.Add(key);
            }

            // Prune exhaustion entries whose regrow window has passed. Without this,
            // a long player-sustained fire (one that never fully dies) would keep
            // _groundExhausted growing for the entire session with no eviction —
            // this is what caused the runaway entry count.
            if ((FireConfig.EffectiveGroundFuelExhaustionEnabled || FireConfig.EffectiveDouseImmunitySeconds > 0f) && _groundExhausted.Count > 0)
            {
                _exhaustedScratch.Clear();
                foreach (KeyValuePair<GroundCellKey, float> kv in _groundExhausted)
                {
                    if (now >= kv.Value) _exhaustedScratch.Add(kv.Key);
                }
                foreach (GroundCellKey key in _exhaustedScratch)
                {
                    _groundExhausted.Remove(key);
                }
            }

            // Same sweep for doused-object immunity — small dict, same cadence.
            if (_dousedUntil.Count > 0)
            {
                _scratch.Clear();
                foreach (KeyValuePair<ZDOID, float> kv in _dousedUntil)
                {
                    if (now >= kv.Value) _scratch.Add(kv.Key);
                }
                foreach (ZDOID id in _scratch) _dousedUntil.Remove(id);
            }
        }

        /// <summary>
        /// Debug/test hook: forces every pending regrowth entry to attempt right now instead of
        /// waiting out its timer, and returns (attempted, regrown, stillPending). The three
        /// numbers are separate on purpose: an entry can leave the queue by growing a tree OR by
        /// being dropped, and a caller that only saw the pending count fall could not tell those
        /// apart - it read a deletion as a success.
        /// </summary>
        public (int attempted, int regrown, int stillPending) ForceTreeRegrowthNow()
        {
            // Check BEFORE rewriting any timer. ProcessTreeRegrowth returns immediately when
            // regrowth is off, so zeroing every RegrowAt first would throw away the whole queue's
            // backoff schedule to accomplish nothing.
            if (!FireConfig.EffectiveTreeRegrowthEnabled) return (0, 0, _pendingRegrowth.Count);

            int attempted = _pendingRegrowth.Count;
            for (int i = 0; i < _pendingRegrowth.Count; i++)
            {
                PendingRegrowth entry = _pendingRegrowth[i];
                entry.RegrowAt = Time.time;
                _pendingRegrowth[i] = entry;
            }
            int regrown = ProcessTreeRegrowth();
            return (attempted, regrown, _pendingRegrowth.Count);
        }

        /// <summary>
        /// True if fire is burning within <paramref name="radius"/> of a point: any ground cell, or
        /// any object. Regrowth asks before planting, because a tree that comes back into ground
        /// that is still alight simply burns down again - observed live on 2026-09-18, when five of
        /// eight regrown trees reignited within seconds of spawning, since ground fire routinely
        /// outlives the 900 s regrowth timer.
        /// </summary>
        private bool IsFireNear(Vector3 pos, float radius)
        {
            float size = Mathf.Max(0.5f, FireConfig.GroundCellSize.Value);
            GroundCellKey origin = KeyOf(pos);
            int range = Mathf.Clamp(Mathf.CeilToInt(radius / size), 1, 8);
            for (int dx = -range; dx <= range; dx++)
                for (int dz = -range; dz <= range; dz++)
                    if (_groundBurning.ContainsKey(new GroundCellKey(origin.X + dx, origin.Z + dz))) return true;

            float radiusSqr = radius * radius;
            foreach (BurningState st in _burning.Values)
                if ((st.Position - pos).sqrMagnitude <= radiusSqr) return true;
            return false;
        }

        /// <summary>
        /// One scan of player-built ZDOs serves a whole cluster of due entries. Returns false when
        /// the scan could not be made at all, which the caller treats as "ask again later" rather
        /// than as permission to build.
        /// </summary>
        private bool PlayerBuiltNear(Vector3 pos, float radius, out bool known)
        {
            if (!_builtScanValid || (pos - _builtScanCentre).sqrMagnitude > 32f * 32f)
            {
                known = ValheimBridge.CollectPlayerBuiltPositionsNear(pos, _builtNearScratch);
                _builtScanValid = known;
                _builtScanCentre = pos;
                if (!known) return false;
            }
            known = true;
            float radiusSqr = radius * radius;
            for (int i = 0; i < _builtNearScratch.Count; i++)
                if ((_builtNearScratch[i] - pos).sqrMagnitude <= radiusSqr) return true;
            return false;
        }

        /// <summary>
        /// Sweeps pending tree regrowth entries and plants the ones that are due. Returns how many
        /// actually grew. Small-scope by design: no stump placeholder, same species only.
        ///
        /// Three ways an entry does NOT grow this pass, and they are deliberately different:
        /// fire still burning at the spot defers it without cost; something player-built within
        /// BuildClearance drops it for good; a spawn that genuinely fails retries on a backoff and
        /// gives up after MaxAttempts. Only the last of those spends an attempt - before 0.21.5
        /// every deferral did, which on a dedicated server (where the old readiness gate could
        /// never pass) silently deleted the entire queue about ten minutes after a fire.
        /// </summary>
        private int ProcessTreeRegrowth()
        {
            if (_pendingRegrowth.Count == 0) return 0;

            // The kill switch has to be honoured HERE and not only where entries are enqueued.
            // Until 0.21.5 nothing headless ever spawned, so an admin turning regrowth off
            // mid-fire got what they asked for by accident; now the queue would keep planting for
            // another fifteen minutes and survive a restart. Entries are kept, not discarded, so
            // turning it back on resumes where it left off.
            if (!FireConfig.EffectiveTreeRegrowthEnabled) return 0;

            const float retryBackoffSeconds = 30f;
            const int maxAttempts = 20;      // real spawn failures only
            const float buildClearance = 3f; // a floor or wall this close to the stump wins
            const float fireClearance = 4f;  // ground fire this close would light the new tree at once
            float now = Time.time;
            int regrew = 0, builtOver = 0, gaveUp = 0, waitingOnFire = 0;
            _regrowthScratchIndices.Clear();
            _builtScanValid = false;

            for (int i = 0; i < _pendingRegrowth.Count; i++)
            {
                PendingRegrowth entry = _pendingRegrowth[i];
                if (now < entry.RegrowAt) continue;

                if (IsFireNear(entry.Position, fireClearance))
                {
                    entry.RegrowAt = now + retryBackoffSeconds;
                    _pendingRegrowth[i] = entry;
                    waitingOnFire++;
                    continue;
                }

                bool known;
                bool built = PlayerBuiltNear(entry.Position, buildClearance, out known);
                if (!known)
                {
                    // Could not tell. Never plant on a maybe.
                    entry.RegrowAt = now + retryBackoffSeconds;
                    _pendingRegrowth[i] = entry;
                    continue;
                }
                if (built)
                {
                    FireLogger.Debug($"[REGROW] {entry.PrefabName} at {entry.Position} stays gone: something player-built stands within {buildClearance:F0}m of the stump.");
                    _regrowthScratchIndices.Add(i);
                    builtOver++;
                    continue;
                }

                if (ValheimBridge.TrySpawnTree(entry.PrefabName, entry.Position))
                {
                    FireLogger.Debug($"[REGROW] {entry.PrefabName} regrew at {entry.Position}.");
                    _treesRegrownCount++;
                    regrew++;
                    _regrowthScratchIndices.Add(i);
                    continue;
                }

                entry.Attempts++;
                if (entry.Attempts >= maxAttempts)
                {
                    FireLogger.Debug($"[REGROW] gave up on {entry.PrefabName} at {entry.Position} after {entry.Attempts} failed spawns.");
                    _regrowthScratchIndices.Add(i);
                    gaveUp++;
                }
                else
                {
                    entry.RegrowAt = now + retryBackoffSeconds;
                    _pendingRegrowth[i] = entry;
                }
            }

            // Remove completed/abandoned entries back-to-front so indices stay valid.
            for (int i = _regrowthScratchIndices.Count - 1; i >= 0; i--)
                _pendingRegrowth.RemoveAt(_regrowthScratchIndices[i]);

            // ONE line per cycle, never one per tree: a forest coming back after a big fire would
            // otherwise be a hundred lines at once. Silence while entries merely wait out the fire.
            if (regrew > 0 || builtOver > 0 || gaveUp > 0)
                FireLogger.Info($"[REGROW] {regrew} regrew, {builtOver} stayed gone (built over), {gaveUp} gave up, " +
                                $"{waitingOnFire} waiting for the fire to pass; {_pendingRegrowth.Count} still pending, " +
                                $"{_treesRegrownCount} total since boot.");

            // Write the store NOW rather than at the next 60 s tick. A tree that grew is a change
            // to the world, but the entry that produced it only left memory: a hard kill inside
            // that window would read the entry back and plant a second tree inside the first.
            if (regrew > 0)
            {
                PersistFiresNow();
                _nextPersistSave = Time.time + PersistSaveInterval;
            }
            return regrew;
        }

        /// <summary>Debug hook: snapshot of pending regrowth entries for console inspection.</summary>
        public List<string> DumpPendingRegrowth()
        {
            var lines = new List<string>();
            foreach (PendingRegrowth entry in _pendingRegrowth)
            {
                float secondsLeft = entry.RegrowAt - Time.time;
                lines.Add($"{entry.PrefabName} at {entry.Position} — " +
                          $"{(secondsLeft > 0 ? $"{secondsLeft:F0}s left" : "due")}, attempts {entry.Attempts}");
            }
            return lines;
        }

        /// <summary>
        /// Flushes queued burned-cell positions to the batched real-dirt painter
        /// once per PaintFlushInterval. On total failure (e.g. no TerrainComp in
        /// the zone yet) the positions are dropped rather than retried forever —
        /// the procedural decal already covered the visual, real paint is a bonus.
        /// </summary>
        private void FlushPendingPaint()
        {
            if (_pendingPaint.Count == 0) return;
            if (Time.time < _nextPaintFlush) return;
            _nextPaintFlush = Time.time + PaintFlushInterval;

            int painted = ValheimBridge.TryPaintScorchedDirtBatch(_pendingPaint, FireConfig.DirtPaintRadius.Value);
            if (painted < _pendingPaint.Count)
            {
                FireLogger.Debug($"Paint flush: {painted}/{_pendingPaint.Count} positions painted " +
                                  "(rest had no TerrainComp or threw; dropped, decal already covers them).");
            }
            _pendingPaint.Clear();
        }

        /// <summary>
        /// Server-side: packs accumulated ground-fire ignite/expire deltas into a
        /// ZPackage and broadcasts once per second. Only the server should ever
        /// have anything in these lists — TryIgniteGroundCell/ExpireGroundTimers
        /// only run as part of the server-only simulation loop — but the empty-
        /// check makes this a no-op on clients regardless.
        /// </summary>
        private void FlushGroundFireSync()
        {
            if (_groundIgnitedSinceFlush.Count == 0 && _groundExpiredSinceFlush.Count == 0) return;
            if (Time.time < _nextGroundSyncFlush) return;
            _nextGroundSyncFlush = Time.time + GroundSyncFlushInterval;

            var pkg = new ZPackage();
            pkg.Write(_groundIgnitedSinceFlush.Count);
            foreach ((GroundCellKey key, float y) in _groundIgnitedSinceFlush)
            {
                pkg.Write(key.X);
                pkg.Write(key.Z);
                pkg.Write(y);
            }
            pkg.Write(_groundExpiredSinceFlush.Count);
            foreach (GroundCellKey key in _groundExpiredSinceFlush)
            {
                pkg.Write(key.X);
                pkg.Write(key.Z);
            }

            ValheimBridge.BroadcastGroundFireSync(pkg);
            _groundIgnitedSinceFlush.Clear();
            _groundExpiredSinceFlush.Clear();
        }

        /// <summary>
        /// Client-side: unpacks a batched ground-fire delta and spawns/removes
        /// local, non-authoritative, no-damage ground VFX to match. Server no-ops
        /// (it already has the real ground VFX from its own simulation).
        /// </summary>
        private void HandleGroundFireSync(long sender, ZPackage pkg)
        {
            // Receipt trace BEFORE the server no-op return — kept permanently.
            // The stale-ZRoutedRpc registration bug burned an evening because
            // "no receipt logged" was indistinguishable from "receipt logged
            // nowhere": this line makes silent-drop diagnosable from one log.
            FireLogger.Debug($"[SYNC-DIAG] GroundFireSync arrived from {sender} (IsServer={ValheimBridge.IsServer()}).");
            if (ValheimBridge.IsServer()) return;

            int ignitedCount = pkg.ReadInt();
            int expiredCountPeek = 0; // logged after reading, just for the trace line below
            FireLogger.Debug($"[IGNITE-TRACE] HandleGroundFireSync received from peer {sender}: {ignitedCount} ignited entries.");
            for (int i = 0; i < ignitedCount; i++)
            {
                int x = pkg.ReadInt();
                int z = pkg.ReadInt();
                float y = pkg.ReadSingle();
                // Enqueue rather than spawn: the flush batches a second's worth
                // of new cells, and building that many procedural particle
                // systems in ONE frame was the client's other periodic frametime
                // spike. The queue drains a few per frame in Update instead.
                var key = new GroundCellKey(x, z);
                if (!_remoteGroundVfx.ContainsKey(key) && _remoteVfxQueuedKeys.Add(key))
                    _remoteVfxSpawnQueue.Add((key, CellCenter(key, y)));
            }

            int expiredCount = pkg.ReadInt();
            expiredCountPeek = expiredCount;
            FireLogger.Debug($"[IGNITE-TRACE] HandleGroundFireSync: {expiredCountPeek} expired entries. " +
                              $"_remoteGroundVfx now holds {_remoteGroundVfx.Count} entries.");
            for (int i = 0; i < expiredCount; i++)
            {
                int x = pkg.ReadInt();
                int z = pkg.ReadInt();
                var key = new GroundCellKey(x, z);
                _remoteVfxQueuedKeys.Remove(key); // expired before it ever spawned — drop it from the queue
                RemoveRemoteGroundVfxFor(key);
            }
        }

        // Pending remote ground VFX, drained a few per frame — see the enqueue
        // site in HandleGroundFireSync for why. Client-only in practice (the
        // server returns from that handler before enqueueing anything).
        private readonly List<(GroundCellKey key, Vector3 pos)> _remoteVfxSpawnQueue = new List<(GroundCellKey, Vector3)>();
        private readonly HashSet<GroundCellKey> _remoteVfxQueuedKeys = new HashSet<GroundCellKey>();
        private const int RemoteVfxSpawnsPerFrame = 3;

        private void DrainRemoteVfxSpawnQueue()
        {
            int spawned = 0;
            while (_remoteVfxSpawnQueue.Count > 0 && spawned < RemoteVfxSpawnsPerFrame)
            {
                (GroundCellKey key, Vector3 pos) = _remoteVfxSpawnQueue[_remoteVfxSpawnQueue.Count - 1];
                _remoteVfxSpawnQueue.RemoveAt(_remoteVfxSpawnQueue.Count - 1);
                if (!_remoteVfxQueuedKeys.Remove(key)) continue; // expired while queued
                SpawnRemoteGroundVfxFor(key, pos);
                spawned++;
            }
        }

        private void SpawnRemoteGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            if (_remoteGroundVfx.ContainsKey(key)) return;
            if (!FireConfig.UseProceduralVfx.Value)
            {
                FireLogger.Debug($"[IGNITE-TRACE] SpawnRemoteGroundVfxFor({key.X},{key.Z}): UseProceduralVfx is false on THIS peer, skipping.");
                return;
            }

            GameObject instance = ValheimBridge.CreateProceduralGroundFireVfx(position);
            if (instance != null)
            {
                _remoteGroundVfx[key] = instance;
            }
            else
            {
                FireLogger.Debug($"[IGNITE-TRACE] SpawnRemoteGroundVfxFor({key.X},{key.Z}): CreateProceduralGroundFireVfx returned null.");
            }
        }

        private void RemoveRemoteGroundVfxFor(GroundCellKey key)
        {
            if (_remoteGroundVfx.TryGetValue(key, out GameObject instance))
            {
                _remoteGroundVfx.Remove(key);
                if (instance != null) Destroy(instance);
            }
        }

        private void LeaveScorchMark(GroundCellKey key, float y)
        {
            Vector3 position = CellCenter(key, y);

            if (FireConfig.UseVanillaDirtPaint.Value && !_groundPainted.Contains(key))
            {
                // Queue for the next batched flush (see FlushPendingPaint) rather
                // than painting immediately: paints are grouped by zone TerrainComp
                // and committed with one Save() per comp per flush. Marked painted
                // now so a reigniting neighbor can't double-queue the same cell
                // before the flush fires. Deliberately falls through to the decal
                // below: it gives instant visual feedback and covers the case where
                // the batched paint later fails (no TerrainComp in zone) — and it
                // self-expires via ScorchMarkLifetimeSeconds, so there's no
                // double-visual buildup once the real paint lands.
                _pendingPaint.Add(position);
                _groundPainted.Add(key);
            }

            if (!FireConfig.EffectiveScorchMarksEnabled) return;
            float size = FireConfig.GroundCellSize.Value * 1.5f;
            ValheimBridge.SpawnScorchMark(position, size, FireConfig.ScorchMarkLifetimeSeconds.Value);
        }

        // ---------------------------------------------------------------
        // Spread — both directions, every cycle
        // ---------------------------------------------------------------

        private float _nextSpreadDiagnosticLog;
        private const float SpreadDiagnosticInterval = 5f;

        /// <summary>
        /// Periodic (not one-shot) visibility into whether structure-to-structure
        /// spread has any real candidates to work with at all. A one-time version
        /// of this caught WearNTear.AllPieces.Count=0 on the very first spread
        /// cycle after a force-created ignition — but a single reading can't
        /// distinguish "the vanilla list is broken here" from "it just hadn't
        /// been populated by Unity's Awake() yet on that exact frame." Logging
        /// every few seconds for as long as a fire burns answers that
        /// definitively: if the count stays 0 for the whole burn, that's a real
        /// platform-specific failure (same class as GetGroundHeight and
        /// GetCharacterLayerMask, both confirmed broken here already). If it
        /// becomes nonzero after the first reading or two, it was just a
        /// one-frame startup race, self-resolving and not worth chasing further.
        /// </summary>
        private void LogSpreadCandidateDiagnostic(float objRadiusSqr)
        {
            // Debug-only since 0.20.7: on a live dedicated server this wrote the same
            // counts every 5s for as long as anything burned (3,283 lines in one day,
            // twice the heartbeat). Toggle with 'fireset debug true' when spread stalls.
            if (!FireLogger.DebugEnabled) return;
            if (_burning.Count == 0) return;
            if (Time.time < _nextSpreadDiagnosticLog) return;
            _nextSpreadDiagnosticLog = Time.time + SpreadDiagnosticInterval;

            int pieceCount = ValheimBridge.AllPieces.Count;
            float nearestSqr = float.MaxValue;
            string nearestName = "<none>";
            int candidatesInRange = 0;

            foreach (BurningState burnerState in _burning.Values)
            {
                Vector3 origin = burnerState.Position;
                for (int i = 0; i < _candidates.Count; i++)
                {
                    Component candidate = _candidates[i];
                    if (candidate == null) continue;
                    float distSqr = (ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude;
                    if (distSqr <= objRadiusSqr) candidatesInRange++;
                    if (distSqr < nearestSqr)
                    {
                        nearestSqr = distSqr;
                        nearestName = ValheimBridge.NameOf(candidate);
                    }
                }
                break; // one burner's worth is enough to answer the question
            }

            float nearestDist = nearestSqr == float.MaxValue ? -1f : Mathf.Sqrt(nearestSqr);
            FireLogger.Info($"[SPREAD-DIAGNOSTIC] WearNTear.AllPieces.Count={pieceCount}, " +
                             $"total candidates (pieces+trees+logs)={_candidates.Count}, " +
                             $"zdoCandidates={_zdoCandidates.Count}, " +
                             $"candidates within SpreadRadius of first burner={candidatesInRange}, " +
                             $"nearest candidate='{nearestName}' at {nearestDist:F2}m " +
                             $"(SpreadRadius={FireConfig.EffectiveSpreadRadius}m). " +
                             "If candidatesInRange is 0 and nearestDist is well outside SpreadRadius, " +
                             "there simply wasn't a burnable piece close enough — not a bug. If AllPieces.Count " +
                             "looks wrong (0, or missing pieces you know are placed), that's the real lead.");
        }

        private void SpreadPass()
        {
            if (_burning.Count == 0 && _groundBurning.Count == 0) return;

            float ramp = GetRampFraction();
            float effectiveSpreadRadius = FireConfig.EffectiveSpreadRadius * ramp;
            float objRadiusSqr = effectiveSpreadRadius * effectiveSpreadRadius;
            float groundRadius = FireConfig.EffectiveGroundSpreadRadius * ramp;

            // Candidate picture is CACHED, not rebuilt per cycle. Rebuilding ran
            // three FindObjectsOfType scene scans plus the ZDO sector sweep every
            // 0.75s — and the sweep's radius follows the ground leash, so at
            // leash 150m it walked 7x7=49 zones per cycle. On a machine hosting
            // server and client together that burst was a visible periodic
            // frametime spike in play. Candidates are static trees and walls: a
            // few seconds of staleness is nothing at a front that moves meters
            // per minute. Stale entries are already tolerated downstream (null
            // Component checks; ComponentFromZdoid returns null for dead ZDOs).
            // A brand-new fire forces an immediate rebuild via TryIgnite.
            if (Time.time >= _nextCandidateRebuild)
            {
                _nextCandidateRebuild = Time.time + CandidateRebuildSeconds;
                BuildCandidateList();
                RebuildCandidateGrids();
            }
            LogSpreadCandidateDiagnostic(objRadiusSqr);

            // --- object burners: ignite nearby objects + seed nearby ground cells ---
            // Uses the cached Position from BurningState directly — no live
            // Component resolution needed for the burner side of this check at
            // all, since position never changes for a static piece/tree. This
            // also avoids force-creating a burner's GameObject every single
            // spread cycle just to read a position we already have cached.
            _scratch.Clear();
            _scratch.AddRange(_burning.Keys);
            // Spread maturity: a burning object passes fire on only after it has
            // burned SpreadMaturityFraction of its burn time. Without this, a
            // just-caught tree could torch its whole reach on the very next
            // 0.75s cycle while itself burning for minutes — the front raced
            // ahead at a pace totally disconnected from how long fuel takes to
            // burn. Gating neighbor ignition, ZDO candidates, AND ground
            // seeding ties front speed to burn duration: at defaults (240s x
            // 0.25) a tree becomes contagious ~60s into its burn. It still
            // burns, glows, and hurts from second one — it just isn't throwing
            // fire yet. Ground fire's own cell-to-cell crawl is untouched: that
            // channel is already paced, and it's how fire creeps INTO a stand.
            float maturitySeconds = FireConfig.BurnDurationSeconds.Value * FireConfig.EffectiveSpreadMaturityFraction;
            foreach (ZDOID burnerId in _scratch)
            {
                if (!_burning.TryGetValue(burnerId, out BurningState burnerState)) continue;
                if (Time.time - burnerState.IgnitedAt < maturitySeconds) continue;
                Vector3 origin = burnerState.Position;
                // Rain on the burner stops it passing fire on at all - objects, ZDO
                // candidates and ground seeds alike. It keeps burning (faster; see
                // AgeFiresInRain) and a direct ignition still lights it: rain stops
                // SPREAD, it does not stop a torch or a lightning strike.
                if (FireConfig.EffectiveRainSuppressesObjectFire && ValheimBridge.IsRainingAt(origin)) continue;

                // Grid query instead of the whole candidate list — see the
                // spatial index for the measurement that motivated it. The
                // cheap position check now runs FIRST too: it was previously
                // last, so every candidate in the world paid for an IsBurnable
                // dispatch and a ZDOIDOf lookup before being rejected on
                // distance.
                QueryGrid(_candidateGrid, origin, effectiveSpreadRadius);
                for (int q = 0; q < _gridQueryScratch.Count; q++)
                {
                    Component candidate = _candidates[_gridQueryScratch[q]];
                    if (candidate == null) continue;
                    if ((ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude > objRadiusSqr) continue;
                    if (!ValheimBridge.IsBurnable(candidate)) continue;

                    ZDOID? candidateId = ValheimBridge.ZDOIDOf(candidate);
                    if (!candidateId.HasValue) continue;
                    if (candidateId.Value.Equals(burnerId)) continue;
                    if (_burning.ContainsKey(candidateId.Value)) continue;

                    TryIgnite(candidate);
                }

                IgniteZdoCandidatesNear(origin, effectiveSpreadRadius, burnerState.EventId);
                IgniteGroundNear(origin, groundRadius);
            }

            // --- ground burners: ignite nearby ground cells + nearby objects ---
            if (_groundBurning.Count > 0)
            {
                _groundScratch.Clear();
                _groundScratch.AddRange(_groundBurning.Keys);
                foreach (GroundCellKey key in _groundScratch)
                {
                    float y = _groundBurning[key].Y;
                    Vector3 origin = CellCenter(key, y);

                    IgniteAdjacentGroundCells(key, y);

                    // Same rule for a ground cell lighting the objects above it.
                    if (FireConfig.EffectiveRainSuppressesObjectFire && ValheimBridge.IsRainingAt(origin)) continue;

                    QueryGrid(_candidateGrid, origin, groundRadius);
                    float groundRadiusSqr = groundRadius * groundRadius;
                    for (int q = 0; q < _gridQueryScratch.Count; q++)
                    {
                        Component candidate = _candidates[_gridQueryScratch[q]];
                        if (candidate == null) continue;
                        if ((ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude > groundRadiusSqr) continue;
                        if (!ValheimBridge.IsBurnable(candidate)) continue;

                        ZDOID? candidateId = ValheimBridge.ZDOIDOf(candidate);
                        if (!candidateId.HasValue) continue;
                        if (_burning.ContainsKey(candidateId.Value)) continue;

                        TryIgnite(candidate);
                    }

                    IgniteZdoCandidatesNear(origin, groundRadius, _groundBurning[key].EventId);
                }
            }
        }

        /// <summary>
        /// Rebuilds the full spread-candidate pool: all vanilla-tracked pieces,
        /// plus (if enabled) all currently-loaded trees and logs via a scene scan.
        /// The scan cost is bounded by what's actually instantiated nearby, but
        /// scales with SpreadCheckInterval — very low intervals with trees enabled
        /// in a dense forest is the case to watch if performance dips.
        /// </summary>
        private void BuildCandidateList()
        {
            _candidates.Clear();

            List<WearNTear> pieces = ValheimBridge.AllPieces;
            for (int i = 0; i < pieces.Count; i++) _candidates.Add(pieces[i]);

            // ZDO-layer candidates — see _zdoCandidates for why. ONE SWEEP PER
            // EVENT, unioned. This used to be a single sweep centred on
            // _fireOrigin, which meant every fire in the world had to live
            // inside one bounded circle around wherever the FIRST fire started:
            // light a fire, travel past that radius, light another, and the
            // second one found zero candidates and could not spread at all.
            // Each blaze now gets its own sweep around its own origin, sized to
            // its own front.
            _zdoCandidates.Clear();
            bool zdoScanRan = false;

            float reach = Mathf.Max(FireConfig.EffectiveSpreadRadius, FireConfig.EffectiveGroundSpreadRadius);
            float leash = FireConfig.EffectiveGroundMaxSpreadDistanceEnabled
                ? FireConfig.GroundMaxSpreadDistance.Value
                : 100f;

            _sweepCenters.Clear();
            foreach (FireEvent ev in _events.Values) _sweepCenters.Add((ev.Id, ev.Origin));
            if (_sweepCenters.Count == 0)
            {
                // No event yet (restored state, or a legacy save) — fall back to
                // any live fire position so a sweep still happens.
                foreach (BurningState state in _burning.Values) { _sweepCenters.Add((0, state.Position)); break; }
                if (_sweepCenters.Count == 0)
                {
                    foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                    {
                        _sweepCenters.Add((0, CellCenter(kv.Key, kv.Value.Y)));
                        break;
                    }
                }
            }

            for (int s = 0; s < _sweepCenters.Count; s++)
            {
                (int evId, Vector3 center) = _sweepCenters[s];

                // Sweep the fire it ACTUALLY is, not the fire it could eventually
                // become. The leash is a lifetime maximum: a fire five cells wide
                // got the same 150m+reach sweep as one that had burned for an
                // hour, which at 64m zones is 7x7=49 sectors walked every rebuild
                // regardless of size. Radius follows this event's live front plus
                // one full reach so next cycle's spread is always covered, still
                // capped at the leash figure so a sweep can never grow past what
                // it used to cost.
                float extent = 0f;
                foreach (BurningState state in _burning.Values)
                {
                    if (evId != 0 && state.EventId != evId) continue;
                    float d = (state.Position - center).sqrMagnitude;
                    if (d > extent) extent = d;
                }
                foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                {
                    if (evId != 0 && kv.Value.EventId != evId) continue;
                    float d = (CellCenter(kv.Key, kv.Value.Y) - center).sqrMagnitude;
                    if (d > extent) extent = d;
                }
                extent = Mathf.Sqrt(extent);

                float radius = Mathf.Min(extent + reach + 8f, leash + reach + 8f);
                if (ValheimBridge.CollectBurnableZdosNear(
                        center, radius,
                        FireConfig.BurnTreesAndLogs.Value, FireConfig.BurnPlayerBuildings.Value, _zdoSweepScratch))
                {
                    zdoScanRan = true;
                    for (int i = 0; i < _zdoSweepScratch.Count; i++) _zdoCandidates.Add(_zdoSweepScratch[i]);
                }
            }

            // Tree/log INSTANCE scans are a fallback, not the primary source.
            // FindObjectsOfType walks every loaded GameObject in the scene and
            // its cost rides the world's object count, not the fire's size — the
            // dominant term in this rebuild, and the one a tester with a forest
            // and a field of dropped wood pays hardest. The ZDO sweep above
            // already resolves trees and logs (EnsureBurnablePrefabKinds maps
            // both), and does it authoritatively, so these scans only earn their
            // keep on a peer where the ZDO layer could not be read at all.
            if (!zdoScanRan && FireConfig.BurnTreesAndLogs.Value)
            {
                TreeBase[] trees = Object.FindObjectsOfType<TreeBase>();
                for (int i = 0; i < trees.Length; i++) _candidates.Add(trees[i]);

                TreeLog[] logs = Object.FindObjectsOfType<TreeLog>();
                for (int i = 0; i < logs.Length; i++) _candidates.Add(logs[i]);
            }
        }

        /// <summary>
        /// ZDO-layer half of spread ignition: ignite any burnable ZDO within
        /// range of a burner, force-creating its GameObject only on an actual
        /// range hit — and only when there's room to burn or queue it, so a
        /// capped-out fire doesn't instantiate a forest it can't ignite yet.
        /// Anything already burning or queued is skipped before any instance
        /// is created; TryIgnite's own dedupe covers the rest (including an
        /// object the instance loop above already ignited this cycle).
        /// </summary>
        private void IgniteZdoCandidatesNear(Vector3 origin, float radius, int eventId)
        {
            if (_zdoCandidates.Count == 0) return;

            // Budget checked against the blaze this burner belongs to.
            int effectiveMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.EffectiveMaxConcurrentBurning * GetRampFraction(eventId)));
            int evBurning = BurningCountForEvent(eventId);
            float radiusSqr = radius * radius;

            QueryGrid(_zdoCandidateGrid, origin, radius);
            for (int q = 0; q < _gridQueryScratch.Count; q++)
            {
                if (evBurning >= effectiveMax && _queue.Count >= _queue.Capacity) return;

                ValheimBridge.ZdoBurnable candidate = _zdoCandidates[_gridQueryScratch[q]];
                if ((candidate.Position - origin).sqrMagnitude > radiusSqr) continue;
                if (_burning.ContainsKey(candidate.Id)) continue;
                if (_queue.Contains(candidate.Id)) continue;

                Component target = ValheimBridge.ComponentFromZdoid(candidate.Id);
                if (target == null) continue;
                TryIgnite(target);
            }
        }

        // Scratch for IgniteBurnablesNear only — _zdoCandidates is the spread
        // cycle's 5s cache and a console command must not clobber it.
        private readonly List<ValheimBridge.ZdoBurnable> _consoleZdoScratch = new List<ValheimBridge.ZdoBurnable>();

        /// <summary>
        /// Console-facing area ignite, reading the ZDO layer: on a headless
        /// server instance scans see nothing (the 0.17.4 lesson), so a relayed
        /// `startfire` that only scanned instances permanently reported 0
        /// targets. Returns how many ignitions were attempted; anything the
        /// caller's own instance pass already lit is skipped via _burning.
        /// </summary>
        public int IgniteBurnablesNear(Vector3 origin, float radius)
        {
            ValheimBridge.CollectBurnableZdosNear(
                origin, radius,
                FireConfig.BurnTreesAndLogs.Value, FireConfig.BurnPlayerBuildings.Value,
                _consoleZdoScratch);

            int attempted = 0;
            for (int i = 0; i < _consoleZdoScratch.Count; i++)
            {
                ValheimBridge.ZdoBurnable candidate = _consoleZdoScratch[i];
                if (_burning.ContainsKey(candidate.Id)) continue;
                if (_queue.Contains(candidate.Id)) continue;

                Component target = ValheimBridge.ComponentFromZdoid(candidate.Id);
                if (target == null) continue;
                TryIgnite(target);
                attempted++;
            }
            _consoleZdoScratch.Clear();
            return attempted;
        }
    }
}