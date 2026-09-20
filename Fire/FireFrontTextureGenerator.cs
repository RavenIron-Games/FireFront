using System.Collections.Generic;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Textures and materials the fire is drawn with. Two sources, in this order of preference:
    /// vanilla's own fire materials CLONED from prefabs the game has already loaded (the exact
    /// shader, blend, flipbook and soft-particle setup fire_pit and bonfire use), and, when a
    /// donor cannot be found, procedural textures baked once and cached.
    /// </summary>
    /// <remarks>
    /// Why cloning rather than Shader.Find: the mod's own logs show Shader.Find missing
    /// "Custom/Particle (Unlit)" after world load — bundle-loaded shaders are simply not
    /// visible to it. A material read off a registered prefab carries a live shader reference
    /// and every property the shader needs; new Material(donor) copies all of it.
    ///
    /// Why donors are picked by EXACT child name: fire_pit's children in depth-first order are
    /// flare, smoke (1), low_flames, flames, sparcs (1), flames (1). "first whose name contains
    /// flame" landed on low_flames — an INACTIVE low-LOD emitter whose texture is an 8x8
    /// point-filtered grey blob — and tiling that along a LineRenderer is what the white
    /// streaks in the 0.21.x tester screenshot were. Read out of the 1.0.15 bundle 2026-09-20.
    ///
    /// Vanilla flame is not a flame picture. It is a greyscale flipbook sheet on
    /// "Custom/Gradient Mapped Particle (Unlit)", recoloured per particle by two HDR colours
    /// delivered through custom vertex streams. The flame controller reproduces that.
    ///
    /// Texture setup: the project is linear colour space, bloom is on, anisotropic filtering is
    /// ForceEnable. Colour sprites are sRGB with a full mip chain, trilinear, aniso 8; masks and
    /// normal maps are linear. The 0.21.x generator built 128x128 RGBA32 with mipChain:false,
    /// which shimmered at distance and opted out of the game's own filtering.
    /// </remarks>
    public static class FireFrontTextureGenerator
    {
        // --------------------------------------------------------------
        // Vanilla donor materials (cloned once, shared by every fire)
        // --------------------------------------------------------------

        private struct Donor
        {
            public string Prefab;
            public string Child;
            public Donor(string prefab, string child) { Prefab = prefab; Child = child; }
        }

        private static readonly Donor[] FlameDonors = { new Donor("fire_pit", "flames (1)"), new Donor("bonfire", "fx_BonfireFlames"), new Donor("piece_groundtorch", "fx_Torch_Basic") };
        private static readonly Donor[] EmberDonors = { new Donor("fire_pit", "sparcs (1)"), new Donor("bonfire", "Sparks") };
        private static readonly Donor[] SmokeDonors = { new Donor("bonfire", "Smoke"), new Donor("fire_pit", "smoke (1)") };
        private static readonly Donor[] GlowDonors = { new Donor("fire_pit", "flare"), new Donor("bonfire", "flare") };
        private static readonly Donor[] HazeDonors = { new Donor("BlobLava", "HeatDistort") };

        private static readonly Dictionary<string, Material> s_cloneCache = new Dictionary<string, Material>();
        private static readonly HashSet<string> s_missLogged = new HashSet<string>();

        /// <summary>
        /// The ParticleSystemRenderer of a named child under a registered prefab, or null. The
        /// prefab is fetched through ZNetScene's own dictionary, not a scan.
        /// </summary>
        public static ParticleSystemRenderer FindDonorRenderer(string prefabName, string childName)
        {
            if (ZNetScene.instance == null) return null;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) return null;
            ParticleSystemRenderer[] renderers = prefab.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].gameObject.name == childName) return renderers[i];
            }
            return null;
        }

        private static Material CloneDonor(Donor[] donors, string role)
        {
            if (s_cloneCache.TryGetValue(role, out Material cached) && cached != null) return cached;
            for (int i = 0; i < donors.Length; i++)
            {
                ParticleSystemRenderer r = FindDonorRenderer(donors[i].Prefab, donors[i].Child);
                if (r == null || r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                Material clone = new Material(r.sharedMaterial) { name = "FireFront_" + role };
                s_cloneCache[role] = clone;
                FireLogger.Info($"[SHADER-DIAG] {role} material cloned from {donors[i].Prefab}/{donors[i].Child} " +
                                $"(\"{r.sharedMaterial.name}\", shader \"{r.sharedMaterial.shader.name}\").");
                return clone;
            }
            if (s_missLogged.Add(role))
            {
                FireLogger.Warn($"[SHADER-DIAG] no vanilla donor found for the {role} material; falling back to a procedural one.");
            }
            return null;
        }

        /// <summary>The donor ParticleSystem for a role, so the controller can copy its flipbook grid and streams.</summary>
        public static ParticleSystem FindDonorSystem(string role)
        {
            Donor[] donors = role == "flame" ? FlameDonors : role == "ember" ? EmberDonors : role == "smoke" ? SmokeDonors : role == "glow" ? GlowDonors : HazeDonors;
            for (int i = 0; i < donors.Length; i++)
            {
                ParticleSystemRenderer r = FindDonorRenderer(donors[i].Prefab, donors[i].Child);
                if (r != null) return r.GetComponent<ParticleSystem>();
            }
            return null;
        }

        /// <summary>Vanilla's gradient-mapped flipbook flame material, or the procedural additive fallback.</summary>
        public static Material GetOrCreateFlameMaterial()
        {
            Material m = CloneDonor(FlameDonors, "flame");
            if (m != null) return m;
            return GetOrCreateFireMaterial(GetOrCreateFlameFlipbook(), true);
        }

        /// <summary>True when the flame material is vanilla's gradient-mapped one (needs custom vertex streams).</summary>
        public static bool FlameMaterialIsGradientMapped()
        {
            Material m = GetOrCreateFlameMaterial();
            return m != null && m.shader != null && m.shader.name.Contains("Gradient Mapped");
        }

        public static Material GetOrCreateEmberMaterial()
        {
            Material m = CloneDonor(EmberDonors, "ember");
            if (m != null) return m;
            return GetOrCreateFireMaterial(GenerateSoftFireGlow(), true);
        }

        public static Material GetOrCreateSmokeMaterial()
        {
            Material m = CloneDonor(SmokeDonors, "smoke");
            if (m != null) return m;
            return GetOrCreateFireMaterial(GenerateSmokeTexture(), false);
        }

        public static Material GetOrCreateGlowMaterial()
        {
            Material m = CloneDonor(GlowDonors, "glow");
            if (m != null) return m;
            return GetOrCreateFireMaterial(GenerateSoftFireGlow(), true);
        }

        /// <summary>The lava heat-haze refraction material; null when the game has no donor (then there is simply no haze).</summary>
        public static Material GetOrCreateHazeMaterial() => CloneDonor(HazeDonors, "haze");

        /// <summary>Kept for callers that already resolve their own texture (ground fire, scorch decals).</summary>
        public static Material GetOrCreateFireMaterial(Texture2D tex, bool isAdditive = true)
        {
            if (isAdditive)
            {
                Material additive = ValheimBridge.CreateAdditiveParticleMaterial(tex, "FireFrontTextureGenerator");
                if (additive != null) return additive;
            }

            Shader shader = ValheimBridge.ResolveParticleShader();
            if (shader == null)
            {
                FireLogger.Warn("[SHADER-DIAG] FireFrontTextureGenerator: no usable particle shader in this build; " +
                                "this effect will not be drawn.");
                return null;
            }

            Material mat = new Material(shader);
            mat.mainTexture = tex;
            if (isAdditive && mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3);
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

        // --------------------------------------------------------------
        // Procedural textures (baked once, cached, full mip chains)
        // --------------------------------------------------------------

        private static Texture2D s_softGlow;
        private static Texture2D s_smoke;
        private static Texture2D s_flipbook;
        private static Texture2D s_emberMask;

        /// <summary>sRGB colour sprite: mips on, trilinear, anisotropic, clamped edges.</summary>
        private static Texture2D NewColorTexture(int size, bool repeat = false)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            return tex;
        }

        /// <summary>Linear data texture (masks, normals): same filtering, no sRGB curve.</summary>
        private static Texture2D NewDataTexture(int size, bool repeat)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            return tex;
        }

        private static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, freq = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Mathf.PerlinNoise(x * freq, y * freq) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return sum / norm;
        }

        public static Texture2D GenerateSoftFireGlow()
        {
            if (s_softGlow != null) return s_softGlow;
            const int size = 128;
            Texture2D tex = NewColorTexture(size);
            var px = new Color32[size * size];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Pow(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - d)), 1.2f);
                    // Falloff baked into RGB too, so an additive shader reading any channel gets soft edges.
                    byte b = (byte)(a * 255f);
                    px[y * size + x] = new Color32(b, (byte)(b * 0.9f), (byte)(b * 0.8f), b);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            s_softGlow = tex;
            return tex;
        }

        /// <summary>
        /// A greyscale 8x8 flame flipbook in the layout vanilla's own sheet uses, for the case
        /// where no vanilla flame material can be cloned. Each frame is a rising, narrowing
        /// tongue with fbm turbulence advected upward over the sequence.
        /// </summary>
        public static Texture2D GetOrCreateFlameFlipbook()
        {
            if (s_flipbook != null) return s_flipbook;
            const int frames = 8, cell = 64, size = frames * cell;
            Texture2D tex = NewColorTexture(size);
            var px = new Color32[size * size];
            for (int fy = 0; fy < frames; fy++)
            {
                for (int fx = 0; fx < frames; fx++)
                {
                    int frame = fy * frames + fx;
                    float t = frame / (float)(frames * frames);
                    for (int y = 0; y < cell; y++)
                    {
                        for (int x = 0; x < cell; x++)
                        {
                            float u = (x + 0.5f) / cell - 0.5f;     // -0.5..0.5 across
                            float v = (y + 0.5f) / cell;            // 0 bottom .. 1 top
                            float width = Mathf.Lerp(0.42f, 0.06f, Mathf.Pow(v, 1.3f));
                            float n = Fbm(u * 4f + frame * 0.37f, v * 3f - t * 6f, 3) - 0.5f;
                            float edge = Mathf.Abs(u + n * 0.22f * v) / width;
                            float body = Mathf.Clamp01(1f - edge);
                            float tip = Mathf.SmoothStep(1f, 0f, Mathf.Clamp01((v - 0.75f) / 0.25f));
                            float foot = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(v / 0.08f));
                            float a = Mathf.Pow(body, 1.6f) * tip * foot * (0.7f + 0.3f * n + 0.3f);
                            a = Mathf.Clamp01(a);
                            byte b = (byte)(a * 255f);
                            int ix = fx * cell + x, iy = (frames - 1 - fy) * cell + y;
                            px[iy * size + ix] = new Color32(b, b, b, b);
                        }
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            s_flipbook = tex;
            return tex;
        }

        public static Texture2D GenerateSmokeTexture()
        {
            if (s_smoke != null) return s_smoke;
            const int size = 128;
            Texture2D tex = NewColorTexture(size);
            var px = new Color32[size * size];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float falloff = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - d));
                    float n = Fbm(x * 0.05f, y * 0.05f, 3);
                    float a = falloff * n * 0.7f;
                    px[y * size + x] = new Color32(52, 50, 48, (byte)(Mathf.Clamp01(a) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            s_smoke = tex;
            return tex;
        }

        /// <summary>
        /// The ember-crack mask a charred trunk glows through: Custom/Vegetation computes
        /// emission as _EmissiveTex.rgb * _EmissiveTex.a * _EmissionColor, so the cracks live in
        /// alpha and carry a warm colour in RGB. Tileable "alligator" char plates: a jittered
        /// cell grid whose boundaries are the cracks, with a slow fbm so no two plates match.
        /// Linear, tiles 4x over the trunk UVs via the material's own _MainTex_ST.
        /// </summary>
        public static Texture2D GetOrCreateEmberCrackMask()
        {
            if (s_emberMask != null) return s_emberMask;
            const int size = 256;
            const int cellsX = 6, cellsY = 10;
            Texture2D tex = NewDataTexture(size, true);
            var px = new Color32[size * size];

            // Jittered cell centres on the torus so the pattern tiles.
            var centres = new Vector2[cellsX * cellsY];
            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    float jx = Mathf.PerlinNoise(cx * 3.1f + 0.7f, cy * 2.3f + 1.9f) - 0.5f;
                    float jy = Mathf.PerlinNoise(cx * 2.7f + 4.2f, cy * 3.7f + 0.3f) - 0.5f;
                    centres[cy * cellsX + cx] = new Vector2((cx + 0.5f + jx * 0.6f) / cellsX, (cy + 0.5f + jy * 0.6f) / cellsY);
                }
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    // Distance to nearest and second-nearest centre (toroidal): the crack is where they tie.
                    float d1 = float.MaxValue, d2 = float.MaxValue;
                    for (int i = 0; i < centres.Length; i++)
                    {
                        float dx = Mathf.Abs(u - centres[i].x); if (dx > 0.5f) dx = 1f - dx;
                        float dy = Mathf.Abs(v - centres[i].y); if (dy > 0.5f) dy = 1f - dy;
                        dx *= cellsX; dy *= cellsY;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d < d1) { d2 = d1; d1 = d; }
                        else if (d < d2) d2 = d;
                    }
                    float edge = d2 - d1;                                  // 0 on a crack, grows into the plate
                    float crackWidth = 0.10f + 0.06f * Fbm(u * 9f, v * 9f, 2);
                    float crack = 1f - Mathf.SmoothStep(0f, crackWidth, edge);
                    float glow = crack * (0.55f + 0.45f * Fbm(u * 5f + 3f, v * 5f + 7f, 3));
                    // A few plates glow faintly from inside too, so the trunk is not black between cracks.
                    float plateGlow = Mathf.Clamp01(Fbm(u * 3f + 11f, v * 3f + 5f, 2) - 0.62f) * 1.2f;
                    float a = Mathf.Clamp01(glow + plateGlow);
                    px[y * size + x] = new Color32(255, (byte)(90 + 110 * a), (byte)(20 + 30 * a), (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            s_emberMask = tex;
            return tex;
        }

        // --------------------------------------------------------------
        // Legacy entry points kept for the ground-fire / scorch callers
        // --------------------------------------------------------------

        public static Texture2D GenerateFireStreak() => GetOrCreateFlameFlipbook();

        /// <summary>
        /// Left in place for external callers; internally the fire no longer reads a "flame"
        /// texture off a prefab by substring, because the first match was an inactive 8x8
        /// blob (see the class remarks). Exact-name donors are the supported path.
        /// </summary>
        public static Texture2D BorrowVanillaTexture(string emitterKeyword)
        {
            string[] donors = { "fire_pit", "bonfire", "piece_groundtorch" };
            for (int d = 0; d < donors.Length; d++)
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(donors[d]) : null;
                if (prefab == null) continue;
                ParticleSystemRenderer[] renderers = prefab.GetComponentsInChildren<ParticleSystemRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    ParticleSystemRenderer r = renderers[i];
                    if (r == null || r.sharedMaterial == null || r.sharedMaterial.mainTexture == null) continue;
                    if (!r.gameObject.activeSelf) continue; // never the low-LOD stand-ins
                    if (r.gameObject.name.ToLower().Contains(emitterKeyword.ToLower()) && r.sharedMaterial.mainTexture is Texture2D t2d)
                        return t2d;
                }
            }
            return null;
        }
    }
}
