using System.Collections.Generic;
using UnityEngine;

namespace FireFront.Fire
{
    public class FireVFXController : MonoBehaviour
    {
        private LineRenderer[] _flameLines = new LineRenderer[5];
        private ParticleSystem _particleSystem;
        private ParticleSystem _sparksSystem;
        
        private float _burnDuration;
        private float _timeAlive;
        private Bounds _targetBounds;
        
        private Material _fireLineMat;
        private Material _fireParticleMat;
        private Material _sparksMat;

        private float _baseWidth = 0.5f;
        
        public void Setup(Bounds bounds, float burnDuration)
        {
            _targetBounds = bounds;
            _burnDuration = burnDuration;
            _timeAlive = 0f;

            Texture2D softTex = FireFrontTextureGenerator.GenerateSoftFireGlow();
            _fireParticleMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(softTex, true);
            
            Texture2D streakTex = FireFrontTextureGenerator.GenerateFireStreak();
            _fireLineMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(streakTex, true);
            
            _sparksMat = FireFrontTextureGenerator.GetOrCreateFireMaterial(softTex, true);

            InitLines();
            InitParticles();
        }

        private void InitLines()
        {
            for (int i = 0; i < 5; i++)
            {
                GameObject lineObj = new GameObject("FireLine_" + i);
                lineObj.transform.SetParent(transform, false);
                var lr = lineObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.material = _fireLineMat;
                lr.positionCount = 8;
                lr.startWidth = _baseWidth;
                lr.endWidth = 0.05f;
                lr.startColor = new Color(1f, 0.7f, 0.2f, 0.8f);
                lr.endColor = new Color(1f, 0.2f, 0f, 0f);
                _flameLines[i] = lr;
            }
        }

        private void InitParticles()
        {
            GameObject psObj = new GameObject("FireParticles");
            psObj.transform.SetParent(transform, false);
            _particleSystem = psObj.AddComponent<ParticleSystem>();
            var main = _particleSystem.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.8f, 0.2f), new Color(1f, 0.3f, 0f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = _particleSystem.emission;
            emission.rateOverTime = 20f;

            var shape = _particleSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 15f;
            shape.radius = _targetBounds.extents.x;

            var renderer = psObj.GetComponent<ParticleSystemRenderer>();
            renderer.material = _fireParticleMat;
            
            // Sparks
            GameObject sparksObj = new GameObject("Sparks");
            sparksObj.transform.SetParent(transform, false);
            _sparksSystem = sparksObj.AddComponent<ParticleSystem>();
            var smain = _sparksSystem.main;
            smain.startLifetime = new ParticleSystem.MinMaxCurve(1f, 2f);
            smain.startSpeed = new ParticleSystem.MinMaxCurve(3f, 6f);
            smain.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
            smain.startColor = new Color(1f, 0.6f, 0.1f, 1f);
            smain.simulationSpace = ParticleSystemSimulationSpace.World;
            var semission = _sparksSystem.emission;
            semission.rateOverTime = 10f;
            var sshape = _sparksSystem.shape;
            sshape.shapeType = ParticleSystemShapeType.Cone;
            sshape.angle = 25f;
            sshape.radius = _targetBounds.extents.x;
            var srenderer = sparksObj.GetComponent<ParticleSystemRenderer>();
            srenderer.material = _sparksMat;
            
            _particleSystem.Play();
            _sparksSystem.Play();
        }

        private void Update()
        {
            _timeAlive += Time.deltaTime;
            float progress = Mathf.Clamp01(_timeAlive / _burnDuration);
            
            // Fire climbs up based on bounds height
            float currentHeight = Mathf.Lerp(0f, _targetBounds.size.y, progress);
            Vector3 basePos = transform.position;
            
            // Move particle emitters up as fire climbs
            if (_particleSystem != null)
            {
                _particleSystem.transform.position = basePos + Vector3.up * currentHeight;
            }
            if (_sparksSystem != null)
            {
                _sparksSystem.transform.position = basePos + Vector3.up * currentHeight;
            }

            // Animate line renderers to simulate dancing fire
            for (int i = 0; i < 5; i++)
            {
                UpdateFlameLine(_flameLines[i], i, basePos, currentHeight);
            }
        }

        private void UpdateFlameLine(LineRenderer lr, int index, Vector3 basePos, float climbHeight)
        {
            float t = Time.time * (2f + index * 0.5f);
            int segments = lr.positionCount;
            
            Vector3[] points = new Vector3[segments];
            for (int j = 0; j < segments; j++)
            {
                float segmentProgress = j / (float)(segments - 1); // 0 to 1
                float h = climbHeight * segmentProgress;
                
                // Spiral/Wobble math
                float angle = t + (j * 1.5f) + (index * Mathf.PI * 0.4f);
                float radius = _targetBounds.extents.x * (1f - segmentProgress) * 1.2f;
                
                float offsetX = Mathf.Cos(angle) * radius;
                float offsetZ = Mathf.Sin(angle) * radius;
                
                // Add some chaotic perlin noise
                offsetX += (Mathf.PerlinNoise(t + index, j * 0.5f) - 0.5f) * 0.5f;
                offsetZ += (Mathf.PerlinNoise(t - index, j * 0.5f) - 0.5f) * 0.5f;
                
                points[j] = basePos + new Vector3(offsetX, h, offsetZ);
            }
            
            lr.SetPositions(points);
        }
    }
}
