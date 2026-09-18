// From Valkyrie's Cargo's ConfigMigration.cs (itself from WingsoftheValkyrie's, Wu'barrk, RGlabs84) - the family's shape; ported 2026-09-18 at the owner's word.
using System;
using System.Globalization;
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

        /// <summary>
        /// How many steps <see cref="Apply"/> REFUSED this boot. A refusal is almost always a bug
        /// in our own ledger rather than in the owner's file — a row naming a key this build does
        /// not bind, or retiring one it still does — plus the one that is nobody's bug, a drop that
        /// threw. Counted because <see cref="LastSummary"/> is written in <see cref="Begin"/> from
        /// the plan's INTENT, before a single step has run, and the console command reads it back
        /// verbatim. Without this it says "1 retired key dropped" for a key still sitting in the
        /// file, and the only contradiction is a warning several hundred log lines earlier.
        /// </summary>
        private static int _refused;

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

                // A step that failed for a reason that could succeed next time must WITHHOLD the
                // stamp, or the retry it deserves never happens: a stamped file takes the
                // AlreadyCurrent path on every future boot.
                bool appliedCleanly = true;
                if (_plan != null && safeToFinish && cfg != null) appliedCleanly = Apply(cfg, _plan);

                if (_state == MigrationState.Failed)
                    FireLogger.Warn("Config migration did not run this boot; every value is as it was, and it will be migrated on the next successful boot.");

                if (versionEntry != null)
                {
                    // THE STAMP ONLY EVER GOES UP, corrected 2026-09-18. A file carrying a HIGHER
                    // version was written by a newer build whose rungs have already run, and this
                    // build knows nothing about them. An unconditional assignment drags it down on
                    // a rollback; rolling forward then replays those rungs against values the owner
                    // has since chosen — and a rebase cannot tell a deliberate choice from the old
                    // default it happens to equal. That is this file's worst possible failure.
                    if (safeToFinish && appliedCleanly)
                    {
                        if (versionEntry.Value < ConfigLedger.CurrentVersion)
                            versionEntry.Value = ConfigLedger.CurrentVersion;
                    }
                    else
                        FireLogger.Warn("Config migration did not finish cleanly; ConfigVersion is left unstamped so the next boot retries instead of treating this one as done.");
                }
                // LastSummary was written in Begin, from the plan's INTENT, before anything ran.
                // The console command reads it back verbatim, so a refused step has to reach it or
                // the one line the owner actually looks at is confidently wrong.
                if (_refused > 0)
                    LastSummary += " — but " + _refused.ToString(CultureInfo.InvariantCulture) +
                                   " step(s) were REFUSED; see the warnings in the log";

                if (cfg != null) cfg.Save();
            }
            catch (Exception ex)
            {
                FireLogger.Error("Config migration could not finish - check the backup beside your config file. Reason: " + ex);
            }
            finally
            {
                _refused = 0;
                _snapshot = null;
                _plan = null;
                _path = null;
                _state = MigrationState.Fresh;
                _backedUp = false;
            }
        }

        /// <summary>
        /// Apply a plan to the bound entries. Split out of <see cref="Finish"/> on 2026-09-18 so the
        /// harness can drive it with a synthetic plan: the shipped ledger has one rung of each kind,
        /// so most of the branches below had never executed anywhere, and the first rung that hits
        /// one of them would have been its first run on somebody's server. Internal rather than
        /// private — the test project compiles this source into its own assembly.
        /// </summary>
        /// <returns>
        /// False when a step failed for a reason that could SUCCEED NEXT TIME — today only a
        /// retirement that threw. That answer gates the version stamp, because a stamped file never
        /// migrates again and a transient file lock must not become permanent.
        ///
        /// A ledger row naming a key this build does not bind is deliberately NOT counted: that
        /// cannot succeed next time either, so withholding the stamp would re-run the whole
        /// migration on every boot forever rather than fixing anything. It warns instead, loudly
        /// and by name, and the plan proceeds.
        /// </returns>
        internal static bool Apply(ConfigFile cfg, ConfigLedger.MigrationPlan plan)
        {
            if (cfg == null || plan == null) return true;
            bool allRetriableStepsSucceeded = true;

            foreach (string slot in plan.ResetToDefault)
            {
                ConfigEntryBase entry = Lookup(cfg, slot);
                if (entry == null) { WarnUnknownSlot(slot); continue; }
                entry.BoxedValue = entry.DefaultValue;
            }

            foreach (string slot in plan.Retired)
            {
                string section, key;
                if (!ConfigLedger.SplitSlot(slot, out section, out key)) continue;

                // A RETIREMENT MUST NEVER TOUCH A KEY THIS BUILD STILL BINDS. Relying on Bind's
                // cast to throw is not protection: BepInEx returns the EXISTING entry for an
                // already-bound definition, so the cast fails only when the type differs, and a
                // still-bound STRING key would be bound and then removed in silence — deleting the
                // owner's value with nothing thrown and nothing logged. Today's rung targets
                // Debug.VerboseLogging, which this build genuinely no longer binds, so this is a
                // guard against the next rung rather than a fix to a live fault.
                if (Lookup(cfg, slot) != null)
                {
                    _refused++;
                    FireLogger.Warn(
                        "Config migration wanted to retire " + slot + ", but this build still binds that key. " +
                        "Nothing was removed - retiring a live setting would delete your value. This is a bug " +
                        "in ConfigLedger, not in your file.");
                    continue;
                }

                if (!ConsumeRetiredKey(cfg, section, key)) allRetriableStepsSucceeded = false;
            }

            return allRetriableStepsSucceeded;
        }

        /// <summary>The bound entry for a "Section::Key" slot, or null when this build does not bind it.</summary>
        private static ConfigEntryBase Lookup(ConfigFile cfg, string slot)
        {
            string section, key;
            if (!ConfigLedger.SplitSlot(slot, out section, out key)) return null;

            // ConfigDefinition's constructor THROWS on null, on leading or trailing whitespace, and
            // on = \n \t \ " ' [ ] in either part, so a malformed ledger row would otherwise take
            // the whole of Finish down rather than just its own step.
            try
            {
                var def = new ConfigDefinition(section, key);
                return cfg.ContainsKey(def) ? cfg[def] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The ledger names a key this build does not bind. Only reachable by editing one file and
        /// not the other — and the skip used to be SILENT, which made it permanent: the version
        /// stamps and the step never runs again. Both sibling mods warn; this one now does too.
        /// </summary>
        private static void WarnUnknownSlot(string slot)
        {
            _refused++;
            FireLogger.Warn(
                "Config migration wanted to touch " + slot + " but this build binds no such key. " +
                "Nothing was changed for it. This is a bug in ConfigLedger, not in your file.");
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
        internal static bool Backup(string path, int fromVersion)
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
        /// <returns>
        /// False when the drop THREW, which is not harmless and used to be logged as though it
        /// were. Bind and Remove share one try block, and Bind does real file I/O (BepInEx saves
        /// after each newly created entry), so a transient lock — antivirus, cloud sync, a config
        /// manager, a second process in the same directory — leaves the key bound and never
        /// removed. Reporting that upward is what stops <see cref="Finish"/> stamping the version
        /// as though the retirement had happened; a stamped file never migrates again, so the
        /// failure would otherwise be permanent and silent, on the only live retire rung of the
        /// three sibling mods.
        /// </returns>
        private static bool ConsumeRetiredKey(ConfigFile cfg, string section, string key)
        {
            try
            {
                var def = new ConfigDefinition(section, key);
                cfg.Bind<string>(def, "");
                cfg.Remove(def);
                return true;
            }
            catch (Exception ex)
            {
                _refused++;
                FireLogger.Error("Could not drop the retired key " + section + "." + key +
                    " from the config file. The layout version is left unstamped so the next boot tries again. Reason: " + ex.Message);
                return false;
            }
        }
    }
}
