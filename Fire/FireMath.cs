using System;

namespace FireFront.Fire
{
    /// <summary>
    /// 1.0.2: the small decisions behind the review fixes, kept free of Unity and Valheim so the
    /// off-game harness (tools\run-tests.ps1) compiles and checks them. FireManager calls these.
    /// </summary>
    public static class FireMath
    {
        /// <summary>
        /// Seconds of burn one tree-fire tick charges. <paramref name="nextTick"/> is when this tick
        /// was due, or negative when the clock is stopped (no fire, or tree fire off). The tick runs
        /// inside the spread cycle, so it is normally up to one <paramref name="cycleInterval"/> late;
        /// that lateness is charged in full, and so is one interval of hitch on top. Anything past
        /// that is a gap (fire paused, tree fire toggled) and is not charged.
        /// </summary>
        public static float TreeTickSeconds(float nextTick, float now, float interval, float cycleInterval)
        {
            if (nextTick < 0f) return interval;
            float late = now - nextTick;
            if (float.IsNaN(late) || late < 0f) late = 0f;
            float cap = interval + Math.Max(interval, float.IsNaN(cycleInterval) ? 0f : cycleInterval);
            return Math.Min(interval + late, cap);
        }

        /// <summary>
        /// Seconds of rain aging to charge since the last pass: 0 on the first pass, and never more
        /// than <paramref name="maxStep"/>, so time the cycle did not run (fire paused, or the
        /// world closed) is not charged all at once when it resumes.
        /// </summary>
        public static float RainAgeSeconds(float last, float now, float maxStep)
        {
            if (last < 0f) return 0f;
            float dt = now - last;
            if (float.IsNaN(dt) || dt <= 0f) return 0f;
            return Math.Min(dt, maxStep);
        }

        /// <summary>World coordinate of the centre of cell <paramref name="index"/> on a grid of <paramref name="cellSize"/>.</summary>
        public static float CellCentre(int index, float cellSize) => (index + 0.5f) * cellSize;

        /// <summary>The GroundCellSize range the config accepts (FireConfig's AcceptableValueRange).</summary>
        public const float MinCellSize = 0.5f, MaxCellSize = 10f;

        /// <summary>
        /// The cell size to turn a ground-fire sync package's indices into positions: the server's,
        /// when the package carried a sane one; this machine's otherwise (a server older than 1.0.2
        /// sends none, and then only the old behaviour is possible).
        /// </summary>
        public static float SyncCellSize(bool hasServerSize, float serverSize, float localSize)
        {
            if (!hasServerSize || float.IsNaN(serverSize) || serverSize < MinCellSize || serverSize > MaxCellSize)
                return localSize;
            return serverSize;
        }

        /// <summary>
        /// 1.0.2: the igniter a new fire is booked to. A player's own hit wins; otherwise the fire
        /// inherits the igniter of the fire it spread from; 0 when neither is known (natural fire,
        /// lightning, a console command, or a store written before 1.0.2).
        /// </summary>
        public static long ResolveIgniter(long direct, long spreadSource) => direct != 0L ? direct : spreadSource;

        /// <summary>
        /// 1.0.2: the queued-igniter table's two operations. Put always writes, 0 included, so a
        /// re-queue replaces any older entry; Take always removes, so nothing outlives the object's
        /// time in the queue. Generic so the harness can run them without Valheim's ZDOID.
        /// </summary>
        public static void PutQueuedIgniter<TKey>(System.Collections.Generic.Dictionary<TKey, long> table, TKey id, long igniter)
        {
            table[id] = igniter;
        }

        public static long TakeQueuedIgniter<TKey>(System.Collections.Generic.Dictionary<TKey, long> table, TKey id)
        {
            if (table.TryGetValue(id, out long igniter)) { table.Remove(id); return igniter; }
            return 0L;
        }

        /// <summary>
        /// 1.0.2: the igniter field of a fire store line, or 0 when the line has no such field (a
        /// store written before 1.0.2) or it cannot be read. Never throws: a bad igniter must not
        /// cost the fire its restore.
        /// </summary>
        public static long IgniterField(string[] fields, int index)
        {
            if (fields == null || index < 0 || index >= fields.Length) return 0L;
            try { return FireFront.Utils.InvariantNumbers.ParseLong(fields[index]); }
            catch (Exception) { return 0L; }
        }

        /// <summary>
        /// 1.0.2: true, and <paramref name="bestSqr"/> updated, when (x, z) is closer to (px, pz) on
        /// the ground plane than the best so far (or, with nothing found yet, within it).
        /// </summary>
        public static bool CloserOnGround(float px, float pz, float x, float z, ref float bestSqr, bool foundAlready)
        {
            float dx = x - px, dz = z - pz;
            float d2 = dx * dx + dz * dz;
            if (float.IsNaN(d2)) return false;
            if (foundAlready ? d2 < bestSqr : d2 <= bestSqr) { bestSqr = d2; return true; }
            return false;
        }

        /// <summary>True when (x, z) lies within <paramref name="reach"/> metres of (refX, refZ) on the ground plane.</summary>
        public static bool WithinReach(float refX, float refZ, float x, float z, float reach)
        {
            float dx = x - refX, dz = z - refZ;
            float d2 = dx * dx + dz * dz;
            return !float.IsNaN(d2) && d2 <= reach * reach;
        }
    }
}
