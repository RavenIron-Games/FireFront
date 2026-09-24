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
        public bool YIsReal;  // false = Y is inherited from the igniter, not measured. See IsStandingInFire.
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
        // Keyed by ZDOID since 0.22.0, like _vfx: a tree the client de-instantiates and later
        // rebuilds is a DIFFERENT Component, and a Component key could neither find the fire
        // again nor be told to stop (the phantom-fire leak). The Component is resolved when
        // the rig is built and re-resolved when the object comes back (see OnBurnableInstantiated).
        private readonly Dictionary<ZDOID, GameObject> _remoteVfx = new Dictionary<ZDOID, GameObject>();
        // Every object fire this client has been told is alight and not yet told has stopped,
        // instance or no instance — so a burner that instantiates later still gets its fire.
        private readonly HashSet<ZDOID> _remoteBurningIds = new HashSet<ZDOID>();
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
        // Roughly two flushes per spread cycle at the 0.75s default, so a new cell reaches
        // every client within half a second of lighting and a dead one stops being drawn just
        // as fast. Was 1s, which combined with the cycle gate to make the real figure 1.5s.
        // A flush with nothing to say costs a count check, so a short interval is nearly free.
        private const float GroundSyncFlushInterval = 0.5f;

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

        // Cells handed to a painter at least once (UseVanillaDirtPaint) - the number `firestatus`
        // reports as paintedcells. A counter, not a gate: a cell that burns again after its fuel
        // regrows is handed out again, and repainting ground that is already dirt is a no-op that
        // costs one PaintCleared. Gating on it would make a cell whose painter could not reach it
        // unpaintable for the life of the session. Not cleared on fire-death; the dirt is not.
        private readonly HashSet<GroundCellKey> _groundPainted = new HashSet<GroundCellKey>();

        // Positions THIS machine has been told to paint, and the disc radius that came with them.
        // Fed by AssignPendingPaint when a listen host elects itself, by HandlePaintAssign on a
        // client. Flushed by FlushPendingPaint once a second, grouped by zone, one Save per zone.
        private readonly List<ValheimBridge.PaintJob> _pendingPaint = new List<ValheimBridge.PaintJob>();
        private float _pendingPaintRadius = 2f;
        // 0.23: the server sends at most one assignment per second per peer (PaintFlushInterval);
        // ten in five seconds is twice that, and anything past it is dropped with a logged count.
        private const int PaintAssignMaxPerWindow = 10;
        private const float PaintAssignWindowSeconds = 5f;
        private float _paintAssignWindowEnd;
        private int _paintAssignInWindow;
        // 0.23: how far from the server's last reference position for a peer an extinguish request
        // may act. The key acts at the player; a thrown bomb lands well inside this.
        private const float MaxExtinguishReach = 200f;
        private float _nextPaintFlush;
        private const float PaintFlushInterval = 1f;

        // Server side: burnt cells waiting to be handed to exactly ONE painter each, and which peer
        // was last elected for each zone (the guard against two peers creating a zone's compiler
        // in the same second). See AssignPendingPaint.
        private readonly List<(GroundCellKey key, float y)> _paintAssignPending = new List<(GroundCellKey, float)>();
        private readonly Dictionary<Vector2s, (long peer, float until)> _zonePainter = new Dictionary<Vector2s, (long, float)>();
        private readonly List<ValheimBridge.PaintCandidate> _paintCandidates = new List<ValheimBridge.PaintCandidate>();
        private readonly Dictionary<long, List<ValheimBridge.PaintJob>> _paintAssignByPeer = new Dictionary<long, List<ValheimBridge.PaintJob>>();
        private readonly Dictionary<Vector2s, List<(GroundCellKey key, float y)>> _paintAssignByZone = new Dictionary<Vector2s, List<(GroundCellKey, float)>>();
        private readonly List<Vector2s> _zonePainterSweep = new List<Vector2s>();
        private float _nextPaintAssign;
        private const float PaintAssignInterval = 1f;
        private const float PaintAssignStickySeconds = 15f;
        // "In reach" of a zone with a compiler: standing in it or in one next to it. Every
        // simulation-distance setting keeps that heightmap loaded; a metre radius does not (a
        // zone's diagonal is 89 m). CREATING a compiler needs more: ZNetScene.IsAreaReady looks
        // at the 3x3 around the zone, which at the lowest simulation distance is only certain to
        // be instantiated for the zone the painter stands in. So a pristine zone is reach 0.
        private const int PaintAssignZoneReach = 1;
        private const int PaintCreateZoneReach = 0;

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
            public long Sender;            // 1.0.2: the asking peer, for the reach check once the ZDO arrives
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
            int hits = 0, unclaimed = 0;
            for (int i = 0; i < _restoreScanScratch.Count; i++)
            {
                ValheimBridge.ZdoBurnable c = _restoreScanScratch[i];
                if (c.PrefabHash != wantHash) continue;
                if ((c.Position - at).sqrMagnitude > radiusSqr) continue;

                // Claimed candidates STILL count towards ambiguity. Skipping them outright would
                // let an earlier line's success make a later line look unambiguous when it is not:
                // two halves of one trunk, the first resolved and claimed, and the second - whose
                // own object a player felled offline - then sees a single free neighbour and lights
                // it. Counting both keeps "exactly one object could be meant" actually true.
                hits++;
                if (!_restoreClaimed.Contains(c.Id)) { found = c.Id; unclaimed++; }
                if (hits > 1) break;
            }
            if (hits == 1 && unclaimed == 1) return true;
            if (hits > 1)
                FireLogger.Debug($"[PERSIST] {hits}+ '{prefabName}' within {ObjRestoreRadius:F2}m of {at} — refusing to guess which was burning.");
            else if (hits == 0)
                FireLogger.Debug($"[PERSIST] no '{prefabName}' within {ObjRestoreRadius:F2}m of {at} — gone since the save, or moved further than the world file remembers.");
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
                ValheimBridge.RegisterFireRpcs(HandleIgniteRequest, HandleFireEventBroadcast, HandleGroundFireSync, HandleExtinguishRequest, HandleConfigSetRequest, HandleStatusRequest, HandleStatusResponse, HandleCommandRelay, HandleFireDamage, HandleGroundSyncRequest, HandlePaintAssign, HandleObjectFireSync, VersionCheck.OnVersion);

                // A fresh ZRoutedRpc means a fresh world, so everything this machine was drawing on
                // behalf of the old one is stale. FireManager lives on the plugin's own GameObject
                // and survives the scene change (Plugin.cs), but the VFX it spawned do NOT - Unity
                // destroys those with the scene and leaves the dictionary holding keys that point at
                // nothing. SpawnRemoteGroundVfxFor then refused those cells forever on ContainsKey,
                // so a player who logged out once and came back could never see fire in those cells
                // again for the rest of the process, and the dictionary grew every session.
                ResetRemoteMirror();
                // 1.0.2: and the simulation's own state. Normally already done by OnWorldShutdown,
                // which also saved it; this covers an exit that never passed ZNet.Shutdown. It
                // runs before the restore below, so the new world's store is what gets loaded.
                ResetWorldState();
                VersionCheck.ResetForNewConnection();
                _wantGroundSnapshot = true; // asked for below, once the connection can carry it
                _snapshotRequestsSent = 0;
                _objectSnapshotReceived = false;
            }

            if (FireConfig.ExtinguishKey.Value.IsDown())
            {
                TryPlayerExtinguish();
            }

            ApplyFireWarmth();

            // A config change made before the world loaded (the config manager at the main menu,
            // which is where people actually use it) has nowhere to go; this delivers it once the
            // connection and the admin list exist. No-op when there is nothing held.
            FireFront.Commands.FireDevCommands.FlushPendingConfigSync();

            // 0.24: this build's version to the server, same moment and same wait as the snapshot.
            VersionCheck.ClientTick();

            // The request cannot go out at registration time: the RPC instance exists several
            // seconds before there is a server peer to address. Same wait the config sync uses.
            if (_wantGroundSnapshot && ValheimBridge.CanReachServer())
            {
                _wantGroundSnapshot = false;
                _snapshotRequestsSent++;
                _nextSnapshotRetry = Time.time + SnapshotRetrySeconds;
                ValheimBridge.RequestGroundSnapshot();
                FireLogger.Debug($"[SYNC-DIAG] asked the server for the fire already burning (request {_snapshotRequestsSent}).");
            }
            else if (!_objectSnapshotReceived && _snapshotRequestsSent > 0 && _snapshotRequestsSent < SnapshotMaxRequests
                     && Time.time >= _nextSnapshotRetry && ValheimBridge.CanReachServer())
            {
                // The request or the reply can be lost in the first seconds of a connection (the
                // server answers only a connected peer, and "connected" lags the routed channel).
                // The server rate-limits per sender, so a repeat costs nothing when the first landed.
                _wantGroundSnapshot = true;
            }

            DrainRemoteVfxSpawnQueue(); // client-side VFX budget — must run before the server gate below
            DrainRemoteObjectVfxQueue(); // same, for object fire — see EnqueueRemoteObjectVfx
            ProcessRemoteSmouldering(); // client-side: the server is headless, THIS is what a player sees
            DrainPendingScorch(); // client-side: scorch decals whose zone has loaded since they were queued
            PruneOrphanedRemoteVfx();   // same: nothing else can reach a stranded effect
            FlushPendingPaint();        // same: real dirt is laid by the machine that has the terrain, and a
                                        // dedicated server has none where fires burn - see AssignPendingPaint.

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

            // 1.0.2: between ZNet.Shutdown and the scene change the old world still answers
            // IsServer, and running here would read its store straight back in (see OnWorldShutdown).
            if (ZNet.instance.HaveStopped) return;

            // 0.24: notice connected peers that never sent their version (FireFront 0.23 or older,
            // or none). Above the Enabled gate on purpose: a mismatch matters with fire off too.
            VersionCheck.ServerTick();

            // Restore persisted fire state exactly once, before the first cycle
            // ever runs — and gate all SAVES behind this flag too, or the empty
            // pre-restore state would clobber the store at every boot.
            if (!_persistenceRestored && ZNet.instance != null && ZDOMan.instance != null)
            {
                _persistenceRestored = true;
                if (FireConfig.PersistFiresEnabled.Value) RestorePersistedFires();
            }

            if (!FireConfig.Enabled.Value) return;

            // ABOVE the cycle gate, on its own clock. Below it, the damage interval would be a
            // floor rather than the rate: with SpreadCheckInterval at its 0.75s default a 1s tick
            // actually lands every 1.5s, and an admin who set the spread cycle to 10s would be
            // quietly turning fire damage down by a factor of ten as well.
            DamagePlayersInFire();

            // ALSO above the cycle gate, and for exactly the same reason. Below it the flush could
            // only ever fire on a spread-cycle boundary, so its own 1s interval quantized UP to the
            // next multiple of SpreadCheckInterval: at the 0.75s default that is a real cadence of
            // 1.5s, not the "roughly once a second" its own comment claimed. At the measured spread
            // rate that left about nine cells alight at the front that no client had been told about
            // and ten already dead that every client was still drawing - the drawn fire trailing the
            // real one by up to two spread steps. Set SpreadCheckInterval to its 10s maximum and the
            // visuals fell that far behind too. This is the second time the same gate has swallowed
            // a subsystem's own clock; check for a third before adding anything below it.
            FlushGroundFireSync();
            AssignPendingPaint();

            if (Time.time < _nextCycle) return;
            _nextCycle = Time.time + FireConfig.EffectiveSpreadCheckInterval;

            MaybePersistFires();
            PruneStale();
            AgeFiresInRain();
            ExpireTimers();
            TickTreeFire();
            PromoteFromQueue();
            ExpireGroundTimers();
            UpgradeDarkGroundCells(); // straight after the expiry that frees the slots
            ProcessTreeRegrowth();
            ProcessPendingIgniteResolutions();
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

        /// <summary>
        /// This machine is being told by the server that its own player is standing in fire.
        /// Applying it here is the point: burning is vanilla's status effect on a live Character,
        /// and this is the only machine that has one for this player.
        /// </summary>
        private void HandleFireDamage(long sender, float damage)
        {
            // A filter, not an authenticator, and the difference matters. ZRoutedRpc reads
            // m_senderPeerID straight off the wire (RoutedRPCData.Deserialize) and the server
            // re-serializes it verbatim when it relays - it never stamps the real sender - so a
            // modded client CAN forge this and address it to Everybody. It still stops ordinary
            // client-to-client traffic, which is worth keeping, but it cannot be the only guard.
            // Since 0.23 the server drops a FireFront package whose claimed sender is not the
            // connection it arrived on (ValheimBridge.ValidateRoutedSender), so the filter holds
            // wherever that guard is armed; the clamp below stays for the case it is not.
            if (!ValheimBridge.IsFromServer(sender)) return;

            // THIS is the guard that matters. Without it the RPC above is a one-packet server-wide
            // instakill: a forged 1e9 reaches SE_Burning as 1e9/ttl per hit, and a forged NaN is
            // worse - vanilla has no finite check, `NaN < 0.2f` is false so the sub-threshold
            // guard passes it, and `Mathf.Max(0f, NaN)` is NaN, so the burn pool never empties and
            // the player's health goes NaN and is SAVED to their .fch: a character that can be
            // neither healed nor killed. Clamped rather than rejected, deliberately - the ceiling
            // is the config's own maximum, not this client's setting, so a legitimate tick from a
            // server configured differently from this client still lands at full strength.
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return;
            ValheimBridge.ApplyFireDamageToLocalPlayer(Mathf.Min(damage, FireConfig.MaxFireDamagePerTick));
        }

        /// <summary>
        /// A client has just connected and is asking what is already alight. Everything else about
        /// ground fire is a delta, so without this a player who joins during a fire never learns
        /// about a single cell that lit before they arrived - it simply burns invisibly beside them
        /// for the rest of its life. This is the last place the client was not reconciled.
        ///
        /// Sent in the ordinary delta format with an empty expiry list, so it lands in the same
        /// client handler as everything else rather than in a second one that could disagree.
        /// </summary>
        private void HandleGroundSyncRequest(long sender)
        {
            if (!ValheimBridge.IsServer()) return;

            // Resolve the sender against the REAL peer list before anything else. It arrives off the
            // wire and can be forged, and two things went wrong when it was merely rate-limited:
            // a forged id of 0 is ZRoutedRpc's "Everybody", so one 40-byte request made the server
            // fan a full snapshot out to every player; and a fresh random id each time missed the
            // cooldown dictionary every time, so it both grew without bound and never throttled
            // anything. Neither is possible against a list of who is actually connected.
            if (!ValheimBridge.IsConnectedPeer(sender)) return;

            float now = Time.time;
            if (_lastSnapshotRequest.TryGetValue(sender, out float last) && now - last < SnapshotCooldownSeconds) return;
            _lastSnapshotRequest[sender] = now;

            var pkg = new ZPackage();
            pkg.Write(_groundBurning.Count);
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                pkg.Write(kv.Key.X);
                pkg.Write(kv.Key.Z);
                pkg.Write(kv.Value.Y);
            }
            pkg.Write(0); // no expiries in a snapshot: this IS the full set
            WriteGroundSyncTrailer(pkg);

            ValheimBridge.SendGroundFireSyncTo(sender, pkg);

            // The object fires too: ZDOID, how long each has burned (so the joiner's smoulder
            // clock and any age-driven look start where the server's are), and whether it has
            // already dropped to embers. ~17 bytes a fire; the burning cap keeps it to a few KB.
            var opkg = new ZPackage();
            opkg.Write(_burning.Count);
            float tnow = Time.time;
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                opkg.Write(kv.Key);
                opkg.Write(Mathf.Max(0f, tnow - kv.Value.IgnitedAt));
                opkg.Write(kv.Value.Smouldering);
            }
            ValheimBridge.SendObjectFireSyncTo(sender, opkg);
            FireLogger.Debug($"[SYNC-DIAG] sent a {_groundBurning.Count}-cell ground snapshot and a {_burning.Count}-object snapshot to peer {sender}.");

            // Peer ids are per-session, so without this the table gains one entry per connection for
            // the life of the process. Swept here rather than on a timer because this is the only
            // thing that writes to it.
            if (_lastSnapshotRequest.Count > 64)
            {
                _snapshotSweepScratch.Clear();
                foreach (KeyValuePair<long, float> kv in _lastSnapshotRequest)
                    if (now - kv.Value > 600f) _snapshotSweepScratch.Add(kv.Key);
                for (int i = 0; i < _snapshotSweepScratch.Count; i++) _lastSnapshotRequest.Remove(_snapshotSweepScratch[i]);
            }
        }

        private readonly List<long> _snapshotSweepScratch = new List<long>();
        private readonly Dictionary<long, float> _lastSnapshotRequest = new Dictionary<long, float>();
        private const float SnapshotCooldownSeconds = 5f;

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

            // 0.23: the server's own settings bound the request. The client sends the radius it
            // was configured with, and until 0.23 that was applied as sent; now the larger of the
            // server's two radii is the ceiling, so a client's copy of the key can only make its
            // own request smaller. The position is checked against where the server last saw the
            // sender (ZNetPeer.m_refPos, refreshed every 2 s): the key acts at the player and a
            // thrown bomb lands tens of metres away, so a request from across the map is refused,
            // not moved. A hosting player never comes through here (the caller extinguishes
            // directly on a server), so an unknown reference position means a peer the server has
            // not placed yet, and the request is allowed on the radius check alone.
            if (float.IsNaN(playerPos.x) || float.IsNaN(playerPos.y) || float.IsNaN(playerPos.z) ||
                float.IsInfinity(playerPos.x) || float.IsInfinity(playerPos.y) || float.IsInfinity(playerPos.z))
            {
                AuthLog.Refused(sender, "extinguish", "refused an extinguish request: the position is not finite");
                return;
            }
            Vector3? seenAt = ValheimBridge.PeerRefPosition(sender);
            if (seenAt.HasValue && (seenAt.Value - playerPos).sqrMagnitude > MaxExtinguishReach * MaxExtinguishReach)
            {
                AuthLog.Refused(sender, "extinguish-reach", $"refused an extinguish request at ({playerPos.x:F0},{playerPos.z:F0}): " +
                                        $"{Vector3.Distance(seenAt.Value, playerPos):F0} m from where the server last saw the player, limit {MaxExtinguishReach:F0} m");
                return;
            }
            float ceiling = Mathf.Max(FireConfig.ExtinguishGroundRadius.Value, FireConfig.DousingBombRadius.Value);
            if (float.IsNaN(groundRadius) || groundRadius < 0f) groundRadius = 0f;
            if (groundRadius > ceiling)
            {
                AuthLog.Refused(sender, "extinguish-radius", $"extinguish radius {groundRadius:F1} m clamped to the server's {ceiling:F1} m " +
                                        "(the larger of ExtinguishGroundRadius and DousingBombRadius here)");
                groundRadius = ceiling;
            }

            // 1.0.2: only a target that is actually burning, and near the player, and without
            // building anything: a burning object's state is all Extinguish needs. This used to
            // build and claim whatever id the client sent (any ZDO, any distance) and only then
            // ask whether it was burning, so even an honest press of the key while aiming at an
            // unlit piece took that piece away from the player standing next to it.
            if (!targetId.Equals(ZDOID.None) && _burning.TryGetValue(targetId, out BurningState burningTarget))
            {
                if (RequestTargetInReach(sender, burningTarget.Position, MaxExtinguishReach))
                    ExtinguishById(targetId);
                else
                    AuthLog.Refused(sender, "extinguish-target", $"refused to put out {targetId}: more than {MaxExtinguishReach:F0} m from where the server last saw the player");
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
        /// True when FireFront must start no fire at this point: FireInAshlands is off and
        /// the point is in the Ashlands. The test is the game's own (WorldGenerator.IsAshlands),
        /// pure maths on x and z, so a headless server answers it exactly as a client does.
        /// Vanilla's own Ashlands fire is untouched; this only stops FireFront catching from it.
        /// No Effective* accessor on purpose: WatchTheWorldBurn must not reopen the Ashlands.
        /// </summary>
        public static bool AshlandsBarsFireAt(Vector3 position) =>
            FireConfig.FireInAshlands != null && !FireConfig.FireInAshlands.Value &&
            WorldGenerator.IsAshlands(position.x, position.z);

        // The Ashlands' own fire touches wood there many times a second, so a refusal is
        // counted, not logged: one Info line at most per minute, and only while it happens.
        private const float AshlandsReportSeconds = 60f;
        private int _ashlandsRefused;
        private float _ashlandsNextReport;

        private void NoteAshlandsRefusal(string what, Vector3 at)
        {
            _ashlandsRefused++;
            FireLogger.Debug($"[ASHLANDS] refused {what} at ({at.x:F0}, {at.z:F0}): FireInAshlands is false.");
            if (Time.time < _ashlandsNextReport) return;
            FireLogger.Info($"[ASHLANDS] FireFront started no fire in the Ashlands: {_ashlandsRefused} " +
                            "ignition attempt(s) refused since the last report (FireInAshlands = false).");
            _ashlandsRefused = 0;
            _ashlandsNextReport = Time.time + AshlandsReportSeconds;
        }

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
            if (!FireConfig.Enabled.Value) return;
            if (!ValheimBridge.IsAlive(target)) return;
            if (!ValheimBridge.IsBurnable(target)) return;

            // Every object ignition passes here (the damage patches on the server, the ignite
            // RPC, spread, commands, the restore), so this one check covers them all.
            Vector3 targetPos = ValheimBridge.PositionOf(target);
            if (AshlandsBarsFireAt(targetPos)) { NoteAshlandsRefusal(ValheimBridge.NameOf(target), targetPos); return; }

            ZDOID? idOrNull = ValheimBridge.ZDOIDOf(target);
            if (!idOrNull.HasValue) return; // can't track what we can't identify
            ZDOID id = idOrNull.Value;

            if (_burning.ContainsKey(id)) return;
            if (_queue.Contains(id)) return;
            if (_dousedUntil.TryGetValue(id, out float wetUntil) && Time.time < wetUntil) return; // soaked — see _dousedUntil

            // The budget is PER EVENT now, not global — a blaze on the far side
            // of the map no longer starves this one. That global cap was the
            // tester's "the first big fire denies any other from existing".
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

        /// <summary>Extinguish(Component) by id alone, for a burner with no instance on this machine.</summary>
        private void ExtinguishById(ZDOID id)
        {
            if (_burning.TryGetValue(id, out BurningState st))
            {
                _burning.Remove(id);
                FireLogger.Debug($"Extinguished: {st.PrefabName}");
            }
            _queue.Remove(id);
            RemoveVfxFor(id);
            if (FireConfig.EffectiveDouseImmunitySeconds > 0f)
                _dousedUntil[id] = Time.time + FireConfig.EffectiveDouseImmunitySeconds;
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
            _groundVfxVisual.Clear();
            _groundVfxDamage.Clear();
            _groundVfxDark.Clear();
            _remoteVfxSpawnQueue.Clear();
            _remoteVfxQueuedKeys.Clear();
            _remoteObjectVfxQueue.Clear();
            _remoteObjectVfxQueued.Clear();
            _remoteBurningIds.Clear();
            _remoteVfxSpawnedAt.Clear();
            _remoteSmouldering.Clear();
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

        /// <summary>
        /// 1.0.2: the world is going away (ZNet.Shutdown, see Patches/WorldShutdownPatch). Saves
        /// this world's fire to ITS store while ZNet and ZDOMan still name it, then forgets every
        /// piece of simulation state. FireManager outlives the world (it sits on the plugin's
        /// GameObject), and before this a host who logged out and loaded another world carried the
        /// old fire into it: its ZDOIDs are load indices that name unrelated objects in the next
        /// world (ZDOID.m_loadID restarts at every load), so the old burners' timers destroyed
        /// whatever pieces held those indices, the old ground cells burned at their old
        /// coordinates, the old regrowth planted trees, and the next save overwrote the new
        /// world's store. A dedicated server only gets here on its way out, where the save is
        /// the same last-chance flush OnDestroy makes.
        /// </summary>
        public void OnWorldShutdown()
        {
            if (ValheimBridge.IsServer()) PersistFiresNow();
            ResetWorldState();
        }

        /// <summary>
        /// Drops all per-world simulation state without saving or telling anyone: the world it
        /// belonged to is gone or going. Called by OnWorldShutdown, and again when a new
        /// ZRoutedRpc appears (a new world), for any exit that did not pass ZNet.Shutdown.
        /// Clears _persistenceRestored so the NEXT world's store is read before anything saves.
        /// </summary>
        internal void ResetWorldState()
        {
            int n = _burning.Count + _queue.Count + _groundBurning.Count + _pendingRegrowth.Count;

            // The server-side effects. No RemoveVfxFor: that broadcasts a stop to the old world's peers.
            foreach (GameObject instance in _vfx.Values) { if (instance != null) Destroy(instance); }
            foreach (GameObject instance in _groundVfx.Values) { if (instance != null) Destroy(instance); }
            _vfx.Clear();
            _groundVfx.Clear();
            _groundVfxVisual.Clear();
            _groundVfxDamage.Clear();
            _groundVfxDark.Clear();

            _burning.Clear();
            _queue.Clear();
            _dousedUntil.Clear();
            _groundBurning.Clear();
            _groundExhausted.Clear();
            _groundIgnitedSinceFlush.Clear();
            _groundExpiredSinceFlush.Clear();
            _groundPainted.Clear();
            _paintAssignPending.Clear();
            _zonePainter.Clear();
            _firebreakCache.Clear();
            _pendingIgniteResolutions.Clear();
            _pendingRegrowth.Clear();
            _restoreClaimed.Clear();
            _events.Clear();
            _nextEventId = 1;
            _lastSnapshotRequest.Clear();

            // Candidate caches hold the old world's objects; rebuild on the next cycle.
            _candidates.Clear();
            _zdoCandidates.Clear();
            ClearGrid(_candidateGrid);
            ClearGrid(_zdoCandidateGrid);
            _nextCandidateRebuild = 0f;
            _builtScanValid = false;
            _restoreScanValid = false;

            // Clocks. -1 is each one's "not running" sentinel.
            _fireStartTime = -1f;
            _restoredRampAge = 0f;
            _restoringRampAge = 0f;
            _fireOrigin = null;
            _fireIgniterPlayerId = 0L;
            _nextSpreadDiagnosticLog = 0f;
            _nextTreeTick = -1f;
            _lastRainAgeTime = -1f;
            _nextCycle = 0f;
            _nextPersistSave = 0f;

            // The next world's store is read before anything is saved into it.
            _persistenceRestored = false;

            if (n > 0) FireLogger.Info($"[PERSIST] world closed: cleared {n} fire entries from memory (a new world loads its own store).");
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
            // 0.24: every number is written in the invariant culture (InvariantNumbers). Before,
            // StringBuilder.Append(float) used the machine's culture, so a store written on a
            // comma-decimal machine held "1,5", and a store moved between machines was misread.
            // The reader accepts both, so no format version bump is needed and nothing is lost.
            sb.Append("version\t").Append(InvariantNumbers.Format(FirePersistence.FormatVersion)).Append('\n');

            if (_fireStartTime >= 0f && _fireOrigin.HasValue)
            {
                Vector3 o = _fireOrigin.Value;
                sb.Append("meta\t").Append(InvariantNumbers.Format((now - _fireStartTime) + _restoredRampAge)).Append('\t')
                  .Append(InvariantNumbers.Format(o.x)).Append('\t').Append(InvariantNumbers.Format(o.y)).Append('\t').Append(InvariantNumbers.Format(o.z)).Append('\t')
                  .Append(InvariantNumbers.Format(_fireIgniterPlayerId)).Append('\n');
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
                sb.Append("obj\t").Append(InvariantNumbers.Format(kv.Key.UserID)).Append('\t').Append(InvariantNumbers.Format(kv.Key.ID)).Append('\t')
                  .Append(InvariantNumbers.Format(at.x)).Append('\t').Append(InvariantNumbers.Format(at.y)).Append('\t').Append(InvariantNumbers.Format(at.z)).Append('\t')
                  .Append(InvariantNumbers.Format(kv.Value.ExpireAt - now)).Append('\t')
                  .Append(InvariantNumbers.Format(now - kv.Value.IgnitedAt)).Append('\t')      // burn age, so restored burners keep their spread maturity
                  .Append(kv.Value.PrefabName).Append('\n');           // field 8, 0.21.6: half of what the entry is keyed on now.
                // Fields 1-2 still carry the ZDOID and are DIAGNOSTIC ONLY - a ZDOID is reassigned
                // on every world load. Kept so a store stays readable by an older build.
            }
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                sb.Append("ground\t").Append(InvariantNumbers.Format(kv.Key.X)).Append('\t').Append(InvariantNumbers.Format(kv.Key.Z)).Append('\t')
                  .Append(InvariantNumbers.Format(kv.Value.Y)).Append('\t').Append(InvariantNumbers.Format(kv.Value.ExpireAt - now)).Append('\n');
            }
            foreach (KeyValuePair<GroundCellKey, float> kv in _groundExhausted)
            {
                sb.Append("spent\t").Append(InvariantNumbers.Format(kv.Key.X)).Append('\t').Append(InvariantNumbers.Format(kv.Key.Z)).Append('\t')
                  .Append(InvariantNumbers.Format(kv.Value - now)).Append('\n');
            }
            foreach (PendingRegrowth entry in _pendingRegrowth)
            {
                sb.Append("regrow\t").Append(InvariantNumbers.Format(entry.Position.x)).Append('\t').Append(InvariantNumbers.Format(entry.Position.y)).Append('\t')
                  .Append(InvariantNumbers.Format(entry.Position.z)).Append('\t').Append(InvariantNumbers.Format(entry.RegrowAt - now)).Append('\t')
                  .Append(InvariantNumbers.Format(entry.Attempts)).Append('\t').Append(entry.PrefabName).Append('\n');
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
                            if (InvariantNumbers.ParseInt(f[1]) != FirePersistence.FormatVersion)
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
                            _restoredRampAge = InvariantNumbers.ParseFloat(f[1]);
                            // The store writes version, meta, obj, ground, spent, regrow in that
                            // order, so this lands before the first re-ignition creates an event.
                            _restoringRampAge = _restoredRampAge;
                            _fireOrigin = new Vector3(InvariantNumbers.ParseFloat(f[2]), InvariantNumbers.ParseFloat(f[3]), InvariantNumbers.ParseFloat(f[4]));
                            _fireIgniterPlayerId = InvariantNumbers.ParseLong(f[5]);
                            break;

                        case "ground":
                        {
                            float remaining = InvariantNumbers.ParseFloat(f[4]);
                            if (remaining <= 0f) { skipped++; break; }
                            var key = new GroundCellKey(InvariantNumbers.ParseInt(f[1]), InvariantNumbers.ParseInt(f[2]));
                            float y = InvariantNumbers.ParseFloat(f[3]);
                            // A save written by 1.0.0 can hold Ashlands fire; it is dropped, not restored.
                            if (AshlandsBarsFireAt(CellCenter(key, y))) { skipped++; break; }
                            _groundBurning[key] = new GroundCellState { ExpireAt = now + remaining, Y = y, YIsReal = true };
                            _groundIgnitedSinceFlush.Add((key, y)); // clients learn of it at the next flush
                            // Queued rather than built here: the restore installs the whole fire at
                            // once, and building fifty ground objects in one frame is the boot spike
                            // this mod has already fixed twice. UpgradeDarkGroundCells drains it a
                            // few per cycle. Without this, restored cells had no object at all - so
                            // on a listen host after a restart, creatures walked through the fire
                            // untouched, silently, because nothing ever revisited a restored cell.
                            _groundVfxDark.Add(key);
                            ground++;
                            break;
                        }

                        case "spent":
                        {
                            float remaining = InvariantNumbers.ParseFloat(f[3]);
                            if (remaining <= 0f) { skipped++; break; }
                            _groundExhausted[new GroundCellKey(InvariantNumbers.ParseInt(f[1]), InvariantNumbers.ParseInt(f[2]))] = now + remaining;
                            spent++;
                            break;
                        }

                        case "obj":
                        {
                            float remaining = InvariantNumbers.ParseFloat(f[6]);
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

                            var at = new Vector3(InvariantNumbers.ParseFloat(f[3]), InvariantNumbers.ParseFloat(f[4]), InvariantNumbers.ParseFloat(f[5]));
                            if (AshlandsBarsFireAt(at)) { skipped++; break; } // TryIgnite would refuse it; skip the lookup
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
                                    ? now - InvariantNumbers.ParseFloat(f[7])
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
                                Position = new Vector3(InvariantNumbers.ParseFloat(f[1]), InvariantNumbers.ParseFloat(f[2]), InvariantNumbers.ParseFloat(f[3])),
                                RegrowAt = now + Mathf.Max(1f, InvariantNumbers.ParseFloat(f[4])),
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
            // Adopt HERE rather than leaving it to the Update that called us. Ground cells restore
            // with EventId 0 and only get an event from adoption, so the blaze age has to still be
            // armed when that runs - but leaving it armed across the rest of Update meant an early
            // return (fire disabled, say) could strand it, and the next unrelated ignition would be
            // born at full ramp. Doing both in one place removes the window entirely.
            AdoptOrphanedFires();
            _restoringRampAge = 0f;

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
                   $"ground {_groundBurning.Count}/{FireConfig.EffectiveGroundMaxConcurrent * fireCount} (enabled {FireConfig.GroundSpreadEnabled.Value}, vfxcap {FireConfig.EffectiveGroundVfxMaxConcurrent} [lit {_groundVfxVisual.Count}, waiting {_groundVfxDark.Count}], dmgcap {FireConfig.EffectiveGroundDamageMaxConcurrent}, raining {RainingBurnersForStatus()}), " +
                   $"burn {FireConfig.BurnDurationSeconds.Value}s (maturity {(FireConfig.EffectiveSpreadMaturityFraction * 100f):F0}%), " +
                   $"radius {FireConfig.EffectiveSpreadRadius}m, " +
                   $"groundradius {FireConfig.EffectiveGroundSpreadRadius}m, " +
                   $"interval {FireConfig.EffectiveSpreadCheckInterval}s, " +
                   $"lowspec {FireConfig.LowSpecPreset.Value}, burntheworld {FireConfig.ApocalypseActive}, " +
                   $"fires {_events.Count}, " +
                   $"trees {FireConfig.BurnTreesAndLogs.Value}, " +
                   $"burnbuildings {FireConfig.BurnPlayerBuildings.Value}, ashlands {FireConfig.FireInAshlands.Value}, " +
                   $"vfx '{FireConfig.VfxPrefabName.Value}', procedural {FireConfig.UseProceduralVfx.Value}, " +
                   $"treeflames {FireConfig.TreeFlameScaling.Value} (max {FireConfig.EffectiveMaxFlameHeight}m, tallcap {FireConfig.EffectiveTallFireMaxConcurrent}, sparks {FireConfig.EffectiveCrownSparksEnabled}), " +
                   $"hurts {FireConfig.FireHurtsEnabled.Value} (playerOnly {FireConfig.FireHurtsPlayerOnly.Value}, {FireConfig.FireDamagePerTick.Value}dmg/{FireConfig.FireDamageTickInterval.Value}s), " +
                   $"dirtpaint {FireConfig.UseVanillaDirtPaint.Value}, " +
                   $"exhaustion {FireConfig.EffectiveGroundFuelExhaustionEnabled} (regrow {FireConfig.GroundFuelRegrowSeconds.Value}s), " +
                   $"treeregrowth {FireConfig.EffectiveTreeRegrowthEnabled} (after {FireConfig.TreeRegrowthSeconds.Value}s, pending {_pendingRegrowth.Count}), " +
                   $"treefire {FireConfig.TreeFireDamageEnabled.Value} (tick {FireConfig.TreeFireTickInterval.Value}s, kill at {(FireConfig.TreeFireKillFraction.Value * 100f):F0}%), " +
                   $"charred (collapse {FireConfig.TreeDestructionRate.Value:F0}% after {FireConfig.CharredCollapseDelaySeconds.Value}s, coal {FireConfig.CharredCoalMin.Value}-{FireConfig.CharredCoalMax.Value}, " +
                   $"health {(FireConfig.CharredTreeHealthFraction.Value * 100f):F0}%, crumble {FireConfig.CharredLogCrumbleSeconds.Value}s, glow {FireConfig.CharredEmberGlowSeconds.Value}s x{FireConfig.CharredEmberIntensity.Value:F2} cover {FireConfig.CharredEmberCoverage.Value:F2}, smoke {FireConfig.EffectiveCharredSmokeEnabled} {FireConfig.CharredSmokeSeconds.Value}s, retired masks {CharredTextures.RetiredMaskCount}; charred {_treesCharredCount}, collapsed {_treesCollapsedCount}), " +
                   $"firelook (shadows {FireConfig.EffectiveFireShadowsEnabled}, haze {FireConfig.EffectiveHeatHazeEnabled}, barkchar {FireConfig.EffectiveBarkCharEnabled}), " +
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

            // Every ground ignition passes here (seeding round a burning object, cell-to-cell
            // spread, firegroundignite); the restore is gated separately.
            if (AshlandsBarsFireAt(approxCenter)) { NoteAshlandsRefusal($"ground cell ({key.X},{key.Z})", approxCenter); return; }

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
            float realY = ValheimBridge.GetGroundHeight(approxCenter, out bool realYSampled);

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

            _groundBurning[key] = new GroundCellState { ExpireAt = Time.time + duration, Y = realY, YIsReal = realYSampled, EventId = cellEventId };
            FireLogger.Debug($"Ground ignited ({_groundBurning.Count}/{effectiveGroundMax}) at cell ({key.X},{key.Z})");
            SpawnGroundVfxFor(key, CellCenter(key, realY));
            _groundIgnitedSinceFlush.Add((key, realY));
        }

        private void SpawnGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            if (_groundVfx.ContainsKey(key)) return;

            // PlayerOnly now means there is nothing for a damage zone to DO: FireBurnZone
            // returns at the top of Update in that mode, because players are found from their
            // ZDO positions server-side instead. Spawning them anyway cost a GameObject, a
            // Collider[16] and a no-op Update dispatch per burning cell - up to 2000 of them
            // on a headless server during a big fire, every frame, for nothing.
            // ...and on a dedicated server there is nothing for it to FIND either. FireBurnZone
            // polls Physics.OverlapSphere, and a headless server has no colliders where players or
            // creatures are - only ZDOs. 0.21.8 measured it resolving a Character exactly zero
            // times. Building the zone anyway cost a GameObject, a Collider[16] and a poll every
            // 0.25s per burning cell and per burning object, for the life of every fire, for
            // nothing. Creatures still burn wherever physics is real: single-player, and a listen
            // host's own view. Players are unaffected either way - they have not come through this
            // path since 0.21.8.
            bool wantDamage = FireConfig.FireHurtsEnabled.Value
                              && !FireConfig.FireHurtsPlayerOnly.Value
                              && !ValheimBridge.IsDedicatedServer();
            // A dedicated server has no camera and nobody to show a particle to, yet it built one
            // per burning cell and per burning object and then simulated them all. Clients render
            // for themselves from the sync deltas; this machine was rendering to nothing. Only the
            // two SERVER-side spawn paths are gated - SpawnRemoteVfxOnly is the client's own and
            // this test is false there anyway.
            bool wantVisual = FireConfig.UseProceduralVfx.Value && !ValheimBridge.IsDedicatedServer();
            if (!wantVisual && !wantDamage) return;

            // Overall cap on tracked ground effect objects (visual and/or damage-only
            // combined) — bounded by the higher of the two per-purpose caps, since a
            // damage-only object is cheap (just a polling FireBurnZone, no particles).
            int overallCap = Mathf.Max(FireConfig.EffectiveGroundVfxMaxConcurrent, FireConfig.EffectiveGroundDamageMaxConcurrent);
            if (_groundVfx.Count >= overallCap)
            {
                if (wantVisual) _groundVfxDark.Add(key);
                return;
            }

            // Visual has its OWN sub-cap, checked independently — this used to share
            // GroundVfxMaxConcurrent with damage entirely, meaning only ~30 of up to
            // 200 burning cells ever got a damage zone at all (objects have their own
            // separate, much higher cap and worked fine — that's why fire only hurt
            // near trees/pieces, never out in open ground).
            // Counted from the sets rather than by walking every live object and calling
            // GetComponent twice: this runs on EVERY ground ignition, so the old form was O(cells)
            // per ignition - quadratic across a spreading fire, and two GetComponent calls deep.
            if (wantVisual && _groundVfxVisual.Count >= FireConfig.EffectiveGroundVfxMaxConcurrent)
            {
                // Remembered, not discarded. Before 0.21.9 losing this race meant burning invisibly
                // for the cell's whole life, because nothing ever revisited it: SpawnGroundVfxFor is
                // called once, at ignition, and returns early ever after on ContainsKey. So a fire
                // sat permanently pockmarked with cells that damaged you and showed nothing.
                // UpgradeDarkGroundCells drains this as soon as another cell finishes.
                _groundVfxDark.Add(key);
                wantVisual = false;
            }

            // Damage gets its own independent check against its own (much higher) cap.
            if (wantDamage && _groundVfxDamage.Count >= FireConfig.EffectiveGroundDamageMaxConcurrent)
                wantDamage = false;

            if (!wantVisual && !wantDamage) return;

            GameObject instance = wantVisual
                ? ValheimBridge.CreateProceduralGroundFireVfx(position)
                : new GameObject("FireFrontGroundDamageZone");
            if (!wantVisual) instance.transform.position = position;

            if (wantDamage)
            {
                ValheimBridge.AttachFireDamageZone(instance, FireConfig.GroundCellSize.Value * 0.5f,
                    FireConfig.FireHurtsPlayerOnly.Value, FireConfig.FireDamagePerTick.Value, FireConfig.FireDamageTickInterval.Value);
                _groundVfxDamage.Add(key);
            }

            if (wantVisual) { _groundVfxVisual.Add(key); _groundVfxDark.Remove(key); }
            _groundVfx[key] = instance;
        }

        // What each tracked ground object actually IS. Kept alongside _groundVfx so the caps can be
        // checked in O(1); every mutation of _groundVfx must keep these three in step, which is why
        // the only paths that touch it are SpawnGroundVfxFor, RemoveGroundVfxFor and the clear-all.
        private readonly HashSet<GroundCellKey> _groundVfxVisual = new HashSet<GroundCellKey>();
        private readonly HashSet<GroundCellKey> _groundVfxDamage = new HashSet<GroundCellKey>();

        // Cells that are burning and WANT a particle visual but could not have one when they lit.
        private readonly HashSet<GroundCellKey> _groundVfxDark = new HashSet<GroundCellKey>();
        private readonly List<GroundCellKey> _groundVfxUpgradeScratch = new List<GroundCellKey>();
        private const int MaxGroundVfxUpgradesPerCycle = 5;

        /// <summary>How many burning cells are waiting for a visual, for <c>firestatus</c>.</summary>
        public int GroundDarkCount => _groundVfxDark.Count;

        /// <summary>
        /// Gives a visual to cells that were refused one, as soon as headroom frees. Without this the
        /// visual cap is decided by a race at ignition and never revisited, so a long fire ends up
        /// permanently patchy: the cells that happened to light while the cap was full stay dark for
        /// their whole burn even after half the fire has gone out. That is the reported symptom -
        /// "the visual spread is different to the cells burning" - and raising the cap alone would
        /// only move the threshold, not fix the shape.
        ///
        /// Bounded per cycle: building particle systems is the expensive part, and doing a hundred in
        /// one frame is the frametime spike this mod has fixed twice already.
        /// </summary>
        private void UpgradeDarkGroundCells()
        {
            if (_groundVfxDark.Count == 0) return;

            _groundVfxUpgradeScratch.Clear();
            _groundVfxUpgradeScratch.AddRange(_groundVfxDark);

            int budget = MaxGroundVfxUpgradesPerCycle;
            for (int i = 0; i < _groundVfxUpgradeScratch.Count; i++)
            {
                if (budget <= 0) return;

                GroundCellKey key = _groundVfxUpgradeScratch[i];

                // Only touch a cell if this pass can actually change something for it, or a cell
                // waiting on a full visual budget would be torn down and rebuilt identically every
                // cycle for its whole life.
                bool visualHeadroom = _groundVfxVisual.Count < FireConfig.EffectiveGroundVfxMaxConcurrent;
                bool hasObject = _groundVfx.ContainsKey(key);
                if (hasObject && !visualHeadroom) continue;
                if (!hasObject && !visualHeadroom &&
                    _groundVfxDamage.Count >= FireConfig.EffectiveGroundDamageMaxConcurrent) continue;

                // Self-healing, deliberately. A cell that burned out while dark may never have had an
                // object for RemoveGroundVfxFor to remove, so this set cannot rely on that path alone
                // and instead drops anything no longer burning. Cheap, and it cannot leak.
                if (!_groundBurning.TryGetValue(key, out GroundCellState cell)) { _groundVfxDark.Remove(key); continue; }

                // Replace the damage-only placeholder, if it got one, rather than leaving an object
                // behind. SpawnGroundVfxFor re-attaches the damage zone from current config.
                if (_groundVfx.TryGetValue(key, out GameObject old))
                {
                    if (old != null) Destroy(old);
                    _groundVfx.Remove(key);
                    _groundVfxVisual.Remove(key);
                    _groundVfxDamage.Remove(key);
                }

                _groundVfxDark.Remove(key);
                SpawnGroundVfxFor(key, CellCenter(key, cell.Y));
                budget--;
            }
        }

        private void RemoveGroundVfxFor(GroundCellKey key)
        {
            _groundVfxVisual.Remove(key);
            _groundVfxDamage.Remove(key);
            _groundVfxDark.Remove(key);
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

            // A dedicated server has no camera and nobody to show a particle to, yet it built one
            // per burning cell and per burning object and then simulated them all. Clients render
            // for themselves from the sync deltas; this machine was rendering to nothing. Only the
            // two SERVER-side spawn paths are gated - SpawnRemoteVfxOnly is the client's own and
            // this test is false there anyway.
            bool wantVisual = (FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value))
                              && !ValheimBridge.IsDedicatedServer();
            // PlayerOnly now means there is nothing for a damage zone to DO: FireBurnZone
            // returns at the top of Update in that mode, because players are found from their
            // ZDO positions server-side instead. Spawning them anyway cost a GameObject, a
            // Collider[16] and a no-op Update dispatch per burning cell - up to 2000 of them
            // on a headless server during a big fire, every frame, for nothing.
            // ...and on a dedicated server there is nothing for it to FIND either. FireBurnZone
            // polls Physics.OverlapSphere, and a headless server has no colliders where players or
            // creatures are - only ZDOs. 0.21.8 measured it resolving a Character exactly zero
            // times. Building the zone anyway cost a GameObject, a Collider[16] and a poll every
            // 0.25s per burning cell and per burning object, for the life of every fire, for
            // nothing. Creatures still burn wherever physics is real: single-player, and a listen
            // host's own view. Players are unaffected either way - they have not come through this
            // path since 0.21.8.
            bool wantDamage = FireConfig.FireHurtsEnabled.Value
                              && !FireConfig.FireHurtsPlayerOnly.Value
                              && !ValheimBridge.IsDedicatedServer();
            if (!wantVisual && !wantDamage) return;

            GameObject instance = null;
            // wantVisual, NOT the config directly: the config says what the admin asked for, wantVisual
            // says what this machine should actually build. Testing the config here made the headless
            // gate above dead code whenever damage was also wanted - the server skipped it and built
            // the particles anyway.
            if (wantVisual && FireConfig.UseProceduralVfx.Value)
            {
                instance = new GameObject("FireFrontProceduralVFX");
                instance.transform.position = position;
                
                float h = ValheimBridge.MeasureBurnerHeight(target);
                float r = ValheimBridge.MeasureBurnerCrownRadius(target);
                if (h <= 0.1f) h = 2f; // Fallback
                if (r <= 0.1f) r = 0.5f;

                Bounds b = new Bounds(position + Vector3.up * (h / 2f), new Vector3(r * 2, h, r * 2));
                float duration = FireConfig.BurnDurationSeconds.Value;
                if (_burning.TryGetValue(id, out var state))
                {
                    duration = state.ExpireAt - state.IgnitedAt;
                }

                var vfxController = instance.AddComponent<FireVFXController>();
                vfxController.Setup(b, duration, target, id);
            }
            else if (wantVisual && !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value))
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
            // 1.0.2: the id comes off the wire, and ComponentFromZdoid builds an instance of it
            // headless and takes its ownership. Before this a modified client could name ANY ZDO,
            // another player's character included, and the server built it, claimed it and then
            // deleted it as out of its area. So the ZDO is read first, building nothing: it must
            // be something fire can burn and lie near the player who asked.
            // Debug, not an [AUTH] line: an honest client forwards every fire hit on any piece,
            // stone walls included, so an unburnable target is ordinary traffic.
            if (!ValheimBridge.ZdoExists(id))
            {
                // Not here YET is not the same as not burnable: a client's fresh object (a log it
                // just felled into a fire) can be hit before its ZDO reaches the server. Retried as
                // before 1.0.2, and each retry runs the same checks once the ZDO has arrived.
                QueueIgniteRetry(sender, id, igniterPlayerId);
                return;
            }
            if (!ValheimBridge.TryGetBurnableZdo(id, out Vector3 requestedAt))
            {
                FireLogger.Debug($"[IGNITE-TRACE] ignite request from peer {sender} for {id}: not a live tree, log or burnable piece — ignored.");
                return;
            }
            if (!RequestTargetInReach(sender, requestedAt, MaxIgniteReach))
            {
                AuthLog.Refused(sender, "ignite-reach", $"refused an ignite request for {id} at ({requestedAt.x:F0},{requestedAt.z:F0}): " +
                                        $"more than {MaxIgniteReach:F0} m from where the server last saw the player");
                return;
            }
            // Nothing to do for a target already alight, queued or soaked, so nothing is built for it.
            if (_burning.ContainsKey(id) || _queue.Contains(id) || IsSoaked(id)) return;
            // Refused by the ZDO's own position, before ComponentFromZdoid can build an
            // instance headless and claim it, for a target TryIgnite would refuse anyway.
            if (AshlandsBarsFireAt(requestedAt))
            {
                NoteAshlandsRefusal($"ZDOID={id} (asked by peer {sender})", requestedAt);
                return;
            }
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
                QueueIgniteRetry(sender, id, igniterPlayerId);
            }
        }

        private void QueueIgniteRetry(long sender, ZDOID id, long igniterPlayerId)
        {
            if (_pendingIgniteResolutions.Count >= IgniteResolutionMaxPending) return; // a flood of unknown ids stays bounded
            _pendingIgniteResolutions.Add(new PendingIgniteResolution
            {
                Id = id,
                Sender = sender,
                RetryAt = Time.time + IgniteResolutionRetryInterval,
                Attempts = 0,
                IgniterPlayerId = igniterPlayerId
            });
        }

        /// <summary>
        /// The ignite request's checks, for a retry whose ZDO has now arrived: false drops the
        /// retry for good (not burnable, out of reach, or nothing left to do). No ZDO yet is true.
        /// </summary>
        private bool IgniteRetryStillValid(PendingIgniteResolution entry)
        {
            if (!ValheimBridge.ZdoExists(entry.Id)) return true;
            if (!ValheimBridge.TryGetBurnableZdo(entry.Id, out Vector3 at)) return false;
            if (!RequestTargetInReach(entry.Sender, at, MaxIgniteReach)) return false;
            if (_burning.ContainsKey(entry.Id) || _queue.Contains(entry.Id) || IsSoaked(entry.Id)) return false;
            return !AshlandsBarsFireAt(at);
        }

        // 1.0.2: how far from the server's last reference position for a peer (ZNetPeer.m_refPos,
        // refreshed every 2 s) an ignite request's target may lie. A client asks only for objects
        // it owns, which sit in the zones around it; five zones is wider than any of that.
        private const float MaxIgniteReach = 320f;

        /// <summary>
        /// True when <paramref name="target"/> is within <paramref name="reach"/> of where the server
        /// last saw <paramref name="sender"/>, on the ground plane. A peer the server has not placed
        /// yet passes, the same rule the extinguish position check uses.
        /// </summary>
        private static bool RequestTargetInReach(long sender, Vector3 target, float reach)
        {
            Vector3? seenAt = ValheimBridge.PeerRefPosition(sender);
            return !seenAt.HasValue || FireMath.WithinReach(seenAt.Value.x, seenAt.Value.z, target.x, target.z, reach);
        }

        private const float IgniteResolutionRetryInterval = 0.5f;
        private const int IgniteResolutionMaxAttempts = 20; // ~10 seconds total before giving up
        private const int IgniteResolutionMaxPending = 256;

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
                if (!IgniteRetryStillValid(entry)) { _igniteResolutionScratchIndices.Add(i); continue; }

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
        private readonly Dictionary<ZDOID, float> _remoteVfxSpawnedAt = new Dictionary<ZDOID, float>();
        private readonly HashSet<ZDOID> _remoteSmouldering = new HashSet<ZDOID>();
        private readonly List<ZDOID> _remoteSmoulderScratch = new List<ZDOID>();
        private float _nextRemoteSmoulderCheck;

        /// <summary>
        /// Client-side half of the smouldering downgrade, and the half that
        /// actually saves frames: the server is headless, so its own _vfx render
        /// nothing — what a player SEES is _remoteVfx, spawned here from
        /// FireEvent broadcasts. Each client runs this on its own clock from
        /// when it started showing a given fire, so no extra sync is needed.
        /// Cosmetic only, and throttled: nothing here is worth a per-frame pass.
        /// </summary>
        /// <summary>
        /// Destroys remote fire whose burner is gone, for the paths that never deliver a stop at
        /// all - a peer that was out of range when the fire ended, a world object removed by
        /// something other than fire, a dropped packet. The map above fixes the common case; this
        /// is what makes an orphan impossible rather than merely unlikely, because the failure is
        /// silent and permanent and a player just sees fire that will not go out.
        /// </summary>
        private void PruneOrphanedRemoteVfx()
        {
            if (_remoteVfx.Count == 0) return;
            if (Time.time < _nextRemoteVfxPrune) return;
            _nextRemoteVfxPrune = Time.time + 5f;

            // 0.22.0: keyed by ZDOID, so "the burner is gone" is "its ZDO no longer exists on
            // this peer" - true for a tree that burned down and was charred (its ZDO is
            // destroyed and a new one created), for a world object removed by anything else,
            // and for a fire whose stop event this peer never received.
            _orphanScratch.Clear();
            foreach (KeyValuePair<ZDOID, GameObject> kv in _remoteVfx)
            {
                if (kv.Value == null || !ValheimBridge.ZdoExists(kv.Key)) _orphanScratch.Add(kv.Key);
            }

            for (int i = 0; i < _orphanScratch.Count; i++)
            {
                _remoteBurningIds.Remove(_orphanScratch[i]);
                RemoveRemoteVfxFor(_orphanScratch[i]);
            }

            if (_orphanScratch.Count > 0)
                FireLogger.Debug($"[VFX] cleaned up {_orphanScratch.Count} stranded fire effect(s) whose burner is gone.");
        }

        private float _nextRemoteVfxPrune;
        private readonly List<ZDOID> _orphanScratch = new List<ZDOID>();

        private void ProcessRemoteSmouldering()
        {
            if (!FireConfig.SmoulderingVfxEnabled.Value) return;
            if (_remoteVfx.Count == 0) return;
            if (Time.time < _nextRemoteSmoulderCheck) return;
            _nextRemoteSmoulderCheck = Time.time + 2f;

            float after = FireConfig.BurnDurationSeconds.Value * Mathf.Clamp01(FireConfig.SmoulderAfterFraction.Value);
            float now = Time.time;

            _remoteSmoulderScratch.Clear();
            foreach (KeyValuePair<ZDOID, float> kv in _remoteVfxSpawnedAt)
            {
                if (_remoteSmouldering.Contains(kv.Key)) continue;
                if (now - kv.Value < after) continue;
                _remoteSmoulderScratch.Add(kv.Key);
            }

            for (int i = 0; i < _remoteSmoulderScratch.Count; i++)
            {
                ZDOID c = _remoteSmoulderScratch[i];
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

        /// <summary>
        /// Client-side: a tree, log or piece just came up as an instance. If this client has
        /// been told it is burning and has no fire drawn on it (it was out of range when the
        /// event arrived, or it was de-instantiated and rebuilt), draw one now.
        /// </summary>
        public void OnBurnableInstantiated(Component target)
        {
            if (target == null || ValheimBridge.IsServer()) return;
            if (_remoteBurningIds.Count == 0) return;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            if (!id.HasValue || !_remoteBurningIds.Contains(id.Value)) return;
            if (_remoteVfx.TryGetValue(id.Value, out GameObject existing) && existing != null)
            {
                // Drawn already; if its rig lost the old instance, rebuild against the new one.
                FireVFXController c = existing.GetComponent<FireVFXController>();
                if (c != null && !c.TargetLost) return;
                RemoveRemoteVfxFor(id.Value);
            }
            EnqueueRemoteObjectVfx(id.Value);
        }
        private void HandleFireEventBroadcast(long sender, ZDOID id, bool started)
        {
            // Receipt trace — kept permanently, see HandleGroundFireSync.
            FireLogger.Debug($"[SYNC-DIAG] FireEvent arrived from {sender} (id={id}, started={started}, IsServer={ValheimBridge.IsServer()}).");
            if (ValheimBridge.IsServer()) return;
            // ZRoutedRpc relays the sender id verbatim, so a modded client could address a fake
            // event to Everybody and paint phantom fires on every screen. Only the server's count.
            if (!ValheimBridge.IsFromServer(sender)) return;

            // No ComponentFromZdoid here: on a client that helper force-creates the object and
            // CLAIMS OWNERSHIP of it, which would pull a far-away tree's ownership (and the
            // server's damage ticks) to whichever client heard about the fire first. The
            // instance is looked up without side effects when the rig is built, and a burner
            // that is not loaded here simply gets its fire when it instantiates.
            if (started)
            {
                _remoteBurningIds.Add(id);
                EnqueueRemoteObjectVfx(id);
            }
            else
            {
                _remoteBurningIds.Remove(id);
                RemoveRemoteVfxFor(id);
            }
        }

        /// <summary>
        /// Client side of the join-time object snapshot: the same bookkeeping a FireEvent does,
        /// once per fire, plus the age so the smoulder clock is backdated when the rig is built.
        /// Idempotent - a fire already known from a FireEvent is left exactly as it is.
        /// </summary>
        private void HandleObjectFireSync(long sender, ZPackage pkg)
        {
            FireLogger.Debug($"[SYNC-DIAG] ObjectFireSync arrived from {sender} (IsServer={ValheimBridge.IsServer()}).");
            if (ValheimBridge.IsServer() || pkg == null) return;
            if (!ValheimBridge.IsFromServer(sender)) return; // only the server paints fires on this client
            _objectSnapshotReceived = true;
            int count = pkg.ReadInt();
            if (count < 0 || count > 4096) return;
            int added = 0;
            for (int i = 0; i < count; i++)
            {
                ZDOID id = pkg.ReadZDOID();
                float age = pkg.ReadSingle();
                bool smouldering = pkg.ReadBool();
                if (id == ZDOID.None) continue;
                // Same discipline as HandleFireDamage: IsFromServer is a filter, not an
                // authenticator, and a NaN here would poison two clocks (the smoulder skip test
                // and FireVFXController's progress) for the life of the fire. Clamped, not
                // dropped, so the fire is still drawn.
                age = (float.IsNaN(age) || float.IsInfinity(age)) ? 0f : Mathf.Clamp(age, 0f, 36000f);
                if (!_remoteBurningIds.Add(id))
                {
                    continue; // heard its FireEvent already; its own clock stands
                }
                _remoteAgeAtSync[id] = Time.time - age; // as an ignition instant on this clock: the rig may be built much later
                if (smouldering) _remoteSmoulderAtSync.Add(id);
                EnqueueRemoteObjectVfx(id);
                added++;
            }
            FireLogger.Debug($"[SYNC-DIAG] object snapshot from {sender}: {count} burning, {added} new to this client.");
        }

        /// <summary>
        /// When a fire this client learned of from a snapshot ignited, on THIS machine's clock
        /// (Time.time at arrival minus the age the server reported). Stored that way rather than
        /// as the age itself because the rig is built when the burner instantiates, which can be
        /// long after the packet - a stored age would then be stale by exactly that long.
        /// </summary>
        private readonly Dictionary<ZDOID, float> _remoteAgeAtSync = new Dictionary<ZDOID, float>();
        private readonly HashSet<ZDOID> _remoteSmoulderAtSync = new HashSet<ZDOID>();

        private readonly List<ZDOID> _remoteObjectVfxQueue = new List<ZDOID>();
        private readonly HashSet<ZDOID> _remoteObjectVfxQueued = new HashSet<ZDOID>();

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
        private void EnqueueRemoteObjectVfx(ZDOID id)
        {
            if (id == ZDOID.None) return;
            if (_remoteVfx.ContainsKey(id)) return;
            if (!_remoteObjectVfxQueued.Add(id)) return;
            _remoteObjectVfxQueue.Add(id);
        }

        private void DrainRemoteObjectVfxQueue()
        {
            int spawned = 0;
            while (_remoteObjectVfxQueue.Count > 0 && spawned < RemoteObjectVfxSpawnsPerFrame)
            {
                ZDOID id = _remoteObjectVfxQueue[_remoteObjectVfxQueue.Count - 1];
                _remoteObjectVfxQueue.RemoveAt(_remoteObjectVfxQueue.Count - 1);
                _remoteObjectVfxQueued.Remove(id);

                // Extinguished while it sat in the queue.
                if (!_remoteBurningIds.Contains(id)) continue;

                // Not instantiated on this peer: nothing to draw yet. OnBurnableInstantiated
                // queues it again the moment the object comes up.
                Component target = ValheimBridge.InstanceComponentOf(id);
                if (target == null) continue;

                SpawnRemoteVfxOnly(id, target);
                spawned++;
            }
        }

        private void SpawnRemoteVfxOnly(ZDOID id, Component target)
        {
            if (_remoteVfx.ContainsKey(id)) return;

            bool wantVisual = FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value);
            if (!wantVisual) return;

            float age = 0f;
            if (_remoteAgeAtSync.TryGetValue(id, out float ignitedAt)) { age = Mathf.Max(0f, Time.time - ignitedAt); _remoteAgeAtSync.Remove(id); }

            GameObject instance = null;
            if (FireConfig.UseProceduralVfx.Value)
            {
                Vector3 pos = ValheimBridge.PositionOf(target);
                instance = new GameObject("FireFrontProceduralVFX_Remote");
                instance.transform.position = pos;

                float h = ValheimBridge.MeasureBurnerHeight(target);
                float r = ValheimBridge.MeasureBurnerCrownRadius(target);
                if (h <= 0.1f) h = 2f;
                if (r <= 0.1f) r = 0.5f;

                Bounds b = new Bounds(pos + Vector3.up * (h / 2f), new Vector3(r * 2, h, r * 2));
                float duration = FireConfig.BurnDurationSeconds.Value;

                // Backdated by the server's age when the fire arrived in a join-time snapshot: a
                // structure's front climbs by time (trees read their ZDO health), so without this
                // a fire that has burned ten minutes elsewhere would restart at the foot here.
                var vfxController = instance.AddComponent<FireVFXController>();
                vfxController.Setup(b, duration, target, id, age);
            }
            else
            {
                GameObject prefab = ValheimBridge.FindPrefabByName(FireConfig.VfxPrefabName.Value);
                if (prefab != null) instance = ValheimBridge.SpawnVfx(prefab, ValheimBridge.PositionOf(target));
            }

            if (instance != null)
            {
                _remoteVfx[id] = instance;
                _remoteVfxSpawnedAt[id] = Time.time - age; // this client's own smoulder clock, same backdating
                if (_remoteSmoulderAtSync.Remove(id))
                {
                    _remoteSmouldering.Add(id);
                    try { ValheimBridge.DowngradeVfxToSmoulder(instance); } catch (System.Exception) { }
                }
            }
        }

        private void RemoveRemoteVfxFor(ZDOID id)
        {
            // Drop it from the queue too, or an extinguished burner still gets a
            // fire built for it a frame or two later and is never cleaned up.
            if (_remoteObjectVfxQueued.Remove(id)) _remoteObjectVfxQueue.Remove(id);

            _remoteAgeAtSync.Remove(id);
            _remoteSmoulderAtSync.Remove(id);
            if (_remoteVfx.TryGetValue(id, out GameObject instance))
            {
                _remoteVfx.Remove(id);
                _remoteVfxSpawnedAt.Remove(id);
                _remoteSmouldering.Remove(id);
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
            // 1.0.2: at most two cycles' worth. The clock only moves while the cycle runs, so
            // resuming after `fireset enabled false` charged the whole pause in one step and every
            // fire in the rain went out at once.
            float dt = FireMath.RainAgeSeconds(_lastRainAgeTime, now, 2f * FireConfig.EffectiveSpreadCheckInterval);
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

                BurnKind kind = ValheimBridge.KindOf(target);
                if (kind == BurnKind.Tree || kind == BurnKind.Log)
                {
                    // The timer is the fallback authority for trees: the unseen damage ticks
                    // normally get there first (TreeFireKillFraction), but rain shortens the
                    // timer and not the ticks, and TreeFireDamageEnabled can be off entirely.
                    // Either way the tree ends charred, never felled with real wood.
                    _burning.Remove(id);
                    FireLogger.Debug($"Burned down (timer): {ValheimBridge.NameOf(target)}");
                    CharTree(id, state);
                    RemoveVfxFor(id);
                    killed++;
                    continue;
                }

                _burning.Remove(id);
                FireLogger.Debug($"Burned down: {ValheimBridge.NameOf(target)}");
                ValheimBridge.KillBurningTarget(target);
                RemoveVfxFor(id);
                killed++;
            }
        }

        // ---------------------------------------------------------------
        // Tree fire damage: the flames climb by the tree's real health
        // ---------------------------------------------------------------

        private float _nextTreeTick = -1f;
        private int _treesCharredCount;
        private int _treesCollapsedCount;

        /// <summary>
        /// Every TreeFireTickInterval, each burning tree and log takes an unseen fire tick sized
        /// so a healthy one dies at TreeFireKillFraction of BurnDurationSeconds; a tree whose
        /// replicated health has reached the floor is charred. Ticks are routed to whichever
        /// peer owns the tree (see CharredTreeLifecycle), so this costs one routed RPC per tree
        /// per tick, not a Component resolution.
        /// </summary>
        private void TickTreeFire()
        {
            // 1.0.2: the clock stops with the fire. It used to stand still while nothing burned,
            // so the first tick of the NEXT fire charged the whole quiet spell as burn time and
            // charred its first tree almost at once, before it was old enough to spread.
            if (!FireConfig.TreeFireDamageEnabled.Value || _burning.Count == 0) { _nextTreeTick = -1f; return; }
            float interval = Mathf.Max(0.5f, FireConfig.TreeFireTickInterval.Value);
            if (Time.time < _nextTreeTick) return;
            float dt = FireMath.TreeTickSeconds(_nextTreeTick, Time.time, interval, FireConfig.EffectiveSpreadCheckInterval);
            _nextTreeTick = Time.time + interval;

            float killSeconds = Mathf.Max(1f, FireConfig.BurnDurationSeconds.Value * Mathf.Clamp(FireConfig.TreeFireKillFraction.Value, 0.2f, 1f));
            int charred = 0;

            _scratch.Clear();
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning) _scratch.Add(kv.Key);

            for (int i = 0; i < _scratch.Count; i++)
            {
                ZDOID id = _scratch[i];
                if (!_burning.TryGetValue(id, out BurningState state)) continue;
                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
                if (zdo == null) continue;

                float max = CharredTreeLifecycle.MaxHealthOf(zdo);
                if (max <= 0f) continue; // a piece, or an unknown prefab: the timer handles it

                if (CharredTreeLifecycle.IsBurnedOut(zdo, max))
                {
                    if (charred >= FireConfig.EffectiveMaxKillsPerCycle) continue; // same batch cap as ExpireTimers
                    _burning.Remove(id);
                    FireLogger.Debug($"[TREE-HP] {state.PrefabName} burned out (health floor) — charring.");
                    CharTree(id, state);
                    RemoveVfxFor(id);
                    charred++;
                    continue;
                }

                float damage = max * dt / killSeconds;
                float before = CharredTreeLifecycle.HealthOf(zdo, max);
                bool sent = CharredTreeLifecycle.SendFireTick(zdo, damage);
                FireLogger.Debug($"[TREE-HP] {state.PrefabName} {id}: {before:F1}/{max:F0} -{damage:F2} " +
                                 $"({(sent ? (zdo.GetOwner() == ZDOMan.GetSessionID() || zdo.GetOwner() == 0L ? "applied here" : "routed to owner " + zdo.GetOwner()) : "NOT sent")})");
            }
        }

        /// <summary>
        /// A tree or log the fire has killed: replaced in place by its charred twin, whose fate
        /// (collapse or stand) is rolled now and carried in its ZDO. Regrowth is queued only for
        /// a collapse — a standing charred snag occupies the spot.
        /// </summary>
        private void CharTree(ZDOID id, BurningState state)
        {
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (zdo == null)
            {
                FireLogger.Debug($"[CHARRED] {state.PrefabName} {id}: ZDO already gone, nothing to char.");
                return;
            }
            bool isTree = ZNetScene.instance != null && ZNetScene.instance.GetPrefab(zdo.GetPrefab()) is GameObject p && p.GetComponent<TreeBase>() != null;
            int fate;
            try
            {
                fate = CharredTreeLifecycle.ReplaceWithCharred(zdo, out _);
            }
            catch (System.Exception ex)
            {
                // The charring is gameplay, so a throw is logged loudly; the tree is still
                // removed so the fire's bookkeeping cannot wedge on it.
                FireLogger.Warn($"[CHARRED] ReplaceWithCharred threw for {state.PrefabName}: {ex}");
                Component target = ValheimBridge.ComponentFromZdoid(id);
                if (target != null) ValheimBridge.KillBurningTarget(target);
                return;
            }
            if (fate == CharredTreeLifecycle.FateUndecided)
            {
                // No registered prefab for this ZDO (a mod's tree that was unloaded?): the
                // one thing that must not happen is a burned-out tree standing forever at
                // the health floor, so it is felled the old way.
                FireLogger.Warn($"[CHARRED] could not char {state.PrefabName} {id} (prefab unresolved); felling it instead.");
                Component target = ValheimBridge.ComponentFromZdoid(id);
                if (target != null) ValheimBridge.KillBurningTarget(target);
                return;
            }

            _treesCharredCount++;
            if (fate == CharredTreeLifecycle.FateCollapse)
            {
                _treesCollapsedCount++;
                if (isTree && FireConfig.EffectiveTreeRegrowthEnabled)
                {
                    EnqueueRegrowth(new PendingRegrowth
                    {
                        PrefabName = state.PrefabName,
                        Position = state.Position,
                        RegrowAt = Time.time + FireConfig.TreeRegrowthSeconds.Value
                    });
                }
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

                // Queued before FireInAshlands was switched off, or queued from outside: the
                // drain calls StartBurning directly, so it checks for itself.
                Vector3 queuedAt = ValheimBridge.PositionOf(target);
                if (AshlandsBarsFireAt(queuedAt)) continue;

                // A queued item rejoins whichever blaze is nearest it now.
                int evId = EventForPosition(queuedAt, 0L);
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
        /// <summary>
        /// Burns every player standing in fire, on their own machine.
        ///
        /// This replaces asking physics. `FireBurnZone` polls Physics.OverlapSphere, which on a
        /// dedicated server finds trees and scenery near a fire and NEVER finds a player, because
        /// the server holds no instance or collider where players are - only ZDOs. Fire therefore
        /// never hurt anyone on a dedicated server from 0.1 to 0.21.7, silently, while working
        /// fine on a world someone hosted themselves. Reported by Wu'barrk 2026-09-19 and proved
        /// from two servers' logs: the zone's own diagnostics recorded colliders found and a
        /// Character resolved exactly zero times.
        ///
        /// A player's position IS in the ZDO layer and always readable, so the server decides who
        /// is burning and the player's own machine carries it out - the same division this mod
        /// already uses for ignition.
        /// </summary>
        private void DamagePlayersInFire()
        {
            // Wrapped because this sits in the middle of the fire cycle: when the first cut of it
            // threw, every step AFTER it in Update stopped running too, each tick, and the only
            // clue was a Unity stack trace with no FireFront prefix on it. Burning players is the
            // least important thing this loop does and must never be able to stop the rest.
            // Backs OFF on a failure; it does not latch off. An earlier cut of this set a flag and
            // never cleared it, so one unlucky frame - a peer disposed mid-iteration, a ZDO
            // destroyed between two reads - would silently restore the exact bug this release
            // exists to fix, on a server that otherwise looked completely healthy. That is the
            // failure mode, not the accident that causes it.
            if (Time.time < _playerDamageRetryAt) return;
            try { DamagePlayersInFireCore(); }
            catch (System.Exception ex)
            {
                _playerDamageRetryAt = Time.time + PlayerDamageRetrySeconds;
                _playerDamageFailures++;
                // Every failure for the first handful, then one line a minute: enough to diagnose a
                // real fault, not enough to bury the log if it starts failing every retry.
                if (_playerDamageFailures <= 3 || Time.time >= _nextPlayerDamageErrorLog)
                {
                    _nextPlayerDamageErrorLog = Time.time + 60f;
                    FireLogger.Error($"Burning players failed ({_playerDamageFailures} time(s)); retrying in " +
                                     $"{PlayerDamageRetrySeconds:0}s. Everything else carries on. Reason: " + ex);
                }
            }
        }
        private const float PlayerDamageRetrySeconds = 10f;
        private float _playerDamageRetryAt;
        private float _nextPlayerDamageErrorLog;
        private int _playerDamageFailures;

        /// <summary>How many times the player-damage pass has thrown, for <c>firestatus</c>.</summary>
        internal int PlayerDamageFailureCount => _playerDamageFailures;

        private void DamagePlayersInFireCore()
        {
            if (!FireConfig.FireHurtsEnabled.Value) return;
            if (_burning.Count == 0 && _groundBurning.Count == 0) return;

            // Its own clock, not the spread cycle's: the damage interval is a player-facing number
            // and should not change because someone tuned how often fire thinks about spreading.
            float now = Time.time;
            if (now < _nextPlayerDamageTick) return;
            _nextPlayerDamageTick = now + Mathf.Max(0.1f, FireConfig.FireDamageTickInterval.Value);

            float damage = FireConfig.FireDamagePerTick.Value;
            float objRadius = FireConfig.FireHurtsObjectRadius.Value;

            // The host's own player on a listen server is a local object, so it is burned directly
            // rather than posted to itself.
            Vector3? localPos = ValheimBridge.LocalPlayerPosition();
            if (localPos.HasValue && IsStandingInFire(localPos.Value, objRadius))
                ValheimBridge.ApplyFireDamageToLocalPlayer(damage);

            if (!ValheimBridge.CollectPlayerTargets(_playerTargetScratch)) return;
            for (int i = 0; i < _playerTargetScratch.Count; i++)
            {
                ValheimBridge.PlayerTarget t = _playerTargetScratch[i];
                if (!IsStandingInFire(t.Position, objRadius)) continue;
                ValheimBridge.SendFireDamageToPeer(t.PeerId, damage);
                FireLogger.Debug($"[BURN] {t.Name} is standing in fire at {t.Position} — {damage} sent to their machine.");
            }
        }

        /// <summary>
        /// True if this position is inside a burning ground cell, or within <paramref name="objRadius"/>
        /// of a burning object. The same two questions the physics zone used to answer, asked of
        /// data that exists on every machine instead of colliders that exist on almost none.
        /// </summary>
        private bool IsStandingInFire(Vector3 pos, float objRadius)
        {
            // The Y band is NOT optional. A GroundCellKey is (x,z) only - KeyOf throws the height
            // away - so a bare ContainsKey makes every burning cell an infinite vertical column:
            // ground fire under a longhouse would burn the player on the floor above it, with no
            // flame in sight and nothing to tell them what was killing them. Also a cliff above a
            // burning beach, and a crypt below one. The physics zone this replaced was a sphere of
            // GroundCellSize*0.5, so that is the band restored here.
            if (_groundBurning.TryGetValue(KeyOf(pos), out GroundCellState cell))
            {
                // The height band is only applied when the cell's Y is a MEASUREMENT. If it is not -
                // no terrain and no WorldGenerator to ask - then Y is the height of whatever lit the
                // fire, and comparing a player against it would refuse damage to anyone standing more
                // than a metre above or below that, which on a slope is most of the fire. 0.21.9
                // shipped exactly that and silently undid 0.21.8 on dedicated servers. Better to keep
                // the old unbounded column in the rare case we are blind than to stop burning people.
                if (!cell.YIsReal ||
                    Mathf.Abs(pos.y - cell.Y) <= Mathf.Max(1f, FireConfig.GroundCellSize.Value * 0.5f))
                    return true;
            }

            float radiusSqr = objRadius * objRadius;
            foreach (BurningState st in _burning.Values)
                if ((st.Position - pos).sqrMagnitude <= radiusSqr) return true;
            return false;
        }

        private float _nextPlayerDamageTick;
        private readonly List<ValheimBridge.PlayerTarget> _playerTargetScratch = new List<ValheimBridge.PlayerTarget>();

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
            const int maxPerCycle = 5;       // trees planted in one frame; the rest wait for the next
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
                    // A whole burned forest comes due together, and every plant is an Instantiate
                    // plus a new ZDO every client in range then receives. Until 0.21.5 this path
                    // could never fire headless, so it has no history at scale; spread the work.
                    if (regrew >= maxPerCycle) break;
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
        /// Flushes the cells this machine was elected to paint to the batched real-dirt painter
        /// once per PaintFlushInterval. Runs on every machine, above the server gate: a listen
        /// host feeds it from AssignPendingPaint, a client from HandlePaintAssign, and on a
        /// dedicated server it is always empty. What the painter cannot reach is dropped rather
        /// than retried - the decal already covers the look.
        /// </summary>
        private void FlushPendingPaint()
        {
            if (_pendingPaint.Count == 0) return;
            if (Time.time < _nextPaintFlush) return;
            _nextPaintFlush = Time.time + PaintFlushInterval;

            int laid = ValheimBridge.TryPaintScorchedDirtBatch(_pendingPaint, _pendingPaintRadius);
            if (laid < _pendingPaint.Count)
            {
                FireLogger.Debug($"Paint flush: {laid} paint ops laid for {_pendingPaint.Count} cells " +
                                  "(the rest had no heightmap loaded here, or a compiler someone else owns; dropped).");
            }
            _pendingPaint.Clear();
        }

        /// <summary>
        /// Server side, once a second: hands every burnt cell to exactly ONE painter, decided per
        /// ZONE, because what has to be unique is the writer of a zone's compiler ZDO: only its
        /// OWNER can publish a write, and a zone nobody has touched has no compiler until someone
        /// creates one, while creating one when another exists anywhere destroys the other with
        /// every hoe mark in it (TerrainComp.Awake). The server is the one machine that sees every
        /// peer and every compiler ZDO, so it decides both who paints and whether they may create.
        /// Per zone with burnt cells: the compiler's owner if it is connected and in reach (an
        /// owner that is not - a peer that left or teleported away, or this server after a world
        /// load - is released first; ReleaseNearbyZDOS only looks around a peer's CURRENT position,
        /// so a compiler left behind by a teleport is otherwise held for good); else whoever was
        /// elected for the zone in the last few seconds and is still in reach (the window between
        /// a painter creating the compiler and its ZDO reaching us - not re-armed while it supplies
        /// the painter, so a painter that cannot reach the zone is replaced when it expires); else
        /// the nearest candidate in reach, who becomes the zone's painter for that window. "In
        /// reach" is the zone or one next to it for a zone that has a compiler, and the zone
        /// itself for one that has none (see PaintCreateZoneReach). A listen host is a candidate
        /// like any peer and queues for itself. Zones no candidate can reach are dropped: the
        /// decal covers them.
        /// </summary>
        private void AssignPendingPaint()
        {
            if (_paintAssignPending.Count == 0) return;
            if (Time.time < _nextPaintAssign) return;
            _nextPaintAssign = Time.time + PaintAssignInterval;
            float now = Time.time;

            ValheimBridge.CollectPaintCandidates(_paintCandidates);
            if (_paintCandidates.Count == 0) { _paintAssignPending.Clear(); return; }
            long self = ValheimBridge.IsDedicatedServer() ? 0L : ValheimBridge.LocalSessionId();

            _paintAssignByZone.Clear();
            foreach ((GroundCellKey key, float y) in _paintAssignPending)
            {
                Vector2s zone = ZoneSystem.GetZone(CellCenter(key, y));
                if (!_paintAssignByZone.TryGetValue(zone, out List<(GroundCellKey key, float y)> cells))
                {
                    cells = new List<(GroundCellKey, float)>();
                    _paintAssignByZone[zone] = cells;
                }
                cells.Add((key, y));
            }
            _paintAssignPending.Clear();

            _paintAssignByPeer.Clear();
            int droppedZones = 0;
            foreach (KeyValuePair<Vector2s, List<(GroundCellKey key, float y)>> zoneCells in _paintAssignByZone)
            {
                Vector2s zone = zoneCells.Key;
                Vector3 zoneCentre = ZoneSystem.GetZonePos(zone);

                ZDO compiler = ValheimBridge.FindTerrainCompilerZdo(zoneCentre);
                bool mayCreate = compiler == null;
                int reach = mayCreate ? PaintCreateZoneReach : PaintAssignZoneReach;
                long owner = compiler != null ? compiler.GetOwner() : 0L;
                if (owner != 0L && !CandidateInReach(owner, zone, reach))
                {
                    ValheimBridge.ReleaseZdoOwner(compiler);
                    owner = 0L;
                }

                long painter = 0L;
                if (owner != 0L)
                {
                    painter = owner;
                }
                else if (_zonePainter.TryGetValue(zone, out (long peer, float until) sticky) && sticky.until > now && CandidateInReach(sticky.peer, zone, reach))
                {
                    painter = sticky.peer;
                }
                else
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < _paintCandidates.Count; i++)
                    {
                        if (!InPaintReach(_paintCandidates[i].Position, zone, reach)) continue;
                        Vector3 d = _paintCandidates[i].Position - zoneCentre;
                        d.y = 0f;
                        float sq = d.sqrMagnitude;
                        if (sq < best) { best = sq; painter = _paintCandidates[i].PeerId; }
                    }
                    if (painter != 0L) _zonePainter[zone] = (painter, now + PaintAssignStickySeconds);
                }
                if (painter == 0L) { droppedZones++; continue; }

                if (!_paintAssignByPeer.TryGetValue(painter, out List<ValheimBridge.PaintJob> jobs))
                {
                    jobs = new List<ValheimBridge.PaintJob>();
                    _paintAssignByPeer[painter] = jobs;
                }
                foreach ((GroundCellKey key, float y) in zoneCells.Value)
                {
                    _groundPainted.Add(key);
                    jobs.Add(new ValheimBridge.PaintJob { Position = CellCenter(key, y), MayCreate = mayCreate });
                }
            }

            foreach (KeyValuePair<long, List<ValheimBridge.PaintJob>> kv in _paintAssignByPeer)
            {
                if (kv.Key == self)
                {
                    _pendingPaintRadius = FireConfig.DirtPaintRadius.Value;
                    _pendingPaint.AddRange(kv.Value);
                    continue;
                }
                // World positions, not cell indices: the painter's idea of GroundCellSize is its
                // own config, and permanent dirt at the wrong coordinates is not a mistake to allow.
                var pkg = new ZPackage();
                pkg.Write(FireConfig.DirtPaintRadius.Value);
                pkg.Write(kv.Value.Count);
                foreach (ValheimBridge.PaintJob job in kv.Value)
                {
                    pkg.Write(job.Position);
                    pkg.Write(job.MayCreate);
                }
                ValheimBridge.SendPaintAssignTo(kv.Key, pkg);
            }
            if (droppedZones > 0)
                FireLogger.Debug($"Paint assign: {droppedZones} zone(s) of burnt cells had no player in reach to paint them (in the zone if it has never been hoed, in or next to it otherwise); dropped.");

            // Zones are per fire, not per session; sweep the expired ones now and then.
            if (_zonePainter.Count > 256)
            {
                _zonePainterSweep.Clear();
                foreach (KeyValuePair<Vector2s, (long peer, float until)> kv in _zonePainter)
                    if (kv.Value.until <= now) _zonePainterSweep.Add(kv.Key);
                for (int i = 0; i < _zonePainterSweep.Count; i++) _zonePainter.Remove(_zonePainterSweep[i]);
            }
        }

        private bool CandidateInReach(long peerId, Vector2s zone, int reach)
            => TryGetPaintCandidatePosition(peerId, out Vector3 position) && InPaintReach(position, zone, reach);

        private bool TryGetPaintCandidatePosition(long peerId, out Vector3 position)
        {
            for (int i = 0; i < _paintCandidates.Count; i++)
            {
                if (_paintCandidates[i].PeerId != peerId) continue;
                position = _paintCandidates[i].Position;
                return true;
            }
            position = Vector3.zero;
            return false;
        }

        private static bool InPaintReach(Vector3 candidate, Vector2s zone, int reach)
        {
            Vector2s cz = ZoneSystem.GetZone(candidate);
            return Mathf.Abs(cz.x - zone.x) <= reach && Mathf.Abs(cz.y - zone.y) <= reach;
        }

        /// <summary>
        /// Client side: the server elected this machine to lay real dirt for these cells - it owns
        /// their zone's terrain compiler, or is the nearest player to a zone nobody has touched,
        /// in which case the job says it may create one. Queued for FlushPendingPaint. The radius
        /// travels with the cells so the server's setting governs the look for everyone.
        /// IsFromServer is a filter on its own (see HandleFireDamage); since 0.23 the server drops
        /// a FireFront package whose claimed sender is not its connection, so the filter holds
        /// wherever that guard is armed. The count cap and the radius clamp bound one message and
        /// the window below bounds how many arrive.
        /// </summary>
        private void HandlePaintAssign(long sender, ZPackage pkg)
        {
            if (!ValheimBridge.IsFromServer(sender)) return;
            float now = Time.realtimeSinceStartup;
            if (now >= _paintAssignWindowEnd)
            {
                _paintAssignWindowEnd = now + PaintAssignWindowSeconds;
                _paintAssignInWindow = 0;
            }
            if (++_paintAssignInWindow > PaintAssignMaxPerWindow)
            {
                AuthLog.Refused(sender, "paint-burst", $"dropped a paint assignment: more than {PaintAssignMaxPerWindow} in {PaintAssignWindowSeconds:F0} s");
                return;
            }
            float radius = pkg.ReadSingle();
            int count = pkg.ReadInt();
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f || count < 0 || count > 4096) return;
            _pendingPaintRadius = Mathf.Clamp(radius, 0.5f, 8f);
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = pkg.ReadVector3();
                bool mayCreate = pkg.ReadBool();
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) ||
                    float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z)) continue;
                _pendingPaint.Add(new ValheimBridge.PaintJob { Position = pos, MayCreate = mayCreate });
            }
        }

        /// <summary>
        /// Server-side: packs accumulated ground-fire ignite/expire deltas into a
        /// ZPackage and broadcasts them on its own clock. Only the server should ever
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
            WriteGroundSyncTrailer(pkg);

            ValheimBridge.BroadcastGroundFireSync(pkg);
            _groundIgnitedSinceFlush.Clear();
            _groundExpiredSinceFlush.Clear();
        }

        // 1.0.2: a marker, then the server's GroundCellSize, AFTER the two lists. Appended rather
        // than put in front so the layout stays readable both ways: a 1.0.1 client stops reading
        // after the lists and never sees it, and a 1.0.2 client talking to an older server finds
        // nothing there and falls back to its own size, which is all that server could offer.
        // Hence no WireProtocol bump. Before, each client turned the indices into positions with
        // its OWN cell size, so a client set to 2 m drew a server's 1 m fire at twice its
        // coordinates, and warmed and scorched there too.
        private const int GroundSyncCellSizeMarker = 0x46464353; // "FFCS"

        private static void WriteGroundSyncTrailer(ZPackage pkg)
        {
            pkg.Write(GroundSyncCellSizeMarker);
            pkg.Write(FireConfig.GroundCellSize.Value);
        }

        /// <summary>
        /// The server's cell size from a sync package's trailer, or this machine's own when there
        /// is none. Leaves the read position where it was, at the start of the lists.
        /// </summary>
        private static float ReadGroundSyncCellSize(ZPackage pkg)
        {
            float local = FireConfig.GroundCellSize.Value;
            int start = pkg.GetPos();
            bool has = false;
            float size = 0f;
            try
            {
                int ignited = pkg.ReadInt();
                if (ignited < 0 || ignited > 1_000_000) return local;
                pkg.SetPos(pkg.GetPos() + ignited * 12);  // int x, int z, float y
                int expired = pkg.ReadInt();
                if (expired < 0 || expired > 1_000_000) return local;
                pkg.SetPos(pkg.GetPos() + expired * 8);   // int x, int z
                if (pkg.Size() - pkg.GetPos() >= 8 && pkg.ReadInt() == GroundSyncCellSizeMarker)
                {
                    size = pkg.ReadSingle();
                    has = true;
                }
            }
            catch (System.Exception) { has = false; }
            finally { pkg.SetPos(start); }
            return FireMath.SyncCellSize(has, size, local);
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
            if (!ValheimBridge.IsFromServer(sender)) return; // same forgery guard as the object handlers

            // 1.0.2: positions use the SERVER's GroundCellSize, carried after the lists (see
            // WriteGroundSyncTrailer). Read first, so every cell below is placed with it.
            float cellSize = ReadGroundSyncCellSize(pkg);

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

                // Recorded whether or not it is ever DRAWN. Anything that asks "is there fire near
                // me" has to read this, not the VFX dictionary: with visuals switched off there are
                // no GameObjects at all, and keying behaviour off them would make a setting about
                // appearance silently change what fire does to you.
                Vector3 centre = new Vector3(FireMath.CellCentre(x, cellSize), y, FireMath.CellCentre(z, cellSize));
                _remoteGroundCells[key] = centre;

                if (!_remoteGroundVfx.ContainsKey(key) && _remoteVfxQueuedKeys.Add(key))
                    _remoteVfxSpawnQueue.Add((key, centre));
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

                // Burnt ground leaves a scar - a thing the README has promised players for a long
                // time and that, on a dedicated server, none of them has ever seen: the decal is
                // not networked, and the only machine that drew one was the one simulating. The
                // client already holds everything needed to draw its own. It recorded this cell's
                // height when it ignited, and this is the moment the cell went out. The size
                // passed is the CELL, unscaled: the spawner grows it to its documented 1.6-2.2x
                // itself. Until 2026-09-20 this site (and LeaveScorchMark) passed the cell x 1.5
                // on top of that, which NomadicWar caught from the changelog's own numbers - a
                // 2.4-3.3 m blot per 1 m cell, 2.7 multiply blots deep over every burnt square
                // metre, burnt ground pushed toward black.
                if (FireConfig.EffectiveScorchMarksEnabled &&
                    _remoteGroundCells.TryGetValue(key, out Vector3 scorchAt))
                {
                    QueueScorchMark(scorchAt,
                        cellSize,
                        FireConfig.ScorchMarkLifetimeSeconds.Value);
                }

                _remoteGroundCells.Remove(key);
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
            // GroundVfxMaxConcurrent governs SpawnGroundVfxFor, which is gated on
            // !IsDedicatedServer() - so on a real dedicated server it governed nothing, and the
            // client mirror below drew every synced cell with NO ceiling at all. The admin's cap
            // never reached the machine actually rendering, and LowSpec's clamp of 10 never
            // reached the players most likely to need it.
            int cap = FireConfig.EffectiveGroundVfxMaxConcurrent;
            if (cap <= 0)
            {
                // Ground visuals off entirely. Nothing will ever drain, so do not let the queue
                // grow for the life of the session. Warmth and damage read _remoteGroundCells,
                // not the VFX, so turning this to 0 still only changes what you see.
                _remoteVfxSpawnQueue.Clear();
                _remoteVfxQueuedKeys.Clear();
                return;
            }

            int spawned = 0;
            while (_remoteVfxSpawnQueue.Count > 0 && spawned < RemoteVfxSpawnsPerFrame)
            {
                // BREAK, do not pop. Popping would drop the key from _remoteVfxQueuedKeys and
                // SpawnRemoteGroundVfxFor would never be asked for that cell again - the client
                // has no equivalent of UpgradeDarkGroundCells, so a discarded cell would stay
                // dark until it expired. That is precisely the permanently patchy front 0.21.9
                // fixed on the server side. Left queued, it retries for free next frame as
                // expiries free headroom.
                if (_remoteGroundVfx.Count >= cap) break;

                (GroundCellKey key, Vector3 pos) = _remoteVfxSpawnQueue[_remoteVfxSpawnQueue.Count - 1];
                _remoteVfxSpawnQueue.RemoveAt(_remoteVfxSpawnQueue.Count - 1);
                if (!_remoteVfxQueuedKeys.Remove(key)) continue; // expired while queued
                SpawnRemoteGroundVfxFor(key, pos);
                spawned++;
            }
        }

        /// <summary>
        /// Drops everything this machine was drawing on behalf of a world it is no longer in. Safe to
        /// call when already empty, which is what happens on a first connect.
        /// </summary>
        private bool _wantGroundSnapshot;
        private bool _objectSnapshotReceived;
        private int _snapshotRequestsSent;
        private float _nextSnapshotRetry;
        private const float SnapshotRetrySeconds = 10f; // must exceed the server's 5 s per-sender cooldown or the retry is swallowed
        private const int SnapshotMaxRequests = 3;
        private float _nextWarmthCheck;
        // Keyed by the SERVER's cell indices; the value is the cell's centre, placed with the
        // server's cell size when the cell arrived (1.0.2; was the height alone, placed with this
        // machine's own GroundCellSize, so a client set differently drew the fire elsewhere).
        private readonly Dictionary<GroundCellKey, Vector3> _remoteGroundCells = new Dictionary<GroundCellKey, Vector3>();

        // A scorch decal waits for its zone. A cell can go out anywhere on the map, and the sync
        // stream delivers the expiry during the loading screen: the first play test logged five
        // of five marks with no terrain hit, spawned 500 m from the player before any heightmap
        // existed. A quad placed then floats at the synced height, untilted, and spends its
        // lifetime unseen. So the mark is queued with its expiry and spawned, with whatever
        // lifetime is left, once ZoneSystem has the zone loaded here. Bounded, thinned before it
        // is queued, one entry per cell, and drained on a per-frame budget.
        private struct PendingScorch { public Vector3 Pos; public float Size; public float ExpiresAt; public float Floor; public (int x, int z) Cell; }
        private readonly List<PendingScorch> _pendingScorch = new List<PendingScorch>();
        // One entry per cell, found in O(1). A cell that re-burns while its mark waits refreshes
        // the entry instead of adding one (two entries would spawn as coincident multiply blots).
        // Kept in step by every add, swap-remove, overwrite and clear. Every mark goes through
        // the queue, loaded zone or not: a sync batch of expiries in the zone the player stands
        // in is the common case, and spawning it inline would be the one-frame burst the drain
        // budget exists to prevent. A loaded cell is spawned by the drain within a frame or two.
        private readonly Dictionary<(int x, int z), int> _pendingScorchIndex = new Dictionary<(int x, int z), int>();
        private const int PendingScorchCap = 4000;          // distinct real marks (thinned, one per cell): the Apocalypse preset's ground counts to reach
        private const int PendingScorchScanPerFrame = 256;  // entries checked for a loaded zone per frame
        private const int PendingScorchSpawnsPerFrame = 8;  // five terrain raycasts and a quad each: a full cap fills in ~8 s at 60 fps
        private const float PendingScorchMinVisible = 15f;  // or half the mark's own lifetime, whichever is less; under that is a blink, not a scar
        private int _pendingScorchCursor; // the drain's
        private int _pendingScorchEvict;  // the cap's, separate: a burst of evictions must not push the drain past entries it never checked

        private void QueueScorchMark(Vector3 pos, float size, float lifetimeSeconds)
        {
            if (!ValheimBridge.ScorchMarkKept(pos)) return; // never queue what would never spawn
            ValheimBridge.ScorchCell(pos, out int cx, out int cz);
            var cell = (cx, cz);
            bool queued = _pendingScorchIndex.TryGetValue(cell, out int at);
            var entry = new PendingScorch
            {
                Pos = pos, Size = size, Cell = cell,
                ExpiresAt = Time.time + lifetimeSeconds,
                Floor = Mathf.Min(PendingScorchMinVisible, 0.5f * lifetimeSeconds),
            };
            if (queued) { _pendingScorch[at] = entry; return; } // re-burnt while waiting: the newer expiry wins
            if (_pendingScorch.Count >= PendingScorchCap)
            {
                // Full. This runs inside the sync handler, so the victim is whatever sits at the
                // eviction cursor: O(1), round-robin over time, no scan for the best one.
                if (_pendingScorchEvict >= _pendingScorch.Count) _pendingScorchEvict = 0;
                _pendingScorchIndex.Remove(_pendingScorch[_pendingScorchEvict].Cell);
                _pendingScorch[_pendingScorchEvict] = entry;
                _pendingScorchIndex[cell] = _pendingScorchEvict;
                _pendingScorchEvict++;
                return;
            }
            _pendingScorchIndex[cell] = _pendingScorch.Count;
            _pendingScorch.Add(entry);
        }

        /// <summary>
        /// Client side, every frame, budgeted both ways: at most min(window, count) entries are
        /// checked for a loaded zone (after a removal the tail entry moves under the cursor, so
        /// one can be checked twice in a frame, but never spawned twice), and a bounded number
        /// of marks is spawned, so a burnt region that loads all at once (a teleport, the loading
        /// screen) fills in over a few seconds rather than in one frame - the same reason the
        /// ground VFX queue drains a few per frame. A mark spawns with the lifetime it has left;
        /// one under its own floor is dropped instead of flashing. Marks switched off while they
        /// waited are dropped, and so is one whose loaded zone turns out to have no terrain under
        /// it, or terrain too rough for a flat quad to lie on (SpawnScorchMark answers false for
        /// both), rather than floating it at the synced height or a third of a metre up. The
        /// spawn budget above is sized for the five terrain rays a mark now casts (centre and
        /// four corners): at most 40 a frame, against a heightfield.
        /// </summary>
        private void DrainPendingScorch()
        {
            int count = _pendingScorch.Count;
            if (count == 0) return;
            if (!FireConfig.EffectiveScorchMarksEnabled) { ClearPendingScorch(); return; }
            float now = Time.time;
            int limit = Mathf.Min(PendingScorchScanPerFrame, count);
            int scanned = 0, spawned = 0;
            while (scanned < limit && spawned < PendingScorchSpawnsPerFrame && _pendingScorch.Count > 0)
            {
                if (_pendingScorchCursor >= _pendingScorch.Count) _pendingScorchCursor = 0;
                scanned++;
                PendingScorch p = _pendingScorch[_pendingScorchCursor];
                float left = p.ExpiresAt - now;
                if (left < p.Floor) { RemovePendingScorchAt(_pendingScorchCursor); continue; }
                if (!ValheimBridge.IsZoneLoaded(p.Pos)) { _pendingScorchCursor++; continue; }
                RemovePendingScorchAt(_pendingScorchCursor);
                ValheimBridge.SpawnScorchMark(p.Pos, p.Size, left);
                spawned++;
            }
        }

        private void RemovePendingScorchAt(int i)
        {
            int last = _pendingScorch.Count - 1;
            _pendingScorchIndex.Remove(_pendingScorch[i].Cell);
            if (i != last)
            {
                PendingScorch tail = _pendingScorch[last];
                _pendingScorch[i] = tail;
                _pendingScorchIndex[tail.Cell] = i;
            }
            _pendingScorch.RemoveAt(last);
        }

        private void ClearPendingScorch()
        {
            _pendingScorch.Clear();
            _pendingScorchIndex.Clear();
            _pendingScorchCursor = 0;
            _pendingScorchEvict = 0;
        }

        /// <summary>
        /// Keeps the player at this keyboard warm while they are near a wildfire.
        ///
        /// Runs on whatever machine the player is actually on, because warmth is a status effect on a
        /// live Player and only that machine has one - the same division as fire damage. It reads the
        /// fire THIS machine knows about: the real simulation on a host, the synced record of it on a
        /// client. Note it reads the record rather than the effects, so turning visuals off changes
        /// what you see and nothing else.
        ///
        /// Five times a second, because vanilla's window is a quarter of a second wide.
        /// </summary>
        private void ApplyFireWarmth()
        {
            if (!FireConfig.FireKeepsYouWarm.Value) return;

            float now = Time.time;
            if (now < _nextWarmthCheck) return;
            _nextWarmthCheck = now + WarmthCheckInterval;

            Vector3? here = ValheimBridge.LocalPlayerPosition();
            if (!here.HasValue) return;

            if (TryFindFireNearPlayer(here.Value, FireConfig.FireWarmthRadius.Value, out Vector3 fire))
                ValheimBridge.MarkLocalPlayerNearFire(fire);
        }

        private const float WarmthCheckInterval = 0.2f;

        /// <summary>
        /// The nearest fire this machine knows about within <paramref name="radius"/>, if any. Checks
        /// the authoritative collections when we are the server and the synced ones otherwise, so a
        /// listen host and a connected client both get an answer about the fire they can actually see.
        /// </summary>
        private bool TryFindFireNearPlayer(Vector3 pos, float radius, out Vector3 fire)
        {
            fire = Vector3.zero;
            float best = radius * radius;
            bool found = false;

            if (ValheimBridge.IsServer())
            {
                foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                {
                    Vector3 c = CellCenter(kv.Key, kv.Value.Y);
                    float d = (c - pos).sqrMagnitude;
                    if (d < best) { best = d; fire = c; found = true; }
                }
                foreach (BurningState st in _burning.Values)
                {
                    float d = (st.Position - pos).sqrMagnitude;
                    if (d < best) { best = d; fire = st.Position; found = true; }
                }
                return found;
            }

            foreach (KeyValuePair<GroundCellKey, Vector3> kv in _remoteGroundCells)
            {
                Vector3 c = kv.Value;
                float d = (c - pos).sqrMagnitude;
                if (d < best) { best = d; fire = c; found = true; }
            }
            foreach (KeyValuePair<ZDOID, GameObject> kv in _remoteVfx)
            {
                if (kv.Value == null) continue;
                Vector3 c = kv.Value.transform.position; // the rig sits at the burner's position
                float d = (c - pos).sqrMagnitude;
                if (d < best) { best = d; fire = c; found = true; }
            }
            return found;
        }

        private void ResetRemoteMirror()
        {
            foreach (GameObject go in _remoteGroundVfx.Values) { if (go != null) Destroy(go); }
            _remoteGroundVfx.Clear();
            _remoteGroundCells.Clear();
            ClearPendingScorch();
            ValheimBridge.ResetScorchDiagnostics(); // the first five marks of the NEXT session are worth logging too
            _remoteVfxSpawnQueue.Clear();
            _remoteVfxQueuedKeys.Clear();
            // Paint assignments from a world we have left would land on the wrong ground.
            _pendingPaint.Clear();

            // The object half too: a world we have left must not leave fires, ids or clocks
            // behind, or those objects could never be drawn again after a reconnect.
            foreach (GameObject go in _remoteVfx.Values) { if (go != null) Destroy(go); }
            _remoteVfx.Clear();
            _remoteBurningIds.Clear();
            _remoteVfxSpawnedAt.Clear();
            _remoteSmouldering.Clear();
            _remoteAgeAtSync.Clear();
            _remoteSmoulderAtSync.Clear();
            _remoteObjectVfxQueue.Clear();
            _remoteObjectVfxQueued.Clear();
        }

        private void SpawnRemoteGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            // Note the null test. A plain ContainsKey would treat a destroyed instance as a live one
            // and refuse to redraw that cell ever again; ResetRemoteMirror catches the common cause
            // but this catches every other way a GameObject can go away underneath us.
            if (_remoteGroundVfx.TryGetValue(key, out GameObject existing))
            {
                if (existing != null) return;
                _remoteGroundVfx.Remove(key);
            }
            if (!FireConfig.UseProceduralVfx.Value)
            {
                FireLogger.Debug($"[IGNITE-TRACE] SpawnRemoteGroundVfxFor({key.X},{key.Z}): UseProceduralVfx is false on THIS peer, skipping.");
                return;
            }

            // Re-sample the height locally. The Y on the wire is the SERVER's, and on a dedicated
            // server it is not a terrain height at all: GetGroundHeight raycasts against terrain
            // colliders, a headless server has none, and its ZoneSystem fallback is the same raycast
            // wrapped - so it returns its own input. Every cell therefore inherits the Y of whatever
            // object seeded the fire and carries it unchanged across the entire spread, out to the
            // ground leash. On level ground nobody notices; on a slope the flames sink under the
            // hill or float above it, and the further the fire has travelled the worse it gets. THIS
            // machine has real terrain, so it asks its own. The same call falls back to the wire
            // value if the zone is not loaded yet, which is exactly the behaviour we want.
            Vector3 grounded = new Vector3(position.x, ValheimBridge.GetGroundHeight(position), position.z);

            GameObject instance = ValheimBridge.CreateProceduralGroundFireVfx(grounded);
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

            // Real dirt needs real terrain and exactly one writer per zone. A dedicated server has
            // no terrain where fires burn (it keeps real zones only around world origin; every
            // heightmap lookup returns null anywhere a player is, which is why this feature never
            // painted anything on one until 0.21.16), and letting every client paint what it sees
            // makes them create duplicate compilers and take each other's ownership, both of which
            // lose paint - or worse, a zone's real hoe work. So the server ELECTS one painter per
            // cell and tells it - see AssignPendingPaint; a listen host is a candidate like any
            // peer. Falls through to the decal on purpose: instant feedback, and a fallback if no
            // painter can reach the cell; the decal expires on its own, so nothing builds up.
            if (FireConfig.UseVanillaDirtPaint.Value)
                _paintAssignPending.Add((key, y));

            // A scorch mark is a bare mesh with no ZNetView, so it has never replicated to
            // anyone - the machine running the simulation was the only one that ever saw one.
            // On a dedicated server that machine draws nothing at all, so this built roughly
            // 1,875 invisible GameObjects per five minutes at stock settings. Clients now spawn
            // their own from the sync stream instead; see the expiry loop in HandleGroundFireSync.
            if (!FireConfig.EffectiveScorchMarksEnabled || ValheimBridge.IsDedicatedServer()) return;
            // The cell, unscaled - the spawner applies the 1.6-2.2x. See the remote expiry
            // path in HandleGroundFireSync for the 1.5 that used to sit here and what it did.
            float size = FireConfig.GroundCellSize.Value;
            QueueScorchMark(position, size, FireConfig.ScorchMarkLifetimeSeconds.Value);
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

            // The ramp is PER EVENT and so is the reach it scales. This used to call the
            // parameterless GetRampFraction(), which reads one global clock that only resets when
            // every fire on the server is out - so while any long-lived fire burned, a brand-new
            // torch fire started at FULL spread radius and the anti-explosion ramp was silently
            // off. The per-event overload was already used correctly for the concurrency caps;
            // reach now matches it, computed inside the loops from the id already in hand.
            //
            // This one stays global on purpose: it sizes a diagnostic log line, nothing else.
            float diagnosticRadius = FireConfig.EffectiveSpreadRadius * GetRampFraction();

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
            LogSpreadCandidateDiagnostic(diagnosticRadius * diagnosticRadius);

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

                float ramp = GetRampFraction(burnerState.EventId);
                float effectiveSpreadRadius = FireConfig.EffectiveSpreadRadius * ramp;
                float objRadiusSqr = effectiveSpreadRadius * effectiveSpreadRadius;
                float groundRadius = FireConfig.EffectiveGroundSpreadRadius * ramp;

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
                    GroundCellState cellState = _groundBurning[key];
                    float y = cellState.Y;
                    Vector3 origin = CellCenter(key, y);
                    float groundRadius = FireConfig.EffectiveGroundSpreadRadius * GetRampFraction(cellState.EventId);

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

                    IgniteZdoCandidatesNear(origin, groundRadius, cellState.EventId);
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
                // 1.0.2: TryIgnite's two refusals, asked here from the ZDO, before an instance is
                // built and claimed only to be refused. Otherwise every soaked tree (a Dousing Bomb
                // line) and every tree past the Ashlands edge within reach of a front was built,
                // taken from the player beside it and torn down again, every cycle.
                if (IsSoaked(candidate.Id)) continue;
                if (AshlandsBarsFireAt(candidate.Position)) continue;

                Component target = ValheimBridge.ComponentFromZdoid(candidate.Id);
                if (target == null) continue;
                TryIgnite(target);
            }
        }

        /// <summary>True while a doused object still refuses fire (see _dousedUntil).</summary>
        private bool IsSoaked(ZDOID id) => _dousedUntil.TryGetValue(id, out float wetUntil) && Time.time < wetUntil;

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