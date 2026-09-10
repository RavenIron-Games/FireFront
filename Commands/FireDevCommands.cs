using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Commands
{
    /// <summary>
    /// Console commands:
    ///   ignite              - ignite the piece/tree/log under the crosshair
    ///   startfire [r]       - ignite everything burnable within r meters of the player (default 5)
    ///   stopfire            - extinguish the target under the crosshair
    ///   clearfires          - extinguish everything and empty the queue
    ///   firestatus          - print active/queued counts and current config
    ///   firedebug           - toggle verbose logging
    ///   fireset k v         - live config setter
    ///   firelistprefabs [f] - list registered prefab names containing filter (default "fire")
    ///   firetreeregrow      - force all pending tree regrowth to attempt now (skip the timer)
    ///   firetreeregrowlist  - list pending tree regrowth entries (prefab, position, time left, attempts)
    /// </summary>
    public static class FireDevCommands
    {
        public static void RegisterAll()
        {
            new Terminal.ConsoleCommand("ignite",
                "FireFront: ignite the piece/tree/log under the crosshair",
                args => Ignite(args));

            new Terminal.ConsoleCommand("startfire",
                "FireFront: startfire [radius] - ignite burnable things around the player",
                args => StartFire(args));

            new Terminal.ConsoleCommand("stopfire",
                "FireFront: extinguish the target under the crosshair",
                args => StopFire(args));

            new Terminal.ConsoleCommand("clearfires",
                "FireFront: extinguish all fires and clear the queue",
                args => ClearFires(args));

            new Terminal.ConsoleCommand("firestatus",
                "FireFront: show burning/queue counts and config",
                args => FireStatus(args));

            new Terminal.ConsoleCommand("firedebug",
                "FireFront: toggle verbose logging",
                args => FireDebug(args));

            new Terminal.ConsoleCommand("fireset",
                "FireFront: fireset <burnduration|firematurity|spreadradius|maxburning|queuesize|spreadinterval|trees|burnbuildings|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|douseimmunity|rainsuppress|rainmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|lowspec|debug|burntheworld|smouldering|smoulderafter|enabled> <value>",
                args => FireSet(args));

            new Terminal.ConsoleCommand("firelistprefabs",
                "FireFront: firelistprefabs [filter] - list prefab names containing filter (default 'fire')",
                args => FireListPrefabs(args));

            new Terminal.ConsoleCommand("firepurgevfx",
                "FireFront: emergency cleanup - destroys every live vanilla Fire-class instance in the scene",
                args => FirePurgeVfx(args));

            new Terminal.ConsoleCommand("firecheckprefab",
                "FireFront: firecheckprefab <name> - inspect a prefab's components WITHOUT spawning it, to check if it's safe for vfx",
                args => FireCheckPrefab(args));

            new Terminal.ConsoleCommand("firegroundignite",
                "FireFront: firegroundignite [radius] - seed ground-fire cells around the player (default GroundSpreadRadius), for testing ground spread without needing a tree/piece",
                args => FireGroundIgnite(args));

            new Terminal.ConsoleCommand("fireinspecteffectarea",
                "FireFront: fireinspecteffectarea [maxdistance] - reads real field values off the nearest EffectArea (e.g. a campfire's burn zone), to verify the correct 'Burning' type before we configure our own",
                args => FireInspectEffectArea(args));

            new Terminal.ConsoleCommand("firetreeregrow",
                "FireFront: force every pending tree-regrowth entry to attempt right now instead of waiting out its timer",
                args => FireTreeRegrow(args));

            new Terminal.ConsoleCommand("firetreeregrowlist",
                "FireFront: list pending tree-regrowth entries (prefab, position, time left, attempts) — use to check for duplicates",
                args => FireTreeRegrowList(args));

            FireLogger.Info("Dev commands registered.");
        }

        // ---------------------------------------------------------------

        private static void Ignite(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            Component target = ValheimBridge.RaycastBurnable();
            if (target == null) { Say(args, "No burnable target under crosshair."); return; }
            if (!ValheimBridge.IsBurnable(target)) { Say(args, $"Not burnable: {ValheimBridge.NameOf(target)}"); return; }

            if (ValheimBridge.IsServer())
            {
                FireManager.Instance.TryIgnite(target);
                Say(args, FireManager.Instance.IsBurning(target)
                    ? $"Ignited: {ValheimBridge.NameOf(target)}"
                    : $"Queued or dropped (cap full): {ValheimBridge.NameOf(target)}");
            }
            else
            {
                // Calling TryIgnite directly here would populate THIS client's own
                // _burning dict — but Update()'s simulation loop only runs on the
                // server now, so that fire would ignite visually and then never
                // expire, while its StartBurning broadcast tells every other peer
                // a fire started that the server has no record of. Route through
                // the same RPC real ignition already uses instead.
                ZDOID? id = ValheimBridge.ZDOIDOf(target);
                if (id.HasValue)
                {
                    // The typist is the igniter — commands run where they are typed.
                    ValheimBridge.SendIgniteRequestToServer(id.Value, ValheimBridge.LocalPlayerId());
                    Say(args, $"Sent ignite request to server: {ValheimBridge.NameOf(target)}");
                }
                else
                {
                    Say(args, $"Couldn't resolve a ZDOID for {ValheimBridge.NameOf(target)} — can't forward to server.");
                }
            }
        }

        private static void StartFire(Terminal.ConsoleEventArgs args)
        {
            // Relay FIRST: the server authorizes the sending peer against its own
            // adminlist (the real, unspoofable check). The local RequireAdmin only
            // guards direct execution here (host/server console) — running it before
            // the relay blocked genuine admins whose client-side flag had not synced
            // yet ("Admin only." x3, live 2026-08-28).
            if (RelayIfClient(args)) return;
            if (!RequireAdmin(args)) return;

            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }
            Vector3 pos = posOrNull.Value;

            float radius = args.TryParameterFloat(1, 5f);
            float radiusSqr = radius * radius;

            int hit = 0;

            var pieces = ValheimBridge.AllPieces;
            for (int i = 0; i < pieces.Count; i++)
                hit += TryIgniteIfInRange(pieces[i], pos, radiusSqr);

            if (FireConfig.BurnTreesAndLogs.Value)
            {
                var trees = Object.FindObjectsOfType<TreeBase>();
                for (int i = 0; i < trees.Length; i++)
                    hit += TryIgniteIfInRange(trees[i], pos, radiusSqr);

                var logs = Object.FindObjectsOfType<TreeLog>();
                for (int i = 0; i < logs.Length; i++)
                    hit += TryIgniteIfInRange(logs[i], pos, radiusSqr);
            }

            // The instance scans above see NOTHING on a headless server, so a
            // relayed startfire always answered "0 targets" there (live,
            // 2026-08-28, in a meadow full of burnables). The ZDO layer is the
            // authoritative census headless; anything the instance pass already
            // lit is skipped inside, so the count never doubles.
            hit += FireManager.Instance.IgniteBurnablesNear(pos, radius);

            Say(args, $"startfire: attempted {hit} targets within {radius}m. {FireManager.Instance.StatusLine()}");
        }

        private static int TryIgniteIfInRange(Component target, Vector3 pos, float radiusSqr)
        {
            if (!ValheimBridge.IsBurnable(target)) return 0;
            if ((ValheimBridge.PositionOf(target) - pos).sqrMagnitude > radiusSqr) return 0;
            FireManager.Instance.TryIgnite(target);
            return 1;
        }

        private static void StopFire(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            Component target = ValheimBridge.RaycastBurnable();
            if (target == null) { Say(args, "No target under crosshair."); return; }

            if (ValheimBridge.IsServer())
            {
                FireManager.Instance.Extinguish(target);
                Say(args, $"Extinguished: {ValheimBridge.NameOf(target)}");
            }
            else
            {
                ZDOID? id = ValheimBridge.ZDOIDOf(target);
                Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
                if (id.HasValue && posOrNull.HasValue)
                {
                    // radius 0 — only extinguish the targeted object, don't also
                    // clear ground fire near the player (that's what the real
                    // extinguish key does; this command targets one thing).
                    ValheimBridge.SendExtinguishRequestToServer(id.Value, posOrNull.Value, 0f);
                    Say(args, $"Sent extinguish request to server: {ValheimBridge.NameOf(target)}");
                }
                else
                {
                    Say(args, $"Couldn't resolve target/position — can't forward to server.");
                }
            }
        }

        private static void ClearFires(Terminal.ConsoleEventArgs args)
        {
            // Relay FIRST: the server authorizes the sending peer against its own
            // adminlist (the real, unspoofable check). The local RequireAdmin only
            // guards direct execution here (host/server console) — running it before
            // the relay blocked genuine admins whose client-side flag had not synced
            // yet ("Admin only." x3, live 2026-08-28).
            if (RelayIfClient(args)) return;
            if (!RequireAdmin(args)) return;

            FireManager.Instance.ClearAll();
            Say(args, "All fires cleared.");
        }

        private static void FireStatus(Terminal.ConsoleEventArgs args)
        {
            if (ValheimBridge.IsServer())
            {
                Say(args, FireManager.Instance.StatusLine());
                return;
            }

            // A client's own StatusLine always shows burning 0/ground 0 — the
            // counts live on the server and the client is a visual mirror, which
            // made the local line actively misleading (confirmed by a real "the
            // fire is raging around me but firestatus says 0" screenshot). Ask
            // the server for the authoritative line instead; it prints as
            // "[server] FireFront: ..." when the reply lands a moment later.
            Say(args, "FireFront: fetching server status... (no reply = server runs pre-0.18.8)");
            ValheimBridge.SendStatusRequestToServer();
        }

        private static void FireDebug(Terminal.ConsoleEventArgs args)
        {
            FireConfig.VerboseLogging.Value = !FireConfig.VerboseLogging.Value;
            Say(args, $"Verbose logging: {FireConfig.VerboseLogging.Value}");
        }

        private static void FireSet(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                Say(args, "Usage: fireset <burnduration|firematurity|spreadradius|maxburning|queuesize|spreadinterval|trees|burnbuildings|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|douseimmunity|rainsuppress|rainmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|lowspec|debug|burntheworld|smouldering|smoulderafter|enabled> <value>");
                return;
            }

            string key = args[1].ToLowerInvariant();
            string raw = args[2];

            switch (key)
            {
                case "burnduration":
                    if (float.TryParse(raw, out float bd)) { FireConfig.BurnDurationSeconds.Value = bd; Ok(args, key, bd); }
                    else Bad(args, raw);
                    break;
                case "spreadradius":
                    if (float.TryParse(raw, out float sr)) { FireConfig.SpreadRadius.Value = sr; Ok(args, key, FireConfig.SpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "maxburning":
                    if (int.TryParse(raw, out int mb)) { FireConfig.MaxConcurrentBurning.Value = mb; Ok(args, key, mb); }
                    else Bad(args, raw);
                    break;
                case "queuesize":
                    if (int.TryParse(raw, out int qs)) { FireConfig.QueueSize.Value = qs; Ok(args, key, FireConfig.QueueSize.Value); }
                    else Bad(args, raw);
                    break;
                case "spreadinterval":
                    if (float.TryParse(raw, out float si)) { FireConfig.SpreadCheckInterval.Value = si; Ok(args, key, si); }
                    else Bad(args, raw);
                    break;
                case "smouldering":
                    if (bool.TryParse(raw, out bool smv)) { FireConfig.SmoulderingVfxEnabled.Value = smv; Ok(args, key, smv); }
                    else Bad(args, raw);
                    break;
                case "smoulderafter":
                    if (float.TryParse(raw, out float sma)) { FireConfig.SmoulderAfterFraction.Value = sma; Ok(args, key, FireConfig.SmoulderAfterFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "burntheworld":
                    if (bool.TryParse(raw, out bool btw))
                    {
                        FireConfig.WatchTheWorldBurn.Value = btw;
                        Say(args, $"burntheworld = {btw} (in force: {FireConfig.ApocalypseActive}). " +
                                  $"maturity {FireConfig.EffectiveSpreadMaturityFraction}, firebreaks {FireConfig.EffectiveGroundFirebreaksEnabled}, " +
                                  $"water {FireConfig.EffectiveGroundWaterBlocksSpreadEnabled}, leash {FireConfig.EffectiveGroundMaxSpreadDistanceEnabled}, " +
                                  $"ramp {FireConfig.EffectiveFireRampEnabled}, regrow {FireConfig.EffectiveTreeRegrowthEnabled}, " +
                                  $"burn cap {FireConfig.EffectiveMaxConcurrentBurning}/fire, ground {FireConfig.EffectiveGroundMaxConcurrent}/fire, " +
                                  $"interval {FireConfig.EffectiveSpreadCheckInterval}s. Your own settings are untouched.");
                    }
                    else Bad(args, raw);
                    break;
                case "debug":
                    if (bool.TryParse(raw, out bool dbg)) { FireConfig.VerboseLogging.Value = dbg; Ok(args, key, dbg); }
                    else Bad(args, raw);
                    break;
                case "lowspec":
                    if (bool.TryParse(raw, out bool ls))
                    {
                        FireConfig.LowSpecPreset.Value = ls;
                        // Report what it actually resolves to — the caps are what
                        // the player wants to see change, not the flag they typed.
                        Say(args, $"lowspec = {ls} (burning cap {FireConfig.EffectiveMaxConcurrentBurning}, " +
                                  $"ground {FireConfig.EffectiveGroundMaxConcurrent}, vfx {FireConfig.EffectiveGroundVfxMaxConcurrent}, " +
                                  $"dmg {FireConfig.EffectiveGroundDamageMaxConcurrent}, interval {FireConfig.EffectiveSpreadCheckInterval}s, " +
                                  $"scorch {FireConfig.EffectiveScorchMarksEnabled}). Your own settings are untouched.");
                    }
                    else Bad(args, raw);
                    break;
                case "trees":
                    if (bool.TryParse(raw, out bool tr)) { FireConfig.BurnTreesAndLogs.Value = tr; Ok(args, key, tr); }
                    else Bad(args, raw);
                    break;
                case "vfx":
                    FireConfig.VfxPrefabName.Value = raw;
                    Ok(args, key, string.IsNullOrEmpty(raw) ? "(disabled)" : raw);
                    break;
                case "procedural":
                    if (bool.TryParse(raw, out bool pr)) { FireConfig.UseProceduralVfx.Value = pr; Ok(args, key, pr); }
                    else Bad(args, raw);
                    break;
                case "groundenabled":
                    if (bool.TryParse(raw, out bool ge)) { FireConfig.GroundSpreadEnabled.Value = ge; Ok(args, key, ge); }
                    else Bad(args, raw);
                    break;
                case "groundcellsize":
                    if (float.TryParse(raw, out float gcs)) { FireConfig.GroundCellSize.Value = gcs; Ok(args, key, FireConfig.GroundCellSize.Value); }
                    else Bad(args, raw);
                    break;
                case "groundradius":
                    if (float.TryParse(raw, out float gr)) { FireConfig.GroundSpreadRadius.Value = gr; Ok(args, key, FireConfig.GroundSpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "groundburnduration":
                    if (float.TryParse(raw, out float gbd)) { FireConfig.GroundBurnDurationSeconds.Value = gbd; Ok(args, key, gbd); }
                    else Bad(args, raw);
                    break;
                case "groundmax":
                    if (int.TryParse(raw, out int gm)) { FireConfig.GroundMaxConcurrent.Value = gm; Ok(args, key, gm); }
                    else Bad(args, raw);
                    break;
                case "groundvfxmax":
                    if (int.TryParse(raw, out int gvm)) { FireConfig.GroundVfxMaxConcurrent.Value = gvm; Ok(args, key, gvm); }
                    else Bad(args, raw);
                    break;
                case "grounddamagemax":
                    if (int.TryParse(raw, out int gdm)) { FireConfig.GroundDamageMaxConcurrent.Value = gdm; Ok(args, key, gdm); }
                    else Bad(args, raw);
                    break;
                case "firehurts":
                    if (bool.TryParse(raw, out bool fh)) { FireConfig.FireHurtsEnabled.Value = fh; Ok(args, key, fh); }
                    else Bad(args, raw);
                    break;
                case "firehurtsplayeronly":
                    if (bool.TryParse(raw, out bool fhp)) { FireConfig.FireHurtsPlayerOnly.Value = fhp; Ok(args, key, fhp); }
                    else Bad(args, raw);
                    break;
                case "firehurtsradius":
                    if (float.TryParse(raw, out float fhr)) { FireConfig.FireHurtsObjectRadius.Value = fhr; Ok(args, key, FireConfig.FireHurtsObjectRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "firedamage":
                    if (float.TryParse(raw, out float fd)) { FireConfig.FireDamagePerTick.Value = fd; Ok(args, key, FireConfig.FireDamagePerTick.Value); }
                    else Bad(args, raw);
                    break;
                case "firetickinterval":
                    if (float.TryParse(raw, out float fti)) { FireConfig.FireDamageTickInterval.Value = fti; Ok(args, key, FireConfig.FireDamageTickInterval.Value); }
                    else Bad(args, raw);
                    break;
                case "extinguishradius":
                    if (float.TryParse(raw, out float exr)) { FireConfig.ExtinguishGroundRadius.Value = exr; Ok(args, key, FireConfig.ExtinguishGroundRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "rainsuppress":
                    if (bool.TryParse(raw, out bool rs)) { FireConfig.RainSuppressesGroundFire.Value = rs; Ok(args, key, rs); }
                    else Bad(args, raw);
                    break;
                case "rainmultiplier":
                    if (float.TryParse(raw, out float rm)) { FireConfig.RainGroundBurnDurationMultiplier.Value = rm; Ok(args, key, FireConfig.RainGroundBurnDurationMultiplier.Value); }
                    else Bad(args, raw);
                    break;
                case "scorchmarks":
                    if (bool.TryParse(raw, out bool sm)) { FireConfig.ScorchMarksEnabled.Value = sm; Ok(args, key, sm); }
                    else Bad(args, raw);
                    break;
                case "dirtpaint":
                    if (bool.TryParse(raw, out bool dp)) { FireConfig.UseVanillaDirtPaint.Value = dp; Ok(args, key, dp); }
                    else Bad(args, raw);
                    break;
                case "dirtpaintradius":
                    if (float.TryParse(raw, out float dpr)) { FireConfig.DirtPaintRadius.Value = dpr; Ok(args, key, FireConfig.DirtPaintRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "scorchlifetime":
                    if (float.TryParse(raw, out float sl)) { FireConfig.ScorchMarkLifetimeSeconds.Value = sl; Ok(args, key, FireConfig.ScorchMarkLifetimeSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampenabled":
                    if (bool.TryParse(raw, out bool re)) { FireConfig.FireRampEnabled.Value = re; Ok(args, key, re); }
                    else Bad(args, raw);
                    break;
                case "rampduration":
                    if (float.TryParse(raw, out float rd)) { FireConfig.FireRampDurationSeconds.Value = rd; Ok(args, key, FireConfig.FireRampDurationSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampstart":
                    if (float.TryParse(raw, out float rst)) { FireConfig.FireRampStartFraction.Value = rst; Ok(args, key, FireConfig.FireRampStartFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "enabled":
                    if (bool.TryParse(raw, out bool en)) { FireConfig.Enabled.Value = en; Ok(args, key, en); }
                    else Bad(args, raw);
                    break;
                case "exhaustionenabled":
                    if (bool.TryParse(raw, out bool exhen)) { FireConfig.GroundFuelExhaustionEnabled.Value = exhen; Ok(args, key, exhen); }
                    else Bad(args, raw);
                    break;
                case "fuelregrow":
                    if (float.TryParse(raw, out float fregrow)) { FireConfig.GroundFuelRegrowSeconds.Value = fregrow; Ok(args, key, FireConfig.GroundFuelRegrowSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "windbias":
                    if (bool.TryParse(raw, out bool wb)) { FireConfig.WindSpreadBiasEnabled.Value = wb; Ok(args, key, wb); }
                    else Bad(args, raw);
                    break;
                case "windupwindchance":
                    if (float.TryParse(raw, out float wuc)) { FireConfig.WindUpwindIgniteChance.Value = wuc; Ok(args, key, FireConfig.WindUpwindIgniteChance.Value); }
                    else Bad(args, raw);
                    break;
                case "windinfluence":
                    if (float.TryParse(raw, out float wi)) { FireConfig.WindInfluence.Value = wi; Ok(args, key, FireConfig.WindInfluence.Value); }
                    else Bad(args, raw);
                    break;
                case "dousingradius":
                    if (float.TryParse(raw, out float dbr)) { FireConfig.DousingBombRadius.Value = dbr; Ok(args, key, FireConfig.DousingBombRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "persistfires":
                    if (bool.TryParse(raw, out bool pf)) { FireConfig.PersistFiresEnabled.Value = pf; Ok(args, key, pf); }
                    else Bad(args, raw);
                    break;
                case "burnbuildings":
                    if (bool.TryParse(raw, out bool bb)) { FireConfig.BurnPlayerBuildings.Value = bb; Ok(args, key, bb); }
                    else Bad(args, raw);
                    break;
                case "douseimmunity":
                    if (float.TryParse(raw, out float di)) { FireConfig.DouseImmunitySeconds.Value = di; Ok(args, key, FireConfig.DouseImmunitySeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "firematurity":
                    if (float.TryParse(raw, out float fm)) { FireConfig.SpreadMaturityFraction.Value = fm; Ok(args, key, FireConfig.SpreadMaturityFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "firebreaks":
                    if (bool.TryParse(raw, out bool fb)) { FireConfig.GroundFirebreaksEnabled.Value = fb; Ok(args, key, fb); }
                    else Bad(args, raw);
                    break;
                case "treeregrowth":
                    if (bool.TryParse(raw, out bool tre)) { FireConfig.TreeRegrowthEnabled.Value = tre; Ok(args, key, tre); }
                    else Bad(args, raw);
                    break;
                case "treeregrowthseconds":
                    if (float.TryParse(raw, out float trs)) { FireConfig.TreeRegrowthSeconds.Value = trs; Ok(args, key, FireConfig.TreeRegrowthSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "groundleashenabled":
                    if (bool.TryParse(raw, out bool gle)) { FireConfig.GroundMaxSpreadDistanceEnabled.Value = gle; Ok(args, key, gle); }
                    else Bad(args, raw);
                    break;
                case "groundleashdistance":
                    if (float.TryParse(raw, out float gld)) { FireConfig.GroundMaxSpreadDistance.Value = gld; Ok(args, key, FireConfig.GroundMaxSpreadDistance.Value); }
                    else Bad(args, raw);
                    break;
                default:
                    Say(args, $"Unknown key: {key}");
                    break;
            }

            // Applied locally above (a few settings ARE read client-side: the
            // extinguish key radius, the dousing bomb radius) — and forwarded
            // here so the same command also lands on the server, where the
            // simulation actually reads it. Cost two real debugging rounds
            // ('rampstart 1', 'burnbuildings false') before this existed.
            ForwardToServerIfClient(key, raw);
        }

        private static void FireListPrefabs(Terminal.ConsoleEventArgs args)
        {
            string filter = args.Length >= 2 ? args[1] : "fire";
            var names = ValheimBridge.FindPrefabNamesContaining(filter);

            if (names.Count == 0)
            {
                Say(args, $"No registered prefabs matching '{filter}'.");
                return;
            }

            Say(args, $"{names.Count} prefabs matching '{filter}':");
            // Print in chunks so the console doesn't eat one giant line.
            for (int i = 0; i < names.Count; i++)
            {
                Say(args, $"  {names[i]}");
            }
        }

        private static void FirePurgeVfx(Terminal.ConsoleEventArgs args)
        {
            int destroyed = ValheimBridge.PurgeAllVanillaFireInstances();
            Say(args, $"Purged {destroyed} leaked vanilla Fire instance(s).");
        }

        private static void FireCheckPrefab(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Say(args, "Usage: firecheckprefab <exact prefab name>");
                return;
            }

            string name = args[1];
            var (found, hasZNetView, scripts) = ValheimBridge.InspectPrefab(name);

            if (!found)
            {
                Say(args, $"No prefab named '{name}' found.");
                return;
            }

            if (!hasZNetView && scripts.Count == 0)
            {
                Say(args, $"'{name}' looks SAFE: no ZNetView, no scripts. Fine to use as vfx.");
                return;
            }

            Say(args, $"'{name}' is RISKY — do not use as vfx without further checking:");
            Say(args, $"  ZNetView present: {hasZNetView}");
            if (scripts.Count > 0)
            {
                Say(args, $"  Scripts: {string.Join(", ", scripts)}");
            }
        }

        private static void FireGroundIgnite(Terminal.ConsoleEventArgs args)
        {
            // Relay FIRST: the server authorizes the sending peer against its own
            // adminlist (the real, unspoofable check). The local RequireAdmin only
            // guards direct execution here (host/server console) — running it before
            // the relay blocked genuine admins whose client-side flag had not synced
            // yet ("Admin only." x3, live 2026-08-28).
            if (RelayIfClient(args)) return;
            if (!RequireAdmin(args)) return;

            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }

            float radius = args.TryParameterFloat(1, FireConfig.GroundSpreadRadius.Value);
            FireManager.Instance.IgniteGroundNear(posOrNull.Value, radius);
            Say(args, $"Seeded ground fire within {radius}m of player. {FireManager.Instance.StatusLine()}");
        }

        private static void FireInspectEffectArea(Terminal.ConsoleEventArgs args)
        {
            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }

            float maxDistance = args.TryParameterFloat(1, 15f);
            string result = ValheimBridge.InspectNearestEffectArea(posOrNull.Value, maxDistance);
            Say(args, result);
        }

        private static void FireTreeRegrow(Terminal.ConsoleEventArgs args)
        {
            if (RelayIfClient(args)) return;

            (int attempted, int stillPending) = FireManager.Instance.ForceTreeRegrowthNow();
            Say(args, $"firetreeregrow: forced {attempted} pending entries, {stillPending} still pending after attempt " +
                       "(blocked spots retry on backoff rather than failing permanently).");
        }

        private static void FireTreeRegrowList(Terminal.ConsoleEventArgs args)
        {
            if (RelayIfClient(args)) return;

            var lines = FireManager.Instance.DumpPendingRegrowth();
            if (lines.Count == 0) { Say(args, "No pending tree regrowth entries."); return; }

            Say(args, $"{lines.Count} pending regrowth entries:");
            foreach (string line in lines) Say(args, "  " + line);
        }

        // ---------------------------------------------------------------

        private static void Say(Terminal.ConsoleEventArgs args, string msg)
        {
            if (_replySink != null) { _replySink(msg); return; } // relayed: stream to the requesting peer
            args.Context?.AddString(msg);
            FireLogger.Info(msg);
        }

        /// <summary>
        /// Gates the state-mutating dev commands (fireignite, stopfire, startfire,
        /// clearfires, firegroundignite) to admins/host only, now that this runs
        /// on a real shared server with other players connected. Deliberately
        /// does NOT gate the normal fire-arrow ignition path (RPC_Damage
        /// patches) — that's the mod working as intended for every player, not
        /// a debug tool. Returns true (allowed) if the check passes.
        /// </summary>
        private static bool RequireAdmin(Terminal.ConsoleEventArgs args)
        {
            if (ValheimBridge.IsLocalPlayerAdmin()) return true;
            Say(args, "Admin only.");
            return false;
        }

        // ---------------------------------------------------------------
        // Generic command relay. Console commands run where they're typed;
        // for commands that read or mutate SERVER state, the client sends the
        // whole command line to the server, the same handler runs there, and
        // every Say() it produces streams back to the typist's console as
        // "[server] ..." lines. This replaced five separate "only works run
        // from the server" refusals with actual behavior — and every future
        // relayable command inherits the plumbing by joining the whitelist.
        // ---------------------------------------------------------------

        // WHITELIST — the only command names ExecuteRelayed will run. Never
        // execute arbitrary console lines from the network: the relay is a
        // remote-execution surface and this dictionary is its entire attack
        // area. Crosshair commands (ignite, stopfire) can't relay — target
        // resolution is inherently local — and firestatus/fireset have their
        // own dedicated forwards.
        private static readonly System.Collections.Generic.Dictionary<string, System.Action<Terminal.ConsoleEventArgs>> _relayable =
            new System.Collections.Generic.Dictionary<string, System.Action<Terminal.ConsoleEventArgs>>
            {
                { "startfire", StartFire },
                { "clearfires", ClearFires },
                { "firegroundignite", FireGroundIgnite },
                { "firetreeregrow", FireTreeRegrow },
                { "firetreeregrowlist", FireTreeRegrowList },
            };

        // When non-null, Say() writes here instead of the local console —
        // set only around a relayed invocation (main thread, no concurrency).
        private static System.Action<string> _replySink;

        /// <summary>Client side: forward this command line to the server. True if forwarded.</summary>
        private static bool RelayIfClient(Terminal.ConsoleEventArgs args)
        {
            if (ValheimBridge.IsServer()) return false;
            Say(args, $"FireFront: sent to server — replies appear as [server] lines. ({args.Args[0]})");
            ValheimBridge.SendCommandRelayToServer(args.FullLine);
            return true;
        }

        /// <summary>
        /// Server side of the relay. Authorization happens HERE, against the
        /// sending peer's identity on the server's own adminlist (vanilla's
        /// exact kick/ban check) — the typist's local admin state is never
        /// trusted. The requester's server-tracked position stands in for
        /// "the local player" so radius commands (startfire,
        /// firegroundignite) act around the person who asked.
        /// </summary>
        public static void ExecuteRelayed(long sender, string commandLine)
        {
            if (!ValheimBridge.IsServer()) return;

            if (!ValheimBridge.PeerIsAdmin(sender))
            {
                ValheimBridge.SendStatusResponse(sender, "FireFront: relay refused — you are not in the server's adminlist.");
                FireLogger.Warn($"[RELAY] refused '{commandLine}' from non-admin peer {sender}.");
                return;
            }

            string name = (commandLine ?? "").Split(' ')[0].ToLowerInvariant();
            if (!_relayable.TryGetValue(name, out System.Action<Terminal.ConsoleEventArgs> handler))
            {
                ValheimBridge.SendStatusResponse(sender, $"FireFront: '{name}' is not relayable.");
                return;
            }

            FireLogger.Info($"[RELAY] {name} from peer {sender}: '{commandLine}'");
            // 1.0.7 added a third ConsoleCommand parameter. It is only stored on the new
            // ConsoleEventArgs.Commmand field (vanilla's spelling) and nothing in the console
            // reads it back, so null keeps this relay behaving exactly as it did before — our
            // handlers take the args object and never touch that field.
            var fakeArgs = new Terminal.ConsoleEventArgs(commandLine, null, null);
            _replySink = line => ValheimBridge.SendStatusResponse(sender, line);
            ValheimBridge.SetPositionOverride(ValheimBridge.PeerRefPosition(sender));
            try
            {
                handler(fakeArgs);
            }
            catch (System.Exception ex)
            {
                ValheimBridge.SendStatusResponse(sender, $"FireFront: {name} threw on the server: {ex.Message}");
                FireLogger.Warn($"[RELAY] {name} threw: {ex}");
            }
            finally
            {
                _replySink = null;
                ValheimBridge.SetPositionOverride(null);
            }
        }

        private static void Ok(Terminal.ConsoleEventArgs args, string key, object val) =>
            Say(args, $"fireset {key} = {val}");

        private static void Bad(Terminal.ConsoleEventArgs args, string raw) =>
            Say(args, $"Couldn't parse value: {raw}");

        // ---------------------------------------------------------------
        // Server-forwarded fireset. Console commands run where they're typed,
        // and every one of these settings only matters where the simulation
        // runs — the server. This map + BepInEx's own serialized-value parser
        // let the forwarded (key, raw) land on the server's real ConfigEntries
        // with the same clamping the console path gets, without duplicating
        // the 40-case switch.
        // ---------------------------------------------------------------

        private static System.Collections.Generic.Dictionary<string, BepInEx.Configuration.ConfigEntryBase> _settable;

        private static System.Collections.Generic.Dictionary<string, BepInEx.Configuration.ConfigEntryBase> Settable()
        {
            if (_settable != null) return _settable;
            _settable = new System.Collections.Generic.Dictionary<string, BepInEx.Configuration.ConfigEntryBase>
            {
                // EVERY key handled by the FireSet switch must also appear here,
                // or typing it on a client sets that CLIENT's config and silently
                // never reaches the server — the console reports success, the
                // simulation never changes, and the two disagree with no error
                // anywhere. Caught live 2026-08-29 the day 'lowspec' was added:
                // the client's caps dropped, the server's status line still read
                // lowspec False.
                { "lowspec", FireConfig.LowSpecPreset },
                { "debug", FireConfig.VerboseLogging },
                { "burntheworld", FireConfig.WatchTheWorldBurn },
                { "smouldering", FireConfig.SmoulderingVfxEnabled },
                { "smoulderafter", FireConfig.SmoulderAfterFraction },
                { "burnduration", FireConfig.BurnDurationSeconds },
                { "firematurity", FireConfig.SpreadMaturityFraction },
                { "spreadradius", FireConfig.SpreadRadius },
                { "maxburning", FireConfig.MaxConcurrentBurning },
                { "queuesize", FireConfig.QueueSize },
                { "spreadinterval", FireConfig.SpreadCheckInterval },
                { "trees", FireConfig.BurnTreesAndLogs },
                { "burnbuildings", FireConfig.BurnPlayerBuildings },
                { "vfx", FireConfig.VfxPrefabName },
                { "procedural", FireConfig.UseProceduralVfx },
                { "groundenabled", FireConfig.GroundSpreadEnabled },
                { "groundcellsize", FireConfig.GroundCellSize },
                { "groundradius", FireConfig.GroundSpreadRadius },
                { "groundburnduration", FireConfig.GroundBurnDurationSeconds },
                { "groundmax", FireConfig.GroundMaxConcurrent },
                { "groundvfxmax", FireConfig.GroundVfxMaxConcurrent },
                { "grounddamagemax", FireConfig.GroundDamageMaxConcurrent },
                { "firehurts", FireConfig.FireHurtsEnabled },
                { "firehurtsplayeronly", FireConfig.FireHurtsPlayerOnly },
                { "firehurtsradius", FireConfig.FireHurtsObjectRadius },
                { "firedamage", FireConfig.FireDamagePerTick },
                { "firetickinterval", FireConfig.FireDamageTickInterval },
                { "extinguishradius", FireConfig.ExtinguishGroundRadius },
                { "douseimmunity", FireConfig.DouseImmunitySeconds },
                { "rainsuppress", FireConfig.RainSuppressesGroundFire },
                { "rainmultiplier", FireConfig.RainGroundBurnDurationMultiplier },
                { "scorchmarks", FireConfig.ScorchMarksEnabled },
                { "scorchlifetime", FireConfig.ScorchMarkLifetimeSeconds },
                { "dirtpaint", FireConfig.UseVanillaDirtPaint },
                { "dirtpaintradius", FireConfig.DirtPaintRadius },
                { "rampenabled", FireConfig.FireRampEnabled },
                { "rampduration", FireConfig.FireRampDurationSeconds },
                { "rampstart", FireConfig.FireRampStartFraction },
                { "exhaustionenabled", FireConfig.GroundFuelExhaustionEnabled },
                { "fuelregrow", FireConfig.GroundFuelRegrowSeconds },
                { "windbias", FireConfig.WindSpreadBiasEnabled },
                { "windupwindchance", FireConfig.WindUpwindIgniteChance },
                { "windinfluence", FireConfig.WindInfluence },
                { "dousingradius", FireConfig.DousingBombRadius },
                { "persistfires", FireConfig.PersistFiresEnabled },
                { "firebreaks", FireConfig.GroundFirebreaksEnabled },
                { "treeregrowth", FireConfig.TreeRegrowthEnabled },
                { "treeregrowthseconds", FireConfig.TreeRegrowthSeconds },
                { "groundleashenabled", FireConfig.GroundMaxSpreadDistanceEnabled },
                { "groundleashdistance", FireConfig.GroundMaxSpreadDistance },
                { "enabled", FireConfig.Enabled },
            };
            return _settable;
        }

        /// <summary>Forward a locally-typed fireset to the server, where the value actually matters.</summary>
        internal static void ForwardToServerIfClient(string key, string raw)
        {
            if (ValheimBridge.IsServer()) return;
            if (!Settable().ContainsKey(key)) return;
            ValheimBridge.SendConfigSetToServer(key, raw);
        }

        /// <summary>
        /// Server-side landing for a client's forwarded fireset. Trusted-tester
        /// surface: the client command is admin-gated and the sender id is
        /// logged for audit; hard server-side admin validation is deliberate
        /// scope left for a public release.
        /// </summary>
        public static void ApplyRemote(long sender, string key, string raw)
        {
            key = key?.ToLowerInvariant();
            if (key == null || !Settable().TryGetValue(key, out BepInEx.Configuration.ConfigEntryBase entry))
            {
                FireLogger.Warn($"fireset (remote from {sender}): unknown key '{key}'.");
                return;
            }
            try
            {
                entry.SetSerializedValue(raw);
                FireLogger.Info($"fireset (remote from {sender}): {key} = {entry.BoxedValue}");
            }
            catch (System.Exception ex)
            {
                FireLogger.Warn($"fireset (remote from {sender}): couldn't parse '{raw}' for {key}: {ex.Message}");
            }
        }
    }
}