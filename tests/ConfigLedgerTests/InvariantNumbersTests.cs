using System;
using System.Globalization;
using System.Text;
using FireFront.Utils;

namespace FireFront.Tests
{
    /// <summary>
    /// Utils/InvariantNumbers.cs, run under the cultures a dedicated-server admin actually has.
    /// Added in 0.24, when the fire save file and `fireset` stopped using the machine's culture.
    ///
    /// The harness runs on modern .NET with ICU culture data, which is stricter than the Mono the
    /// game runs on: sv-SE formats a negative number with U+2212, not '-'. If the reader copes
    /// with that here, it copes with anything Mono writes.
    /// </summary>
    public static class InvariantNumbersTests
    {
        private static readonly string[] Cultures =
            { "", "en-US", "de-DE", "fr-FR", "ru-RU", "sv-SE", "tr-TR", "pt-BR", "es-ES", "fi-FI" };

        // Values shaped like what the fire save file holds: positions, remaining seconds, ages.
        private static readonly float[] Samples =
            { 0f, 1.5f, -1.5f, 0.25f, -0.25f, 662.93f, -8123.457f, 54.57f, 239.99998f, 1e-4f, 10500f };

        public static void Run(Action<string, bool, string> checkFn)
        {
            void check(string name, bool ok, string detail = null) => checkFn(name, ok, detail);

            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo savedUi = CultureInfo.CurrentUICulture;
            try
            {
                foreach (string name in Cultures)
                {
                    SetCulture(name);
                    string c = name == "" ? "invariant" : name;

                    // Writing: identical text in every culture.
                    check($"numbers[{c}]: Format(1.5f) is \"1.5\"", InvariantNumbers.Format(1.5f) == "1.5", InvariantNumbers.Format(1.5f));
                    check($"numbers[{c}]: Format(-12) is \"-12\"", InvariantNumbers.Format(-12) == "-12", InvariantNumbers.Format(-12));
                    check($"numbers[{c}]: Format(long) round-trips",
                        InvariantNumbers.ParseLong(InvariantNumbers.Format(-5764607523034234879L)) == -5764607523034234879L);
                    foreach (float f in Samples)
                    {
                        string t = InvariantNumbers.Format(f);
                        check($"numbers[{c}]: Format({t}) reads back to the same bits",
                            InvariantNumbers.TryParseFloat(t, out float back) && BitConverter.SingleToInt32Bits(back) == BitConverter.SingleToInt32Bits(f),
                            t);
                    }

                    // Reading what a person types.
                    check($"numbers[{c}]: \"1.5\" is 1.5", InvariantNumbers.TryParseFloat("1.5", out float a) && a == 1.5f, a.ToString(CultureInfo.InvariantCulture));
                    check($"numbers[{c}]: \"1,5\" is 1.5", InvariantNumbers.TryParseFloat("1,5", out float b) && b == 1.5f, b.ToString(CultureInfo.InvariantCulture));
                    check($"numbers[{c}]: \"-0,25\" is -0.25", InvariantNumbers.TryParseFloat("-0,25", out float d) && d == -0.25f);
                    check($"numbers[{c}]: U+2212 minus reads", InvariantNumbers.TryParseFloat("−1.5", out float e) && e == -1.5f);
                    check($"numbers[{c}]: \" 8 \" trims", InvariantNumbers.TryParseFloat(" 8 ", out float g) && g == 8f);
                    check($"numbers[{c}]: \"1e3\" is 1000", InvariantNumbers.TryParseFloat("1e3", out float h) && h == 1000f);
                    foreach (string bad in new[] { "1,500.5", "1.234,5", "1,2,3", "NaN", "Infinity", "-Infinity", "", "abc", "1.5.5", null })
                        check($"numbers[{c}]: refuses {(bad == null ? "null" : "\"" + bad + "\"")}",
                            !InvariantNumbers.TryParseFloat(bad, out float _));

                    check($"numbers[{c}]: int \"42\"", InvariantNumbers.TryParseInt("42", out int i1) && i1 == 42);
                    check($"numbers[{c}]: int U+2212", InvariantNumbers.TryParseInt("−7", out int i2) && i2 == -7);
                    foreach (string bad in new[] { "1,000", "1.5", "4 2", "", null })
                        check($"numbers[{c}]: int refuses {(bad == null ? "null" : "\"" + bad + "\"")}",
                            !InvariantNumbers.TryParseInt(bad, out int _));
                    check($"numbers[{c}]: uint refuses a negative", !InvariantNumbers.TryParseUInt("-1", out uint _));
                }

                // Reading OLD save files: a store written before 0.24 used StringBuilder.Append,
                // i.e. the writing machine's culture. Whatever culture reads it now, each number
                // must come back as the writing machine meant it.
                foreach (string writer in Cultures)
                {
                    SetCulture(writer);
                    CultureInfo wc = CultureInfo.CurrentCulture;
                    var oldFloats = new string[Samples.Length];
                    for (int k = 0; k < Samples.Length; k++) oldFloats[k] = new StringBuilder().Append(Samples[k]).ToString();
                    string oldNeg = new StringBuilder().Append(-37).ToString();
                    string oldLong = new StringBuilder().Append(-5764607523034234879L).ToString();

                    foreach (string reader in Cultures)
                    {
                        SetCulture(reader);
                        string w = writer == "" ? "invariant" : writer, r = reader == "" ? "invariant" : reader;
                        for (int k = 0; k < Samples.Length; k++)
                        {
                            float meant = float.Parse(oldFloats[k], NumberStyles.Float, wc);
                            check($"old store written in {w}, read in {r}: \"{oldFloats[k]}\"",
                                InvariantNumbers.TryParseFloat(oldFloats[k], out float got) && got == meant,
                                got.ToString("R", CultureInfo.InvariantCulture));
                        }
                        check($"old store written in {w}, read in {r}: cell key \"{oldNeg}\"",
                            InvariantNumbers.TryParseInt(oldNeg, out int n) && n == -37);
                        check($"old store written in {w}, read in {r}: igniter id",
                            InvariantNumbers.TryParseLong(oldLong, out long l) && l == -5764607523034234879L);
                    }
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
                CultureInfo.CurrentUICulture = savedUi;
            }
        }

        private static void SetCulture(string name)
        {
            CultureInfo ci = name == "" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = ci;
            CultureInfo.CurrentUICulture = ci;
        }
    }
}
