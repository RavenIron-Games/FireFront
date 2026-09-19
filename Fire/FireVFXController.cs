using System.Collections.Generic;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    public class FireVFXController : MonoBehaviour
    {
        private const int LineCount = 8;
        private const int CharLineCount = 4;
        private const int GroundLineCount = 4;
        private const int SegmentsPerLine = 30;
        private const int SegmentsPerGroundLine = 20;

        // 1. Outer licking flame ribbons
        private LineRenderer[] _flameLines = new LineRenderer[LineCount];
        private float[] _lineTwists = new float[LineCount];
        private float[] _bottomOffsets = new float[LineCount];
        private float[] _topOffsets = new float[LineCount];
        private float[] _noiseSeedsX = new float[LineCount];
        private float[] _noiseSeedsZ = new float[LineCount];

        // 2. Inner glowing char ribbons (wood actively smoldering/burning underneath)
        private LineRenderer[] _charLines = new LineRenderer[CharLineCount];

        // 3. Ground collar creeping flames (crawling across turf/roots)
        private LineRenderer[] _groundLines = new LineRenderer[GroundLineCount];
        private float[] _groundAngles = new float[GroundLineCount];

        private ParticleSystem _sparksSystem;
        private ParticleSystem _smokeSystem;
        private Light _fireLight;

        private float _burnDuration;
        private float _timeAlive;
        private Bounds _targetBounds;
        private float _trunkRadius = 0.40f;
        private float _treeHeight = 4f;
        private float _baseLightIntensity = 2.8f;

        private Vector3 _trunkAxis = Vector3.up;
        private Vector3 _perpX = Vector3.right;
        private Vector3 _perpZ = Vector3.forward;
        private bool _isLog = false;

        private Material _fireLineMat;
        private Material _charMat;
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
                _trunkRadius = Mathf.Clamp(bounds.size.y * 0.38f, 0.22f, 0.50f);
            }
            else
            {
                _isLog = false;
                _trunkAxis = Vector3.up;
                _treeHeight = Mathf.Max(2.5f, bounds.size.y);
                // Tight trunk radius to hug the bark directly
                _trunkRadius = Mathf.Clamp(bounds.extents.x * 0.14f, 0.28f, 0.55f);
            }

            // Establish orthonormal basis around trunk axis
            Vector3.OrthoNormalize(ref _trunkAxis, ref _perpX, ref _perpZ);

            // Seed per-tendril variations
            for (int i = 0; i < LineCount; i++)
            {
                _lineTwists[i] = (i % 2 == 0 ? 0.30f : -0.30f) + Random.Range(-0.08f, 0.08f);
                _bottomOffsets[i] = Random.Range(-0.15f, 0.25f);
                _topOffsets[i] = Random.Range(-0.35f, 0.45f);
                _noiseSeedsX[i] = Random.Range(10f, 100f);
                _noiseSeedsZ[i] = Random.Range(10f, 100f);
            }

            for (int k = 0; k < GroundLineCount; k++)
            {
                _groundAngles[k] = (k / (float)GroundLineCount) * Mathf.PI * 2f + Random.Range(-0.2f, 0.2f);
            }

            // Borrow authentic materials or fall back to high-res procedural
            _fireLineMat = FireFrontTextureGenerator.GetOrCreateFlameMaterial();
            _charMat = FireFrontTextureGenerator.GetOrCreateFlameMaterial();
            _sparksMat = FireFrontTextureGenerator.GetOrCreateSparkMaterial();
            _smokeMat = FireFrontTextureGenerator.GetOrCreateSmokeMaterial();

            InitLines();
            InitLight();
            InitParticles();
        }

        private void InitLines()
        {
            // 1. Tapered flame ribbon width curve
            AnimationCurve trunkCurve = new AnimationCurve();
            trunkCurve.AddKey(new Keyframe(0.0f, 0.32f));
            trunkCurve.AddKey(new Keyframe(0.25f, 0.44f));
            trunkCurve.AddKey(new Keyframe(0.65f, 0.22f));
            trunkCurve.AddKey(new Keyframe(1.0f, 0.02f));

            // Char under-glow width curve (broad glowing embers on the wood)
            AnimationCurve charCurve = new AnimationCurve();
            charCurve.AddKey(new Keyframe(0.0f, 0.38f));
            charCurve.AddKey(new Keyframe(0.5f, 0.35f));
            charCurve.AddKey(new Keyframe(1.0f, 0.05f));

            // Ground collar creeping fire curve (wide base spreading across turf)
            AnimationCurve groundCurve = new AnimationCurve();
            groundCurve.AddKey(new Keyframe(0.0f, 0.40f));
            groundCurve.AddKey(new Keyframe(0.3f, 0.30f));
            groundCurve.AddKey(new Keyframe(0.7f, 0.15f));
            groundCurve.AddKey(new Keyframe(1.0f, 0.01f));

            // Incandescent 5-stop flame color gradient
            Gradient flameGradient = new Gradient();
            flameGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1.0f, 0.98f, 0.80f), 0.0f),  // White-hot core
                    new GradientColorKey(new Color(1.0f, 0.65f, 0.10f), 0.22f), // Brilliant flame yellow-orange
                    new GradientColorKey(new Color(1.0f, 0.26f, 0.02f), 0.58f), // Deep flame red-orange
                    new GradientColorKey(new Color(0.70f, 0.08f, 0.01f), 0.85f),// Crimson ember
                    new GradientColorKey(new Color(0.18f, 0.02f, 0.00f), 1.0f)  // Dark smoke tip
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.95f, 0.0f),
                    new GradientAlphaKey(0.90f, 0.25f),
                    new GradientAlphaKey(0.75f, 0.65f),
                    new GradientAlphaKey(0.35f, 0.88f),
                    new GradientAlphaKey(0.00f, 1.0f)
                }
            );

            // Char under-glow color gradient (deep smoldering wood embers)
            Gradient charGradient = new Gradient();
            charGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1.0f, 0.40f, 0.05f), 0.0f),  // Hot orange embers
                    new GradientColorKey(new Color(0.85f, 0.15f, 0.01f), 0.5f), // Deep red char
                    new GradientColorKey(new Color(0.20f, 0.02f, 0.00f), 1.0f)  // Blackened char
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.85f, 0.0f),
                    new GradientAlphaKey(0.70f, 0.5f),
                    new GradientAlphaKey(0.00f, 1.0f)
                }
            );

            // 1. Trunk-climbing licking flame ribbons
            for (int i = 0; i < LineCount; i++)
            {
                GameObject lineObj = new GameObject("FlameRibbon_" + i);
                lineObj.transform.SetParent(transform, false);
                var lr = lineObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _fireLineMat;
                lr.positionCount = SegmentsPerLine;
                lr.textureMode = LineTextureMode.Tile;
                lr.numCornerVertices = 4;
                lr.numCapVertices = 4;
                lr.widthCurve = trunkCurve;
                lr.colorGradient = flameGradient;
                _flameLines[i] = lr;
            }

            // 2. Char under-glow ribbons (hugging the bark directly)
            for (int c = 0; c < CharLineCount; c++)
            {
                GameObject charObj = new GameObject("CharGlow_" + c);
                charObj.transform.SetParent(transform, false);
                var lr = charObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _charMat;
                lr.positionCount = SegmentsPerLine;
                lr.textureMode = LineTextureMode.Tile;
                lr.numCornerVertices = 3;
                lr.numCapVertices = 3;
                lr.widthCurve = charCurve;
                lr.colorGradient = charGradient;
                _charLines[c] = lr;
            }

            // 3. Ground collar creeping flames
            for (int k = 0; k < GroundLineCount; k++)
            {
                GameObject gObj = new GameObject("GroundCreep_" + k);
                gObj.transform.SetParent(transform, false);
                var lr = gObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _fireLineMat;
                lr.positionCount = SegmentsPerGroundLine;
                lr.textureMode = LineTextureMode.Tile;
                lr.numCornerVertices = 4;
                lr.numCapVertices = 4;
                lr.widthCurve = groundCurve;
                lr.colorGradient = flameGradient;
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
            _fireLight.color = new Color(1.0f, 0.58f, 0.18f);
            _fireLight.range = Mathf.Clamp(_trunkRadius * 8f + 8f, 8f, 22f);
            _fireLight.intensity = _baseLightIntensity;
            _fireLight.shadows = LightShadows.None;
        }

        private void InitParticles()
        {
            // Natural lazy drifting sparks/embers (ELIMINATES FIREWORKS!)
            GameObject sparksObj = new GameObject("Sparks");
            sparksObj.transform.SetParent(transform, false);
            _sparksSystem = sparksObj.AddComponent<ParticleSystem>();
            var smain = _sparksSystem.main;
            smain.loop = true;
            smain.startLifetime = new ParticleSystem.MinMaxCurve(2.0f, 4.0f);
            smain.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.2f); // Slow, lazy drift
            smain.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f); // Authentic small glowing motes
            smain.startColor = new Color(1f, 0.85f, 0.35f, 0.95f);
            smain.simulationSpace = ParticleSystemSimulationSpace.World;
            smain.gravityModifier = 0.02f; // Slight positive gravity: embers slowly sink unless lifted by heat

            var semission = _sparksSystem.emission;
            semission.rateOverTime = 8f; // Gentle, occasional embers rather than rocket barrage

            var sshape = _sparksSystem.shape;
            sshape.shapeType = ParticleSystemShapeType.Cone;
            sshape.angle = 20f;
            sshape.radius = _trunkRadius * 1.05f;
            sshape.rotation = new Vector3(-90f, 0f, 0f); // Point along world +Y

            // Swirling thermal turbulence
            var snoise = _sparksSystem.noise;
            snoise.enabled = true;
            snoise.strength = 0.45f;
            snoise.frequency = 0.6f;
            snoise.scrollSpeed = 0.8f;

            var srenderer = sparksObj.GetComponent<ParticleSystemRenderer>();
            srenderer.renderMode = ParticleSystemRenderMode.Billboard; // No more 4-meter laser streaks!
            srenderer.material = _sparksMat;

            // Billowing atmospheric smoke plume
            GameObject smokeObj = new GameObject("Smoke");
            smokeObj.transform.SetParent(transform, false);
            _smokeSystem = smokeObj.AddComponent<ParticleSystem>();
            var skmain = _smokeSystem.main;
            skmain.loop = true;
            skmain.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6.0f);
            skmain.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            skmain.startSize = new ParticleSystem.MinMaxCurve(1.0f, 2.6f);
            skmain.startColor = new Color(0.22f, 0.22f, 0.22f, 0.35f);
            skmain.simulationSpace = ParticleSystemSimulationSpace.World;
            skmain.gravityModifier = -0.04f;

            var skemission = _smokeSystem.emission;
            skemission.rateOverTime = 10f;

            var skshape = _smokeSystem.shape;
            skshape.shapeType = ParticleSystemShapeType.Cone;
            skshape.angle = 20f;
            skshape.radius = _trunkRadius * 1.4f;
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

            // Dynamic wind gusts
            float gust = 1f + 0.35f * (Mathf.PerlinNoise(Time.time * 2.2f, 31.4f) - 0.5f) * 2f;
            float currentWindSpeed = windStrength * gust;
            Vector3 windForceVec = windHoriz * (currentWindSpeed * 1.4f);

            // Continuous upward texture scrolling on the flame material (flames actively rush & lick upward!)
            if (_fireLineMat != null)
            {
                _fireLineMat.mainTextureOffset = new Vector2(0f, -Time.time * 2.6f);
            }

            // Fire front progression along trunk
            float flameHeadDist = Mathf.Lerp(1.0f, _treeHeight, progress);
            float flameBaseDist = Mathf.Max(0f, (progress - 0.35f) / 0.65f * (_treeHeight * 0.75f));

            Vector3 rootPos = transform.position;
            float midDist = (flameBaseDist + flameHeadDist) * 0.5f;
            Vector3 midPosition = rootPos + _trunkAxis * midDist;

            // Point light tracks the flame center, leaning slightly with wind
            if (_fireLight != null)
            {
                _fireLight.transform.position = midPosition + windForceVec * (midDist / _treeHeight * 0.4f);
                float flicker = Mathf.PerlinNoise(Time.time * 8.5f, 0.25f) * 0.35f + Mathf.Sin(Time.time * 26f) * 0.1f;
                _fireLight.intensity = _baseLightIntensity * (0.85f + flicker);
            }

            // Gentle thermal updraft on embers and smoke (no rocket acceleration!)
            if (_sparksSystem != null)
            {
                _sparksSystem.transform.position = midPosition;
                var svel = _sparksSystem.velocityOverLifetime;
                svel.enabled = true;
                svel.space = ParticleSystemSimulationSpace.World;
                svel.x = windForceVec.x * 0.9f;
                svel.z = windForceVec.z * 0.9f;
                svel.y = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            }

            if (_smokeSystem != null)
            {
                _smokeSystem.transform.position = rootPos + _trunkAxis * flameHeadDist;
                var skvel = _smokeSystem.velocityOverLifetime;
                skvel.enabled = true;
                skvel.space = ParticleSystemSimulationSpace.World;
                skvel.x = windForceVec.x * 2.2f;
                skvel.z = windForceVec.z * 2.2f;
                skvel.y = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            }

            // 1. Animate outer licking flame ribbons
            for (int i = 0; i < LineCount; i++)
            {
                if (_flameLines[i] != null)
                {
                    UpdateFlameLine(_flameLines[i], i, rootPos, flameBaseDist, flameHeadDist, windForceVec);
                }
            }

            // 2. Animate inner glowing char ribbons (clamped directly to bark)
            for (int c = 0; c < CharLineCount; c++)
            {
                if (_charLines[c] != null)
                {
                    UpdateCharLine(_charLines[c], c, rootPos, flameBaseDist, flameHeadDist);
                }
            }

            // 3. Animate ground collar creeping fire surrounding the tree base
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
            float lineTop = Mathf.Clamp(flameHeadDist + _topOffsets[index], lineBottom + 0.6f, _treeHeight + 0.4f);
            float lineSpan = lineTop - lineBottom;

            Vector3[] points = new Vector3[SegmentsPerLine];

            for (int j = 0; j < SegmentsPerLine; j++)
            {
                float u = j / (float)(SegmentsPerLine - 1); // 0 (base) to 1 (flame tip)
                float distAlongTrunk = lineBottom + u * lineSpan;

                // Convective wave traveling along the trunk
                float wavePhase = (distAlongTrunk * 2.2f) - (Time.time * 6.2f) + (index * 1.8f);
                float waveRadial = (Mathf.PerlinNoise(_noiseSeedsX[index] + wavePhase * 0.35f, 0.3f) - 0.5f) * 2f;
                float waveTangential = (Mathf.PerlinNoise(0.3f, _noiseSeedsZ[index] + wavePhase * 0.35f) - 0.5f) * 2f;

                // High-frequency chaotic flutter/lick (increases toward flame tip)
                float flutter = Mathf.Sin(Time.time * 24f + j * 1.5f + index * 3.7f) * 0.05f;

                // Clamped tightly to the bark cylinder so fire creeps along the surface
                float currentAngle = baseAzimuth + (u * twist) + (waveTangential * 0.15f * u);
                float currentRadius = _trunkRadius + (0.03f + waveRadial * 0.04f * u + flutter * u);
                if (currentRadius < 0.12f) currentRadius = 0.12f;

                // Radial displacement around the trunk
                Vector3 radialOffset = (_perpX * Mathf.Cos(currentAngle) + _perpZ * Mathf.Sin(currentAngle)) * currentRadius;

                // Wind deflection: flame tips lean downwind
                Vector3 windOffset = windForceVec * (Mathf.Pow(u, 1.35f) * 0.9f);

                // Natural buoyant updraft
                Vector3 buoyantLick = Vector3.up * (flutter * u * 0.25f + Mathf.Pow(u, 1.2f) * 0.18f);

                points[j] = rootPos + (_trunkAxis * distAlongTrunk) + radialOffset + windOffset + buoyantLick;
            }

            lr.SetPositions(points);
        }

        private void UpdateCharLine(LineRenderer lr, int index, Vector3 rootPos, float flameBaseDist, float flameHeadDist)
        {
            float baseAzimuth = (index / (float)CharLineCount) * (Mathf.PI * 2f) + (Mathf.PI / 4f);

            float lineBottom = flameBaseDist;
            float lineTop = Mathf.Min(flameHeadDist, _treeHeight);
            float lineSpan = Mathf.Max(0.1f, lineTop - lineBottom);

            Vector3[] points = new Vector3[SegmentsPerLine];

            for (int j = 0; j < SegmentsPerLine; j++)
            {
                float u = j / (float)(SegmentsPerLine - 1);
                float distAlongTrunk = lineBottom + u * lineSpan;

                // Sits flush against the bark with subtle heat shimmer
                float shimmer = Mathf.Sin(Time.time * 12f + j * 2f + index) * 0.015f;
                float currentAngle = baseAzimuth + shimmer;
                float currentRadius = _trunkRadius + 0.01f + shimmer;

                Vector3 radialOffset = (_perpX * Mathf.Cos(currentAngle) + _perpZ * Mathf.Sin(currentAngle)) * currentRadius;
                points[j] = rootPos + (_trunkAxis * distAlongTrunk) + radialOffset;
            }

            lr.SetPositions(points);
        }

        private void UpdateGroundLine(LineRenderer lr, int index, Vector3 rootPos, float progress, Vector3 windForceVec)
        {
            float baseAngle = _groundAngles[index];
            float maxReach = Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(progress * 1.8f));

            Vector3[] points = new Vector3[SegmentsPerGroundLine];

            for (int j = 0; j < SegmentsPerGroundLine; j++)
            {
                float u = j / (float)(SegmentsPerGroundLine - 1); // 0 (trunk collar) to 1 (outer creeping tongue)
                float r = Mathf.Lerp(_trunkRadius * 0.9f, maxReach, u);

                float wave = Mathf.Sin(Time.time * 8f + j * 0.8f + index * 2.3f) * 0.05f;
                float angle = baseAngle + (Mathf.PerlinNoise(index * 10f + u * 2f, Time.time * 1.2f) - 0.5f) * 0.4f;

                Vector3 dir = (_perpX * Mathf.Cos(angle) + _perpZ * Mathf.Sin(angle)).normalized;

                // Hug ground with slight licking flame height
                float groundY = wave * (1f - u) + Mathf.Sin(Time.time * 14f + j) * 0.04f * u;

                // Wind pushes creeping ground flames downwind
                Vector3 windLean = windForceVec * (u * 0.25f);

                points[j] = rootPos + (dir * r) + Vector3.up * Mathf.Max(0.02f, groundY) + windLean;
            }

            lr.SetPositions(points);
        }
    }
}


