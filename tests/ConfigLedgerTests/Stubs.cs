// Hand-written stand-ins for the BepInEx types ConfigMigration names, plus the mod's logger.
//
// Added 2026-09-18, and the reason is worth stating: until then this harness compiled the PURE
// ledger only. Every engine-side rule — that a retirement never touches a key the build still
// binds, that the version stamp only ever rises, that a ledger row naming an unknown key warns
// instead of vanishing — was unmeasured, in the one mod of the three with a live rung of each kind.
//
// Each stub mirrors REAL BepInEx behaviour where the difference could make a test lie, and says so.
// The two sibling mods carry the same stubs; do not let the three drift.

using System;
using System.Collections.Generic;
using System.Globalization;

// ---- the mod's logger --------------------------------------------------------------
// FireLogger routes to BepInEx's ManualLogSource in game. Here it records, because several of the
// migration's guarantees ARE the log line: a step that refuses to act and says nothing is
// indistinguishable from a step that never ran.

namespace FireFront.Utils
{
    public static class FireLogger
    {
        public static readonly List<string> Messages = new List<string>();

        public static void Info(object o)  { Record("info", o); }
        public static void Warn(object o)  { Record("warn", o); }
        public static void Error(object o) { Record("error", o); }

        private static void Record(string level, object o)
        {
            string line = "[" + level + "] " + o;
            Messages.Add(line);
            Console.WriteLine("      " + line);
        }

        public static void Clear() => Messages.Clear();

        public static bool Said(string fragment)
            => Messages.Exists(m => m.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}

// ---- the one Unity type FireConfig touches -----------------------------------------
// FireConfig binds a single KeyboardShortcut, which is the only thing standing between the
// shipping config surface and this harness. Stubbing it is what lets the cross-test below compile
// the REAL FireConfig and check that every slot ConfigLedger names is a key FireConfig binds — the
// join between the two is a pair of bare strings, and a typo in either stamps the version and
// silently never migrates again.

namespace UnityEngine
{
    public enum KeyCode { None = 0, G = 103 }

    /// <summary>Only the clamps FireConfig uses when it reads a value back. Same semantics as Unity's.</summary>
    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Abs(float v) => v < 0f ? -v : v;
        public static int RoundToInt(float v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        public static int CeilToInt(float v) => (int)System.Math.Ceiling(v);
        public static int FloorToInt(float v) => (int)System.Math.Floor(v);
    }
}

namespace BepInEx.Configuration
{
    public struct KeyboardShortcut
    {
        public readonly UnityEngine.KeyCode MainKey;
        public KeyboardShortcut(UnityEngine.KeyCode mainKey) { MainKey = mainKey; }
        public override string ToString() => MainKey.ToString();
    }

    public class AcceptableValueList<T>
    {
        public readonly T[] AcceptableValues;
        public AcceptableValueList(params T[] values) { AcceptableValues = values; }
    }

    public class AcceptableValueRange<T>
    {
        public readonly T MinValue, MaxValue;
        public AcceptableValueRange(T min, T max) { MinValue = min; MaxValue = max; }
    }

    public class ConfigDescription
    {
        public readonly string Description;
        public readonly object AcceptableValues;

        public ConfigDescription(string description, object acceptableValues = null)
        {
            Description = description;
            AcceptableValues = acceptableValues;
        }
    }

    /// <summary>
    /// ORDINAL AND CASE-SENSITIVE, because the real one is: BepInEx's Equals is
    /// `string.Equals(Key, other.Key) &amp;&amp; string.Equals(Section, other.Section)` — the
    /// two-argument overload — over a case-sensitive GetHashCode (read out of libs\BepInEx.dll with
    /// ilspycmd). A case-insensitive stub would let a mis-cased ledger row find its entry here and
    /// miss it in game, so every apply assertion would pass for a reason that does not hold on a
    /// real server. The ctor also throws on the characters the real one rejects, because a
    /// malformed ledger row taking down the whole migration is itself a thing to test.
    /// </summary>
    public class ConfigDefinition
    {
        public readonly string Section;
        public readonly string Key;

        public ConfigDefinition(string section, string key)
        {
            if (section == null || key == null) throw new ArgumentNullException();
            if (section != section.Trim() || key != key.Trim()) throw new ArgumentException("whitespace");
            Section = section;
            Key = key;
        }

        public override bool Equals(object obj)
            => obj is ConfigDefinition d
               && string.Equals(d.Section, Section, StringComparison.Ordinal)
               && string.Equals(d.Key, Key, StringComparison.Ordinal);

        public override int GetHashCode()
            => ((Key ?? "").GetHashCode() * 397) ^ (Section ?? "").GetHashCode();
    }

    public abstract class ConfigEntryBase
    {
        public abstract object BoxedValue { get; set; }
        public abstract object DefaultValue { get; }
        public abstract string GetSerializedValue();
        public abstract void SetSerializedValue(string value);
    }

    public class ConfigEntry<T> : ConfigEntryBase
    {
        private readonly T _default;
        private readonly ConfigDescription _description;
        private T _value;

        /// <summary>
        /// CLAMPS on the way in, like the real setter, which runs ClampValue against the entry's
        /// AcceptableValueRange and returns MinValue or MaxValue rather than refusing.
        /// </summary>
        public T Value
        {
            get => _value;
            set => _value = Clamp(value);
        }

        public ConfigEntry(T defaultValue, ConfigDescription description = null)
        {
            _default = defaultValue;
            _description = description;
            _value = defaultValue;
        }

        private T Clamp(T candidate)
        {
            if (_description != null && _description.AcceptableValues is AcceptableValueRange<T> range
                && candidate is IComparable<T> c)
            {
                if (c.CompareTo(range.MinValue) < 0) return range.MinValue;
                if (c.CompareTo(range.MaxValue) > 0) return range.MaxValue;
            }
            return candidate;
        }

        public override object BoxedValue { get => Value; set => Value = (T)value; }
        public override object DefaultValue => _default;

        /// <summary>Invariant, and lower-case for a bool, because that is how BepInEx spells a config value.</summary>
        public override string GetSerializedValue()
            => Value is bool b ? (b ? "true" : "false") : Convert.ToString(Value, CultureInfo.InvariantCulture);

        /// <summary>
        /// SWALLOWS a value it cannot parse and leaves the entry alone, because the real one does:
        /// ConfigEntryBase.SetSerializedValue is entirely its own try/catch, logging a BepInEx
        /// warning and returning. A stub that threw would make a caller's error handling look
        /// reachable when in game it is dead code.
        /// </summary>
        public override void SetSerializedValue(string value)
        {
            try { Value = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }
            catch { }
        }
    }

    /// <summary>
    /// Enough ConfigFile to bind, look up, remove and save — and to model ORPHANS, which is the
    /// whole reason a retirement has to do real work. BepInEx keeps a line nothing binds in a
    /// private dictionary and writes every one of them back out on each Save, so a key you simply
    /// stop binding rides along in the file forever.
    /// </summary>
    public class ConfigFile
    {
        private readonly Dictionary<ConfigDefinition, ConfigEntryBase> _entries =
            new Dictionary<ConfigDefinition, ConfigEntryBase>();
        private readonly HashSet<string> _orphans = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<string, string> _stored;

        public int SaveCount { get; private set; }
        public string ConfigFilePath { get; set; } = "";

        public int OrphanCount => _orphans.Count;
        public bool HasOrphan(string section, string key) => _orphans.Contains(section + "::" + key);

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue,
                                      ConfigDescription description = null)
            => BindCore(section, key, defaultValue, description);

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
            => BindCore(section, key, defaultValue, new ConfigDescription(description));

        public ConfigEntry<T> Bind<T>(ConfigDefinition definition, T defaultValue,
                                      ConfigDescription description = null)
            => BindCore(definition.Section, definition.Key, defaultValue, description);

        private ConfigEntry<T> BindCore<T>(string section, string key, T defaultValue,
                                           ConfigDescription description)
        {
            if (_stored == null)
            {
                _stored = !string.IsNullOrEmpty(ConfigFilePath) && System.IO.File.Exists(ConfigFilePath)
                    ? FireFront.Config.ConfigLedger.ParseIni(System.IO.File.ReadAllLines(ConfigFilePath))
                    : new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string slot in _stored.Keys) _orphans.Add(slot);
            }

            // Binding a key is what takes it OUT of the orphan set, in the real ConfigFile as here.
            _orphans.Remove(section + "::" + key);

            var def = new ConfigDefinition(section, key);
            if (_entries.TryGetValue(def, out ConfigEntryBase existing)) return (ConfigEntry<T>)existing;

            var entry = new ConfigEntry<T>(defaultValue, description);
            if (_stored.TryGetValue(section + "::" + key, out string raw)) entry.SetSerializedValue(raw);

            _entries[def] = entry;
            return entry;
        }

        public bool ContainsKey(ConfigDefinition def) => _entries.ContainsKey(def);
        public ConfigEntryBase this[ConfigDefinition def] => _entries[def];
        public virtual bool Remove(ConfigDefinition def) => _entries.Remove(def);
        public void Save() { SaveCount++; }
    }

    /// <summary>
    /// A ConfigFile whose Remove throws, standing in for the realistic way a retirement fails on a
    /// real machine: Bind and Remove both touch the file (BepInEx saves after each newly created
    /// entry), so a transient lock from antivirus, cloud sync, a config manager or a second process
    /// in the same directory takes one of them down. Nothing about the migration is wrong in that
    /// case — it simply has to be told, so it can withhold the stamp and try again next boot.
    /// </summary>
    public class ThrowingConfigFile : ConfigFile
    {
        public override bool Remove(ConfigDefinition def)
            => throw new System.IO.IOException("the file is locked by another process");
    }
}
