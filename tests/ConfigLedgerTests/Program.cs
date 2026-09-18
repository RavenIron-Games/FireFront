using System;
using FireFront.Utils;
using BepInEx.Configuration;
using System.IO;
using System.Collections.Generic;
using FireFront.Config;

namespace FireFront.Tests
{
    /// <summary>
    /// Off-game harness for ConfigLedger, the pure half of the config migration. No test
    /// framework by design (the family's shape, same as Ragnarok's Wrath's CoreTests): a
    /// console program whose exit code is the verdict adds no dependency to keep current.
    ///
    /// What it is for: a migration's mistakes are silent. A default that should have moved
    /// and did not is an install stuck on the old behaviour with nothing in the log; an
    /// admin's value that should have been kept and was reset is worse. Both are decided
    /// entirely inside ConfigLedger.Plan, from text, so both are checkable without a game.
    /// </summary>
    public static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) { _passed++; return; }
            _failed++;
            Console.WriteLine("FAIL  " + name + (detail != null ? "  [" + detail + "]" : ""));
        }

        private static Dictionary<string, string> Snapshot(params string[] lines) => ConfigLedger.ParseIni(lines);

        public static int Main()
        {
            // --- ParseIni ---------------------------------------------------------------------
            var ini = Snapshot(
                "## Settings file was created by plugin FireFront v0.21.2",
                "",
                "[Visuals]",
                "",
                "## Fraction of the burn after which flames drop to embers.",
                "# Setting type: Single",
                "# Default value: 0.65",
                "SmoulderAfterFraction = 0.45",
                "VfxPrefabName = a=b,c",
                "  Spaced Key  =  spaced value  ",
                "[Debug]",
                "VerboseLogging = true",
                "VerboseLogging = false");
            Check("ParseIni: sectioned key", ini["Visuals::SmoulderAfterFraction"] == "0.45");
            Check("ParseIni: value keeps its own '=' and ','", ini["Visuals::VfxPrefabName"] == "a=b,c");
            Check("ParseIni: key and value trimmed", ini["Visuals::Spaced Key"] == "spaced value");
            Check("ParseIni: last duplicate wins", ini["Debug::VerboseLogging"] == "false");
            // CORRECTED 2026-09-18. This asserted the opposite, and stated it as a fact about
            // BepInEx. ConfigDefinition.Equals is the two-argument string.Equals over a
            // case-sensitive GetHashCode, so a mis-cased line is a DIFFERENT key there: it binds
            // nothing and becomes an orphan. A snapshot that collapsed the two would make a rebase
            // claim it moved a value it never reached, and a retirement report dropping a line
            // that is still sitting in the file.
            Check("ParseIni: lookup is ORDINAL and case-SENSITIVE, matching BepInEx's own keys",
                !ini.ContainsKey("visuals::smoulderafterfraction"));
            Check("ParseIni: and still finds the key spelled the way BepInEx wrote it",
                ini.ContainsKey("Visuals::SmoulderAfterFraction"));
            Check("ParseIni: comments and blanks are not keys", ini.Count == 4, "count=" + ini.Count);
            Check("ParseIni: null input is an empty snapshot", ConfigLedger.ParseIni(null).Count == 0);

            // --- ReadVersion ------------------------------------------------------------------
            Check("ReadVersion: absent is 0", ConfigLedger.ReadVersion(ini) == 0);
            Check("ReadVersion: stamped 1", ConfigLedger.ReadVersion(Snapshot("[Meta]", "ConfigVersion = 1")) == 1);
            Check("ReadVersion: garbage is 0", ConfigLedger.ReadVersion(Snapshot("[Meta]", "ConfigVersion = one")) == 0);
            Check("ReadVersion: null snapshot is 0", ConfigLedger.ReadVersion(null) == 0);

            // --- Plan: the 0.19.13 threshold ----------------------------------------------------
            var atOldDefault = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"), 0);
            Check("Plan: 0.45 is the old default and moves", atOldDefault.ResetToDefault.Contains("Visuals::SmoulderAfterFraction"));
            Check("Plan: nothing kept when only the old default is present", atOldDefault.Kept.Count == 0);
            Check("Plan: from 0 to current", atOldDefault.FromVersion == 0 && atOldDefault.ToVersion == ConfigLedger.CurrentVersion);

            var adminValue = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.5"), 0);
            Check("Plan: an admin's 0.5 is kept", adminValue.ResetToDefault.Count == 0 && adminValue.Kept.Count == 1);
            Check("Plan: the kept slot names its value", adminValue.Kept[0].Slot == "Visuals::SmoulderAfterFraction" && adminValue.Kept[0].Value == "0.5");

            var handSet = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.65"), 0);
            Check("Plan: a hand-set 0.65 (the 0.19.14 advice) is kept, not reset", handSet.ResetToDefault.Count == 0 && handSet.Kept.Count == 1);

            // THE NEAR MISS. An old default is matched as EXACT TEXT, never as a prefix or a
            // number. "0.455" begins with "0.45" and is nobody's default - it is an admin who
            // tuned the threshold by hand, and a loose comparison would reset it and report the
            // reset as routine. This is the shape of data loss this table exists to prevent.
            var nearMiss = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.455"), 0);
            Check("Plan: '0.455' is not '0.45' - an old default matches as exact text, never as a prefix",
                nearMiss.ResetToDefault.Count == 0 && nearMiss.Kept.Count == 1);

            var absent = ConfigLedger.Plan(Snapshot("[Visuals]", "MaxFlameHeight = 30"), 0);
            Check("Plan: a file without the key plans nothing for it", absent.ResetToDefault.Count == 0 && absent.Kept.Count == 0);

            // --- Plan: the 0.18.7 rename ----------------------------------------------------------
            var orphan = ConfigLedger.Plan(Snapshot("[Debug]", "VerboseLogging = true", "DebugLogging = false"), 0);
            Check("Plan: the orphan VerboseLogging is retired", orphan.Retired.Contains("Debug::VerboseLogging"));
            Check("Plan: the live DebugLogging is not touched", !orphan.Retired.Contains("Debug::DebugLogging") && orphan.ResetToDefault.Count == 0);

            var noOrphan = ConfigLedger.Plan(Snapshot("[Debug]", "DebugLogging = true"), 0);
            Check("Plan: nothing to retire when the orphan is absent", noOrphan.Retired.Count == 0);

            // --- Plan: already current, fresh, empty ------------------------------------------------
            var current = ConfigLedger.Plan(Snapshot("[Meta]", "ConfigVersion = 1", "[Visuals]", "SmoulderAfterFraction = 0.45", "[Debug]", "VerboseLogging = true"), 1);
            Check("Plan: a file at the current version plans nothing, even with old-looking values", current.ResetToDefault.Count == 0 && current.Retired.Count == 0 && current.Kept.Count == 0);
            Check("Plan: a future version plans nothing", ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"), 99).ResetToDefault.Count == 0);
            Check("Plan: an empty snapshot plans nothing", ConfigLedger.Plan(new Dictionary<string, string>(), 0).ResetToDefault.Count == 0);
            Check("Plan: a null snapshot plans nothing and does not throw", ConfigLedger.Plan(null, 0).ResetToDefault.Count == 0);

            // --- Plan: the whole first rung at once --------------------------------------------------
            var both = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45", "[Debug]", "VerboseLogging = true", "DebugLogging = false"), 0);
            Check("Plan: one boot does both steps", both.ResetToDefault.Count == 1 && both.Retired.Count == 1 && both.Kept.Count == 0);

            // --- Describe -----------------------------------------------------------------------------
            string bothLine = ConfigLedger.Describe(both);
            Check("Describe: names the moved value", bothLine.Contains("1 value moved to its new default (Visuals.SmoulderAfterFraction)"), bothLine);
            Check("Describe: names the dropped key", bothLine.Contains("1 retired key dropped (Debug.VerboseLogging)"), bothLine);
            Check("Describe: carries the version span", bothLine.StartsWith("config: version 0 -> " + ConfigLedger.CurrentVersion + ": "), bothLine);
            string keptLine = ConfigLedger.Describe(adminValue);
            Check("Describe: a kept value is shown with what it holds", keptLine.Contains("kept as yours (Visuals.SmoulderAfterFraction=0.5)"), keptLine);
            Check("Describe: nothing to migrate reads as such", ConfigLedger.Describe(absent).EndsWith("nothing to migrate"));
            Check("Describe: null plan does not throw", ConfigLedger.Describe(null).Length > 0);

            // --- Slot helpers ----------------------------------------------------------------------------
            string sec, key;
            Check("SplitSlot: round-trips", ConfigLedger.SplitSlot(ConfigLedger.Slot("A", "B"), out sec, out key) && sec == "A" && key == "B");
            Check("SplitSlot: rejects a bare key", !ConfigLedger.SplitSlot("NoSeparator", out sec, out key));
            Check("SplitSlot: rejects null", !ConfigLedger.SplitSlot(null, out sec, out key));

            // --- The four fixes of 2026-09-18, each pinned ------------------------------------------

            // A hand-edited or corrupt stamp must not become a loop bound. ConfigVersion carries no
            // AcceptableValueRange (correctly - BepInEx clamps silently), so a large negative number
            // is reachable by hand, and an unclamped window counted all the way up from it.
            var negClock = System.Diagnostics.Stopwatch.StartNew();
            var fromNegative = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"), -2000000000);
            negClock.Stop();
            Check("Plan: a wildly negative stamp is treated as version 0, not as a loop bound",
                fromNegative.ResetToDefault.Count == 1);
            Check("Plan: and it costs ONE step rather than two billion",
                negClock.ElapsedMilliseconds < 250, negClock.ElapsedMilliseconds + " ms");

            // One slot, one decision. Every rung is judged against the SAME unchanged snapshot, so
            // without the guard a key named twice is reported and reset once per rung. This ledger
            // is the only one with two KINDS of rung live at once, so it is also the only one where
            // a key could be rebased and retired in the same boot.
            var twoRebases = new Dictionary<int, ConfigLedger.Rebase[]>
            {
                { 1, new[] { new ConfigLedger.Rebase { Section = "Visuals", Key = "SmoulderAfterFraction", OldDefaults = new[] { "0.45" } } } },
                { 2, new[] { new ConfigLedger.Rebase { Section = "Visuals", Key = "SmoulderAfterFraction", OldDefaults = new[] { "0.45" } } } },
            };
            var decidedOnce = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"),
                0, 2, twoRebases, new Dictionary<int, ConfigLedger.Retire[]>());
            Check("Plan: a key named by two rungs is decided ONCE, by the first that matches",
                decidedOnce.ResetToDefault.Count == 1, "decisions=" + decidedOnce.ResetToDefault.Count);

            var rebaseThenRetire = ConfigLedger.Plan(Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"),
                0, 1,
                new Dictionary<int, ConfigLedger.Rebase[]>
                { { 1, new[] { new ConfigLedger.Rebase { Section = "Visuals", Key = "SmoulderAfterFraction", OldDefaults = new[] { "0.45" } } } } },
                new Dictionary<int, ConfigLedger.Retire[]>
                { { 1, new[] { new ConfigLedger.Retire { Section = "Visuals", Key = "SmoulderAfterFraction" } } } });
            Check("Plan: a key already rebased is never ALSO retired in the same boot",
                rebaseThenRetire.ResetToDefault.Count == 1 && rebaseThenRetire.Retired.Count == 0);

            // --- The ENGINE, which this harness could not reach until Stubs.cs existed -------------

            // A config as an affected 0.21.2 install would have left it: the old smoulder default,
            // the renamed debug key, and a line no current build binds.
            var cfg = new ConfigFile();
            var smoulder = cfg.Bind("Visuals", "SmoulderAfterFraction", 0.65f,
                new ConfigDescription("test", new AcceptableValueRange<float>(0f, 1f)));
            var debugLogging = cfg.Bind("Debug", "DebugLogging", false, "test");
            var version = cfg.Bind(ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0, "test");

            // A REBASE puts the entry back to its shipped default, and names the slot when the
            // ledger points at a key this build does not bind - that skip used to be silent, which
            // made a ledger typo invisible and then permanently stamped.
            smoulder.Value = 0.45f;
            var resetPlan = new ConfigLedger.MigrationPlan();
            resetPlan.ResetToDefault.Add(ConfigLedger.Slot("Visuals", "SmoulderAfterFraction"));
            ConfigMigration.Apply(cfg, resetPlan);
            Check("Apply: a rebase puts the entry back to its SHIPPED default",
                Math.Abs(smoulder.Value - 0.65f) < 0.0001f);

            FireLogger.Clear();
            var ghostPlan = new ConfigLedger.MigrationPlan();
            ghostPlan.ResetToDefault.Add(ConfigLedger.Slot("Visuals", "NoSuchKey"));
            bool threw = false;
            try { ConfigMigration.Apply(cfg, ghostPlan); } catch { threw = true; }
            Check("Apply: a ledger row naming an unknown key does not throw", !threw);
            Check("Apply: and SAYS so, rather than skipping in silence and stamping anyway",
                FireLogger.Said("binds no such key"));

            // A RETIREMENT acts on an ORPHAN. BepInEx keeps a line nothing binds and rewrites it on
            // every save, which is why the key has to be bound and then removed rather than ignored.
            string dir = Path.Combine(Path.GetTempPath(), "ff_cfgmig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string orphanPath = Path.Combine(dir, "com.raveniron.firefront.cfg");
                File.WriteAllLines(orphanPath, new[] { "[Debug]", "DebugLogging = false", "VerboseLogging = true" });

                var withOrphan = new ConfigFile { ConfigFilePath = orphanPath };
                withOrphan.Bind("Debug", "DebugLogging", false, "test");
                Check("a key no current build binds survives binding as a BepInEx orphan",
                    withOrphan.HasOrphan("Debug", "VerboseLogging"));

                var retirePlan = new ConfigLedger.MigrationPlan();
                retirePlan.Retired.Add(ConfigLedger.Slot("Debug", "VerboseLogging"));
                ConfigMigration.Apply(withOrphan, retirePlan);
                Check("Apply: a retirement takes the orphan out, so the next Save stops writing it",
                    !withOrphan.HasOrphan("Debug", "VerboseLogging"));
                Check("Apply: and leaves nothing bound behind either",
                    !withOrphan.ContainsKey(new ConfigDefinition("Debug", "VerboseLogging")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // A RETIREMENT MUST NEVER TOUCH A KEY THIS BUILD STILL BINDS. Relying on Bind's cast to
            // throw protects only the types that differ: BepInEx returns the EXISTING entry for an
            // already-bound definition, so a still-bound bool or string would be removed in silence.
            FireLogger.Clear();
            var wrongRetire = new ConfigLedger.MigrationPlan();
            wrongRetire.Retired.Add(ConfigLedger.Slot("Debug", "DebugLogging"));
            ConfigMigration.Apply(cfg, wrongRetire);
            Check("Apply: retiring a key this build STILL binds is refused, not silently obeyed",
                cfg.ContainsKey(new ConfigDefinition("Debug", "DebugLogging")));
            Check("Apply: and the refusal is named in the log",
                FireLogger.Said("still binds that key"));

            threw = false;
            try { ConfigMigration.Apply(null, resetPlan); ConfigMigration.Apply(cfg, null); } catch { threw = true; }
            Check("Apply: survives a null config or a null plan", !threw);

            // A step that failed for a reason that could SUCCEED NEXT TIME must report it, so the
            // stamp is withheld and the boot after this one tries again. A retirement does real
            // file I/O through Bind, so a transient lock — antivirus, cloud sync, a config manager,
            // a second process in the same directory — is the realistic way it throws. Until
            // 2026-09-18 that exception was swallowed and logged as "harmless", and the version
            // stamped anyway: on a stamped file the migration never runs again, so a lock lasting
            // one second made the retirement permanently undone.
            //
            // A slot naming a key this build does not bind is deliberately NOT retriable: it cannot
            // succeed next time either, so withholding the stamp would re-run the whole migration
            // on every boot forever instead of fixing anything. It warns, and the plan proceeds.
            Check("Apply: reports success when every step it could retry succeeded",
                ConfigMigration.Apply(cfg, resetPlan));
            Check("Apply: an unknown ledger slot is a LEDGER bug, not a retriable one - it warns and still reports success",
                ConfigMigration.Apply(cfg, ghostPlan));

            var throwingRetire = new ConfigLedger.MigrationPlan();
            throwingRetire.Retired.Add(ConfigLedger.Slot("Debug", "Throw Me"));
            Check("Apply: a retirement that THREW is reported, so Finish can withhold the stamp and retry next boot",
                !ConfigMigration.Apply(new ThrowingConfigFile(), throwingRetire));

            // THE STAMP ONLY GOES UP. A file written by a NEWER build has already had rungs this
            // build knows nothing about; dragging the stamp down makes the next upgrade replay them
            // against values the owner has since chosen.
            version.Value = 7;
            ConfigMigration.Finish(cfg, version);
            Check("Finish: a stamp ABOVE this build's layout is left where it is",
                version.Value == 7, "stamp=" + version.Value);

            version.Value = 0;
            ConfigMigration.Finish(cfg, version);
            Check("Finish: a stamp BELOW this build's layout is raised to it",
                version.Value == ConfigLedger.CurrentVersion, "stamp=" + version.Value);

            // --- THE CROSS-CHECK: does the ledger name keys this build actually binds? ---------------
            //
            // The join between ConfigLedger and FireConfig is a pair of BARE STRINGS written in two
            // different files. Mistype either half and the plan is built, the log reports the step,
            // the version stamps - and the step reached nothing. It never runs again, because the
            // file now reads as current. Silent, permanent, and invisible to every test above,
            // which only ever asks what the LEDGER says.
            var surface = new ConfigFile();
            FireConfig.Bind(surface);

            // A REBASE must name a key this build binds, or it moves nothing.
            var everyRebase = ConfigLedger.Plan(
                Snapshot("[Visuals]", "SmoulderAfterFraction = 0.45"), 0);
            foreach (string slot in everyRebase.ResetToDefault)
            {
                string rs, rk;
                bool split = ConfigLedger.SplitSlot(slot, out rs, out rk);
                Check("cross: the rebase slot " + slot + " is a key FireConfig actually binds",
                    split && surface.ContainsKey(new ConfigDefinition(rs, rk)));
            }

            // A RETIRE must name a key this build does NOT bind - the exact inverse. Get this
            // backwards and the migration deletes a live setting rather than an orphan.
            var everyRetire = ConfigLedger.Plan(
                Snapshot("[Debug]", "VerboseLogging = true"), 0);
            Check("cross: the shipped ledger still has a retirement to check", everyRetire.Retired.Count > 0);
            foreach (string slot in everyRetire.Retired)
            {
                string ts, tk;
                bool split = ConfigLedger.SplitSlot(slot, out ts, out tk);
                Check("cross: the retired slot " + slot + " is a key FireConfig no longer binds",
                    split && !surface.ContainsKey(new ConfigDefinition(ts, tk)));
            }

            // And the stamp itself, which every future migration reads. If ConfigLedger and
            // FireConfig disagree about where it lives, every file reads as version 0 forever and
            // re-migrates on every single boot.
            Check("cross: FireConfig binds the stamp at the slot ConfigLedger addresses",
                surface.ContainsKey(new ConfigDefinition(ConfigLedger.MetaSection, ConfigLedger.VersionKey)));

            // --- Backup(): four outcomes, none of which had ever executed under a test -------------
            string bakDir = Path.Combine(Path.GetTempPath(), "ff_bak_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bakDir);
            try
            {
                string src = Path.Combine(bakDir, "com.raveniron.firefront.cfg");
                File.WriteAllText(src, "[Visuals]\nSmoulderAfterFraction = 0.45\n");

                Check("Backup: reports success when it writes a copy", ConfigMigration.Backup(src, 0));
                Check("Backup: and the copy lands beside the file as .v0.bak", File.Exists(src + ".v0.bak"));
                Check("Backup: a second run over a byte-identical backup succeeds without writing again",
                    ConfigMigration.Backup(src, 0));
                Check("Backup: and did not multiply the backups", Directory.GetFiles(bakDir, "*.bak").Length == 1);

                // Somebody's only clean copy already lives at that name and is NOT what we are about
                // to migrate - a half-finished earlier run. It must not be clobbered.
                File.WriteAllText(src, "[Visuals]\nSmoulderAfterFraction = 0.65\n");
                Check("Backup: a run over a DIFFERENT existing backup still succeeds",
                    ConfigMigration.Backup(src, 0));
                Check("Backup: by falling back to a timestamped name rather than overwriting the clean copy",
                    Directory.GetFiles(bakDir, "*.bak").Length == 2);

                Check("Backup: reports FAILURE rather than throwing when there is nothing to copy",
                    !ConfigMigration.Backup(Path.Combine(bakDir, "no-such-file.cfg"), 0));
            }
            finally { try { Directory.Delete(bakDir, true); } catch { } }

            // --- THE STAMP GATE, END TO END THROUGH THE REAL LEDGER ---------------------------------
            //
            // A retirement is the one step that can fail for a reason that will not still be true
            // next boot: Bind and Remove both touch the file (BepInEx saves after each newly
            // created entry), so a transient lock from antivirus, cloud sync, a config manager or a
            // second process in the same directory takes it down. If Finish stamps the version
            // anyway, the file reads as current on every future boot, the retirement is never
            // retried, and a one-second lock has become permanent. FireFront is the mod of the
            // three with a LIVE retire rung, so this drives the shipped table rather than a
            // hand-built plan: the throw is supplied by the ConfigFile, not by the ledger.
            string gateDir = Path.Combine(Path.GetTempPath(), "ff_gate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(gateDir);
            try
            {
                string gatePath = Path.Combine(gateDir, "com.raveniron.firefront.cfg");
                File.WriteAllText(gatePath, "[Debug]\nVerboseLogging = true\n");

                // FireConfig.Bind IS the whole migration - it calls Begin first and Finish last -
                // so the boot is reproduced by binding, and nothing here drives either by hand.
                // (Doing it by hand is how this test first passed for the wrong reason: a second
                // Finish runs with no plan left, finds nothing wrong, and stamps.)
                FireLogger.Clear();
                var locked = new ThrowingConfigFile { ConfigFilePath = gatePath };
                FireConfig.Bind(locked);
                var lockedStamp = FireConfig.ConfigVersion;

                // Read the guard conditions first, or a pass here proves only that SOMETHING went
                // wrong. safeToFinish also withholds the stamp when no backup could be written,
                // and that is a different bug with the same symptom.
                Check("gate: the backup was written, so the stamp can only be withheld for the retirement",
                    Directory.GetFiles(gateDir, "*.bak").Length == 1);
                Check("gate: and the retirement really did fail, rather than never being planned",
                    FireLogger.Said("Could not drop the retired key"));
                Check("gate: a retirement that threw leaves ConfigVersion UNSTAMPED, so the next boot migrates again",
                    lockedStamp.Value == 0);
                Check("gate: and says so - an unstamped file that never explains itself is a bug report nobody can read",
                    FireLogger.Said("did not finish cleanly"));

                // The line the owner actually reads. LastSummary is written in Begin from the
                // plan's INTENT, before a step has run, and `firestatus` prints it verbatim - so
                // without a correction it reports "1 retired key dropped" for a key still sitting
                // in the file, and the only contradiction is a warning hundreds of lines earlier.
                // Asserted HERE, before the next boot below overwrites LastSummary.
                Check("summary: the boot whose retirement threw says REFUSED in the line firestatus prints",
                    ConfigMigration.LastSummary.IndexOf("REFUSED", StringComparison.Ordinal) >= 0);
                Check("summary: and still carries what it set out to do, rather than being replaced by the complaint",
                    ConfigMigration.LastSummary.IndexOf("Debug.VerboseLogging", StringComparison.Ordinal) >= 0);

                // The inverse, on the same file, with a ConfigFile that does not throw. The gate
                // has to WITHHOLD rather than block: a migration that can never stamp is its own
                // permanent failure, and it would re-run the retirement on every boot forever.
                FireLogger.Clear();
                File.WriteAllText(gatePath, "[Debug]\nVerboseLogging = true\n");
                var working = new ConfigFile { ConfigFilePath = gatePath };
                FireConfig.Bind(working);
                var workingStamp = FireConfig.ConfigVersion;
                Check("gate: the same migration against an unlocked file stamps as it always did",
                    workingStamp.Value == ConfigLedger.CurrentVersion);
                Check("gate: and dropped the orphan it was retiring",
                    !working.HasOrphan("Debug", "VerboseLogging"));
                Check("summary: and the clean boot straight after the refused one does not inherit its complaint",
                    ConfigMigration.LastSummary.IndexOf("REFUSED", StringComparison.Ordinal) < 0);
            }
            finally { try { Directory.Delete(gateDir, true); } catch { } }

            Console.WriteLine("ConfigLedgerTests: " + _passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
