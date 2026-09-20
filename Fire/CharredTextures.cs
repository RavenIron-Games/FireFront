using System.Collections.Generic;
using System.IO;
using FireFront.Config;
using FireFront.Utils;
using SharedMedia;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// The textures a charred tree is drawn with, built once per vanilla texture on the client
    /// and cached: a charred albedo derived from the species' own bark (plates near-black, the
    /// grain kept as tone, fissures black, ash flecks on the plate tops), a normal map that
    /// carries the char relief over the vanilla bark normal, and a small set of ember masks
    /// whose glow lives only in scattered pockets so most of a burnt trunk stays black.
    /// </summary>
    /// <remarks>
    /// Bundle textures are not CPU-readable, so a vanilla albedo/normal is first copied through
    /// a RenderTexture (Graphics.Blit + ReadPixels). That needs a graphics device — it returns
    /// nothing under -nographics without an error (HEADLESS-AND-EMPTY-SERVER-FACTS.md §1) —
    /// so every entry point is gated on <see cref="FireVFXController.GraphicsAvailable"/> and
    /// a failed copy falls back to the tint-only look in <see cref="CharredTreeSkin"/>.
    ///
    /// The generator itself is <c>SharedMedia.ProceduralTextures</c> (vendored from libs-Tools,
    /// previewable offline with libs-Tools/CSharp/TexturePreview), so what the harness shows is
    /// what the game gets. Normal maps are AG-packed like every vanilla map (R=255, G=Y, A=X).
    /// </remarks>
    public static class CharredTextures
    {
        private const int Size = 512;
        public const int Variants = 4;
        private const int Seed = 20260920;

        private static ProceduralTextures.CrackField s_crack;
        private static float[] s_height;
        private static float[] s_albedoPocket;
        private static Texture2D[] s_emberMasks;
        private static readonly List<Texture2D> s_retiredMasks = new List<Texture2D>();
        private static float s_maskCoverage = -1f;
        private static readonly Dictionary<Texture, Texture2D> s_albedos = new Dictionary<Texture, Texture2D>();
        private static readonly Dictionary<Texture, Texture2D> s_normals = new Dictionary<Texture, Texture2D>();
        private static Texture2D s_heightOnlyNormal;
        private static readonly HashSet<Texture> s_failed = new HashSet<Texture>();
        private static bool s_blitFailureLogged;

        private static void EnsureFields()
        {
            if (s_crack != null) return;
            s_crack = ProceduralTextures.BuildCrackField(Size, Size, 9, 5, Seed);
            s_height = ProceduralTextures.CharHeight(s_crack, Size, Size, Seed);
            s_albedoPocket = ProceduralTextures.PocketField(Size, Size, 0.25f, Seed + 100);
        }

        /// <summary>Which of the ember mask variants a given object uses; stable per object and spread evenly.</summary>
        public static int VariantFor(ZDOID id)
        {
            unchecked
            {
                uint h = (uint)id.GetHashCode();
                h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
                return (int)(h % Variants);
            }
        }

        /// <summary>
        /// Ember mask <paramref name="variant"/>, built for the current CharredEmberCoverage. A
        /// coverage change rebuilds the set on the next call; the skin re-points its clones.
        /// </summary>
        public static Texture2D EmberMask(int variant)
        {
            float coverage = Mathf.Clamp01(FireConfig.CharredEmberCoverage.Value);
            if (s_emberMasks == null || !Mathf.Approximately(coverage, s_maskCoverage))
            {
                EnsureFields();
                // The previous set is NOT destroyed here: material clones and property blocks
                // still point at it, and a destroyed texture samples as white — exactly the
                // whole-tree glow this mask exists to prevent. It is parked and freed on unload.
                if (s_emberMasks != null) s_retiredMasks.AddRange(s_emberMasks);
                s_emberMasks = new Texture2D[Variants];
                for (int v = 0; v < Variants; v++)
                {
                    float[] pocket = ProceduralTextures.PocketField(Size, Size, coverage, Seed + 100 + v * 17);
                    Color32[] px = ProceduralTextures.EmberMask(Size, Size, s_crack, pocket, Seed + v * 17);
                    s_emberMasks[v] = MakeTexture(px, Size, Size, linear: true, name: "FireFront_EmberMask_" + v);
                }
                s_maskCoverage = coverage;
                FireLogger.Debug($"[CHARRED] ember masks built: {Variants}x{Size}² at coverage {coverage:F2}");
                CharredTreeSkin.OnEmberMasksRebuilt(s_emberMasks[0]);
            }
            int i = variant % Variants; if (i < 0) i += Variants;
            return s_emberMasks[i];
        }

        /// <summary>
        /// Charred albedo built from <paramref name="vanillaAlbedo"/> (sRGB). Null when the
        /// source could not be read back, in which case the caller tints the vanilla texture.
        /// </summary>
        public static Texture2D CharredAlbedoFor(Texture vanillaAlbedo)
        {
            if (vanillaAlbedo == null || !FireVFXController.GraphicsAvailable) return null;
            if (s_albedos.TryGetValue(vanillaAlbedo, out Texture2D cached) && cached != null) return cached;
            if (s_failed.Contains(vanillaAlbedo)) return null;
            try
            {
                EnsureFields();
                Color32[] src = ReadableCopy(vanillaAlbedo, linear: false, out int w, out int h);
                if (w != Size || h != Size) src = ProceduralTextures.Resample(src, w, h, Size, Size);
                Color32[] px = ProceduralTextures.CharredAlbedo(src, Size, Size, s_crack, s_albedoPocket, Seed, ProceduralTextures.CharParams.Default);
                Texture2D tex = MakeTexture(px, Size, Size, linear: false, name: vanillaAlbedo.name + "_charred");
                s_albedos[vanillaAlbedo] = tex;
                FireLogger.Debug($"[CHARRED] charred albedo built from {vanillaAlbedo.name} ({w}x{h})");
                return tex;
            }
            catch (System.Exception ex)
            {
                s_failed.Add(vanillaAlbedo);
                if (!s_blitFailureLogged)
                {
                    s_blitFailureLogged = true;
                    FireLogger.Warn($"[CHARRED] could not read {vanillaAlbedo.name} back from the GPU ({ex.Message}); charred trees use the tint-only look.");
                }
                return null;
            }
        }

        /// <summary>
        /// Char relief over the vanilla bark normal (linear, AG-packed). With a null source the
        /// relief alone is returned, which is still better than the flat vanilla map on a char.
        /// </summary>
        public static Texture2D CharredNormalFor(Texture vanillaNormal)
        {
            if (!FireVFXController.GraphicsAvailable) return null;
            if (vanillaNormal == null)
            {
                if (s_heightOnlyNormal == null)
                {
                    EnsureFields();
                    Color32[] px = ProceduralTextures.NormalFromHeight(s_height, Size, Size, 2.2f);
                    s_heightOnlyNormal = MakeTexture(px, Size, Size, linear: true, name: "FireFront_CharNormal");
                }
                return s_heightOnlyNormal;
            }
            if (s_normals.TryGetValue(vanillaNormal, out Texture2D cached) && cached != null) return cached;
            if (s_failed.Contains(vanillaNormal)) return CharredNormalFor(null);
            try
            {
                EnsureFields();
                Color32[] src = ReadableCopy(vanillaNormal, linear: true, out int w, out int h);
                if (w != Size || h != Size) src = ProceduralTextures.Resample(src, w, h, Size, Size);
                Color32[] px = ProceduralTextures.NormalFromHeight(s_height, Size, Size, 2.2f, src, 1.0f);
                Texture2D tex = MakeTexture(px, Size, Size, linear: true, name: vanillaNormal.name + "_charred");
                s_normals[vanillaNormal] = tex;
                return tex;
            }
            catch (System.Exception ex)
            {
                s_failed.Add(vanillaNormal);
                FireLogger.Debug($"[CHARRED] normal read-back of {vanillaNormal.name} failed ({ex.Message}); using relief only.");
                return CharredNormalFor(null);
            }
        }

        /// <summary>
        /// CPU copy of any GPU texture, through a temporary RenderTexture. Compressed bundle
        /// textures come back decoded; an sRGB source goes through an sRGB target so the bytes
        /// are the authored values, a linear one (normal maps) through a linear target.
        /// </summary>
        public static Color32[] ReadableCopy(Texture src, bool linear, out int w, out int h)
        {
            w = src.width; h = src.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Texture2D tmp = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                Color32[] px = tmp.GetPixels32();
                // A device that silently blits nothing hands back all-zero pixels (the -nographics
                // failure mode); treat that as a failure rather than charring from black.
                bool any = false;
                for (int i = 0; i < px.Length; i += 97) { if (px[i].r != 0 || px[i].g != 0 || px[i].b != 0 || px[i].a != 0) { any = true; break; } }
                if (!any) throw new System.InvalidOperationException("read-back returned only zeros");
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) Object.Destroy(tmp);
            }
        }

        private static Texture2D MakeTexture(Color32[] px, int w, int h, bool linear, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            tex.SetPixels32(px);
            tex.Apply(true, true); // GPU only from here; the generator is deterministic, so a dump regenerates
            return tex;
        }

        /// <summary>
        /// `firedumptex`: regenerates every texture this client has built (same seed, same
        /// inputs, so byte-identical) and writes it as PNG beside its vanilla source, so what
        /// the game drew can be looked at outside it. Returns the number of files written.
        /// </summary>
        public static int DumpAll(string dir)
        {
            Directory.CreateDirectory(dir);
            EnsureFields();
            int n = 0;
            float coverage = Mathf.Clamp01(FireConfig.CharredEmberCoverage.Value);
            for (int v = 0; v < Variants; v++)
            {
                float[] pocket = ProceduralTextures.PocketField(Size, Size, coverage, Seed + 100 + v * 17);
                Write(dir, "FireFront_EmberMask_" + v, Size, ProceduralTextures.EmberMask(Size, Size, s_crack, pocket, Seed + v * 17)); n++;
            }
            Write(dir, "FireFront_CharNormal", Size, ProceduralTextures.NormalFromHeight(s_height, Size, Size, 2.2f)); n++;
            foreach (KeyValuePair<Texture, Texture2D> kv in s_albedos)
            {
                if (kv.Key == null) continue;
                Color32[] src = ReadableCopy(kv.Key, linear: false, out int w, out int h);
                Write(dir, kv.Key.name + "_vanilla", w, src, h); n++;
                if (w != Size || h != Size) src = ProceduralTextures.Resample(src, w, h, Size, Size);
                Write(dir, kv.Key.name + "_charred", Size, ProceduralTextures.CharredAlbedo(src, Size, Size, s_crack, s_albedoPocket, Seed, ProceduralTextures.CharParams.Default)); n++;
            }
            foreach (KeyValuePair<Texture, Texture2D> kv in s_normals)
            {
                if (kv.Key == null) continue;
                Color32[] src = ReadableCopy(kv.Key, linear: true, out int w, out int h);
                Write(dir, kv.Key.name + "_vanilla", w, src, h); n++;
                if (w != Size || h != Size) src = ProceduralTextures.Resample(src, w, h, Size, Size);
                Write(dir, kv.Key.name + "_charred", Size, ProceduralTextures.NormalFromHeight(s_height, Size, Size, 2.2f, src, 1.0f)); n++;
            }
            return n;
        }

        private static void Write(string dir, string name, int w, Color32[] px, int h = -1)
        {
            if (h < 0) h = w;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), ProceduralTextures.EncodePng(w, h, px));
        }

        private static void ReleaseMasks()
        {
            if (s_emberMasks != null)
            {
                for (int i = 0; i < s_emberMasks.Length; i++) if (s_emberMasks[i] != null) Object.Destroy(s_emberMasks[i]);
                s_emberMasks = null;
            }
            for (int i = 0; i < s_retiredMasks.Count; i++) if (s_retiredMasks[i] != null) Object.Destroy(s_retiredMasks[i]);
            s_retiredMasks.Clear();
        }

        /// <summary>Plugin unload: everything here is ours to free.</summary>
        public static void ReleaseAll()
        {
            ReleaseMasks();
            foreach (KeyValuePair<Texture, Texture2D> kv in s_albedos) if (kv.Value != null) Object.Destroy(kv.Value);
            foreach (KeyValuePair<Texture, Texture2D> kv in s_normals) if (kv.Value != null) Object.Destroy(kv.Value);
            s_albedos.Clear(); s_normals.Clear(); s_failed.Clear();
            if (s_heightOnlyNormal != null) { Object.Destroy(s_heightOnlyNormal); s_heightOnlyNormal = null; }
            s_crack = null; s_height = null; s_albedoPocket = null; s_maskCoverage = -1f;
        }
    }
}
