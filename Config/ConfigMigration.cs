// From Valkyrie's Cargo's ConfigMigration.cs (itself from WingsoftheValkyrie's, Wu'barrk, RGlabs84) - the family's shape; ported 2026-09-18 at the owner's word.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using FireFront.Utils;

namespace FireFront.Config
{
    /// <summary>
    /// The engine-facing half of the config migration; the decisions themselves are
    /// <see cref="ConfigLedger"/> (PURE, off-game). Same machinery as the family's (TortalPortal,
    /// Fatty, Wings, Cargo): snapshot the raw file BEFORE any bind, a stamped layout version, a
    /// backup beside the file, and a failed migration never stops the mod loading.
    ///
    /// <see cref="Begin"/> runs before any `config.Bind`; <see cref="Finish"/> runs after every bind,
    /// so every ConfigEntry it might reset exists.
    /// </summary>
    public static class ConfigMigration
    {
        /// <summary>
        /// What <see cref="Begin"/> decided, so <see cref="Finish"/> can tell "nothing to do" from "tried
        /// and could not tell": stamping <c>ConfigVersion</c> on a state the mod never actually reached
        /// makes the failure permanent, because the next boot sees a current file and never retries.
        /// Fresh = no file existed to migrate (a first install). AlreadyCurrent = the file's own stamped
        /// version already meets or beats <see cref="ConfigLedger.CurrentVersion"/>. Planned = a plan was
        /// built. Failed = the <c>try</c> in <see cref="Begin"/> threw before a plan could be built (a
        /// sharing violation on the cfg at boot is the realistic one on Windows) - only this one blocks
        /// the stamp.
        /// </summary>
        private enum MigrationState { Fresh, AlreadyCurrent, Planned, Failed }

        private static Dictionary<string, string> _snapshot;
        private static ConfigLedger.MigrationPlan _plan;
        private static string _path;
        private static MigrationState _state = MigrationState.Fresh;

        /// <summary>Whether <see cref="Backup"/> actually landed a copy (or found an identical one already there) for THIS boot's migration. False with <see cref="_path"/> non-null means the raw file is left untouched and unstamped, on purpose.</summary>
        private static bool _backedUp;

        /// <summary>The last migration's boot line, kept for `firestatus`. Empty when nothing has run.</summary>
        public static string LastSummary { get; private set; } = "";

        public static void Begin(ConfigFile cfg)
        {
            _snapshot = null;
            _plan = null;
            _path = null;
            _state = MigrationState.Fresh;
            _backedUp = false;

            try
            {
                if (cfg == null) return; // Fresh: nothing to migrate, nothing to stamp against
                string path = cfg.ConfigFilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return; // fresh install: new defaults bind on their own (Fresh)

                _snapshot = ConfigLedger.ParseIni(ReadLinesShared(path));
                int fileVersion = ConfigLedger.ReadVersion(_snapshot);
                if (fileVersion >= ConfigLedger.CurrentVersion) { _state = MigrationState.AlreadyCurrent; return; }

                _path = path;
                _plan = ConfigLedger.Plan(_snapshot, fileVersion);
                _state = MigrationState.Planned;
                LastSummary = ConfigLedger.Describe(_plan);

                bool touchesFile = _plan.ResetToDefault.Count > 0 || _plan.Retired.Count > 0;
                // Only a plan that changes something needs the previous file kept. A stamp-only boot
                // (nothing moved, nothing dropped) leaves every value exactly as it was.
                _backedUp = !touchesFile || Backup(path, fileVersion);

                if (!touchesFile)
                {
                    FireLogger.Info(LastSummary + " (stamping the layout version)");
                }
                else if (_backedUp)
                {
                    FireLogger.Warn(LastSummary + " (the previous file is backed up beside it, .v" + fileVersion + ".bak)");
                }
                else
                {
                    FireLogger.Warn(LastSummary + " (no backup could be written, so nothing is changed and the version is left unstamped; this migration retries next boot)");
                }
            }
            catch (Exception ex)
            {
                // A failed migration must never stop the mod loading. Every value binds exactly as it
                // always did; _state stays Failed so Finish neither stamps a version the mod never
                // reached nor drops a key (an unstamped file is what makes the retry possible).
                _state = MigrationState.Failed;
                FireLogger.Error("Config migration could not start. Every value binds as it always did and the migration retries on the next boot. Reason: " + ex);
            }
        }

        /// <summary>
        /// After every bind: reset the slots found at an old default, drop the retired keys (BepInEx keeps
        /// an orphaned line forever otherwise: it lives in `ConfigFile.OrphanedEntries`, a PRIVATE property
        /// - not touched - so this binds each one under a throwaway default, which pulls it out of the
        /// orphan set, then removes it, both public `ConfigFile` API), stamp the version and save.
        ///
        /// The stamp and the drops are gated on the boot having actually backed up the file when the plan
        /// changes it: stamping <c>ConfigVersion</c> when <see cref="Begin"/> failed, or dropping the
        /// admin's only copy of a value with no backup written, both destroy the one thing this migration
        /// promises never to lose. Skipping either is safe - an unstamped file is exactly a pre-migration
        /// file, so the next boot's <see cref="Begin"/> retries the whole thing from scratch.
        /// </summary>
        public static void Finish(ConfigFile cfg, ConfigEntry<int> versionEntry)
        {
            try
            {
                bool migrationAttempted = _path != null;
                bool safeToFinish = _state != MigrationState.Failed && (!migrationAttempted || _backedUp);

                if (_plan != null && safeToFinish && cfg != null)
                {
                    foreach (string slot in _plan.ResetToDefault)
                    {
                        string section, key;
                        if (!ConfigLedger.SplitSlot(slot, out section, out key)) continue;
                        var def = new ConfigDefinition(section, key);
                        if (!cfg.ContainsKey(def)) continue;
                        ConfigEntryBase entry = cfg[def];
                        entry.BoxedValue = entry.DefaultValue;
                    }
                    foreach (string slot in _plan.Retired)
                    {
                        string section, key;
                        if (ConfigLedger.SplitSlot(slot, out section, out key)) ConsumeRetiredKey(cfg, section, key);
                    }
                }

                if (_state == MigrationState.Failed)
                    FireLogger.Warn("Config migration did not run this boot; every value is as it was, and it will be migrated on the next successful boot.");

                if (versionEntry != null)
                {
                    if (safeToFinish)
                        versionEntry.Value = ConfigLedger.CurrentVersion;
                    else
                        FireLogger.Warn("Config migration did not finish cleanly; ConfigVersion is left unstamped so the next boot retries instead of treating this one as done.");
                }
                if (cfg != null) cfg.Save();
            }
            catch (Exception ex)
            {
                FireLogger.Error("Config migration could not finish - check the backup beside your config file. Reason: " + ex);
            }
            finally
            {
                _snapshot = null;
                _plan = null;
                _path = null;
                _state = MigrationState.Fresh;
                _backedUp = false;
            }
        }

        /// <summary>
        /// The raw file, line by line, opened with FileShare.ReadWrite so another process holding the cfg
        /// open for writing (a config manager, an editor, a sync tool - the realistic Windows case) does
        /// not fail the migration the way File.ReadAllLines would.
        /// </summary>
        private static List<string> ReadLinesShared(string path)
        {
            var lines = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// Copies `path` beside itself as `path.v` + `fromVersion` + `.bak` (never overwritten - falls back
        /// to a timestamped name so a half-finished earlier run cannot destroy the only clean copy). Returns
        /// true when a copy now exists on disk for this migration (the fresh copy landed, or an identical
        /// one was already there from an earlier attempt); false means no safe copy of the pre-migration
        /// file exists anywhere, which the caller must treat as a reason not to touch the original. Never throws.
        /// </summary>
        private static bool Backup(string path, int fromVersion)
        {
            try
            {
                string bak = path + ".v" + fromVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                if (File.Exists(bak))
                {
                    if (BytesEqual(bak, path)) return true; // already backed up, byte for byte - nothing more to do

                    // Somebody's only clean copy already lives here and it is NOT what we are about to
                    // migrate - a migration that half-finished on an earlier boot would otherwise be
                    // overwritten. Fall back to a timestamped name instead of clobbering it.
                    bak = path + ".v" + fromVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                          DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                }
                File.Copy(path, bak, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                FireLogger.Error("Could not back up the config before migrating (" + ex.Message + "). Nothing is changed and the version is left unstamped so this retries next boot.");
                return false;
            }
        }

        private static bool BytesEqual(string pathA, string pathB)
        {
            try
            {
                byte[] a = File.ReadAllBytes(pathA);
                byte[] b = File.ReadAllBytes(pathB);
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;
                return true;
            }
            catch
            {
                return false; // can't prove they match - treat as different, which routes to the timestamped fallback
            }
        }

        /// <summary>
        /// Drop a retired key through public `ConfigFile` API alone: `Bind` under a throwaway default reads
        /// whatever is still in `OrphanedEntries` (BepInEx's own `Bind` removes it from there the moment
        /// it binds), and `Remove` then takes the now-bound entry back out of `Entries` too, so neither
        /// collection carries it into the next `Save`. Never names the private property itself.
        /// </summary>
        private static void ConsumeRetiredKey(ConfigFile cfg, string section, string key)
        {
            try
            {
                var def = new ConfigDefinition(section, key);
                cfg.Bind<string>(def, "");
                cfg.Remove(def);
            }
            catch (Exception ex)
            {
                FireLogger.Error("Could not drop the retired key " + section + "." + key + " from the config file (harmless - it is unbound and ignored from here). Reason: " + ex.Message);
            }
        }
    }
}
