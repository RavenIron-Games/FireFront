using System;
using System.IO;
using System.Text;
using FireFront.Utils;
using HarmonyLib;

namespace FireFront.Fire
{
    /// <summary>
    /// World-keyed sidecar store for live fire state, so a server restart no
    /// longer erases every burning fire, all spent-fuel state, and — worst of
    /// the old losses — every tree waiting to regrow (burned trees were
    /// permanently gone if the server bounced mid-regrow).
    ///
    /// Pure file IO + path resolution; WHAT gets stored lives in FireManager
    /// (it owns the private state and builds/consumes the line format).
    ///
    /// Path resolution mirrors Ragnarok's Wrath's Persistence.cs, which paid
    /// for these lessons live:
    ///  - Utils.GetSaveDataPath returns "" for Auto/Cloud when Steam Cloud is
    ///    on (cloud saves are addressed by relative path through Steam's API,
    ///    not by filesystem path), so the directory must be asked for with
    ///    FileSource.Local explicitly. Consequence: for a cloud-saved world
    ///    this store stays on this machine and does not travel with the save.
    ///  - World.m_uid is declared long, and FieldRefAccess is type-exact —
    ///    asking for ulong throws instead of converting.
    /// </summary>
    public static class FirePersistence
    {
        private const string FileStem = "firefront_fires";
        public const int FormatVersion = 1;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static bool _pathWarned;

        private static string ResolvePath()
        {
            try
            {
                World world = ZNet.GetWorldIfIsHost();
                if (world == null) return null;

                long uid = AccessTools.FieldRefAccess<World, long>(world, "m_uid");
                // Valheim 1.0.7 deleted World.GetWorldSavePath; SaveSystem.GetWorldsSaveRootPath
                // is the same method rehoused, same body and same "/worlds_local" for Local.
                // 1.0.7 also renumbered FileSource into a [Flags] enum (Local is 2, was 1) —
                // pass the symbol, never the number.
                string dir = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                if (string.IsNullOrEmpty(dir)) return null;

                return Path.Combine(dir, $"{FileStem}_{(ulong)uid}.txt");
            }
            catch (Exception ex)
            {
                if (!_pathWarned)
                {
                    _pathWarned = true;
                    FireLogger.Warn($"[PERSIST] could not resolve store path: {ex.Message} — fire state will not survive restarts.");
                }
                return null;
            }
        }

        /// <summary>Atomic-ish write: old file survives until the new one is complete.</summary>
        public static void Write(string content)
        {
            string path = ResolvePath();
            if (path == null) return;

            try
            {
                string tmp = path + ".tmp";
                string bak = path + ".bak";

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(tmp, content, Utf8NoBom);

                if (File.Exists(path))
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(path, bak);
                }
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                FireLogger.Warn($"[PERSIST] save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Lines of the store, or null when there is nothing to restore (no
        /// file, unresolvable path, or unreadable content — an unreadable file
        /// is quarantined rather than deleted, same policy as Ragnarok's
        /// Wrath: keep the evidence, never crash the load).
        /// </summary>
        public static string[] ReadLines()
        {
            string path = ResolvePath();
            if (path == null || !File.Exists(path)) return null;

            try
            {
                return File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                FireLogger.Warn($"[PERSIST] load failed: {ex.Message} — quarantining the store.");
                try
                {
                    string dead = path + ".corrupt";
                    if (File.Exists(dead)) File.Delete(dead);
                    File.Move(path, dead);
                }
                catch { /* the world keeps turning without the quarantine */ }
                return null;
            }
        }
    }
}
