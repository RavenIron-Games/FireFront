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

        /// <summary>
        /// The ground scorch decal: vanilla's unlit particle material (the ember donor's
        /// "Custom/Particle (Unlit)") re-blended to MULTIPLY (framebuffer times the blot's red
        /// channel, then fogged), so the mark darkens whatever is already lit on the terrain -
        /// grass detail, sun, shadow and the fire's own light survive - instead of painting an
        /// umber disc over it. Its own clone, never the ember material (that one is additive
        /// and shared by every spark system). Null when no donor exists; the caller then keeps
        /// the alpha-blended fallback.
        /// </summary>
        /// <remarks>
        /// Every value below comes from the shader's compiled GLSL (1.0.15 bundle, dumped
        /// 2026-09-20: libs-Tools/1.0/ASSET-DATA/shaders/particle_unlit_frag.glsl), because the
        /// property names lie about what the fragment does:
        ///
        /// The fragment NEVER writes the texture's RGB. Its colour is the vertex colour
        /// (`rgb = _SrcBlend == 3 ? a * COLOR.rgb : COLOR.rgb`), and the texture contributes ONE
        /// channel, picked by _AlphaChannel (0 R, 1 G, 2 B, 3 A), into alpha:
        /// `a = tex[_AlphaChannel] * COLOR.a`. So "Blend DstColor Zero" (0.22.1) multiplied the
        /// ground by the vertex colour - white on a quad without a colour stream - and drew
        /// nothing at all; the soot texture was ignored. The multiply has to come from the
        /// destination factor instead: DstBlend = SrcAlpha gives framebuffer * a, and with
        /// _AlphaChannel = Red, a IS the blot: 1 at the rim, 0.30 at the heart as stored (sRGB,
        /// so the sampler hands the shader 0.07 - which, displayed, is the ground at about 30 %
        /// of its brightness; G and B carry a warm tint and are never read). The donor's
        /// _AlphaChannel = 3 would read our alpha-255 texture as a = 1 everywhere.
        ///
        /// Fog, and why the vertices are BLACK and the source factor is OneMinusSrcAlpha rather
        /// than Zero: the ground under the mark is already fogged when this pass runs (deferred
        /// opaques are fogged before transparents), so a bare framebuffer * a scales the fog
        /// term too, and at fog range every mark is a black blob on grey. The shader's own fog
        /// (`rgb = _FejdFog ? lerp(COLOR.rgb, fogRGB, f) : COLOR.rgb`, the same term every
        /// vanilla particle uses) fixes it exactly when COLOR.rgb is black: the fragment's RGB
        /// is then f * fog, and Blend OneMinusSrcAlpha SrcAlpha gives
        ///   (1 - a) * f * fog + a * ((1 - f) * lit + f * fog) = a * (1 - f) * lit + f * fog,
        /// the multiply applied under the fog instead of on top of it; with f = 0 it is
        /// framebuffer * a again. So `_FejdFog` is ON, the quad's vertex colour is black, and
        /// the `_SrcBlend == 3` premultiply branch stays off (10 != 3).
        ///
        /// Everything else in the shader can only SHRINK that alpha, and every one of them is
        /// a fade toward "no mark", so each is forced to 1:
        ///  - _SkyMask (donor: ON): `a = min(a, skyFactor)`, where skyFactor is 0 for anything
        ///    farther from DepthCamera (a top-down depth render 50 m above the player, once a
        ///    second) than the first occluder it saw. Under a canopy or a roof the ground is
        ///    exactly that, so the mark would vanish in a forest. Off.
        ///  - Soft particles: only compiled under the GLOBAL keyword SOFTPARTICLES_ON
        ///    (Valheim's Default quality has softParticles on), never under _SoftParticles,
        ///    which is not a uniform in any variant. `a *= saturate((sceneZ - fragZ -
        ///    _SoftNearFade) * _SoftFadeFactor)`: the donor's nearFade 0.5 m against a decal
        ///    4 cm off the ground is a negative bracket, alpha 0. _SoftNearFade = -1000 makes
        ///    the bracket >= 1000 whatever the depth gap, times factor 1, saturates to 1 - and
        ///    that holds even if the material-level DisableKeyword loses to the global.
        ///  - _CameraFadeFactor (donor 0.2): `a *= saturate(eyeDepth * factor)`, an ease-in
        ///    over the first 1/factor metres from the camera - the mark would fade out as the
        ///    player walks up to it. 0 would kill it outright; 1000 saturates past 1 mm.
        ///  - _FejdFog: on, see above (the fog keyword variants are byte-identical to the
        ///    no-keyword one - fog is gated by this uniform alone).
        ///  - _Color / _TintColor: the shader declares neither, so those writes are no-ops
        ///    today. They stay because the donor material carries stale saved values from an
        ///    earlier shader (_TintColor alpha 0.5), and a future donor on a shader that does
        ///    declare them would inherit a half-strength tint silently.
        /// Vertex colour: COLOR.a scales the alpha and COLOR.rgb is the fog carrier, so the
        /// quad mesh must reach the shader with black, alpha 1 - ValheimBridge builds that
        /// quad (GetOrCreateQuadMesh(blackVertices: true)) for any material carrying
        /// <see cref="ScorchMultiplyMaterialName"/>; nothing rides on Unity's missing-stream
        /// default.
        /// </remarks>
        /// <summary>The multiply decal's material name; ValheimBridge picks the black-vertex quad by it.</summary>
        public const string ScorchMultiplyMaterialName = "FireFront_scorch";

        public static Material GetOrCreateScorchMultiplyMaterial(Texture2D soot)
        {
            if (s_cloneCache.TryGetValue("scorch", out Material cached) && cached != null) return cached;
            Material ember = CloneDonor(EmberDonors, "ember");
            if (ember == null || ember.shader == null || ember.shader.name != "Custom/Particle (Unlit)") return null;
            Material m = new Material(ember) { name = ScorchMultiplyMaterialName };
            m.SetTexture("_MainTex", soot);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); // out = (1-a) * f*fog + a * dst: the multiply, fogged
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_AlphaChannel", 0f);                                            // a = soot.R: the blot's grey, 1 outside, ~0.3 at the heart
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);                        // a decal on a slope is seen from either side of its own plane
            if (m.HasProperty("_SkyMask")) m.SetFloat("_SkyMask", 0f);                  // DepthCamera occlusion: hides the ground itself under any canopy
            if (m.HasProperty("_SoftParticles")) m.SetFloat("_SoftParticles", 0f);      // inspector toggle only; the real gate is the global keyword below
            m.DisableKeyword("SOFTPARTICLES_ON");
            if (m.HasProperty("_SoftNearFade")) m.SetFloat("_SoftNearFade", -1000f);    // depth-gap fade saturates to 1 for any gap, in case the keyword stays on
            if (m.HasProperty("_SoftFadeFactor")) m.SetFloat("_SoftFadeFactor", 1f);
            if (m.HasProperty("_CameraFadeFactor")) m.SetFloat("_CameraFadeFactor", 1000f); // near-camera fade saturates past 1 mm; 0 would fade the mark to nothing
            if (m.HasProperty("_FejdFog")) m.SetFloat("_FejdFog", 1f);                  // the fog term rides on the black vertex colour; see the remarks
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);             // not declared by this shader (no-op); insurance against a tinted future donor
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", Color.white);
            m.renderQueue = 2950; // after every opaque and alpha-tested thing on the ground, before the game's transparent effects
            s_cloneCache["scorch"] = m;
            FireLogger.Info($"[SHADER-DIAG] scorch decal material: \"{m.shader.name}\" from the ember donor, " +
                            $"blend {m.GetFloat("_SrcBlend"):0}/{m.GetFloat("_DstBlend"):0} (OneMinusSrcAlpha/SrcAlpha = framebuffer x soot.R under the fog), " +
                            $"alphaChannel {m.GetFloat("_AlphaChannel"):0}, cull {m.GetFloat("_Cull"):0}, skyMask {m.GetFloat("_SkyMask"):0}, " +
                            $"softParticles {m.GetFloat("_SoftParticles"):0} (SOFTPARTICLES_ON {(m.IsKeywordEnabled("SOFTPARTICLES_ON") ? "on" : "off")}, " +
                            $"nearFade {m.GetFloat("_SoftNearFade"):0}, fadeFactor {m.GetFloat("_SoftFadeFactor"):0}), " +
                            $"cameraFadeFactor {m.GetFloat("_CameraFadeFactor"):0}, fejdFog {m.GetFloat("_FejdFog"):0}, " +
                            $"tint {(m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "n/a")}/{(m.HasProperty("_TintColor") ? m.GetColor("_TintColor").ToString() : "n/a")}, " +
                            $"queue {m.renderQueue}.");
            return m;
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
