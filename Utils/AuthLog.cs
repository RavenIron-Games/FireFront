using System.Collections.Generic;
using UnityEngine;

namespace FireFront.Utils
{
    /// <summary>
    /// One place for refusals: what was dropped, from whom, and why, at Warn so an admin reading
    /// the log sees it, gated per peer and per kind of refusal so a client sending a thousand
    /// bad packets a second produces one line every ten seconds carrying a count rather than a
    /// thousand lines. Per kind as well as per peer, so a forged package and a refused fireset
    /// from the same peer in the same ten seconds are both visible. A log flood is its own
    /// denial of service: the 0.22.1 velocity-curve error wrote 80,000 lines in one evening and
    /// buried everything else in the file.
    /// </summary>
    public static class AuthLog
    {
        public const string Prefix = "[AUTH]";
        private const float WindowSeconds = 10f;
        private const int MaxTrackedGates = 1024; // kinds are a handful of literals; peers are per session

        private struct Gate
        {
            public float NextAt;
            public int Suppressed;
        }

        private static readonly Dictionary<(long peer, string kind), Gate> _gates =
            new Dictionary<(long peer, string kind), Gate>();

        /// <summary>
        /// Warn once per peer, per kind, per ten-second window. Refusals inside the window are
        /// counted and reported on the next line of that kind that does get written.
        /// </summary>
        public static void Refused(long peer, string kind, string what)
        {
            float now = Time.realtimeSinceStartup;
            (long, string) key = (peer, kind ?? "");
            _gates.TryGetValue(key, out Gate gate);
            if (now < gate.NextAt)
            {
                gate.Suppressed++;
                _gates[key] = gate;
                return;
            }

            string tail = gate.Suppressed > 0
                ? $" ({gate.Suppressed} more of these from this peer in the last {WindowSeconds:F0} s not logged)"
                : "";
            FireLogger.Warn($"{Prefix} {what} — peer {peer}.{tail}");

            // A backstop against a pathological run, not a policy.
            if (_gates.Count >= MaxTrackedGates) _gates.Clear();
            _gates[key] = new Gate { NextAt = now + WindowSeconds, Suppressed = 0 };
        }
    }
}
