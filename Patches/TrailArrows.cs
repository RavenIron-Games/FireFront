using System.Collections.Generic;
using System.Reflection;
using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Patches
{
    /// <summary>
    /// 1.0.6: two firefighting arrows that act along their whole flight, not where they land.
    ///
    ///   Ember arrow   - cloned from ArrowFire. Shot low, it drops embers on the ground under its
    ///                   flight: a controlled burn that never spreads and lights no object, then
    ///                   holds as a firebreak (FireManager.ApplyArrowTrail, kind 0).
    ///   Dousing arrow - cloned from ArrowFrost. The air tanker: everything under its flight line,
    ///                   from up to DousingMaxHeight up, is put out and soaked (kind 1).
    ///
    /// Both carry the wood arrow's damage and no fire or frost, so the impact itself starts and
    /// slows nothing. Built the Dousing Bomb's way: clones registered straight into ObjectDB and
    /// ZNetScene under stable names, no Jotunn. The projectile prefab is vanilla's (shared with the
    /// donor arrow); the arrow is recognised in flight by Projectile.m_ammo's item name.
    ///
    /// Vanilla moves a projectile on its owner only (Projectile.FixedUpdate returns early elsewhere),
    /// so the shooter's game samples the trail and sends it in batches; the server applies it.
    /// </summary>
    public static class TrailArrows
    {
        public const string EmberPrefab = "FireFront_ArrowEmber";
        public const string EmberName = "Ember arrow";
        public const string DousingPrefab = "FireFront_ArrowDousing";
        public const string DousingName = "Dousing arrow";

        private static GameObject _ember;
        private static GameObject _dousing;
        private static GameObject _container;
        private static bool _failureLogged;

        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ItemByHashField = typeof(ObjectDB).GetField("m_itemByHash", AnyInstance);
        private static readonly FieldInfo ItemsField = typeof(ObjectDB).GetField("m_items", AnyInstance);
        private static readonly FieldInfo RecipesField = typeof(ObjectDB).GetField("m_recipes", AnyInstance);

        private static GameObject Clone(ObjectDB db, string donorName, string prefabName, string displayName, string description)
        {
            GameObject donor = db.GetItemPrefab(donorName);
            GameObject wood = db.GetItemPrefab("ArrowWood");
            if (donor == null || wood == null) return null; // bootstrap ObjectDB: the retry from CopyOtherDB builds it

            if (_container == null)
            {
                _container = new GameObject("FireFront_ArrowPrefabs");
                _container.SetActive(false);
                Object.DontDestroyOnLoad(_container);
            }
            GameObject clone = Object.Instantiate(donor, _container.transform);
            clone.name = prefabName;
            ItemDrop drop = clone.GetComponent<ItemDrop>();
            ItemDrop woodDrop = wood.GetComponent<ItemDrop>();
            if (drop == null || woodDrop == null) { Object.Destroy(clone); return null; }

            // m_shared is shared with the donor: copy it before changing anything.
            drop.m_itemData.m_shared = CloneShared(drop.m_itemData.m_shared);
            drop.m_itemData.m_shared.m_name = displayName;
            drop.m_itemData.m_shared.m_description = description;
            // The wood arrow's damage: no fire, no frost, so the hit starts and slows nothing.
            drop.m_itemData.m_shared.m_damages = woodDrop.m_itemData.m_shared.m_damages;
            drop.m_itemData.m_shared.m_damagesPerLevel = woodDrop.m_itemData.m_shared.m_damagesPerLevel;
            drop.m_itemData.m_shared.m_attackStatusEffect = null;
            return clone;
        }

        private static ItemDrop.ItemData.SharedData CloneShared(ItemDrop.ItemData.SharedData source)
        {
            var copy = new ItemDrop.ItemData.SharedData();
            FieldInfo[] fields = typeof(ItemDrop.ItemData.SharedData).GetFields(AnyInstance);
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(copy, fields[i].GetValue(source));
            return copy;
        }

        public static void EnsureCreated(ObjectDB db)
        {
            if (db == null) return;
            if (_ember == null)
                _ember = Clone(db, "ArrowFire", EmberPrefab, EmberName,
                    "Shot low over grass, it drops embers along its flight: a controlled burn that never spreads, " +
                    "then holds as a firebreak. Lay one ahead of a wildfire and the fire stops there.");
            if (_dousing == null)
                _dousing = Clone(db, "ArrowFrost", DousingPrefab, DousingName,
                    "An air tanker in an arrow: every fire under its flight goes out, and the ground stays soaked " +
                    "for a while. Fire it over the front, or along a line the fire must not cross.");
        }

        public static void RegisterIntoObjectDB(ObjectDB db)
        {
            EnsureCreated(db);
            if (_ember == null || _dousing == null) return;

            var itemByHash = ItemByHashField?.GetValue(db) as Dictionary<int, GameObject>;
            var items = ItemsField?.GetValue(db) as List<GameObject>;
            var recipes = RecipesField?.GetValue(db) as List<Recipe>;
            if (itemByHash == null || items == null || recipes == null)
            {
                if (!_failureLogged) { _failureLogged = true; FireLogger.Warn("[ARROWS] ObjectDB registries unreadable; Ember and Dousing arrows unavailable."); }
                return;
            }

            // The fire arrow's crafting station (the workbench), read from its own recipe.
            CraftingStation station = null;
            for (int i = 0; i < recipes.Count; i++)
                if (recipes[i] != null && recipes[i].m_item != null && recipes[i].m_item.gameObject.name == "ArrowFire")
                { station = recipes[i].m_craftingStation; break; }

            Add(db, itemByHash, items, recipes, _ember, "Recipe_FireFrontArrowEmber", station,
                ("Wood", 8), ("Coal", 4), ("Feathers", 2));
            Add(db, itemByHash, items, recipes, _dousing, "Recipe_FireFrontArrowDousing", station,
                ("Wood", 8), ("Resin", 4), ("LeatherScraps", 2), ("Feathers", 2));

            if (ZNetScene.instance != null)
            {
                ValheimBridge.RegisterPrefabWithZNetScene(ZNetScene.instance, _ember);
                ValheimBridge.RegisterPrefabWithZNetScene(ZNetScene.instance, _dousing);
            }
        }

        private static void Add(ObjectDB db, Dictionary<int, GameObject> itemByHash, List<GameObject> items, List<Recipe> recipes,
                                GameObject prefab, string recipeName, CraftingStation station, params (string item, int amount)[] cost)
        {
            int hash = prefab.name.GetStableHashCode();
            if (!itemByHash.ContainsKey(hash)) { items.Add(prefab); itemByHash.Add(hash, prefab); }

            for (int i = 0; i < recipes.Count; i++)
                if (recipes[i] != null && recipes[i].name == recipeName) return;

            var reqs = new List<Piece.Requirement>();
            foreach ((string item, int amount) in cost)
            {
                GameObject res = db.GetItemPrefab(item);
                if (res == null) { FireLogger.Warn($"[ARROWS] recipe ingredient '{item}' missing; {prefab.name} registered without a recipe."); return; }
                reqs.Add(new Piece.Requirement { m_resItem = res.GetComponent<ItemDrop>(), m_amount = amount });
            }
            Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = recipeName;
            recipe.m_item = prefab.GetComponent<ItemDrop>();
            recipe.m_amount = 20;
            recipe.m_enabled = true;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = 1;
            recipe.m_resources = reqs.ToArray();
            recipes.Add(recipe);
            FireLogger.Info($"[ARROWS] '{prefab.name}' registered, 20 per craft at {(station != null ? station.name : "hand")}.");
        }

        public static void RegisterWithScene(ZNetScene scene)
        {
            EnsureCreated(ObjectDB.instance);
            if (_ember != null) ValheimBridge.RegisterPrefabWithZNetScene(scene, _ember);
            if (_dousing != null) ValheimBridge.RegisterPrefabWithZNetScene(scene, _dousing);
        }

        // ---- in flight ---------------------------------------------------------------------------

        private const float EmberSpacing = 1f;       // one ember per ground cell
        private const float EmberMaxHeight = 6f;     // embers from higher up go out before they land
        private const float DousingSpacing = 2f;
        private const float DousingMaxHeight = 30f;  // the air tanker drops from height
        private const int FlushAt = 16;
        private const int MaxPointsPerArrow = 120;

        private class Trail
        {
            public int Kind;
            public long Igniter;
            public Vector3 Last;
            public bool HaveLast;
            public int Taken;
            public readonly List<Vector3> Points = new List<Vector3>();
        }

        private static readonly Dictionary<Projectile, Trail> Trails = new Dictionary<Projectile, Trail>();
        private static readonly List<Projectile> PruneScratch = new List<Projectile>();
        private static readonly FieldInfo AmmoField = typeof(Projectile).GetField("m_ammo", AnyInstance);
        private static readonly FieldInfo OwnerField = typeof(Projectile).GetField("m_owner", AnyInstance);
        private static readonly FieldInfo DidHitField = typeof(Projectile).GetField("m_didHit", AnyInstance);

        private static int KindOf(Projectile p)
        {
            var ammo = AmmoField?.GetValue(p) as ItemDrop.ItemData;
            string name = ammo?.m_shared?.m_name;
            if (name == EmberName) return FireManager.ArrowTrailKindEmber;
            if (name == DousingName) return FireManager.ArrowTrailKindDousing;
            return -1;
        }

        // The shooter's game stamps the arrow's kind (+1) on its ZDO, because m_ammo is set only there
        // (Projectile.Setup runs on the shooter): every other game reads the stamp to draw the water.
        private static readonly int TrailKindKey = "FireFront_TrailKind".GetStableHashCode();

        public static void OnFixedUpdate(Projectile p)
        {
            if (!Trails.TryGetValue(p, out Trail t))
            {
                ZNetView nview = p.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                if (!nview.IsOwner())
                {
                    // Someone else's arrow: draw the air tanker's water if it is a Dousing arrow.
                    ZDO zdo = nview.GetZDO();
                    if (zdo != null && zdo.GetInt(TrailKindKey) - 1 == FireManager.ArrowTrailKindDousing) DousingRain.Attach(p);
                    return;
                }
                int kind = KindOf(p);
                if (kind < 0) return;
                if (!(OwnerField?.GetValue(p) is Player player)) return;
                PruneDead();
                t = new Trail { Kind = kind, Igniter = player.GetPlayerID() };
                Trails[p] = t;
                nview.GetZDO()?.Set(TrailKindKey, kind + 1);
                if (kind == FireManager.ArrowTrailKindDousing) DousingRain.Attach(p);
            }
            if (DidHitField != null && (bool)DidHitField.GetValue(p)) return;
            if (t.Taken >= MaxPointsPerArrow) return;

            Vector3 pos = p.transform.position;
            float spacing = t.Kind == FireManager.ArrowTrailKindEmber ? EmberSpacing : DousingSpacing;
            if (t.HaveLast)
            {
                float ddx = pos.x - t.Last.x, ddz = pos.z - t.Last.z;
                if (ddx * ddx + ddz * ddz < spacing * spacing) return;
            }
            t.Last = pos;
            t.HaveLast = true;
            if (pos.y > 3000f) return; // an interior: no ground under it that FireFront burns

            float ground = ValheimBridge.GetGroundHeight(pos, out bool sampled);
            if (!sampled) return;
            float height = pos.y - ground;
            float max = t.Kind == FireManager.ArrowTrailKindEmber ? EmberMaxHeight : DousingMaxHeight;
            if (height < -1f || height > max) return;

            t.Points.Add(new Vector3(pos.x, ground, pos.z));
            t.Taken++;
            if (t.Points.Count >= FlushAt) Flush(t);
        }

        public static void OnHit(Projectile p, Vector3 hitPoint)
        {
            if (!Trails.TryGetValue(p, out Trail t)) return;
            // The landing spot counts too: an arrow dropped onto the fire line marks its end.
            if (t.Taken < MaxPointsPerArrow && hitPoint.y < 3000f)
            {
                float ground = ValheimBridge.GetGroundHeight(hitPoint, out bool sampled);
                float max = t.Kind == FireManager.ArrowTrailKindEmber ? EmberMaxHeight : DousingMaxHeight;
                if (sampled && hitPoint.y - ground <= max) t.Points.Add(new Vector3(hitPoint.x, ground, hitPoint.z));
            }
            Flush(t);
            Trails.Remove(p);
        }

        private static void PruneDead()
        {
            PruneScratch.Clear();
            foreach (KeyValuePair<Projectile, Trail> kv in Trails) if (kv.Key == null) PruneScratch.Add(kv.Key);
            foreach (Projectile p in PruneScratch) { Flush(Trails[p]); Trails.Remove(p); }
        }

        private static void Flush(Trail t)
        {
            if (t.Points.Count == 0) return;
            var batch = new List<Vector3>(t.Points);
            t.Points.Clear();
            if (ValheimBridge.IsServer()) FireManager.Instance?.ApplyArrowTrail(t.Kind, batch, t.Igniter);
            else ValheimBridge.SendArrowTrailToServer(t.Kind, t.Igniter, batch);
        }
    }

    /// <summary>
    /// The Dousing arrow's water: a curtain of droplets and spray pouring from the arrow from the
    /// moment it leaves the bow, an air tanker's drop. Purely visual, built on every game that sees
    /// the arrow; world-space particles, so the water keeps falling where it was released. Stops
    /// pouring once the arrow stops moving (it has landed), and goes with the arrow when vanilla
    /// removes it.
    /// </summary>
    public class DousingRain : MonoBehaviour
    {
        private const string ChildName = "FireFront_DousingRain";
        private ParticleSystem[] _systems;
        private Vector3 _last;
        private float _stillFor;
        private bool _stopped;

        public static void Attach(Projectile p)
        {
            if (p == null || p.transform.Find(ChildName) != null) return;
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return; // nobody sees it there
            var go = new GameObject(ChildName);
            go.transform.SetParent(p.transform, false);
            go.AddComponent<DousingRain>().Build();
        }

        private void Build()
        {
            Material mat = FireFrontTextureGenerator.GetOrCreateSmokeMaterial();
            _systems = new[]
            {
                // Droplets: thin streaks that fall and die on the ground.
                MakeSystem("Drops", mat, rate: 160f, life: (1.2f, 2.2f), size: (0.05f, 0.12f), gravity: 1.3f,
                           c0: new Color(0.70f, 0.85f, 1.00f, 0.75f), c1: new Color(0.85f, 0.93f, 1.00f, 0.55f),
                           shapeRadius: 0.35f, stretch: true, max: 400),
                // Spray: a soft spreading curtain round the droplets.
                MakeSystem("Spray", mat, rate: 30f, life: (1.0f, 1.8f), size: (0.8f, 1.8f), gravity: 0.45f,
                           c0: new Color(0.80f, 0.90f, 1.00f, 0.28f), c1: new Color(0.92f, 0.96f, 1.00f, 0.18f),
                           shapeRadius: 0.6f, stretch: false, max: 80),
            };
            _last = transform.position;
        }

        private ParticleSystem MakeSystem(string name, Material mat, float rate, (float, float) life, (float, float) size,
                                          float gravity, Color c0, Color c1, float shapeRadius, bool stretch, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.Item1, life.Item2);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(size.Item1, size.Item2);
            main.startColor = new ParticleSystem.MinMaxGradient(c0, c1);
            main.gravityModifier = gravity;
            main.maxParticles = max;

            var emission = ps.emission;
            emission.rateOverTime = rate;
            // Rate over distance too, so a fast arrow leaves an unbroken line rather than dots.
            emission.rateOverDistance = rate * 0.15f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = shapeRadius;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.7f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            if (!stretch)
            {
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 1.8f)));
            }

            var collision = ps.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Low;
            collision.lifetimeLoss = 1f; // water that reaches the ground is gone
            collision.bounce = 0f;

            var r = go.GetComponent<ParticleSystemRenderer>();
            if (stretch)
            {
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.06f;
                r.lengthScale = 2f;
            }
            else r.renderMode = ParticleSystemRenderMode.Billboard;
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        private void Update()
        {
            if (_stopped || _systems == null) return;
            Vector3 pos = transform.position;
            float moved = (pos - _last).sqrMagnitude;
            _last = pos;
            _stillFor = moved < 0.0025f ? _stillFor + Time.deltaTime : 0f;
            if (_stillFor > 0.15f)
            {
                _stopped = true;
                foreach (ParticleSystem ps in _systems) if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    public static class ObjectDBAwakeArrowsPatch
    {
        private static bool _thrown;
        private static void Postfix(ObjectDB __instance)
        {
            try { TrailArrows.RegisterIntoObjectDB(__instance); }
            catch (System.Exception ex) { if (!_thrown) { _thrown = true; FireLogger.Warn($"[ARROWS] registration threw at ObjectDB.Awake: {ex.Message}"); } }
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
    public static class ObjectDBCopyArrowsPatch
    {
        private static bool _thrown;
        private static void Postfix(ObjectDB __instance)
        {
            try { TrailArrows.RegisterIntoObjectDB(__instance); }
            catch (System.Exception ex) { if (!_thrown) { _thrown = true; FireLogger.Warn($"[ARROWS] registration threw at ObjectDB.CopyOtherDB: {ex.Message}"); } }
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class ZNetSceneAwakeArrowsPatch
    {
        private static bool _thrown;
        private static void Postfix(ZNetScene __instance)
        {
            try { TrailArrows.RegisterWithScene(__instance); }
            catch (System.Exception ex) { if (!_thrown) { _thrown = true; FireLogger.Warn($"[ARROWS] registration threw at ZNetScene.Awake: {ex.Message}"); } }
        }
    }

    [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
    public static class ProjectileFixedUpdateTrailPatch
    {
        private static bool _thrown;
        private static void Postfix(Projectile __instance)
        {
            try { TrailArrows.OnFixedUpdate(__instance); }
            catch (System.Exception ex) { if (!_thrown) { _thrown = true; FireLogger.Warn($"[ARROWS] trail sampling threw: {ex.Message}; trail arrows stop working this session."); } }
        }
    }

    [HarmonyPatch(typeof(Projectile), "OnHit")]
    public static class ProjectileOnHitTrailPatch
    {
        private static bool _thrown;
        private static void Postfix(Projectile __instance, Vector3 hitPoint)
        {
            try { TrailArrows.OnHit(__instance, hitPoint); }
            catch (System.Exception ex) { if (!_thrown) { _thrown = true; FireLogger.Warn($"[ARROWS] trail flush threw: {ex.Message}"); } }
        }
    }
}
