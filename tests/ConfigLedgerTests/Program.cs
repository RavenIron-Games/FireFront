using System;
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
            Check("ParseIni: lookup ignores case", ini.ContainsKey("visuals::smoulderafterfraction"));
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

            Console.WriteLine("ConfigLedgerTests: " + _passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
