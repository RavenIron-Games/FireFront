using System;
using System.Collections.Generic;
using System.Globalization;

namespace FireFront.Config
{
    /// <summary>
    /// The config migration's decisions, PURE and off-game: no BepInEx, no Unity, so
    /// tests/ConfigLedgerTests runs it on plain .NET in a second. Shape from Wu'barrk's
    /// WingsoftheValkyrie ConfigMigration.cs as ported into Valkyrie's Cargo on 2026-09-16
    /// (the family's: TortalPortal, Fatty, Wings, Cargo): a stamped layout version under
    /// [Meta], a rebase table keyed by the version it produces, and the one rule that
    /// matters - a stored value still equal to an OLD default belongs to the mod and moves
    /// to the new default; anything else is an admin's and is kept, by name, in the log.
    /// This mod adds one more kind of step: a RETIRED key, one an old build wrote and no
    /// current build binds, is dropped from the file instead of riding along forever as a
    /// BepInEx orphan.
    ///
    /// Why this exists here: BepInEx persists every bound value to disk, so a changed
    /// default never reaches an install that already wrote the key. FireFront hit that
    /// twice and answered both times by hand - 0.18.7 renamed VerboseLogging to
    /// DebugLogging so a stored 'true' could not follow it, and 0.19.14 could only tell
    /// people to set the smoulder threshold themselves after 0.19.13 moved its default.
    /// Both are this table's first rung, so those installs finally get what the changelog
    /// promised.
    ///
    /// Version numbers (backfilled; nothing before 0.21.3 stamped one):
    ///   0 = any unstamped file, whatever build wrote it.
    ///   1 = 0.21.3: Visuals.SmoulderAfterFraction still at 0.45 moves to 0.65; the orphan
    ///       Debug.VerboseLogging is dropped.
    ///   2 = 0.21.9: Ground.GroundVfxMaxConcurrent still at 30 moves to 200 - at 30 against a
    ///       GroundMaxConcurrent of 50, two cells in five burned invisibly. Current.
    /// </summary>
    public static class ConfigLedger
    {
        public const string MetaSection = "Meta";
        public const string VersionKey = "ConfigVersion";
        public const int CurrentVersion = 2;

        /// <summary>One slot's every old default, from Wings' shape. A stored value equal to ANY of them is the mod's own old default, not admin work.</summary>
        public sealed class Rebase
        {
            public string Section;
            public string Key;
            public string[] OldDefaults;
        }

        /// <summary>A key an old build wrote that no current build binds. Present in the file, it is dropped; absent, nothing happens.</summary>
        public sealed class Retire
        {
            public string Section;
            public string Key;
        }

        /// <summary>Keyed by the version the step produces: <c>Rebases[n]</c> takes a file at n-1 up to n.</summary>
        private static readonly Dictionary<int, Rebase[]> Rebases = new Dictionary<int, Rebase[]>
        {
            { 1, new[]
                {
                    // 0.19.13 moved the default 0.45 -> 0.65 ("flames carry most of the burn and
                    // smouldering is the tail"); 0.19.14 could only tell people to set it by hand.
                    // net472 writes the float as "0.45" (invariant), so that is the text to match.
                    new Rebase { Section = "Visuals", Key = "SmoulderAfterFraction", OldDefaults = new[] { "0.45" } },
                }
            },
            { 2, new[]
                {
                    // 0.21.9 moved the default 30 -> 200. At 30, with GroundMaxConcurrent at 50, two
                    // cells in five burned invisibly - and a stored 30 is the mod's own old default,
                    // not a choice anyone made, so it has to follow or the fix reaches nobody who
                    // already has a config file. Anything else in that slot is admin work and is kept.
                    new Rebase { Section = "Ground", Key = "GroundVfxMaxConcurrent", OldDefaults = new[] { "30" } },
                }
            },
        };

        /// <summary>Same keying. Retired in version 1: the pre-0.18.7 debug key, renamed away so a stored 'true' could not follow.</summary>
        private static readonly Dictionary<int, Retire[]> Retirements = new Dictionary<int, Retire[]>
        {
            { 1, new[]
                {
                    new Retire { Section = "Debug", Key = "VerboseLogging" },
                }
            },
        };

        /// <summary>One slot whose stored value was NOT an old default - real admin work, kept and named in the log.</summary>
        public struct KeptSlot
        {
            public string Slot;
            public string Value;
        }

        /// <summary>The whole plan a file's snapshot produces. Nothing to do plans an empty one.</summary>
        public sealed class MigrationPlan
        {
            public int FromVersion;
            public int ToVersion;
            public List<string> ResetToDefault = new List<string>();
            public List<KeptSlot> Kept = new List<KeptSlot>();
            public List<string> Retired = new List<string>();
        }

        public static string Slot(string section, string key) => section + "::" + key;

        /// <summary>The inverse of <see cref="Slot"/>. False for anything that is not "Section::Key".</summary>
        public static bool SplitSlot(string slot, out string section, out string key)
        {
            section = null; key = null;
            if (slot == null) return false;
            int i = slot.IndexOf("::", StringComparison.Ordinal);
            if (i <= 0 || i + 2 >= slot.Length) return false;
            section = slot.Substring(0, i);
            key = slot.Substring(i + 2);
            return true;
        }

        /// <summary>
        /// BepInEx config files are plain INI: `[Section]` headers, `#` comments, blank lines, and
        /// `Key = value` where the value may itself contain `=` or `,`. Keyed "Section::Key"; the
        /// last duplicate wins; values trimmed. Never throws.
        ///
        /// ORDINAL AND CASE-SENSITIVE, corrected 2026-09-18. This was OrdinalIgnoreCase, which does
        /// not match BepInEx: `ConfigDefinition.Equals` is `string.Equals(Key, other.Key) &&
        /// string.Equals(Section, other.Section)` — the two-argument overload — over a
        /// case-sensitive `GetHashCode` (read out of libs\BepInEx.dll with ilspycmd). So
        /// `smoulderafterfraction` and `SmoulderAfterFraction` are two DIFFERENT keys there: one
        /// binds, the other sits in the orphan table and is rewritten on every save.
        ///
        /// The snapshot is this migration's whole model of what is in the file, so it has to be
        /// BepInEx's model rather than a friendlier one. Ignoring case makes a rebase claim it moved
        /// a value it never reached, and makes a retirement report dropping a line that is still
        /// there. The same mistake was found in all three ports of this code on the same day.
        /// </summary>
        public static Dictionary<string, string> ParseIni(IEnumerable<string> lines)
        {
            var into = new Dictionary<string, string>(StringComparer.Ordinal);
            if (lines == null) return into;

            string section = "";
            foreach (string rawLine in lines)
            {
                if (rawLine == null) continue;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                into[Slot(section, key)] = value;
            }
            return into;
        }

        /// <summary>The stamped `Meta.ConfigVersion`, or 0 when absent or unparseable (a pre-migration file, by definition).</summary>
        public static int ReadVersion(Dictionary<string, string> snapshot)
        {
            string raw;
            if (snapshot != null && snapshot.TryGetValue(Slot(MetaSection, VersionKey), out raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                return version;
            }
            return 0;
        }

        /// <summary>
        /// What migrating `snapshot` from `fileVersion` to <see cref="CurrentVersion"/> does. A missing
        /// snapshot (fresh install) or a file already current plans nothing. Steps apply in version
        /// order. Never throws.
        /// </summary>
        public static MigrationPlan Plan(Dictionary<string, string> snapshot, int fileVersion) =>
            Plan(snapshot, fileVersion, CurrentVersion, Rebases, Retirements);

        /// <summary>
        /// The same thing against tables supplied by the caller, and the whole implementation — the
        /// overload above is one line of delegation, so this is not a parallel code path. Added
        /// 2026-09-18 so the harness can reach rules the one shipped rung of each kind cannot
        /// exercise: two rungs naming one key, a rung at a version the file has already passed, an
        /// empty table. Both sibling mods carry the same seam.
        /// </summary>
        public static MigrationPlan Plan(
            Dictionary<string, string> snapshot,
            int fileVersion,
            int toVersion,
            Dictionary<int, Rebase[]> rebases,
            Dictionary<int, Retire[]> retirements)
        {
            var plan = new MigrationPlan { FromVersion = fileVersion, ToVersion = toVersion };
            if (snapshot == null || snapshot.Count == 0) return plan;
            if (fileVersion >= toVersion) return plan;

            // A NEGATIVE stamp is a hand-edited or corrupt file and must not become a loop bound.
            // ConfigVersion is bound without an AcceptableValueRange (correctly — BepInEx clamps
            // silently, and a ceiling would one day refuse the stamp), so nothing stops an owner
            // typing -2000000000, and an unclamped window counts all the way up from there on the
            // boot thread. Measured at eighteen to twenty seconds in the sibling repos. A file
            // claiming to predate version 0 simply IS a version 0 file.
            int from = fileVersion < 0 ? 0 : fileVersion;

            // One slot, one decision. Every rung is judged against the SAME unchanged snapshot — the
            // stored value never advances along the ladder — so without this a key named twice is
            // reported and reset once per rung, and a slot an earlier rung claimed can be
            // re-classified by a later one. First match owns it. This matters more here than in the
            // siblings, because this is the only ledger with rungs of two different kinds live at
            // once: a key must never be both rebased and retired.
            var decided = new HashSet<string>(StringComparer.Ordinal);

            for (int version = from + 1; version <= toVersion; version++)
            {
                Rebase[] steps;
                if (rebases != null && rebases.TryGetValue(version, out steps) && steps != null)
                {
                    foreach (Rebase r in steps)
                    {
                        if (r == null) continue;
                        string slot = Slot(r.Section, r.Key);
                        if (decided.Contains(slot)) continue;

                        string stored;
                        if (!snapshot.TryGetValue(slot, out stored)) continue;

                        decided.Add(slot);

                        bool wasOldDefault = false;
                        if (r.OldDefaults != null)
                        {
                            foreach (string oldDefault in r.OldDefaults)
                            {
                                if (string.Equals(stored.Trim(), oldDefault, StringComparison.Ordinal)) { wasOldDefault = true; break; }
                            }
                        }

                        if (wasOldDefault) plan.ResetToDefault.Add(slot);
                        else plan.Kept.Add(new KeptSlot { Slot = slot, Value = stored });
                    }
                }

                Retire[] retireSteps;
                if (retirements != null && retirements.TryGetValue(version, out retireSteps) && retireSteps != null)
                {
                    foreach (Retire r in retireSteps)
                    {
                        if (r == null) continue;
                        string slot = Slot(r.Section, r.Key);
                        if (decided.Contains(slot)) continue;
                        if (!snapshot.ContainsKey(slot)) continue;

                        decided.Add(slot);
                        plan.Retired.Add(slot);
                    }
                }
            }
            return plan;
        }

        /// <summary>
        /// One boot line: `"config: version 0 -> 1: 1 value moved to its new default (Visuals.SmoulderAfterFraction);
        /// 1 retired key dropped (Debug.VerboseLogging)"`, or `"... nothing to migrate"`. Kept values are
        /// named with what they hold, so an admin can see the migration read them and left them. Never throws.
        /// </summary>
        public static string Describe(MigrationPlan plan)
        {
            if (plan == null) return "config: nothing to migrate";

            var parts = new List<string>();
            if (plan.ResetToDefault.Count > 0)
                parts.Add(Count(plan.ResetToDefault.Count, "value") + " moved to " + (plan.ResetToDefault.Count == 1 ? "its" : "their") +
                          " new default" + (plan.ResetToDefault.Count == 1 ? "" : "s") + " (" + Names(plan.ResetToDefault) + ")");
            if (plan.Kept.Count > 0)
            {
                var kept = new List<string>();
                foreach (KeptSlot k in plan.Kept) kept.Add(Dotted(k.Slot) + "=" + k.Value);
                parts.Add(Count(plan.Kept.Count, "value") + " kept as yours (" + string.Join(", ", kept.ToArray()) + ")");
            }
            if (plan.Retired.Count > 0)
                parts.Add(Count(plan.Retired.Count, "retired key") + " dropped (" + Names(plan.Retired) + ")");

            string clause = parts.Count == 0 ? "nothing to migrate" : string.Join("; ", parts.ToArray());
            return "config: version " + plan.FromVersion.ToString(CultureInfo.InvariantCulture) + " -> " +
                   plan.ToVersion.ToString(CultureInfo.InvariantCulture) + ": " + clause;
        }

        private static string Count(int n, string noun) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

        private static string Names(List<string> slots)
        {
            var names = new List<string>();
            foreach (string s in slots) names.Add(Dotted(s));
            return string.Join(", ", names.ToArray());
        }

        private static string Dotted(string slot) => slot.Replace("::", ".");
    }
}
