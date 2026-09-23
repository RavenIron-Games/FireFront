using System;
using System.Globalization;

namespace FireFront.Utils
{
    /// <summary>
    /// Every number FireFront parses from text or writes as text goes through here, in one
    /// culture: the invariant one. Pure code (no Unity, no Valheim), so the off-game harness
    /// compiles this file as it is and runs it under several cultures.
    ///
    /// Why it exists (0.24): until then the tree had not one InvariantCulture. The fire save file
    /// was written with the machine's culture and read with it, and `fireset` parsed with it, so
    /// a German server read "1.5" as 15 (the dot is its thousands separator) and a German
    /// client's "1,5" reached an English server's BepInEx parser, which is invariant, as 15.
    ///
    /// Reading is tolerant, writing is strict:
    ///  - written: the invariant culture, round-trip precision, ASCII minus;
    ///  - read: the invariant culture, plus two things older files and human typing contain.
    ///    A value with exactly one comma and no dot is a decimal comma ("1,5" is 1.5), which is
    ///    how every pre-0.24 save written on a comma-decimal machine stores its floats. The
    ///    Unicode minus sign (U+2212), which some cultures' number formats use, reads as '-'.
    ///    Thousands separators are never accepted: "1,500.5" and "1.234,5" are refused rather
    ///    than guessed, so a value is either read exactly or reported as unreadable.
    ///  - NaN and the infinities are refused for floats. None of them is a meaningful setting or
    ///    a meaningful saved fire, and a NaN written into a config entry poisons every
    ///    comparison that reads it.
    /// </summary>
    public static class InvariantNumbers
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// The tolerant rewrite described above; does not validate. Returns the input unchanged
        /// when there is nothing to rewrite, and null for null.
        /// </summary>
        public static string Normalize(string raw)
        {
            if (raw == null) return null;
            string s = raw.Trim().Replace('−', '-');
            int comma = s.IndexOf(',');
            if (comma >= 0 && s.IndexOf('.') < 0 && s.IndexOf(',', comma + 1) < 0)
                s = s.Substring(0, comma) + "." + s.Substring(comma + 1);
            return s;
        }

        public static bool TryParseFloat(string raw, out float value)
        {
            value = 0f;
            if (raw == null) return false;
            if (!float.TryParse(Normalize(raw), NumberStyles.Float, Inv, out float v)) return false;
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
            value = v;
            return true;
        }

        public static bool TryParseInt(string raw, out int value)
        {
            value = 0;
            return raw != null && int.TryParse(Normalize(raw), NumberStyles.Integer, Inv, out value);
        }

        public static bool TryParseLong(string raw, out long value)
        {
            value = 0L;
            return raw != null && long.TryParse(Normalize(raw), NumberStyles.Integer, Inv, out value);
        }

        public static bool TryParseUInt(string raw, out uint value)
        {
            value = 0u;
            return raw != null && uint.TryParse(Normalize(raw), NumberStyles.Integer, Inv, out value);
        }

        /// <summary>Throwing forms, for readers that already treat a throw as "skip this line".</summary>
        public static float ParseFloat(string raw)
        {
            if (!TryParseFloat(raw, out float v)) throw new FormatException($"not a finite number: '{raw}'");
            return v;
        }

        public static int ParseInt(string raw)
        {
            if (!TryParseInt(raw, out int v)) throw new FormatException($"not an integer: '{raw}'");
            return v;
        }

        public static long ParseLong(string raw)
        {
            if (!TryParseLong(raw, out long v)) throw new FormatException($"not an integer: '{raw}'");
            return v;
        }

        /// <summary>Round-trip text for a float: parses back to the same bits in any culture.</summary>
        public static string Format(float value) => value.ToString("R", Inv);

        public static string Format(int value) => value.ToString(Inv);

        public static string Format(long value) => value.ToString(Inv);

        public static string Format(uint value) => value.ToString(Inv);
    }
}
