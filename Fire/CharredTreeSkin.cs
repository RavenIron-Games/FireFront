using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Turns a live tree, log or stub into its charred twin purely by re-skinning: the game
    /// ships no burnt tree prefab of any species (every Charred* asset is an Ashlands enemy
    /// or piece), so "a charred beech" is a beech whose materials say so.
    /// </summary>
    /// <remarks>
    /// Facts this leans on, read out of the 1.0.15 bundles rather than guessed (2026-09-20):
    ///
    /// Every standing tree material is Custom/Vegetation. Its only keyword is _USEMETALMAP_ON;
    /// emission (_EmissiveTex.rgb * .a * _EmissionColor) and the normal map need none, so
    /// nothing here has to EnableKeyword. Alpha test runs in the forward AND the shadow
    /// pass, so a foliage material with _Color.a = 0 and _Cutoff above 1 discards every leaf
    /// card and its shadow, which is how the crown disappears without touching a mesh. Bark
    /// materials of Birch/Beech/Oak/Ygga are separate from their leaf materials, but the SLOT
    /// ORDER swaps between LOD0 and LOD1, so materials are classified by name, never index.
    /// Pine and Fir use one atlas for trunk and needles; vanilla's own PineTree_01_dead atlas
    /// (on Pinetree_Snow_dead) is the same layout with the needle half at alpha 0, and
    /// FirTree_small_dead carries a dead-frond atlas for the fir layout.
    ///
    /// All tree materials have GPU instancing on and are shared world-wide. renderer.materials
    /// (what the first draft used) instantiates a private copy per slot per renderer per tree
    /// and Unity never frees them; sharedMaterials[i].SetColor would recolour every tree of that
    /// species in the world. So: ONE cached clone per vanilla material, assigned by identity,
    /// and per-tree animation (the ember fade) through a MaterialPropertyBlock, which reaches
    /// every declared property including textures and never allocates a Material.
    ///
    /// Stubs and logs of Birch/Beech/Oak/Ygga use Unity's Standard shader instead, where
    /// emission is dead without the _EMISSION keyword; that is enabled on the clone.
    /// </remarks>
    public static class CharredTreeSkin
    {
        private static readonly int P_MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int P_BumpMap = Shader.PropertyToID("_BumpMap");
        private static readonly int P_BumpScale = Shader.PropertyToID("_BumpScale");
        private static readonly int P_Color = Shader.PropertyToID("_Color");
        private static readonly int P_Cutoff = Shader.PropertyToID("_Cutoff");
        private static readonly int P_Glossiness = Shader.PropertyToID("_Glossiness");
        private static readonly int P_Metallic = Shader.PropertyToID("_Metallic");
        private static readonly int P_MossTex = Shader.PropertyToID("_MossTex");
        private static readonly int P_MossTexST = Shader.PropertyToID("_MossTex_ST");
        private static readonly int P_MossAlpha = Shader.PropertyToID("_MossAlpha");
        private static readonly int P_MossBlend = Shader.PropertyToID("_MossBlend");
        private static readonly int P_MossNormal = Shader.PropertyToID("_MossNormal");
        private static readonly int P_MossTransition = Shader.PropertyToID("_MossTransition");
        private static readonly int P_EmissiveTex = Shader.PropertyToID("_EmissiveTex");
        private static readonly int P_EmissionMap = Shader.PropertyToID("_EmissionMap");
        private static readonly int P_EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int P_SwayDistance = Shader.PropertyToID("_SwayDistance");
        private static readonly int P_RippleDistance = Shader.PropertyToID("_RippleDistance");

        public static readonly int EmissionColorId = P_EmissionColor;
        public static readonly int EmissiveTexId = P_EmissiveTex;
        public static readonly int ColorId = P_Color;

        private const string VegetationShader = "Custom/Vegetation";

        /// <summary>Leaf-card materials, by vanilla asset name. Anything else named like foliage is caught by the heuristic below.</summary>
        private static readonly HashSet<string> Foliage = new HashSet<string>
        {
            "birch_leaf", "birch_leaf_aut", "beech_leaf", "oak_leaf", "Shoot_Leaf_mat", "swamptree1_branch",
        };

        /// <summary>Charcoal: near-black with a hint of warm brown so the vanilla bark grain still reads through the tint (fallback look, when the bark could not be read back).</summary>
        public static readonly Color CharTint = new Color(0.10f, 0.09f, 0.08f, 1f);
        /// <summary>Tint over the GENERATED charred albedo, which is already black: near-white so the generator's tones are what is drawn.</summary>
        private static readonly Color GeneratedTint = new Color(0.96f, 0.94f, 0.92f, 1f);

        /// <summary>
        /// Peak ember emission in HDR. 1.0 on the dial is roughly vanilla's Ashlands tree value
        /// (AshlandsTrees_mat._EmissionColor = 4.78, on a mask of pin-points); the default 0.4
        /// sits just over the bloom threshold, so only the cores bloom.
        /// </summary>
        public static float EmberPeakHdr => 4f * Mathf.Clamp(FireConfig.CharredEmberIntensity.Value, 0f, 2f);
        /// <summary>Fresh embers: the mask carries the orange→yellow hue, this carries the heat.</summary>
        public static Color EmberHot => new Color(1f, 0.42f, 0.10f, 1f) * EmberPeakHdr;
        /// <summary>Cooling embers: deeper red, less than half the heat.</summary>
        public static Color EmberCool => new Color(1f, 0.20f, 0.03f, 1f) * (EmberPeakHdr * 0.55f);

        private static readonly Dictionary<Material, Material> s_clones = new Dictionary<Material, Material>();
        private static Texture s_ash;
        private static Texture s_deadPine;
        private static Texture s_deadFir;
        private static Texture s_emberMask;
        private static bool s_donorsResolved;
        private static bool s_donorFailureLogged;

        /// <summary>Per-material-index property block scratch; blocks are set per renderer slot so a leaf/bark pair never share one.</summary>
        private static readonly MaterialPropertyBlock s_mpb = new MaterialPropertyBlock();

        /// <summary>
        /// Textures the char look borrows from assets the game already has loaded. Resolved
        /// once; a miss just means that layer is skipped, never that the tree stays green.
        /// </summary>
        private static void EnsureDonors()
        {
            if (s_donorsResolved) return;
            s_donorsResolved = true;
            try
            {
                s_ash = FindMaterialTexture("AshlandsTree1", "AshlandsTrees_mat", P_MossTex);      // 1024² tileable ash
                s_deadPine = FindMaterialTexture("Pinetree_Snow_dead", "PineTree_01_dead", P_MainTex); // pine atlas, needles alpha 0
                s_deadFir = FindMaterialTexture("FirTree_small_dead", "Pine_tree_small_dead", P_MainTex); // fir atlas, dead fronds
            }
            catch (System.Exception ex)
            {
                if (!s_donorFailureLogged)
                {
                    s_donorFailureLogged = true;
                    FireLogger.Warn($"[CHARRED] donor texture lookup threw ({ex.Message}); charred trees use tint only.");
                }
            }
            if (FireVFXController.GraphicsAvailable)
            {
                try { s_emberMask = CharredTextures.EmberMask(0); }
                catch (System.Exception ex) { FireLogger.Warn($"[CHARRED] ember mask generation threw ({ex.Message}); charred trees will not glow."); }
            }
            FireLogger.Debug($"[CHARRED] donors: ash={(s_ash != null)}, deadPine={(s_deadPine != null)}, deadFir={(s_deadFir != null)}, emberMask={(s_emberMask != null)}");
        }

        private static Texture FindMaterialTexture(string prefabName, string materialName, int property)
        {
            Material m = FindVanillaMaterial(prefabName, materialName);
            if (m == null || !m.HasProperty(property)) return null;
            return m.GetTexture(property);
        }

        /// <summary>A registered prefab's material by asset name. Direct dictionary lookup, no reflection scan.</summary>
        public static Material FindVanillaMaterial(string prefabName, string materialName)
        {
            if (ZNetScene.instance == null) return null;
            GameObject go = ZNetScene.instance.GetPrefab(prefabName);
            if (go == null) return null;
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] mats = renderers[i].sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    if (mats[j] != null && mats[j].name == materialName) return mats[j];
                }
            }
            return null;
        }

        private static bool IsFoliage(Material m)
        {
            if (Foliage.Contains(m.name)) return true;
            string n = m.name.ToLowerInvariant();
            return n.Contains("leaf") || n.Contains("branch") || n.Contains("needle");
        }

        /// <summary>
        /// The surface of a char: a generated charred albedo (from the species' own bark) and a
        /// normal carrying the char relief over the bark's grain. When the bark cannot be read
        /// back (no graphics device, or an unexpected format) the vanilla textures stay and a
        /// dark tint plus a pushed bump scale approximate it.
        /// </summary>
        private static void ApplyCharSurface(Material c, Texture srcAlbedo, Texture srcNormal, Color fallbackTint)
        {
            Texture2D charAlbedo = CharredTextures.CharredAlbedoFor(srcAlbedo);
            if (charAlbedo != null)
            {
                c.SetTexture(P_MainTex, charAlbedo);
                if (c.HasProperty(P_Color)) c.SetColor(P_Color, GeneratedTint);
            }
            else
            {
                if (srcAlbedo != null && c.HasProperty(P_MainTex)) c.SetTexture(P_MainTex, srcAlbedo);
                if (c.HasProperty(P_Color)) c.SetColor(P_Color, fallbackTint);
            }

            Texture2D charNormal = c.HasProperty(P_BumpMap) ? CharredTextures.CharredNormalFor(srcNormal) : null;
            if (charNormal != null)
            {
                c.SetTexture(P_BumpMap, charNormal);
                if (c.HasProperty(P_BumpScale)) c.SetFloat(P_BumpScale, 1.25f);
                // Standard reads its normal map only under _NORMALMAP; a stub or log whose vanilla
                // material had none would otherwise ignore the relief. Custom/Vegetation has no
                // such keyword (the whole shader has only _USEMETALMAP_ON).
                if (c.shader != null && c.shader.name.StartsWith("Standard")) c.EnableKeyword("_NORMALMAP");
            }
            else if (c.HasProperty(P_BumpScale))
            {
                // The vanilla bark normal, pushed harder: ridges become fissures.
                c.SetFloat(P_BumpScale, 1.8f);
            }
        }

        /// <summary>
        /// The charred twin of a vanilla material, built once and shared by every charred
        /// tree of that species. Never mutates the source.
        /// </summary>
        public static Material CharredCloneOf(Material src)
        {
            if (src == null) return null;
            if (s_clones.TryGetValue(src, out Material cached) && cached != null) return cached;
            EnsureDonors();

            Material c = new Material(src) { name = src.name + "_charred" };
            bool vegetation = src.shader != null && src.shader.name == VegetationShader;

            if (vegetation && IsFoliage(src))
            {
                // Alpha test in both passes: every fragment of every leaf card is discarded,
                // shadows included. No mesh edit, no renderer toggling, LOD-proof.
                c.SetColor(P_Color, new Color(0f, 0f, 0f, 0f));
                c.SetFloat(P_Cutoff, 2f);
            }
            else if (vegetation)
            {
                // Atlas species: trunk and needles share one material, so the needles are
                // removed by swapping in vanilla's own dead atlas (same UV layout).
                Texture srcAlbedo = src.HasProperty(P_MainTex) ? src.GetTexture(P_MainTex) : null;
                if (src.name == "PineTree_01" && s_deadPine != null) srcAlbedo = s_deadPine;
                else if (src.name == "Pine_tree" && s_deadFir != null) srcAlbedo = s_deadFir;

                ApplyCharSurface(c, srcAlbedo, src.HasProperty(P_BumpMap) ? src.GetTexture(P_BumpMap) : null, CharTint);
                if (c.HasProperty(P_Glossiness)) c.SetFloat(P_Glossiness, 0.08f); // charcoal plates: faint glassy sheen
                if (c.HasProperty(P_Metallic)) c.SetFloat(P_Metallic, 0f);

                // Ash dusting rides the shader's moss layer: grey only on upward faces, and
                // the shader zeroes smoothness where it sits. _MossBlend must be 0 here — it
                // is luminance-driven and a black albedo would saturate it.
                if (s_ash != null && c.HasProperty(P_MossTex))
                {
                    c.SetTexture(P_MossTex, s_ash);
                    c.SetVector(P_MossTexST, new Vector4(3f, 3f, 0f, 0f));
                    c.SetFloat(P_MossAlpha, 0.5f);
                    c.SetFloat(P_MossBlend, 0f);
                    c.SetFloat(P_MossNormal, 0.35f);
                    c.SetFloat(P_MossTransition, 0.15f);
                }

                // Ember cracks. A null _EmissiveTex binds Unity's default WHITE texture and the
                // whole tree would glow uniformly, so the mask is always assigned.
                if (c.HasProperty(P_EmissiveTex) && s_emberMask != null) c.SetTexture(P_EmissiveTex, s_emberMask);
                c.SetColor(P_EmissionColor, Color.black); // per-tree value comes from the property block

                // Dead wood does not sway like a live crown.
                if (c.HasProperty(P_SwayDistance)) c.SetFloat(P_SwayDistance, src.GetFloat(P_SwayDistance) * 0.25f);
                if (c.HasProperty(P_RippleDistance)) c.SetFloat(P_RippleDistance, 0f);
            }
            else
            {
                // Standard (Birch/Beech/Oak/Ygga stubs and logs) or Custom/StaticRock (swamp log).
                ApplyCharSurface(c, src.HasProperty(P_MainTex) ? src.GetTexture(P_MainTex) : null,
                                 src.HasProperty(P_BumpMap) ? src.GetTexture(P_BumpMap) : null, new Color(0.09f, 0.08f, 0.075f, 1f));
                if (c.HasProperty(P_Glossiness)) c.SetFloat(P_Glossiness, 0.08f);
                if (c.HasProperty(P_EmissionMap) && s_emberMask != null)
                {
                    c.EnableKeyword("_EMISSION");
                    c.SetTexture(P_EmissionMap, s_emberMask);
                    c.SetColor(P_EmissionColor, Color.black);
                }
            }

            s_clones[src] = c;
            return c;
        }

        /// <summary>
        /// Re-skins every mesh renderer under <paramref name="root"/> (every LOD — LODGroup only
        /// toggles visibility, each LOD has its own material array) and silences leaf-particle
        /// emitters. Returns the renderers touched so the caller can drive the ember fade.
        /// </summary>
        public static List<Renderer> Apply(GameObject root, Color ember, int emberVariant = 0)
        {
            var touched = new List<Renderer>();
            if (root == null) return touched;
            EnsureDonors();

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                if (r is ParticleSystemRenderer)
                {
                    // Beech/Oak/Ygga LOD0 carry a falling-leaf emitter. A charred tree has no leaves to shed.
                    ParticleSystem ps = r.GetComponent<ParticleSystem>();
                    if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    r.enabled = false;
                    continue;
                }
                if (!(r is MeshRenderer)) continue;

                Material[] mats = r.sharedMaterials;
                bool changed = false;
                for (int j = 0; j < mats.Length; j++)
                {
                    if (mats[j] == null) continue;
                    Material clone = CharredCloneOf(mats[j]);
                    if (clone != null && clone != mats[j]) { mats[j] = clone; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;

                touched.Add(r);
            }

            SetEmber(touched, ember, emberVariant);
            return touched;
        }

        /// <summary>
        /// Per-tree ember glow, through the property block only — no material is touched. The
        /// mask variant rides along so two charred trees side by side glow in different places,
        /// and so a rebuilt mask set (coverage changed) is picked up on the next fade tick.
        /// </summary>
        public static void SetEmber(List<Renderer> renderers, Color ember, int emberVariant = 0)
        {
            if (renderers == null) return;
            Texture2D mask = null;
            if (FireVFXController.GraphicsAvailable)
            {
                try { mask = CharredTextures.EmberMask(emberVariant); } catch (System.Exception) { }
            }
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                s_mpb.Clear();
                if (r.HasPropertyBlock()) r.GetPropertyBlock(s_mpb);
                s_mpb.SetColor(P_EmissionColor, ember);
                if (mask != null)
                {
                    s_mpb.SetTexture(P_EmissiveTex, mask);
                    s_mpb.SetTexture(P_EmissionMap, mask);
                }
                r.SetPropertyBlock(s_mpb);
            }
        }

        /// <summary>
        /// Ember colour for a charred tree that has been charred for <paramref name="ageSeconds"/>.
        /// Fades as (1-t)² over CharredEmberGlowSeconds with a slow breathing pulse; each pocket
        /// in the mask is a different brightness already, so one colour is enough per tree.
        /// </summary>
        public static Color EmberAt(float ageSeconds, float glowSeconds, float noiseSeed)
        {
            if (glowSeconds <= 0f || EmberPeakHdr <= 0f) return Color.black;
            float t = Mathf.Clamp01(ageSeconds / glowSeconds);
            float fade = (1f - t) * (1f - t);
            float pulse = 0.82f + 0.18f * Mathf.PerlinNoise(Time.time * 0.9f, noiseSeed);
            Color c = Color.Lerp(EmberHot, EmberCool, t);
            return c * (fade * pulse);
        }

        /// <summary>
        /// Live-burn bark char: darkens the trunk and lights ember cracks in proportion to
        /// how far the fire has climbed, through property blocks so the vanilla materials are
        /// untouched and <see cref="ClearBurnChar"/> restores the tree exactly.
        /// </summary>
        public static void ApplyBurnChar(List<Renderer> renderers, float progress, float emberPulse, int emberVariant = 0)
        {
            if (renderers == null) return;
            EnsureDonors();
            float p = Mathf.Clamp01(progress);
            // Bark darkens from untouched to CharTint as the front climbs; leaves only start
            // to scorch once the fire is well up the trunk.
            Color barkTint = Color.Lerp(Color.white, CharTint, Mathf.SmoothStep(0f, 1f, p));
            Color leafTint = Color.Lerp(Color.white, new Color(0.18f, 0.12f, 0.06f, 1f), Mathf.Clamp01((p - 0.45f) / 0.5f));
            // Wood that is still burning glows harder than a char that has been left to cool,
            // but through the same sparse mask: pockets, not a lit lattice up the whole trunk.
            Color ember = EmberHot * (1.4f * Mathf.SmoothStep(0f, 1f, p) * emberPulse);
            Texture2D mask = null;
            if (FireVFXController.GraphicsAvailable)
            {
                try { mask = CharredTextures.EmberMask(emberVariant); } catch (System.Exception) { }
            }

            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    Material m = mats[j];
                    if (m == null) continue;
                    bool vegetation = m.shader != null && m.shader.name == VegetationShader;
                    s_mpb.Clear();
                    if (vegetation && IsFoliage(m))
                    {
                        s_mpb.SetColor(P_Color, leafTint);
                    }
                    else
                    {
                        // _Color multiplies the albedo; keep alpha 1 or the cutoff discards bark.
                        Color tint = m.HasProperty(P_Color) ? m.GetColor(P_Color) : Color.white;
                        s_mpb.SetColor(P_Color, new Color(tint.r * barkTint.r, tint.g * barkTint.g, tint.b * barkTint.b, tint.a));
                        if (vegetation && mask != null)
                        {
                            s_mpb.SetTexture(P_EmissiveTex, mask);
                            s_mpb.SetColor(P_EmissionColor, ember);
                        }
                    }
                    r.SetPropertyBlock(s_mpb, j);
                }
            }
        }

        /// <summary>Removes every live-burn block: the tree looks exactly as vanilla drew it.</summary>
        public static void ClearBurnChar(List<Renderer> renderers)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int j = 0; j < mats.Length; j++) r.SetPropertyBlock(null, j);
            }
        }

        /// <summary>Mesh renderers of a burner that the live-burn char may tint. Skips particle emitters.</summary>
        public static List<Renderer> CollectMeshRenderers(GameObject root)
        {
            var list = new List<Renderer>();
            if (root == null) return list;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] is MeshRenderer) list.Add(renderers[i]);
            }
            return list;
        }

        /// <summary>
        /// A new mask set exists (CharredEmberCoverage changed): every cached clone is re-pointed
        /// at it. Live trees pick up their own variant on their next fade tick through the block.
        /// </summary>
        public static void OnEmberMasksRebuilt(Texture2D mask0)
        {
            s_emberMask = mask0;
            foreach (KeyValuePair<Material, Material> kv in s_clones)
            {
                Material c = kv.Value;
                if (c == null) continue;
                if (c.HasProperty(P_EmissiveTex) && c.GetTexture(P_EmissiveTex) != null) c.SetTexture(P_EmissiveTex, mask0);
                if (c.HasProperty(P_EmissionMap) && c.GetTexture(P_EmissionMap) != null) c.SetTexture(P_EmissionMap, mask0);
            }
        }

        /// <summary>Plugin unload: the clones are ours to free; the donor textures are not.</summary>
        public static void ReleaseAll()
        {
            foreach (KeyValuePair<Material, Material> kv in s_clones)
            {
                if (kv.Value != null) Object.Destroy(kv.Value);
            }
            s_clones.Clear();
            s_emberMask = null;
            s_donorsResolved = false;
            CharredTextures.ReleaseAll();
        }
    }
}
