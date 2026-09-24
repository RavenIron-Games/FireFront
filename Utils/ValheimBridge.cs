using System.Collections.Generic;
using System.Reflection;
using FireFront.Fire;
using UnityEngine;

namespace FireFront.Utils
{
    /// <summary>
    /// The ONLY file (besides Patches/) allowed to touch vanilla fields/methods.
    ///
    /// IMPORTANT: assembly_valheim_publicized.dll is only public at COMPILE time.
    /// The actual DLL Valheim loads at runtime is the real, non-publicized game
    /// assembly — its private fields are still private there. A direct field
    /// access compiles fine against the publicized reference but throws
    /// FieldAccessException at runtime. Every vanilla field touch below goes
    /// through reflection (GetValue/SetValue/Invoke), which bypasses the CLR's
    /// runtime accessibility check. Confirmed public at runtime (accessed
    /// directly, no reflection needed): HitData.m_damage, DamageTypes.m_fire.
    ///
    /// Handles three burnable target types with different underlying vanilla
    /// components: WearNTear (structures), TreeBase (standing trees), TreeLog
    /// (felled logs). None share a base type/interface in vanilla, so this
    /// class branches on the concrete type.
    ///
    /// When Valheim 1.0 lands (Sept 2026), re-run net_meta.py against the new
    /// publicized DLLs and re-verify field/method names HERE only.
    /// </summary>
    public static class ValheimBridge
    {
        /// <summary>
        /// Targets currently being killed via a synthetic RPC_Damage call (see
        /// KillBurningTarget's Tree case). While a target is in here, the
        /// ignition patches ignore hits on it — otherwise our own lethal
        /// kill-shot re-enters RPC_Damage and re-ignites the tree we're trying
        /// to finish off, looping forever instead of ever actually destroying it.
        /// </summary>
        public static readonly HashSet<Component> SuppressIgnition = new HashSet<Component>();

        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // --- WearNTear (Piece) ---
        private static readonly FieldInfo AllInstancesField = typeof(WearNTear).GetField("s_allInstances", AnyStatic);
        private static readonly FieldInfo BurnableField = typeof(WearNTear).GetField("m_burnable", AnyInstance);
        private static readonly FieldInfo WntNviewField = typeof(WearNTear).GetField("m_nview", AnyInstance);
        private static readonly FieldInfo PieceField = typeof(WearNTear).GetField("m_piece", AnyInstance);
        private static readonly FieldInfo PieceNameField = typeof(Piece).GetField("m_name", AnyInstance);
        private static readonly MethodInfo WntDestroyMethod =
            typeof(WearNTear).GetMethod("Destroy", AnyInstance, null, new[] { typeof(HitData), typeof(bool) }, null);

        // --- TreeBase (standing trees) ---
        private static readonly FieldInfo TreeNviewField = typeof(TreeBase).GetField("m_nview", AnyInstance);
        private static readonly MethodInfo TreeSpawnLogMethod =
            typeof(TreeBase).GetMethod("SpawnLog", AnyInstance, null, new[] { typeof(Vector3) }, null);

        // --- TreeLog (felled logs) ---
        private static readonly FieldInfo LogNviewField = typeof(TreeLog).GetField("m_nview", AnyInstance);
        private static readonly MethodInfo LogDestroyMethod =
            // 1.0.7: Destroy(HitData) -> Destroy(HitData, bool cheatedTool). No default on the
            // new parameter, so the old types array simply stopped matching and felled logs
            // stopped being removed.
            typeof(TreeLog).GetMethod("Destroy", AnyInstance, null,
                new[] { typeof(HitData), typeof(bool) }, null);

        // --- Vanilla's own "Fire" gameplay class (Ashlands wildfire component) ---
        // Used ONLY for the emergency purge command — see PurgeAllVanillaFireInstances.
        private static readonly System.Type VanillaFireType = typeof(WearNTear).Assembly.GetType("Fire");

        // --- Shared singletons for raycast/position ---
        private static readonly FieldInfo GameCameraInstanceField = typeof(GameCamera).GetField("m_instance", AnyStatic);
        private static readonly FieldInfo GameCameraCameraField = typeof(GameCamera).GetField("m_camera", AnyInstance);
        private static readonly FieldInfo LocalPlayerField = typeof(Player).GetField("m_localPlayer", AnyStatic);

        // --- ZNetScene prefab listing (for firelistprefabs) ---
        private static readonly FieldInfo ZNetScenePrefabsField = typeof(ZNetScene).GetField("m_prefabs", AnyInstance);
        private static readonly FieldInfo ZNetSceneInstanceField = typeof(ZNetScene).GetField("s_instance", AnyStatic);

        // -----------------------------------------------------------------
        // Type identification
        // -----------------------------------------------------------------

        public static BurnKind KindOf(Component target)
        {
            if (target is WearNTear) return BurnKind.Piece;
            if (target is TreeLog) return BurnKind.Log;
            if (target is TreeBase) return BurnKind.Tree;
            return BurnKind.Unknown;
        }

        // -----------------------------------------------------------------
        // Piece (WearNTear) — unchanged from 0.1.0, proven working
        // -----------------------------------------------------------------

        /// <summary>All placed WearNTear pieces currently loaded. Vanilla-maintained static list.</summary>
        public static List<WearNTear> AllPieces =>
            AllInstancesField?.GetValue(null) as List<WearNTear> ?? new List<WearNTear>();

        // -----------------------------------------------------------------
        // Generic target operations — dispatch by BurnKind
        // -----------------------------------------------------------------

        public static bool IsAlive(Component target)
        {
            if (target == null) return false;
            switch (KindOf(target))
            {
                case BurnKind.Piece:
                    return AsZNetView(WntNviewField, target) is ZNetView p && p.IsValid();
                case BurnKind.Tree:
                    return AsZNetView(TreeNviewField, target) is ZNetView t && t.IsValid();
                case BurnKind.Log:
                    return AsZNetView(LogNviewField, target) is ZNetView l && l.IsValid();
                default:
                    return target != null; // fallback: Unity null-check
            }
        }

        private static ZNetView AsZNetView(FieldInfo field, Component target) =>
            field?.GetValue(target) as ZNetView;

        // ZNetScene.CreateObject(ZDO) hit the identical publicized-DLL-vs-real-
        // assembly access lie we already found and fixed for
        // ZRoutedRpc.GetServerPeerID — IL-flagged public in the reference DLL,
        // throws MethodAccessException against the real game assembly at
        // runtime. Confirmed by a live dedicated-server test. Same reflection
        // fix, same reasoning.
        // ZNet.LocalPlayerIsAdminOrHost() had the same IL-public-but-throws-at-
        // runtime shape as GetServerPeerID and CreateObject — reflecting it
        // defensively rather than calling it directly.
        private static readonly MethodInfo ZNetLocalPlayerIsAdminMethod =
            typeof(ZNet).GetMethod("LocalPlayerIsAdminOrHost", AnyInstance, null, System.Type.EmptyTypes, null);

        /// <summary>
        /// True if the LOCAL peer running this code is a server admin or the
        /// host. Used to gate dev/debug console commands (fireignite, stopfire,
        /// clearfires, etc.) — NOT the normal fire-arrow ignition path, which
        /// stays open to every player since that's just the mod working as
        /// intended. Defaults to false (deny) if the reflection lookup or
        /// ZNet.instance isn't available, rather than failing open.
        /// </summary>
        public static bool IsLocalPlayerAdmin()
        {
            if (ZNet.instance == null || ZNetLocalPlayerIsAdminMethod == null) return false;
            try
            {
                return (bool)ZNetLocalPlayerIsAdminMethod.Invoke(ZNet.instance, null);
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[IGNITE-TRACE] IsLocalPlayerAdmin reflection invoke threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        private static readonly MethodInfo ZNetSceneCreateObjectMethod =
            typeof(ZNetScene).GetMethod("CreateObject", AnyInstance, null, new[] { typeof(ZDO) }, null);

        private static GameObject CreateObjectReflected(ZNetScene scene, ZDO zdo)
        {
            if (ZNetSceneCreateObjectMethod == null) return null;
            return ZNetSceneCreateObjectMethod.Invoke(scene, new object[] { zdo }) as GameObject;
        }

        /// <summary>Resolves a burnable target's ZNetView regardless of BurnKind, or null.</summary>
        public static ZNetView ZNetViewOf(Component target)
        {
            if (target == null) return null;
            switch (KindOf(target))
            {
                case BurnKind.Piece: return AsZNetView(WntNviewField, target);
                case BurnKind.Tree: return AsZNetView(TreeNviewField, target);
                case BurnKind.Log: return AsZNetView(LogNviewField, target);
                default: return null;
            }
        }

        /// <summary>
        /// The target's ZDOID, for sending over the wire (RPC params can't carry
        /// Component references — ZDOID is the standard cross-peer identifier).
        /// Null if the target has no valid ZNetView.
        /// </summary>
        public static ZDOID? ZDOIDOf(Component target)
        {
            ZNetView nv = ZNetViewOf(target);
            return (nv != null && nv.IsValid()) ? nv.GetZDO().m_uid : (ZDOID?)null;
        }

        /// <summary>
        /// Reverse of ZDOIDOf: resolves a received ZDOID back to the live burnable
        /// Component on this peer, or null if that object isn't loaded/instanced
        /// here (e.g. a client far from the fire). Confirmed via the publicized
        /// DLL: ZNetScene has two FindInstance overloads — FindInstance(ZDO)
        /// returns ZNetView, but FindInstance(ZDOID) (the one we need, since RPC
        /// params carry ZDOID not a live ZDO reference) returns GameObject.
        /// </summary>
        // -----------------------------------------------------------------
        // ZDO-level burnable scan — headless-server spread support
        // -----------------------------------------------------------------
        // [SPREAD-DIAGNOSTIC] on the live dedicated server (0.17.3) proved the
        // instance scans (WearNTear.AllPieces, FindObjectsOfType<TreeBase>) see
        // NOTHING there — count 0 with a forest actively burning around the
        // player — because a headless server tracks world objects as ZDOs
        // without instantiating GameObjects (the same finding ComponentFromZdoid
        // documents for single targets). Spread candidates must therefore come
        // from the ZDO layer; instances are force-created per actual ignition,
        // never per nearby candidate.

        /// <summary>A burnable world object as the ZDO layer sees it — may have no GameObject on this peer.</summary>
        public struct ZdoBurnable
        {
            public ZDOID Id;
            public Vector3 Position;
            public int PrefabHash;   // 0.21.6: lets a caller match a stored prefab name without a second lookup
        }

        // 1.0.7 retyped this whole call: the sector is a Vector2s now, and the two ring
        // integers collapsed into one SimulationDistance. It is looked up by explicit types, so
        // the change did not warn — the lookup returned null and the burnable-object scan that
        // feeds ground fire silently found nothing at all.
        private static readonly MethodInfo ZdoManFindSectorObjectsMethod =
            typeof(ZDOMan).GetMethod("FindSectorObjects", AnyInstance, null,
                new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) }, null);
        private static readonly FieldInfo ZNetSceneNamedPrefabsField =
            typeof(ZNetScene).GetField("m_namedPrefabs", AnyInstance);

        // Prefab hash -> true when the prefab is a tree or log (so the
        // BurnTreesAndLogs gate can apply at scan time), false when it's a
        // burnable piece. Built once — the prefab set is fixed for the session,
        // and prefabs are loaded assets that exist fine on a headless server.
        private static Dictionary<int, bool> _burnablePrefabKinds;
        private static bool _zdoScanFailureLogged;
        private static readonly List<ZDO> _zdoScanScratch = new List<ZDO>(512);

        private static Dictionary<int, bool> EnsureBurnablePrefabKinds()
        {
            if (_burnablePrefabKinds != null) return _burnablePrefabKinds;

            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return null; // world not up yet — retry next call
            var named = ZNetSceneNamedPrefabsField?.GetValue(scene) as Dictionary<int, GameObject>;
            if (named == null || named.Count == 0)
            {
                if (!_zdoScanFailureLogged)
                {
                    _zdoScanFailureLogged = true;
                    FireLogger.Debug("[ZDO-SCAN] m_namedPrefabs unreadable or empty — ZDO-layer spread candidates unavailable.");
                }
                return null;
            }

            var kinds = new Dictionary<int, bool>();
            foreach (KeyValuePair<int, GameObject> kv in named)
            {
                GameObject prefab = kv.Value;
                if (prefab == null) continue;
                if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null)
                {
                    kinds[kv.Key] = true;
                    continue;
                }
                WearNTear wnt = prefab.GetComponent<WearNTear>();
                if (wnt != null && BurnableField != null && (bool)BurnableField.GetValue(wnt))
                    kinds[kv.Key] = false;
            }
            _burnablePrefabKinds = kinds;
            FireLogger.Debug($"[ZDO-SCAN] burnable prefab table built: {kinds.Count} prefab hashes.");
            return kinds;
        }

        /// <summary>
        /// Insert a runtime-created prefab into ZNetScene's name-hash registry
        /// so network spawns (dropped items, etc.) can resolve it. Idempotent.
        /// </summary>
        public static void RegisterPrefabWithZNetScene(ZNetScene scene, GameObject prefab)
        {
            if (scene == null || prefab == null) return;
            var named = ZNetSceneNamedPrefabsField?.GetValue(scene) as Dictionary<int, GameObject>;
            if (named == null)
            {
                FireLogger.Warn($"[DOUSING] m_namedPrefabs unreadable — '{prefab.name}' not visible to network spawns.");
                return;
            }
            int hash = prefab.name.GetStableHashCode();
            if (!named.ContainsKey(hash)) named.Add(hash, prefab);
        }

        // Placement stamps the placer's player id into the ZDO under this hash
        // (verified in the decompiled Piece: m_creator = zdo.GetLong(s_creator),
        // IsPlacedByPlayer() == creator != 0). Reading it at the ZDO layer means
        // the player-built check works headless, before any instance exists.
        private static readonly int CreatorZdoHash = "creator".GetStableHashCode();

        /// <summary>True if this object carries a player's creator stamp — a built piece, not world-generated.</summary>
        public static bool IsPlayerBuilt(Component target)
        {
            ZNetView nv = ZNetViewOf(target);
            if (nv == null || !nv.IsValid()) return false;
            return nv.GetZDO().GetLong(CreatorZdoHash, 0L) != 0L;
        }

        /// <summary>
        /// Collect every burnable object the ZDO layer knows about within
        /// radius of center, whether or not a GameObject exists for it on this
        /// peer. Verified against the decompiled DLL: ZDOMan.FindSectorObjects
        /// takes a zone coordinate plus a ring count (zones are 64m square),
        /// and ZoneSystem.GetZone(Vector3) is public static.
        /// includePlayerBuildings=false drops any piece carrying a creator
        /// stamp — world-generated WearNTear (ruins, dungeon chests) stays in.
        /// </summary>
        /// <returns>
        /// True if the ZDO layer was actually readable and the sweep ran. False
        /// means the caller is blind here and must fall back to instance scans —
        /// which is the ONLY case those scans are still worth their cost, since
        /// this sweep otherwise finds the same trees, logs and pieces far more
        /// cheaply (see BuildCandidateList).
        /// </returns>
        public static bool CollectBurnableZdosNear(Vector3 center, float radius, bool includeTreesAndLogs, bool includePlayerBuildings, List<ZdoBurnable> into)
        {
            into.Clear();

            ZDOMan man = ZDOMan.instance;
            if (man == null || ZdoManFindSectorObjectsMethod == null)
            {
                if (!_zdoScanFailureLogged)
                {
                    _zdoScanFailureLogged = true;
                    FireLogger.Debug($"[ZDO-SCAN] unavailable (ZDOMan null: {man == null}, " +
                                     $"FindSectorObjects null: {ZdoManFindSectorObjectsMethod == null}) — " +
                                     "spread falls back to instance candidates only.");
                }
                return false;
            }

            Dictionary<int, bool> kinds = EnsureBurnablePrefabKinds();
            if (kinds == null || kinds.Count == 0) return false;

            _zdoScanScratch.Clear();
            int rings = Mathf.Max(1, Mathf.CeilToInt(radius / 64f));
            try
            {
                // GetZone already returns the Vector2s 1.0.7 wants. classic:true is what keeps
                // this a full square sweep of `rings` rings with no distant pass — without it
                // the near ring gets radius-filtered and the distant loop re-walks it.
                ZdoManFindSectorObjectsMethod.Invoke(man,
                    new object[] { ZoneSystem.GetZone(center), new SimulationDistance(rings, 0, true), _zdoScanScratch, null });
            }
            catch (System.Exception ex)
            {
                if (!_zdoScanFailureLogged)
                {
                    _zdoScanFailureLogged = true;
                    FireLogger.Debug($"[ZDO-SCAN] FindSectorObjects threw: {ex.InnerException?.Message ?? ex.Message}");
                }
                return false;
            }

            float radiusSqr = radius * radius;
            for (int i = 0; i < _zdoScanScratch.Count; i++)
            {
                ZDO zdo = _zdoScanScratch[i];
                if (zdo == null) continue;
                if (!kinds.TryGetValue(zdo.GetPrefab(), out bool isTreeOrLog)) continue;
                if (isTreeOrLog && !includeTreesAndLogs) continue;
                if (isTreeOrLog && FireFront.Fire.CharredTreeLifecycle.IsCharred(zdo)) continue; // spent fuel
                if (!isTreeOrLog && !includePlayerBuildings && zdo.GetLong(CreatorZdoHash, 0L) != 0L) continue;

                Vector3 pos = zdo.GetPosition();
                if ((pos - center).sqrMagnitude > radiusSqr) continue;
                into.Add(new ZdoBurnable { Id = zdo.m_uid, Position = pos, PrefabHash = zdo.GetPrefab() });
            }

            return true;
        }

        /// <summary>
        /// True if the ZDO (network data) still exists for this ZDOID — the
        /// key distinction between "really destroyed" (chopped down, burned by
        /// something else, etc.) and "just de-instantiated" (the server tore
        /// down the local GameObject but the object's data is still tracked).
        /// Both cases fire the same OnDestroy event on a dedicated server,
        /// since de-instantiation there literally is destroy-then-later-
        /// recreate — this is the only reliable way to tell them apart.
        /// </summary>
        public static bool ZdoExists(ZDOID id) => ZDOMan.instance?.GetZDO(id) != null;

        /// <summary>
        /// 1.0.2: true when <paramref name="id"/> names a ZDO fire could burn: its prefab is in the
        /// burnable table (trees, logs, burnable pieces) and it is not charred already. Reads the ZDO
        /// only, so it builds nothing and claims nothing; the RPC handlers ask this before they let
        /// a client-supplied id anywhere near <see cref="ComponentFromZdoid"/>. False when the table
        /// cannot be read yet: a request from the wire is refused, never guessed at.
        /// </summary>
        public static bool TryGetBurnableZdo(ZDOID id, out Vector3 position)
        {
            position = Vector3.zero;
            if (id == ZDOID.None) return false;
            ZDO zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null) return false;
            Dictionary<int, bool> kinds = EnsureBurnablePrefabKinds();
            if (kinds == null || !kinds.TryGetValue(zdo.GetPrefab(), out bool isTreeOrLog)) return false;
            if (isTreeOrLog && FireFront.Fire.CharredTreeLifecycle.IsCharred(zdo)) return false;
            position = zdo.GetPosition();
            return true;
        }

        /// <summary>
        /// The burnable Component of a ZDOID's LOCAL instance, or null — never creates one and
        /// never touches ownership, unlike <see cref="ComponentFromZdoid"/>. This is the lookup
        /// for anything cosmetic on a client: a fire drawn on a tree must not steal the tree.
        /// </summary>
        public static Component InstanceComponentOf(ZDOID id)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || id == ZDOID.None) return null;
            GameObject go = scene.FindInstance(id);
            if (go == null) return null;
            Component c = go.GetComponent<WearNTear>();
            if (c != null) return c;
            c = go.GetComponent<TreeBase>();
            if (c != null) return c;
            return go.GetComponent<TreeLog>();
        }

        public static Component ComponentFromZdoid(ZDOID id)
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
            {
                FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): ZNetScene.instance is null.");
                return null;
            }

            GameObject go = scene.FindInstance(id);
            if (go == null)
            {
                // Confirmed via live dedicated-server testing on BOTH trees and
                // player-built pieces: a headless server doesn't automatically
                // instantiate a local GameObject for most world objects — it
                // just tracks their ZDO (data) for sync/persistence. Waiting
                // longer never helped (retried 20x over 10s, still nothing).
                // The actual fix is forcing instantiation on demand via
                // ZNetScene.CreateObject(ZDO), since we already know the ZDO
                // itself exists. Safe to call repeatedly — FindInstance above
                // will find the now-real instance on any subsequent call.
                ZDO zdo = ZDOMan.instance?.GetZDO(id);
                if (zdo == null)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): ZDO not found in ZDOMan either — nothing to instantiate.");
                    return null;
                }

                // 1.0.2: only ever for something fire can burn. Every caller means a tree, a log or
                // a burnable piece, and building anything else here also takes its ownership: a
                // forged ignite request once made the server build, claim and then delete another
                // player's character. Unknown when the prefab table cannot be read (no ZNetScene
                // names yet), and then the old behaviour stands rather than fire stopping altogether.
                Dictionary<int, bool> kinds = EnsureBurnablePrefabKinds();
                if (kinds != null && !kinds.ContainsKey(zdo.GetPrefab()))
                {
                    FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): prefab {zdo.GetPrefab()} is not burnable — not creating it.");
                    return null;
                }

                FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): ZDO exists but no local instance — forcing CreateObject.");
                try
                {
                    go = CreateObjectReflected(scene, zdo);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): CreateObject threw: {ex.InnerException?.Message ?? ex.Message}");
                    return null;
                }

                if (go == null)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): CreateObject returned null too.");
                    return null;
                }

                // Untested theory, worth trying first before a bigger refactor:
                // a force-created object that nobody owns might not register as
                // "actively needed" to whatever server-side housekeeping decides
                // what stays instantiated, and gets torn back down almost
                // immediately (matches the observed symptom — HandleTargetRemoved
                // firing right after ignition with no natural expiry ever
                // happening). Claiming ownership is the same thing
                // TerrainComp's paint path already does for the same reason.
                ZNetView createdNv = go.GetComponent<ZNetView>();
                if (createdNv != null && createdNv.IsValid())
                {
                    ClaimOwnershipIfNeeded(createdNv);
                    FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): claimed ownership on the force-created instance.");
                }
            }

            Component c = go.GetComponent<WearNTear>();
            if (c != null) return c;
            c = go.GetComponent<TreeBase>();
            if (c != null) return c;
            c = go.GetComponent<TreeLog>();
            if (c != null) return c;

            FireLogger.Debug($"[IGNITE-TRACE] ComponentFromZdoid({id}): found/created GameObject " +
                              $"'{go.name}' but it has none of WearNTear/TreeBase/TreeLog.");
            return null;
        }

        /// <summary>
        /// Pieces respect vanilla m_burnable. Trees/logs have no equivalent flag —
        /// wood is inherently flammable — gated by the BurnTreesAndLogs config toggle.
        /// </summary>
        public static bool IsBurnable(Component target)
        {
            if (target == null) return false;
            switch (KindOf(target))
            {
                case BurnKind.Piece:
                    // Anti-grief switch: with BurnPlayerBuildings off, anything a
                    // player placed is fireproof no matter how the ignition tried
                    // to happen (spread, arrows, console) — this is the single
                    // gate every path funnels through. World-generated WearNTear
                    // (ruins, dungeon furniture) carries no creator stamp and
                    // keeps burning.
                    if (!FireFront.Config.FireConfig.BurnPlayerBuildings.Value && IsPlayerBuilt(target)) return false;
                    return BurnableField != null && (bool)BurnableField.GetValue(target);
                case BurnKind.Tree:
                case BurnKind.Log:
                    if (!FireFront.Config.FireConfig.BurnTreesAndLogs.Value) return false;
                    // Charred wood is spent fuel: a burned snag or a fallen charred trunk never
                    // catches again, however hard the fire around it burns.
                    return !FireFront.Fire.CharredTreeLifecycle.IsCharred(target.GetComponent<ZNetView>());
                default:
                    return false;
            }
        }

        public static Vector3 PositionOf(Component target) => target.transform.position;

        /// <summary>
        /// Raw registered-prefab name for target's GameObject, with Unity's runtime
        /// "(Clone)" suffix stripped — this is what FindPrefabByName expects, unlike
        /// NameOf() below which is display-oriented and left as the live instance
        /// name (including "(Clone)") for logging purposes.
        /// </summary>
        public static string PrefabNameOf(Component target)
        {
            if (target == null) return null;
            string name = target.gameObject.name;
            const string suffix = "(Clone)";
            return name.EndsWith(suffix, System.StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        public static string NameOf(Component target)
        {
            if (target == null) return "<null>";
            if (KindOf(target) == BurnKind.Piece && PieceField != null)
            {
                Piece piece = PieceField.GetValue(target) as Piece;
                if (piece != null && PieceNameField != null)
                {
                    string name = PieceNameField.GetValue(piece) as string;
                    if (!string.IsNullOrEmpty(name)) return name; // TODO(Localization): raw token
                }
            }
            return target.gameObject.name;
        }

        /// <summary>
        /// Finish off a burned-down target the vanilla way for its type:
        ///   Piece  -> WearNTear.Destroy(null, false) directly (proven working in 0.1.0)
        ///   Log    -> TreeLog.Destroy(null) directly (same null-hit pattern)
        ///   Tree   -> SpawnLog(hitDir) for real drops/felling, then ZNetView.Destroy()
        ///             as a fallback if the tree is somehow still standing after.
        /// </summary>
        public static void KillBurningTarget(Component target)
        {
            if (!IsAlive(target)) return;

            switch (KindOf(target))
            {
                case BurnKind.Piece:
                {
                    ZNetView pieceNv = AsZNetView(WntNviewField, target);
                    ClaimOwnershipIfNeeded(pieceNv);
                    if (WntDestroyMethod != null) WntDestroyMethod.Invoke(target, new object[] { null, false });
                    else LogKillReflectionFailureOnce("WearNTear.Destroy(HitData, bool)");
                    // The same fallback the Tree branch has always had. Without it a failed
                    // reflection lookup was completely silent: the caller has already dropped its
                    // bookkeeping and told every client the fire went out, so the object stays
                    // standing and untouched while the mod believes it burned down.
                    if (IsAlive(target) && pieceNv != null) pieceNv.Destroy();
                    break;
                }

                case BurnKind.Log:
                {
                    ZNetView logNv = AsZNetView(LogNviewField, target);
                    ClaimOwnershipIfNeeded(logNv);
                    // cheatedTool: false — 1.0.7's added parameter; false is the honest path.
                    if (LogDestroyMethod != null) LogDestroyMethod.Invoke(target, new object[] { null, false });
                    else LogKillReflectionFailureOnce("TreeLog.Destroy(HitData, bool)");
                    if (IsAlive(target) && logNv != null) logNv.Destroy();
                    break;
                }

                case BurnKind.Tree:
                    // 0.22.0: the fire no longer kills trees through here — a burned tree is
                    // charred in place (CharredTreeLifecycle) and never felled with real wood.
                    // This branch remains for the dev commands and the last-resort path when
                    // charring throws, and still does what it did: vanilla felling.
                    //
                    // 0.2.3 used ZNetScene.Destroy(gameObject) here directly, which
                    // does NOT properly deregister the object from ZNetScene's
                    // internal near/distant tracking lists — it left a dangling
                    // entry that made ZNetScene.Update() throw NullReferenceException
                    // every single frame afterward (a corrupted vanilla core system,
                    // not just cosmetic). Fixed in 0.3.2: use ZNetView's own
                    // Destroy() instance method for removal.
                    //
                    // 0.7.x: call SpawnLog(hitDir) FIRST — this is vanilla's own
                    // felling method (used for a normal axe-chop), so it should
                    // handle real drops/logs correctly without us hand-rolling
                    // item spawning ourselves (which would mean reaching into
                    // ZNetScene.Instantiate for networked pickups — a new risk
                    // category best avoided if vanilla's own method works).
                    // hitDir's exact required type is a best guess (Vector3,
                    // matching the usual Unity convention) — if drops don't
                    // appear, this guess needs re-verifying. Re-check IsAlive
                    // after SpawnLog before falling back to direct destroy,
                    // since SpawnLog may already remove the tree itself as
                    // part of normal felling — calling destroy on an
                    // already-gone object is exactly the kind of mistake
                    // that's bitten this project before.
                    ZNetView treeNv = AsZNetView(TreeNviewField, target);
                    if (treeNv != null && !treeNv.IsOwner()) treeNv.ClaimOwnership();

                    TreeSpawnLogMethod?.Invoke(target, new object[] { Vector3.up });

                    if (IsAlive(target) && treeNv != null)
                    {
                        treeNv.Destroy();
                    }
                    break;
            }
        }

        private static readonly HashSet<string> _killFailuresLogged = new HashSet<string>();

        /// <summary>
        /// Says so, once, when a burn-down could not use vanilla's own destruction path. This
        /// used to be a bare `?.Invoke` with no else branch, which meant a reflection break
        /// showed up as objects that burned in the log and never moved in the world.
        /// </summary>
        private static void LogKillReflectionFailureOnce(string member)
        {
            if (!_killFailuresLogged.Add(member)) return;
            FireLogger.Warn($"Could not resolve {member}, so burnt-down objects of that kind are being " +
                            "removed through ZNetView.Destroy instead. They still disappear; their drops " +
                            "may differ from a normal destruction.");
        }

        private static void ClaimOwnershipIfNeeded(ZNetView nv)
        {
            if (nv != null && !nv.IsOwner()) nv.ClaimOwnership();
        }

        /// <summary>Fire component of the incoming hit, before resists. Confirmed public at runtime.</summary>
        public static float FireDamageOf(HitData hit) => hit != null ? hit.m_damage.m_fire : 0f;

        // -----------------------------------------------------------------
        // Camera / player
        // -----------------------------------------------------------------

        /// <summary>Piece/tree/log under the local player's crosshair, if any.</summary>
        public static Component RaycastBurnable(float maxDistance = 50f)
        {
            GameCamera cam = GameCameraInstanceField?.GetValue(null) as GameCamera;
            if (cam == null) return null;
            Camera camera = GameCameraCameraField?.GetValue(cam) as Camera;
            if (camera == null) return null;

            Transform camTransform = camera.transform;
            if (!Physics.Raycast(camTransform.position, camTransform.forward, out RaycastHit hit, maxDistance))
                return null;
            if (hit.collider == null) return null;

            Component c = hit.collider.GetComponentInParent<WearNTear>();
            if (c != null) return c;
            c = hit.collider.GetComponentInParent<TreeLog>();
            if (c != null) return c;
            c = hit.collider.GetComponentInParent<TreeBase>();
            return c;
        }

        // While a relayed command executes on the headless server, "the local
        // player" means the REQUESTING player — the server has none of its own.
        // Set from the requester's peer refPos (server-tracked, not client-
        // claimed) around the handler invocation; see ExecuteRelayed.
        private static Vector3? _positionOverride;
        public static void SetPositionOverride(Vector3? position) => _positionOverride = position;

        public static Vector3? LocalPlayerPosition()
        {
            if (_positionOverride.HasValue) return _positionOverride;
            Player local = LocalPlayerField?.GetValue(null) as Player;
            return local != null ? local.transform.position : (Vector3?)null;
        }

        // -----------------------------------------------------------------
        // Prefab lookup (for firelistprefabs dev command)
        // -----------------------------------------------------------------

        /// <summary>All registered GameObject prefab names whose name contains the filter (case-insensitive).</summary>
        public static List<string> FindPrefabNamesContaining(string filter)
        {
            var result = new List<string>();
            List<GameObject> prefabs = AllPrefabs();
            string needle = (filter ?? "").ToLowerInvariant();
            foreach (GameObject go in prefabs)
            {
                if (go == null) continue;
                if (string.IsNullOrEmpty(needle) || go.name.ToLowerInvariant().Contains(needle))
                    result.Add(go.name);
            }
            return result;
        }

        /// <summary>Find a registered prefab by exact name (case-insensitive). Null if not found.</summary>
        public static GameObject FindPrefabByName(string exactName)
        {
            if (string.IsNullOrEmpty(exactName)) return null;
            foreach (GameObject go in AllPrefabs())
            {
                if (go != null && string.Equals(go.name, exactName, System.StringComparison.OrdinalIgnoreCase))
                    return go;
            }
            return null;
        }

        private static List<GameObject> AllPrefabs()
        {
            object scene = ZNetSceneInstanceField?.GetValue(null);
            if (scene == null) return new List<GameObject>();
            return ZNetScenePrefabsField?.GetValue(scene) as List<GameObject> ?? new List<GameObject>();
        }

        /// <summary>
        /// Inspect a registered prefab's components WITHOUT instantiating it —
        /// checking the static prefab asset directly, so Awake() never runs and
        /// nothing can register itself with ZNetScene/ZDOMan. Use this to vet a
        /// VFX candidate before ever risking a live spawn in the world.
        /// </summary>
        public static (bool found, bool hasZNetView, List<string> scriptNames) InspectPrefab(string exactName)
        {
            GameObject prefab = FindPrefabByName(exactName);
            if (prefab == null) return (false, false, new List<string>());

            bool hasZNetView = prefab.GetComponentInChildren<ZNetView>(true) != null;
            var scripts = new List<string>();
            foreach (MonoBehaviour mb in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null) scripts.Add(mb.GetType().Name);
            }
            return (true, hasZNetView, scripts);
        }

        private static readonly string[] CandidateParticleShaders =
        {
            "Particles/Standard Unlit",
            "Particles/Standard Surface",
            "Legacy Shaders/Particles/Additive",
            "Legacy Shaders/Particles/Alpha Blended",
            "Sprites/Default",
            "UI/Default"
        };

        /// <summary>
        /// Shader.Find returns null for anything stripped from the build, so we
        /// try a chain and take the first that resolves; null if none do.
        /// </summary>
        /// <remarks>
        /// Settled against the shipped build, because a first pass got this
        /// exactly backwards and nearly committed the inversion.
        ///
        /// Parsing the ScriptMapper in globalgamemanagers (225 entries, laid out
        /// PPtr-first then name — reading it name-first shifts every mapping by
        /// one and inverts the answer) and cross-checking the object table of
        /// Resources/unity_builtin_extra:
        ///
        ///   Particles/Standard Unlit                NULL — stripped
        ///   Particles/Standard Surface              NULL — stripped
        ///   Legacy Shaders/Particles/Additive       NULL — stripped
        ///   Legacy Shaders/Particles/Alpha Blended  NULL — stripped
        ///   Sprites/Default                         ships (pathID 10753)
        ///   UI/Default                              ships (pathID 10770)
        ///
        /// So the first FOUR candidates can never resolve and this chain lands
        /// on candidate 5, Sprites/Default. The reason Standard Unlit misses is
        /// mundane: the game ships it renamed. Its own shader declares
        /// "Particles/Standard Unlit2", with a trailing 2, so Shader.Find on the
        /// un-suffixed name finds nothing.
        ///
        /// Two consequences, one of which had to be fixed. Sprites/Default is
        /// ALPHA BLENDED: right for smoke, WRONG for flame, because alpha-blended
        /// particles occlude one another instead of accumulating and a mass of
        /// them reads as separate orange discs rather than fire. That is what
        /// 0.20.1 looked like in play, so flames, sparks and ground fire now use
        /// GetOrCreateAdditiveParticleMaterial instead and only smoke still comes
        /// through here. It also draws an untextured particle as a hard-edged
        /// quad, which is why GetOrCreateSoftParticleTexture exists. The log line
        /// below prints which candidate won, so this is never argued from memory.
        /// </remarks>
        private static Shader FindUsableParticleShader()
        {
            foreach (string name in CandidateParticleShaders)
            {
                Shader shader = Shader.Find(name);
                if (shader != null)
                {
                    if (!_loggedResolvedParticleShader)
                    {
                        _loggedResolvedParticleShader = true;
                        FireLogger.Info($"[SHADER-DIAG] particle shader resolved to \"{name}\" " +
                                        $"(candidate {System.Array.IndexOf(CandidateParticleShaders, name) + 1} " +
                                        $"of {CandidateParticleShaders.Length}).");
                    }
                    return shader;
                }
            }

            if (!_loggedResolvedParticleShader)
            {
                _loggedResolvedParticleShader = true;
                FireLogger.Warn("[SHADER-DIAG] none of the " + CandidateParticleShaders.Length +
                                " candidate particle shaders resolved in this build.");
            }
            return null;
        }

        private static bool _loggedResolvedParticleShader;

        // --- EffectArea (vanilla's own "standing in fire hurts you" detection zone) ---
        private static readonly FieldInfo EffectAreaTypeField = typeof(EffectArea).GetField("m_type", AnyInstance);
        private static readonly FieldInfo EffectAreaPlayerOnlyField = typeof(EffectArea).GetField("m_playerOnly", AnyInstance);
        private static readonly FieldInfo EffectAreaIsHeatField = typeof(EffectArea).GetField("m_isHeatType", AnyInstance);
        private static readonly FieldInfo EffectAreaColliderField = typeof(EffectArea).GetField("m_collider", AnyInstance);

        /// <summary>
        /// Reads the real field values off the nearest live EffectArea in the
        /// scene (e.g. one attached to a lit campfire) — used to verify the
        /// correct enum value for "Burning" before configuring our own ground
        /// fire's EffectArea, rather than guessing the enum ordinal blind.
        /// </summary>
        public static string InspectNearestEffectArea(Vector3 near, float maxDistance)
        {
            EffectArea[] all = Object.FindObjectsOfType<EffectArea>();
            EffectArea closest = null;
            float bestSqr = maxDistance * maxDistance;

            foreach (EffectArea area in all)
            {
                if (area == null) continue;
                float sqr = (area.transform.position - near).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    closest = area;
                }
            }

            if (closest == null) return $"No EffectArea found within {maxDistance}m.";

            object typeVal = EffectAreaTypeField?.GetValue(closest);
            object playerOnlyVal = EffectAreaPlayerOnlyField?.GetValue(closest);
            object isHeatVal = EffectAreaIsHeatField?.GetValue(closest);
            object colliderVal = EffectAreaColliderField?.GetValue(closest);
            float dist = Mathf.Sqrt(bestSqr <= maxDistance * maxDistance ? (closest.transform.position - near).sqrMagnitude : 0f);

            return $"Nearest EffectArea '{closest.gameObject.name}' at {dist:F1}m: " +
                   $"type={typeVal} (underlying={System.Convert.ToInt32(typeVal)}), " +
                   $"playerOnly={playerOnlyVal}, isHeatType={isHeatVal}, " +
                   $"collider={(colliderVal != null ? colliderVal.GetType().Name : "null")}";
        }

        // --- Terrain queries (READ-ONLY — no writes, no networking, safe to call
        // freely unlike the TerrainComp paint block below). Confirmed via the
        // publicized DLL: Heightmap.FindHeightmap(Vector3) is a static lookup
        // for the right per-zone Heightmap instance (same shape as
        // TerrainComp.FindTerrainCompiler below); IsCleared/IsCultivated are
        // public instance methods taking a world position, returning bool.
        private static readonly MethodInfo HeightmapFindMethod =
            typeof(Heightmap).GetMethod("FindHeightmap", AnyStatic, null, new[] { typeof(Vector3) }, null);
        private static readonly MethodInfo HeightmapIsClearedMethod =
            typeof(Heightmap).GetMethod("IsCleared", AnyInstance, null, new[] { typeof(Vector3) }, null);
        private static readonly MethodInfo HeightmapIsCultivatedMethod =
            typeof(Heightmap).GetMethod("IsCultivated", AnyInstance, null, new[] { typeof(Vector3) }, null);

        /// <summary>
        /// True if the ground at worldPos is cleared (e.g. a real dirt path) or
        /// cultivated (tilled soil) — either way, no grass fuel there. Used to let
        /// a real path or tilled strip act as an actual firebreak against ground
        /// spread. Read-only: no terrain is modified by this call. Returns false
        /// (i.e. "don't treat as a firebreak") if the Heightmap can't be found or
        /// reflection lookups failed, rather than silently blocking all spread.
        /// </summary>
        public static bool IsClearedOrCultivated(Vector3 worldPos)
        {
            if (HeightmapFindMethod == null) return false;

            object heightmap;
            try
            {
                heightmap = HeightmapFindMethod.Invoke(null, new object[] { worldPos });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"IsClearedOrCultivated: FindHeightmap threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            if (heightmap == null) return false;

            bool cleared = HeightmapIsClearedMethod != null &&
                           (bool)HeightmapIsClearedMethod.Invoke(heightmap, new object[] { worldPos });
            bool cultivated = HeightmapIsCultivatedMethod != null &&
                              (bool)HeightmapIsCultivatedMethod.Invoke(heightmap, new object[] { worldPos });
            return cleared || cultivated;
        }

        // --- Terrain painting (real vanilla dirt, via the same system the Hoe uses) ---
        // TerrainComp is networked (m_nview) AND writes per-zone terrain data that is SAVED WITH
        // THE WORLD, unlike every VFX and damage system in this file, which is runtime only. That
        // is why UseVanillaDirtPaint is opt-in. Six facts, read from the shipping 1.0.15 assembly
        // on 2026-09-20. The publicized copy in libs\ has the same method bodies; what it gets
        // wrong is visibility, which is why the private/public list at the end came from the
        // shipping DLL and not from what compiles.
        //
        //   1. FindTerrainCompiler and Heightmap.FindHeightmap scan static lists of LOADED
        //      INSTANCES. A dedicated server keeps real zones only around its own reference
        //      position, which never leaves world origin; everywhere a player actually is it makes
        //      ghost zones (ZoneSystem.CreateGhostZones instantiates a zone only to generate it and
        //      destroys it in the same call). So where a fire burns, every lookup here returns
        //      null on a server, and until 0.21.16 this feature queued every burnt cell there,
        //      found nothing, and dropped the lot at Debug level. Paint is laid by a machine with
        //      the terrain loaded: a listen host, or a client.
        //   2. A zone nobody has ever hoed has NO compiler. Vanilla's TerrainOp.Awake goes through
        //      Heightmap.GetAndCreateTerrainCompiler, which instantiates one; FindTerrainCompiler
        //      alone returns null for pristine forest, which is most of what burns. But creating
        //      one is only safe when NO compiler ZDO exists for the zone anywhere: TerrainComp.Awake
        //      destroys the OTHER compiler it finds, through ZNetScene, on every machine that has
        //      one instantiated - so a duplicate created because the real one's ZDO had not been
        //      instantiated here yet destroys the real one, and every hoe mark in it, for everyone.
        //      Two peers creating one in the same second destroy each other's, on repeat. Hence:
        //      exactly one painter per cell, elected by the server, which alone sees every peer
        //      and every compiler ZDO, and a per-cell "may create" flag the server sets only when
        //      it sees no compiler ZDO in that zone (FireManager.AssignPendingPaint). The painter
        //      creates only with that flag, and only once ZNetScene.IsAreaReady says every ZDO in
        //      the zone is instantiated here.
        //   3. TerrainComp.Save returns early unless m_nview.IsOwner(), and it republishes this
        //      machine's whole picture of the zone - heights included. ZDOMan keeps whichever
        //      revision lands first, so two writers from one base revision lose one of them for
        //      good, and the loser never notices (its own revision counter already matches). So
        //      this NEVER takes a compiler from a live owner; the server elects the owner instead,
        //      and releases an owner that is gone or out of reach (ReleaseNearbyZDOS only looks
        //      around a peer's CURRENT position, so a compiler left behind by a teleport is held
        //      for good otherwise). An unowned compiler is claimed only for a zone the server
        //      elected this machine for - vanilla's own Awake claims an unowned one the same way.
        //      A disc that spills into the next zone is painted there only if that compiler is
        //      already ours: its own elected painter may be claiming it this very second.
        //   4. Save writes the ZDO and does NOT regenerate the heightmap. Vanilla's DoOperation is
        //      Save(paintOnly) + m_hmap.Poke(1, paintOnly) + ClutterSystem.ResetGrass. Other peers
        //      regenerate on their own (CheckLoad sees the ZDO's DataRevision move); the painter's
        //      own revision is already current, so without the Poke it never saw what it painted.
        //   5. InternalDoOperation stamps m_operations++, m_lastOpPoint and m_lastOpRadius after
        //      every op, Save serialises them, and CheckLoad on every OTHER peer resets grass over
        //      that point and radius when m_operations advanced by exactly one - otherwise it
        //      rebuilds the whole zone's clutter. Calling PaintCleared directly skips the stamp,
        //      so the batch stamps one op covering itself - and takes the stamp back when
        //      Save(paintOnly) short-circuits on an unchanged mask, or the counter drifts ahead of
        //      the ZDO and every later batch lands in the whole-zone branch.
        //   6. PaintCleared reads each vertex's current colour through getMask, which returns the
        //      heightmap's last-BAKED texture unless a late-update regenerate is pending
        //      (m_doLateUpdate == 1), in which case it reads the compiler's own accumulating mask.
        //      Vanilla pokes after every op, so the next op sees the last; a batch has to poke
        //      BEFORE painting or its second disc overwrites its first from stale pixels.
        //
        // PaintCleared's signature: 1.0.7 folded (worldPos, radius, paintType, heightCheck, apply)
        // into (worldPos, rot, TerrainOp.Settings), and the trailing `apply` that used to call
        // Save/Poke for you is gone - the batch does both itself. m_hmap, m_nview, m_operations,
        // m_lastOpPoint, m_lastOpRadius, PaintCleared and Save are private in the shipping
        // assembly; FindTerrainCompiler, FindHeightmap, GetAndCreateTerrainCompiler, IsOwner,
        // HasOwner, ClaimOwnership, Poke and ResetGrass are public and called directly.
        private static readonly MethodInfo TerrainCompPaintClearedMethod =
            typeof(TerrainComp).GetMethod("PaintCleared", AnyInstance, null,
                new[] { typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings) }, null);
        private static readonly FieldInfo TerrainCompNviewField =
            typeof(TerrainComp).GetField("m_nview", AnyInstance);
        private static readonly FieldInfo TerrainCompHmapField =
            typeof(TerrainComp).GetField("m_hmap", AnyInstance);
        private static readonly FieldInfo TerrainCompOperationsField =
            typeof(TerrainComp).GetField("m_operations", AnyInstance);
        private static readonly FieldInfo TerrainCompLastOpPointField =
            typeof(TerrainComp).GetField("m_lastOpPoint", AnyInstance);
        private static readonly FieldInfo TerrainCompLastOpRadiusField =
            typeof(TerrainComp).GetField("m_lastOpRadius", AnyInstance);
        private static readonly MethodInfo TerrainCompSaveMethod =
            // Save(bool paintOnly = false): a default argument still changes the signature, so an
            // EmptyTypes lookup does not match.
            typeof(TerrainComp).GetMethod("Save", AnyInstance, null, new[] { typeof(bool) }, null);
        private static bool _paintReflectionFailureLogged;
        private static readonly List<Heightmap> _paintHeightmapScratch = new List<Heightmap>(4);

        // --- Real prefab-based terrain paint (confirmed via firecheckprefab: the
        // 'cultivate' piece carries ZNetView, Piece, TerrainModifier — the actual
        // prefab the Cultivator tool places). Spawning the real, already-correctly-
        // configured prefab through ZNetScene.Instantiate reuses vanilla's own
        // networked object lifecycle rather than us hand-assembling a ZNetView-backed
        // object ourselves — much safer given TerrainModifier's paint operation is
        // RPC-driven and needs proper ownership/network setup to actually work.
        private static readonly MethodInfo ZNetSceneSpawnObjectMethod =
            typeof(ZNetScene).GetMethod("SpawnObject", AnyInstance, null,
                new[] { typeof(Vector3), typeof(Quaternion), typeof(GameObject) }, null);
        private static readonly MethodInfo ZNetSceneIsAreaReadyMethod =
            typeof(ZNetScene).GetMethod("IsAreaReady", AnyInstance, null, new[] { typeof(Vector3) }, null);

        /// <summary>
        /// Spawns the real vanilla "cultivate" piece at a position to paint the
        /// ground as tilled dirt — the same visual result you'd get from actually
        /// using the Cultivator tool. Uses the prefab's own default configuration
        /// (PaintType.Cultivate) rather than trying to override it to PaintType.Dirt
        /// before its own Awake()/OnPlaced() applies the paint — overriding would
        /// mean racing the same "configure before Awake" timing problem that's
        /// already needed careful handling elsewhere (EffectArea), and Cultivate's
        /// actual visual (bare tilled dirt) is very likely what "bare dirt" means
        /// in practice anyway. A TerrainPaintCleanup safety net force-removes the
        /// spawned piece after 5s if it doesn't self-destroy on its own (real
        /// Cultivator use normally leaves nothing behind).
        /// </summary>
        private static readonly MethodInfo PlayerPlacePieceMethod =
            // 1.0.7 appended `bool cheated = false`. A default argument still changes the
            // signature, so the old four-type lookup no longer matches.
            typeof(Player).GetMethod("PlacePiece", AnyInstance, null,
                new[] { typeof(Piece), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) }, null);

        /// <summary>
        /// Spawns the real vanilla "cultivate" piece at a position to paint the
        /// ground as tilled dirt — the same visual result you'd get from actually
        /// using the Cultivator tool.
        ///
        /// v1 (0.15.3-0.15.6) tried ZNetScene.SpawnObject directly — found the
        /// right method after two wrong guesses, but it consistently returned
        /// null even with IsAreaReady confirmed true. Turns out "cultivate" isn't
        /// meant to be spawned as a raw prefab at all: it's placed through the
        /// Hoe's normal build flow, which does real placement validation/setup
        /// (Piece.OnPlaced(), TerrainModifier's m_triggerOnPlaced hook, etc.)
        /// that a raw SpawnObject call skips entirely.
        ///
        /// v2 (this version) calls Player.PlacePiece directly — the actual
        /// lower-level placement executor, not the higher-level TryPlacePiece
        /// wrapper (which adds cost/validity UI checks we don't want, since
        /// we're not simulating a real player click). Runs on the LOCAL PLAYER's
        /// own Player instance, since that's the only Character with this method
        /// meaningfully available. One real unknown: whether this causes
        /// visible side effects on the player (a swing animation, stamina cost,
        /// etc.) since it's normally invoked as part of the player's own build
        /// action — doAttack is passed false to at least skip the swing/attack
        /// animation specifically.
        /// </summary>
        private static readonly FieldInfo PiecePlaceEffectField = typeof(Piece).GetField("m_placeEffect", AnyInstance);

        public static bool TrySpawnDirtPaintPiece(Vector3 worldPos)
        {
            GameObject prefab = FindPrefabByName("cultivate");
            if (prefab == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: 'cultivate' prefab not found via FindPrefabByName.");
                return false;
            }

            Piece piecePrefabComponent = prefab.GetComponent<Piece>();
            if (piecePrefabComponent == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: 'cultivate' prefab has no Piece component.");
                return false;
            }

            if (PlayerPlacePieceMethod == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: Player.PlacePiece(Piece,Vector3,Quaternion,bool) " +
                                  "reflection lookup returned null — likely a parameter-type mismatch.");
                return false;
            }

            Player local = LocalPlayerField?.GetValue(null) as Player;
            if (local == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: no local player.");
                return false;
            }

            // PlacePiece plays the piece's own placement sound/effect (Piece.m_placeEffect)
            // regardless of doAttack — confirmed by real testing (a Hoe swing sound
            // firing on every ground-cell burnout, which would get old fast on any
            // active fire). Blank it out on the PREFAB's shared Piece component just
            // for the duration of this call, then restore it immediately afterward —
            // in a try/finally so restoration happens even if PlacePiece throws — so
            // real player use of the actual Cultivator/Hoe tool is completely
            // unaffected. Synchronous read-modify-restore is safe here specifically
            // because effect playback happens synchronously as part of placement,
            // not on some later frame/coroutine we can't control the timing of.
            object originalEffect = PiecePlaceEffectField?.GetValue(piecePrefabComponent);
            if (PiecePlaceEffectField != null)
            {
                PiecePlaceEffectField.SetValue(piecePrefabComponent, new EffectList());
            }

            object result;
            try
            {
                // trailing false = 1.0.7's `cheated`, which is its own default.
                result = PlayerPlacePieceMethod.Invoke(local, new object[] { piecePrefabComponent, worldPos, Quaternion.identity, false, false });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"TrySpawnDirtPaintPiece: PlacePiece threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            finally
            {
                if (PiecePlaceEffectField != null)
                {
                    PiecePlaceEffectField.SetValue(piecePrefabComponent, originalEffect);
                }
            }

            bool succeeded = !(result is bool b) || b; // if it doesn't return bool, assume success since no exception was thrown
            if (!succeeded)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: PlacePiece returned false (placement rejected).");
            }
            return succeeded;
        }

        [System.Obsolete("Superseded by TrySpawnDirtPaintPiece via Player.PlacePiece — kept only for reference.")]
        private static bool TrySpawnDirtPaintPieceViaSpawnObject(Vector3 worldPos)
        {
            GameObject prefab = FindPrefabByName("cultivate");
            if (prefab == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: 'cultivate' prefab not found via FindPrefabByName.");
                return false;
            }
            if (ZNetSceneSpawnObjectMethod == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: ZNetScene.SpawnObject(Vector3,Quaternion,GameObject) " +
                                  "reflection lookup returned null — likely a parameter-type or overload mismatch.");
                return false;
            }

            object scene = ZNetSceneInstanceField?.GetValue(null);
            if (scene == null)
            {
                FireLogger.Debug("TrySpawnDirtPaintPiece: ZNetScene.instance is null.");
                return false;
            }

            if (ZNetSceneIsAreaReadyMethod != null)
            {
                bool areaReady = (bool)ZNetSceneIsAreaReadyMethod.Invoke(scene, new object[] { worldPos });
                if (!areaReady)
                {
                    FireLogger.Debug("TrySpawnDirtPaintPiece: IsAreaReady(worldPos) is false — this is likely why SpawnObject returns null.");
                }
            }

            object result;
            try
            {
                result = ZNetSceneSpawnObjectMethod.Invoke(scene, new object[] { worldPos, Quaternion.identity, prefab });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"TrySpawnDirtPaintPiece: SpawnObject threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }

            if (!(result is GameObject instance))
            {
                FireLogger.Debug($"TrySpawnDirtPaintPiece: SpawnObject returned {(result == null ? "null" : result.GetType().Name)}, not a GameObject.");
                return false;
            }

            instance.AddComponent<TerrainPaintCleanup>();
            return true;
        }

        /// <summary>
        /// Server side: put a tree back at a burned-down spot. Instantiates the prefab HERE,
        /// which is what vanilla's own RPC_SpawnObject does on each machine that receives it:
        /// ZNetView.Awake creates one persistent ZDO owned by this server, every client in
        /// range receives it, and if the instance itself is later dropped by
        /// ZNetScene.RemoveObjects (headless, instances only live near world origin) the ZDO
        /// survives because trees are persistent. Decompiled from the 1.0.15 server, 2026-09-18.
        ///
        /// What this replaced, and why it had never grown a single tree on a dedicated server:
        /// the previous body waited behind ZNetScene.IsAreaReady, which first demands the zone
        /// be in ZoneSystem.m_zones; headless, every zone a player stands in is a GHOST zone
        /// (ZDO-only, root destroyed the same frame) and never enters that table, so the gate
        /// was false everywhere but the origin and every entry burned its retries and was
        /// dropped. Behind the gate it reflected into ZNetScene.SpawnObject, which in 1.0.x is
        /// a VOID that broadcasts a "SpawnObject" routed RPC to Everybody - each peer would have
        /// instantiated its own copy - and whose null return was then read as failure.
        /// </summary>
        public static bool TrySpawnTree(string prefabName, Vector3 worldPos)
        {
            if (!IsServer())
            {
                FireLogger.Debug("TrySpawnTree: not the server - regrowth is server authority only.");
                return false;
            }
            GameObject prefab = FindPrefabByName(prefabName);
            if (prefab == null)
            {
                FireLogger.Debug($"TrySpawnTree: prefab '{prefabName}' not found via FindPrefabByName.");
                return false;
            }
            if (ZDOMan.instance == null || ZNetSceneInstanceField?.GetValue(null) == null)
            {
                FireLogger.Debug("TrySpawnTree: ZDOMan/ZNetScene not up yet.");
                return false;
            }

            // A fixed yaw per spot, so a regrown tree is not the one orientation every time
            // and a restart does not re-roll it.
            float yaw = Mathf.Repeat(worldPos.x * 37.1f + worldPos.z * 91.7f, 360f);
            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(prefab, worldPos, Quaternion.Euler(0f, yaw, 0f));
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"TrySpawnTree: Instantiate threw: {ex.Message}");
                return false;
            }
            if (go == null) return false;

            ZNetView nv = go.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid())
            {
                // No ZDO means nothing was registered with the world; do not leave a ghost object.
                FireLogger.Debug($"TrySpawnTree: '{prefabName}' came up without a valid ZNetView/ZDO - destroyed.");
                UnityEngine.Object.Destroy(go);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The prefab hash Valheim itself would store for an object named <paramref name="name"/>.
        /// `ZNetView.Awake` hashes `Utils.GetPrefabName`, which is `name.Remove(name.IndexOfAny(new[]
        /// { '(', ' ' }))` - it truncates at the FIRST '(' or space, not just a trailing "(Clone)".
        /// The two rules agree for every vanilla prefab and diverge for a modded name containing a
        /// space, which would silently never match. Read out of assembly_utils, 2026-09-18.
        /// </summary>
        public static int PrefabHashOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int cut = name.IndexOfAny(PrefabNameTerminators);
            return (cut >= 0 ? name.Remove(cut) : name).GetStableHashCode();
        }
        private static readonly char[] PrefabNameTerminators = { '(', ' ' };

        /// <summary>
        /// Every ZDO in the 3x3 zone block around <paramref name="center"/>, as id + position +
        /// prefab hash. Unfiltered on purpose: the restore matches an exact prefab and does its own
        /// distance test, and one block serves a whole cluster of store lines.
        /// </summary>
        public static bool CollectAllZdosNear(Vector3 center, List<ZdoBurnable> into)
        {
            into.Clear();
            ZDOMan man = ZDOMan.instance;
            if (man == null || ZdoManFindSectorObjectsMethod == null) return false;
            _zdoScanScratch.Clear();
            try
            {
                ZdoManFindSectorObjectsMethod.Invoke(man,
                    new object[] { ZoneSystem.GetZone(center), new SimulationDistance(1, 0, true), _zdoScanScratch, null });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"CollectAllZdosNear: FindSectorObjects threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            for (int i = 0; i < _zdoScanScratch.Count; i++)
            {
                ZDO zdo = _zdoScanScratch[i];
                if (zdo == null) continue;
                into.Add(new ZdoBurnable { Id = zdo.m_uid, Position = zdo.GetPosition(), PrefabHash = zdo.GetPrefab() });
            }
            return true;
        }

        /// <summary>Where a ZDO is RIGHT NOW, which for a felled log is not where it was lit.</summary>
        public static bool TryGetZdoPosition(ZDOID id, out Vector3 position)
        {
            position = Vector3.zero;
            ZDO zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null) return false;
            position = zdo.GetPosition();
            return true;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the world position of every player-built
        /// (creator-stamped) ZDO in the 3x3 zone block around <paramref name="center"/>, and
        /// returns true. Returns FALSE when the sector scan is unavailable, which the caller must
        /// treat as "cannot tell", never as "nothing is built here" - failing open would let a
        /// tree grow up through somebody's floor.
        ///
        /// It hands back the whole block rather than answering one yes/no question because the
        /// block is 192 m wide either way: regrowth entries come due in clusters inside one burn,
        /// so the caller scans once and tests every entry against the result.
        /// </summary>
        public static bool CollectPlayerBuiltPositionsNear(Vector3 center, List<Vector3> into)
        {
            into.Clear();
            ZDOMan man = ZDOMan.instance;
            if (man == null || ZdoManFindSectorObjectsMethod == null)
            {
                FireLogger.Debug("CollectPlayerBuiltPositionsNear: ZDOMan null or the FindSectorObjects " +
                                 "lookup failed - the build-over check cannot answer, so the caller must skip.");
                return false;
            }
            _zdoScanScratch.Clear();
            try
            {
                ZdoManFindSectorObjectsMethod.Invoke(man,
                    new object[] { ZoneSystem.GetZone(center), new SimulationDistance(1, 0, true), _zdoScanScratch, null });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"CollectPlayerBuiltPositionsNear: FindSectorObjects threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            for (int i = 0; i < _zdoScanScratch.Count; i++)
            {
                ZDO zdo = _zdoScanScratch[i];
                if (zdo == null || zdo.GetLong(CreatorZdoHash, 0L) == 0L) continue;
                into.Add(zdo.GetPosition());
            }
            return true;
        }

        /// <summary>Force-removes a lingering terrain-paint piece via the proper
        /// ZNetView.Destroy() self-removal path. See TerrainPaintCleanup.</summary>
        public static void ForceCleanupTerrainPaintPiece(GameObject go)
        {
            if (go == null) return;
            ZNetView nv = go.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;

            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.Destroy();
        }

        public struct PaintJob
        {
            public Vector3 Position;
            /// <summary>Set by the server only when it sees NO compiler ZDO for the zone (fact 2).</summary>
            public bool MayCreate;
        }

        private sealed class ZoneBatch
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            /// <summary>True when at least one job's OWN zone is this one: the server elected us for it.</summary>
            public bool Assigned;
        }

        /// <summary>
        /// Lays real dirt for every job whose zone is loaded here, one zone at a time, and only
        /// into compilers this machine owns, or that nobody owns in a zone the server elected this
        /// machine for. The caller is that ONE painter (FireManager.AssignPendingPaint). Per zone:
        /// Poke first so the discs accumulate (fact 6), PaintCleared per position, vanilla's op
        /// bookkeeping (fact 5), ONE Save (fact 4), one grass reset. Returns the number of paint
        /// operations laid; jobs the painter cannot take - zone not loaded here, compiler not
        /// instantiated here yet, or owned by someone else - are dropped. The decal already covers
        /// the look, and a mark that cannot be laid is not worth a retry queue.
        /// </summary>
        public static int TryPaintScorchedDirtBatch(List<PaintJob> jobs, float radius)
        {
            if (jobs == null || jobs.Count == 0) return 0;
            if (TerrainCompPaintClearedMethod == null || TerrainCompNviewField == null ||
                TerrainCompHmapField == null || TerrainCompSaveMethod == null ||
                TerrainCompOperationsField == null || TerrainCompLastOpPointField == null ||
                TerrainCompLastOpRadiusField == null)
            {
                if (!_paintReflectionFailureLogged)
                {
                    _paintReflectionFailureLogged = true;
                    FireLogger.Warn("UseVanillaDirtPaint is on, but a TerrainComp member this build reflects " +
                                    "(PaintCleared / Save / m_nview / m_hmap / m_operations / m_lastOpPoint / " +
                                    "m_lastOpRadius) is missing from this game version. No dirt will be painted; " +
                                    "the scorch decal still draws.");
                }
                return 0;
            }

            // One pass per zone: the expensive part - serialising ~4,200 vertices into the ZDO -
            // happens once per zone. A disc that crosses a zone line is painted into the
            // neighbouring compiler too, as vanilla's TerrainOp.Awake does for every heightmap in
            // the op's radius; the neighbour is never CREATED and never CLAIMED from here (facts 2
            // and 3) - only painted if it is already ours.
            var byComp = new Dictionary<TerrainComp, ZoneBatch>();
            int unloaded = 0, notReady = 0;
            ZNetScene scene = ZNetScene.instance;
            foreach (PaintJob job in jobs)
            {
                Vector3 pos = job.Position;
                Heightmap own = Heightmap.FindHeightmap(pos);
                if (own == null) { unloaded++; continue; }

                // Fact 2: create only on the server's word, and only once every ZDO of the zone
                // has an instance here - a compiler ZDO that exists but is not instantiated yet is
                // exactly the case where a local create becomes a duplicate.
                TerrainComp comp = TerrainComp.FindTerrainCompiler(own.transform.position);
                if (comp == null && job.MayCreate && scene != null && scene.IsAreaReady(pos))
                {
                    try
                    {
                        comp = own.GetAndCreateTerrainCompiler();
                    }
                    catch (System.Exception ex)
                    {
                        FireLogger.Debug($"TryPaintScorchedDirtBatch: GetAndCreateTerrainCompiler threw at {pos:F0}: {ex.Message}");
                        continue;
                    }
                }
                if (comp == null) { notReady++; continue; }
                AddPaintPosition(byComp, comp, pos, assigned: true);

                _paintHeightmapScratch.Clear();
                Heightmap.FindHeightmap(pos, radius, _paintHeightmapScratch);
                for (int i = 0; i < _paintHeightmapScratch.Count; i++)
                {
                    Heightmap other = _paintHeightmapScratch[i];
                    if (other == null || other == own) continue;
                    TerrainComp neighbour = TerrainComp.FindTerrainCompiler(other.transform.position);
                    if (neighbour != null && neighbour != comp) AddPaintPosition(byComp, neighbour, pos, assigned: false);
                }
            }

            int painted = 0;
            int notOurs = 0;
            foreach (KeyValuePair<TerrainComp, ZoneBatch> kv in byComp)
            {
                TerrainComp comp = kv.Key;
                ZoneBatch batch = kv.Value;
                ZNetView nv = TerrainCompNviewField.GetValue(comp) as ZNetView;
                if (nv == null || !nv.IsValid())
                {
                    FireLogger.Debug("TryPaintScorchedDirtBatch: compiler has no valid ZNetView, skipping its zone.");
                    continue;
                }
                // Fact 3: never take a compiler from a live owner, and never claim one we were
                // only spilled into. Unowned and ours to paint: claimed.
                if (!nv.IsOwner())
                {
                    if (nv.HasOwner() || !batch.Assigned) { notOurs += batch.Positions.Count; continue; }
                    nv.ClaimOwnership();
                }
                Heightmap hmap = TerrainCompHmapField.GetValue(comp) as Heightmap;
                if (hmap == null) continue;

                // Fact 6: request the regenerate BEFORE painting, so every disc in this batch reads
                // the compiler's accumulating mask rather than the last-baked texture. Still one
                // regenerate, on the next LateUpdate, texture only.
                hmap.Poke(1, true);

                // Built once per zone: the radius is constant for the batch and PaintCleared only
                // reads it. Defaults left alone on purpose: m_halfOffset true is the half-vertex
                // shift every hoe op gets, m_paintExp 0.1 is the Hoe's own near-hard edge, and
                // m_centerMultiplicationFactor 0 skips the mask-multiply branch. rot is zero:
                // m_rotation is off, so a round splat has no orientation to give it.
                var settings = new TerrainOp.Settings
                {
                    m_paintCleared = true,
                    m_paintType = TerrainModifier.PaintType.Dirt,
                    m_paintRadius = radius,
                    m_paintHeightCheck = false,
                };

                int paintedOnComp = 0;
                Vector3 centroid = Vector3.zero;
                foreach (Vector3 pos in batch.Positions)
                {
                    try
                    {
                        TerrainCompPaintClearedMethod.Invoke(comp, new object[] { pos, Vector3.zero, settings });
                        paintedOnComp++;
                        centroid += pos;
                    }
                    catch (System.Exception ex)
                    {
                        FireLogger.Debug($"TryPaintScorchedDirtBatch: PaintCleared threw: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                if (paintedOnComp == 0) continue;
                painted += paintedOnComp;

                // Fact 5: one op covering the batch - its centroid, and the farthest disc's reach.
                centroid /= paintedOnComp;
                float reach = 0f;
                foreach (Vector3 pos in batch.Positions)
                    reach = Mathf.Max(reach, Vector3.Distance(pos, centroid) + radius);
                try
                {
                    TerrainCompOperationsField.SetValue(comp, (int)TerrainCompOperationsField.GetValue(comp) + 1);
                    TerrainCompLastOpPointField.SetValue(comp, centroid);
                    TerrainCompLastOpRadiusField.SetValue(comp, reach);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"TryPaintScorchedDirtBatch: op bookkeeping threw: {ex.Message}");
                }

                // Fact 4: paintOnly=true, so Save short-circuits on an unchanged mask hash. When it
                // does (every disc landed on ground that was already dirt) nothing was published,
                // so the stamp above must not stand either, and there is no grass to reset.
                uint before = nv.GetZDO().DataRevision;
                try
                {
                    TerrainCompSaveMethod.Invoke(comp, new object[] { true });
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"TryPaintScorchedDirtBatch: Save threw (painted locally, not persisted or synced): {ex.InnerException?.Message ?? ex.Message}");
                }
                if (nv.GetZDO().DataRevision == before)
                {
                    try { TerrainCompOperationsField.SetValue(comp, (int)TerrainCompOperationsField.GetValue(comp) - 1); }
                    catch (System.Exception) { }
                    continue;
                }
                if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(centroid, reach);
            }

            if (unloaded > 0 || notReady > 0 || notOurs > 0)
                FireLogger.Debug($"TryPaintScorchedDirtBatch: {painted} laid; dropped {unloaded} with no heightmap loaded here, " +
                                 $"{notReady} whose compiler is not instantiated here yet (or may not be created), " +
                                 $"{notOurs} in a zone whose compiler is not ours to write.");
            return painted;
        }

        private static void AddPaintPosition(Dictionary<TerrainComp, ZoneBatch> byComp, TerrainComp comp, Vector3 pos, bool assigned)
        {
            if (!byComp.TryGetValue(comp, out ZoneBatch batch))
            {
                batch = new ZoneBatch();
                byComp[comp] = batch;
            }
            batch.Positions.Add(pos);
            if (assigned) batch.Assigned = true;
        }

        // --- Dirt-paint election (server side) ---
        private static readonly int TerrainCompilerPrefabHash = "_TerrainCompiler".GetStableHashCode();
        private static readonly List<ZDO> _compilerScanScratch = new List<ZDO>(64);

        public struct PaintCandidate
        {
            public long PeerId;
            public Vector3 Position;
        }

        /// <summary>
        /// The zone's terrain compiler ZDO, or null when the zone has none anywhere - a headless
        /// server has every ZDO even though it has no instance. A zero ring walks exactly one
        /// sector. The ZDO's owner is the only machine that can publish a write to it.
        /// </summary>
        public static ZDO FindTerrainCompilerZdo(Vector3 pos)
        {
            ZDOMan man = ZDOMan.instance;
            if (man == null || ZdoManFindSectorObjectsMethod == null) return null;
            _compilerScanScratch.Clear();
            try
            {
                ZdoManFindSectorObjectsMethod.Invoke(man,
                    new object[] { ZoneSystem.GetZone(pos), new SimulationDistance(0, 0, true), _compilerScanScratch, null });
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"FindTerrainCompilerZdo: FindSectorObjects threw: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
            for (int i = 0; i < _compilerScanScratch.Count; i++)
            {
                ZDO zdo = _compilerScanScratch[i];
                if (zdo != null && zdo.GetPrefab() == TerrainCompilerPrefabHash) return zdo;
            }
            return null;
        }

        /// <summary>
        /// Server side: drops a ZDO's owner to nobody. Only ever called for an owner that is gone
        /// or out of reach of the zone. Vanilla's ReleaseNearbyZDOS does the same the moment a
        /// peer's active area covers the ZDO, but it only scans around each peer's CURRENT
        /// position, so a compiler an owner teleported away from is otherwise held for good.
        /// </summary>
        public static void ReleaseZdoOwner(ZDO zdo)
        {
            if (zdo == null) return;
            try { zdo.SetOwner(0L); }
            catch (System.Exception ex) { FireLogger.Debug($"ReleaseZdoOwner threw: {ex.Message}"); }
        }

        /// <summary>
        /// Every machine that could lay terrain paint right now: each ready peer at its reference
        /// position, plus this machine itself when it is a listen host. A dedicated server has no
        /// terrain where fires burn and is never a candidate. Peer ids are session ids, the same
        /// currency ZDO ownership and routed-RPC targets use.
        /// </summary>
        public static void CollectPaintCandidates(List<PaintCandidate> into)
        {
            into.Clear();
            ZNet net = ZNet.instance;
            if (net == null) return;
            List<ZNetPeer> peers = net.GetPeers();
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    ZNetPeer peer = peers[i];
                    if (peer == null || !peer.IsReady()) continue;
                    into.Add(new PaintCandidate { PeerId = peer.m_uid, Position = peer.GetRefPos() });
                }
            }
            if (!IsDedicatedServer())
                into.Add(new PaintCandidate { PeerId = LocalSessionId(), Position = net.GetReferencePosition() });
        }

        public static long LocalSessionId() => ZDOMan.GetSessionID();

        // --- Terrain height ---
        private static readonly MethodInfo ZoneSystemGetGroundHeightMethod =
            typeof(ZoneSystem).GetMethod("GetGroundHeight", AnyInstance, null, new[] { typeof(Vector3) }, null);
        // Valheim 1.0.7 RENAMED this backing field m_instance -> s_instance (ZNetScene and
        // EnvMan below were always s_; ZoneSystem and GameCamera were the m_ holdouts, and
        // only ZoneSystem moved). Nothing about that is compile-visible: the lookup simply
        // returned null, GetWaterLevel fell back to -10000, and the "water blocks ground
        // spread" rule silently stopped applying — fire crossing rivers, with only a Debug
        // line to say so. Both spellings are tried so this works on either game version.
        //
        // The sturdier answer is the public `ZoneSystem.instance` PROPERTY, which exists
        // under that name in both versions and cannot be renamed out from under us without
        // breaking the build loudly. Left as reflection here only to keep this port minimal.
        private static readonly FieldInfo ZoneSystemInstanceField =
            typeof(ZoneSystem).GetField("s_instance", AnyStatic)
            ?? typeof(ZoneSystem).GetField("m_instance", AnyStatic);

        // Valheim's own terrain colliders live on a layer literally named
        // "terrain" — a standard Unity LayerMask lookup by name, not fragile
        // member reflection. Used to raycast straight down for the real
        // surface height ourselves, bypassing ZoneSystem.GetGroundHeight
        // entirely.
        private static readonly int TerrainLayerMask = LayerMask.GetMask("terrain");

        /// <summary>
        /// Samples the real terrain height at a given (x,z).
        ///
        /// Confirmed via a real dedicated-server test: the reflected
        /// ZoneSystem.GetGroundHeight call didn't just occasionally fail — over
        /// an entire session, hundreds of calls across dozens of meters of
        /// terrain, it returned the exact same unresolved echo (the synthetic
        /// 10000 query height) every single time, 100% failure. Whatever
        /// internal state that method depends on (a per-zone heightmap being
        /// "ready") apparently never becomes true on this platform, so the
        /// fallback of "use the inherited approximate y" wasn't actually a rare
        /// safety net — it was silently running for every ground cell in every
        /// fire, permanently pinning every cell to the ORIGINAL ignition
        /// height regardless of real terrain, which is exactly the reported
        /// "floating fire" (visuals not on the ground) and the reason standing
        /// in visible ground fire dealt no damage (the damage zone was
        /// positioned at the wrong height too).
        ///
        /// Fixed by sampling terrain directly via Physics.Raycast against
        /// Valheim's own "terrain" layer instead of trusting the reflected
        /// call at all. This doesn't depend on ZoneSystem's internal
        /// heightmap-readiness state the way that method apparently does.
        /// The old reflected path is kept as a secondary fallback only in case
        /// the raycast itself ever fails (e.g. terrain collider not loaded).
        /// </summary>
        private static bool _groundHeightLoggedOnce;
        private static bool _terrainLayerMaskLoggedOnce;

        // WorldGenerator computes terrain height ANALYTICALLY - it is what generates the terrain in
        // the first place - so unlike every collider-based route it works perfectly on a headless
        // server. Reflected for the usual reason. Verified public in the shipping SERVER assembly:
        // `public static WorldGenerator instance` and `public float GetHeight(float wx, float wy)`.
        private static readonly PropertyInfo WorldGeneratorInstanceProp =
            typeof(WorldGenerator).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo WorldGeneratorGetHeightMethod =
            typeof(WorldGenerator).GetMethod("GetHeight", AnyInstance, null, new[] { typeof(float), typeof(float) }, null);
        private static bool _worldGenHeightLoggedOnce;

        public static float GetGroundHeight(Vector3 xzPosition) => GetGroundHeight(xzPosition, out _);

        /// <summary>
        /// The ground height under a point, and - the reason this overload exists - whether that is a
        /// REAL measurement or just the input handed back.
        ///
        /// The distinction is not academic. Both collider routes below need terrain colliders, a
        /// dedicated server has none, and both then quietly return their own input: 14,518 misses and
        /// zero hits in one logged session. Every caller that stored the result was storing the height
        /// of whatever started the fire. 0.21.9 then hung a 1m vertical damage band off that value and
        /// silently disarmed fire damage on exactly the platform 0.21.8 had just fixed it for.
        /// So: measure properly where we can, and where we cannot, SAY SO.
        /// </summary>
        public static float GetGroundHeight(Vector3 xzPosition, out bool sampled)
        {
            sampled = true;
            if (!_terrainLayerMaskLoggedOnce)
            {
                _terrainLayerMaskLoggedOnce = true;
                // If "terrain" isn't a real Unity layer name in this game version,
                // GetMask silently returns 0 (matches nothing) rather than
                // throwing — same failure shape as every other reflection gotcha
                // in this file, so it needs the same one-time visibility.
                FireLogger.Debug($"[IGNITE-TRACE] GetGroundHeight: TerrainLayerMask = {TerrainLayerMask} " +
                                  $"(binary {System.Convert.ToString(TerrainLayerMask, 2)}) — 0 means the 'terrain' " +
                                  "layer name didn't resolve and this raycast will never hit anything.");
            }

            if (Physics.Raycast(new Vector3(xzPosition.x, 5000f, xzPosition.z), Vector3.down,
                    out RaycastHit hit, 10000f, TerrainLayerMask))
            {
                if (!_groundHeightLoggedOnce)
                {
                    _groundHeightLoggedOnce = true;
                    FireLogger.Debug($"[IGNITE-TRACE] GetGroundHeight: raycast against 'terrain' layer succeeded, " +
                                      $"real sample = {hit.point.y:F2} at ({xzPosition.x:F1},{xzPosition.z:F1}) " +
                                      $"— inherited input y was {xzPosition.y:F2}.");
                }
                return hit.point.y;
            }

            object instance = ZoneSystemInstanceField?.GetValue(null);
            if (instance == null || ZoneSystemGetGroundHeightMethod == null)
            {
                return WorldGenHeight(xzPosition, out sampled);
            }

            Vector3 queryPoint = new Vector3(xzPosition.x, 10000f, xzPosition.z);
            object result;
            try
            {
                result = ZoneSystemGetGroundHeightMethod.Invoke(instance, new object[] { queryPoint });
            }
            catch (System.Exception)
            {
                return xzPosition.y;
            }

            if (!(result is float f) || f > 9000f)
            {
                // ZoneSystem.GetGroundHeight is itself just that same raycast with a fallback of
                // "return the query point's own y" (decompiled from the shipping assembly), and we
                // pass 10000f in, so this branch IS the headless case every time.
                return WorldGenHeight(xzPosition, out sampled);
            }

            return f;
        }

        /// <summary>
        /// The last resort that actually works headless. Returns the generated terrain height, which
        /// ignores player terraforming - close enough to place fire, and immeasurably better than the
        /// alternative of handing back the height of whatever object started the fire.
        /// </summary>
        private static float WorldGenHeight(Vector3 xzPosition, out bool sampled)
        {
            sampled = false;
            if (WorldGeneratorInstanceProp == null || WorldGeneratorGetHeightMethod == null) return xzPosition.y;

            object gen = WorldGeneratorInstanceProp.GetValue(null);
            if (gen == null) return xzPosition.y; // world not generated yet

            try
            {
                object h = WorldGeneratorGetHeightMethod.Invoke(gen, new object[] { xzPosition.x, xzPosition.z });
                if (!(h is float f) || float.IsNaN(f) || float.IsInfinity(f)) return xzPosition.y;

                if (!_worldGenHeightLoggedOnce)
                {
                    _worldGenHeightLoggedOnce = true;
                    FireLogger.Debug($"[IGNITE-TRACE] GetGroundHeight: no terrain colliders here, so heights come " +
                                      $"from WorldGenerator instead — {f:F2} at ({xzPosition.x:F1},{xzPosition.z:F1}), " +
                                      $"against an inherited input y of {xzPosition.y:F2}.");
                }
                sampled = true;
                return f;
            }
            catch (System.Exception ex)
            {
                if (!_worldGenHeightLoggedOnce)
                {
                    _worldGenHeightLoggedOnce = true;
                    FireLogger.Warn($"Could not read terrain height from WorldGenerator ({ex.Message}); fire will be " +
                                    "placed at the height of whatever lit it, and ground fire will not check height.");
                }
                return xzPosition.y;
            }
        }

        // Same publicized-DLL-vs-real-assembly caution as everywhere else in
        // this file — reflecting m_waterLevel defensively rather than trusting
        // its IL-public flag, since that flag has already lied to us twice
        // this session for methods, and the publicizer tool that produced the
        // reference DLL would strip field accessibility the same way.
        private static readonly FieldInfo ZoneSystemWaterLevelField =
            typeof(ZoneSystem).GetField("m_waterLevel", AnyInstance);

        /// <summary>
        /// The world's actual water level (set at world generation, ~30 in a
        /// typical Valheim world but not a hardcoded constant). Returns a
        /// very low fallback (never treats anything as underwater) if the
        /// reflection lookup fails, rather than silently blocking all ground
        /// fire spread on a lookup failure.
        /// </summary>
        private static bool _waterLevelLogged;

        public static float GetWaterLevel()
        {
            object instance = ZoneSystemInstanceField?.GetValue(null);
            if (instance == null || ZoneSystemWaterLevelField == null)
            {
                if (!_waterLevelLogged)
                {
                    _waterLevelLogged = true;
                    FireLogger.Debug("[IGNITE-TRACE] GetWaterLevel: ZoneSystem.instance or the reflected field is null — " +
                                      "falling back to -10000 (never treats anything as underwater, i.e. the water check silently no-ops).");
                }
                return -10000f;
            }

            object result;
            try
            {
                result = ZoneSystemWaterLevelField.GetValue(instance);
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[IGNITE-TRACE] GetWaterLevel: field read threw: {ex.InnerException?.Message ?? ex.Message}");
                return -10000f;
            }
            float value = result is float f ? f : -10000f;

            if (!_waterLevelLogged)
            {
                _waterLevelLogged = true;
                FireLogger.Debug($"[IGNITE-TRACE] GetWaterLevel resolved to {value:F2} " +
                                  $"(expect ~30 for a normal Valheim world; if this is -10000, the field read silently failed).");
            }

            return value;
        }

        /// <summary>True if the real terrain height at this (x,z) is at or below the world's water level.</summary>
        public static bool IsUnderwater(Vector3 xzPosition) => GetGroundHeight(xzPosition) <= GetWaterLevel();
        // -----------------------------------------------------------------
        // Weather AT A POSITION. EnvMan.s_isWet is what vanilla itself reads,
        // but EnvMan.UpdateEnvironment returns before choosing an environment
        // when there is no main camera, so on a dedicated server the current
        // environment never changes from its startup value and s_isWet is
        // false forever (verified 2026-09-16: zero 'raining True' heartbeats
        // across every test-server run since August, including while the
        // player stood in rain). Wind updates on a separate path with no camera
        // check, which is why the wind reflection below works headless and a
        // flag read never could. So instead of reading the flag, replay
        // vanilla's own selection for the position that matters: weather is
        // deterministic per environment PERIOD (world seconds divided by
        // m_environmentDuration) and biome sector, drawn from a Random seeded
        // with the period number - the same numbers every client computes - so
        // what the server decides here is what a player standing at the fire
        // sees, give or take EnvMan's 2s transition blend. The overrides vanilla
        // applies ahead of the roll (forceenv, the env console command, a
        // random event whose area covers the spot, an alt-biome forced
        // environment, a persistent event whose radius covers the spot) are
        // honoured in the same order - and where vanilla scopes one to the
        // LOCAL player's position (both event kinds), the fire's position is
        // used instead, since headless there is no local player and vanilla's
        // own methods would answer for the origin. Not replicated: EnvZone, the per-player
        // trigger volume a client's own player walks into, and the
        // Ashlands/Deepnorth edge fixup in GetBiome(), which reads a heightmap
        // the server does not load.
        // -----------------------------------------------------------------
        private static readonly FieldInfo EnvManEnvDurationField = typeof(EnvMan).GetField("m_environmentDuration", AnyInstance);
        private static readonly FieldInfo EnvManDebugEnvField = typeof(EnvMan).GetField("m_debugEnv", AnyInstance);
        private static readonly FieldInfo EnvManForceEnvField = typeof(EnvMan).GetField("m_forceEnv", AnyInstance);
        private static readonly MethodInfo EnvManGetEnvMethod =
            typeof(EnvMan).GetMethod("GetEnv", AnyInstance, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo EnvManGetAvailableEnvironmentsMethod =
            typeof(EnvMan).GetMethod("GetAvailableEnvironments", AnyInstance, null, new[] { typeof(BiomeSector) }, null);
        private static readonly MethodInfo EnvManSelectWeightedEnvironmentMethod =
            typeof(EnvMan).GetMethod("SelectWeightedEnvironment", AnyInstance, null, new[] { typeof(List<EnvEntry>) }, null);
        private static readonly MethodInfo EnvManGetCurrentEnvironmentMethod =
            typeof(EnvMan).GetMethod("GetCurrentEnvironment", AnyInstance, null, System.Type.EmptyTypes, null);
        private static bool _weatherFailureLogged;

        // Wet-or-not per 64m zone per environment period. A period is minutes
        // long and a zone is a fixed square, so every rain check the simulation
        // makes between two rolls is one dictionary lookup. Cleared on a new
        // period, and whenever one of the global overrides changes.
        private static readonly Dictionary<long, bool> _wetByZoneAndPeriod = new Dictionary<long, bool>();
        private static long _wetCachePeriod = long.MinValue;
        private static string _seenForceEnv, _seenDebugEnv;
        private static RandomEvent _seenRandomEvent;
        private static int _seenPersistentEventCount = -1;

        /// <summary>True if rain is falling at this world position, as vanilla would show it to a player standing there.</summary>
        public static bool IsRainingAt(Vector3 position)
        {
            object envMan = EnvManInstanceField?.GetValue(null);
            if (envMan == null) return false;
            long period = CurrentEnvironmentPeriod(envMan);
            if (period < 0) return false;
            if (period != _wetCachePeriod || OverridesChanged(envMan))
            {
                _wetByZoneAndPeriod.Clear();
                _wetCachePeriod = period;
            }

            int zx = (Mathf.FloorToInt(position.x / 64f) + 512) & 0x3FF;
            int zz = (Mathf.FloorToInt(position.z / 64f) + 512) & 0x3FF;
            long key = (period << 20) | ((long)zx << 10) | (long)zz;
            if (_wetByZoneAndPeriod.TryGetValue(key, out bool cached)) return cached;

            EnvSetup env = ResolveEnvironmentAt(position, out _);
            bool wet = env != null && env.m_isWet;
            _wetByZoneAndPeriod[key] = wet;
            return wet;
        }

        /// <summary>
        /// The environment vanilla would be showing a player standing at this
        /// position right now, or null if EnvMan or the world is not up.
        /// <paramref name="source"/> names the rule that chose it, for fireweather.
        /// </summary>
        public static EnvSetup ResolveEnvironmentAt(Vector3 position, out string source)
        {
            source = "unavailable";
            object envMan = EnvManInstanceField?.GetValue(null);
            if (envMan == null || ZNet.instance == null || WorldGenerator.instance == null) return null;
            if (EnvManGetAvailableEnvironmentsMethod == null || EnvManSelectWeightedEnvironmentMethod == null || EnvManEnvDurationField == null)
            {
                if (!_weatherFailureLogged)
                {
                    _weatherFailureLogged = true;
                    FireLogger.Warn("[WEATHER] EnvMan reflection lookup failed (GetAvailableEnvironments / SelectWeightedEnvironment / " +
                                    "m_environmentDuration) - rain will never be detected. Game update?");
                }
                return null;
            }

            // 1. forceenv - GetCurrentEnvironment honours it above everything else.
            string force = EnvManForceEnvField?.GetValue(envMan) as string;
            if (!string.IsNullOrEmpty(force))
            {
                EnvSetup forced = EnvironmentByName(envMan, force);
                if (forced != null) { source = "forceenv"; return forced; }
            }

            // 2. The chain EnvMan.GetEnvironmentOverride walks, in its order, with
            //    the fire's position standing in for the local player's.
            BiomeSector sector = WorldGenerator.instance.GetBiomeSector(position);
            string name = EnvManDebugEnvField?.GetValue(envMan) as string;
            source = "env command";
            if (string.IsNullOrEmpty(name)) { name = RandomEventOverrideAt(position, sector); source = "random event"; }
            if (string.IsNullOrEmpty(name) && sector != null && sector.AltBiomes != null)
            {
                foreach (AltBiome alt in sector.AltBiomes)
                {
                    if (string.IsNullOrEmpty(alt.m_forceEnvironment)) continue;
                    name = alt.m_forceEnvironment; source = "alt-biome forced environment"; break;
                }
            }
            if (string.IsNullOrEmpty(name)) { name = PersistentEventOverrideAt(position); source = "persistent event"; }
            if (!string.IsNullOrEmpty(name)) return EnvironmentByName(envMan, name);

            // 3. The deterministic roll every client makes for this period and sector.
            long period = CurrentEnvironmentPeriod(envMan);
            if (period < 0 || sector == null) return null;
            bool ashlands = WorldGenerator.IsAshlands(position.x, position.z);
            bool deepnorth = WorldGenerator.IsDeepnorth(position.x, position.y); // sic - vanilla passes y here too, and agreeing with the client matters more than the geometry
            Random.State saved = Random.state;
            Random.InitState((int)period);
            try
            {
                List<EnvEntry> envs = EnvManGetAvailableEnvironmentsMethod.Invoke(envMan, new object[] { sector }) as List<EnvEntry>;
                if (envs == null || envs.Count == 0) return null;
                EnvSetup env = EnvManSelectWeightedEnvironmentMethod.Invoke(envMan, new object[] { envs }) as EnvSetup;
                foreach (EnvEntry entry in envs)
                {
                    if (entry.m_ashlandsOverride && ashlands) env = entry.m_env;
                    if (entry.m_deepnorthOverride && deepnorth) env = entry.m_env;
                }
                source = "period " + period + " in " + sector.Biome;
                return env;
            }
            finally
            {
                Random.state = saved; // vanilla restores it too; the roll must not disturb anyone else's randomness
            }
        }

        /// <summary>Vanilla's own current environment and wet flag - on a client, what the player is looking at - for comparing against the replay.</summary>
        public static string VanillaWeatherForStatus()
        {
            object envMan = EnvManInstanceField?.GetValue(null);
            EnvSetup current = envMan != null ? EnvManGetCurrentEnvironmentMethod?.Invoke(envMan, null) as EnvSetup : null;
            return current == null ? "no environment yet" : $"'{current.m_name}' wet={current.m_isWet} (EnvMan.IsWet={EnvMan.IsWet()})";
        }

        /// <summary>
        /// Sets EnvMan.m_debugEnv on THIS process - the field vanilla's own 'env'
        /// console command writes - so a test can make it rain on the server.
        /// 'env' itself only ever reaches the client it is typed on, which is why
        /// a player standing in forced rain sees the server answer 'Clear'. Empty
        /// clears. Not saved; a restart forgets it. Returns a message for the console.
        /// </summary>
        public static string SetDebugEnvironment(string name)
        {
            object envMan = EnvManInstanceField?.GetValue(null);
            if (envMan == null || EnvManDebugEnvField == null) return "EnvMan is not available here.";
            name = name ?? "";
            EnvSetup env = string.IsNullOrEmpty(name) ? null : EnvironmentByName(envMan, name);
            if (!string.IsNullOrEmpty(name) && env == null)
                return $"no environment named '{name}' (names are case-sensitive: Clear, Rain, LightRain, ThunderStorm, Misty, Snow, ...).";
            EnvManDebugEnvField.SetValue(envMan, name);
            _wetByZoneAndPeriod.Clear();
            return env == null
                ? "debug environment cleared - weather follows the world again."
                : $"debug environment forced to '{env.m_name}' (wet={env.m_isWet}) for every fire on this server until 'fireweather reset'.";
        }

        private static long CurrentEnvironmentPeriod(object envMan)
        {
            if (ZNet.instance == null) return -1;
            object d = EnvManEnvDurationField?.GetValue(envMan);
            long duration = d is long l ? l : 0L;
            if (duration <= 0) return -1;
            return (long)ZNet.instance.GetTimeSeconds() / duration;
        }

        private static EnvSetup EnvironmentByName(object envMan, string name) =>
            EnvManGetEnvMethod?.Invoke(envMan, new object[] { name }) as EnvSetup;

        // RandEventSystem.GetEnvOverride answers for the LOCAL player: the event
        // is only 'active' on a client whose player stands inside its range, and
        // InEventBiome reads EnvMan's camera-derived biome. Headless neither
        // exists, so the server's copy of that method says nothing during a raid
        // that is forcing a thunderstorm over someone's base. Same test, same
        // fields, the fire's position instead: inside m_eventRange of the
        // world's current random event, and in one of its biomes.
        private static string RandomEventOverrideAt(Vector3 position, BiomeSector sector)
        {
            RandomEvent current = RandEventSystem.instance != null ? RandEventSystem.instance.GetCurrentRandomEvent() : null;
            if (current == null || string.IsNullOrEmpty(current.m_forceEnvironment)) return null;
            if (position.y > 3000f) return null; // vanilla's own 'not on the ground' guard
            float dx = position.x - current.m_pos.x, dz = position.z - current.m_pos.z;
            if (dx * dx + dz * dz >= current.m_eventRange * current.m_eventRange) return null;
            if (sector != null && (sector.Biome & current.m_biome) == 0) return null;
            return current.m_forceEnvironment;
        }

        // PersistentEventSystem.GetEnvironmentOverride measures from the local
        // player - Vector3.zero headless, which is a real place in the world and
        // the wrong one. Same walk over the same public list, from the fire.
        private static string PersistentEventOverrideAt(Vector3 position)
        {
            PersistentEventSystem system = PersistentEventSystem.instance;
            if (system == null || system.m_activePersistentEvents == null) return null;
            foreach (PersistentEventSystem.ActivePersistentEvent item in system.m_activePersistentEvents.list)
            {
                if ((item.position - position).sqrMagnitude >= item.radius * item.radius) continue;
                PersistentEventSystem.PersistentEvent sourceEvent = item.Source;
                return sourceEvent != null ? sourceEvent.GetEnvironmentOverride(position) : null;
            }
            return null;
        }

        // Compares against the last-seen values without building a string, so
        // the per-check cost stays at four reads and four compares. A random
        // event is a new object each time one starts (SetRandomEvent clones),
        // and the persistent list only grows or shrinks, so identity and count
        // are enough to know the position-scoped answers may have changed.
        private static bool OverridesChanged(object envMan)
        {
            string force = EnvManForceEnvField?.GetValue(envMan) as string;
            string debug = EnvManDebugEnvField?.GetValue(envMan) as string;
            RandomEvent randomEvent = RandEventSystem.instance != null ? RandEventSystem.instance.GetCurrentRandomEvent() : null;
            PersistentEventSystem persistent = PersistentEventSystem.instance;
            int persistentCount = persistent != null && persistent.m_activePersistentEvents != null ? persistent.m_activePersistentEvents.list.Count : 0;
            bool changed = !string.Equals(force, _seenForceEnv) || !string.Equals(debug, _seenDebugEnv)
                        || !ReferenceEquals(randomEvent, _seenRandomEvent) || persistentCount != _seenPersistentEventCount;
            _seenForceEnv = force; _seenDebugEnv = debug; _seenRandomEvent = randomEvent; _seenPersistentEventCount = persistentCount;
            return changed;
        }

        private static readonly FieldInfo EnvManInstanceField = typeof(EnvMan).GetField("s_instance", AnyStatic);
        private static readonly MethodInfo EnvManGetWindDirMethod =
            typeof(EnvMan).GetMethod("GetWindDir", AnyInstance, null, new System.Type[0], null);
        private static readonly MethodInfo EnvManGetWindIntensityMethod =
            typeof(EnvMan).GetMethod("GetWindIntensity", AnyInstance, null, new System.Type[0], null);
        private static bool _windIntensityFailureLogged;
        private static int _windFrame = -1;
        private static Vector3? _windDirCache;
        private static float? _windIntensityCache;

        /// <summary>
        /// Wind is ONE global value that cannot change within a frame, but every caller was
        /// paying a reflected static-field read plus a reflected Invoke for it, each boxing its
        /// result: IgniteAdjacentGroundCells once per burning ground cell per spread cycle, and
        /// the VFX controllers once per burning object per frame. Memoizing on Time.frameCount is
        /// behaviour-identical and fixes every caller without any of them having to change.
        /// </summary>
        private static void RefreshWindCache()
        {
            if (Time.frameCount == _windFrame) return;
            _windFrame = Time.frameCount;
            _windDirCache = GetWindDirectionUncached();
            _windIntensityCache = GetWindIntensityUncached();
        }

        /// <summary>Current wind direction, memoized for this frame. See RefreshWindCache.</summary>
        public static Vector3? GetWindDirection()
        {
            RefreshWindCache();
            return _windDirCache;
        }

        /// <summary>Current wind strength, memoized for this frame. See RefreshWindCache.</summary>
        public static float? GetWindIntensity()
        {
            RefreshWindCache();
            return _windIntensityCache;
        }

        /// <summary>
        /// Current wind direction per vanilla's own EnvMan state, or null if EnvMan
        /// isn't up yet (world not loaded) or the reflection lookup failed. Verified
        /// against the publicized DLL: EnvMan.s_instance is a public static field,
        /// GetWindDir() is a public parameterless instance method returning Vector3.
        /// </summary>
        private static Vector3? GetWindDirectionUncached()
        {
            object instance = EnvManInstanceField?.GetValue(null);
            if (instance == null || EnvManGetWindDirMethod == null) return null;

            object result = EnvManGetWindDirMethod.Invoke(instance, null);
            return result is Vector3 v ? v : (Vector3?)null;
        }

        /// <summary>
        /// Current wind strength per vanilla's own EnvMan state, or null if EnvMan
        /// isn't up yet (world not loaded) or the reflection lookup failed. Verified
        /// against the decompiled body, not just the signature: GetWindIntensity()
        /// returns m_wind.w, and every write to m_wind goes through SetTargetWind,
        /// which clamps intensity to 0.05-1. So this reads ~0.05 (dead calm) to 1
        /// (gale) once the world is actually running — never a true 0. The field's
        /// pre-UpdateWind initial value IS 0 though, so callers should treat 0 as
        /// "no wind data yet" rather than as a real calm reading.
        /// </summary>
        private static float? GetWindIntensityUncached()
        {
            object instance = EnvManInstanceField?.GetValue(null);
            if (instance == null || EnvManGetWindIntensityMethod == null)
            {
                if (!_windIntensityFailureLogged)
                {
                    _windIntensityFailureLogged = true;
                    FireLogger.Debug($"GetWindIntensity unavailable (instance null: {instance == null}, " +
                                     $"method null: {EnvManGetWindIntensityMethod == null}). " +
                                     "Wind bias falls back to full strength, ignoring live intensity.");
                }
                return null;
            }

            object result = EnvManGetWindIntensityMethod.Invoke(instance, null);
            return result is float f ? f : (float?)null;
        }

        // --- Vanilla light helpers: LightFlicker, LightLod ---
        // Both components expose their tunables as public fields, which is exactly how these
        // two got missed: the ONE field on each that matters for driving the light at runtime
        // is private. LightFlicker captures m_baseIntensity in Awake and multiplies it into the
        // Light every update, so a flickering light's brightness can only be changed through
        // it. LightLod captures m_baseRange the same way and ramps the Light's range back up to
        // it every second. Both are `private float` in the shipping assembly (ilspycmd,
        // 2026-09-20) and public only in the publicized reference; direct writes compiled clean
        // and threw FieldAccessException on every frame of every fire, on every client.
        //
        // The flicker base is written per frame per burning object, so it is a Harmony
        // FieldRef (one delegate, then a plain field store) rather than FieldInfo.SetValue,
        // which boxes a float per call.
        private static readonly HarmonyLib.AccessTools.FieldRef<LightFlicker, float> FlickerBaseIntensityRef =
            ResolveFlickerBaseIntensityRef();
        private static readonly FieldInfo LightLodBaseRangeField = typeof(LightLod).GetField("m_baseRange", AnyInstance);
        private static bool _flickerRefFailureLogged;

        private static HarmonyLib.AccessTools.FieldRef<LightFlicker, float> ResolveFlickerBaseIntensityRef()
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<LightFlicker, float>("m_baseIntensity"); }
            catch (System.Exception) { return null; }
        }

        /// <summary>
        /// Sets the base brightness LightFlicker multiplies into its Light. Returns false if the
        /// field could not be resolved, so the caller can write Light.intensity instead and at
        /// least have a light - LightFlicker will then hold it at its own captured base.
        /// </summary>
        public static bool TrySetFlickerBaseIntensity(LightFlicker flicker, float value)
        {
            if (flicker == null) return false;
            if (FlickerBaseIntensityRef == null)
            {
                if (!_flickerRefFailureLogged)
                {
                    _flickerRefFailureLogged = true;
                    FireLogger.Warn("LightFlicker.m_baseIntensity could not be resolved, so fire lights flicker at a " +
                                    "fixed brightness instead of swelling with the burn. Everything else is unaffected.");
                }
                return false;
            }
            FlickerBaseIntensityRef(flicker) = value;
            return true;
        }

        /// <summary>
        /// Sets the range LightLod ramps its Light back up to. Returns false if unresolved.
        /// Set Light.range as well: lowering the base does NOT lower a range that is already at
        /// full, because LightLod's ramp-up only runs while range is below the base and its
        /// ramp-down only runs beyond m_lightDistance. Inside 40 m, the base alone changes nothing.
        /// </summary>
        public static bool TrySetLightLodBaseRange(LightLod lod, float range)
        {
            if (lod == null || LightLodBaseRangeField == null) return false;
            LightLodBaseRangeField.SetValue(lod, range);
            return true;
        }

        // --- Player feedback messages ---
        private static readonly MethodInfo PlayerMessageMethod =
            // 1.0.7 appended `bool log = false`, which changes the signature even though it
            // has a default — an explicit types lookup stops matching and the player stops
            // getting told anything.
            typeof(Player).GetMethod("Message", AnyInstance, null,
                new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) }, null);

        /// <summary>Shows a top-left HUD message to the local player, if one exists.</summary>
        public static void ShowPlayerMessage(string text)
        {
            Player local = LocalPlayerField?.GetValue(null) as Player;
            if (local == null || PlayerMessageMethod == null) return;
            // trailing false = 1.0.7's `log`, its own default.
            PlayerMessageMethod.Invoke(local, new object[] { MessageHud.MessageType.TopLeft, text, 0, null, false });
        }
        private static readonly MethodInfo CharacterAddFireDamageMethod =
            // 1.0.7: AddFireDamage(float) -> AddFireDamage(float, short variant), with NO
            // default on the new parameter. This is the break that mattered most here — the
            // lookup returned null and fire simply stopped hurting anything. `variant` is
            // handed straight to SEMan.AddStatusEffect, whose own default is -1, so -1 is the
            // value that means "the burning effect as it was before 1.0".
            typeof(Character).GetMethod("AddFireDamage", AnyInstance, null,
                new[] { typeof(float), typeof(short) }, null);
        private static readonly MethodInfo CharacterGetSEManMethod =
            typeof(Character).GetMethod("GetSEMan", AnyInstance, null, System.Type.EmptyTypes, null);
        private static readonly FieldInfo SEManBurningStatusField =
            typeof(SEMan).GetField("s_statusEffectBurning", AnyStatic);
        private static readonly MethodInfo SEManAddStatusEffectIntMethod =
            // 1.0.7 changed the fourth parameter's TYPE (int skillLevel -> float) and appended
            // `short variant = -1`. Two reasons one lookup can go stale at once.
            typeof(SEMan).GetMethod("AddStatusEffect", AnyInstance, null,
                new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) }, null);
        private static readonly MethodInfo SEManAddStatusEffectObjMethod =
            typeof(SEMan).GetMethod("AddStatusEffect", AnyInstance, null,
                new[] { typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short) }, null);

        private static readonly FieldInfo EffectAreaCharacterMaskField =
            typeof(EffectArea).GetField("s_characterMask", AnyStatic);

        /// <summary>
        /// The exact LayerMask vanilla's own EffectArea uses to find characters —
        /// reused so FireBurnZone's Physics.OverlapSphere polling matches
        /// whatever layer characters actually live on, sidestepping the need to
        /// guess (our dynamically-created GameObjects sit on the default layer,
        /// which OnTriggerStay/Enter never fired against — likely blocked by
        /// Valheim's physics collision matrix; explicit OverlapSphere queries
        /// ignore that matrix entirely and only care about the LayerMask param).
        /// </summary>
        private static bool _characterMaskLoggedOnce;

        public static LayerMask GetCharacterLayerMask()
        {
            object value = EffectAreaCharacterMaskField?.GetValue(null);
            int? resolved = value is LayerMask lm ? lm.value : (value is int i ? i : (int?)null);

            // Confirmed via a real dedicated-server test: resolved = exactly 0.
            // EffectArea.s_characterMask is a static field vanilla apparently
            // only populates from an INSTANCE's own Awake() (e.g. a lit
            // campfire/bonfire's EffectArea) rather than a static initializer —
            // if no such instance has run yet anywhere in the loaded world, the
            // field just sits at C#'s default int value, 0. A LayerMask of 0
            // matches NO layers at all, so every Physics.OverlapSphere query
            // built from it silently finds nothing, forever — indistinguishable
            // from "no character was ever nearby" without this check. Treated
            // the same as an unresolved field: fall back to ~0 (everything),
            // which is always safe since callers still filter by Character
            // component afterward. Not cached — re-reads the field every call,
            // so as soon as some real EffectArea instance initializes it later
            // in the session, this starts returning the real mask automatically.
            if (resolved.HasValue && resolved.Value != 0)
            {
                if (!_characterMaskLoggedOnce)
                {
                    _characterMaskLoggedOnce = true;
                    FireLogger.Debug($"[IGNITE-TRACE] GetCharacterLayerMask: resolved EffectArea.s_characterMask = " +
                                      $"{resolved.Value} (binary {System.Convert.ToString(resolved.Value, 2)}).");
                }
                return (LayerMask)resolved.Value;
            }

            if (!_characterMaskLoggedOnce)
            {
                _characterMaskLoggedOnce = true;
                FireLogger.Debug($"[IGNITE-TRACE] GetCharacterLayerMask: field null={EffectAreaCharacterMaskField == null}, " +
                                  $"resolved value={(resolved.HasValue ? resolved.Value.ToString() : "null/wrong-type")} " +
                                  "— treating as unresolved (0 is an empty mask that would match nothing) and falling back to LayerMask ~0 (everything).");
            }
            return (LayerMask)(~0); // fallback: everything — safe since we still filter by Character component afterward
        }

        /// <summary>
        /// Applies a fire damage tick to a Character. Calling Character.AddFireDamage
        /// alone (0.9.0/0.9.1) produced a frozen "burning" status timer and no real
        /// damage — consistent with AddFireDamage only queueing into SE_Burning's
        /// internal damage pool without actually attaching/refreshing a running
        /// status effect instance to process it. Fixed by also explicitly attaching
        /// (and continuously refreshing — called every tick while in a fire zone)
        /// the real Burning status effect via SEMan.AddStatusEffect, using
        /// SEMan.s_statusEffectBurning — the same reference vanilla itself uses —
        /// rather than a guessed hash. The field's actual runtime type (int hash vs
        /// StatusEffect reference) is checked dynamically so we call whichever
        /// overload actually matches, rather than guessing the compile-time type.
        /// </summary>
        private static bool _fireDamageTickLoggedOnce;

        public static void ApplyFireDamageTick(Character character, float damage)
        {
            if (character == null) return;

            // Every reflected call below was previously unguarded — an exception
            // anywhere in here (a signature drift, a null on an unexpected code
            // path) would throw up out of FireBurnZone.Update() with no FireFront
            // log line at all, indistinguishable from "no character was ever in
            // range." Wrapped so a real failure is at least visible once instead
            // of silently eating all fire damage forever.
            try
            {
                object seman = CharacterGetSEManMethod?.Invoke(character, null);
                bool addedStatusEffect = false;
                if (seman != null)
                {
                    object burningRef = SEManBurningStatusField?.GetValue(null);
                    if (burningRef is int hash && SEManAddStatusEffectIntMethod != null)
                    {
                        // 1f, not 1: skillLevel is a float in 1.0.7. Trailing -1 is `variant`,
                        // vanilla's own default, meaning the plain burning effect.
                        SEManAddStatusEffectIntMethod.Invoke(seman, new object[] { hash, true, 1, 1f, (short)-1 });
                        addedStatusEffect = true;
                    }
                    else if (burningRef != null && SEManAddStatusEffectObjMethod != null)
                    {
                        SEManAddStatusEffectObjMethod.Invoke(seman, new object[] { burningRef, true, 1, 1f, (short)-1 });
                        addedStatusEffect = true;
                    }
                }

                CharacterAddFireDamageMethod?.Invoke(character, new object[] { damage, (short)-1 });

                if (!_fireDamageTickLoggedOnce)
                {
                    _fireDamageTickLoggedOnce = true;
                    FireLogger.Debug($"[IGNITE-TRACE] ApplyFireDamageTick: seman resolved={seman != null}, " +
                                      $"statusEffectAttached={addedStatusEffect}, AddFireDamage method null={CharacterAddFireDamageMethod == null} " +
                                      $"— applied to {character.gameObject.name}.");
                }
            }
            catch (System.Exception ex)
            {
                if (!_fireDamageTickLoggedOnce)
                {
                    _fireDamageTickLoggedOnce = true;
                    FireLogger.Info($"[IGNITE-TRACE] ApplyFireDamageTick THREW for {character.gameObject.name}: " +
                                     $"{ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        public static bool IsPlayerCharacter(Character character) => character is Player;

        /// <summary>
        /// Attaches our own FireBurnZone (see Fire/FireBurnZone.cs) to the given
        /// GameObject. Detection is done via Physics.OverlapSphere polling
        /// (using EffectArea's own verified character layer mask), not Unity
        /// trigger events — OnTriggerStay never fired in testing (0.9.0), most
        /// likely because our dynamically-created GameObject's default layer is
        /// blocked from generating trigger callbacks against characters by
        /// Valheim's physics collision matrix. Explicit OverlapSphere queries
        /// bypass that matrix entirely, so no collider/trigger setup is needed
        /// at all now — just the radius to query.
        /// </summary>
        public static void AttachFireDamageZone(GameObject go, float radius, bool playerOnly, float damagePerTick, float tickInterval)
        {
            if (go == null) return;

            FireBurnZone zone = go.AddComponent<FireBurnZone>();
            zone.Radius = radius;
            zone.PlayerOnly = playerOnly;
            zone.DamagePerTick = damagePerTick;
            zone.TickInterval = tickInterval;
        }

        private static Texture2D _cachedScorchTexture;

        /// <summary>Dark, soft-edged radial texture for burn scars — same approach as
        /// the fire particle texture, just dark instead of bright.</summary>
        private static Texture2D GetOrCreateScorchTexture()
        {
            if (_cachedScorchTexture != null) return _cachedScorchTexture;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var center = new Vector2(size / 2f, size / 2f);
            float maxDist = size / 2f;
            const float noiseScale = 6f;
            float noiseOffset = Random.Range(0f, 1000f);

            // Dark umber/brown dirt tones, not near-black — reads as burnt
            // earth rather than a dark smudge. Perlin noise breaks up the
            // color so it looks like mottled dirt, not a flat painted circle.
            var dirtDark = new Color(0.11f, 0.07f, 0.04f);
            var dirtLight = new Color(0.24f, 0.16f, 0.09f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float edgeFalloff = Mathf.Clamp01(1f - dist / maxDist);
                    edgeFalloff = Mathf.Pow(edgeFalloff, 0.6f); // wider fully-opaque middle, soft fade only near the rim

                    float noise = Mathf.PerlinNoise(x / noiseScale + noiseOffset, y / noiseScale + noiseOffset);
                    Color dirt = Color.Lerp(dirtDark, dirtLight, noise);

                    tex.SetPixel(x, y, new Color(dirt.r, dirt.g, dirt.b, edgeFalloff));
                }
            }
            tex.Apply();
            _cachedScorchTexture = tex;
            return tex;
        }

        /// <summary>
        /// True when the zone under <paramref name="pos"/> is instantiated in this process, so a
        /// terrain collider exists there to probe. On a dedicated server that is only the ring
        /// around the world origin; on a client it is the ring around the player.
        /// </summary>
        public static bool IsZoneLoaded(Vector3 pos)
        {
            ZoneSystem zs = ZoneSystem.instance;
            return zs != null && zs.IsZoneLoaded(pos);
        }

        /// <summary>
        /// The deterministic 42 % keep for a scorch mark at <paramref name="position"/>, decided
        /// by ground cell so every peer that draws the cell, and a re-ignition of it, agree. By
        /// CELL: GroundCellSize goes down to 0.5 m, and keying on the metre would fold
        /// neighbouring cells onto one pick and one jitter, which is the grid this exists to
        /// break. Public so the client's queue can drop what would never spawn.
        /// </summary>
        public static bool ScorchMarkKept(Vector3 position)
        {
            ScorchCell(position, out int cx, out int cz);
            return SharedMedia.ProceduralTextures.Hash(cx, cz, 7) <= 0.42f;
        }

        /// <summary>The ground cell a scorch mark is keyed on, in the local GroundCellSize grid.</summary>
        public static void ScorchCell(Vector3 position, out int cx, out int cz)
        {
            float cell = Mathf.Max(0.01f, FireFront.Config.FireConfig.GroundCellSize.Value);
            cx = Mathf.FloorToInt(position.x / cell);
            cz = Mathf.FloorToInt(position.z / cell);
        }

        /// <summary>
        /// Leaves a burn scar on the ground where a cell went out. Purely cosmetic and
        /// fire-and-forget (self-destructs via Unity's delayed Destroy). Since 0.22.1 it is a
        /// multiply-blended soot blot laid on the terrain's own normal, not an umber disc:
        /// the first client run showed the old one as rows of dark squares - one near-opaque
        /// 1.5 m disc per 1 m cell, feathered rims overlapping LIGHTER than the discs (a
        /// lattice), horizontal quads cutting into slopes, and a palette lighter than a Black
        /// Forest floor, so the tiles read both ways. Now: roughly two blots in five cells,
        /// 1.6-2.2x the cell, jittered, each a noise-warped blot that DARKENS what is lit
        /// (framebuffer times the blot's red channel, fogged the way vanilla's own particles
        /// are - the shader outputs its VERTEX colour, not the texture, so the multiply has to
        /// ride on alpha; see FireFrontTextureGenerator.GetOrCreateScorchMultiplyMaterial for
        /// the GLSL and the blend) so it shows on any floor, in any light, and never lightens an
        /// overlap. The first few placements are logged (position, terrain hits, lift, shader)
        /// so "no marks" is diagnosable as absent vs invisible.
        ///
        /// <paramref name="size"/> is the ground cell itself; the 1.6-2.2x is applied HERE and
        /// nowhere else. Both callers used to pass the cell x 1.5 as well, which NomadicWar
        /// (PR #3, 2026-09-20) caught from the changelog's own numbers: a 2.4-3.3 m blot per
        /// 1 m cell. How deep the multiply stacks is what decides whether burnt ground reads as
        /// soot or as black, so, counting each blot as the disc inscribed in its quad (the
        /// outline the soot texture draws, rim at 0.39-0.5 of the quad width): keep 0.42 per
        /// cell times pi/4 times the mean quad area E[(1.6+0.6h)^2] = 3.64 m^2 puts a burnt
        /// square metre under 1.2 blot outlines on average, and under about 0.5 blots of full
        /// soot tone once the feathered rim and the interior grain are integrated (a 0.30 tone
        /// at 0.5 deep is lit ground at 54 %). With the callers' extra 1.5 those were 2.7 and
        /// 1.1, and 0.30 at 1.1 deep is lit ground at 25 %, which is the near-black reported.
        ///
        /// A quad of that size spans two or three terrain vertices (the heightfield is ~1 m),
        /// so a single centre probe cannot say where its corners lie: one normal and a fixed
        /// 4 cm lift let the far edge of a tilted quad dip under a slope's terrain, the second
        /// finding on PR #3. The quad is therefore fitted to FIVE terrain rays, centre and four
        /// corners, and lifted along the fitted normal by what its highest-standing sample needs.
        /// </summary>
        public static bool SpawnScorchMark(Vector3 position, float size, float lifetimeSeconds)
        {
            if (!ScorchMarkKept(position)) return true; // see ScorchMarkKept: by cell, so every peer agrees; not a failure
            ScorchCell(position, out int cx, out int cz);
            float jx = (SharedMedia.ProceduralTextures.Hash(cx, cz, 11) - 0.5f) * 0.8f;
            float jz = (SharedMedia.ProceduralTextures.Hash(cx, cz, 13) - 0.5f) * 0.8f;
            float scale = size * (1.6f + 0.6f * SharedMedia.ProceduralTextures.Hash(cx, cz, 17));
            Vector3 pos = new Vector3(position.x + jx, position.y, position.z + jz);

            // Sit on the real ground, tilted to it: the client has the terrain collider (the
            // headless server does not, which is why the spawn moved client-side in 0.21.15).
            // The height that arrived with the sync stream is not a terrain height at all on a
            // dedicated server - every cell inherits the Y of whatever seeded the fire - so the
            // probe ignores it and casts the way ZoneSystem.GetGroundHeight does: from far above,
            // terrain layer only (a heightfield, so one crossing at most, and nothing overhangs).
            // No terrain under a loaded zone is a miss, and a miss is no mark: the queue only
            // sends a position here once the zone is loaded, and a quad floating at a made-up
            // height was the failure this exists to remove.
            if (!Physics.Raycast(new Vector3(pos.x, 6000f, pos.z), Vector3.down, out RaycastHit rh, 10000f, TerrainLayerMask))
            {
                FireLogger.Debug($"[SCORCH] no terrain under {pos.x:F0},{pos.z:F0}; mark dropped.");
                return false;
            }

            // Then the four corners, with the same ray. The yaw is chosen first so the corners
            // probed are the quad's OWN corners (its in-plane axes spun by the yaw, then
            // projected to the horizontal - the rays are vertical, so only x/z matter, and the
            // tilt moves a corner's footprint by (1 - cos tilt) of its reach, a few centimetres
            // on any slope a decal belongs on), not an axis-aligned square that would sample
            // ground the quad never covers. Four rays on top of the centre's is cheap where it
            // runs: only the ~42 % of cells that keep a mark reach this line, the queue spawns
            // at most PendingScorchSpawnsPerFrame of them a frame, and a terrain-only ray
            // against a heightfield is about the cheapest cast Unity does.
            float yaw = Random.Range(0f, 360f);
            Quaternion spin = Quaternion.Euler(0f, yaw, 0f);
            Vector3 ax = spin * (Vector3.right * (0.5f * scale));
            Vector3 az = spin * (Vector3.forward * (0.5f * scale));
            Vector3 centre = rh.point;
            Vector3 normalSum = rh.normal;
            int cornerHits = 0;
            Vector3[] cornerPoints = _scorchCornerScratch;
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = centre + ((i & 1) == 0 ? ax : -ax) + ((i & 2) == 0 ? az : -az);
                if (!Physics.Raycast(new Vector3(c.x, 6000f, c.z), Vector3.down, out RaycastHit ch, 10000f, TerrainLayerMask))
                    continue; // a zone edge, most likely: the neighbour is not loaded yet. Fit to what did hit.
                cornerPoints[cornerHits++] = ch.point;
                normalSum += ch.normal;
            }

            // The plane: the mean of the sampled normals, which on a plain slope is the slope
            // and on a crest or a hollow is the average the quad has to split anyway. Fewer
            // than three corners is a zone edge, not a fit; the centre normal stands alone then.
            Vector3 normal = cornerHits >= 3 ? normalSum.normalized : rh.normal;

            // The lift: 4 cm off the fitted plane at the centre, plus however far the HIGHEST
            // sample stands above that plane, so every probed point ends up 4 cm or more under
            // the quad. A sample's height above the centre plane is its signed distance along
            // the normal (the centre's own is zero, so the lift is never under 4 cm). Past
            // MaxScorchLift the terrain is rougher than a flat quad can follow - a hollow, a
            // hoe's ditch, a rock edge - and a decal hanging a third of a metre in the air
            // reads worse than no decal, so the mark is dropped and says so.
            const float MaxScorchLift = 0.35f;
            float lift = 0.04f;
            for (int i = 0; i < cornerHits; i++)
                lift = Mathf.Max(lift, 0.04f + Vector3.Dot(cornerPoints[i] - centre, normal));
            if (lift > MaxScorchLift)
            {
                FireLogger.Debug($"[SCORCH] terrain under {pos.x:F0},{pos.z:F0} too rough for a {scale:F1} m quad (would lift {lift:F2} m, cap {MaxScorchLift:F2}); mark dropped.");
                return false;
            }
            pos = centre + normal * lift; // off the surface, along its normal, not straight up

            var quad = new GameObject("FireFrontScorchMark");
            quad.transform.position = pos;
            quad.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(90f, yaw, 0f);
            quad.transform.localScale = new Vector3(scale, scale, 1f);

            Material shared = GetOrCreateScorchMaterial();
            // The multiply decal wants BLACK vertices (its fog rides on the vertex colour, see
            // FireFrontTextureGenerator.GetOrCreateScorchMultiplyMaterial); the alpha-disc
            // fallback multiplies its texture by the vertex colour and wants white.
            bool multiply = shared != null && shared.name == FireFrontTextureGenerator.ScorchMultiplyMaterialName;
            quad.AddComponent<MeshFilter>().sharedMesh = GetOrCreateQuadMesh(multiply);
            MeshRenderer renderer = quad.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (shared != null) renderer.sharedMaterial = shared;

            if (_scorchLogged < 5)
            {
                _scorchLogged++;
                FireLogger.Info($"[SCORCH] mark {_scorchLogged}: at {pos.x:F1},{pos.y:F1},{pos.z:F1} size {scale:F1} terrain-hits={1 + cornerHits}/5 lift={lift:F2}m normal-tilt={Vector3.Angle(Vector3.up, normal):F0}deg shader={(shared != null && shared.shader != null ? shared.shader.name : "none")} blend={(shared != null && shared.HasProperty("_SrcBlend") ? shared.GetFloat("_SrcBlend") + "/" + shared.GetFloat("_DstBlend") : "n/a")}");
            }

            Object.Destroy(quad, lifetimeSeconds);
            return true;
        }

        private static int _scorchLogged;
        private static readonly Vector3[] _scorchCornerScratch = new Vector3[4]; // main thread only, like every spawn here; no per-mark array

        /// <summary>The first five marks of a session are logged; a reconnect starts that count again.</summary>
        public static void ResetScorchDiagnostics() { _scorchLogged = 0; }

        private static Mesh _cachedQuadMesh;
        private static Mesh _cachedBlackQuadMesh;
        private static Material _cachedScorchMaterial;

        /// <summary>
        /// One quad mesh, shared by every scorch mark ever spawned - two, since 0.22.1's decal
        /// fix: the same quad with white or with black vertex colours, see below.
        /// </summary>
        /// <remarks>
        /// Replaces GameObject.CreatePrimitive(Quad), which built a fresh mesh
        /// AND a MeshCollider per mark only for the collider to be destroyed on
        /// the very next line. Scorch marks spawn per burned ground cell, so on
        /// a spreading front that churn was continuous — and allocation churn on
        /// the render thread is exactly the shape of the periodic GC stall
        /// 0.18.7 chased out of the logging path.
        /// </remarks>
        private static Mesh GetOrCreateQuadMesh(bool blackVertices)
        {
            if (blackVertices && _cachedBlackQuadMesh != null) return _cachedBlackQuadMesh;
            if (!blackVertices && _cachedQuadMesh != null) return _cachedQuadMesh;

            var mesh = new Mesh { name = blackVertices ? "FireFrontQuadBlack" : "FireFrontQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            // Custom/Particle (Unlit) outputs COLOR.rgb and scales its alpha by COLOR.a. The
            // multiply decal rides on that alpha and puts the game's fog through COLOR.rgb -
            // black, so the fragment's RGB is exactly the fog term - and the alpha-blended
            // fallback wants white. Either way the stream is set outright rather than left to
            // Unity's substitute for a missing one (white in practice, promised nowhere); the
            // alpha must be 255 in both, it scales the whole decal.
            Color32 c = blackVertices ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            mesh.colors32 = new[] { c, c, c, c };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (blackVertices) _cachedBlackQuadMesh = mesh; else _cachedQuadMesh = mesh;
            return mesh;
        }

        /// <summary>
        /// Shared scorch material - every mark renders from this one instance. Preferred: the
        /// multiply-blended vanilla particle shader with a generated soot blot. Fallback (no
        /// donor): the 0.21.x alpha-blended umber disc on whatever particle shader resolves.
        /// </summary>
        private static Material GetOrCreateScorchMaterial()
        {
            if (_cachedScorchMaterial != null) return _cachedScorchMaterial;

            Material multiply = null;
            try { multiply = FireFrontTextureGenerator.GetOrCreateScorchMultiplyMaterial(GetOrCreateSootTexture()); }
            catch (System.Exception ex) { FireLogger.Debug($"[SCORCH] multiply material failed ({ex.Message}); alpha fallback."); }
            if (multiply != null) { _cachedScorchMaterial = multiply; return multiply; }

            Shader shader = FindUsableParticleShader();
            if (shader == null) return null;

            _cachedScorchMaterial = new Material(shader) { mainTexture = GetOrCreateScorchTexture() };
            return _cachedScorchMaterial;
        }

        private static Texture2D _cachedSootTexture;

        /// <summary>The multiply-decal blot: white = untouched ground, ~0.3 at the heart, alpha 255 throughout.</summary>
        private static Texture2D GetOrCreateSootTexture()
        {
            if (_cachedSootTexture != null) return _cachedSootTexture;
            const int size = 256;
            Color32[] px = SharedMedia.ProceduralTextures.SootBlot(size, 20260920);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = "FireFront_soot",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            _cachedSootTexture = tex;
            return tex;
        }

        private static Texture2D _cachedSoftParticleTexture;

        /// <summary>
        /// Generates a small radial-gradient texture (white center fading to
        /// transparent edges) entirely in code — fixes the fallback shaders
        /// (Sprites/Default, UI/Default, etc.) rendering particles as hard-edged
        /// squares instead of soft glowing blobs, since those shaders just draw
        /// a flat quad when no texture is assigned. Cached after first build.
        /// </summary>
        private static Texture2D GetOrCreateSoftParticleTexture()
        {
            if (_cachedSoftParticleTexture != null) return _cachedSoftParticleTexture;

            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var center = new Vector2(size / 2f, size / 2f);
            float maxDist = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = Mathf.Clamp01(1f - dist / maxDist);
                    alpha *= alpha; // soften the falloff curve, less "disc with a hard rim"
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            _cachedSoftParticleTexture = tex;
            return tex;
        }

        private static Material _cachedParticleMaterial;

        /// <summary>
        /// Assigns the ONE shared particle material rather than instantiating a
        /// fresh Material per effect. Ground cells alone can spawn up to
        /// GroundVfxMaxConcurrent of these, each previously carrying its own
        /// Material instance — pure per-spawn allocation for a material whose
        /// shader and texture were identical every time. sharedMaterial is the
        /// assignment that does NOT clone; `renderer.material` would silently
        /// instantiate a per-renderer copy and undo the whole point.
        /// </summary>
        private static Texture2D _cachedAdditiveTexture;

        /// <summary>
        /// The soft radial particle texture with its falloff baked into RGB as
        /// well as alpha, for the additive material.
        /// </summary>
        /// <remarks>
        /// GetOrCreateSoftParticleTexture writes white RGB and fades through
        /// alpha, which is right for an alpha-blended shader. It is wrong here:
        /// Custom/Particle (Unlit) selects its alpha channel through
        /// _AlphaChannel and defaults to RED, so a white-RGB texture is fully
        /// opaque everywhere and draws squares. Premultiplying means the edges
        /// are black whichever channel the shader ends up reading, and black
        /// contributes nothing to an additive blend.
        /// </remarks>
        private static Texture2D GetOrCreateAdditiveParticleTexture()
        {
            if (_cachedAdditiveTexture != null) return _cachedAdditiveTexture;

            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var center = new Vector2(size / 2f, size / 2f);
            float maxDist = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float a = Mathf.Clamp01(1f - dist / maxDist);
                    a *= a;
                    tex.SetPixel(x, y, new Color(a, a, a, a));
                }
            }
            tex.Apply();
            _cachedAdditiveTexture = tex;
            return tex;
        }

        private static Material _cachedAdditiveMaterial;
        private static bool _additiveUnavailable;

        /// <summary>
        /// The ONE shared ADDITIVE material, for anything that should read as
        /// light rather than as a painted object: flames, embers, sparks.
        /// </summary>
        /// <remarks>
        /// This exists because the fallback chain lands on Sprites/Default,
        /// which is ALPHA BLENDED. Alpha-blended particles occlude each other
        /// instead of accumulating, so a hundred overlapping flame particles
        /// read as a hundred separate orange discs rather than as a body of
        /// fire. Photographed in 0.20.1 and unmistakable.
        ///
        /// Vanilla's answer is its own shader. Assets/Effects/materials/
        /// ashrain_cinder.mat uses Custom/Particle (Unlit) with _SrcBlend 3
        /// (SrcColor), _DstBlend 1 (One) and _ZWrite 0 — additive-family, and
        /// the same values are used here rather than invented. Unlike the
        /// stripped builtins, this shader is the game's own and is present.
        ///
        /// Falls back to the alpha material if Shader.Find misses, so a miss
        /// costs the old look rather than invisible fire, and says so once.
        /// </remarks>
        private static Material GetOrCreateAdditiveParticleMaterial(string callerName)
        {
            if (_cachedAdditiveMaterial != null) return _cachedAdditiveMaterial;
            if (_additiveUnavailable) return null;
            _cachedAdditiveMaterial = CreateAdditiveParticleMaterial(GetOrCreateAdditiveParticleTexture(), callerName);
            return _cachedAdditiveMaterial;
        }

        /// <summary>
        /// Builds an additive particle material around YOUR texture, with the blend setup this mod
        /// had to work out the hard way. Returns null if the build ships no usable additive shader,
        /// which is the caller's cue to fall back rather than render nothing.
        ///
        /// Use this for anything that should read as FIRE. The alpha chain behind
        /// <see cref="ResolveParticleShader"/> lands on Sprites/Default, which is alpha-blended:
        /// correct for smoke, wrong for flame, because alpha particles occlude one another instead
        /// of accumulating and a mass of them reads as separate orange discs. That is exactly what
        /// 0.20.1 looked like in play.
        /// </summary>
        public static Material CreateAdditiveParticleMaterial(Texture2D texture, string callerName)
        {
            if (_additiveUnavailable) return null;

            string how = "Shader.Find";
            Shader shader = Shader.Find("Custom/Particle (Unlit)");
            if (shader == null)
            {
                shader = BorrowShaderFromVanillaFire();
                how = "borrowed from a vanilla fire material";
            }

            if (shader == null)
            {
                _additiveUnavailable = true;
                FireLogger.Warn($"[SHADER-DIAG] {callerName}: no additive particle shader available " +
                                "(Shader.Find missed and no vanilla fire material could be read); " +
                                "flames fall back to the alpha-blended material and will read as " +
                                "separate dots rather than as fire.");
                return null;
            }

            var mat = new Material(shader) { mainTexture = texture };

            // WHICH CHANNEL IS ALPHA. Custom/Particle (Unlit) exposes
            //   [Enum(Red,0,Green,1,Blue,2,Alpha,3)] _AlphaChannel = 0
            // so out of the box it takes alpha from the texture's RED channel.
            // The old soft texture is white RGB with its falloff in the alpha
            // channel, which means red reads 1.0 across the whole quad and every
            // particle draws as a solid square. That, not the blend, is what made
            // 0.20.2 and 0.20.4 render blocks; two different _SrcBlend values were
            // tried against it and neither could have worked.
            //
            // Belt and braces, because this shader's real body cannot be read
            // (AssetRipper emits a DummyShaderTextExporter stub and only the
            // property list is genuine): point it at the alpha channel AND bake
            // the falloff into RGB as well, so the edges go to black even if the
            // channel selector behaves differently than the enum implies. Under
            // additive blending black adds nothing, so either path gives soft edges.
            if (mat.HasProperty("_AlphaChannel")) mat.SetFloat("_AlphaChannel", 3f); // Alpha
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);                 // Off - billboards face any way
            FireLogger.Info($"[SHADER-DIAG] additive shader acquired via {how}: \"{shader.name}\".");

            // SrcAlpha, NOT the SrcColor (3) that ashrain_cinder.mat uses.
            //
            // Copying vanilla's number was wrong, and wrong in a way that is
            // obvious on screen: SrcColor ignores the alpha channel entirely, and
            // our particle texture is white RGB that fades out THROUGH ALPHA. So
            // every quad contributed at full strength right to its corners and
            // the fire rendered as hard-edged squares. Vanilla can use SrcColor
            // because its own textures bake the falloff into RGB; ours does not.
            // SrcAlpha x One is the classic additive pairing for an alpha-faded
            // texture, and it is the texture we have that decides this, not the
            // material we borrowed the shader from.
            mat.SetFloat("_SrcBlend", 5f); // SrcAlpha
            mat.SetFloat("_DstBlend", 1f); // One
            mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = 3000;        // Transparent

            FireLogger.Info($"[SHADER-DIAG] additive flame material built for {callerName} " +
                            "(_SrcBlend=5 SrcAlpha, _DstBlend=1 One, _ZWrite=0).");
            return mat;
        }

        /// <summary>
        /// The alpha-blended particle shader this build actually ships, or null. Public so callers
        /// outside this file resolve it HERE rather than calling Shader.Find themselves: the first
        /// four obvious candidates are all stripped from Valheim, and a name-based guess returns
        /// null and renders nothing. See the remarks on the chain itself.
        /// </summary>
        public static Shader ResolveParticleShader() => FindUsableParticleShader();

        /// <summary>
        /// Takes a shader off a vanilla fire material that is already loaded,
        /// rather than asking for one by name.
        /// </summary>
        /// <remarks>
        /// Shader.Find only sees shaders currently resident, which depends on
        /// what the scene has pulled in — so it can miss a shader the game
        /// definitely ships, and it is guaranteed to miss on a headless server,
        /// which loads none at all. A prefab registered in ZNetScene carries its
        /// materials with it, and a material always carries a live shader
        /// reference, so reading one is not subject to that timing.
        ///
        /// Names are avoided deliberately. The only evidence tying vanilla's
        /// flame materials to particular shader NAMES is AssetRipper's builtin
        /// fileID table, which is its own mapping rather than the game's, and
        /// trusting it is what inverted the shader survey twice already. Whatever
        /// object comes back here is by definition present and usable; the log
        /// records what it turned out to be.
        /// </remarks>
        private static Shader BorrowShaderFromVanillaFire()
        {
            string[] donors = { "fire_pit", "bonfire", "piece_groundtorch" };

            for (int d = 0; d < donors.Length; d++)
            {
                GameObject prefab = FindPrefabByName(donors[d]);
                if (prefab == null) continue;

                ParticleSystemRenderer[] renderers = prefab.GetComponentsInChildren<ParticleSystemRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    ParticleSystemRenderer r = renderers[i];
                    if (r == null) continue;

                    Material m = r.sharedMaterial;
                    if (m == null || m.shader == null) continue;

                    // Prefer a shader that exposes the blend properties we set,
                    // since that is what lets us force additive rather than
                    // inheriting whatever the donor happened to be authored as.
                    if (m.HasProperty("_SrcBlend") && m.HasProperty("_DstBlend"))
                    {
                        FireLogger.Debug($"[SHADER-DIAG] borrowed \"{m.shader.name}\" from " +
                                         $"{donors[d]}/{r.gameObject.name} (material \"{m.name}\").");
                        return m.shader;
                    }
                }
            }

            return null;
        }

        private static void ApplyParticleShader(ParticleSystemRenderer renderer, string callerName)
        {
            ApplyParticleShader(renderer, callerName, additive: false);
        }

        /// <summary>
        /// Assigns a shared material. Pass additive:true for anything that emits
        /// light (flame, ember, spark) and false for anything that blocks it
        /// (smoke) — additive smoke glows instead of darkening, which is worse
        /// than the problem it would be solving.
        /// </summary>
        private static void ApplyParticleShader(ParticleSystemRenderer renderer, string callerName, bool additive)
        {
            if (additive)
            {
                Material add = GetOrCreateAdditiveParticleMaterial(callerName);
                if (add != null)
                {
                    renderer.sharedMaterial = add;
                    return;
                }
                // fall through to the alpha material
            }

            if (_cachedParticleMaterial == null)
            {
                Shader shader = FindUsableParticleShader();
                if (shader == null)
                {
                    FireLogger.Warn($"{callerName}: no usable particle shader found in this build; " +
                                     "using the default material (may render as a flat/incorrect color).");
                    return;
                }
                _cachedParticleMaterial = new Material(shader) { mainTexture = GetOrCreateSoftParticleTexture() };
            }

            renderer.sharedMaterial = _cachedParticleMaterial;
        }

        /// <summary>
        /// Builds a small fire-like particle effect entirely from code — no
        /// vanilla prefab, no ZNetView, no dependency on anything registered
        /// in ZNetScene. This exists because every registered fire-related
        /// prefab in the game turned out to carry a ZNetView (see SpawnVfx),
        /// making them all unsafe to spawn directly. Won't look identical to
        /// vanilla fire, but is safe by construction and should read as fire:
        /// rising orange/red particles fading to nothing, plus a warm light.
        /// </summary>
        public static GameObject CreateProceduralFireVfx(Vector3 position)
        {
            return CreateProceduralFireVfx(position, 0f);
        }

        /// <summary>
        /// As above, but sized to the thing that is burning.
        /// </summary>
        /// <remarks>
        /// A burner shorter than TallBurnerMinHeight gets exactly the effect it
        /// always got. Above it the flame becomes a COLUMN rather than a bigger
        /// puddle: the emitter changes to a cone VOLUME whose length spans the
        /// trunk, so Unity distributes particles up the whole height natively.
        /// That distinction is the entire point. A wild Valheim fir stands
        /// 15.8-31.7 m — FirTree is 10.55 m at scale 1, and ZoneSystem plants it
        /// only in Black Forest at scale 2-2.5 and Mountain at 1.5-3 — and it
        /// used to get a 1.5 m plume at the foot of the trunk, which read as a
        /// campfire beside an untouched tree rather than a tree on fire. Only a
        /// player-planted sapling is ever short enough for the old effect to
        /// have covered it.
        ///
        /// The sizing is deliberately sub-linear. Particle COUNT and emission
        /// rate grow with height, since a taller column needs more to stay
        /// dense, but particle SIZE barely does: scaling the whole effect
        /// uniformly just produces a giant campfire. Everything is clamped, and
        /// the height itself is clamped by MaxFlameHeight, so one freak bounds
        /// measurement cannot turn a single tree into a particle storm.
        /// </remarks>
        public static GameObject CreateProceduralFireVfx(Vector3 position, float burnerHeight) =>
            CreateProceduralFireVfx(position, burnerHeight, burnerHeight * 0.3f); // no measured crown: assume a fir-ish silhouette

        public static GameObject CreateProceduralFireVfx(Vector3 position, float burnerHeight, float crownRadius)
        {
            var go = new GameObject("FireFrontVfx_Procedural");
            go.transform.position = position;

            float height = ResolveFlameHeight(burnerHeight);
            if (height > 0f && !TryReserveTallVfx(go)) height = 0f;

            BuildFlameParticles(go, height);
            if (height > 0f) BuildCrownFlames(go, height, crownRadius);
            if (FireFront.Config.FireConfig.FireSmokeEnabled.Value)
            {
                BuildSmokeParticles(go, height);
            }
            if (height > 0f && FireFront.Config.FireConfig.EffectiveCrownSparksEnabled)
            {
                BuildCrownSparks(go, height);
            }

            Light light = go.AddComponent<Light>();
            light.color = new Color(1f, 0.5f, 0.2f);
            light.intensity = height > 0f ? Mathf.Min(2.5f + height * 0.10f, 4f) : 2.5f;
            // Range is the dominant cost of a realtime light, so it grows slowly
            // and stops early. See the note in DowngradeVfxToSmoulder.
            light.range = height > 0f ? Mathf.Min(6f + height * 0.45f, 12f) : 6f;

            return go;
        }

        /// <summary>
        /// Live tall-fire effects, so their total can be bounded.
        /// </summary>
        /// <remarks>
        /// Object fire has never had the aggregate cap that ground fire gives
        /// itself through GroundVfxMaxConcurrent, and a tall burner costs roughly
        /// 4x a short one. Without a ceiling, a forest going up would multiply a
        /// cost that was already unbounded.
        ///
        /// Bounded at SPAWN, deliberately, not by a per-frame sweep: picking the
        /// nearest N every frame is exactly the shape of managed work that caused
        /// the spikes this repo keeps re-learning about. Once the budget is full,
        /// later ignitions simply get the ordinary small effect and still burn,
        /// spread and damage normally. Destroyed entries are pruned here, which
        /// is why the list self-heals as fires burn out — Unity's overloaded ==
        /// reports a destroyed GameObject as null.
        /// </remarks>
        private static readonly List<GameObject> _tallVfx = new List<GameObject>();

        private static bool TryReserveTallVfx(GameObject go)
        {
            for (int i = _tallVfx.Count - 1; i >= 0; i--)
            {
                if (_tallVfx[i] == null) _tallVfx.RemoveAt(i);
            }

            if (_tallVfx.Count >= FireFront.Config.FireConfig.EffectiveTallFireMaxConcurrent) return false;

            _tallVfx.Add(go);
            return true;
        }

        /// <summary>Burners below this keep exactly the effect they always had.</summary>
        private const float TallBurnerMinHeight = 3f;

        /// <summary>
        /// Height, in metres, past which a fire stops getting MORE EXPENSIVE —
        /// as distinct from MaxFlameHeight, which bounds how TALL it is drawn.
        /// </summary>
        /// <remarks>
        /// These have to be two numbers. Particle counts, emission rates, sizes
        /// and lifetimes are driven by min(height, this), so a 30 m tree costs
        /// exactly what a 14 m one does; only the geometry — column length and
        /// where smoke and sparks sit — follows the real height. Without the
        /// split, raising MaxFlameHeight to cover a real tree would have raised
        /// the particle bill with it, and LowSpecPreset could not have bounded
        /// smoke at all, since smoke size and lifetime keyed off nothing but the
        /// "is it tall" boolean.
        /// </remarks>
        private const float CostHeightCeiling = 14f;

        /// <summary>
        /// Clamps a measured burner height into the range the flame builder will
        /// honour. Returns 0 for anything short enough to keep the original
        /// small-fire look, which is what every caller reads as "not tall".
        /// </summary>
        private static float ResolveFlameHeight(float burnerHeight)
        {
            if (!FireFront.Config.FireConfig.TreeFlameScaling.Value) return 0f;
            if (burnerHeight < TallBurnerMinHeight) return 0f;
            return Mathf.Min(burnerHeight, FireFront.Config.FireConfig.EffectiveMaxFlameHeight);
        }

        /// <summary>
        /// World-space height of a burning thing, from its own origin to the top
        /// of its renderers. Zero when nothing can be measured.
        /// </summary>
        /// <remarks>
        /// Renderer.bounds is a world AABB that already accounts for LOD meshes
        /// and leaf cards, which is what we want: the flame should cover the
        /// silhouette, not the trunk capsule. ParticleSystemRenderers are skipped
        /// so a fire already attached to the target can never feed its own bounds
        /// back in and grow the next measurement.
        ///
        /// The allocation in GetComponentsInChildren is fine here. This runs once
        /// per ignition, on a path already doing far more work, and never per
        /// frame. The result is clamped on the way out.
        /// </remarks>
        public static float MeasureBurnerHeight(Component target)
        {
            if (target == null) return 0f;

            GameObject go = target.gameObject;
            if (go == null) return 0f;

            float baseY = go.transform.position.y;
            float top = baseY;
            bool found = false;

            // INACTIVE renderers count, deliberately. An LODGroup keeps only the
            // current LOD enabled and disables the rest, so a tree ignited while
            // it is far away or culled would measure 0 and keep the small flame
            // for its entire burn, even once you walked up to it. Bounds are
            // valid whether or not the renderer is drawing, and every LOD of the
            // same tree reports the same top, so reading them all is both safe
            // and the only way to get a stable answer regardless of view.
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer) continue;
                float t = r.bounds.max.y;
                if (t > top) { top = t; found = true; }
            }

            if (!found) return 0f;
            return Mathf.Clamp(top - baseY, 0f, 40f);
        }

        /// <summary>
        /// How far the burner's canopy reaches sideways from its trunk, in metres:
        /// the largest horizontal distance from the object's position to any
        /// renderer's bounds, LOD and billboard renderers included, same walk as
        /// the height. A wild beech measures 4-6m here, a fir 2-3m. 0 if nothing
        /// renders; callers treat that as 'no crown', and the flame builder falls
        /// back to a slim shell scaled off the height.
        /// </summary>
        public static float MeasureBurnerCrownRadius(Component target)
        {
            if (target == null) return 0f;
            GameObject go = target.gameObject;
            if (go == null) return 0f;
            Vector3 p = go.transform.position;
            float reach = 0f;
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r is ParticleSystemRenderer) continue;
                Bounds b = r.bounds;
                reach = Mathf.Max(reach, Mathf.Abs(b.max.x - p.x), Mathf.Abs(b.min.x - p.x),
                                         Mathf.Abs(b.max.z - p.z), Mathf.Abs(b.min.z - p.z));
            }
            return Mathf.Clamp(reach, 0f, 12f);
        }

        /// <summary>
        /// Points a cone emitter along world +Y.
        /// </summary>
        /// <remarks>
        /// Unity emits a Cone along its LOCAL +Z and ShapeModule.rotation
        /// defaults to zero, so a system built in code with AddComponent fires
        /// SIDEWAYS. The Editor hides this: its own "Particle System" menu item
        /// creates the GameObject pre-rotated -90 on X, which is why the default
        /// looks upward there and nowhere else. Vanilla Valheim follows the same
        /// convention. In fire_pit.prefab every directional emitter (flames,
        /// low_flames, flames (1), smoke (1), smok_small) carries exactly -90 X,
        /// while the two non-directional ones (flare, sparcs (1)) sit at 0.
        ///
        /// Assigning the rotation ABSOLUTELY, rather than rotating the transform,
        /// keeps this idempotent: calling it twice, or on a system whose default
        /// ever changes, still ends up pointing up instead of flipping over.
        ///
        /// One correction to the survey above: "sparcs (1)" is NOT an exception.
        /// It is a Cone that carries its -90 on the ShapeModule rather than on
        /// the Transform — the very mechanism used here — which leaves "flare",
        /// a billboard glow with no direction to point, as the only emitter in
        /// that prefab legitimately sitting at zero.
        /// </remarks>
        private static void AimShapeUp(ParticleSystem.ShapeModule shape)
        {
            shape.rotation = new Vector3(-90f, 0f, 0f);
        }

        private static void BuildFlameParticles(GameObject go, float height)
        {
            bool tall = height > 0f;
            float cost = Mathf.Min(height, CostHeightCeiling);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = tall
                ? new ParticleSystem.MinMaxCurve(0.9f, 1.5f)
                : new ParticleSystem.MinMaxCurve(0.7f, 1.1f);
            main.startSpeed = tall
                ? new ParticleSystem.MinMaxCurve(1.6f, 2.6f)
                : new ParticleSystem.MinMaxCurve(1.2f, 1.9f);
            main.startSize = tall
                ? new ParticleSystem.MinMaxCurve(0.4f, 0.8f)
                : new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.startColor = new Color(1f, 0.55f, 0.15f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = tall ? Mathf.Clamp(Mathf.RoundToInt(120f + cost * 20f), 120, 400) : 120;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = tall ? Mathf.Clamp(40f + cost * 6f, 40f, 140f) : 40f;

            ParticleSystem.ShapeModule shape = ps.shape;
            if (tall)
            {
                // ConeVolume, not Cone: the volume form spreads emission ALONG
                // the axis instead of only across the base disc. That is what
                // puts fire up the trunk rather than in a ring around its foot,
                // and it costs nothing, because Unity samples the volume itself
                // instead of us placing particles from managed code every frame.
                shape.shapeType = ParticleSystemShapeType.ConeVolume;
                shape.angle = 9f;
                shape.radius = Mathf.Clamp(height * 0.07f, 0.3f, 1.1f);
                shape.length = height * 0.85f;
            }
            else
            {
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 12f;
                shape.radius = 0.25f;
            }
            AimShapeUp(shape);

            // Flame licks: particles grow slightly through their first half-life,
            // then shrink as they burn out — reads much more like a living flame
            // than a constant-size particle fading in place.
            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.6f), new Keyframe(0.35f, 1.15f), new Keyframe(1f, 0.2f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            // Subtle turbulence so flames don't look like they're on rails.
            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.3f;
            noise.frequency = 0.6f;
            noise.scrollSpeed = 0.5f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.3f), 0f),
                    new GradientColorKey(new Color(1f, 0.3f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.2f, 0.1f, 0.1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.6f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = grad;

            ApplyParticleShader(go.GetComponent<ParticleSystemRenderer>(), nameof(BuildFlameParticles), additive: true);
        }

        /// <summary>
        /// Smoke rises above the flame, drifts, expands, and fades — a separate
        /// child particle system rather than folding into the flame emitter,
        /// since smoke needs a longer lifetime, slower rise, larger/growing
        /// size, and a completely different color/alpha curve. Offset slightly
        /// above the flame's origin so it visually emerges from the flame tips.
        /// </summary>

        /// <summary>
        /// Turns a full fire effect into a smouldering one, in place.
        /// </summary>
        /// <remarks>
        /// A tester's suggestion, and the measurement behind it was their own:
        /// their residual frametime spike was WORSE looking toward the fire and
        /// better looking away, which is a rendering cost, not a simulation one.
        /// A burn lasts BurnDurationSeconds (240 by default) and rendered a full
        /// flame for every second of it.
        ///
        /// The single most expensive part per burner is the real-time Light —
        /// fifty burning objects meant fifty dynamic lights — so that goes
        /// first. Flames drop to a few embers; smoke is KEPT (reduced), because
        /// smoke is what actually reads as "this ground is still smouldering"
        /// once the flames are gone.
        ///
        /// Mutates the existing components rather than destroying and respawning
        /// the effect: no allocation, no VFX churn, and the object keeps its
        /// place in _vfx so removal still works normally.
        /// </remarks>
        public static void DowngradeVfxToSmoulder(GameObject vfx)
        {
            if (vfx == null) return;

            // 0.22.0: the object-fire rig is a FireVFXController, which downgrades itself
            // (front, band, embers, haze, glow, light). The generic pass below is kept for
            // the ground-cell and prefab rigs, which have no controller.
            FireFront.Fire.FireVFXController controller = vfx.GetComponent<FireFront.Fire.FireVFXController>();
            if (controller != null)
            {
                controller.SetSmoulder();
                return;
            }

            // The light is SHRUNK, not destroyed. Deleting it outright (first
            // attempt) took the glow with it and the fire read as extinguished —
            // which matters because it is still contagious and still burning
            // anything standing in it. Range is the dominant cost of a realtime
            // light (it decides how many objects the light has to touch), so
            // more than halving it keeps most of the saving while the fire
            // still visibly has heat in it.
            Light light = vfx.GetComponent<Light>();
            if (light != null)
            {
                light.intensity *= 0.45f;
                light.range *= 0.5f;
                light.color = new Color(1f, 0.35f, 0.10f); // deeper ember red
            }

            ParticleSystem[] systems = vfx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null) continue;

                // Sparks are SILENCED rather than reduced. A smouldering tree
                // throwing sparks reads as still-raging, and the generic flame
                // branch below would make it worse: it writes startSize 0.18-0.34
                // absolutely, which is 2-5x BIGGER than a spark starts, so the
                // one thing meant to shrink would visibly grow.
                if (ps.gameObject.name == "Sparks")
                {
                    ParticleSystem.EmissionModule sparkEmission = ps.emission;
                    sparkEmission.enabled = false;
                    continue;
                }

                bool isSmoke = ps.gameObject.name == "Smoke";
                // Smoke is the SIGNATURE of smouldering, so it is barely reduced.
                // Flames drop hard but not to nothing — 0.12 was invisible.
                float keep = isSmoke ? 0.8f : 0.35f;

                ParticleSystem.MainModule main = ps.main;
                main.maxParticles = Mathf.Max(8, Mathf.RoundToInt(main.maxParticles * keep));

                ParticleSystem.EmissionModule emission = ps.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(
                    Mathf.Max(2f, emission.rateOverTime.constant * keep));

                if (!isSmoke)
                {
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.45f, 0.10f));
                    main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 0.9f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.34f);

                    // INTERMITTENT FLAMES — the tester's actual words, and the
                    // thing a constant weak trickle failed to convey. A steady
                    // low emission reads as "dying"; irregular flare-ups read as
                    // "still burning, just not raging". Costs nothing between
                    // bursts, which is the whole point.
                    emission.SetBursts(new[]
                    {
                        new ParticleSystem.Burst(0f, new ParticleSystem.MinMaxCurve(6f, 14f), 1000, 2.5f),
                    });

                    // Turbulence stays ON but weaker — with it off entirely the
                    // embers sat in dead straight lines and looked artificial.
                    ParticleSystem.NoiseModule noise = ps.noise;
                    noise.enabled = true;
                    noise.strength = 0.15f;
                }
            }
        }
        private static void BuildSmokeParticles(GameObject parent, float height)
        {
            bool tall = height > 0f;
            float cost = Mathf.Min(height, CostHeightCeiling);
            var smokeGo = new GameObject("Smoke");
            smokeGo.transform.SetParent(parent.transform, false);
            // On a tall burner the smoke belongs at the CANOPY, not at the foot.
            // Smoke born at ground level inside a burning tree spends its life
            // behind the trunk and the flame column in front of it.
            smokeGo.transform.localPosition = Vector3.up * (tall ? height * 0.72f : 0.4f);

            ParticleSystem ps = smokeGo.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            // Scaled from `cost`, not stepped off `tall`. A boolean step meant a
            // 3 m bush and a 30 m fir got identical smoke, and left the biggest
            // fill-rate term in the effect answering to no cap at all.
            main.startLifetime = tall
                ? new ParticleSystem.MinMaxCurve(2.5f + cost * 0.11f, 4f + cost * 0.18f)
                : new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = tall
                ? new ParticleSystem.MinMaxCurve(0.6f + cost * 0.02f, 1.1f + cost * 0.04f)
                : new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
            main.startSize = tall
                ? new ParticleSystem.MinMaxCurve(0.5f + cost * 0.03f, 0.9f + cost * 0.05f)
                : new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.startColor = new Color(0.2f, 0.2f, 0.2f, 0.45f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = tall ? Mathf.Clamp(Mathf.RoundToInt(60f + cost * 6f), 60, 150) : 60;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = tall ? Mathf.Clamp(8f + cost * 0.9f, 8f, 24f) : 8f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f; // wider than the flame — smoke drifts and spreads, doesn't stay a tight column
            shape.radius = tall ? Mathf.Clamp(cost * 0.10f, 0.2f, 1.2f) : 0.2f;
            AimShapeUp(shape);

            // Smoke expands as it rises and disperses, unlike the flame which
            // shrinks — this is the key visual distinction between the two.
            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var sizeCurve = new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 2.2f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.5f;
            noise.frequency = 0.3f;
            noise.scrollSpeed = 0.2f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.15f, 0.15f, 0.15f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.3f, 0.3f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.4f, 0.2f),
                    new GradientAlphaKey(0.25f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = grad;

            ApplyParticleShader(smokeGo.GetComponent<ParticleSystemRenderer>(), nameof(BuildSmokeParticles));
        }

        /// <summary>
        /// Sparks thrown off the upper half of a tall burner: bright, stretched,
        /// and falling.
        /// </summary>
        /// <remarks>
        /// This is the cheapest of the three tall-burner effects and carries the
        /// most of the read. Flames say "there is fire here"; sparks say the fire
        /// is IN THE CROWN, well above head height, which is the thing a plume at
        /// the foot of a trunk can never convey.
        ///
        /// Stretch render mode is what makes them read as sparks rather than
        /// orange dots. Positive gravity is deliberate: they arc and fall, unlike
        /// everything else in this file, which rises.
        ///
        /// Emission is flat-rate and tiny (a handful a second, hard-capped) and
        /// there is no per-frame managed work, so the cost is bounded per burner
        /// rather than growing with how much of the forest is alight.
        /// </remarks>
        private static void BuildCrownSparks(GameObject parent, float height)
        {
            var sparkGo = new GameObject("Sparks");
            sparkGo.transform.SetParent(parent.transform, false);
            sparkGo.transform.localPosition = Vector3.up * (height * 0.55f);

            ParticleSystem ps = sparkGo.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            main.startColor = new Color(1f, 0.85f, 0.4f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.3f;
            float cost = Mathf.Min(height, CostHeightCeiling);
            main.maxParticles = Mathf.Clamp(Mathf.RoundToInt(20f + cost * 2f), 20, 60);

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = Mathf.Clamp(3f + cost * 0.5f, 3f, 12f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f; // much wider than the flame — sparks scatter outward
            shape.radius = Mathf.Clamp(cost * 0.09f, 0.2f, 1f);
            AimShapeUp(shape);

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var sizeCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.2f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.55f), 0f),
                    new GradientColorKey(new Color(1f, 0.3f, 0.05f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.9f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = grad;

            var renderer = sparkGo.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 3f;
            renderer.velocityScale = 0.05f;
            ApplyParticleShader(renderer, nameof(BuildCrownSparks), additive: true);
        }

        // Flames a player can SEE on a leafy tree. The trunk column is a cone
        // under a metre wide running up the middle of the tree, and a beech's
        // crown is solid foliage from about three metres up, so the leaves draw
        // over every flame inside it - the first burning beech on 0.21.1 showed
        // 'sparks only, no flame' (2026-09-16), because only the sparks, on a 35
        // degree cone, escape the canopy. This is a hemispherical SHELL of flame
        // on the outside of the measured crown: centred a little above mid-height,
        // as wide as the canopy reaches, emitting from the outer third of the
        // radius so the flames sit on the leaves rather than in them, big enough
        // to read at distance, licking upward. A fir keeps its column and gains a
        // narrower shell around its middle. Cost scales with crown area, halved
        // under the low-spec preset, and the tall-fire cap already bounds how many
        // burners get here at all.
        private static void BuildCrownFlames(GameObject parent, float height, float crownRadius)
        {
            float radius = crownRadius > 0.5f ? crownRadius : Mathf.Max(1f, height * 0.25f);
            radius = Mathf.Clamp(radius, 1f, 10f);
            bool lowSpec = FireFront.Config.FireConfig.LowSpecPreset != null && FireFront.Config.FireConfig.LowSpecPreset.Value;
            float budget = lowSpec ? 0.5f : 1f;

            var crownGo = new GameObject("CrownFlames");
            crownGo.transform.SetParent(parent.transform, false);
            crownGo.transform.localPosition = Vector3.up * (height * 0.55f);

            ParticleSystem ps = crownGo.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            float size = Mathf.Clamp(radius * 0.22f, 0.5f, 1.4f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.9f); // radial, off the surface
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size * 1.3f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.startColor = new Color(1f, 0.55f, 0.15f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            float rate = Mathf.Clamp(20f + radius * radius * 3f, 20f, 110f) * budget;
            main.maxParticles = Mathf.Clamp(Mathf.RoundToInt(rate * 1.8f), 40, 220);

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = rate;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = radius * 0.95f;
            shape.radiusThickness = 0.3f; // outer 30% only: on the canopy, not inside it
            AimShapeUp(shape);            // dome up; the flat face is the underside of the crown

            ParticleSystem.VelocityOverLifetimeModule velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.y = new ParticleSystem.MinMaxCurve(1.2f, 2.4f); // the lick upward that makes it fire, not a glow
            // x, y and z must share one curve mode or Unity logs "Particle Velocity curves must
            // all be in the same mode" every frame the crown burns (see FireVFXController.BuildFlames).
            velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.6f), new Keyframe(0.35f, 1.15f), new Keyframe(1f, 0.2f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.45f;
            noise.frequency = 0.5f;
            noise.scrollSpeed = 0.6f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.3f), 0f),
                    new GradientColorKey(new Color(1f, 0.3f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.2f, 0.1f, 0.1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.6f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = grad;

            ApplyParticleShader(crownGo.GetComponent<ParticleSystemRenderer>(), nameof(BuildCrownFlames), additive: true);
        }

        /// <summary>
        /// Deliberately CHEAPER than CreateProceduralFireVfx — no Light (the
        /// most expensive part per-instance), fewer/smaller/shorter-lived
        /// particles. Ground cells can have up to GroundMaxConcurrent (default
        /// 200) burning at once; spawning 200 full fire effects with dynamic
        /// lights would be a real performance problem. FireManager also caps
        /// how many of these actually get created at once (GroundVfxMaxConcurrent)
        /// independent of how many cells are logically burning — the simulation
        /// keeps running everywhere, only a bounded number are ever rendered.
        /// </summary>
        public static GameObject CreateProceduralGroundFireVfx(Vector3 position)
        {
            var go = new GameObject("FireFrontVfx_Ground");
            go.transform.position = position;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = 1.0f;
            main.startSpeed = 1.0f;
            main.startSize = 0.55f;
            main.startColor = new Color(1f, 0.5f, 0.1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 20f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = 0.35f;
            AimShapeUp(shape);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.8f, 0.2f), 0f),
                    new GradientColorKey(new Color(0.9f, 0.2f, 0.05f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.8f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = grad;

            ApplyParticleShader(go.GetComponent<ParticleSystemRenderer>(), nameof(CreateProceduralGroundFireVfx), additive: true);

            return go;
        }

        /// <summary>
        /// Spawn a visual-only VFX instance at a target's position.
        ///
        /// CORRECTION from 0.3.1: this used to strip any MonoBehaviour/ZNetView
        /// off the spawned copy as a "safety net." That was wrong — destroying a
        /// ZNetView directly with Object.Destroy() instead of calling its own
        /// Destroy() method is itself the same category of bug that corrupted
        /// ZNetScene earlier (see KillBurningTarget's history). Stripping only
        /// ever protected against non-networked scripts; every registered
        /// fire-related prefab in this game turned out to carry a ZNetView
        /// (they're normally spawned as a result of networked actions), so the
        /// old approach was never actually safe for the exact prefabs we wanted.
        /// Now: if the prefab has ANY ZNetView anywhere in its hierarchy, refuse
        /// to spawn it at all. Only genuinely network-free prefabs are usable.
        /// </summary>
        public static GameObject SpawnVfx(GameObject prefab, Vector3 position)
        {
            if (prefab == null) return null;

            if (prefab.GetComponentInChildren<ZNetView>(true) != null)
            {
                FireLogger.Warn($"Refusing to spawn '{prefab.name}' as vfx — it has a ZNetView. " +
                                 "Stripping it after spawn is not safe (see SpawnVfx comment).");
                return null;
            }

            GameObject instance = Object.Instantiate(prefab, position, Quaternion.identity);

            // Any remaining non-network scripts are still stripped, in case a
            // ZNetView-free prefab has some other unwanted gameplay script.
            foreach (MonoBehaviour script in instance.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Object.Destroy(script);
            }

            return instance;
        }

        /// <summary>
        /// Emergency cleanup: finds and destroys every live instance of vanilla's
        /// own "Fire" gameplay class in the currently loaded scene. Only needed
        /// once, to clear out orphans spawned before the VFX-stripping fix landed.
        /// </summary>
        public static int PurgeAllVanillaFireInstances()
        {
            if (VanillaFireType == null) return 0;
            Object[] instances = Object.FindObjectsOfType(VanillaFireType);
            int count = 0;
            foreach (Object obj in instances)
            {
                if (obj is Component c && c != null)
                {
                    Object.Destroy(c.gameObject);
                    count++;
                }
            }
            return count;
        }

        // -----------------------------------------------------------------
        // Server-authority networking. FireManager's simulation only runs on
        // the server (ZNet.instance.IsServer() — true for a dedicated server
        // AND for single-player/client-hosted play, so existing solo testing
        // is unaffected). RPC_Damage fires wherever Valheim currently has the
        // target's ZDO owned, which is very often a nearby CLIENT, not the
        // server — confirmed by a real dedicated-server test where ignition
        // only ever happened on the connected client, and the server never
        // learned about the fire at all. These two RPCs close that gap:
        // clients forward ignition requests to the server instead of
        // simulating locally, and the server broadcasts start/stop back out
        // so every peer can spawn its own local (non-authoritative) VFX.
        //
        // Object fire (pieces/trees/logs) only for now — ground fire has no
        // ZDOID to key on and needs its own sync channel (cell coordinates
        // instead of ZDOID); that's a follow-up, not covered here.
        // -----------------------------------------------------------------

        // "2" suffix (0.17.3): the ignite request now carries the igniter's player id
        // for cross-mod arson attribution. A renamed RPC makes a version-mismatched
        // client/server pair no-op cleanly (requests silently dropped, visible in
        // IGNITE-TRACE) instead of half-deserializing the old single-argument shape.
        private const string RpcIgniteRequest = "FireFront_IgniteRequest2";

        // GetServerPeerID() is IL-flagged public in the publicized reference DLL
        // used to compile against, but throws MethodAccessException at actual
        // runtime against the real (non-publicized) game assembly — confirmed by
        // a live dedicated-server test. Same class of gotcha every other
        // non-public Valheim member in this file already routes around via
        // reflection; this was the one place calling it directly instead.
        private static readonly MethodInfo ZRoutedRpcGetServerPeerIdMethod =
            typeof(ZRoutedRpc).GetMethod("GetServerPeerID", AnyInstance, null, System.Type.EmptyTypes, null);

        /// <summary>Reflected wrapper around ZRoutedRpc.instance.GetServerPeerID(). Returns 0L on failure.</summary>
        /// <summary>
        /// True once this client can actually deliver a routed RPC to the server. ZNet.instance
        /// exists from the moment the world scene loads, seconds before the socket finishes its
        /// handshake, so "ZNet is up" is NOT the same question and answering it instead loses
        /// whatever is sent in between.
        /// </summary>
        // Reflected for the usual reason - a publicized DLL's access flags have lied about methods
        // in this file before, and this one is called from the VFX path where a throw would be
        // noisy. Failing to resolve it returns false, which means "build visuals as before": the
        // fallback is the old behaviour, never a silent loss of fire on someone's screen.
        private static readonly MethodInfo ZNetIsDedicatedMethod =
            typeof(ZNet).GetMethod("IsDedicated", AnyInstance, null, System.Type.EmptyTypes, null);

        /// <summary>
        /// True only on a real dedicated server: a machine with no camera, no player and nobody to
        /// show a particle to. A listen host is NOT dedicated and must keep its visuals.
        /// </summary>
        public static bool IsDedicatedServer()
        {
            ZNet net = ZNet.instance;
            if (net == null || ZNetIsDedicatedMethod == null) return false;
            try { return (bool)ZNetIsDedicatedMethod.Invoke(net, null); }
            catch { return false; }
        }

        public static bool CanReachServer() => !IsServer() && ZRoutedRpc.instance != null && GetServerPeerId() != 0L;

        private static long GetServerPeerId()
        {
            if (ZRoutedRpc.instance == null || ZRoutedRpcGetServerPeerIdMethod == null) return 0L;
            try
            {
                return (long)ZRoutedRpcGetServerPeerIdMethod.Invoke(ZRoutedRpc.instance, null);
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[IGNITE-TRACE] GetServerPeerID reflection invoke threw: {ex.InnerException?.Message ?? ex.Message}");
                return 0L;
            }
        }

        // Same publicized-DLL-vs-real-assembly gotcha could apply here too — we
        // proved it once already with GetServerPeerID, so don't trust this
        // static field's IL-public flag either without a fallback. ZRoutedRpc's
        // "everybody" broadcast target is a well-established 0L in Valheim's own
        // convention (peer ID 0 = server/broadcast), used as the fallback if
        // reflection access fails for any reason.
        private static readonly FieldInfo ZRoutedRpcEverybodyField =
            typeof(ZRoutedRpc).GetField("Everybody", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static long GetEverybodyTarget()
        {
            if (ZRoutedRpcEverybodyField != null)
            {
                try
                {
                    return (long)ZRoutedRpcEverybodyField.GetValue(null);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] ZRoutedRpc.Everybody reflection access threw: {ex.InnerException?.Message ?? ex.Message}, falling back to 0L.");
                }
            }
            return 0L;
        }
        private const string RpcFireEvent = "FireFront_FireEvent";
        private const string RpcPaintAssign = "FireFront_PaintAssign";
        private const string RpcGroundFireSync = "FireFront_GroundFireSync";
        private const string RpcExtinguishRequest = "FireFront_ExtinguishRequest";
        private const string RpcConfigSet = "FireFront_ConfigSet";
        private const string RpcStatusRequest = "FireFront_StatusRequest";
        private const string RpcStatusResponse = "FireFront_StatusResponse";
        private const string RpcCommandRelay = "FireFront_CommandRelay";
        private const string RpcFireDamage = "FireFront_FireDamage";
        private const string RpcGroundSyncRequest = "FireFront_GroundSyncRequest";
        private const string RpcObjectFireSync = "FireFront_ObjectFireSync";
        private const string RpcVersion = "FireFront_Version"; // 0.24: Fire/VersionCheck.cs

        /// <summary>Client → server: run this whitelisted dev command there; replies stream back on the status-response channel.</summary>
        public static void SendCommandRelayToServer(string commandLine)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RpcCommandRelay, commandLine); }
            catch (System.Exception ex) { FireLogger.Info($"[IGNITE-TRACE] SendCommandRelayToServer THREW: {ex}"); }
        }

        // Server-side authorization for relayed commands. This mirrors exactly
        // how vanilla gates its own kick/ban RPCs: ListContainsId(m_adminList,
        // socket.GetHostName()) — including the id-prefix handling crossplay
        // needs. FAIL CLOSED: any reflection or lookup failure refuses. The
        // typist's local admin state is NEVER trusted for relayed execution —
        // that check lives on their machine and a modified client could claim
        // anything.
        private static readonly FieldInfo ZNetAdminListField = typeof(ZNet).GetField("m_adminList", AnyInstance);
        private static readonly MethodInfo ZNetListContainsIdMethod = typeof(ZNet).GetMethod("ListContainsId", AnyInstance);

        public static bool PeerIsAdmin(long peerId)
        {
            try
            {
                ZNet net = ZNet.instance;
                if (net == null) return false;
                ZNetPeer peer = net.GetPeer(peerId);
                string host = peer?.m_rpc?.GetSocket()?.GetHostName();
                if (string.IsNullOrEmpty(host)) return false;
                object adminList = ZNetAdminListField?.GetValue(net);
                if (adminList == null || ZNetListContainsIdMethod == null) return false;
                return (bool)ZNetListContainsIdMethod.Invoke(net, new[] { adminList, (object)host });
            }
            catch (System.Exception ex)
            {
                FireLogger.Warn($"[RELAY] admin check failed for peer {peerId}: {ex.Message} — refusing.");
                return false;
            }
        }

        /// <summary>The server-tracked reference position of a connected peer, or null.</summary>
        public static Vector3? PeerRefPosition(long peerId)
        {
            try
            {
                ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
                return peer != null ? peer.m_refPos : (Vector3?)null;
            }
            catch { return null; }
        }

        /// <summary>Client → server: "send me your real firestatus line."</summary>
        public static void SendStatusRequestToServer()
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RpcStatusRequest); }
            catch (System.Exception ex) { FireLogger.Info($"[IGNITE-TRACE] SendStatusRequestToServer THREW: {ex}"); }
        }

        /// <summary>Server → one requesting peer: the authoritative status line.</summary>
        /// <summary>
        /// A connected player as the SERVER can see one: a position and a peer to talk to, and
        /// nothing else. There is no Character instance and no collider on a dedicated server
        /// where a player is standing - that is the whole reason this exists.
        /// </summary>
        public struct PlayerTarget
        {
            public long PeerId;
            public Vector3 Position;
            public string Name;
        }

        // Reflected, and ONLY this one needs to be. ZNet.m_peers is `private readonly` in the
        // shipping assembly; the publicized libs\ copy declares it public, so 0.21.8 first read it
        // directly and Mono threw FieldAccessException once per cycle on the real server, taking
        // the rest of the fire tick down with it. I had "checked the decompile" - of the publicized
        // file, not the shipping one. Same trap as ObjectDB.m_itemByHash in 0.17.5. ZNetPeer's own
        // members below ARE public in the shipping assembly (ilspycmd on the server's DLL, 1.0.12).
        private static readonly FieldInfo ZNetPeersField = typeof(ZNet).GetField("m_peers", AnyInstance);
        private static bool _playerTargetsFailureLogged;

        /// <summary>
        /// Every connected REMOTE player's character position, read straight off its ZDO, plus the
        /// peer id to route an RPC to. Excludes this machine's own player, which is a local object
        /// and is handled directly by the caller.
        ///
        /// Why not physics: a dedicated server has no terrain, no instances and no colliders where
        /// players actually are - only ZDOs - so Physics.OverlapSphere finds world scenery near a
        /// fire and never finds the player standing in it. FireFront's fire damage polled exactly
        /// that from 0.1 to 0.21.7, which is why fire never once hurt anyone on a dedicated server.
        /// </summary>
        /// <summary>
        /// Is this peer id one the server is actually connected to? A routed RPC's sender is read off
        /// the wire and relayed unchanged, so it is a CLAIM, not an identity - anything that keys
        /// state or work off it has to resolve it against the real peer list first.
        /// </summary>
        // Character.OnNearFire is public and virtual - Player overrides it and the whole body is
        // `m_nearFireTimer = 0f`. Reflected anyway, on the same principle as everything else here,
        // and the cost is irrelevant because this is called about five times a second, not per frame.
        private static readonly MethodInfo CharacterOnNearFireMethod =
            typeof(Character).GetMethod("OnNearFire", AnyInstance, null, new[] { typeof(Vector3) }, null);
        private static bool _nearFireFailureLogged;

        /// <summary>
        /// Tells the local player they are beside a fire, using the SAME hook a campfire uses.
        ///
        /// Vanilla decides you are warm in Player.UpdateEnvStatusEffects from `m_nearFireTimer &lt;
        /// 0.25f`, and both the Cold and the Freezing branches are gated on it. EffectArea does this
        /// for real heat sources every frame they contain you. Going through the same door means
        /// shelter, frost resistance and the wet/cold interaction all keep behaving exactly as they
        /// do around a campfire, instead of us adding and removing status effects behind vanilla's
        /// back and fighting it for control of them.
        /// </summary>
        public static void MarkLocalPlayerNearFire(Vector3 firePoint)
        {
            Player local = Player.m_localPlayer;
            if (local == null || CharacterOnNearFireMethod == null)
            {
                if (local != null && !_nearFireFailureLogged)
                {
                    _nearFireFailureLogged = true;
                    FireLogger.Warn("Could not find Character.OnNearFire, so standing in a wildfire will not keep " +
                                    "you warm. Everything else about fire is unaffected.");
                }
                return;
            }
            try { CharacterOnNearFireMethod.Invoke(local, new object[] { firePoint }); }
            catch (System.Exception ex)
            {
                if (!_nearFireFailureLogged)
                {
                    _nearFireFailureLogged = true;
                    FireLogger.Warn($"Marking the player as near fire failed ({ex.Message}); wildfires will not keep " +
                                    "you warm. Everything else about fire is unaffected.");
                }
            }
        }

        public static bool IsConnectedPeer(long peerId)
        {
            if (peerId == 0L) return false; // ZRoutedRpc.Everybody
            ZNet net = ZNet.instance;
            if (net == null || ZNetPeersField == null) return false;
            try
            {
                var peers = ZNetPeersField.GetValue(net) as List<ZNetPeer>;
                if (peers == null) return false;
                for (int i = 0; i < peers.Count; i++)
                    if (peers[i] != null && peers[i].m_uid == peerId) return true;
            }
            catch { }
            return false;
        }

        public static bool CollectPlayerTargets(List<PlayerTarget> into)
        {
            into.Clear();
            ZNet net = ZNet.instance;
            ZDOMan man = ZDOMan.instance;
            if (net == null || man == null) return false;

            if (ZNetPeersField == null)
            {
                if (!_playerTargetsFailureLogged)
                {
                    _playerTargetsFailureLogged = true;
                    FireLogger.Warn("Could not reach ZNet's peer list, so fire cannot find players to burn. " +
                                    "A Valheim update has probably renamed it; fire still spreads and still burns objects.");
                }
                return false;
            }

            try
            {
                var peers = ZNetPeersField.GetValue(net) as List<ZNetPeer>;
                if (peers == null) return false;

                for (int i = 0; i < peers.Count; i++)
                {
                    ZNetPeer peer = peers[i];
                    if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;

                    ZDO zdo = man.GetZDO(peer.m_characterID);
                    if (zdo == null) continue; // just connected, or between zones

                    into.Add(new PlayerTarget
                    {
                        PeerId = peer.m_uid,
                        Position = zdo.GetPosition(),
                        Name = string.IsNullOrEmpty(peer.m_playerName) ? "player" : peer.m_playerName,
                    });
                }
                return true;
            }
            catch (System.Exception ex)
            {
                if (!_playerTargetsFailureLogged)
                {
                    _playerTargetsFailureLogged = true;
                    FireLogger.Warn($"Reading the peer list to find players in fire failed: {ex.Message}. " +
                                    "Fire still spreads and still burns objects.");
                }
                return false;
            }
        }

        /// <summary>Server side: tell ONE peer to set its own player alight for a tick.</summary>
        public static void SendFireDamageToPeer(long targetPeer, float damage)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcFireDamage, damage); }
            catch (System.Exception ex) { FireLogger.Debug($"SendFireDamageToPeer threw: {ex.Message}"); }
        }

        /// <summary>
        /// True if this routed RPC claims to come from the server. On its own that is a filter:
        /// the sender id is written by the sender and relayed verbatim. Since 0.23 the server
        /// drops FireFront and RPC_Damage packages whose claimed sender is not their connection
        /// (<see cref="ValidateRoutedSender"/>), so the claim is trustworthy wherever that guard
        /// is armed, and each handler keeps its own clamps for the case it is not.
        /// </summary>
        public static bool IsFromServer(long sender)
        {
            long server = GetServerPeerId();
            return server != 0L ? sender == server : IsServer();
        }

        // ---------------------------------------------------------------------------------
        // 0.23: the routed-RPC sender guard, server side.
        //
        // ZRoutedRpc.RPC_RoutedRPC reads m_senderPeerID straight off the wire and never compares
        // it with the connection the package arrived on; when it relays (target != itself) it
        // re-serialises that field verbatim. So until 0.23 a modded client could write the
        // server's own id as the sender, address the package to Everybody, and every other
        // client's IsFromServer check would pass it. This guard runs as a prefix on
        // RPC_RoutedRPC, on the server only, and drops any package for one of the methods below
        // whose claimed sender is not the uid of the ZNetPeer that owns the ZRpc it came in on.
        // A client cannot register with the server's own uid (ZNet.RPC_PeerInfo refuses it as
        // already connected), so "matches the connection" also means "is not the server".
        //
        // Scoped, deliberately: the twelve FireFront methods and vanilla's RPC_Damage, whose
        // sender the tree, log and wear-and-tear prefixes act on. FireFront polices the traffic
        // it consumes and nothing else. Vanilla and other mods stamp their own uid
        // (InvokeRoutedRPC writes m_id) and would pass anyway; a mod that does something
        // stranger is not this mod's to break.
        //
        // Fail-open, and says so: if ZNet.m_peers cannot be reflected the guard reports itself
        // unarmed at RPC registration and every handler's own clamps stand alone, as in 0.22.
        // ---------------------------------------------------------------------------------
        private static readonly HashSet<int> GuardedRoutedMethods = new HashSet<int>
        {
            RpcIgniteRequest.GetStableHashCode(),
            RpcFireEvent.GetStableHashCode(),
            RpcPaintAssign.GetStableHashCode(),
            RpcGroundFireSync.GetStableHashCode(),
            RpcExtinguishRequest.GetStableHashCode(),
            RpcConfigSet.GetStableHashCode(),
            RpcStatusRequest.GetStableHashCode(),
            RpcStatusResponse.GetStableHashCode(),
            RpcCommandRelay.GetStableHashCode(),
            RpcFireDamage.GetStableHashCode(),
            RpcGroundSyncRequest.GetStableHashCode(),
            RpcObjectFireSync.GetStableHashCode(),
            RpcVersion.GetStableHashCode(),
            "RPC_Damage".GetStableHashCode(),
        };

        /// <summary>The guard can only validate when the peer list is reachable.</summary>
        public static bool RoutedSenderGuardArmed => ZNetPeersField != null;

        /// <summary>
        /// The prefix body for ZRoutedRpc.RPC_RoutedRPC. True lets the package through to vanilla;
        /// false drops it. The package cursor is always put back where it was.
        /// </summary>
        public static bool ValidateRoutedSender(ZRpc rpc, ZPackage pkg)
        {
            if (rpc == null || pkg == null || !IsServer()) return true;

            long claimed;
            int methodHash;
            int pos = pkg.GetPos();
            try
            {
                pkg.ReadLong();           // m_msgID
                claimed = pkg.ReadLong(); // m_senderPeerID
                pkg.ReadLong();           // m_targetPeerID
                pkg.ReadZDOID();          // m_targetZDO
                methodHash = pkg.ReadInt();
            }
            catch (System.Exception)
            {
                // Too short to be a routed package. Vanilla's own Deserialize will say so.
                return true;
            }
            finally
            {
                pkg.SetPos(pos);
            }

            if (!GuardedRoutedMethods.Contains(methodHash)) return true;
            if (!RoutedSenderGuardArmed) return true;

            long actual = ConnectionPeerId(rpc);
            if (actual != 0L && actual == claimed) return true;

            string host = "?";
            try { host = rpc.GetSocket()?.GetHostName() ?? "?"; } catch { }
            AuthLog.Refused(actual, "sender",
                $"dropped a routed RPC (method {methodHash}) from connection {host}: it claims sender {claimed}, the connection is peer {actual}");
            return false;
        }

        /// <summary>The uid of the connected peer that owns this ZRpc, or 0 when there is none.</summary>
        private static long ConnectionPeerId(ZRpc rpc)
        {
            ZNet net = ZNet.instance;
            if (net == null || ZNetPeersField == null) return 0L;
            try
            {
                var peers = ZNetPeersField.GetValue(net) as List<ZNetPeer>;
                if (peers == null) return 0L;
                for (int i = 0; i < peers.Count; i++)
                    if (peers[i] != null && ReferenceEquals(peers[i].m_rpc, rpc)) return peers[i].m_uid;
            }
            catch { }
            return 0L;
        }

        /// <summary>
        /// Apply a fire tick to the player sitting at this keyboard. The server cannot do this
        /// itself - burning is vanilla's own status effect on a live Character, and the only
        /// machine with that object is the one the player is playing on.
        /// </summary>
        public static void ApplyFireDamageToLocalPlayer(float damage)
        {
            Player local = Player.m_localPlayer;
            if (local == null) return;
            ApplyFireDamageTick(local, damage);
        }

        public static void SendStatusResponse(long targetPeer, string statusLine)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcStatusResponse, statusLine); }
            catch (System.Exception ex) { FireLogger.Info($"[IGNITE-TRACE] SendStatusResponse THREW: {ex}"); }
        }

        /// <summary>Print a line into the local in-game console (and always into the log).</summary>
        public static void AddConsoleLine(string msg)
        {
            Console.instance?.AddString(msg);
            FireLogger.Info(msg);
        }

        public static bool IsServer() => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>
        /// The persistent player id behind a hit's attacker, or 0 when there is none —
        /// environmental fire (campfire embers, Ashlands rain) has no attacker, and a
        /// creature attacker has no player id. Resolved from the attacker ZDO directly on
        /// whatever peer processes the damage: the attacker is standing right there
        /// attacking, so their ZDO is loaded. Every member on this path is genuinely
        /// public in the real assembly (checked — not just IL-flagged).
        /// </summary>
        /// <summary>The local player's persistent id, or 0 headless. For attributing acts
        /// typed at a console — commands run where they are typed.</summary>
        public static long LocalPlayerId()
        {
            try
            {
                return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            }
            catch (System.Exception)
            {
                return 0L;
            }
        }

        /// <summary>
        /// 1.0.2: true when <paramref name="playerId"/> is the persistent id of a player on this
        /// server right now: the local player (a listen host) or a connected peer's character.
        /// One ZDO read per peer; called once per client ignite request.
        /// </summary>
        public static bool IsOnlinePlayerId(long playerId)
        {
            if (playerId == 0L) return false;
            try
            {
                if (LocalPlayerId() == playerId) return true;
                ZNet net = ZNet.instance;
                ZDOMan man = ZDOMan.instance;
                if (net == null || man == null) return false;
                List<ZNetPeer> peers = net.GetPeers();
                if (peers == null) return false;
                for (int i = 0; i < peers.Count; i++)
                {
                    ZNetPeer peer = peers[i];
                    if (peer == null || peer.m_characterID.IsNone()) continue;
                    ZDO zdo = man.GetZDO(peer.m_characterID);
                    if (zdo != null && zdo.GetLong(ZDOVars.s_playerID, 0L) == playerId) return true;
                }
                return false;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        public static long AttackerPlayerId(HitData hit)
        {
            try
            {
                if (hit == null || hit.m_attacker == ZDOID.None) return 0L;
                if (ZDOMan.instance == null) return 0L;

                ZDO attacker = ZDOMan.instance.GetZDO(hit.m_attacker);
                if (attacker == null) return 0L;

                return attacker.GetLong(ZDOVars.s_playerID, 0L);
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[IGNITE-TRACE] AttackerPlayerId threw: {ex.Message}");
                return 0L;
            }
        }

        /// <summary>
        /// Registers every FireFront routed RPC. NOT safe to call twice on the same
        /// ZRoutedRpc: its Register is a Dictionary.Add and throws on a duplicate name (it is
        /// ZRpc.Register, the unrouted one, that removes-then-adds), and one throw inside the
        /// block below leaves every later RPC unregistered on that instance. The caller keys
        /// on the instance reference, which ZNet.Awake rebuilds per world. ZRoutedRpc.instance
        /// doesn't exist yet at plugin Awake(), same as ZNet.instance.
        /// </summary>
        public static void RegisterFireRpcs(
            System.Action<long, ZDOID, long> onIgniteRequest,
            System.Action<long, ZDOID, bool> onFireEvent,
            System.Action<long, ZPackage> onGroundFireSync,
            System.Action<long, ZDOID, Vector3, float> onExtinguishRequest,
            System.Action<long, string, string> onConfigSet,
            System.Action<long> onStatusRequest,
            System.Action<long, string> onStatusResponse,
            System.Action<long, string> onCommandRelay,
            System.Action<long, float> onFireDamage,
            System.Action<long> onGroundSyncRequest,
            System.Action<long, ZPackage> onPaintAssign,
            System.Action<long, ZPackage> onObjectFireSync,
            System.Action<long, string, int> onVersion)
        {
            if (ZRoutedRpc.instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] RegisterFireRpcs: ZRoutedRpc.instance is null, registration skipped.");
                return;
            }

            try
            {
                // Pass the delegates DIRECTLY rather than wrapping each in a
                // pointless closure lambda (they already match the exact
                // Action<long, T...> shape Register expects) — a live test
                // showed Valheim's own reflection-based RPC dispatcher throwing
                // "BadImageFormatException: Method has zero rva" specifically on
                // the 3-generic-parameter RoutedMethod (extinguish-request), and
                // the unnecessary extra closure layer is the prime suspect.
                ZRoutedRpc.instance.Register<ZDOID, long>(RpcIgniteRequest, onIgniteRequest);
                ZRoutedRpc.instance.Register<ZDOID, bool>(RpcFireEvent, onFireEvent);
                ZRoutedRpc.instance.Register<ZPackage>(RpcGroundFireSync, onGroundFireSync);
                ZRoutedRpc.instance.Register<ZDOID, Vector3, float>(RpcExtinguishRequest, onExtinguishRequest);
                ZRoutedRpc.instance.Register<string, string>(RpcConfigSet, onConfigSet);
                ZRoutedRpc.instance.Register(RpcStatusRequest, onStatusRequest);
                ZRoutedRpc.instance.Register<string>(RpcStatusResponse, onStatusResponse);
                ZRoutedRpc.instance.Register<string>(RpcCommandRelay, onCommandRelay);
                ZRoutedRpc.instance.Register<float>(RpcFireDamage, onFireDamage);
                ZRoutedRpc.instance.Register(RpcGroundSyncRequest, onGroundSyncRequest);
                ZRoutedRpc.instance.Register<ZPackage>(RpcPaintAssign, onPaintAssign);
                ZRoutedRpc.instance.Register<ZPackage>(RpcObjectFireSync, onObjectFireSync);
                ZRoutedRpc.instance.Register<string, int>(RpcVersion, onVersion);
                FireLogger.Info($"[IGNITE-TRACE] All 13 FireFront RPCs registered successfully (IsServer={IsServer()}).");
                if (IsServer())
                {
                    if (RoutedSenderGuardArmed)
                        FireLogger.Info($"{AuthLog.Prefix} routed-sender guard armed: {GuardedRoutedMethods.Count} methods ({GuardedRoutedMethods.Count - 1} FireFront + RPC_Damage) are checked against the connection they arrive on.");
                    else
                        FireLogger.Warn($"{AuthLog.Prefix} routed-sender guard NOT armed: ZNet.m_peers could not be reflected. Forged sender ids pass as they did before 0.23; every handler's own clamps still apply.");
                }
            }
            catch (System.Exception ex)
            {
                FireLogger.Info($"[IGNITE-TRACE] RPC registration THREW: {ex}");
            }
        }

        /// <summary>Client → server: "something I own just took fire damage, please ignite it."
        /// Carries the ATTACKER's persistent player id (0 = unknown/natural), extracted from
        /// the HitData at the patch — the RPC sender is the object's OWNER, and the owner is
        /// not the arsonist when someone torches a piece in another player's loaded area.</summary>
        public static void SendIgniteRequestToServer(ZDOID id, long igniterPlayerId)
        {
            try
            {
                if (ZRoutedRpc.instance == null)
                {
                    FireLogger.Debug("[IGNITE-TRACE] SendIgniteRequestToServer: ZRoutedRpc.instance is null, not sent.");
                    return;
                }

                long serverPeerId = GetServerPeerId();
                FireLogger.Debug($"[IGNITE-TRACE] SendIgniteRequestToServer: targeting peer {serverPeerId} with ZDOID={id}, igniter={igniterPlayerId}.");

                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RpcIgniteRequest, id, igniterPlayerId);
                FireLogger.Debug("[IGNITE-TRACE] InvokeRoutedRPC call completed without throwing.");
            }
            catch (System.Exception ex)
            {
                // Widened to wrap the WHOLE method, not just InvokeRoutedRPC — the
                // narrower try/catch this replaced could have let an exception in
                // GetServerPeerID() (or anywhere else) escape uncaught out of a
                // Harmony prefix with no visible log line, which is exactly the
                // blind spot a real test just hit: registration logging showed up,
                // but nothing from inside this method did, at all.
                FireLogger.Info($"[IGNITE-TRACE] SendIgniteRequestToServer THREW: {ex}");
            }
        }

        /// <summary>Server → every peer (including itself): "this ZDOID started/stopped burning."</summary>
        public static void BroadcastFireEvent(ZDOID id, bool started)
        {
            if (ZRoutedRpc.instance == null) return;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(GetEverybodyTarget(), RpcFireEvent, id, started);
            }
            catch (System.Exception ex)
            {
                FireLogger.Info($"[IGNITE-TRACE] BroadcastFireEvent THREW: {ex}");
            }
        }

        /// <summary>
        /// Server → every peer: a batched delta of ground cells that started or
        /// stopped burning since the last flush. Ground cells have no ZDOID to
        /// key on (unlike object fire), and churn far more often — up to 50+
        /// concurrent cells cycling every few seconds — so this is batched once
        /// per second rather than one RPC per cell event, the same reasoning
        /// that drove the batched terrain-paint rewrite earlier.
        /// </summary>
        /// <summary>Client -> server: "tell me every ground cell you have alight right now."</summary>
        /// <summary>Client -> server: this build's version and wire protocol (Fire/VersionCheck.cs).</summary>
        public static void SendVersionToServer(string version, int protocol)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RpcVersion, version, protocol); }
            catch (System.Exception ex) { FireLogger.Debug($"SendVersionToServer threw: {ex.Message}"); }
        }

        /// <summary>Server -> one peer: the answer to its version check.</summary>
        public static void SendVersionTo(long peer, string version, int protocol)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcVersion, version, protocol); }
            catch (System.Exception ex) { FireLogger.Debug($"SendVersionTo threw: {ex.Message}"); }
        }

        /// <summary>The uid of every connected peer that has finished its handshake.</summary>
        public static void CollectReadyPeerIds(List<long> into)
        {
            ZNet net = ZNet.instance;
            if (net == null || ZNetPeersField == null) return;
            try
            {
                var peers = ZNetPeersField.GetValue(net) as List<ZNetPeer>;
                if (peers == null) return;
                for (int i = 0; i < peers.Count; i++)
                    if (peers[i] != null && peers[i].m_uid != 0L && peers[i].IsReady()) into.Add(peers[i].m_uid);
            }
            catch { }
        }

        /// <summary>True once a connected peer's character has spawned (vanilla's CharacterID RPC sets it).</summary>
        public static bool PeerHasCharacter(long peerId)
        {
            try
            {
                ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
                return peer != null && !peer.m_characterID.IsNone();
            }
            catch { return false; }
        }

        /// <summary>The socket host name (a platform id such as Steam_7656...) of a connected peer, or null.</summary>
        public static string PeerHostName(long peerId)
        {
            try { return ZNet.instance?.GetPeer(peerId)?.m_rpc?.GetSocket()?.GetHostName(); }
            catch { return null; }
        }

        /// <summary>
        /// Server -> one peer, through two channels VANILLA registers on every client, so it
        /// reaches a client with an old FireFront or none: the per-connection "RemotePrint"
        /// (ZNet.RPC_PeerInfo registers it; prints to the client's console) and the routed
        /// "ShowMessage" (MessageHud.Awake registers it; int MessageType, 2 = Center).
        /// </summary>
        public static void TellPeerDirectly(long peerId, string text)
        {
            try
            {
                ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
                peer?.m_rpc?.Invoke("RemotePrint", text);
            }
            catch (System.Exception ex) { FireLogger.Debug($"TellPeerDirectly RemotePrint threw: {ex.Message}"); }
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(peerId, "ShowMessage", (int)MessageHud.MessageType.Center, text); }
            catch (System.Exception ex) { FireLogger.Debug($"TellPeerDirectly ShowMessage threw: {ex.Message}"); }
        }

        public static void RequestGroundSnapshot()
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RpcGroundSyncRequest); }
            catch (System.Exception ex) { FireLogger.Debug($"RequestGroundSnapshot threw: {ex.Message}"); }
        }

        /// <summary>
        /// Server -> ONE peer, in the same wire format the periodic delta broadcast uses, so the
        /// client needs no second handler and no second code path to get it wrong in.
        /// </summary>
        public static void SendGroundFireSyncTo(long targetPeer, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcGroundFireSync, pkg); }
            catch (System.Exception ex) { FireLogger.Debug($"SendGroundFireSyncTo threw: {ex.Message}"); }
        }

        /// <summary>
        /// Server -> ONE peer: every OBJECT fire alight right now (ZDOID, age, smouldering), the
        /// answer to the same snapshot request as the ground set. Object fires are otherwise only
        /// ever announced by a FireEvent at ignition, which a player who joins later never hears.
        /// </summary>
        public static void SendObjectFireSyncTo(long targetPeer, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcObjectFireSync, pkg); }
            catch (System.Exception ex) { FireLogger.Debug($"SendObjectFireSyncTo threw: {ex.Message}"); }
        }

        /// <summary>Server -> ONE peer: "lay real dirt on these cells" (see FireManager.AssignPendingPaint).</summary>
        public static void SendPaintAssignTo(long targetPeer, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcPaintAssign, pkg); }
            catch (System.Exception ex) { FireLogger.Debug($"SendPaintAssignTo threw: {ex.Message}"); }
        }

        public static void BroadcastGroundFireSync(ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(GetEverybodyTarget(), RpcGroundFireSync, pkg);
            }
            catch (System.Exception ex)
            {
                FireLogger.Info($"[IGNITE-TRACE] BroadcastGroundFireSync THREW: {ex}");
            }
        }

        /// <summary>
        /// Client → server: "I pressed the extinguish key — put out whatever I'm
        /// aiming at (targetId, or ZDOID.None if nothing) and any ground fire
        /// near me (playerPos/groundRadius)." Extinguishing has the same
        /// authority problem ignition had: it was only ever removing from the
        /// CALLER's own _burning/_groundBurning, which are empty on a real
        /// client now — so the extinguish key silently did nothing for a
        /// connected player until this existed.
        /// </summary>
        public static void SendExtinguishRequestToServer(ZDOID targetId, Vector3 playerPos, float groundRadius)
        {
            if (ZRoutedRpc.instance == null) return;
            try
            {
                long serverPeerId = GetServerPeerId();
                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RpcExtinguishRequest, targetId, playerPos, groundRadius);
            }
            catch (System.Exception ex)
            {
                FireLogger.Info($"[IGNITE-TRACE] SendExtinguishRequestToServer THREW: {ex}");
            }
        }

        /// <summary>
        /// Client → server: "apply this fireset on YOUR config." Console
        /// commands run where they're typed, but every FireFront setting that
        /// matters is read by the server's simulation — before this forward
        /// existed, a client's fireset changed its own irrelevant copy and the
        /// server never heard (cost two real debugging rounds: 'rampstart 1'
        /// and 'burnbuildings false' both "didn't work").
        /// </summary>
        public static void SendConfigSetToServer(string key, string raw)
        {
            if (ZRoutedRpc.instance == null) return;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(GetServerPeerId(), RpcConfigSet, key, raw);
            }
            catch (System.Exception ex)
            {
                FireLogger.Info($"[IGNITE-TRACE] SendConfigSetToServer THREW: {ex}");
            }
        }
    }
}