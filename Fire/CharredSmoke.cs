using FireFront.Config;
using FireFront.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireFront.Fire
{
    /// <summary>
    /// The thin smoke a charred trunk or fallen charred log gives off after the fire is out:
    /// a few wisps rising from points along the wood, thinning to nothing over
    /// CharredSmokeSeconds. Purely local — every peer computes the same age from the ZDO's
    /// CharredAt, so a late joiner sees the right stage without a message.
    /// </summary>
    /// <remarks>
    /// Built from the same cloned vanilla smoke material the fire uses (bonfire/Smoke, Lux
    /// lit particles, so the sun shadows it and it reads as smoke, not a sprite), at a rate
    /// two orders of magnitude below a burning tree's smoke. Emitters sit on the wood surface,
    /// not in the trunk centre, so the wisps come off the bark. Capped world-wide so a burnt
    /// forest is a few dozen smoking snags, not hundreds of particle systems.
    /// </remarks>
    public class CharredSmoke : MonoBehaviour
    {
        private const float TickInterval = 0.5f;
        private const int MaxSmokingObjects = 32;
        private const float FarDistance = 80f;   // beyond this the wisps are sub-pixel; stop paying for them
        private static int s_active;

        private ParticleSystem[] _systems;
        private float _seconds;
        private float _nextTick;
        private float _noiseSeed;
        private bool _stopped;
        private bool _counted;

        public static int ActiveCount => s_active;

        /// <summary>
        /// Attaches smoke to a charred object if the switch, the cap and the age allow it.
        /// Returns null when nothing was attached.
        /// </summary>
        public static CharredSmoke TryAttach(GameObject root, bool isLog, float ageSeconds)
        {
            if (root == null || !FireVFXController.GraphicsAvailable) return null;
            if (!FireConfig.EffectiveCharredSmokeEnabled) return null;
            float seconds = FireConfig.CharredSmokeSeconds.Value;
            if (seconds <= 0f || ageSeconds >= seconds) return null;
            if (s_active >= MaxSmokingObjects) return null;
            CharredSmoke existing = root.GetComponent<CharredSmoke>();
            if (existing != null) return existing;

            CharredSmoke cs = root.AddComponent<CharredSmoke>();
            try
            {
                cs.Build(isLog, seconds);
                s_active++;
                cs._counted = true;
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[CHARRED] smoke build on {root.name} threw: {ex.Message}");
                Object.Destroy(cs);
                return null;
            }
            return cs;
        }

        private void Build(bool isLog, float seconds)
        {
            _seconds = seconds;
            _noiseSeed = Random.Range(0f, 100f);

            // Geometry from the mesh renderers: the crown is gone, so the bounds are the wood.
            Bounds b = new Bounds(transform.position, Vector3.one);
            bool any = false;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!(renderers[i] is MeshRenderer) || !renderers[i].enabled) continue;
                if (!any) { b = renderers[i].bounds; any = true; } else b.Encapsulate(renderers[i].bounds);
            }
            if (!any) b = new Bounds(transform.position + Vector3.up * 2f, new Vector3(1f, 4f, 1f));

            Material mat = FireFrontTextureGenerator.GetOrCreateSmokeMaterial();
            Vector3[] points; Vector3 up = Vector3.up; float radius;
            if (isLog)
            {
                // A log's mesh runs along its local up; smoke comes off the top of it at three points.
                Vector3 axis = transform.up;
                float half = Mathf.Max(0.5f, Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 0.8f);
                // Same estimate the fire rig uses for a log (FireVFXController.MeasureGeometry).
                radius = Mathf.Clamp(b.size.y * 0.35f, 0.2f, 0.6f);
                Vector3 c = b.center;
                points = new[] { c - axis * half * 0.6f, c, c + axis * half * 0.6f };
                for (int i = 0; i < points.Length; i++) points[i] += Vector3.up * radius * 0.6f;
            }
            else
            {
                float height = Mathf.Max(2f, b.size.y);
                // The bounds are branch reach even without leaves; the trunk is a small fraction of
                // it (same ratio as FireVFXController.MeasureGeometry).
                radius = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z) * 0.14f, 0.25f, 0.6f);
                float baseY = b.min.y;
                Vector3 foot = new Vector3(transform.position.x, baseY, transform.position.z);
                // Lower two thirds of the trunk: where the char is thickest and the wood still holds heat.
                points = height > 6f
                    ? new[] { foot + Vector3.up * height * 0.12f, foot + Vector3.up * height * 0.34f, foot + Vector3.up * height * 0.58f }
                    : new[] { foot + Vector3.up * height * 0.2f, foot + Vector3.up * height * 0.55f };
            }

            _systems = new ParticleSystem[points.Length];
            for (int i = 0; i < points.Length; i++) _systems[i] = BuildWisp("CharredSmoke" + i, mat, points[i], up, radius, i);
        }

        private ParticleSystem BuildWisp(string name, Material mat, Vector3 pos, Vector3 up, float radius, int index)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(up);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.36f, 0.36f, 0.36f, 0.5f), new Color(0.58f, 0.58f, 0.58f, 0.38f));
            main.maxParticles = 24;
            main.gravityModifier = -0.015f; // warm air: a faint lift beyond the velocity below

            var emission = ps.emission;
            emission.rateOverTime = BaseRate(index);

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.radius = radius;
            shape.radiusThickness = 0.15f;   // on the bark, just under the surface
            shape.angle = 6f;
            shape.length = 0.1f;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sc = new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.35f, 1f), new Keyframe(1f, 1.6f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sc);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.7f, 0.18f), new GradientAlphaKey(0.3f, 0.65f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.22f;
            noise.frequency = 0.55f;
            noise.scrollSpeed = 0.15f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.y = new ParticleSystem.MinMaxCurve(0.28f, 0.55f);
            // x, y and z must share one curve mode or Unity logs an error every frame (see
            // FireVFXController.BuildFlames); the wind update below keeps them two-constant.
            vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            if (mat != null) r.sharedMaterial = mat;
            r.sortMode = ParticleSystemSortMode.OldestInFront;
            r.maxParticleSize = 4f;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            ps.Play();
            return ps;
        }

        /// <summary>Called by the controller with the object's charred age (world time).</summary>
        public void Tick(float ageSeconds)
        {
            if (_stopped || _systems == null) return;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;

            if (ageSeconds >= _seconds || !FireConfig.EffectiveCharredSmokeEnabled)
            {
                StopSmoking();
                return;
            }
            // Full rate for the first two thirds, then a straight taper to nothing; nothing at
            // all beyond FarDistance (the fire rig's own far gate is 70 m).
            float t = Mathf.Clamp01(ageSeconds / _seconds);
            float taper = t < 0.66f ? 1f : 1f - (t - 0.66f) / 0.34f;
            Camera cam = global::Utils.GetMainCamera();
            if (cam != null && (cam.transform.position - transform.position).sqrMagnitude > FarDistance * FarDistance) taper = 0f;

            Vector3 wind = EnvMan.instance != null ? EnvMan.instance.GetWindForce() : Vector3.zero;
            wind.y = 0f;
            float gust = 1f + 0.3f * (Mathf.PerlinNoise(Time.time * 0.8f, _noiseSeed) - 0.5f) * 2f;
            Vector3 windVel = wind * (1.6f * gust);

            for (int i = 0; i < _systems.Length; i++)
            {
                ParticleSystem ps = _systems[i];
                if (ps == null) continue;
                var e = ps.emission;
                e.rateOverTime = BaseRate(i) * taper;
                var v = ps.velocityOverLifetime;
                v.x = new ParticleSystem.MinMaxCurve(windVel.x, windVel.x);
                v.z = new ParticleSystem.MinMaxCurve(windVel.z, windVel.z);
            }
        }

        /// <summary>
        /// Wisps per second from emitter <paramref name="index"/>: about a third of vanilla's
        /// dormant campfire smoke (3/s) spread over the points, uneven so one spot smokes a
        /// little more than the others. A whole tree is ~2.5/s; a burning tree's rig is 3-7/s
        /// at three times the particle size.
        /// </summary>
        private static float BaseRate(int index) => 0.6f + 0.3f * index;

        private void StopSmoking()
        {
            if (_stopped) return;
            _stopped = true;
            if (_systems != null)
            {
                for (int i = 0; i < _systems.Length; i++)
                {
                    if (_systems[i] != null) _systems[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            // Let the last wisps finish before the objects go.
            Destroy(this, 8f);
        }

        private void OnDestroy()
        {
            if (_counted) { s_active = Mathf.Max(0, s_active - 1); _counted = false; }
            if (_systems != null)
            {
                for (int i = 0; i < _systems.Length; i++)
                {
                    if (_systems[i] != null) Destroy(_systems[i].gameObject);
                }
                _systems = null;
            }
        }
    }
}
