using BepInEx.Configuration;
using UnityEngine;

namespace FireFront.Config
{
    /// <summary>
    /// All FireFront config. Every value is live-settable via the fireset dev command.
    /// </summary>
    public static class FireConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> BurnDurationSeconds;
        public static ConfigEntry<float> SpreadMaturityFraction;
        public static ConfigEntry<float> SpreadRadius;
        public static ConfigEntry<int> MaxConcurrentBurning;
        public static ConfigEntry<int> QueueSize;
        public static ConfigEntry<float> SpreadCheckInterval;
        // [Meta] layout version, stamped by ConfigMigration; the rungs are in ConfigLedger. Not a setting.
        public static ConfigEntry<int> ConfigVersion;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> BurnTreesAndLogs;
        public static ConfigEntry<bool> BurnPlayerBuildings;
        public static ConfigEntry<string> VfxPrefabName;
        public static ConfigEntry<bool> UseProceduralVfx;
        public static ConfigEntry<bool> FireSmokeEnabled;
        public static ConfigEntry<bool> TreeFlameScaling;
        public static ConfigEntry<bool> CrownSparksEnabled;
        public static ConfigEntry<float> MaxFlameHeight;
        public static ConfigEntry<int> TallFireMaxConcurrent;

        public static ConfigEntry<bool> GroundSpreadEnabled;
        public static ConfigEntry<float> GroundCellSize;
        public static ConfigEntry<float> GroundSpreadRadius;
        public static ConfigEntry<float> GroundBurnDurationSeconds;
        public static ConfigEntry<int> GroundMaxConcurrent;
        public static ConfigEntry<int> MaxKillsPerCycle;
        public static ConfigEntry<int> GroundVfxMaxConcurrent;
        public static ConfigEntry<int> GroundDamageMaxConcurrent;
        public static ConfigEntry<bool> FireHurtsEnabled;
        public static ConfigEntry<bool> FireHurtsPlayerOnly;
        public static ConfigEntry<float> FireHurtsObjectRadius;
        public static ConfigEntry<float> FireDamagePerTick;
        public static ConfigEntry<float> FireDamageTickInterval;
        public static ConfigEntry<KeyboardShortcut> ExtinguishKey;
        public static ConfigEntry<float> ExtinguishGroundRadius;
        public static ConfigEntry<float> DouseImmunitySeconds;
        public static ConfigEntry<bool> RainSuppressesGroundFire;
        public static ConfigEntry<float> RainGroundBurnDurationMultiplier;
        public static ConfigEntry<bool> RainSuppressesObjectFire;
        public static ConfigEntry<float> RainObjectBurnDurationMultiplier;
        public static ConfigEntry<bool> ScorchMarksEnabled;
        public static ConfigEntry<float> ScorchMarkLifetimeSeconds;
        public static ConfigEntry<bool> UseVanillaDirtPaint;
        public static ConfigEntry<float> DirtPaintRadius;
        public static ConfigEntry<bool> FireRampEnabled;
        public static ConfigEntry<float> FireRampDurationSeconds;
        public static ConfigEntry<float> FireRampStartFraction;
        public static ConfigEntry<bool> GroundFuelExhaustionEnabled;
        public static ConfigEntry<float> GroundFuelRegrowSeconds;
        public static ConfigEntry<bool> TreeRegrowthEnabled;
        public static ConfigEntry<float> TreeRegrowthSeconds;
        public static ConfigEntry<bool> GroundFirebreaksEnabled;
        public static ConfigEntry<bool> GroundWaterBlocksSpreadEnabled;
        public static ConfigEntry<bool> WindSpreadBiasEnabled;
        public static ConfigEntry<float> WindUpwindIgniteChance;
        public static ConfigEntry<float> WindInfluence;
        public static ConfigEntry<float> DousingBombRadius;
        public static ConfigEntry<bool> PersistFiresEnabled;
        public static ConfigEntry<bool> GroundMaxSpreadDistanceEnabled;
        public static ConfigEntry<float> GroundMaxSpreadDistance;
        public static ConfigEntry<bool> LowSpecPreset;
        public static ConfigEntry<bool> WatchTheWorldBurn;
        public static ConfigEntry<bool> SmoulderingVfxEnabled;
        public static ConfigEntry<float> SmoulderAfterFraction;

        // --- Low-spec preset -------------------------------------------------
        //
        // One switch instead of eight, for a player whose machine struggles.
        //
        // It NEVER writes to the config file. Every setting below stays exactly
        // as the player wrote it; these accessors just return a cheaper number
        // while the preset is on, so switching it back off restores their own
        // values with nothing lost. That matters here specifically: BepInEx
        // persists whatever is in a ConfigEntry, so a preset implemented by
        // assigning values would silently overwrite the player's settings and
        // could never be undone.
        //
        // It also only ever makes things CHEAPER. Anyone who has already tuned
        // below these numbers keeps their lower value — the preset is a ceiling
        // on cost, not an instruction to raise anything.

        private const int LowSpecMaxConcurrentBurning = 20;
        private const int LowSpecGroundMaxConcurrent = 25;
        private const int LowSpecGroundVfxMaxConcurrent = 10;
        private const int LowSpecGroundDamageMaxConcurrent = 20;
        private const float LowSpecSpreadCheckInterval = 2f;
        private const float LowSpecMaxFlameHeight = 12f;
        private const int LowSpecTallFireMaxConcurrent = 4;

        private static bool LowSpec => LowSpecPreset != null && LowSpecPreset.Value;

        public static int EffectiveMaxConcurrentBurning =>
            LowSpec ? Mathf.Min(MaxConcurrentBurning.Value, LowSpecMaxConcurrentBurning)
            : Apocalypse ? Mathf.Max(MaxConcurrentBurning.Value, BurnMaxConcurrentBurning)
            : MaxConcurrentBurning.Value;

        public static int EffectiveGroundMaxConcurrent =>
            LowSpec ? Mathf.Min(GroundMaxConcurrent.Value, LowSpecGroundMaxConcurrent)
            : Apocalypse ? Mathf.Max(GroundMaxConcurrent.Value, BurnGroundMaxConcurrent)
            : GroundMaxConcurrent.Value;

        public static int EffectiveGroundVfxMaxConcurrent =>
            LowSpec ? Mathf.Min(GroundVfxMaxConcurrent.Value, LowSpecGroundVfxMaxConcurrent) : GroundVfxMaxConcurrent.Value;

        public static int EffectiveGroundDamageMaxConcurrent =>
            LowSpec ? Mathf.Min(GroundDamageMaxConcurrent.Value, LowSpecGroundDamageMaxConcurrent) : GroundDamageMaxConcurrent.Value;

        /// <summary>Scorch decals are cosmetic; the preset drops them entirely.</summary>
        public static bool EffectiveScorchMarksEnabled => !LowSpec && ScorchMarksEnabled.Value;

        /// <summary>Crown sparks are pure decoration; the preset drops them entirely.</summary>
        public static bool EffectiveCrownSparksEnabled =>
            !LowSpec && TreeFlameScaling.Value && CrownSparksEnabled.Value;

        /// <summary>
        /// Tall flames SURVIVE the low-spec preset, capped rather than switched
        /// off, because the column is a correctness fix as much as a visual one -
        /// without it a burning tree reads as a campfire at the foot of an
        /// untouched tree. The cap is what keeps it affordable.
        /// </summary>
        public static float EffectiveMaxFlameHeight =>
            LowSpec ? Mathf.Min(MaxFlameHeight.Value, LowSpecMaxFlameHeight) : MaxFlameHeight.Value;

        public static int EffectiveTallFireMaxConcurrent =>
            LowSpec ? Mathf.Min(TallFireMaxConcurrent.Value, LowSpecTallFireMaxConcurrent)
            : TallFireMaxConcurrent.Value;

        // --- Watch The World Burn ------------------------------------------
        //
        // LowSpecPreset's opposite number: one switch that takes every brake
        // off fire at once. It is deliberately NOT a "max everything" button —
        // the visual caps (GroundVfxMaxConcurrent, GroundDamageMaxConcurrent)
        // are left exactly as the player set them, because those are what cost
        // frames, and someone who wants the world to burn still gets to decide
        // how much of it they can afford to render.
        //
        // Same two properties as LowSpecPreset: it resolves at read time and
        // never writes to the config, so switching it off restores the
        // player's own values exactly.
        //
        // If BOTH presets are somehow on, LOW SPEC WINS. A machine that cannot
        // cope is a harder constraint than a preference for spectacle, and the
        // failure mode of getting that backwards is someone's game locking up.

        private const int BurnMaxConcurrentBurning = 200;   // config max
        private const int BurnGroundMaxConcurrent = 500;
        private const float BurnSpreadCheckInterval = 0.25f; // config min = fastest
        private const float BurnSpreadRadius = 15f;          // config max
        private const float BurnGroundSpreadRadius = 20f;    // config max
        private const int BurnMaxKillsPerCycle = 50;         // config max

        private static bool Apocalypse =>
            WatchTheWorldBurn != null && WatchTheWorldBurn.Value && !LowSpec;

        /// <summary>True when the world-burning preset is actually in force.</summary>
        public static bool ApocalypseActive => Apocalypse;

        /// <summary>Fire becomes contagious the instant it lights.</summary>
        public static float EffectiveSpreadMaturityFraction =>
            Apocalypse ? 0f : SpreadMaturityFraction.Value;

        /// <summary>Paths, cultivated ground and water stop being firebreaks.</summary>
        public static bool EffectiveGroundFirebreaksEnabled =>
            !Apocalypse && GroundFirebreaksEnabled.Value;

        public static bool EffectiveGroundWaterBlocksSpreadEnabled =>
            !Apocalypse && GroundWaterBlocksSpreadEnabled.Value;

        public static bool EffectiveRainSuppressesGroundFire =>
            !Apocalypse && RainSuppressesGroundFire.Value;

        /// <summary>Rain douses object fire too; burntheworld ignores weather entirely.</summary>
        public static bool EffectiveRainSuppressesObjectFire =>
            !Apocalypse && RainSuppressesObjectFire.Value;

        /// <summary>Burned ground can relight immediately instead of staying spent.</summary>
        public static bool EffectiveGroundFuelExhaustionEnabled =>
            !Apocalypse && GroundFuelExhaustionEnabled.Value;

        /// <summary>No leash: a fire can walk as far as it can find fuel.</summary>
        public static bool EffectiveGroundMaxSpreadDistanceEnabled =>
            !Apocalypse && GroundMaxSpreadDistanceEnabled.Value;

        /// <summary>Fires start at full strength rather than ramping up.</summary>
        public static bool EffectiveFireRampEnabled =>
            !Apocalypse && FireRampEnabled.Value;

        /// <summary>Nothing grows back.</summary>
        public static bool EffectiveTreeRegrowthEnabled =>
            !Apocalypse && TreeRegrowthEnabled.Value;

        /// <summary>Dousing no longer keeps anything wet.</summary>
        public static float EffectiveDouseImmunitySeconds =>
            Apocalypse ? 0f : DouseImmunitySeconds.Value;

        public static float EffectiveSpreadRadius =>
            Apocalypse ? Mathf.Max(SpreadRadius.Value, BurnSpreadRadius) : SpreadRadius.Value;

        public static float EffectiveGroundSpreadRadius =>
            Apocalypse ? Mathf.Max(GroundSpreadRadius.Value, BurnGroundSpreadRadius) : GroundSpreadRadius.Value;

        public static int EffectiveMaxKillsPerCycle =>
            Apocalypse ? Mathf.Max(MaxKillsPerCycle.Value, BurnMaxKillsPerCycle) : MaxKillsPerCycle.Value;

        /// <summary>
        /// The odd one out: a LONGER interval is the cheaper one, so this takes
        /// the max rather than the min. Fewer spread cycles per second means
        /// fewer candidate rebuilds, which is the dominant per-cycle cost.
        /// </summary>
        public static float EffectiveSpreadCheckInterval =>
            LowSpec ? Mathf.Max(SpreadCheckInterval.Value, LowSpecSpreadCheckInterval)
            : Apocalypse ? Mathf.Min(SpreadCheckInterval.Value, BurnSpreadCheckInterval)
            : SpreadCheckInterval.Value;

        public static void Bind(ConfigFile config)
        {
            // Before ANY bind: snapshot the raw file so the migration can see what an old build
            // wrote, before BepInEx's own bind normalises it. Finish runs after the last bind.
            ConfigMigration.Begin(config);

            ConfigVersion = config.Bind(
                ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0,
                "The layout version of this file, stamped by the mod itself after it has moved any " +
                "value still sitting at an OLD default onto the new one and dropped keys no current " +
                "build reads (a copy of the previous file lands beside it as .vN.bak first). Not a " +
                "setting: leave it alone. Delete the line to make the next boot re-run the migration.");

            Enabled = config.Bind(
                "General", "Enabled", true,
                "Master switch. When false, no burn timers run and no spread occurs.");

            SmoulderingVfxEnabled = config.Bind(
                "Visuals", "SmoulderingVfxEnabled", true,
                "After a fire has been burning a while, drop its full flame effect down to a " +
                "smouldering one — embers and smoke instead of flames, and no dynamic light. " +
                "The simulation is untouched: it still burns, still spreads, still hurts, for " +
                "exactly as long. This is purely what gets DRAWN, and it is the single biggest " +
                "rendering saving available during a big fire, because every burning object " +
                "otherwise carries its own real-time light for its whole burn.");

            SmoulderAfterFraction = config.Bind(
                "Visuals", "SmoulderAfterFraction", 0.65f,
                new ConfigDescription(
                    "Fraction of its burn time an object shows full flames before dropping to " +
                    "smouldering. 0.65 = flames for the first ~65%, then embers, glow and smoke " +
                    "for the rest. It is still burning and still spreading while it smoulders.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            WatchTheWorldBurn = config.Bind(
                "General", "WatchTheWorldBurn", false,
                "Takes every brake off fire at once, for people who want to watch the world burn. " +
                "Fire becomes contagious the instant it lights; dirt paths, cultivated ground and " +
                "WATER stop being firebreaks; rain no longer suppresses it; burned ground can " +
                "relight immediately; fires start at full strength; nothing regrows; extinguishing " +
                "no longer keeps anything wet; spread reach and the burning/ground caps go to " +
                "maximum and the spread cycle to its fastest. Your VISUAL caps are deliberately " +
                "left alone — those are what cost frames, and you still choose how much of the " +
                "apocalypse your machine renders. Your own settings are NOT overwritten; turning " +
                "it off restores everything. If LowSpecPreset is also on, LOW SPEC WINS. " +
                "Toggle live with 'fireset burntheworld true'.");

            LowSpecPreset = config.Bind(
                "General", "LowSpecPreset", false,
                "One switch for a machine that struggles with big fires. Caps burning pieces at " +
                "20, ground cells at 25, ground fire visuals at 10 and damage zones at 20, turns " +
                "off scorch decals, and slows the spread cycle to at least 2s. Fire still spreads " +
                "and still burns things down — there is just less of it happening at once. " +
                "Your own settings are NOT overwritten: this only ever makes things cheaper, " +
                "anything you already set lower is kept, and turning it off restores everything. " +
                "Toggle live with 'fireset lowspec true'.");

            BurnDurationSeconds = config.Bind(
                "Fire", "BurnDurationSeconds", 240f,
                new ConfigDescription(
                    "Seconds a piece burns before it is destroyed.",
                    new AcceptableValueRange<float>(1f, 600f)));

            SpreadMaturityFraction = config.Bind(
                "Fire", "SpreadMaturityFraction", 0.25f,
                new ConfigDescription(
                    "Fraction of its burn duration a burning object must burn before it can " +
                    "ignite anything — neighbors or the ground under it. This ties the fire " +
                    "front's pace to how long fuel takes to burn: at defaults (240s x 0.25) a " +
                    "tree becomes contagious about a minute into its burn instead of torching " +
                    "its whole reach on the next spread cycle. It burns, glows, and hurts from " +
                    "second one — it just isn't throwing fire yet. Ground fire's cell-to-cell " +
                    "crawl is unaffected. 0 = old instant-contagion behavior.",
                    new AcceptableValueRange<float>(0f, 0.9f)));

            SpreadRadius = config.Bind(
                "Fire", "SpreadRadius", 8f,
                new ConfigDescription(
                    "Max distance (meters) from an actively burning piece for spread to occur. " +
                    "Originally locked to 2-4m per the initial spec; widened to allow testing " +
                    "wider spread since real object spacing often exceeds that.",
                    new AcceptableValueRange<float>(2f, 15f)));

            MaxConcurrentBurning = config.Bind(
                "Fire", "MaxConcurrentBurning", 50,
                new ConfigDescription(
                    "Hard cap on pieces burning at the same time.",
                    new AcceptableValueRange<int>(1, 200)));

            QueueSize = config.Bind(
                "Fire", "QueueSize", 20,
                new ConfigDescription(
                    "FIFO overflow queue slots. Ignitions past the concurrent cap wait here. Overflow beyond the queue drops silently and is re-attempted next cycle. " +
                    "Originally locked to 5-10 per the initial spec; widened for testing with bigger fires.",
                    new AcceptableValueRange<int>(5, 100)));

            SpreadCheckInterval = config.Bind(
                "Fire", "SpreadCheckInterval", 0.75f,
                new ConfigDescription(
                    "Seconds between spread/queue-promotion cycles.",
                    new AcceptableValueRange<float>(0.25f, 10f)));

            VerboseLogging = config.Bind(
                "Debug", "DebugLogging", false,
                "Log every ignite/spread/queue/destroy event (toggle live with firedebug). Off by " +
                "default since 0.18.7: during a big fire this wrote hundreds of lines a second, and " +
                "the string churn plus BepInEx console/file I/O fed periodic GC frame spikes on " +
                "tester machines. The key was RENAMED from VerboseLogging deliberately — BepInEx " +
                "never retro-applies a changed default to an existing config file (learned the hard " +
                "way with UseProceduralVfx in 0.17.0), and a debug firehose that testers were " +
                "unknowingly stuck with is exactly the kind of value that must not stick.");

            BurnTreesAndLogs = config.Bind(
                "Fire", "BurnTreesAndLogs", true,
                "Standing trees and felled logs can catch fire and spread alongside structures.");

            BurnPlayerBuildings = config.Bind(
                "Fire", "BurnPlayerBuildings", true,
                "Player-built structures (anything carrying a placement creator stamp — walls, " +
                "floors, furniture, chests you placed) can catch fire. Set false for an " +
                "anti-grief server: fire then never ignites player builds by ANY path — spread, " +
                "fire arrows, console commands — while world-generated structures (abandoned " +
                "villages, ruins, dungeon furniture) still burn. Wildfire still crawls past a " +
                "protected base and still hurts anyone standing in it; only the buildings are " +
                "safe. Note the terrain firebreak already protects a base on leveled/pathed " +
                "ground from SPREAD — this switch is the stronger guarantee that also covers " +
                "deliberate arson.");

            VfxPrefabName = config.Bind(
                "Visuals", "VfxPrefabName", "",
                "Name of a registered vanilla prefab to spawn on burning targets. " +
                "Run 'firecheckprefab <name>' first — most fire-related prefabs carry a " +
                "ZNetView and are refused automatically as unsafe. Empty disables this path. " +
                "Ignored if UseProceduralVfx is true.");

            UseProceduralVfx = config.Bind(
                "Visuals", "UseProceduralVfx", true,
                "Use a small custom particle fire effect built entirely in code instead of a " +
                "vanilla prefab. Won't look identical to vanilla fire, but has zero ZNetView/" +
                "ZNetScene dependency — safe by construction. Takes priority over VfxPrefabName. " +
                "Defaults to true: without any visual, fire is simulated but invisible, and " +
                "'nothing is happening' was a real, repeated report on a real dedicated-server " +
                "test purely because this was off with no vfx prefab configured either.");

            FireSmokeEnabled = config.Bind(
                "Visuals", "FireSmokeEnabled", true,
                "Object fire (pieces/trees/logs) gets a rising smoke layer above the flame, in " +
                "addition to the flame itself. Ground fire never gets smoke — it's deliberately " +
                "kept cheap since up to 200 cells can be burning at once. Turn off if a big fire " +
                "with many burning objects starts affecting performance.");

            TreeFlameScaling = config.Bind(
                "Visuals", "TreeFlameScaling", true,
                "Fire on a tall object (a tree, a raised building) climbs it, instead of " +
                "burning as one small plume at its base. The flame becomes a column spanning " +
                "the burner's measured height, its smoke starts at the canopy rather than the " +
                "ground, and its light reaches further. Turn off to go back to the one small " +
                "flame every burning thing used to get regardless of size.");

            CrownSparksEnabled = config.Bind(
                "Visuals", "CrownSparksEnabled", true,
                "Tall burners throw sparks off their upper half - stretched, bright, and " +
                "falling. This is what makes a burning tree read as alight in the CROWN rather " +
                "than at the foot. Costs a few particles a second per burning tree and stops " +
                "entirely once that tree drops to smouldering. Ignored when TreeFlameScaling " +
                "is off, and forced off by LowSpecPreset.");

            MaxFlameHeight = config.Bind(
                "Visuals", "MaxFlameHeight", 30f,
                "Ceiling on how tall a single fire column is DRAWN, in metres. This bounds " +
                "geometry, not cost: particle counts and sizes stop growing at 14m regardless, " +
                "so raising this stretches the same particles over a taller tree rather than " +
                "buying more of them. The default covers a real Valheim fir, which stands 15.8 " +
                "to 31.7m in the world - 14 would have clamped every wild tree in the game. " +
                "LowSpecPreset caps this at 12.");

            TallFireMaxConcurrent = config.Bind(
                "Visuals", "TallFireMaxConcurrent", 12,
                "How many tall fire columns may be drawn at once. Past this, further ignitions " +
                "get the ordinary small flame - they still burn, spread and do damage exactly " +
                "the same, they just cost what fire always cost. Object fire otherwise has no " +
                "aggregate visual cap the way ground fire does, and a tall burner costs about " +
                "four times a short one. LowSpecPreset caps this at 4.");

            GroundSpreadEnabled = config.Bind(
                "Ground", "GroundSpreadEnabled", true,
                "Fire can spread across open ground (grass, gaps between trees) via an " +
                "invisible grid of burning cells, not just object-to-object. Grass itself " +
                "has no real game object to ignite — this only affects how far fire reaches.");

            GroundCellSize = config.Bind(
                "Ground", "GroundCellSize", 1f,
                new ConfigDescription(
                    "Size in meters of each ground-fire grid cell. Smaller = finer spread, more cells.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            GroundSpreadRadius = config.Bind(
                "Ground", "GroundSpreadRadius", 4f,
                new ConfigDescription(
                    "Max distance (meters) a burning ground cell or object can ignite nearby ground cells.",
                    new AcceptableValueRange<float>(1f, 20f)));

            GroundBurnDurationSeconds = config.Bind(
                "Ground", "GroundBurnDurationSeconds", 8f,
                new ConfigDescription(
                    "Seconds a ground cell stays burning before it goes out.",
                    new AcceptableValueRange<float>(1f, 120f)));

            GroundMaxConcurrent = config.Bind(
                "Ground", "GroundMaxConcurrent", 50,
                new ConfigDescription(
                    "Hard cap on concurrently burning ground cells. Cheap (no real objects), so this can be much higher than MaxConcurrentBurning.",
                    new AcceptableValueRange<int>(1, 2000)));

            MaxKillsPerCycle = config.Bind(
                "General", "MaxKillsPerCycle", 5,
                new ConfigDescription(
                    "Safety throttle: max real objects (pieces/trees/logs) destroyed in a single " +
                    "cycle. Destroying many ZNetView objects in one frame risked racing with " +
                    "ZNetScene's own bookkeeping during the ground-spread bug — this keeps any " +
                    "large simultaneous burn-down spread across multiple cycles instead.",
                    new AcceptableValueRange<int>(1, 50)));

            GroundVfxMaxConcurrent = config.Bind(
                "Ground", "GroundVfxMaxConcurrent", 30,
                new ConfigDescription(
                    "Max ground-fire cells that get an actual PARTICLE VISUAL at once. Ground fire " +
                    "can have up to GroundMaxConcurrent (200) cells burning logically; rendering " +
                    "that many particle effects simultaneously would be a real performance cost. " +
                    "Does NOT limit damage zones — see GroundDamageMaxConcurrent for that.",
                    new AcceptableValueRange<int>(0, 200)));

            GroundDamageMaxConcurrent = config.Bind(
                "Damage", "GroundDamageMaxConcurrent", 50,
                new ConfigDescription(
                    "Max ground-fire cells that get a damage zone at once, INDEPENDENT of " +
                    "GroundVfxMaxConcurrent. Was accidentally sharing the low visual cap through " +
                    "0.12.1, meaning only ~30 of up to 200 burning cells could ever hurt anyone — " +
                    "objects (trees/pieces) have their own separate high cap and worked fine, which " +
                    "is why ground fire felt like it never actually hurt anyone. Defaults to match " +
                    "GroundMaxConcurrent so every burning cell gets damage coverage by default; a " +
                    "FireBurnZone's own polling cost is much lower than a full particle system, so " +
                    "a much higher cap here is fine.",
                    new AcceptableValueRange<int>(0, 2000)));

            FireHurtsEnabled = config.Bind(
                "Damage", "FireHurtsEnabled", true,
                "Standing in fire (object or ground) actually deals damage, via vanilla's own " +
                "burn-check system (EffectArea/Type.Burning) — the same mechanism real campfires " +
                "use. Independent of visuals: still works even if VfxPrefabName/UseProceduralVfx " +
                "are both off, so turning off effects for performance doesn't silently disable this.");

            FireHurtsPlayerOnly = config.Bind(
                "Damage", "FireHurtsPlayerOnly", false,
                "If true, only players take damage from fire — creatures/mobs are unaffected. " +
                "Default false: fire hurts anything standing in it, players and mobs alike.");

            FireHurtsObjectRadius = config.Bind(
                "Damage", "FireHurtsObjectRadius", 2f,
                new ConfigDescription(
                    "Radius (meters) of the damage zone around a burning object (piece/tree/log). " +
                    "Ground-fire damage zones use half of GroundCellSize instead — no separate setting.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            FireDamagePerTick = config.Bind(
                "Damage", "FireDamagePerTick", 5f,
                new ConfigDescription(
                    "Fire damage applied per tick to anything standing in a fire zone (see FireDamageTickInterval).",
                    new AcceptableValueRange<float>(0.5f, 50f)));

            FireDamageTickInterval = config.Bind(
                "Damage", "FireDamageTickInterval", 1f,
                new ConfigDescription(
                    "Seconds between fire damage ticks for anything standing in a fire zone.",
                    new AcceptableValueRange<float>(0.1f, 10f)));

            ExtinguishKey = config.Bind(
                "Controls", "ExtinguishKey", new KeyboardShortcut(KeyCode.G),
                "Hold/press this key to extinguish fire: the burning object under your crosshair " +
                "(if any) plus any ground fire within ExtinguishGroundRadius of you. Rebindable — " +
                "change this if G conflicts with something you use.");

            ExtinguishGroundRadius = config.Bind(
                "Controls", "ExtinguishGroundRadius", 15f,
                new ConfigDescription(
                    "Radius (meters) around the player that ExtinguishKey clears of ground fire.",
                    new AcceptableValueRange<float>(1f, 15f)));

            DouseImmunitySeconds = config.Bind(
                "Controls", "DouseImmunitySeconds", 90f,
                new ConfigDescription(
                    "Anything deliberately extinguished — dousing bomb, extinguish key, stopfire — " +
                    "is soaked and can't re-ignite for this many seconds. Without this, the " +
                    "surrounding fire simply re-lit every doused cell and object within a cycle " +
                    "or two, so fighting a ramped fire was hopeless: a bomb's cleared hole " +
                    "refilled itself in seconds. With it, dousing genuinely carves firebreaks — " +
                    "clear a line ahead of the front and hold it. 0 disables (old behavior).",
                    new AcceptableValueRange<float>(0f, 600f)));

            RainSuppressesGroundFire = config.Bind(
                "Weather", "RainSuppressesGroundFire", true,
                "While rain falls on a ground cell it can't spread cell-to-cell, can't light an " +
                "object above it, and its clock runs faster (RainGroundBurnDurationMultiplier) so " +
                "it dies out under the rain. Rain is judged AT THE FIRE, the way vanilla would show " +
                "it to a player standing there - not from the server's own sky, which on a " +
                "dedicated server never changes. Before 0.21.0 this key read that sky and so did " +
                "nothing at all on a dedicated server.");

            RainGroundBurnDurationMultiplier = config.Bind(
                "Weather", "RainGroundBurnDurationMultiplier", 0.3f,
                new ConfigDescription(
                    "How fast a ground cell burns while rain falls on it, as a fraction of its normal " +
                    "burn time: 0.3 = its remaining time passes about 3x faster, so a cell lit in the " +
                    "rain lasts 30% as long and a cell the rain reaches late loses 70% of what it had left.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            RainSuppressesObjectFire = config.Bind(
                "Weather", "RainSuppressesObjectFire", true,
                "While rain falls on a burning tree or building it can't pass fire to anything - no " +
                "neighbouring objects, no ground seeds - and its clock runs faster " +
                "(RainObjectBurnDurationMultiplier) so it goes out over a minute or two instead of " +
                "burning its full time. It still burns, glows and hurts until then, and a direct " +
                "ignition (torch, campfire, lightning) still lights it: rain stops spread and " +
                "shortens fire, it does not forbid fire. New in 0.21.0; object fire ignored weather " +
                "entirely before that.");

            RainObjectBurnDurationMultiplier = config.Bind(
                "Weather", "RainObjectBurnDurationMultiplier", 0.3f,
                new ConfigDescription(
                    "How fast an object burns while rain falls on it, as a fraction of its normal burn " +
                    "time: 0.3 = remaining time passes about 3x faster, so at the 240s default a tree " +
                    "that catches in the rain is out in about 72s.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            ScorchMarksEnabled = config.Bind(
                "Visuals", "ScorchMarksEnabled", true,
                "Leave a dark burn-scar decal on the ground where ground fire burned out or was " +
                "extinguished. Purely cosmetic, self-destructs after ScorchMarkLifetimeSeconds.");

            ScorchMarkLifetimeSeconds = config.Bind(
                "Visuals", "ScorchMarkLifetimeSeconds", 300f,
                new ConfigDescription(
                    "How long a burn-scar decal stays before disappearing. Ignored if UseVanillaDirtPaint is true — real terrain paint is permanent, not timed.",
                    new AcceptableValueRange<float>(10f, 3600f)));

            UseVanillaDirtPaint = config.Bind(
                "Visuals", "UseVanillaDirtPaint", false,
                "Paint REAL bare dirt via vanilla's own terrain system (PaintType.Dirt, the same " +
                "mechanism the Cultivator uses) instead of the procedural decal. GENUINELY HIGHER " +
                "RISK than every other visual system in this mod: TerrainComp is networked and " +
                "writes to persistent per-zone terrain data that gets SAVED TO DISK, unlike " +
                "anything else here, which is purely runtime and reload-safe. Test-world use only " +
                "until proven safe. When enabled, this replaces (not adds to) the scorch decal, " +
                "and the painted dirt is permanent — it does not respect ScorchMarkLifetimeSeconds.");

            DirtPaintRadius = config.Bind(
                "Visuals", "DirtPaintRadius", 2f,
                new ConfigDescription(
                    "Radius (meters) of real dirt painted each time a ground cell burns out and " +
                    "triggers TryPaintScorchedDirt, if UseVanillaDirtPaint is on. This is a direct " +
                    "terrain write (TerrainComp.PaintCleared) — no new object is spawned, so a " +
                    "larger radius covers more ground per call at zero extra instance cost, unlike " +
                    "the old per-cell piece-spawning approach.",
                    new AcceptableValueRange<float>(0.5f, 8f)));

            FireRampEnabled = config.Bind(
                "Fire", "FireRampEnabled", true,
                "A fire starts weak and gradually intensifies toward its full configured strength " +
                "over FireRampDurationSeconds, instead of hitting max spread radius and max " +
                "concurrent cap immediately on first ignition. Resets when a fire fully burns out " +
                "or clearfires is used, so the next fire ramps up fresh.");

            FireRampDurationSeconds = config.Bind(
                "Fire", "FireRampDurationSeconds", 600f,
                new ConfigDescription(
                    "Seconds from first ignition until a fire reaches full configured intensity " +
                    "(spread radius, max concurrent caps).",
                    new AcceptableValueRange<float>(5f, 1200f)));

            FireRampStartFraction = config.Bind(
                "Fire", "FireRampStartFraction", 0.1f,
                new ConfigDescription(
                    "Intensity fraction a brand-new fire starts at (0.25 = 25% of configured " +
                    "radius/caps), ramping linearly to 100% by FireRampDurationSeconds.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            GroundFuelExhaustionEnabled = config.Bind(
                "Ground", "GroundFuelExhaustionEnabled", true,
                "Once a ground cell burns out, it can't reignite until GroundFuelRegrowSeconds has " +
                "passed. Without this, a burned-out cell was free to be re-lit by a neighbor the " +
                "very next cycle, causing the fire to churn back and forth over the same small " +
                "footprint instead of advancing outward — the exact pattern that produced patchy, " +
                "gap-filled burn scars. Time-bounded rather than tied to the whole fire dying out: " +
                "a long player-sustained fire that never fully extinguishes would otherwise grow " +
                "the tracking set without limit for the entire session.");

            GroundFuelRegrowSeconds = config.Bind(
                "Ground", "GroundFuelRegrowSeconds", 90f,
                new ConfigDescription(
                    "Seconds after a ground cell burns out before it's eligible to reignite again, " +
                    "if GroundFuelExhaustionEnabled is true. Independent of whether the overall " +
                    "fire is still burning elsewhere.",
                    new AcceptableValueRange<float>(5f, 1200f)));

            TreeRegrowthEnabled = config.Bind(
                "Trees", "TreeRegrowthEnabled", true,
                "If true, a tree that finished burning down respawns as the same species after " +
                "TreeRegrowthSeconds, provided the spot is still clear. Small-scope by design: no " +
                "stump placeholder while waiting. Pending regrowth survives a server restart " +
                "since 0.18.0 via the fire-state sidecar (see PersistFiresEnabled) — before that " +
                "it was in-memory only and a restart permanently ate any tree mid-regrow.");

            TreeRegrowthSeconds = config.Bind(
                "Trees", "TreeRegrowthSeconds", 900f,
                new ConfigDescription(
                    "Seconds after a tree burns down before it attempts to respawn, if " +
                    "TreeRegrowthEnabled is true. A blocked spot (something built there since) " +
                    "retries every 30s for up to ~10 minutes before giving up on that tree.",
                    new AcceptableValueRange<float>(30f, 7200f)));

            GroundFirebreaksEnabled = config.Bind(
                "Ground", "GroundFirebreaksEnabled", true,
                "A ground cell on cleared (real dirt path) or cultivated (tilled) terrain won't " +
                "ignite from ground-to-ground spread — no grass fuel there, so a real path or " +
                "tilled strip functions as an actual firebreak. Read-only terrain query, no " +
                "terrain is modified by this check.");

            GroundWaterBlocksSpreadEnabled = config.Bind(
                "Ground", "GroundWaterBlocksSpreadEnabled", true,
                "A ground cell won't ignite if the real terrain height there is at or below the " +
                "world's actual water level — no grass grows on open water. Without this, ground " +
                "fire had no way to distinguish land from ocean/lake and could spread straight " +
                "through water (confirmed: a small island's fire crossed clean through the " +
                "surrounding water). Read-only terrain query, same as GroundFirebreaksEnabled.");

            WindSpreadBiasEnabled = config.Bind(
                "Ground", "WindSpreadBiasEnabled", true,
                "Weight ground-to-ground spread by vanilla's own wind direction (EnvMan.GetWindDir), " +
                "so the fire front elongates downwind and narrows upwind instead of spreading " +
                "evenly in all directions. Falls back to unweighted spread if wind can't be read " +
                "(e.g. EnvMan not initialized yet).");

            WindUpwindIgniteChance = config.Bind(
                "Ground", "WindUpwindIgniteChance", 0.2f,
                new ConfigDescription(
                    "Ignite chance (0-1) for a ground cell directly upwind of the burning cell. " +
                    "Downwind neighbors always ignite (chance 1.0); this is the floor for the " +
                    "opposite extreme, linearly interpolated in between by wind angle. Only used " +
                    "if WindSpreadBiasEnabled is true.",
                    new AcceptableValueRange<float>(0f, 1f)));

            WindInfluence = config.Bind(
                "Ground", "WindInfluence", 1f,
                new ConfigDescription(
                    "How much the directional weighting from WindUpwindIgniteChance actually " +
                    "counts. 0 = ignore wind entirely (every neighbor ignites, same as turning " +
                    "WindSpreadBiasEnabled off); 1 = apply the full upwind/downwind bias. This " +
                    "is multiplied by vanilla's LIVE wind strength (EnvMan.GetWindIntensity, " +
                    "itself clamped 0.05-1), so a dead-calm day spreads nearly evenly and a gale " +
                    "produces a sharply elongated front — through 0.17.1 the bias was applied at " +
                    "full strength regardless of how hard the wind was actually blowing, which is " +
                    "why weather changes never visibly altered the fire's shape. Defaults to 1 so " +
                    "this setting stays out of the way and the live wind alone decides how sharp " +
                    "the front is; note that even at 1 the bias is softer than the old always-full " +
                    "behavior at anything below a gale, since intensity still multiplies in. Turn " +
                    "it DOWN to damp how much weather swings the fire's shape. If wind strength " +
                    "can't be read, this falls back to full strength so the bias behaves as it did " +
                    "before rather than silently vanishing.",
                    new AcceptableValueRange<float>(0f, 1f)));

            DousingBombRadius = config.Bind(
                "Items", "DousingBombRadius", 6f,
                new ConfigDescription(
                    "Radius (meters) cleared of fire — ground cells and burning objects both — where a " +
                    "thrown Dousing Bomb lands. The bomb itself (cloned from vanilla's ooze bomb, " +
                    "hand-craftable from 3 Resin + 2 Leather scraps) always exists; this only tunes " +
                    "how much fire one throw puts out.",
                    new AcceptableValueRange<float>(1f, 15f)));

            PersistFiresEnabled = config.Bind(
                "General", "PersistFiresEnabled", true,
                "Live fire state survives a server restart: burning objects and ground cells (with " +
                "their remaining burn time), spent-fuel cells, the fire's origin/ramp/igniter, and " +
                "pending tree regrowth. Stored as a small sidecar file next to the world save " +
                "(worlds_local), written every 60s and on clearfires/shutdown — a hard kill loses " +
                "at most the last minute of fire drift. Server-side only, like the simulation " +
                "itself. Note: for a Steam-Cloud world the sidecar stays on the host machine and " +
                "does not travel with the save.");

            GroundMaxSpreadDistanceEnabled = config.Bind(
                "Ground", "GroundMaxSpreadDistanceEnabled", true,
                "Leash ground-to-ground spread to GroundMaxSpreadDistance from where the current " +
                "fire first ignited. Without this, cell-to-adjacent-cell propagation (see " +
                "IgniteAdjacentGroundCells) has no distance limit at all — only a cap on how many " +
                "cells burn AT ONCE — so a wind-driven front can march indefinitely away from any " +
                "player, silently consuming cycles and never reaching the structures/players it " +
                "would need to be near to actually spread or deal damage. Object-to-ground seeding " +
                "(a burning piece/tree lighting the ground right around itself) is already bounded " +
                "by GroundSpreadRadius and effectively never hits this leash.");

            GroundMaxSpreadDistance = config.Bind(
                "Ground", "GroundMaxSpreadDistance", 40f,
                new ConfigDescription(
                    "Max distance (meters) ground fire can travel from the current fire's origin " +
                    "point (captured once, at first ignition) via cell-to-cell spread, if " +
                    "GroundMaxSpreadDistanceEnabled is true. Single global origin, not per-fire — " +
                    "same simplification the ramp clock already makes (see FireRampEnabled) — so a " +
                    "second, unrelated fire started while an earlier one is still smoldering is " +
                    "leashed to the FIRST fire's origin, not its own. Origin resets once every fire " +
                    "fully burns out.",
                    new AcceptableValueRange<float>(5f, 500f)));

            // After EVERY bind, so each ConfigEntry the plan may reset exists: apply, stamp, save.
            ConfigMigration.Finish(config, ConfigVersion);
        }
    }
}