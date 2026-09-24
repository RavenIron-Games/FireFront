using System;
using FireFront.Fire;
using FireFront.Utils;

namespace FireFront.Tests
{
    /// <summary>
    /// Fire/FireMath.cs: the pure decisions behind the 1.0.2 review fixes. Each case names the
    /// failure it guards against, in the numbers the review used.
    /// </summary>
    public static class FireMathTests
    {
        public static void Run(Action<string, bool, string> checkFn)
        {
            void check(string name, bool ok, string detail = null) => checkFn(name, ok, detail);
            bool near(float a, float b) => Math.Abs(a - b) < 1e-4f;

            // --- TreeTickSeconds: the tree-fire clock after a quiet spell -----------------------
            const float interval = 2f, cycle = 0.75f; // the defaults
            check("tree tick: a stopped clock charges one interval",
                near(FireMath.TreeTickSeconds(-1f, 5000f, interval, cycle), interval));
            check("tree tick: on time charges one interval",
                near(FireMath.TreeTickSeconds(100f, 100f, interval, cycle), interval));
            check("tree tick: a small hitch is caught up",
                near(FireMath.TreeTickSeconds(100f, 101f, interval, cycle), 3f));
            float gap = FireMath.TreeTickSeconds(100f, 400f, interval, cycle); // five quiet minutes
            check("tree tick: a five-minute gap is NOT charged (was 302 s, ~140% of a tree)",
                near(gap, 2f * interval), gap.ToString());
            // A slow custom cycle: SpreadCheckInterval 10 s, tick 2 s. Ticks land ~10 s apart
            // (8 s late); the first fix charged 4 s of them, so trees burned at 40 % speed.
            float slow = FireMath.TreeTickSeconds(100f, 108f, interval, 10f);
            check("tree tick: a 10 s spread cycle is charged in full (10 s per tick, not 4)",
                near(slow, 10f), slow.ToString());
            check("tree tick: LowSpec-like 2 s cycle with a 0.5 s tick charges the full 2 s",
                near(FireMath.TreeTickSeconds(100f, 101.5f, 0.5f, 2f), 2f));
            check("tree tick: a slow cycle still does not charge a five-minute gap",
                near(FireMath.TreeTickSeconds(100f, 400f, interval, 10f), 12f));
            // At the defaults a tree dies in 216 s of burn; one tick after a gap takes <= 2 %.
            check("tree tick: the first tick after a gap takes under 2% of the tree at the defaults",
                gap / 216f < 0.02f, (gap / 216f).ToString());
            check("tree tick: a clock in the future charges one interval",
                near(FireMath.TreeTickSeconds(100f, 99f, interval, cycle), interval));
            check("tree tick: NaN time charges one interval",
                near(FireMath.TreeTickSeconds(100f, float.NaN, interval, cycle), interval));

            // --- RainAgeSeconds: resuming after fire was paused ---------------------------------
            check("rain: the first pass charges nothing", FireMath.RainAgeSeconds(-1f, 100f, 1.5f) == 0f);
            check("rain: one cycle charges one cycle", near(FireMath.RainAgeSeconds(100f, 100.75f, 1.5f), 0.75f));
            check("rain: a five-minute pause charges at most the cap (was 300 s)",
                near(FireMath.RainAgeSeconds(100f, 400f, 1.5f), 1.5f));
            check("rain: time going backwards charges nothing", FireMath.RainAgeSeconds(100f, 50f, 1.5f) == 0f);

            // --- CellCentre + SyncCellSize: the ground-fire mirror uses the server's grid -------
            check("cell: centre of cell 0 at 1 m is 0.5", near(FireMath.CellCentre(0, 1f), 0.5f));
            check("cell: centre of cell -1 at 1 m is -0.5", near(FireMath.CellCentre(-1, 1f), -0.5f));
            // The review's case: server 1 m, fire at (1000, -600) is cells (1000, -600).
            float serverSize = FireMath.SyncCellSize(true, 1f, 2f);
            check("sync: a client set to 2 m uses the server's 1 m", serverSize == 1f);
            check("sync: so a fire at x=1000 is drawn at 1000.5, not 2001",
                near(FireMath.CellCentre(1000, serverSize), 1000.5f));
            check("sync: and at z=-600 at -599.5, not -1199",
                near(FireMath.CellCentre(-600, serverSize), -599.5f));
            check("sync: an older server sends no size, so the client's own is used",
                FireMath.SyncCellSize(false, 0f, 2f) == 2f);
            check("sync: a size below the config range is ignored", FireMath.SyncCellSize(true, 0.1f, 1f) == 1f);
            check("sync: a size above the config range is ignored", FireMath.SyncCellSize(true, 50f, 1f) == 1f);
            check("sync: NaN is ignored", FireMath.SyncCellSize(true, float.NaN, 1f) == 1f);
            check("sync: infinity is ignored", FireMath.SyncCellSize(true, float.PositiveInfinity, 1f) == 1f);
            check("sync: the range edges are accepted",
                FireMath.SyncCellSize(true, 0.5f, 1f) == 0.5f && FireMath.SyncCellSize(true, 10f, 1f) == 10f);

            // --- WithinReach: ignite/extinguish targets near the asking player ------------------
            check("reach: 200 m away is within 320", FireMath.WithinReach(0f, 0f, 200f, 0f, 320f));
            check("reach: on the edge counts", FireMath.WithinReach(0f, 0f, 0f, 320f, 320f));
            check("reach: across the map is refused", !FireMath.WithinReach(0f, 0f, 5000f, -3000f, 320f));
            check("reach: diagonal just past the limit is refused", !FireMath.WithinReach(0f, 0f, 227f, 227f, 320f));
            check("reach: a NaN target is refused", !FireMath.WithinReach(0f, 0f, float.NaN, 0f, 320f));
            check("reach: an infinite target is refused", !FireMath.WithinReach(0f, 0f, float.PositiveInfinity, 0f, 320f));

            // --- 1.0.2 per-fire igniter -----------------------------------------------------
            const long A = 76561198000000001L, B = 76561198000000002L;
            check("igniter: a player's own hit wins over the fire it spread from", FireMath.ResolveIgniter(B, A) == B);
            check("igniter: spread inherits the source fire's igniter", FireMath.ResolveIgniter(0L, A) == A);
            check("igniter: natural fire spreading stays natural", FireMath.ResolveIgniter(0L, 0L) == 0L);
            check("igniter: a player lighting from a natural fire is billed", FireMath.ResolveIgniter(A, 0L) == A);
            // A chain: A lights a tree, it spreads to ground, the ground to another tree.
            long chain = FireMath.ResolveIgniter(0L, FireMath.ResolveIgniter(0L, FireMath.ResolveIgniter(A, 0L)));
            check("igniter: A's fire is still A's three hops out", chain == A);

            // Store lines. The writer appends the igniter as the LAST field; the readers (this
            // build and every older one) index fields by position, so older builds never see it.
            string newObj = string.Join("\t", "obj", "0", "123", "10.5", "30", "-600.25", "200", "40", "Beech1", InvariantNumbers.Format(A));
            string oldObj = string.Join("\t", "obj", "0", "123", "10.5", "30", "-600.25", "200", "40", "Beech1");
            string newGround = string.Join("\t", "ground", "10", "-600", "30", "20", InvariantNumbers.Format(B));
            string oldGround = string.Join("\t", "ground", "10", "-600", "30", "20");
            string[] no = newObj.Split('\t'), oo = oldObj.Split('\t'), ng = newGround.Split('\t'), og = oldGround.Split('\t');
            check("store: a 1.0.2 obj line restores its igniter", FireMath.IgniterField(no, 9) == A);
            check("store: a 1.0.2 obj line keeps the prefab at field 8 for older builds", no[8] == "Beech1");
            check("store: a pre-1.0.2 obj line restores with igniter 0", FireMath.IgniterField(oo, 9) == 0L);
            check("store: a 1.0.2 ground line restores its igniter", FireMath.IgniterField(ng, 5) == B);
            check("store: a 1.0.2 ground line keeps remaining time at field 4 for older builds",
                InvariantNumbers.ParseFloat(ng[4]) == 20f);
            check("store: a pre-1.0.2 ground line restores with igniter 0", FireMath.IgniterField(og, 5) == 0L);
            check("store: an unreadable igniter is 0, never a throw", FireMath.IgniterField(new[] { "obj", "x" }, 1) == 0L);
            check("store: a negative field index is 0", FireMath.IgniterField(no, -1) == 0L);
            check("store: null fields are 0", FireMath.IgniterField(null, 9) == 0L);

            // FireIgniterNear's distance rule.
            float bestSqr = 10f * 10f;
            check("near: a fire 6 m away is within 10", FireMath.CloserOnGround(0f, 0f, 6f, 0f, ref bestSqr, false) && near(bestSqr, 36f));
            check("near: a farther fire does not replace it", !FireMath.CloserOnGround(0f, 0f, 8f, 0f, ref bestSqr, true));
            check("near: a nearer fire does", FireMath.CloserOnGround(0f, 0f, 0f, 3f, ref bestSqr, true) && near(bestSqr, 9f));
            float none = 5f * 5f;
            check("near: nothing past the radius", !FireMath.CloserOnGround(0f, 0f, 6f, 0f, ref none, false));
            check("near: the radius edge counts", FireMath.CloserOnGround(0f, 0f, 5f, 0f, ref none, false));
            check("near: NaN never matches", !FireMath.CloserOnGround(0f, 0f, float.NaN, 0f, ref none, false));
        }
    }
}
