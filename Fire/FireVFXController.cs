using System.Collections.Generic;
using UnityEngine;

namespace FireFront.Fire
{
    public class FireVFXController : MonoBehaviour
    {
        private const int LineCount = 8;
        private const int SegmentsPerLine = 30;

        private LineRenderer[] _flameLines = new LineRenderer[LineCount];
        private float[] _lineTwists = new float[LineCount];
        private float[] _bottomOffsets = new float[LineCount];
        private float[] _topOffsets = new float[LineCount];
        private float[] _noiseSeedsX = new float[LineCount];
        private float[] _noiseSeedsZ = new float[LineCount];

        private ParticleSystem _particleSystem;
        private ParticleSystem _sparksSystem;
        private Light _fireLight;
        
        private float _burnDuration;
        private float _timeAlive;
        private Bounds _targetBounds;
        private float _trunkRadius = 0.45f;
        private float _treeHeight = 4f;
        private float _baseLightIntensity = 2.5f;

        private Material _fireLineMat;
        private Material _fireParticleMat;
        private Material _sparksMat;

        public void Setup(Bounds bounds, float burnDuration)
        {
            _targetBounds = bounds;
            _burnDuration = Mathf.Max(1f, burnDuration);
            _timeAlive = 0f;

            // Compute realistic trunk radius and tree height
            _trunkRadius = Mathf.Clamp(_targetBounds.extents.x * 0.16f, 0.32f, 0.70f);
            _treeHeight = Mathf.Max(2.5f, _targetBounds.size.y);

            // Seed per-tendril variations
            for (int i = 0; i < LineCount; i++)
            {
                _lineTwists[i] = (i % 2 == 0 ? 0.35f : -0.35f) + Random.Range(-0.1f, 0.1f);
                _bottomOffsets[i] = Random.Range(-0.2f, 0.3f);
                _topOffsets[i] = Random.Range(-0.4f, 0.6f);
                _noiseSeedsX[i] = Random.Range(10f, 100f);
                _noiseSeedsZ[i] = Random.Range(10f, 100f);
            }

            Texture2D softTex = FireFrontTextureGenerator.GenerateSoftFireGlow();
            _fireParticleMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(softTex, true);
            
            Texture2D streakTex = FireFrontTextureGenerator.GenerateFireStreak();
            _fireLineMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(streakTex, true);
            
            _sparksMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(softTex, true);

            InitLines();
            InitLight();
            InitParticles();
        }

        private void InitLines()
        {
            // Build shared tapered flame width curve
            AnimationCurve widthCurve = new AnimationCurve();
            widthCurve.AddKey(new Keyframe(0.0f, 0.35f));
            widthCurve.AddKey(new Keyframe(0.2f, 0.45f));
            widthCurve.AddKey(new Keyframe(0.6f, 0.25f));
            widthCurve.AddKey(new Keyframe(1.0f, 0.02f));

            // Build shared incandescent color gradient (white-hot core -> orange -> crimson -> smoke tip)
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1.0f, 0.96f, 0.70f), 0.0f),
                    new GradientColorKey(new Color(1.0f, 0.65f, 0.10f), 0.20f),
                    new GradientColorKey(new Color(1.0f, 0.28f, 0.03f), 0.55f),
                    new GradientColorKey(new Color(0.70f, 0.08f, 0.01f), 0.85f),
                    new GradientColorKey(new Color(0.20f, 0.02f, 0.00f), 1.0f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.95f, 0.0f),
                    new GradientAlphaKey(0.90f, 0.20f),
                    new GradientAlphaKey(0.75f, 0.60f),
                    new GradientAlphaKey(0.35f, 0.85f),
                    new GradientAlphaKey(0.00f, 1.0f)
                }
            );

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
                lr.widthCurve = widthCurve;
                lr.colorGradient = gradient;
                _flameLines[i] = lr;
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
            // Soft flame puffs
            GameObject psObj = new GameObject("FireParticles");
            psObj.transform.SetParent(transform, false);
            _particleSystem = psObj.AddComponent<ParticleSystem>();
            var main = _particleSystem.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.3f, 0.7f), new Color(1f, 0.3f, 0f, 0.4f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = _particleSystem.emission;
            emission.rateOverTime = 25f;

            var shape = _particleSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = _trunkRadius * 1.1f;

            var renderer = psObj.GetComponent<ParticleSystemRenderer>();
            renderer.material = _fireParticleMat;
            
            // Rising sparks & embers
            GameObject sparksObj = new GameObject("Sparks");
            sparksObj.transform.SetParent(transform, false);
            _sparksSystem = sparksObj.AddComponent<ParticleSystem>();
            var smain = _sparksSystem.main;
            smain.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
            smain.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
            smain.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
            smain.startColor = new Color(1f, 0.75f, 0.2f, 1f);
            smain.simulationSpace = ParticleSystemSimulationSpace.World;

            var semission = _sparksSystem.emission;
            semission.rateOverTime = 18f;

            var sshape = _sparksSystem.shape;
            sshape.shapeType = ParticleSystemShapeType.Cone;
            sshape.angle = 15f;
            sshape.radius = _trunkRadius * 0.9f;

            var srenderer = sparksObj.GetComponent<ParticleSystemRenderer>();
            srenderer.material = _sparksMat;
            
            _particleSystem.Play();
            _sparksSystem.Play();
        }

        private void Update()
        {
            _timeAlive += Time.deltaTime;
            float progress = Mathf.Clamp01(_timeAlive / _burnDuration);
            
            // The leading flame front climbs from the base to the top of the tree
            float flameHeadY = Mathf.Lerp(1.2f, _treeHeight, progress);
            
            // The base of active flames stays near ground initially, then lifts as lower wood is consumed
            float flameBaseY = Mathf.Max(0f, (progress - 0.35f) / 0.65f * (_treeHeight * 0.75f));
            
            Vector3 basePos = transform.position;
            float midHeight = (flameBaseY + flameHeadY) * 0.5f;

            // Move dynamic light with the climbing fire center and flicker realistically
            if (_fireLight != null)
            {
                _fireLight.transform.position = basePos + Vector3.up * midHeight;
                float flicker = Mathf.PerlinNoise(Time.time * 8.5f, 0.25f) * 0.35f + Mathf.Sin(Time.time * 26f) * 0.1f;
                _fireLight.intensity = _baseLightIntensity * (0.85f + flicker);
            }

            // Move particle emitters up with the climbing fire front
            if (_particleSystem != null)
            {
                _particleSystem.transform.position = basePos + Vector3.up * flameBaseY;
            }
            if (_sparksSystem != null)
            {
                _sparksSystem.transform.position = basePos + Vector3.up * midHeight;
            }

            // Animate vertical flame ribbons dancing up the trunk
            for (int i = 0; i < LineCount; i++)
            {
                if (_flameLines[i] != null)
                {
                    UpdateFlameLine(_flameLines[i], i, basePos, flameBaseY, flameHeadY);
                }
            }
        }

        private void UpdateFlameLine(LineRenderer lr, int index, Vector3 basePos, float flameBaseY, float flameHeadY)
        {
            float baseAzimuth = (index / (float)LineCount) * (Mathf.PI * 2f);
            float twist = _lineTwists[index];

            float lineBottom = Mathf.Max(0f, flameBaseY + _bottomOffsets[index]);
            float lineTop = Mathf.Clamp(flameHeadY + _topOffsets[index], lineBottom + 0.8f, _treeHeight + 0.5f);
            float lineSpan = lineTop - lineBottom;

            Vector3[] points = new Vector3[SegmentsPerLine];

            for (int j = 0; j < SegmentsPerLine; j++)
            {
                float u = j / (float)(SegmentsPerLine - 1); // 0 (base) to 1 (flame tip)
                float y = lineBottom + u * lineSpan;

                // Upward-traveling convective wave: phase scrolls upward with Time
                float wavePhase = (y * 2.2f) - (Time.time * 6.2f) + (index * 1.8f);
                float waveRadial = (Mathf.PerlinNoise(_noiseSeedsX[index] + wavePhase * 0.35f, 0.3f) - 0.5f) * 2f;
                float waveTangential = (Mathf.PerlinNoise(0.3f, _noiseSeedsZ[index] + wavePhase * 0.35f) - 0.5f) * 2f;

                // High-frequency chaotic flutter/lick (intensity increases near tip)
                float flutter = Mathf.Sin(Time.time * 24f + j * 1.5f + index * 3.7f) * 0.07f;

                // Amplitude scaling: zero at base (firmly rooted to bark), large at tip (licking into air)
                float amp = Mathf.Pow(u, 1.3f) * 0.38f;

                float currentAngle = baseAzimuth + (u * twist) + (waveTangential * amp);
                float currentRadius = _trunkRadius * (1f - u * 0.18f) + (waveRadial * amp * 0.5f) + (flutter * u);
                if (currentRadius < 0.12f) currentRadius = 0.12f;

                float px = Mathf.Cos(currentAngle) * currentRadius;
                float pz = Mathf.Sin(currentAngle) * currentRadius;
                float py = y + (flutter * u * 0.4f);

                points[j] = basePos + new Vector3(px, py, pz);
            }

            lr.SetPositions(points);
        }
    }
}
