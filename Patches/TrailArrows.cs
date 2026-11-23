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
                    "Shot over grass, it drops embers along its whole flight: a controlled burn that never spreads, " +
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
        // The whole flight, from the shooter to where it lands (1.0.6 test: at 6 m a level shot laid
        // embers only at the shooter's feet and burned him). Starts EmberClearOfShooter out.
        private const float EmberMaxHeight = 80f;
        private const float EmberClearOfShooter = 5f;
        private const float DousingSpacing = 4f;    // the drop is 24 m wide: a report every 4 m is plenty
        private const float DousingMaxHeight = 30f;  // the air tanker drops from height
        private const int FlushAt = 16;
        private const int MaxPointsPerArrow = 120;

        private class Trail
        {
            public int Kind;
            public long Igniter;
            public Vector3 From; // where the shooter stood when it was loosed
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
                t = new Trail { Kind = kind, Igniter = player.GetPlayerID(), From = player.transform.position };
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
            if (t.Kind == FireManager.ArrowTrailKindEmber && !ClearOfShooter(t, pos)) return;

            t.Points.Add(new Vector3(pos.x, ground, pos.z));
            t.Taken++;
            if (t.Points.Count >= FlushAt) Flush(t);
        }

        public static void OnHit(Projectile p, Vector3 hitPoint)
        {
            if (!Trails.TryGetValue(p, out Trail t)) return;
            // The landing spot counts too: an Ember arrow's marks the end of its line, and a Dousing
            // arrow's is a splash of its own, 1.6 times as wide, so a shot straight into a fire puts
            // out a wide circle around where it hits.
            List<Vector3> impact = null;
            if (hitPoint.y < 3000f)
            {
                float ground = ValheimBridge.GetGroundHeight(hitPoint, out bool sampled);
                float max = t.Kind == FireManager.ArrowTrailKindEmber ? EmberMaxHeight : DousingMaxHeight;
                if (sampled && hitPoint.y - ground <= max)
                {
                    var landing = new Vector3(hitPoint.x, ground, hitPoint.z);
                    if (t.Kind == FireManager.ArrowTrailKindDousing) impact = new List<Vector3> { landing };
                    else if (t.Taken < MaxPointsPerArrow && ClearOfShooter(t, landing)) t.Points.Add(landing);
                }
            }
            Flush(t);
            if (impact != null)
            {
                if (ValheimBridge.IsServer()) FireManager.Instance?.ApplyArrowTrail(FireManager.ArrowTrailKindDousingImpact, impact, t.Igniter);
                else ValheimBridge.SendArrowTrailToServer(FireManager.ArrowTrailKindDousingImpact, t.Igniter, impact);
            }
            Trails.Remove(p);
        }

        private static bool ClearOfShooter(Trail t, Vector3 at)
        {
            float dx = at.x - t.From.x, dz = at.z - t.From.z;
            return dx * dx + dz * dz >= EmberClearOfShooter * EmberClearOfShooter;
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
    /// The Dousing arrow's retardant drop, after CAL FIRE's: a heavy dark-red column leaves the arrow
    /// from the moment it is loosed and breaks into a wide billowing curtain that sinks to the ground,
    /// where it leaves a red strip, darkest down the middle. Purely visual, built on every game that sees
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
        private Vector3 _lastStain;
        private bool _haveStain;
        private int _stains;
        private const float StainSpacing = 4f;      // rows along the flight
        private const float StainMaxHeight = 30f;   // the same height the drop works from
        private const float StainDelay = 1.8f;      // about when the curtain reaches the ground
        private const int MaxStainsPerArrow = 240;

        /// <summary>Half the drop's width, metres: the server's soak radius as this game knows it.</summary>
        private static float HalfWidth => Mathf.Clamp(FireConfig.DousingArrowRadius.Value, 0.5f, 20f);

        // The curtain and streaks were tuned at 5 m either side; a wider drop needs more of them.
        private static float Volume => Mathf.Clamp(HalfWidth / 5f, 1f, 3f);

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
            float w = HalfWidth;
            _systems = new[]
            {
                // The load leaving the tank: a heavy dark-red column that drops straight down fast.
                MakeSystem("Column", mat, rate: 170f * Volume, life: (1.0f, 1.6f), size: (1.0f * Volume, 1.7f * Volume), gravity: 1.1f,
                           c0: new Color(0.62f, 0.10f, 0.05f, 0.95f), c1: new Color(0.75f, 0.16f, 0.07f, 0.90f),
                           width: 0.6f * Volume, height: 0.6f * Volume, stretch: false, max: (int)(420 * Volume), growth: 2.8f, noise: 0.25f, drift: 0.4f),
                // The curtain it breaks into: wide across the flight, billowing, sinking slowly.
                // Thicker and taller than before, and it hangs: slow to sink, drifting with the wind.
                MakeSystem("Curtain", mat, rate: 160f * Volume, life: (5.0f, 8.5f), size: (2.0f * Mathf.Sqrt(Volume), 3.6f * Mathf.Sqrt(Volume)), gravity: 0.12f,
                           c0: new Color(0.80f, 0.20f, 0.09f, 0.82f), c1: new Color(0.94f, 0.34f, 0.15f, 0.68f),
                           width: w * 1.5f, height: 2.5f * Volume, stretch: false, max: (int)(1100 * Volume), growth: 4.2f, noise: 1.0f, drift: 1f),
                // Heavier streaks falling out of the curtain to the ground, the full width.
                MakeSystem("Streaks", mat, rate: 230f * Volume, life: (1.5f, 2.8f), size: (0.08f, 0.22f), gravity: 1.25f,
                           c0: new Color(0.70f, 0.14f, 0.06f, 0.85f), c1: new Color(0.88f, 0.28f, 0.12f, 0.65f),
                           width: w * 1.6f, height: 1.5f, stretch: true, max: (int)(700 * Volume), growth: 1f, noise: 0f, drift: 0.3f),
            };
            _drift = new[] { 0.4f, 1f, 0.3f };
            ApplyWind();
            _roar = RetardantSound.StartRoar(gameObject);
            _last = transform.position;
        }

        // How hard the wind pushes each system (column, curtain, streaks), m/s at full wind.
        private float[] _drift;
        private const float WindPush = 4f;
        private float _nextWind;
        private AudioSource _roar;

        /// <summary>The drop drifts downwind: vanilla's wind direction times its live strength.</summary>
        private void ApplyWind()
        {
            if (_systems == null || _drift == null) return;
            Vector3? dir = ValheimBridge.GetWindDirection();
            float? strength = ValheimBridge.GetWindIntensity();
            Vector3 wind = dir.HasValue ? new Vector3(dir.Value.x, 0f, dir.Value.z).normalized * (WindPush * Mathf.Clamp01(strength ?? 0.5f)) : Vector3.zero;
            for (int i = 0; i < _systems.Length && i < _drift.Length; i++)
            {
                if (_systems[i] == null) continue;
                var vel = _systems[i].velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                // x, y and z share one curve mode or Unity logs an error every frame (FireVFXController).
                vel.x = new ParticleSystem.MinMaxCurve(wind.x * _drift[i]);
                vel.y = new ParticleSystem.MinMaxCurve(0f);
                vel.z = new ParticleSystem.MinMaxCurve(wind.z * _drift[i]);
            }
        }

        /// <param name="width">Full width of the emitter ACROSS the flight (the arrow's local x; vanilla
        /// points a projectile along its velocity), so the curtain spreads sideways, not along the line.</param>
        private ParticleSystem MakeSystem(string name, Material mat, float rate, (float, float) life, (float, float) size,
                                          float gravity, Color c0, Color c1, float width, float height, bool stretch, int max,
                                          float growth, float noise, float drift)
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
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(size.Item1, size.Item2);
            main.startColor = new ParticleSystem.MinMaxGradient(c0, c1);
            main.gravityModifier = gravity;
            main.maxParticles = max;

            var emission = ps.emission;
            emission.rateOverTime = rate;
            // Rate over distance too, so a fast arrow leaves an unbroken line rather than dots.
            emission.rateOverDistance = rate * 0.15f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(Mathf.Max(0.2f, width), height, 0.8f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.75f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            if (!stretch)
            {
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0f, 0.4f), new Keyframe(0.3f, growth * 0.6f), new Keyframe(1f, growth)));
            }
            if (noise > 0f)
            {
                // Billowing: the curtain churns and spreads as it sinks.
                var n = ps.noise;
                n.enabled = true;
                n.strength = noise;
                n.frequency = 0.35f;
                n.scrollSpeed = 0.25f;
                n.quality = ParticleSystemNoiseQuality.Low;
            }

            var collision = ps.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Low;
            collision.lifetimeLoss = 1f; // retardant that reaches the ground is gone (the stain takes over)
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

        // Across the strip, as fractions of the half width: the middle twice, so it stains darkest
        // down the centre and thins toward the edges, like a real drop's coverage.
        private static readonly float[] StainOffsets = { -0.72f, -0.36f, 0f, 0f, 0.36f, 0.72f };

        private void LayStain(Vector3 pos)
        {
            if (_stains >= MaxStainsPerArrow || pos.y > 3000f) return;
            if (_haveStain)
            {
                float dx = pos.x - _lastStain.x, dz = pos.z - _lastStain.z;
                if (dx * dx + dz * dz < StainSpacing * StainSpacing) return;
            }
            Vector3 dir = _haveStain ? new Vector3(pos.x - _lastStain.x, 0f, pos.z - _lastStain.z) : transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            Vector3 across = Vector3.Cross(Vector3.up, dir.normalized);
            _lastStain = pos;
            _haveStain = true;

            float ground = ValheimBridge.GetGroundHeight(pos, out bool sampled);
            if (!sampled || pos.y - ground > StainMaxHeight || pos.y - ground < -1f) return;
            float lifetime = Mathf.Clamp(FireConfig.DousingArrowSoakSeconds.Value, 10f, 1800f);
            float w = HalfWidth;
            float quad = Mathf.Max(1.5f, w * 0.85f); // each patch, metres across
            Vector3 at = new Vector3(pos.x, ground, pos.z);
            foreach (float f in StainOffsets)
                RetardantStains.Enqueue(at + across * (f * w), StainDelay, lifetime, quad);
            _stains += StainOffsets.Length;

            // Steam where it lands on fire: whatever this game draws as burning under the row, read
            // NOW (the server puts it out within moments), shown when the retardant gets there.
            if (_steams < MaxSteamsPerArrow && FireManager.Instance != null)
            {
                _fireScratch.Clear();
                FireManager.Instance.CollectVisibleFiresNear(at, w, _fireScratch);
                for (int i = 0; i < _fireScratch.Count && _steams < MaxSteamsPerArrow; i++)
                {
                    Vector3 f = _fireScratch[i];
                    bool near = false;
                    foreach (Vector3 done in _steamAt)
                        if ((done - f).sqrMagnitude < SteamSpacing * SteamSpacing) { near = true; break; }
                    if (near) continue;
                    _steamAt.Add(f);
                    _steams++;
                    RetardantStains.EnqueueSteam(f, SteamDelay);
                }
            }
        }

        private readonly List<Vector3> _fireScratch = new List<Vector3>();
        private readonly List<Vector3> _steamAt = new List<Vector3>();
        private int _steams;
        private const int MaxSteamsPerArrow = 40;
        private const float SteamSpacing = 2.5f;
        private const float SteamDelay = 1.4f;

        /// <summary>Where it lands: a round stain 1.6 times the drop's width, matching the server's splash.</summary>
        private void Splash(Vector3 pos)
        {
            if (pos.y > 3000f) return;
            float ground = ValheimBridge.GetGroundHeight(pos, out bool sampled);
            if (!sampled || pos.y - ground > StainMaxHeight) return;
            float lifetime = Mathf.Clamp(FireConfig.DousingArrowSoakSeconds.Value, 10f, 1800f);
            float r = HalfWidth * 1.6f;
            float quad = Mathf.Max(1.5f, r * 0.7f);
            Vector3 at = new Vector3(pos.x, ground, pos.z);
            RetardantSound.SplashAt(at);
            RetardantStains.Enqueue(at, 0.6f, lifetime, quad);
            RetardantStains.Enqueue(at, 0.6f, lifetime, quad);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                RetardantStains.Enqueue(at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (r * 0.6f), 0.6f, lifetime, quad);
            }
        }

        private void Update()
        {
            if (_stopped || _systems == null) return;
            Vector3 pos = transform.position;
            LayStain(pos);
            if (Time.time >= _nextWind) { _nextWind = Time.time + 0.5f; ApplyWind(); }
            float moved = (pos - _last).sqrMagnitude;
            _last = pos;
            _stillFor = moved < 0.0025f ? _stillFor + Time.deltaTime : 0f;
            if (_stillFor > 0.15f)
            {
                _stopped = true;
                foreach (ParticleSystem ps in _systems) if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                if (_roar != null) _roar.Stop();
                Splash(pos);
            }
        }
    }

    /// <summary>The DousingRain stain queue: marks wait StainDelay, then go down. Lives on its own
    /// object because the arrow (and its rain) is gone long before the last stain lands.</summary>
    public class RetardantStains : MonoBehaviour
    {
        private static RetardantStains _instance;
        private readonly List<(float at, Vector3 pos, float lifetime, float size)> _queue = new List<(float, Vector3, float, float)>();
        private const int MaxQueued = 1200;

        private readonly List<(float at, Vector3 pos)> _steam = new List<(float, Vector3)>();

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("FireFront_RetardantStains");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<RetardantStains>();
        }

        public static void EnqueueSteam(Vector3 pos, float delay)
        {
            EnsureInstance();
            if (_instance._steam.Count < 200) _instance._steam.Add((Time.time + delay, pos));
        }

        public static void Enqueue(Vector3 pos, float delay, float lifetime, float size)
        {
            EnsureInstance();
            if (_instance._queue.Count >= MaxQueued) return;
            _instance._queue.Add((Time.time + delay, pos, lifetime, size));
        }

        private void Update()
        {
            float now = Time.time;
            int hisses = 0;
            for (int i = _steam.Count - 1; i >= 0; i--)
            {
                if (now < _steam[i].at) continue;
                SteamBurst.Spawn(_steam[i].pos);
                if (hisses++ < 3) RetardantSound.Hiss(_steam[i].pos); // a few per frame is plenty to hear
                _steam.RemoveAt(i);
            }
            int spawned = 0;
            for (int i = _queue.Count - 1; i >= 0 && spawned < 16; i--)
            {
                if (now < _queue[i].at) continue;
                // The spawner scales by 1.6-2.2 (the scorch mark's jitter): divide that out.
                ValheimBridge.SpawnRetardantMark(_queue[i].pos, _queue[i].size / 1.9f, _queue[i].lifetime);
                _queue.RemoveAt(i);
                spawned++;
            }
        }
    }

    /// <summary>A one-shot burst of white steam boiling up where the retardant lands on fire.</summary>
    public static class SteamBurst
    {
        public static void Spawn(Vector3 at)
        {
            var go = new GameObject("FireFront_Steam");
            go.transform.position = at + Vector3.up * 0.3f;
            go.transform.rotation = Quaternion.LookRotation(Vector3.up);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1.2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.95f, 0.95f, 0.95f, 0.7f), new Color(0.82f, 0.82f, 0.84f, 0.55f));
            main.gravityModifier = -0.06f; // hot: it rises
            main.maxParticles = 60;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 22), new ParticleSystem.Burst(0.35f, 14), new ParticleSystem.Burst(0.8f, 8) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.9f;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 3.2f)));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0.5f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.5f;
            noise.frequency = 0.4f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            Material mat = FireFrontTextureGenerator.GetOrCreateSmokeMaterial();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ps.Play();
            Object.Destroy(go, 7f);
        }
    }

    /// <summary>
    /// The drop's sound, borrowed from vanilla: the game's own sound-effect prefabs (ZSFX) are
    /// searched once by name, a rushing-water loop for the pour (pitched down for weight) and a
    /// hiss for fire dying. The whole candidate list is logged once, so a better pick can be
    /// named later. Plays through the game's own mixer group, so the SFX volume slider applies.
    /// </summary>
    public static class RetardantSound
    {
        // Chosen from the candidate list a 1.0.21 game logged (2026-11-23): vanilla has no waterfall or
        // steam sound, and the keyword pick took a cinder-rain hit and a lava blob's alert. The Frost
        // King's twirling wind, pitched down, is the rush of a drop; the lava bomb's failed fizzle is
        // fire dying; the water landing is the splash. Keywords stay as the fallback if one is renamed.
        private static readonly string[] RoarKeys = { "sfx_frozenking_frozentwirl_wind", "waterfall", "water_loop", "river", "wind" };
        private static readonly string[] HissKeys = { "sfx_bomblava_fail", "extinguish", "steam", "hiss", "sizzle", "fail" };
        private static readonly string[] SplashKeys = { "sfx_land_water", "splash", "water" };
        private static AudioClip _splashClip;
        private static bool _searched;
        private static AudioClip _roarClip, _hissClip;
        private static UnityEngine.Audio.AudioMixerGroup _mixer;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PrefabsField = typeof(ZNetScene).GetField("m_prefabs", Any);
        private static readonly FieldInfo ClipsField = typeof(ZSFX).GetField("m_audioClips", Any);

        private static void Search()
        {
            if (_searched || ZNetScene.instance == null) return;
            _searched = true;
            try
            {
                var prefabs = PrefabsField?.GetValue(ZNetScene.instance) as List<GameObject>;
                if (prefabs == null) return;
                var found = new List<(string name, AudioClip clip, AudioSource src)>();
                foreach (GameObject p in prefabs)
                {
                    if (p == null) continue;
                    ZSFX sfx = p.GetComponent<ZSFX>();
                    if (sfx == null) continue;
                    var clips = ClipsField?.GetValue(sfx) as AudioClip[];
                    if (clips == null || clips.Length == 0 || clips[0] == null) continue;
                    found.Add((p.name.ToLowerInvariant(), clips[0], p.GetComponent<AudioSource>()));
                }
                var listed = new List<string>();
                foreach (var f in found)
                    if (f.name.Contains("water") || f.name.Contains("splash") || f.name.Contains("rain") || f.name.Contains("steam") ||
                        f.name.Contains("fire") || f.name.Contains("hiss") || f.name.Contains("lava") || f.name.Contains("wind") || f.name.Contains("extinguish"))
                        listed.Add(f.name);
                FireLogger.Info($"[ARROWS] sound candidates ({listed.Count} of {found.Count} sfx): {string.Join(", ", listed)}");

                _roarClip = Pick(found, RoarKeys, out AudioSource roarSrc);
                _hissClip = Pick(found, HissKeys, out AudioSource hissSrc);
                _splashClip = Pick(found, SplashKeys, out _);
                _mixer = roarSrc?.outputAudioMixerGroup ?? hissSrc?.outputAudioMixerGroup;
                FireLogger.Info($"[ARROWS] dousing sound: pour '{(_roarClip != null ? _roarClip.name : "none")}', " +
                                $"hiss '{(_hissClip != null ? _hissClip.name : "none")}', splash '{(_splashClip != null ? _splashClip.name : "none")}', mixer '{(_mixer != null ? _mixer.name : "none")}'.");
            }
            catch (System.Exception ex) { FireLogger.Warn($"[ARROWS] sound search threw: {ex.Message}; the drop is silent."); }
        }

        private static AudioClip Pick(List<(string name, AudioClip clip, AudioSource src)> found, string[] keys, out AudioSource src)
        {
            foreach (string key in keys)
                foreach (var f in found)
                    if (f.name.Contains(key)) { src = f.src; return f.clip; }
            src = null;
            return null;
        }

        private static AudioSource MakeSource(GameObject host, AudioClip clip, float volume, float pitch, float maxDistance)
        {
            var a = host.AddComponent<AudioSource>();
            a.clip = clip;
            a.outputAudioMixerGroup = _mixer;
            a.volume = volume;
            a.pitch = pitch;
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.minDistance = 6f;
            a.maxDistance = maxDistance;
            a.dopplerLevel = 0.3f;
            return a;
        }

        /// <summary>The pour: a deep rushing loop riding on the arrow. Null when no clip was found.</summary>
        public static AudioSource StartRoar(GameObject host)
        {
            Search();
            if (_roarClip == null || host == null) return null;
            AudioSource a = MakeSource(host, _roarClip, 1f, 0.55f, 90f);
            a.loop = true;
            a.Play();
            return a;
        }

        /// <summary>The load hitting the ground where the arrow lands.</summary>
        public static void SplashAt(Vector3 at)
        {
            Search();
            if (_splashClip == null) return;
            var go = new GameObject("FireFront_Splash");
            go.transform.position = at;
            AudioSource a = MakeSource(go, _splashClip, 1f, 0.7f, 70f);
            a.Play();
            Object.Destroy(go, _splashClip.length / 0.7f + 0.2f);
        }

        /// <summary>A hiss where fire dies under the drop.</summary>
        public static void Hiss(Vector3 at)
        {
            Search();
            if (_hissClip == null) return;
            var go = new GameObject("FireFront_Hiss");
            go.transform.position = at;
            AudioSource a = MakeSource(go, _hissClip, 0.9f, Random.Range(0.8f, 1.05f), 50f);
            a.Play();
            Object.Destroy(go, _hissClip.length / Mathf.Max(0.5f, a.pitch) + 0.2f);
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
