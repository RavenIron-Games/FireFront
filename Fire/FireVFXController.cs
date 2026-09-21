using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireFront.Fire
{
    /// <summary>
    /// The fire drawn on a burning tree, log or structure. A FIRE FRONT that starts at the foot
    /// and climbs the trunk as the object's real health drops, so the height of the flames is
    /// the tree's health bar; a band of licking flame behind the front; embers, lit smoke, heat
    /// shimmer, a glow halo and a shadow-casting point light that follow it; and the bark
    /// itself blackening and glowing with ember cracks below the front.
    /// </summary>
    /// <remarks>
    /// Built from vanilla's own fire, not from scratch (read out of the 1.0.15 bundles, see
    /// FireFrontTextureGenerator): the flame is a greyscale flipbook sheet on the game's
    /// gradient-mapped particle shader, recoloured by two HDR colours delivered per particle
    /// through custom vertex streams — the exact construction of fire_pit's "flames (1)". HDR
    /// colours above 1.0 are what make the game's bloom read it as fire; the 0.21.x flame
    /// gradient never left 0..1 and only reached bloom by stacking twelve additive ribbons,
    /// which saturated to white streaks.
    ///
    /// LineRenderer is gone. A ribbon is a camera-facing strip with no internal structure and
    /// no per-element motion; tiling a texture along it and scrolling it reads as a laser, not
    /// a flame, whatever the texture. Flames are vertical-billboard flipbook particles here,
    /// like every fire the game ships.
    ///
    /// Progress comes from the ZDO health that the server's unseen damage ticks drive (see
    /// CharredTreeLifecycle), re-read at 4 Hz on every peer — the float is replicated, so no
    /// extra sync exists for this. Structures, and trees while TreeFireDamageEnabled is off,
    /// climb on the burn clock instead.
    ///
    /// House rules honoured: nothing here is built on a headless server (no graphics device,
    /// nothing to draw, and a LineRenderer rig per burner was being updated there every frame
    /// in 0.21.x); every existing Visuals key is wired (smoke, crown sparks, max flame height,
    /// the tall-fire cap reserved at spawn, smouldering, the low-spec preset); wind is read off
    /// EnvMan directly rather than through per-frame reflection; materials are shared clones.
    /// </remarks>
    public class FireVFXController : MonoBehaviour
    {
        // Geometry
        private Bounds _bounds;
        private float _burnDuration = 60f;
        private float _timeAlive;
        private Component _target;
        private ZDOID _id;
        private BurnKind _kind = BurnKind.Unknown;
        private bool _isLog;
        private float _height = 4f;        // along the trunk axis, metres
        private float _trunkRadius = 0.4f;
        private Vector3 _axis = Vector3.up;
        private Quaternion _axisRot = Quaternion.identity;
        private float _maxHealth;
        private bool _healthDriven;

        // Progress
        private float _rawProgress;
        private float _progress;
        private float _nextHealthPoll;
        private float _frontHeight;

        // Rig
        private bool _built;
        private bool _tallReserved;
        private bool _smoulder;
        private ParticleSystem _front;    // fire-front tongues
        private ParticleSystem _column;   // licks along the burning band
        private ParticleSystem _embers;
        private ParticleSystem _smoke;
        private ParticleSystem _haze;
        private ParticleSystem _glow;
        private Light _light;
        private LightFlicker _flicker;
        private LightLod _lightLod;
        private float _baseLightIntensity = 2.2f;
        private float _baseLightRange = 10f;
        private CharredTreeSkin.CharSlot[] _barkSlots;
        private float _nextBarkUpdate;
        private float _nextDistanceCheck;
        private bool _far;
        private float _cameraDistance; // refreshed with _far, once a second; the bark pass reads it
        private float _noiseSeed;

        private const float CostHeightCeiling = 14f;   // particle budget stops growing past this
        private const float TallBurnerMinHeight = 3f;
        private const float HealthPollInterval = 0.25f;
        private const float BarkUpdateInterval = 0.2f;
        private const float FarDistance = 70f;

        /// <summary>Live tall fires, so their total can be bounded at spawn (never by a per-frame sweep).</summary>
        private static readonly List<FireVFXController> s_tall = new List<FireVFXController>();

        private static readonly List<ParticleSystemVertexStream> s_gradientStreams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.Custom1XYZ,
            ParticleSystemVertexStream.Custom2XYZ,
        };

        // Vanilla fire_pit "flames (1)" CustomData colours: HDR hot/cold pairs the gradient shader maps between.
        private static readonly Gradient s_hot = MakeGradient(new Color(2.0f, 1.22f, 0f), new Color(0.25f, 0.16f, 0f));
        private static readonly Gradient s_cold = MakeGradient(new Color(2.0f, 0f, 0f), new Color(1.0f, 0.30f, 0f));

        public static bool GraphicsAvailable => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>
        /// How much of the ember glow survives the distance to the camera: whole to 30 m, a third
        /// from 90 m, a straight ramp between. The ember mask is a texture, and past its last mip
        /// level the sampler averages it toward its mean; a mean glow spread over a whole trunk is
        /// the neon rod the first in-game run showed, so the glow is faded out over the range in
        /// which the cracks stop resolving. The one source of truth for the live burn's bark pass
        /// and the charred twin's CurrentEmber alike: review of PR #3 caught the two disagreeing
        /// (a step to 30 % at 70 m against a ramp over 30 to 90 m), which made a tree that went
        /// from burning to charred at 50 m jump in brightness.
        /// </summary>
        public static float EmberDistanceFactor(float distance) => Mathf.Lerp(1f, EmberFarFloor, Mathf.Clamp01((distance - EmberFadeStart) / (EmberFadeEnd - EmberFadeStart)));

        private const float EmberFadeStart = 30f;
        private const float EmberFadeEnd = 90f;
        private const float EmberFarFloor = 0.3f;

        /// <summary>
        /// Distance from the main camera to <paramref name="position"/>. False when there is no
        /// camera (loading, or a headless peer), and each caller then keeps whatever it had:
        /// nothing is drawn without a camera, so no fade is the right answer there.
        /// </summary>
        public static bool TryDistanceToMainCamera(Vector3 position, out float distance)
        {
            Camera cam = global::Utils.GetMainCamera();
            if (cam == null) { distance = 0f; return false; }
            distance = Vector3.Distance(cam.transform.position, position);
            return true;
        }

        public float Progress => _progress;
        public float FrontHeight => _frontHeight;

        /// <summary>The burner this rig was built on has been destroyed or de-instantiated on this peer.</summary>
        public bool TargetLost => _hadTarget && _target == null;
        private bool _hadTarget;

        // ---------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------

        public void Setup(Bounds bounds, float burnDuration, Component target = null) => Setup(bounds, burnDuration, target, default);

        public void Setup(Bounds bounds, float burnDuration, Component target, ZDOID id, float initialAge = 0f)
        {
            _bounds = bounds;
            _burnDuration = Mathf.Max(1f, burnDuration);
            _timeAlive = Mathf.Max(0f, initialAge); // a late joiner's rig starts as old as the fire is
            _target = target;
            _hadTarget = target != null;
            _id = id;
            _kind = target != null ? ValheimBridge.KindOf(target) : BurnKind.Unknown;
            _noiseSeed = Random.Range(0f, 100f);

            MeasureGeometry(target, bounds);
            ResolveHealthSource();

            // Start the front where the health already is: a re-ignited, half-burned tree does
            // not restart at the foot.
            _rawProgress = SampleProgress();
            _progress = _rawProgress;
            _frontHeight = FrontHeightFor(_progress);

            if (!GraphicsAvailable) return; // headless: state only, nothing to draw
            if (!FireConfig.UseProceduralVfx.Value) return;

            try
            {
                Build();
                _built = true;
            }
            catch (System.Exception ex)
            {
                // Cosmetics stay off the gameplay path: a rig that fails to build is a fire
                // you cannot see, not a fire that stops burning.
                FireLogger.Warn($"[VFX] fire rig failed to build for {(target != null ? target.name : "?")}: {ex.Message}");
            }
        }

        private void MeasureGeometry(Component target, Bounds bounds)
        {
            _isLog = _kind == BurnKind.Log;
            if (_isLog && target != null)
            {
                // A TreeLog's long axis is its LOCAL +Y (TreeLog scatters drops along transform.up).
                Vector3 axis = target.transform.up;
                if (axis.sqrMagnitude < 0.01f) axis = Vector3.forward;
                _axis = axis.normalized;
                float along = Mathf.Abs(Vector3.Dot(bounds.size, _axis));
                _height = Mathf.Clamp(Mathf.Max(along, Mathf.Max(bounds.size.x, bounds.size.z)), 2f, 12f);
                _trunkRadius = Mathf.Clamp(bounds.size.y * 0.35f, 0.25f, 0.6f);
            }
            else
            {
                _axis = Vector3.up;
                float h = Mathf.Max(1.5f, bounds.size.y);
                if (_kind == BurnKind.Tree && FireConfig.TreeFlameScaling.Value)
                    _height = Mathf.Min(h, FireConfig.EffectiveMaxFlameHeight);
                else if (_kind == BurnKind.Tree)
                    _height = Mathf.Min(h, TallBurnerMinHeight);
                else
                    _height = Mathf.Min(h, 8f);
                // The crown radius that arrives in bounds is canopy reach; the trunk is a small
                // fraction of it. Structures get their footprint instead.
                _trunkRadius = _kind == BurnKind.Tree
                    ? Mathf.Clamp(bounds.extents.x * 0.14f, 0.28f, 0.6f)
                    : Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.6f, 0.3f, 1.6f);
            }
            _axisRot = Quaternion.LookRotation(_axis, Mathf.Abs(Vector3.Dot(_axis, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
        }

        private void ResolveHealthSource()
        {
            _healthDriven = false;
            _maxHealth = 0f;
            if (!FireConfig.TreeFireDamageEnabled.Value) return;
            if (_kind != BurnKind.Tree && _kind != BurnKind.Log) return;
            if (_id == default && _target != null)
            {
                ZDOID? maybe = ValheimBridge.ZDOIDOf(_target);
                if (maybe.HasValue) _id = maybe.Value;
            }
            ZDO zdo = CurrentZdo();
            if (zdo == null) return;
            _maxHealth = CharredTreeLifecycle.MaxHealthOf(zdo);
            _healthDriven = _maxHealth > 0f;
        }

        /// <summary>Re-fetched by id every time: ZDOs are pooled and a cached reference can come back as another object.</summary>
        private ZDO CurrentZdo()
        {
            if (_id == default || ZDOMan.instance == null) return null;
            return ZDOMan.instance.GetZDO(_id);
        }

        private float SampleProgress()
        {
            if (_healthDriven)
            {
                ZDO zdo = CurrentZdo();
                if (zdo != null) return CharredTreeLifecycle.BurntFraction(zdo, _maxHealth);
            }
            return Mathf.Clamp01(_timeAlive / _burnDuration);
        }

        private float FrontHeightFor(float progress)
        {
            // Slightly eased so the front spends a little longer low, where the fire looks
            // like it is taking hold, and reaches the crown only at death.
            return Mathf.Lerp(0.6f, _height, Mathf.Pow(Mathf.Clamp01(progress), 0.9f));
        }

        // ---------------------------------------------------------------
        // Rig construction
        // ---------------------------------------------------------------

        private static Gradient MakeGradient(Color a, Color b)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        private static Gradient AlphaGradient(Color tint, params (float t, float a)[] keys)
        {
            var g = new Gradient();
            var ak = new GradientAlphaKey[keys.Length];
            for (int i = 0; i < keys.Length; i++) ak[i] = new GradientAlphaKey(keys[i].a, keys[i].t);
            g.SetKeys(new[] { new GradientColorKey(tint, 0f), new GradientColorKey(tint, 1f) }, ak);
            return g;
        }

        private static AnimationCurve Curve(params (float t, float v)[] keys)
        {
            var c = new AnimationCurve();
            for (int i = 0; i < keys.Length; i++) c.AddKey(new Keyframe(keys[i].t, keys[i].v));
            return c;
        }

        private ParticleSystem NewSystem(string name, Material mat, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.rotation = _axisRot;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = true;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        private void Build()
        {
            float cost = Mathf.Clamp(_height, 2f, CostHeightCeiling) / CostHeightCeiling; // 0.14..1
            bool tall = _kind == BurnKind.Tree && _height >= TallBurnerMinHeight && FireConfig.TreeFlameScaling.Value;
            if (tall) _tallReserved = TryReserveTall();
            bool full = !tall || _tallReserved;   // past the tall cap: the ordinary small fire
            if (tall && !_tallReserved)
            {
                // The small fire keeps a short front, or its handful of tongues would be spread
                // over thirty metres of trunk and read as nothing at all.
                _height = TallBurnerMinHeight;
                _frontHeight = FrontHeightFor(_progress);
            }

            Material flameMat = FireFrontTextureGenerator.GetOrCreateFlameMaterial();
            bool gradient = FireFrontTextureGenerator.FlameMaterialIsGradientMapped();

            _front = BuildFlames("FireFront", flameMat, gradient, cost, front: true);
            if (full) _column = BuildFlames("FlameColumn", flameMat, gradient, cost, front: false);
            if (FireConfig.EffectiveCrownSparksEnabled || _kind != BurnKind.Tree) _embers = BuildEmbers(cost);
            if (FireConfig.FireSmokeEnabled.Value) _smoke = BuildSmoke(cost);
            if (FireConfig.EffectiveHeatHazeEnabled && full) _haze = BuildHaze();
            _glow = BuildGlow();
            BuildLight(full);

            if (FireConfig.EffectiveBarkCharEnabled && _target != null && (_kind == BurnKind.Tree || _kind == BurnKind.Log))
            {
                _barkSlots = CharredTreeSkin.CollectCharSlots(_target.gameObject);
            }

            PositionEmitters();
            _front?.Play();
            _column?.Play();
            _embers?.Play();
            _smoke?.Play();
            _haze?.Play();
            _glow?.Play();
        }

        private ParticleSystem BuildFlames(string name, Material mat, bool gradient, float cost, bool front)
        {
            ParticleSystem ps = NewSystem(name, mat, ParticleSystemRenderMode.VerticalBillboard);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.pivot = new Vector3(0f, 0.4f, 0f);          // base-anchored tongues, like the Ashlands ground fire
            r.sortMode = ParticleSystemSortMode.OldestInFront;
            r.maxParticleSize = 0.5f;
            r.alignment = ParticleSystemRenderSpace.View;
            if (gradient) r.SetActiveVertexStreams(s_gradientStreams);

            var main = ps.main;
            float scale = Mathf.Clamp(_trunkRadius / 0.4f, 0.7f, 1.6f);
            main.startLifetime = front ? new ParticleSystem.MinMaxCurve(0.55f, 0.9f) : new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
            // No shape-directed speed: the emitter's cone runs ALONG the trunk (so the band of
            // fire lies on a fallen log's length), but flame always rises, so the rise is a
            // world-space velocity set below, the same for a standing tree and a log.
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.9f * scale, 1.4f * scale);
            main.startSizeY = front ? new ParticleSystem.MinMaxCurve(1.6f * scale, 3.0f * scale) : new ParticleSystem.MinMaxCurve(0.7f * scale, 1.4f * scale);
            main.startSizeZ = new ParticleSystem.MinMaxCurve(1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            // Per-particle brightness variance, exactly vanilla's grey 0.625..1 start colour.
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.625f, 0.625f, 0.625f, 1f), Color.white);
            main.maxParticles = front ? Mathf.RoundToInt(Mathf.Lerp(90f, 260f, cost)) : Mathf.RoundToInt(Mathf.Lerp(60f, 200f, cost));
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.rateOverTime = front ? Mathf.Lerp(30f, 55f, cost) * scale : 20f;

            var shape = ps.shape;
            if (front)
            {
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.radius = _trunkRadius + 0.05f;
                shape.radiusThickness = 0f;      // on the bark, not inside the trunk
                shape.angle = 8f;
                shape.length = 0.3f;
            }
            else
            {
                shape.shapeType = ParticleSystemShapeType.ConeVolume;
                shape.radius = _trunkRadius + 0.03f;
                shape.radiusThickness = 0f;
                shape.angle = 0f;
                shape.length = 1f;               // stretched to the burning band every update
            }
            shape.rotation = Vector3.zero;        // the emitter transform already points along the trunk

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(AlphaGradient(Color.white, (0f, 0f), (0.135f, 1f), (0.28f, 0.86f), (1f, 0f)));

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, Curve((0f, 1f), (1f, 0.28f)));

            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            tsa.mode = ParticleSystemAnimationMode.Grid;
            tsa.animation = ParticleSystemAnimationType.WholeSheet;
            CopyFlipbookGrid(tsa);
            tsa.timeMode = ParticleSystemAnimationTimeMode.FPS;
            tsa.fps = 30f;
            tsa.startFrame = new ParticleSystem.MinMaxCurve(0f, 0.999f);
            tsa.cycleCount = 1;

            if (gradient)
            {
                var custom = ps.customData;
                custom.enabled = true;
                custom.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Color);
                custom.SetColor(ParticleSystemCustomData.Custom1, new ParticleSystem.MinMaxGradient(s_hot));
                custom.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
                custom.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(s_cold));
            }
            else
            {
                // Procedural additive fallback: the flipbook is white, so the tint is the colour.
                col.color = new ParticleSystem.MinMaxGradient(FallbackFlameGradient());
            }

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.18f;
            noise.frequency = 1.5f;
            noise.scrollSpeed = 0.3f;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.y = front ? new ParticleSystem.MinMaxCurve(1.0f, 3.2f) : new ParticleSystem.MinMaxCurve(0.6f, 1.8f);
            // Unity insists x, y and z share one curve mode and logs "Particle Velocity curves
            // must all be in the same mode" EVERY FRAME a system simulates when they do not:
            // 80k lines in eight minutes on the first play test, each one a BepInEx disk write.
            // y is a random lick between two constants, so x and z are two-constant zeros, and
            // SetVelocity keeps whatever mode a system was built with when the wind moves them.
            vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            return ps;
        }

        private static Gradient FallbackFlameGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.85f, 0.35f), 0f), new GradientColorKey(new Color(1f, 0.45f, 0.05f), 0.4f), new GradientColorKey(new Color(0.7f, 0.1f, 0.01f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        /// <summary>Grid size from the donor sheet when we have one (8x8 for fire_pit, 16x8 for the Ashlands tongues); 8x8 otherwise.</summary>
        private static void CopyFlipbookGrid(ParticleSystem.TextureSheetAnimationModule tsa)
        {
            ParticleSystem donor = FireFrontTextureGenerator.FindDonorSystem("flame");
            if (donor != null && donor.textureSheetAnimation.enabled)
            {
                tsa.numTilesX = Mathf.Max(1, donor.textureSheetAnimation.numTilesX);
                tsa.numTilesY = Mathf.Max(1, donor.textureSheetAnimation.numTilesY);
                return;
            }
            tsa.numTilesX = 8;
            tsa.numTilesY = 8;
        }

        private ParticleSystem BuildEmbers(float cost)
        {
            ParticleSystem ps = NewSystem("Sparks", FireFrontTextureGenerator.GetOrCreateEmberMaterial(), ParticleSystemRenderMode.Billboard);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sortMode = ParticleSystemSortMode.Distance;
            r.maxParticleSize = 0.5f;

            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);     // motes, never rectangles
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.77f, 0f, 1f), Color.white);
            main.maxParticles = Mathf.RoundToInt(Mathf.Lerp(40f, 120f, cost));
            main.gravityModifier = 0.01f;

            var emission = ps.emission;
            emission.rateOverTime = Mathf.Lerp(5f, 14f, cost);

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.radius = _trunkRadius + 0.1f;
            shape.angle = 20f;
            shape.length = 0.2f;
            shape.rotation = Vector3.zero;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.8f, 0f), 0.5f), new GradientColorKey(new Color(1f, 0.19f, 0f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.03f), new GradientAlphaKey(0.97f, 0.47f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, Curve((0f, 1f), (1f, 0f)));

            // Buoyancy, not a rocket: a little lift, thermal turbulence, drag so nothing streaks.
            var force = ps.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.World;
            force.y = new ParticleSystem.MinMaxCurve(0.2f, 0.4f);
            // Same one-mode rule as velocity: x and z in y's two-constant mode.
            force.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            force.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.5f;
            noise.frequency = 1f;
            noise.scrollSpeed = 0.1f;
            noise.octaveCount = 1;
            noise.damping = true;
            noise.positionAmount = 0.3f;
            noise.quality = ParticleSystemNoiseQuality.High;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag = 0.3f;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            return ps;
        }

        private ParticleSystem BuildSmoke(float cost)
        {
            ParticleSystem ps = NewSystem("Smoke", FireFrontTextureGenerator.GetOrCreateSmokeMaterial(), ParticleSystemRenderMode.Billboard);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sortMode = ParticleSystemSortMode.OldestInFront;
            r.maxParticleSize = 10f;
            r.sortingFudge = 1f;
            r.receiveShadows = true;   // Lux Lit Particles: shadowed by the sun, lit by our point light

            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Lerp(1.6f, 3.2f, cost));
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.10f, 0.10f, 0.10f, 1f), new Color(0.47f, 0.47f, 0.47f, 1f));
            main.maxParticles = Mathf.RoundToInt(Mathf.Lerp(24f, 60f, cost));
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.rateOverTime = Mathf.Lerp(3f, 7f, cost);

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.ConeVolume;
            shape.radius = Mathf.Max(0.6f, _trunkRadius * 1.4f);
            shape.length = 1.2f;
            shape.angle = 10f;
            shape.rotation = Vector3.zero;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, Curve((0f, 0.03f), (0.4f, 0.7f), (1f, 1f)));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.64f, 0f), 0f), new GradientColorKey(Color.white, 0.3f), new GradientColorKey(Color.white, 1f) }, // fire-tinted at birth
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.22f), new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);

            var force = ps.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.World;
            force.y = new ParticleSystem.MinMaxCurve(0.1f, 0.5f);
            // Same one-mode rule as velocity: x and z in y's two-constant mode.
            force.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            force.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            return ps;
        }

        private ParticleSystem BuildHaze()
        {
            Material haze = FireFrontTextureGenerator.GetOrCreateHazeMaterial();
            if (haze == null) return null; // no donor, no haze — never a fallback that draws a grey quad
            ParticleSystem ps = NewSystem("HeatHaze", haze, ParticleSystemRenderMode.Billboard);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sortMode = ParticleSystemSortMode.Distance;

            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3f);
            main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
            main.maxParticles = 8;       // GrabPass refraction: keep the count tiny
            main.gravityModifier = -0.05f;

            var emission = ps.emission;
            emission.rateOverTime = 2.5f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.5f;

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.09f, 0.09f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(AlphaGradient(Color.white, (0f, 0f), (0.4f, 1f), (1f, 0f)));

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            return ps;
        }

        /// <summary>Vanilla's cheap halo trick (fire_pit "flare"): one long-lived billboard the bloom smears into a glow.</summary>
        private ParticleSystem BuildGlow()
        {
            ParticleSystem ps = NewSystem("Glow", FireFrontTextureGenerator.GetOrCreateGlowMaterial(), ParticleSystemRenderMode.Billboard);
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local; // rides the emitter to the front
            main.startLifetime = new ParticleSystem.MinMaxCurve(1e6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Clamp(_trunkRadius * 5f, 1.6f, 3.5f));
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1.6f, 0.7f, 0.25f, 0.35f));
            main.maxParticles = 1;
            main.loop = false;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

            var shape = ps.shape;
            shape.enabled = false;
            return ps;
        }

        private void BuildLight(bool full)
        {
            var go = new GameObject("FirePointLight");
            go.transform.SetParent(transform, false);
            _light = go.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.504f, 0.324f);            // vanilla fire_pit
            _baseLightIntensity = full ? 2.2f + Mathf.Min(_height, CostHeightCeiling) * 0.06f : 2f;
            _baseLightRange = full ? Mathf.Clamp(10f + _height * 0.35f, 10f, 16f) : 8f;
            _light.intensity = _baseLightIntensity;
            _light.range = _baseLightRange;
            _light.renderMode = LightRenderMode.ForcePixel;           // so the lit smoke gets it per pixel
            _light.cullingMask = ~0;
            if (FireConfig.EffectiveFireShadowsEnabled)
            {
                // Set BEFORE LightLod's Awake: it disables shadow LOD for a light that has none.
                _light.shadows = LightShadows.Soft;
                _light.shadowResolution = LightShadowResolution.Medium;
                _light.shadowBias = 0.05f;
                _light.shadowNormalBias = 0.4f;
                _light.shadowStrength = 1f;
            }
            else
            {
                _light.shadows = LightShadows.None;
            }

            // Vanilla's own light management: fades the light in within 40 m, the shadows within
            // 20 m, and keeps the number of shadowed point lights inside the player's setting.
            try
            {
                _lightLod = go.AddComponent<LightLod>();
                _lightLod.m_lightDistance = 40f;
                _lightLod.m_shadowDistance = 20f;
                _flicker = go.AddComponent<LightFlicker>();
                _flicker.m_flickerIntensity = 0.12f;
                _flicker.m_flickerSpeed = 10f;
                _flicker.m_movement = 0.1f;
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[VFX] vanilla light helpers unavailable: {ex.Message}");
            }
        }

        private bool TryReserveTall()
        {
            for (int i = s_tall.Count - 1; i >= 0; i--)
            {
                if (s_tall[i] == null) s_tall.RemoveAt(i);
            }
            if (s_tall.Count >= FireConfig.EffectiveTallFireMaxConcurrent) return false;
            s_tall.Add(this);
            return true;
        }

        // ---------------------------------------------------------------
        // Per frame
        // ---------------------------------------------------------------

        private void Update()
        {
            float dt = Time.deltaTime;
            _timeAlive += dt;

            if (Time.time >= _nextHealthPoll)
            {
                _nextHealthPoll = Time.time + HealthPollInterval;
                _rawProgress = SampleProgress();
            }
            // Ease toward the sampled value: a tick every couple of seconds must read as a
            // fire climbing, not as flames jumping a rung.
            _progress = Mathf.MoveTowards(_progress, _rawProgress, dt * 0.12f);
            _frontHeight = FrontHeightFor(_progress);

            if (!_built) return;

            if (Time.time >= _nextDistanceCheck)
            {
                _nextDistanceCheck = Time.time + 1f;
                UpdateDistanceLod();
            }

            Vector3 wind = EnvMan.instance != null ? EnvMan.instance.GetWindForce() : Vector3.zero;
            wind.y = 0f;
            float gust = 1f + 0.35f * (Mathf.PerlinNoise(Time.time * 1.7f, _noiseSeed) - 0.5f) * 2f;
            Vector3 windVel = wind * gust;

            PositionEmitters();
            ApplyWind(windVel);
            UpdateRates();
            UpdateLight(windVel);

            if (_barkSlots != null && Time.time >= _nextBarkUpdate)
            {
                _nextBarkUpdate = Time.time + BarkUpdateInterval;
                float pulse = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 2.5f, _noiseSeed);
                if (_smoulder) pulse *= 0.5f; // a smoulder's cracks are dimmer at any distance; this stays on top of the ramp
                // Past the mask's last mip the glow averages over the whole trunk, so it fades with
                // distance on the same ramp the charred twin uses (EmberDistanceFactor holds the
                // why); the flames carry the fire from there. The distance is up to a second stale,
                // which a ramp over 60 m cannot show. _far keeps its step: it is the emitter and
                // smoke cost LOD, not a look.
                pulse *= EmberDistanceFactor(_cameraDistance);
                CharredTreeSkin.ApplyBurnChar(_barkSlots, _progress, pulse, CharredTextures.VariantFor(_id));
            }
        }

        private Vector3 AlongTrunk(float dist) => transform.position + _axis * dist;

        private void PositionEmitters()
        {
            float front = _frontHeight;
            if (_front != null) _front.transform.position = AlongTrunk(Mathf.Max(0f, front - 0.3f));
            if (_column != null)
            {
                // The burning band runs from the foot to the front; the emitter sits at the
                // foot and its cone volume is stretched to the front.
                _column.transform.position = AlongTrunk(0.1f);
                var shape = _column.shape;
                shape.length = Mathf.Max(0.3f, front - 0.2f);
            }
            if (_embers != null) _embers.transform.position = AlongTrunk(front * 0.85f);
            if (_smoke != null) _smoke.transform.position = AlongTrunk(front + 0.5f);
            if (_haze != null) _haze.transform.position = AlongTrunk(front + 1f);
            if (_glow != null) _glow.transform.position = AlongTrunk(Mathf.Max(0.5f, front * 0.7f));
        }

        private void ApplyWind(Vector3 windVel)
        {
            SetVelocity(_front, windVel * 0.6f, 0f);
            SetVelocity(_column, windVel * 0.35f, 0f);
            SetVelocity(_embers, windVel * 0.9f, 0f);
            SetVelocity(_smoke, windVel * 2.0f, 0f);
            SetVelocity(_haze, windVel * 1.2f, 0f);
        }

        private static void SetVelocity(ParticleSystem ps, Vector3 v, float y)
        {
            if (ps == null) return;
            var vel = ps.velocityOverLifetime;
            // A bare float converts to a Constant curve, which broke the one-mode rule on the
            // flame systems (their y is two constants) every time the wind changed. Write x and
            // z in whatever mode y already has; a two-constant pair with equal ends is a constant.
            bool two = vel.y.mode == ParticleSystemCurveMode.TwoConstants;
            vel.x = two ? new ParticleSystem.MinMaxCurve(v.x, v.x) : new ParticleSystem.MinMaxCurve(v.x);
            vel.z = two ? new ParticleSystem.MinMaxCurve(v.z, v.z) : new ParticleSystem.MinMaxCurve(v.z);
            if (y != 0f) vel.y = two ? new ParticleSystem.MinMaxCurve(y, y) : new ParticleSystem.MinMaxCurve(y);
        }

        private void UpdateRates()
        {
            // The band behind the front burns hardest early and settles to embers as the wood
            // chars; the front itself is fiercest in the middle of the burn.
            float bandFactor = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((_progress - 0.5f) / 0.5f));
            float frontFactor = 0.8f + 0.4f * Mathf.Sin(_progress * Mathf.PI);
            if (_smoulder) { bandFactor *= 0.3f; frontFactor *= 0.4f; }
            if (_column != null)
            {
                var e = _column.emission;
                float perMetre = _far ? 0f : 22f;
                e.rateOverTime = Mathf.Min(150f, perMetre * Mathf.Max(0.3f, _frontHeight) * bandFactor);
            }
            if (_front != null)
            {
                var e = _front.emission;
                float baseRate = Mathf.Lerp(30f, 55f, Mathf.Clamp01(_height / CostHeightCeiling)) * Mathf.Clamp(_trunkRadius / 0.4f, 0.7f, 1.6f);
                e.rateOverTime = baseRate * frontFactor;
            }
        }

        private void UpdateLight(Vector3 windVel)
        {
            if (_light == null) return;
            float lean = Mathf.Clamp01(_frontHeight / Mathf.Max(1f, _height)) * 0.4f;
            _light.transform.position = AlongTrunk(Mathf.Max(0.5f, _frontHeight - 0.5f)) + windVel * lean;
            // LightFlicker owns the fast flicker; this is the slow swell with the burn.
            float swell = _smoulder ? 0.45f : Mathf.Lerp(0.8f, 1.15f, Mathf.Sin(_progress * Mathf.PI));
            float target = _baseLightIntensity * swell;
            // LightFlicker rewrites intensity from its captured base every update; nudging the
            // base through the component keeps the two from fighting. That base is a PRIVATE
            // field in the shipping assembly - it only looks public through the publicized
            // reference - so the write goes through the bridge. False means the field could not
            // be resolved; a plain intensity write is then the best available.
            if (!ValheimBridge.TrySetFlickerBaseIntensity(_flicker, target)) _light.intensity = target;
        }

        private void UpdateDistanceLod()
        {
            if (!TryDistanceToMainCamera(transform.position, out float d)) return;
            _cameraDistance = d;
            bool far = d > FarDistance;
            if (far == _far) return;
            _far = far;
            // Far away, the fire is its front, its glow and its light; the rest is not visible
            // at that size and only costs.
            if (_haze != null) { var e = _haze.emission; e.enabled = !far; }
            if (_embers != null) { var e = _embers.emission; e.enabled = !far; }
            if (_smoke != null) { var e = _smoke.emission; e.rateOverTime = far ? 1.5f : Mathf.Lerp(3f, 7f, Mathf.Clamp01(_height / CostHeightCeiling)); }
        }

        // ---------------------------------------------------------------
        // Smoulder / teardown
        // ---------------------------------------------------------------

        /// <summary>
        /// Long-burning fires drop to embers and smoke. Cosmetic only; the front keeps climbing
        /// with the health. Called by ValheimBridge.DowngradeVfxToSmoulder.
        /// </summary>
        public void SetSmoulder()
        {
            if (_smoulder) return;
            _smoulder = true;
            if (_haze != null) { var e = _haze.emission; e.enabled = false; }
            if (_glow != null)
            {
                var main = _glow.main;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1.2f, 0.35f, 0.1f, 0.25f));
                _glow.Clear();
                _glow.Play();
            }
            if (_light != null)
            {
                _light.color = new Color(1f, 0.35f, 0.10f);
                float range = _baseLightRange * 0.6f;
                // LightLod ramps the range back up to ITS captured base every second, so the base
                // has to drop - through the bridge, because it is private at runtime. But the base
                // ALONE changes nothing while the player is inside m_lightDistance: LightLod's
                // ramp-up only runs while range < base, and its ramp-down only runs beyond that
                // distance. Both writes, or the light keeps its full range indefinitely.
                ValheimBridge.TrySetLightLodBaseRange(_lightLod, range);
                _light.range = range;
                // Embers do not cast a soft shadow, and a shadow-casting point light is the single
                // most expensive thing a fire draws. m_shadowLod is public and short-circuits
                // LightLod's whole shadow block; without it LightLod restores Soft within a second
                // inside m_shadowDistance. Shedding it also frees a slot in the player's global
                // shadowed-point-light budget for a fire that is still actually burning.
                if (_lightLod != null) _lightLod.m_shadowLod = false;
                _light.shadows = LightShadows.None;
                _light.shadowStrength = 0f;
            }
            if (_embers != null) { var e = _embers.emission; e.rateOverTime = 2f; }
            UpdateRates();
        }

        private void OnDestroy()
        {
            if (_barkSlots != null)
            {
                try { CharredTreeSkin.ClearBurnChar(_barkSlots); } catch (System.Exception) { }
                _barkSlots = null;
            }
            s_tall.Remove(this);
        }
    }
}
