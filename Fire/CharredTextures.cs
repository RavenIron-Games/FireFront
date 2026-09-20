using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

        // Everything below is built OFF the main thread. The generator (SharedMedia.
        // ProceduralTextures) is pure CPU code over Color32[] and float[] - it touches no Unity
        // object until MakeTexture uploads the result - and a 512² field plus four 512² masks is
        // several hundred milliseconds of hashing, which the first version of this did
        // synchronously inside FireVFXController.Update the frame the first tree caught: a
        // visible freeze at the worst possible moment (2026-09-20 review, before any run). Now a
        // mask that is not ready yet is null, every caller already treats null as "no mask"
        // (blackTexture, no emission), and the texture appears on a later tick; when variant 0 of
        // a generation lands, the skin re-points its clones. Only the GPU read-backs stay on the
        // main thread. The synchronous paths (a charred albedo or normal at charring time,
        // `firedumptex`) join a build in flight rather than starting a second.
        private sealed class Fields
        {
            public ProceduralTextures.CrackField Crack;
            public float[] Height;
            public float[] AlbedoPocket;
        }
        private sealed class AtlasSet
        {
            public int Generation;
            public readonly Texture2D[] Masks = new Texture2D[Variants];
            public readonly Task<Color32[]>[] Tasks = new Task<Color32[]>[Variants];
        }

        private static ProceduralTextures.CrackField s_crack;
        private static float[] s_height;
        private static float[] s_albedoPocket;
        private static Task<Fields> s_fieldsTask;
        private static bool s_fieldsFailed;
        private static Texture2D[] s_emberMasks;
        private static Task<Color32[]>[] s_maskTasks;
        private static int s_generation; // bumps on every coverage change; a set built for an older generation is retired
        private static readonly List<Texture2D> s_retiredMasks = new List<Texture2D>();
        private static float s_maskCoverage = -1f;
        /// <summary>Per-atlas mask sets (pine, fir): the standard masks confined to the atlas' opaque bark block, so needle cards never light during a live burn.</summary>
        private static readonly Dictionary<Texture, AtlasSet> s_atlasMasks = new Dictionary<Texture, AtlasSet>();
        private static readonly Dictionary<Texture, float[]> s_atlasRegions = new Dictionary<Texture, float[]>();
        private static readonly Dictionary<Texture, Task<float[]>> s_atlasRegionTasks = new Dictionary<Texture, Task<float[]>>();
        private static readonly Dictionary<Texture, Texture2D> s_albedos = new Dictionary<Texture, Texture2D>();
        private static readonly Dictionary<Texture, Texture2D> s_normals = new Dictionary<Texture, Texture2D>();
        private static Texture2D s_heightOnlyNormal;
        private static readonly HashSet<Texture> s_failed = new HashSet<Texture>();
        private static bool s_blitFailureLogged;
        private static bool s_taskFailureLogged;

        private static Fields BuildFields()
        {
            var f = new Fields { Crack = ProceduralTextures.BuildCrackField(Size, Size, 9, 5, Seed) };
            f.Height = ProceduralTextures.CharHeight(f.Crack, Size, Size, Seed);
            f.AlbedoPocket = ProceduralTextures.PocketField(Size, Size, 0.25f, Seed + 100);
            return f;
        }

        private static void AdoptFields(Fields f)
        {
            s_crack = f.Crack; s_height = f.Height; s_albedoPocket = f.AlbedoPocket;
            s_fieldsTask = null;
        }

        /// <summary>
        /// Starts the field build on a worker thread now, so a tree that streams in already charred
        /// (a persisted flag, no local burn ever ran) finds it done instead of joining it on its own
        /// spawn frame through EnsureFields. A few MB of arrays per client process, once; idempotent.
        /// </summary>
        public static void Prewarm()
        {
            if (s_crack != null || s_fieldsFailed || s_fieldsTask != null) return;
            s_fieldsTask = Task.Run((System.Func<Fields>)BuildFields);
        }

        /// <summary>The fields, NOW, for the synchronous paths. Joins a build already in flight; .Result rethrows a build failure into the caller's own catch.</summary>
        private static void EnsureFields()
        {
            if (s_crack != null) return;
            if (s_fieldsTask == null) s_fieldsTask = Task.Run((System.Func<Fields>)BuildFields);
            AdoptFields(s_fieldsTask.Result);
        }

        /// <summary>The fields if they are ready; otherwise starts building them and answers false.</summary>
        private static bool TryGetFields()
        {
            if (s_crack != null) return true;
            if (s_fieldsFailed) return false;
            if (s_fieldsTask == null) { s_fieldsTask = Task.Run((System.Func<Fields>)BuildFields); return false; }
            if (!s_fieldsTask.IsCompleted) return false;
            if (s_fieldsTask.IsFaulted)
            {
                s_fieldsFailed = true;
                LogTaskFailure("char fields", s_fieldsTask.Exception);
                s_fieldsTask = null;
                return false;
            }
            AdoptFields(s_fieldsTask.Result);
            return true;
        }

        private static void LogTaskFailure(string what, System.Exception ex)
        {
            if (s_taskFailureLogged) return;
            s_taskFailureLogged = true;
            FireLogger.Warn($"[CHARRED] building the {what} threw ({ex?.GetBaseException().Message}); charred trees will not glow.");
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
        /// Ember mask <paramref name="variant"/> for the current CharredEmberCoverage, or null
        /// while it is still being built (or if it cannot be). Built one variant at a time, on a
        /// worker thread, on demand. A coverage change starts a new generation; the previous set
        /// is NOT destroyed: material clones and property blocks still point at it, and a
        /// destroyed texture samples as white - exactly the whole-tree glow this mask exists to
        /// prevent. It is parked and freed on unload.
        /// </summary>
        public static Texture2D EmberMask(int variant)
        {
            float coverage = Mathf.Clamp01(FireConfig.CharredEmberCoverage.Value);
            if (s_emberMasks == null || !Mathf.Approximately(coverage, s_maskCoverage))
            {
                if (s_emberMasks != null)
                    for (int v = 0; v < s_emberMasks.Length; v++) if (s_emberMasks[v] != null) s_retiredMasks.Add(s_emberMasks[v]);
                s_emberMasks = new Texture2D[Variants];
                s_maskTasks = new Task<Color32[]>[Variants];
                s_maskCoverage = coverage;
                s_generation++;
            }
            int i = variant % Variants; if (i < 0) i += Variants;
            if (s_emberMasks[i] != null) return s_emberMasks[i];
            if (!TryGetFields()) return null;

            Task<Color32[]> task = s_maskTasks[i];
            if (task == null)
            {
                ProceduralTextures.CrackField crack = s_crack;
                int pocketSeed = Seed + 100 + i * 17, maskSeed = Seed + i * 17;
                s_maskTasks[i] = Task.Run(() => ProceduralTextures.EmberMask(Size, Size, crack,
                    ProceduralTextures.PocketField(Size, Size, coverage, pocketSeed), maskSeed));
                return null;
            }
            if (!task.IsCompleted) return null;
            if (task.IsFaulted) { LogTaskFailure("ember mask", task.Exception); return null; }

            s_emberMasks[i] = MakeTexture(task.Result, Size, Size, linear: true, name: "FireFront_EmberMask_" + i);
            s_maskTasks[i] = null;
            FireLogger.Debug($"[CHARRED] ember mask {i} built ({Size}², coverage {coverage:F2}, generation {s_generation})");
            if (i == 0) CharredTreeSkin.OnEmberMasksRebuilt(s_emberMasks[0]);
            return s_emberMasks[i];
        }

        /// <summary>
        /// Ember mask for a material whose _MainTex is a trunk+foliage atlas (Pine, Fir): the
        /// standard mask multiplied by the atlas' opaque bark block, found by eroding its alpha
        /// (needle cards are cut-outs full of holes and erode away; the bark rectangle survives).
        /// Null while it is being built - the caller then lights NOTHING on that slot rather than
        /// the plain mask, which would light the needle cards. Falls back to the plain mask only
        /// when the atlas cannot be read back at all.
        /// </summary>
        public static Texture2D EmberMaskForAtlas(Texture atlas, int variant)
        {
            Texture2D plain = EmberMask(variant); // also starts, or advances, the base set
            if (plain == null) return null;
            if (atlas == null || !FireVFXController.GraphicsAvailable) return plain;
            int i = variant % Variants; if (i < 0) i += Variants;

            if (!s_atlasMasks.TryGetValue(atlas, out AtlasSet set) || set == null || set.Generation != s_generation)
            {
                if (set != null)
                    for (int v = 0; v < Variants; v++) if (set.Masks[v] != null) s_retiredMasks.Add(set.Masks[v]);
                set = new AtlasSet { Generation = s_generation };
                s_atlasMasks[atlas] = set;
            }
            if (set.Masks[i] != null) return set.Masks[i];

            if (!TryGetTrunkRegion(atlas, out float[] region)) return null; // still being read back or eroded
            if (region == null) return plain;                                // unreadable atlas: the old behaviour

            Task<Color32[]> task = set.Tasks[i];
            if (task == null)
            {
                ProceduralTextures.CrackField crack = s_crack;
                float coverage = s_maskCoverage;
                int pocketSeed = Seed + 100 + i * 17, maskSeed = Seed + i * 17;
                set.Tasks[i] = Task.Run(() => ProceduralTextures.EmberMask(Size, Size, crack,
                    ProceduralTextures.PocketField(Size, Size, coverage, pocketSeed), maskSeed, region: region));
                return null;
            }
            if (!task.IsCompleted) return null;
            if (task.IsFaulted) { LogTaskFailure("atlas ember mask", task.Exception); return null; }

            set.Masks[i] = MakeTexture(task.Result, Size, Size, linear: true, name: "FireFront_EmberMask_" + atlas.name + "_" + i);
            set.Tasks[i] = null;
            FireLogger.Debug($"[CHARRED] atlas ember mask {i} built for {atlas.name}");
            return set.Masks[i];
        }

        /// <summary>
        /// Trunk-only region of an atlas texture (1 on the opaque bark block). The GPU read-back
        /// must run on the main thread and is cheap; the erosion is not and runs on a worker.
        /// False while that is in flight. True with a null region when the atlas cannot be read
        /// or has nothing solid, in which case the caller uses the plain mask, as before.
        /// </summary>
        private static bool TryGetTrunkRegion(Texture atlas, out float[] region)
        {
            if (s_atlasRegions.TryGetValue(atlas, out region)) return true;
            if (!s_atlasRegionTasks.TryGetValue(atlas, out Task<float[]> task))
            {
                Color32[] src;
                int w, h;
                try
                {
                    src = ReadableCopy(atlas, linear: false, out w, out h);
                }
                catch (System.Exception ex)
                {
                    FireLogger.Debug($"[CHARRED] trunk region of {atlas.name} failed ({ex.Message}); plain mask used.");
                    s_atlasRegions[atlas] = null;
                    region = null;
                    return true;
                }
                int cw = w, ch = h;
                s_atlasRegionTasks[atlas] = Task.Run(() =>
                {
                    Color32[] px = (cw != Size || ch != Size) ? ProceduralTextures.Resample(src, cw, ch, Size, Size) : src;
                    float[] r = ProceduralTextures.OpaqueBlockMask(px, Size, Size, Size / 48);
                    int on = 0; for (int k = 0; k < r.Length; k++) if (r[k] > 0f) on++;
                    return on == 0 ? null : r; // nothing solid: leave the plain mask rather than a black one
                });
                region = null;
                return false;
            }
            if (!task.IsCompleted) { region = null; return false; }
            s_atlasRegionTasks.Remove(atlas);
            if (task.IsFaulted)
            {
                FireLogger.Debug($"[CHARRED] trunk region of {atlas.name} failed ({task.Exception?.GetBaseException().Message}); plain mask used.");
                region = null;
            }
            else
            {
                region = task.Result;
                if (region != null)
                {
                    int on = 0; for (int k = 0; k < region.Length; k++) if (region[k] > 0f) on++;
                    FireLogger.Debug($"[CHARRED] trunk region of {atlas.name}: {(100f * on / region.Length):F1}% of the atlas is solid bark");
                }
            }
            s_atlasRegions[atlas] = region;
            return true;
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
            s_maskTasks = null; // a build still in flight completes into nothing and is collected
            for (int i = 0; i < s_retiredMasks.Count; i++) if (s_retiredMasks[i] != null) Object.Destroy(s_retiredMasks[i]);
            s_retiredMasks.Clear();
        }

        /// <summary>Plugin unload: everything here is ours to free.</summary>
        public static void ReleaseAll()
        {
            foreach (KeyValuePair<Texture, AtlasSet> kv in s_atlasMasks)
            {
                if (kv.Value == null) continue;
                for (int i = 0; i < Variants; i++) if (kv.Value.Masks[i] != null) Object.Destroy(kv.Value.Masks[i]);
            }
            s_atlasMasks.Clear(); s_atlasRegions.Clear(); s_atlasRegionTasks.Clear();
            ReleaseMasks();
            foreach (KeyValuePair<Texture, Texture2D> kv in s_albedos) if (kv.Value != null) Object.Destroy(kv.Value);
            foreach (KeyValuePair<Texture, Texture2D> kv in s_normals) if (kv.Value != null) Object.Destroy(kv.Value);
            s_albedos.Clear(); s_normals.Clear(); s_failed.Clear();
            if (s_heightOnlyNormal != null) { Object.Destroy(s_heightOnlyNormal); s_heightOnlyNormal = null; }
            s_crack = null; s_height = null; s_albedoPocket = null; s_maskCoverage = -1f;
            s_fieldsTask = null; s_fieldsFailed = false;
        }
    }
}
