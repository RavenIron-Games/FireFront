using System;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// What happens to a tree the fire has killed: it is replaced IN PLACE by a charred husk of
    /// the same species, which a few seconds later either topples (vanilla's own felling
    /// physics, coal only) or stays standing as a blackened snag that also yields only coal
    /// when someone eventually chops it.
    /// </summary>
    /// <remarks>
    /// The whole lifecycle is carried in the charred tree's ZDO — flag, charred-at time, the
    /// collapse-or-stand FATE rolled once at replacement, when the collapse is due, which way
    /// it falls — so it replicates to every peer, survives a save/restart, and executes on
    /// whichever peer happens to own the tree when its time comes. That matters on a dedicated
    /// server, where the server usually has no GameObject for a tree at all and the nearest
    /// client owns it (ZDOMan.ReleaseNearbyZDOS hands persistent ZDOs to the peer whose active
    /// area holds them, within about two seconds of a server claim — read out of 1.0.15).
    ///
    /// Two vanilla facts shape the damage path (Decompiled_1.0.15 TreeBase.cs:113-125,
    /// DamageText.cs:184-192, Values_Dump): every tree and log has m_fire = Immune, so a fire
    /// HitData through RPC_Damage does nothing, and RPC_Damage's DamageText.ShowText is
    /// unconditional and routed to every peer within 30 m, so it can never be silent. The fire
    /// therefore sends a MARKED HitData (m_statusEffectHash, serialized and unused by trees) to
    /// the owner, where the RPC_Damage prefix recognises it and applies the damage itself:
    /// health only, no text, no shake, no hit effect. Health is a ZDO float, replicated to all
    /// peers, and is what the flames climb by.
    ///
    /// The death is handled by the server: the owner clamps health at <see cref="DeadHealth"/>
    /// (never at or below zero — TreeBase.Awake and RPC_Damage both delete a tree whose stored
    /// health is <= 0, TreeBase.cs:51-54,105-110), the server sees that on its next tick and
    /// performs the replacement ZDO-only, needing no instance.
    /// </remarks>
    public static class CharredTreeLifecycle
    {
        public static readonly int CharredHash = "FireFront_Charred".GetStableHashCode();
        public static readonly int CharredAtHash = "FireFront_CharredAt".GetStableHashCode();     // long, ZNet time ticks
        public static readonly int FateHash = "FireFront_Fate".GetStableHashCode();               // int, see Fate*
        public static readonly int CollapseAtHash = "FireFront_CollapseAt".GetStableHashCode();   // long, ZNet time ticks
        public static readonly int CrumbleAtHash = "FireFront_CrumbleAt".GetStableHashCode();     // long, ZNet time ticks (logs)
        public static readonly int FallDirHash = "FireFront_FallDir".GetStableHashCode();         // Vector3
        public static readonly int CoalDroppedHash = "FireFront_CoalDropped".GetStableHashCode(); // bool

        /// <summary>Marks a HitData as one of the fire's own unseen ticks (HitData.m_statusEffectHash is serialized and never read by trees).</summary>
        public static readonly int TickMarker = "FireFront.Tick".GetStableHashCode();

        public const int FateUndecided = 0;
        public const int FateCollapse = 1;
        public const int FateStand = 2;

        /// <summary>
        /// Where the owner parks a burned-out tree's health. Strictly above zero: vanilla deletes
        /// (with full drops) any tree whose stored health is <= 0 the moment it Awakes or is hit.
        /// </summary>
        public const float DeadHealth = 0.5f;

        public static long NowTicks => ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;

        public static float SecondsSince(long ticks) => (float)((NowTicks - ticks) / (double)TimeSpan.TicksPerSecond);

        public static bool IsCharred(ZDO zdo) => zdo != null && zdo.GetBool(CharredHash, false);

        public static bool IsCharred(ZNetView nv) => nv != null && nv.IsValid() && IsCharred(nv.GetZDO());

        // ---------------------------------------------------------------
        // Health
        // ---------------------------------------------------------------

        /// <summary>
        /// The health a full tree of this ZDO's species starts with. TreeBase reads its default
        /// UNSCALED (RPC_Damage falls back to m_health; only Awake's dead-check applies the world
        /// level and never writes it); TreeLog writes the scaled value into the ZDO at Awake.
        /// </summary>
        public static float MaxHealthOf(ZDO zdo)
        {
            if (zdo == null || ZNetScene.instance == null) return 0f;
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null) return 0f;
            TreeBase tb = prefab.GetComponent<TreeBase>();
            if (tb != null) return tb.m_health;
            TreeLog tl = prefab.GetComponent<TreeLog>();
            if (tl != null) return LogMaxHealth(tl.m_health);
            return 0f;
        }

        public static float LogMaxHealth(float baseHealth)
        {
            float mult = Game.instance != null ? Game.instance.m_worldLevelMineHPMultiplier : 0f;
            return baseHealth + Game.m_worldLevel * baseHealth * mult;
        }

        public static float HealthOf(ZDO zdo, float max) => zdo != null ? zdo.GetFloat(ZDOVars.s_health, max) : max;

        /// <summary>0 = untouched, 1 = burned to death. What the flames climb by.</summary>
        public static float BurntFraction(ZDO zdo, float max)
        {
            if (zdo == null || max <= 0f) return 0f;
            return Mathf.Clamp01(1f - HealthOf(zdo, max) / max);
        }

        // ---------------------------------------------------------------
        // Unseen damage tick (server -> owner)
        // ---------------------------------------------------------------

        public static HitData MakeTickHit(float damage, Vector3 point)
        {
            var hit = new HitData
            {
                m_hitType = HitData.HitType.CinderFire, // belt and braces for any other prefix that keys off it
                m_statusEffectHash = TickMarker,
                m_toolTier = 99,
                m_itemWorldLevel = (byte)Game.m_worldLevel,
                m_point = point,
                m_dir = Vector3.up,
                m_dodgeable = false,
                m_blockable = false,
            };
            hit.m_damage.m_fire = damage;
            return hit;
        }

        /// <summary>
        /// Deliver one tick. Routed to the ZDO's owner when a peer owns it (the prefix there
        /// applies it), written directly when nobody or this peer does. Never claims ownership:
        /// a server claim on a client-owned tree bounces back within seconds and races the
        /// owner's own writes.
        /// </summary>
        public static bool SendFireTick(ZDO zdo, float damage)
        {
            if (zdo == null || damage <= 0f) return false;
            long owner = zdo.GetOwner();
            long self = ZDOMan.GetSessionID();
            if (owner != 0L && owner != self)
            {
                if (ZRoutedRpc.instance == null) return false;
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_Damage", MakeTickHit(damage, zdo.GetPosition()));
                return true;
            }
            // Unowned, or ours: apply here. The instance, if any, sees the same ZDO.
            float max = MaxHealthOf(zdo);
            if (max <= 0f) return false;
            ApplyTickToZdo(zdo, damage, max);
            return true;
        }

        /// <summary>The one place a tick actually changes health. Shared by the direct path and the owner-side prefix.</summary>
        public static float ApplyTickToZdo(ZDO zdo, float damage, float max)
        {
            float cur = HealthOf(zdo, max);
            float next = cur - damage;
            if (next < DeadHealth) next = DeadHealth;
            zdo.Set(ZDOVars.s_health, next);
            return next;
        }

        public static bool IsBurnedOut(ZDO zdo, float max) => HealthOf(zdo, max) <= DeadHealth + 0.001f;

        // ---------------------------------------------------------------
        // Replacement in place (server)
        // ---------------------------------------------------------------

        /// <summary>
        /// Swap a dead tree or log for its charred twin at the same transform and scale, with
        /// the fate already rolled and baked into the new ZDO. Works with or without a local
        /// GameObject: on a host the clone is instantiated here (pre-baked ZDO, so the flag is
        /// visible in Awake); on a headless server the ZDO alone is created and every client
        /// in range instantiates it through the normal sync. Returns the fate, or
        /// <see cref="FateUndecided"/> if nothing could be done.
        /// </summary>
        public static int ReplaceWithCharred(ZDO oldZdo, out string prefabName)
        {
            prefabName = null;
            if (oldZdo == null || !oldZdo.IsValid() || ZNetScene.instance == null || ZDOMan.instance == null) return FateUndecided;

            int hash = oldZdo.GetPrefab();
            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            if (prefab == null) return FateUndecided;
            prefabName = prefab.name;
            ZNetView prefabView = prefab.GetComponent<ZNetView>();
            if (prefabView == null) return FateUndecided;
            bool isLog = prefab.GetComponent<TreeLog>() != null;

            ZNetView oldNv = ZNetScene.instance.FindInstance(oldZdo);
            Vector3 pos = oldNv != null ? oldNv.transform.position : oldZdo.GetPosition();
            Quaternion rot = oldNv != null ? oldNv.transform.rotation : oldZdo.GetRotation();

            float max = MaxHealthOf(oldZdo);
            bool collapse = UnityEngine.Random.Range(0f, 100f) < FireConfig.TreeDestructionRate.Value;
            long now = NowTicks;
            long delayTicks = (long)(Mathf.Max(0f, FireConfig.CharredCollapseDelaySeconds.Value) * TimeSpan.TicksPerSecond);

            ZDO z = ZDOMan.instance.CreateNewZDO(pos, hash);
            z.Persistent = prefabView.m_persistent;
            z.Type = prefabView.m_type;
            z.Distant = prefabView.m_distant;
            z.SetPrefab(hash);
            z.SetRotation(rot);

            // Vegetation trees carry a float scale scalar (wild firs run 1.5-3x), hand-placed
            // objects a Vec3; copy whichever the original had, or the clone comes up at 1.0.
            Vector3 scaleVec = oldZdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (scaleVec != Vector3.zero) z.Set(ZDOVars.s_scaleHash, scaleVec);
            else
            {
                float scalar = oldZdo.GetFloat(ZDOVars.s_scaleScalarHash, 1f);
                if (!Mathf.Approximately(scalar, 1f)) z.Set(ZDOVars.s_scaleScalarHash, scalar);
            }

            z.Set(CharredHash, true);
            z.Set(CharredAtHash, now);
            z.Set(FateHash, collapse ? FateCollapse : FateStand);
            z.Set(CollapseAtHash, now + delayTicks);
            z.Set(FallDirHash, PickFallDirection());
            if (max > 0f) z.Set(ZDOVars.s_health, Mathf.Max(1f, max * FireConfig.CharredTreeHealthFraction.Value));
            if (isLog)
            {
                // A log that burned down where it lay has no fall to make: "collapse" is a
                // crumble after the same delay a standing tree would take to topple, and the
                // roll decides whether it crumbles or stays, exactly as for a tree.
                z.Set(CrumbleAtHash, now + delayTicks);
            }

            // A host with a live instance swaps it this frame; the pre-baked ZDO means the
            // Awake postfix sees the flag on this peer too, not only on the others.
            if (oldNv != null)
            {
                ZNetView.m_useInitZDO = true;
                ZNetView.m_initZDO = z;
                try
                {
                    UnityEngine.Object.Instantiate(prefab, pos, rot);
                }
                catch (Exception ex)
                {
                    FireLogger.Warn($"[CHARRED] instantiating charred {prefabName} threw: {ex.Message} (the ZDO still exists; clients will build it).");
                }
                finally
                {
                    ZNetView.m_initZDO = null;
                    ZNetView.m_useInitZDO = false;
                }
            }

            // The original goes without ever entering vanilla's death branch: no felling, no
            // log, no stub, no seeds. ZNetView.Destroy is ZNetScene.Destroy, which needs the
            // ZDO owned here to broadcast the removal.
            if (oldNv != null)
            {
                oldNv.ClaimOwnership();
                oldNv.Destroy();
            }
            else
            {
                oldZdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(oldZdo);
            }

            FireLogger.Debug($"[CHARRED] {prefabName} at {pos} replaced by charred twin {z.m_uid}: fate={(collapse ? "collapse" : "stand")} " +
                             $"in {FireConfig.CharredCollapseDelaySeconds.Value}s, instance={(oldNv != null)}");
            return collapse ? FateCollapse : FateStand;
        }

        /// <summary>Downwind with a little scatter, so a burned stand does not fall in lockstep.</summary>
        public static Vector3 PickFallDirection()
        {
            Vector3 dir = Vector3.forward;
            if (EnvMan.instance != null)
            {
                Vector3 wind = EnvMan.instance.GetWindDir();
                wind.y = 0f;
                if (wind.sqrMagnitude > 0.01f) dir = wind.normalized;
            }
            float jitter = UnityEngine.Random.Range(-35f, 35f);
            return Quaternion.Euler(0f, jitter, 0f) * dir;
        }

        // ---------------------------------------------------------------
        // Collapse / crumble (whoever owns the charred object)
        // ---------------------------------------------------------------

        /// <summary>
        /// The charred snag falls: vanilla's own felling — the species' log prefab thrown from
        /// the log spawn point with the same impulse TreeBase.SpawnLog uses, so it tips, hits
        /// the ground with the real crash (ImpactEffect: sfx_tree_fall_hit, dust, camera shake)
        /// and replicates through ZSyncTransform — but the log is born charred, drops no wood,
        /// and no stub is left. Coal drops at the foot. Owner only.
        /// </summary>
        public static bool CollapseStandingTree(TreeBase tb, ZNetView nv, Vector3 fallDir)
        {
            if (tb == null || nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
            ZDO zdo = nv.GetZDO();
            Vector3 basePos = tb.transform.position;
            Vector3 scale = tb.transform.localScale;
            if (fallDir.sqrMagnitude < 0.01f) fallDir = PickFallDirection();
            fallDir.y = 0f;
            fallDir.Normalize();

            try
            {
                if (tb.m_logPrefab != null && tb.m_logSpawnPoint != null && ZDOMan.instance != null)
                {
                    int logHash = ValheimBridge.PrefabHashOf(tb.m_logPrefab.name);
                    ZNetView logView = tb.m_logPrefab.GetComponent<ZNetView>();
                    TreeLog logProto = tb.m_logPrefab.GetComponent<TreeLog>();
                    Vector3 logPos = tb.m_logSpawnPoint.position;
                    Quaternion logRot = tb.m_logSpawnPoint.rotation;

                    ZDO lz = ZDOMan.instance.CreateNewZDO(logPos, logHash);
                    lz.Persistent = logView == null || logView.m_persistent;
                    if (logView != null) { lz.Type = logView.m_type; lz.Distant = logView.m_distant; }
                    lz.SetPrefab(logHash);
                    lz.SetRotation(logRot);
                    long now = NowTicks;
                    lz.Set(CharredHash, true);
                    lz.Set(CharredAtHash, zdo.GetLong(CharredAtHash, now));
                    lz.Set(FateHash, FateStand); // the fall IS its fate; crumble is timed separately
                    lz.Set(CoalDroppedHash, true); // the tree paid its coal at the foot
                    if (logProto != null) lz.Set(ZDOVars.s_health, Mathf.Max(1f, LogMaxHealth(logProto.m_health) * FireConfig.CharredTreeHealthFraction.Value));
                    if (FireConfig.CharredLogCrumbleSeconds.Value > 0f)
                        lz.Set(CrumbleAtHash, now + (long)(FireConfig.CharredLogCrumbleSeconds.Value * TimeSpan.TicksPerSecond));

                    GameObject log;
                    ZNetView.m_useInitZDO = true;
                    ZNetView.m_initZDO = lz;
                    try { log = UnityEngine.Object.Instantiate(tb.m_logPrefab, logPos, logRot); }
                    finally { ZNetView.m_initZDO = null; ZNetView.m_useInitZDO = false; }

                    if (log != null)
                    {
                        ZNetView lnv = log.GetComponent<ZNetView>();
                        if (lnv != null && lnv.IsValid()) lnv.SetLocalScale(scale);
                        Rigidbody body = log.GetComponent<Rigidbody>();
                        if (body != null)
                        {
                            // TreeBase.SpawnLog, verbatim: mass scales with the tree, the shove
                            // lands 4 m up so the trunk pivots rather than slides.
                            body.mass *= scale.x;
                            body.ResetInertiaTensor();
                            body.AddForceAtPosition(fallDir * 0.2f * body.mass, log.transform.position + Vector3.up * 4f * scale.y, ForceMode.Impulse);
                        }
                        ImpactEffect impact = log.GetComponent<ImpactEffect>();
                        if (impact != null) impact.m_hitVariant = tb.m_treeStatType;
                    }
                }

                SpawnNetworkedEffect("sfx_tree_fall", basePos, tb.transform.rotation);
                SpawnAshBurst(basePos + Vector3.up * 0.5f);

                if (!zdo.GetBool(CoalDroppedHash, false))
                {
                    zdo.Set(CoalDroppedHash, true);
                    SpawnCoal(basePos, RollCoal());
                }
            }
            catch (Exception ex)
            {
                FireLogger.Warn($"[CHARRED] collapse of {tb.name} threw: {ex.Message}; the snag is removed without the fall.");
            }

            nv.Destroy();
            FireLogger.Debug($"[CHARRED] {tb.name} collapsed at {basePos} toward {fallDir}.");
            return true;
        }

        /// <summary>A charred log gives out: ash puff, coal if it still owes any, gone. Owner only.</summary>
        public static bool CrumbleLog(TreeLog tl, ZNetView nv)
        {
            if (tl == null || nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
            ZDO zdo = nv.GetZDO();
            Vector3 pos = tl.transform.position;
            try
            {
                SpawnAshBurst(pos);
                if (!zdo.GetBool(CoalDroppedHash, false))
                {
                    zdo.Set(CoalDroppedHash, true);
                    SpawnCoal(pos, RollCoal());
                }
            }
            catch (Exception ex)
            {
                FireLogger.Warn($"[CHARRED] crumble of {tl.name} threw: {ex.Message}.");
            }
            nv.Destroy();
            FireLogger.Debug($"[CHARRED] {tl.name} crumbled at {pos}.");
            return true;
        }

        public static int RollCoal()
        {
            int min = Mathf.Max(0, FireConfig.CharredCoalMin.Value);
            int max = Mathf.Max(min, FireConfig.CharredCoalMax.Value);
            return UnityEngine.Random.Range(min, max + 1);
        }

        /// <summary>
        /// A small stack of Coal as a networked pickup, the way vanilla drops items: plain
        /// Instantiate of the item prefab (its ZNetView creates a persistent ZDO on any peer,
        /// server included) and SetStack, which saves the count into the ZDO synchronously.
        /// </summary>
        public static void SpawnCoal(Vector3 basePos, int count)
        {
            if (count <= 0 || ZNetScene.instance == null) return;
            GameObject coal = ZNetScene.instance.GetPrefab("Coal");
            if (coal == null)
            {
                FireLogger.Warn("[CHARRED] Coal prefab not registered; charred tree dropped nothing.");
                return;
            }
            Vector2 j = UnityEngine.Random.insideUnitCircle * 0.6f;
            Vector3 pos = basePos + Vector3.up * 0.6f + new Vector3(j.x, 0f, j.y);
            GameObject go = UnityEngine.Object.Instantiate(coal, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f));
            ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null) return;
            drop.SetStack(count);
            ItemDrop.OnCreateNew(drop);
        }

        /// <summary>
        /// One-shot vanilla effect prefab. These carry a non-persistent ZNetView, so ONE spawn
        /// on the owner is heard/seen by every peer in range; never spawn them per client.
        /// </summary>
        public static void SpawnNetworkedEffect(string prefabName, Vector3 pos, Quaternion rot)
        {
            if (ZNetScene.instance == null) return;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) return;
            UnityEngine.Object.Instantiate(prefab, pos, rot);
        }

        /// <summary>A grey puff where charred wood gives out. Vanilla's tree-fall dust, networked like the rest.</summary>
        public static void SpawnAshBurst(Vector3 pos)
        {
            SpawnNetworkedEffect("vfx_tree_fall_hit", pos, Quaternion.identity);
        }
    }
}
