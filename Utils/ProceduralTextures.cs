// SharedMedia/ProceduralTextures.cs — shared, engine-light procedural texture primitives.
//
// Canonical copy lives in libs-Tools/SharedMedia/. Mods vendor a byte-identical copy (FireFront:
// Utils/ProceduralTextures.cs) because a mod's build must not depend on ..\libs-Tools; when you
// change one, change both and note it in IMPLEMENTATIONS/SharedInfrastructure.md.
//
// Everything here works on float[] / Color32[] and touches only UnityEngine.Mathf, Color and
// Color32, so the same file compiles unchanged inside the offline preview harness
// (libs-Tools/CSharp/TexturePreview, which stubs those three types) and inside a BepInEx mod.
// Nothing allocates a Texture2D: the caller does that, sets the pixels, and owns the object.
//
// Lineage: the runtime rune texture generators (TortalPortal/Njord/WingsoftheValkyrie/Runic —
// see IMPLEMENTATIONS/MASTER_IMPLEMENTATIONS.md pattern 4) and TortalUITheme's coverage+height
// two-buffer painter with a bake-time emboss. This file generalises the "height buffer → shaded
// result" idea into a tileable noise/cell toolkit plus a dependency-free PNG codec so any
// generator can be looked at without launching the game (lesson 11 in the master sheet).
//
// Conventions:
//   * (u, v) are texture coordinates in [0,1); every field is periodic on the unit torus so the
//     result tiles on bark/rock/cloth UVs that repeat.
//   * Colour arrays are 8-bit sRGB unless a method says linear. Normal maps are written in
//     Valheim's AG (DXT5nm) layout: R=255, G=Y, B=255, A=X — the shader unpacks x = R*A, y = G.
//   * seed changes everything; the same seed always gives the same texture on every machine.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace SharedMedia
{
    public static class ProceduralTextures
    {
        // ------------------------------------------------------------------
        // Hashing and tileable noise
        // ------------------------------------------------------------------

        /// <summary>Deterministic integer hash to [0,1). Same answer on every platform (no Mathf.PerlinNoise, whose lattice differs per Unity build).</summary>
        public static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)seed * 0xCB1AB31Fu;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0x00FFFFFFu) / 16777216f;
            }
        }

        private static float Quintic(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        /// <summary>
        /// Value noise with a lattice of <paramref name="px"/> x <paramref name="py"/> cells that wraps,
        /// so the field is periodic in u and v. Returns [0,1].
        /// </summary>
        public static float ValueNoise(float u, float v, int px, int py, int seed)
        {
            float x = u * px, y = v * py;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            int ax = Mod(x0, px), bx = Mod(x1, px), ay = Mod(y0, py), by = Mod(y1, py);
            float sx = Quintic(fx), sy = Quintic(fy);
            float n00 = Hash(ax, ay, seed), n10 = Hash(bx, ay, seed), n01 = Hash(ax, by, seed), n11 = Hash(bx, by, seed);
            float nx0 = n00 + (n10 - n00) * sx;
            float nx1 = n01 + (n11 - n01) * sx;
            return nx0 + (nx1 - nx0) * sy;
        }

        /// <summary>Fractal sum of <see cref="ValueNoise"/>; the lattice doubles per octave so every octave tiles. Normalised to [0,1].</summary>
        public static float Fbm(float u, float v, int px, int py, int octaves, int seed, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int cx = Math.Max(1, px), cy = Math.Max(1, py);
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(u, v, cx, cy, seed + i * 131) * amp;
                norm += amp;
                amp *= gain;
                cx *= 2; cy *= 2;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>
        /// Toroidal Worley (cellular) noise on a jittered <paramref name="cx"/> x <paramref name="cy"/> grid.
        /// d1/d2 are distances (in units where one cell is ~1) to the nearest and second-nearest feature
        /// point; d2 - d1 is 0 on a cell boundary and grows into the plate. cellId identifies the owning cell.
        /// </summary>
        public static void Worley(float u, float v, int cx, int cy, int seed, float jitter, out float d1, out float d2, out int cellId)
        {
            float x = u * cx, y = v * cy;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            d1 = float.MaxValue; d2 = float.MaxValue; cellId = 0;
            // Distances are measured in cell units on each axis and then scaled by the aspect so
            // the crack width is the same in texture space regardless of the cell counts.
            float ax = 1f, ay = (float)cx / cy;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int gx = ix + ox, gy = iy + oy;
                    int wx = Mod(gx, cx), wy = Mod(gy, cy);
                    float fx = gx + 0.5f + (Hash(wx, wy, seed) - 0.5f) * jitter;
                    float fy = gy + 0.5f + (Hash(wx, wy, seed + 7919) - 0.5f) * jitter;
                    float dx = (x - fx) * ax, dy = (y - fy) * ay;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d < d1) { d2 = d1; d1 = d; cellId = wy * cx + wx; }
                    else if (d < d2) d2 = d;
                }
            }
        }

        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        public static float Smooth(float a, float b, float x)
        {
            if (b <= a) return x >= b ? 1f : 0f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------------
        // Fields
        // ------------------------------------------------------------------

        /// <summary>A crack network plus the plate shapes it separates. All arrays are w*h, row-major, bottom row first (Unity texture order).</summary>
        public sealed class CrackField
        {
            public int W, H;
            /// <summary>1 on a fissure, 0 inside a plate (coarse and fine cracks merged).</summary>
            public float[] Crack;
            /// <summary>Plate dome: 1 at a plate's centre, 0 at its edges. Lights the plate tops in the albedo and normal.</summary>
            public float[] Plate;
            /// <summary>Per-texel id of the coarse plate the texel belongs to; lets a caller vary something per plate.</summary>
            public int[] PlateId;
            /// <summary>Coarse-network edge distance (second-nearest minus nearest, cell units): 0 on a fissure centre, growing into the plate. The glow and the relief both read this.</summary>
            public float[] Edge;
        }

        /// <summary>
        /// Charred-wood "alligator" plates: a coarse jittered cell network cut by wide fissures, and a
        /// finer network of shallow cracks inside the plates. <paramref name="width"/> is the coarse
        /// crack half-width in cell units (0.08–0.14 reads as burnt wood); the fine layer is narrower
        /// and weighted by <paramref name="fineWeight"/>.
        /// </summary>
        public static CrackField BuildCrackField(int w, int h, int cellsX, int cellsY, int seed, float width = 0.10f, float widthNoise = 0.05f, float fineWeight = 0.55f, float warp = 0.05f)
        {
            var f = new CrackField { W = w, H = h, Crack = new float[w * h], Plate = new float[w * h], PlateId = new int[w * h], Edge = new float[w * h] };
            int fineX = cellsX * 2 + 1, fineY = cellsY * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    // Domain warp: straight-edged Voronoi polygons read as cobblestones; a little
                    // low-frequency displacement bends the fissures the way heat-split wood does.
                    float wu = u + (Fbm(u, v, 3, 3, 2, seed + 901) - 0.5f) * warp;
                    float wv = v + (Fbm(u, v, 3, 3, 2, seed + 907) - 0.5f) * warp;
                    Worley(wu, wv, cellsX, cellsY, seed, 0.65f, out float d1, out float d2, out int id);
                    float edge = d2 - d1;
                    float wn = width + widthNoise * (Fbm(u, v, 9, 9, 2, seed + 3) - 0.5f) * 2f;
                    float coarse = 1f - Smooth(0f, Math.Max(0.02f, wn), edge);

                    Worley(wu, wv, fineX, fineY, seed + 17, 0.8f, out float e1, out float e2, out int _);
                    float fine = 1f - Smooth(0f, width * 0.45f, e2 - e1);
                    // Fine cracks only live inside plates and fade out where a coarse crack already is.
                    fine *= (1f - coarse) * (0.6f + 0.4f * Fbm(u, v, 5, 5, 2, seed + 11));

                    int i = y * w + x;
                    f.Crack[i] = Mathf.Clamp01(Math.Max(coarse, fine * fineWeight));
                    // Dome: distance from the crack line, normalised by a typical half-cell.
                    f.Plate[i] = Mathf.Clamp01(edge / 0.55f) * (1f - coarse);
                    f.PlateId[i] = id;
                    f.Edge[i] = edge;
                }
            }
            return f;
        }

        /// <summary>
        /// Low-frequency cluster mask covering roughly <paramref name="coverage"/> (0..1) of the area:
        /// the field is thresholded at its own (1 - coverage) quantile, so the fraction is honest for
        /// any seed rather than a guess. <paramref name="period"/> is the number of clusters across the
        /// tile (2–3 gives hand-sized pockets on bark). Soft-edged so glow feathers out.
        /// </summary>
        public static float[] PocketField(int w, int h, float coverage, int seed, int period = 3, float softness = 0.10f)
        {
            var field = new float[w * h];
            coverage = Mathf.Clamp01(coverage);
            if (coverage <= 0f) return field;
            var raw = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    raw[y * w + x] = Fbm(u, v, period, period, 3, seed, 0.55f);
                }
            }
            if (coverage >= 1f) { for (int i = 0; i < raw.Length; i++) field[i] = 1f; return field; }
            // Quantile on a stride sample: exact enough for a mask, cheap enough for 512².
            var sample = new List<float>(4096);
            int stride = Math.Max(1, raw.Length / 4096);
            for (int i = 0; i < raw.Length; i += stride) sample.Add(raw[i]);
            sample.Sort();
            float q = sample[Mathf.Clamp((int)((1f - coverage) * (sample.Count - 1)), 0, sample.Count - 1)];
            for (int i = 0; i < raw.Length; i++) field[i] = Smooth(q, q + softness, raw[i]);
            return field;
        }

        // ------------------------------------------------------------------
        // Charred wood
        // ------------------------------------------------------------------

        public struct CharParams
        {
            /// <summary>Albedo of a plate at full luminance, sRGB 0..1 (charcoal ≈ 0.10–0.14).</summary>
            public float PlateLight;
            /// <summary>Albedo of a plate at zero luminance (≈ 0.03).</summary>
            public float PlateDark;
            /// <summary>How much of the source bark's own contrast survives (0 = flat charcoal, 1 = fully tinted grain).</summary>
            public float GrainKeep;
            /// <summary>Fraction of plate tops that carry pale ash flecks (0..1; 0.08 is a lightly-dusted log).</summary>
            public float AshAmount;
            /// <summary>Ash colour, sRGB.</summary>
            public float AshR, AshG, AshB;
            /// <summary>Extra darkening inside ember pockets (deeper burn), 0..1.</summary>
            public float PocketDarken;

            public static CharParams Default => new CharParams
            {
                PlateLight = 0.13f, PlateDark = 0.03f, GrainKeep = 0.7f,
                AshAmount = 0.14f, AshR = 0.66f, AshG = 0.64f, AshB = 0.60f, PocketDarken = 0.25f,
            };
        }

        /// <summary>
        /// Turns a bark albedo into charred wood: plates go near-black keeping the species' grain as
        /// faint tonal variation, fissures go black, plate tops pick up pale ash flecks, and ember
        /// pockets are burnt a shade deeper so the glow sits in a hollow. Alpha is preserved (atlas
        /// species keep their needle cut-outs). <paramref name="src"/> may be null for a flat charcoal.
        /// </summary>
        public static Color32[] CharredAlbedo(Color32[] src, int w, int h, CrackField cf, float[] pocket, int seed, CharParams p)
        {
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float u = (x + 0.5f) / w;
                    float lum = 0.5f; byte a = 255;
                    if (src != null)
                    {
                        Color32 s = src[i];
                        lum = (0.299f * s.r + 0.587f * s.g + 0.114f * s.b) / 255f;
                        a = s.a;
                    }
                    float grain = 0.5f + (lum - 0.5f) * p.GrainKeep;
                    float baseV = p.PlateDark + (p.PlateLight - p.PlateDark) * grain;
                    // Charcoal is faintly warm; plate tops catch a touch more light than the dome edges,
                    // and no two plates burned to quite the same tone.
                    float dome = cf != null ? cf.Plate[i] : 0.5f;
                    float plateVar = cf != null ? 0.75f + 0.5f * Hash(cf.PlateId[i], 3, seed + 5) : 1f;
                    baseV *= (0.85f + 0.3f * dome) * plateVar;
                    float r = baseV * 1.00f, g = baseV * 0.93f, b = baseV * 0.86f;

                    float crack = cf != null ? cf.Crack[i] : 0f;
                    float dark = 1f - 0.85f * crack;
                    r *= dark; g *= dark; b *= dark;

                    // Ash flecks: a high-frequency field thresholded so ~AshAmount of plate tops carry
                    // them; they sit on the domes (the parts that burned in open air), never in a fissure.
                    float ashNoise = Fbm(u, v, 20, 20, 3, seed + 41, 0.6f);
                    float ashPlate = cf != null ? 0.5f + Hash(cf.PlateId[i], 9, seed + 13) : 1f;   // some plates are dusted, some are not
                    float ash = Smooth(1f - p.AshAmount * 1.8f * ashPlate, 1f - p.AshAmount * 0.3f * ashPlate, ashNoise) * (1f - crack) * dome * dome;
                    r += (p.AshR - r) * ash; g += (p.AshG - g) * ash; b += (p.AshB - b) * ash;

                    float pk = pocket != null ? pocket[i] : 0f;
                    float pd = 1f - p.PocketDarken * pk;
                    r *= pd; g *= pd; b *= pd;

                    dst[i] = new Color32(ToByte(r), ToByte(g), ToByte(b), a);
                }
            }
            return dst;
        }

        /// <summary>
        /// Ember mask for Custom/Vegetation's _EmissiveTex (shader: rgb * a * _EmissionColor) and
        /// Standard's _EmissionMap. Glow lives only inside <paramref name="pocket"/>: along the fissures
        /// there, plus sparse pin-point embers on the plates (vanilla's own Ashlands tree mask is a field
        /// of such points). Nothing glows outside a pocket, so most of the trunk stays black.
        /// rgb runs deep orange → pale yellow with intensity so the hottest cores read white-hot under bloom.
        /// </summary>
        public static Color32[] EmberMask(int w, int h, CrackField cf, float[] pocket, int seed, float dotDensity = 0.25f, float crackGlow = 1f, float glowWidth = 0.30f)
        {
            var dst = new Color32[w * h];
            int dotsX = 40, dotsY = 40;
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float u = (x + 0.5f) / w;
                    float pk = pocket != null ? pocket[i] : 1f;
                    float crack = cf != null ? cf.Crack[i] : 0f;

                    // Fissure glow: hottest in the coarse fissure, bleeding onto the plate edges either
                    // side (heat-split wood glows along the split, not as a hairline), modulated along
                    // its length so no crack is lit end to end. Fine cracks add a fainter web.
                    float along = 0.40f + 0.60f * Fbm(u, v, 7, 7, 3, seed + 5);
                    float edge = cf != null ? cf.Edge[i] : 1f;
                    float wide = 1f - Smooth(0f, glowWidth, edge);
                    float fissure = (wide * wide * 0.85f + crack * 0.35f) * along * crackGlow;

                    // Pin-point embers: a fine Worley field, keeping only some cells, dot radius ~ a texel or two.
                    Worley(u, v, dotsX, dotsY, seed + 23, 0.9f, out float d1, out float _, out int id);
                    float keep = Hash(id, 77, seed) < dotDensity ? 1f : 0f;
                    float dot = (1f - Smooth(0.04f, 0.15f, d1)) * keep * (1f - crack * 0.6f) * (0.5f + 0.5f * Hash(id, 5, seed + 3));

                    float glow = Mathf.Clamp01(fissure + dot * 0.75f);
                    float a = glow * pk * pk;   // pk² : pockets feather to nothing well before their edge

                    // Hue: deep orange at the edges, yellow-white in the core.
                    float core = a * a;
                    float r = 1f;
                    float g = 0.28f + 0.50f * core;
                    float b = 0.04f + 0.30f * core;
                    dst[i] = new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
                }
            }
            return dst;
        }

        /// <summary>Height field for the char relief: plate domes stand proud, fissures cut deep, with a little surface grit. Range roughly [-1, 1].</summary>
        public static float[] CharHeight(CrackField cf, int w, int h, int seed, float crackDepth = 1.0f, float domeHeight = 0.6f, float grit = 0.12f)
        {
            var hf = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float u = (x + 0.5f) / w;
                    float g = (Fbm(u, v, 32, 32, 2, seed + 61) - 0.5f) * 2f;
                    hf[i] = domeHeight * cf.Plate[i] - crackDepth * cf.Crack[i] + grit * g;
                }
            }
            return hf;
        }

        /// <summary>
        /// Sobel normal from a tileable height field, optionally blended over an existing AG-packed
        /// normal map (the vanilla bark normal) so the species' grain survives under the char relief.
        /// Output is AG-packed for Valheim's shaders: R=255, G=Y, B=255, A=X.
        /// <paramref name="strength"/> ~1.5–3 for texel-scale relief.
        /// </summary>
        public static Color32[] NormalFromHeight(float[] height, int w, int h, float strength, Color32[] baseNormalAG = null, float baseWeight = 1f)
        {
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int ym = (y - 1 + h) % h, yp = (y + 1) % h;
                for (int x = 0; x < w; x++)
                {
                    int xm = (x - 1 + w) % w, xp = (x + 1) % w;
                    // Sobel; texture row 0 is the bottom, so +y is up in tangent space (Unity convention).
                    float dx = (height[y * w + xp] - height[y * w + xm]) * 2f
                             + (height[yp * w + xp] - height[yp * w + xm])
                             + (height[ym * w + xp] - height[ym * w + xm]);
                    float dy = (height[yp * w + x] - height[ym * w + x]) * 2f
                             + (height[yp * w + xp] - height[ym * w + xp])
                             + (height[yp * w + xm] - height[ym * w + xm]);
                    float nx = -dx * strength / 8f, ny = -dy * strength / 8f, nz = 1f;

                    if (baseNormalAG != null)
                    {
                        Color32 bn = baseNormalAG[y * w + x];
                        float bx = (bn.a / 255f) * 2f - 1f;   // AG packing: x in alpha
                        float by = (bn.g / 255f) * 2f - 1f;   // y in green
                        float bz = (float)Math.Sqrt(Math.Max(0f, 1f - bx * bx - by * by));
                        // Reoriented-style blend: add the base slope to ours; z stays product-like.
                        nx += bx * baseWeight; ny += by * baseWeight; nz *= Math.Max(0.2f, bz);
                    }
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx /= len; ny /= len;
                    dst[y * w + x] = new Color32(255, ToByte(ny * 0.5f + 0.5f), 255, ToByte(nx * 0.5f + 0.5f));
                }
            }
            return dst;
        }

        /// <summary>
        /// A plain preview shade of an AG normal + albedo + emission (for the offline harness and for
        /// in-game dumps): Lambert with one light from the upper left, plus the emission added.
        /// Not a render of the game shader — just enough to judge whether the relief and the glow sit
        /// where the eye expects them.
        /// </summary>
        public static Color32[] PreviewShade(Color32[] albedo, Color32[] normalAG, Color32[] emission, float emissionScale, int w, int h)
        {
            var dst = new Color32[w * h];
            float lx = -0.45f, ly = 0.55f, lz = 0.70f;
            float ll = (float)Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
            for (int i = 0; i < w * h; i++)
            {
                float nx = 0f, ny = 0f, nz = 1f;
                if (normalAG != null)
                {
                    nx = (normalAG[i].a / 255f) * 2f - 1f; ny = (normalAG[i].g / 255f) * 2f - 1f;
                    nz = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                }
                float ndl = Math.Max(0f, nx * lx + ny * ly + nz * lz);
                float light = 0.18f + 0.82f * ndl;
                float r = albedo != null ? albedo[i].r / 255f : 0.5f, g = albedo != null ? albedo[i].g / 255f : 0.5f, b = albedo != null ? albedo[i].b / 255f : 0.5f;
                r *= light; g *= light; b *= light;
                if (emission != null)
                {
                    float ea = emission[i].a / 255f * emissionScale;
                    r += emission[i].r / 255f * ea; g += emission[i].g / 255f * ea; b += emission[i].b / 255f * ea;
                }
                dst[i] = new Color32(ToByte(r), ToByte(g), ToByte(b), 255);
            }
            return dst;
        }

        // ------------------------------------------------------------------
        // Colour helpers
        // ------------------------------------------------------------------

        public static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055) / 1.055, 2.4);
        public static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;

        /// <summary>Nearest-neighbour resample (for matching a mask to a bark texture's size).</summary>
        public static Color32[] Resample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(sh - 1, y * sh / dh);
                for (int x = 0; x < dw; x++) dst[y * dw + x] = src[sy * sw + Math.Min(sw - 1, x * sw / dw)];
            }
            return dst;
        }

        // ------------------------------------------------------------------
        // PNG codec — no System.Drawing, no NuGet; RGBA8 out, 8-bit gray/RGB/RGBA in.
        // Pixel order matches Unity (row 0 = bottom); PNG stores top row first, so both
        // directions flip vertically.
        // ------------------------------------------------------------------

        public static byte[] EncodePng(int w, int h, Color32[] px)
        {
            var raw = new byte[h * (w * 4 + 1)];
            int o = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                raw[o++] = 0; // filter: none
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    raw[o++] = c.r; raw[o++] = c.g; raw[o++] = c.b; raw[o++] = c.a;
                }
            }
            byte[] z = Zlib(raw);
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
                ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // 8-bit RGBA, deflate, none, no interlace
                Chunk(ms, "IHDR", ihdr);
                Chunk(ms, "IDAT", z);
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        public static Color32[] DecodePng(byte[] data, out int w, out int h)
        {
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) throw new InvalidDataException("not a PNG");
            int pos = 8; w = 0; h = 0; int depth = 0, ctype = 0, interlace = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= data.Length)
            {
                int len = (int)ReadBE(data, pos); string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                int body = pos + 8;
                if (type == "IHDR")
                {
                    w = (int)ReadBE(data, body); h = (int)ReadBE(data, body + 4);
                    depth = data[body + 8]; ctype = data[body + 9]; interlace = data[body + 12];
                }
                else if (type == "IDAT") idat.Write(data, body, len);
                else if (type == "IEND") break;
                pos = body + len + 4;
            }
            if (depth != 8) throw new InvalidDataException("only 8-bit PNGs are supported (got " + depth + ")");
            if (interlace != 0) throw new InvalidDataException("interlaced PNGs are not supported");
            int ch = ctype == 0 ? 1 : ctype == 2 ? 3 : ctype == 4 ? 2 : ctype == 6 ? 4 : -1;
            if (ch < 0) throw new InvalidDataException("unsupported PNG colour type " + ctype);

            byte[] zbytes = idat.ToArray();
            byte[] raw;
            using (var zin = new MemoryStream(zbytes, 2, zbytes.Length - 2)) // skip the 2-byte zlib header
            using (var inf = new DeflateStream(zin, CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                inf.CopyTo(outp);
                raw = outp.ToArray();
            }
            int stride = w * ch;
            var prev = new byte[stride];
            var cur = new byte[stride];
            var px = new Color32[w * h];
            int rp = 0;
            for (int row = 0; row < h; row++)
            {
                int filter = raw[rp++];
                Array.Copy(raw, rp, cur, 0, stride); rp += stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= ch ? cur[i - ch] : 0, b = prev[i], c = i >= ch ? prev[i - ch] : 0;
                    int v = cur[i];
                    switch (filter)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) / 2; break;
                        case 4: { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c); break; }
                    }
                    cur[i] = (byte)v;
                }
                int y = h - 1 - row;
                for (int x = 0; x < w; x++)
                {
                    int s = x * ch;
                    Color32 col;
                    if (ch == 1) col = new Color32(cur[s], cur[s], cur[s], 255);
                    else if (ch == 2) col = new Color32(cur[s], cur[s], cur[s], cur[s + 1]);
                    else if (ch == 3) col = new Color32(cur[s], cur[s + 1], cur[s + 2], 255);
                    else col = new Color32(cur[s], cur[s + 1], cur[s + 2], cur[s + 3]);
                    px[y * w + x] = col;
                }
                var t = prev; prev = cur; cur = t;
            }
            return px;
        }

        private static byte[] Zlib(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                for (int i = 0; i < raw.Length; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        private static void Chunk(Stream s, string type, byte[] body)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var len = new byte[4]; WriteBE(len, 0, (uint)body.Length); s.Write(len, 0, 4);
            s.Write(t, 0, 4); s.Write(body, 0, body.Length);
            uint crc = Crc32(t, 0xFFFFFFFFu); crc = Crc32(body, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c, 0, 4);
        }

        private static uint[] s_crcTable;
        private static uint Crc32(byte[] buf, uint crc)
        {
            if (s_crcTable == null)
            {
                s_crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    s_crcTable[n] = c;
                }
            }
            for (int i = 0; i < buf.Length; i++) crc = s_crcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static uint ReadBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    }
}
