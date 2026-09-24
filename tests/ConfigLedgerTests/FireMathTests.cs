using System;
using FireFront.Fire;

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
            const float interval = 2f;
            check("tree tick: a stopped clock charges one interval",
                near(FireMath.TreeTickSeconds(-1f, 5000f, interval), interval));
            check("tree tick: on time charges one interval",
                near(FireMath.TreeTickSeconds(100f, 100f, interval), interval));
            check("tree tick: a small hitch is caught up",
                near(FireMath.TreeTickSeconds(100f, 101f, interval), 3f));
            float gap = FireMath.TreeTickSeconds(100f, 400f, interval); // five quiet minutes
            check("tree tick: a five-minute gap is NOT charged (was 302 s, ~140% of a tree)",
                near(gap, 2f * interval), gap.ToString());
            // At the defaults a tree dies in 216 s of burn; one tick after a gap takes <= 2 %.
            check("tree tick: the first tick after a gap takes under 2% of the tree at the defaults",
                gap / 216f < 0.02f, (gap / 216f).ToString());
            check("tree tick: a clock in the future charges one interval",
                near(FireMath.TreeTickSeconds(100f, 99f, interval), interval));
            check("tree tick: NaN time charges one interval",
                near(FireMath.TreeTickSeconds(100f, float.NaN, interval), interval));

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
        }
    }
}
