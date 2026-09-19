using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    public static class FireFrontTextureGenerator
    {
        private const int Size = 128;

        public static Texture2D GenerateSoftFireGlow()
        {
            Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            Vector2 center = new Vector2(Size / 2f, Size / 2f);
            float maxDist = Size / 2f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float dist = Vector2.Distance(p, center) / maxDist;
                    float a = Mathf.Clamp01(1f - dist);
                    a = Mathf.SmoothStep(0f, 1f, a);
                    a = Mathf.Pow(a, 1.2f); // Softer than rune

                    // Fire base color (white/yellowish with transparency falloff)
                    Color c = new Color(1f, 0.9f, 0.8f, a);
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply(true);
            return tex;
        }

        public static Texture2D GenerateFireStreak()
        {
            Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            Vector2 center = new Vector2(Size / 2f, Size / 2f);
            float maxDist = Size / 2f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float along = Mathf.Abs((y + 0.5f) - center.y) / maxDist;
                    float across = Mathf.Abs((x + 0.5f) - center.x) / (maxDist * 0.4f);

                    float a = Mathf.Clamp01(1f - across);
                    float lengthFade = Mathf.SmoothStep(1f, 0f, along);
                    a *= Mathf.Pow(lengthFade, 0.8f);
                    
                    // Add some noise simulation
                    float noise = Mathf.PerlinNoise(x * 0.1f, y * 0.3f);
                    a *= (0.5f + noise * 0.5f);

                    Color c = new Color(1f, 0.6f, 0.1f, a);
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply(true);
            return tex;
        }

        public static Texture2D GenerateSmokeTexture()
        {
            Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            Vector2 center = new Vector2(Size / 2f, Size / 2f);
            float maxDist = Size / 2f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float dist = Vector2.Distance(p, center) / maxDist;
                    float falloff = Mathf.Clamp01(1f - dist);
                    falloff = Mathf.SmoothStep(0f, 1f, falloff);

                    float n = Mathf.PerlinNoise(x * 0.08f, y * 0.08f) * 0.6f +
                              Mathf.PerlinNoise(x * 0.16f + 10f, y * 0.16f + 10f) * 0.4f;
                    float a = falloff * n * 0.6f;

                    Color c = new Color(0.2f, 0.2f, 0.2f, a);
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply(true);
            return tex;
        }
        
        public static Material GetOrCreateFireMaterial(Texture2D tex, bool isAdditive = true)
        {
            // Shader.Find("Particles/Standard Unlit") CANNOT work here, and neither can
            // "Particles/Alpha Blended". Valheim strips both, along with Particles/Standard Surface
            // and both Legacy Shaders/Particles variants - this repo settled it by parsing the
            // ScriptMapper in globalgamemanagers rather than by guessing, and wrote the results up
            // in ValheimBridge.FindUsableParticleShader. A name-based Find returns null, and a
            // Material built on a null shader draws nothing at all.
            //
            // Additive is what makes a mass of particles read as fire instead of as separate orange
            // discs, so that is tried first; the alpha chain is the fallback, because losing the
            // look is survivable and rendering nothing is not.
            if (isAdditive)
            {
                Material additive = ValheimBridge.CreateAdditiveParticleMaterial(tex, "FireFrontTextureGenerator");
                if (additive != null) return additive;
            }

            Shader shader = ValheimBridge.ResolveParticleShader();
            if (shader == null)
            {
                // Nothing usable in this build. Returning null is deliberate: a Material built
                // on a null shader is not an error Unity reports, it just draws nothing, and a
                // silent invisible fire is far harder to diagnose than an absent one.
                FireLogger.Warn("[SHADER-DIAG] FireFrontTextureGenerator: no usable particle shader in this build; " +
                                "this effect will not be drawn.");
                return null;
            }
            
            Material mat = new Material(shader);
            mat.mainTexture = tex;
            
            if (isAdditive && mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3); // Additive
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }
            return mat;
        }
    }
}
