using System.Collections.Generic;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    public class FireVFXController : MonoBehaviour
    {
        private const int LineCount = 8;
        private const int GroundLineCount = 4;
        private const int SegmentsPerLine = 30;
        private const int SegmentsPerGroundLine = 20;

        private LineRenderer[] _flameLines = new LineRenderer[LineCount];
        private float[] _lineTwists = new float[LineCount];
        private float[] _bottomOffsets = new float[LineCount];
        private float[] _topOffsets = new float[LineCount];
        private float[] _noiseSeedsX = new float[LineCount];
        private float[] _noiseSeedsZ = new float[LineCount];

        private LineRenderer[] _groundLines = new LineRenderer[GroundLineCount];
        private float[] _groundAngles = new float[GroundLineCount];

        private ParticleSystem _sparksSystem;
        private ParticleSystem _smokeSystem;
        private Light _fireLight;

        private float _burnDuration;
        private float _timeAlive;
        private Bounds _targetBounds;
        private float _trunkRadius = 0.45f;
        private float _treeHeight = 4f;
        private float _baseLightIntensity = 2.8f;

        private Vector3 _trunkAxis = Vector3.up;
        private Vector3 _perpX = Vector3.right;
        private Vector3 _perpZ = Vector3.forward;
        private bool _isLog = false;

        private Material _fireLineMat;
        private Material _sparksMat;
        private Material _smokeMat;

        public void Setup(Bounds bounds, float burnDuration, Component target = null)
        {
            _targetBounds = bounds;
            _burnDuration = Mathf.Max(1f, burnDuration);
            _timeAlive = 0f;

            // Detect if the target is a fallen log or horizontal burner
            if (target != null && target.transform != null && bounds.size.y < 2.2f && (bounds.size.x > 2.5f || bounds.size.z > 2.5f))
            {
                _isLog = true;
                Vector3 fwd = target.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.05f) fwd = target.transform.right;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.05f) fwd = Vector3.forward;
                _trunkAxis = fwd.normalized;
                _treeHeight = Mathf.Max(2.5f, Mathf.Max(bounds.size.x, bounds.size.z));
                _trunkRadius = Mathf.Clamp(bounds.size.y * 0.4f, 0.25f, 0.55f);
            }
            else
            {
                _isLog = false;
                _trunkAxis = Vector3.up;
                _treeHeight = Mathf.Max(2.5f, bounds.size.y);
                _trunkRadius = Mathf.Clamp(bounds.extents.x * 0.16f, 0.32f, 0.70f);
            }

            // Establish orthonormal basis around trunk axis
            Vector3.OrthoNormalize(ref _trunkAxis, ref _perpX, ref _perpZ);

            // Seed per-tendril variations
            for (int i = 0; i < LineCount; i++)
            {
                _lineTwists[i] = (i % 2 == 0 ? 0.35f : -0.35f) + Random.Range(-0.1f, 0.1f);
                _bottomOffsets[i] = Random.Range(-0.2f, 0.3f);
                _topOffsets[i] = Random.Range(-0.4f, 0.6f);
                _noiseSeedsX[i] = Random.Range(10f, 100f);
                _noiseSeedsZ[i] = Random.Range(10f, 100f);
            }

            for (int k = 0; k < GroundLineCount; k++)
            {
                _groundAngles[k] = (k / (float)GroundLineCount) * Mathf.PI * 2f + Random.Range(-0.2f, 0.2f);
            }

            Texture2D streakTex = FireFrontTextureGenerator.GenerateFireStreak();
            _fireLineMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(streakTex, true);

            Texture2D glowTex = FireFrontTextureGenerator.GenerateSoftFireGlow();
            _sparksMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(glowTex, true);

            Texture2D smokeTex = FireFrontTextureGenerator.GenerateSmokeTexture();
            _smokeMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(smokeTex, false);

            InitLines();
            InitLight();
            InitParticles();
        }

        private void InitLines()
        {
            // Tapered flame ribbon width curve
            AnimationCurve trunkCurve = new AnimationCurve();
            trunkCurve.AddKey(new Keyframe(0.0f, 0.38f));
            trunkCurve.AddKey(new Keyframe(0.2f, 0.48f));
            trunkCurve.AddKey(new Keyframe(0.6f, 0.26f));
            trunkCurve.AddKey(new Keyframe(1.0f, 0.02f));

            // Ground collar creeping fire curve (wide base spreading across turf)
            AnimationCurve groundCurve = new AnimationCurve();
            groundCurve.AddKey(new Keyframe(0.0f, 0.45f));
            groundCurve.AddKey(new Keyframe(0.3f, 0.35f));
            groundCurve.AddKey(new Keyframe(0.7f, 0.18f));
            groundCurve.AddKey(new Keyframe(1.0f, 0.01f));

            // Incandescent 5-stop color gradient
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1.0f, 0.98f, 0.75f), 0.0f),  // White-hot core
                    new GradientColorKey(new Color(1.0f, 0.68f, 0.12f), 0.22f), // Brilliant flame yellow-orange
                    new GradientColorKey(new Color(1.0f, 0.30f, 0.03f), 0.58f), // Deep flame red-orange
                    new GradientColorKey(new Color(0.75f, 0.09f, 0.01f), 0.85f),// Crimson ember
                    new GradientColorKey(new Color(0.22f, 0.02f, 0.00f), 1.0f)  // Dark smoke tip
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.95f, 0.0f),
                    new GradientAlphaKey(0.92f, 0.25f),
                    new GradientAlphaKey(0.78f, 0.65f),
                    new GradientAlphaKey(0.40f, 0.88f),
                    new GradientAlphaKey(0.00f, 1.0f)
                }
            );

            // 1. Trunk-climbing flame ribbons
            for (int i = 0; i < LineCount; i++)
            {
                GameObject lineObj = new GameObject("FlameRibbon_" + i);
                lineObj.transform.SetParent(transform, false);
                var lr = lineObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _fireLineMat;
                lr.positionCount = SegmentsPerLine;
                lr.textureMode = LineTextureMode.Stretch;
                lr.numCornerVertices = 4;
                lr.numCapVertices = 4;
                lr.widthCurve = trunkCurve;
                lr.colorGradient = gradient;
                _flameLines[i] = lr;
            }

            // 2. Ground collar creeping flames
            for (int k = 0; k < GroundLineCount; k++)
            {
                GameObject gObj = new GameObject("GroundCreep_" + k);
                gObj.transform.SetParent(transform, false);
                var lr = gObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _fireLineMat;
                lr.positionCount = SegmentsPerGroundLine;
                lr.textureMode = LineTextureMode.Stretch;
                lr.numCornerVertices = 4;
                lr.numCapVertices = 4;
                lr.widthCurve = groundCurve;
                lr.colorGradient = gradient;
                _groundLines[k] = lr;
            }
        }

        private void InitLight()
        {
            GameObject lightObj = new GameObject("FirePointLight");
            lightObj.transform.SetParent(transform, false);
            lightObj.transform.localPosition = Vector3.zero;

            _fireLight = lightObj.AddComponent<Light>();
            _fireLight.type = LightType.Point;
            _fireLight.color = new Color(1.0f, 0.60f, 0.20f);
            _fireLight.range = Mathf.Clamp(_trunkRadius * 8f + 8f, 8f, 22f);
            _fireLight.intensity = _baseLightIntensity;
            _fireLight.shadows = LightShadows.None;
        }

        private void InitParticles()
        {
            // Stretched incandescent sparks/embers (eliminates round dots!)
            GameObject sparksObj = new GameObject("Sparks");
            sparksObj.transform.SetParent(transform, false);
            _sparksSystem = sparksObj.AddComponent<ParticleSystem>();
            var smain = _sparksSystem.main;
            smain.loop = true;
            smain.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
            smain.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 7.5f);
            smain.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.10f);
            smain.startColor = new Color(1f, 0.85f, 0.35f, 1f);
            smain.simulationSpace = ParticleSystemSimulationSpace.World;
            smain.gravityModifier = -0.15f; // Heat convection buoyancy

            var semission = _sparksSystem.emission;
            semission.rateOverTime = 22f;

            var sshape = _sparksSystem.shape;
            sshape.shapeType = ParticleSystemShapeType.Cone;
            sshape.angle = 15f;
            sshape.radius = _trunkRadius * 1.1f;
            sshape.rotation = new Vector3(-90f, 0f, 0f); // Point along world +Y

            var srenderer = sparksObj.GetComponent<ParticleSystemRenderer>();
            srenderer.renderMode = ParticleSystemRenderMode.Stretch;
            srenderer.lengthScale = 4.0f;
            srenderer.velocityScale = 0.08f;
            srenderer.material = _sparksMat;

            // Billowing atmospheric smoke plume
            GameObject smokeObj = new GameObject("Smoke");
            smokeObj.transform.SetParent(transform, false);
            _smokeSystem = smokeObj.AddComponent<ParticleSystem>();
            var skmain = _smokeSystem.main;
            skmain.loop = true;
            skmain.startLifetime = new ParticleSystem.MinMaxCurve(3.2f, 5.5f);
            skmain.startSpeed = new ParticleSystem.MinMaxCurve(1.0f, 2.5f);
            skmain.startSize = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
            skmain.startColor = new Color(0.2f, 0.2f, 0.2f, 0.35f);
            skmain.simulationSpace = ParticleSystemSimulationSpace.World;
            skmain.gravityModifier = -0.06f;

            var skemission = _smokeSystem.emission;
            skemission.rateOverTime = 12f;

            var skshape = _smokeSystem.shape;
            skshape.shapeType = ParticleSystemShapeType.Cone;
            skshape.angle = 22f;
            skshape.radius = _trunkRadius * 1.5f;
            skshape.rotation = new Vector3(-90f, 0f, 0f);

            var sksize = _smokeSystem.sizeOverLifetime;
            sksize.enabled = true;
            sksize.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(1f, 1.8f)));

            var skcolor = _smokeSystem.colorOverLifetime;
            skcolor.enabled = true;
            Gradient smokeGrad = new Gradient();
            smokeGrad.SetKeys(
                new[] { new GradientColorKey(new Color(0.25f, 0.23f, 0.2f), 0f), new GradientColorKey(new Color(0.35f, 0.35f, 0.35f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.35f, 0.25f), new GradientAlphaKey(0f, 1f) }
            );
            skcolor.color = smokeGrad;

            var skrenderer = smokeObj.GetComponent<ParticleSystemRenderer>();
            skrenderer.material = _smokeMat;

            _sparksSystem.Play();
            _smokeSystem.Play();
        }

        private void Update()
        {
            _timeAlive += Time.deltaTime;
            float progress = Mathf.Clamp01(_timeAlive / _burnDuration);

            // Fetch live wind from the environment
            Vector3 windDir = ValheimBridge.GetWindDirection() ?? Vector3.forward;
            float windStrength = ValheimBridge.GetWindIntensity() ?? 0.5f;
            Vector3 windHoriz = new Vector3(windDir.x, 0f, windDir.z);
            if (windHoriz.sqrMagnitude > 0.001f) windHoriz.Normalize();
            else windHoriz = Vector3.forward;

            // Natural dynamic wind gusts
            float gust = 1f + 0.35f * (Mathf.PerlinNoise(Time.time * 2.2f, 31.4f) - 0.5f) * 2f;
            float currentWindSpeed = windStrength * gust;
            Vector3 windForceVec = windHoriz * (currentWindSpeed * 1.6f);

            // Fire front progression along trunk
            float flameHeadDist = Mathf.Lerp(1.2f, _treeHeight, progress);
            float flameBaseDist = Mathf.Max(0f, (progress - 0.35f) / 0.65f * (_treeHeight * 0.75f));

            Vector3 rootPos = transform.position;
            float midDist = (flameBaseDist + flameHeadDist) * 0.5f;
            Vector3 midPosition = rootPos + _trunkAxis * midDist;

            // Point light tracks the flame center, leaning slightly with wind
            if (_fireLight != null)
            {
                _fireLight.transform.position = midPosition + windForceVec * (midDist / _treeHeight * 0.5f);
                float flicker = Mathf.PerlinNoise(Time.time * 8.5f, 0.25f) * 0.35f + Mathf.Sin(Time.time * 26f) * 0.1f;
                _fireLight.intensity = _baseLightIntensity * (0.85f + flicker);
            }

            // Particle emitters follow the flame front and blow downwind
            if (_sparksSystem != null)
            {
                _sparksSystem.transform.position = midPosition;
                var svel = _sparksSystem.velocityOverLifetime;
                svel.enabled = true;
                svel.space = ParticleSystemSimulationSpace.World;
                svel.x = windForceVec.x * 2.8f;
                svel.z = windForceVec.z * 2.8f;
                svel.y = new ParticleSystem.MinMaxCurve(3.0f, 6.0f);
            }

            if (_smokeSystem != null)
            {
                _smokeSystem.transform.position = rootPos + _trunkAxis * flameHeadDist;
                var skvel = _smokeSystem.velocityOverLifetime;
                skvel.enabled = true;
                skvel.space = ParticleSystemSimulationSpace.World;
                skvel.x = windForceVec.x * 3.6f;
                skvel.z = windForceVec.z * 3.6f;
                skvel.y = new ParticleSystem.MinMaxCurve(1.5f, 3.2f);
            }

            // 1. Animate vertical trunk ribbons climbing and interacting with wind
            for (int i = 0; i < LineCount; i++)
            {
                if (_flameLines[i] != null)
                {
                    UpdateFlameLine(_flameLines[i], i, rootPos, flameBaseDist, flameHeadDist, windForceVec);
                }
            }

            // 2. Animate ground collar creeping fire surrounding the tree base
            for (int k = 0; k < GroundLineCount; k++)
            {
                if (_groundLines[k] != null)
                {
                    UpdateGroundLine(_groundLines[k], k, rootPos, progress, windForceVec);
                }
            }
        }

        private void UpdateFlameLine(LineRenderer lr, int index, Vector3 rootPos, float flameBaseDist, float flameHeadDist, Vector3 windForceVec)
        {
            float baseAzimuth = (index / (float)LineCount) * (Mathf.PI * 2f);
            float twist = _lineTwists[index];

            float lineBottom = Mathf.Max(0f, flameBaseDist + _bottomOffsets[index]);
            float lineTop = Mathf.Clamp(flameHeadDist + _topOffsets[index], lineBottom + 0.8f, _treeHeight + 0.5f);
            float lineSpan = lineTop - lineBottom;

            Vector3[] points = new Vector3[SegmentsPerLine];

            for (int j = 0; j < SegmentsPerLine; j++)
            {
                float u = j / (float)(SegmentsPerLine - 1); // 0 (base) to 1 (flame tip)
                float distAlongTrunk = lineBottom + u * lineSpan;

                // Convective wave traveling along the trunk
                float wavePhase = (distAlongTrunk * 2.4f) - (Time.time * 6.5f) + (index * 1.8f);
                float waveRadial = (Mathf.PerlinNoise(_noiseSeedsX[index] + wavePhase * 0.35f, 0.3f) - 0.5f) * 2f;
                float waveTangential = (Mathf.PerlinNoise(0.3f, _noiseSeedsZ[index] + wavePhase * 0.35f) - 0.5f) * 2f;

                // High-frequency chaotic flutter/lick (increases toward flame tip)
                float flutter = Mathf.Sin(Time.time * 24f + j * 1.5f + index * 3.7f) * 0.07f;

                // Amplitude scaling (zero at bark anchor, wide lick at tip)
                float amp = Mathf.Pow(u, 1.3f) * 0.38f;

                float currentAngle = baseAzimuth + (u * twist) + (waveTangential * amp);
                float currentRadius = _trunkRadius * (1f - u * 0.18f) + (waveRadial * amp * 0.5f) + (flutter * u);
                if (currentRadius < 0.12f) currentRadius = 0.12f;

                // Radial displacement around the trunk
                Vector3 radialOffset = (_perpX * Mathf.Cos(currentAngle) + _perpZ * Mathf.Sin(currentAngle)) * currentRadius;

                // Wind deflection: flames lean downwind as they climb higher
                Vector3 windOffset = windForceVec * (Mathf.Pow(u, 1.35f) * 1.2f);

                // Natural buoyant updraft: flames always lick upward against gravity
                Vector3 buoyantLick = Vector3.up * (flutter * u * 0.4f + Mathf.Pow(u, 1.2f) * 0.25f);

                points[j] = rootPos + (_trunkAxis * distAlongTrunk) + radialOffset + windOffset + buoyantLick;
            }

            lr.SetPositions(points);
        }

        private void UpdateGroundLine(LineRenderer lr, int index, Vector3 rootPos, float progress, Vector3 windForceVec)
        {
            float baseAngle = _groundAngles[index];
            float maxReach = Mathf.Lerp(0.8f, 1.8f, Mathf.Clamp01(progress * 2f));

            Vector3[] points = new Vector3[SegmentsPerGroundLine];

            for (int j = 0; j < SegmentsPerGroundLine; j++)
            {
                float u = j / (float)(SegmentsPerGroundLine - 1); // 0 (trunk collar) to 1 (outer creeping tongue)
                float r = Mathf.Lerp(_trunkRadius * 0.8f, maxReach, u);

                float wave = Mathf.Sin(Time.time * 10f + j * 0.8f + index * 2.3f) * 0.08f;
                float angle = baseAngle + (Mathf.PerlinNoise(index * 10f + u * 2f, Time.time * 1.5f) - 0.5f) * 0.6f;

                Vector3 dir = (_perpX * Mathf.Cos(angle) + _perpZ * Mathf.Sin(angle)).normalized;

                // Hug ground with slight licking flame height
                float groundY = wave * (1f - u) + Mathf.Sin(Time.time * 16f + j) * 0.06f * u;

                // Wind pushes creeping ground flames downwind
                Vector3 windLean = windForceVec * (u * 0.4f);

                points[j] = rootPos + (dir * r) + Vector3.up * Mathf.Max(0.02f, groundY) + windLean;
            }

            lr.SetPositions(points);
        }
    }
}

