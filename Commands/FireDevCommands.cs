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
                "FireFront: fireset <burnduration|firematurity|spreadradius|maxburning|queuesize|spreadinterval|trees|burnbuildings|ashlands|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|douseimmunity|rainsuppress|rainmultiplier|rainobjects|rainobjectmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|lowspec|debug|burntheworld|smouldering|smoulderafter|treeflames|crownsparks|maxflameheight|tallfiremax|firewarmth|firewarmthradius|firesmoke|waterblocks|maxkills|fireshadows|heathaze|barkchar|treefire|treetick|treekillfraction|treedestruction|charreddelay|charredcoalmin|charredcoalmax|charredhealth|charredcrumble|charredglow|charredember|charredembercover|charredsmoke|charredsmokeseconds|enabled> <value>",
                args => FireSet(args));

            new Terminal.ConsoleCommand("firelistprefabs",
                "FireFront: firelistprefabs [filter] - list prefab names containing filter (default 'fire')",
                args => FireListPrefabs(args));

            new Terminal.ConsoleCommand("firepurgevfx",
                "FireFront: emergency cleanup - destroys every live vanilla Fire-class instance in the scene",
                args => FirePurgeVfx(args));

            new Terminal.ConsoleCommand("firedumptex",
                "FireFront: writes the charred-wood textures this client generated (albedo, normal, ember masks) as PNG under BepInEx/config/FireFront-textures/, so the look can be checked outside the game",
                args => FireDumpTextures(args));

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

            new Terminal.ConsoleCommand("fireweather",
                "FireFront: fireweather - the weather FireFront resolves at your position, beside what vanilla shows you (client), then the server's answer. " +
                "fireweather force <EnvName> | reset - override the SERVER's weather for testing; vanilla's own 'env' only reaches the client it is typed on",
                args => FireWeather(args));

            FireLogger.Info("Dev commands registered.");
        }

        // ---------------------------------------------------------------

        private static void Ignite(Terminal.ConsoleEventArgs args)
        {
            // Relayed form "ignite <zdoUserId> <zdoId>". ignite is the one gated
            // command that cannot be relayed as typed: it picks its target by
            // raycasting the crosshair, and a headless server has no crosshair.
            // So the CLIENT raycasts, then relays the resolved ZDOID here, where
            // ExecuteRelayed has already authorized the sender against the
            // server's own adminlist. That is the real, unspoofable check — the
            // typist's local admin flag is not trusted, and on a dedicated
            // server it is not even set for genuine admins: live 2026-09-19,
            // clearfires (relayed) worked while ignite answered "Admin only."
            // for a player who was in adminlist.txt in all three id forms.
            if (ValheimBridge.IsServer() && args.Length >= 3)
            {
                if (!InvariantNumbers.TryParseLong(args[1], out long zUser) || !InvariantNumbers.TryParseUInt(args[2], out uint zId))
                {
                    Say(args, $"Couldn't parse ZDOID: '{args[1]} {args[2]}'.");
                    return;
                }

                Component relayed = ValheimBridge.ComponentFromZdoid(new ZDOID(zUser, zId));
                if (relayed == null) { Say(args, "That target no longer exists on the server."); return; }
                if (!ValheimBridge.IsBurnable(relayed)) { Say(args, $"Not burnable: {ValheimBridge.NameOf(relayed)}"); return; }
                if (FireManager.AshlandsBarsFireAt(ValheimBridge.PositionOf(relayed))) { Say(args, AshlandsRefusal("ignite")); return; }

                FireManager.Instance.TryIgnite(relayed);
                Say(args, FireManager.Instance.IsBurning(relayed)
                    ? $"Ignited: {ValheimBridge.NameOf(relayed)}"
                    : $"Queued or dropped (cap full): {ValheimBridge.NameOf(relayed)}");
                return;
            }

            // Typed locally: only this machine has a crosshair, so the raycast
            // happens here whichever side we are on.
            Component target = ValheimBridge.RaycastBurnable();
            if (target == null) { Say(args, "No burnable target under crosshair."); return; }
            if (!ValheimBridge.IsBurnable(target)) { Say(args, $"Not burnable: {ValheimBridge.NameOf(target)}"); return; }

            if (ValheimBridge.IsServer())
            {
                if (!RequireAdmin(args)) return;
                if (FireManager.AshlandsBarsFireAt(ValheimBridge.PositionOf(target))) { Say(args, AshlandsRefusal("ignite")); return; }

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
                // a fire started that the server has no record of. Relay the
                // resolved target instead and let the server ignite it.
                ZDOID? id = ValheimBridge.ZDOIDOf(target);
                if (id.HasValue)
                {
                    Say(args, "FireFront: sent to server — replies appear as [server] lines. (ignite)");
                    ValheimBridge.SendCommandRelayToServer($"ignite {InvariantNumbers.Format(id.Value.UserID)} {InvariantNumbers.Format(id.Value.ID)}");
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
            if (FireManager.AshlandsBarsFireAt(pos)) { Say(args, AshlandsRefusal("startfire")); return; }

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

        // Said by the server that refused, so it names the server's setting.
        private static string AshlandsRefusal(string command) =>
            $"{command}: this spot is in the Ashlands, where FireFront starts no fire while " +
            "FireInAshlands is false. An admin can allow it with: fireset ashlands true";

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
            // This machine's own config migration, whichever side it is: the line the boot logged.
            Say(args, ConfigMigration.StatusLine());

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
            Say(args, "FireFront: fetching server status... a 0.21.5+ server answers with two [server] lines, its " +
                       "config migration then its fire status; an older one sends only the fire status, and nothing at " +
                       "all means the server runs pre-0.18.8.");
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
                Say(args, "Usage: fireset <burnduration|firematurity|spreadradius|maxburning|queuesize|spreadinterval|trees|burnbuildings|ashlands|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|douseimmunity|rainsuppress|rainmultiplier|rainobjects|rainobjectmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|lowspec|debug|burntheworld|smouldering|smoulderafter|treeflames|crownsparks|maxflameheight|tallfiremax|firewarmth|firewarmthradius|firesmoke|waterblocks|maxkills|fireshadows|heathaze|barkchar|treefire|treetick|treekillfraction|treedestruction|charreddelay|charredcoalmin|charredcoalmax|charredhealth|charredcrumble|charredglow|charredember|charredembercover|charredsmoke|charredsmokeseconds|enabled> <value>");
                return;
            }

            string key = args[1].ToLowerInvariant();
            string raw = args[2];

            // Suppress the SettingChanged hook for THIS key while the switch below writes it; the
            // explicit forward at the end of this method is the one that should reach the server.
            _firesetKeyInFlight = key;
            _firesetKeyInFlightAt = Time.realtimeSinceStartup;

            switch (key)
            {
                case "burnduration":
                    if (InvariantNumbers.TryParseFloat(raw, out float bd)) { FireConfig.BurnDurationSeconds.Value = bd; Ok(args, key, bd); }
                    else Bad(args, raw);
                    break;
                case "spreadradius":
                    if (InvariantNumbers.TryParseFloat(raw, out float sr)) { FireConfig.SpreadRadius.Value = sr; Ok(args, key, FireConfig.SpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "maxburning":
                    if (InvariantNumbers.TryParseInt(raw, out int mb)) { FireConfig.MaxConcurrentBurning.Value = mb; Ok(args, key, mb); }
                    else Bad(args, raw);
                    break;
                case "queuesize":
                    if (InvariantNumbers.TryParseInt(raw, out int qs)) { FireConfig.QueueSize.Value = qs; Ok(args, key, FireConfig.QueueSize.Value); }
                    else Bad(args, raw);
                    break;
                case "spreadinterval":
                    if (InvariantNumbers.TryParseFloat(raw, out float si)) { FireConfig.SpreadCheckInterval.Value = si; Ok(args, key, si); }
                    else Bad(args, raw);
                    break;
                case "smouldering":
                    if (bool.TryParse(raw, out bool smv)) { FireConfig.SmoulderingVfxEnabled.Value = smv; Ok(args, key, smv); }
                    else Bad(args, raw);
                    break;
                case "smoulderafter":
                    if (InvariantNumbers.TryParseFloat(raw, out float sma)) { FireConfig.SmoulderAfterFraction.Value = sma; Ok(args, key, FireConfig.SmoulderAfterFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "burntheworld":
                    if (bool.TryParse(raw, out bool btw))
                    {
                        FireConfig.WatchTheWorldBurn.Value = btw;
                        Say(args, $"burntheworld = {btw}{WhereSet()} (in force: {FireConfig.ApocalypseActive}). " +
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
                        Say(args, $"lowspec = {ls}{WhereSet()} (burning cap {FireConfig.EffectiveMaxConcurrentBurning}, " +
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
                case "treeflames":
                    if (bool.TryParse(raw, out bool tf)) { FireConfig.TreeFlameScaling.Value = tf; Ok(args, key, tf); }
                    else Bad(args, raw);
                    break;
                case "crownsparks":
                    if (bool.TryParse(raw, out bool cs)) { FireConfig.CrownSparksEnabled.Value = cs; Ok(args, key, cs); }
                    else Bad(args, raw);
                    break;
                case "maxflameheight":
                    if (InvariantNumbers.TryParseFloat(raw, out float mfh)) { FireConfig.MaxFlameHeight.Value = mfh; Ok(args, key, mfh); }
                    else Bad(args, raw);
                    break;
                case "tallfiremax":
                    if (InvariantNumbers.TryParseInt(raw, out int tfm)) { FireConfig.TallFireMaxConcurrent.Value = tfm; Ok(args, key, tfm); }
                    else Bad(args, raw);
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
                    if (InvariantNumbers.TryParseFloat(raw, out float gcs)) { FireConfig.GroundCellSize.Value = gcs; Ok(args, key, FireConfig.GroundCellSize.Value); }
                    else Bad(args, raw);
                    break;
                case "groundradius":
                    if (InvariantNumbers.TryParseFloat(raw, out float gr)) { FireConfig.GroundSpreadRadius.Value = gr; Ok(args, key, FireConfig.GroundSpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "groundburnduration":
                    if (InvariantNumbers.TryParseFloat(raw, out float gbd)) { FireConfig.GroundBurnDurationSeconds.Value = gbd; Ok(args, key, gbd); }
                    else Bad(args, raw);
                    break;
                case "groundmax":
                    if (InvariantNumbers.TryParseInt(raw, out int gm)) { FireConfig.GroundMaxConcurrent.Value = gm; Ok(args, key, gm); }
                    else Bad(args, raw);
                    break;
                case "groundvfxmax":
                    if (InvariantNumbers.TryParseInt(raw, out int gvm)) { FireConfig.GroundVfxMaxConcurrent.Value = gvm; Ok(args, key, gvm); }
                    else Bad(args, raw);
                    break;
                case "grounddamagemax":
                    if (InvariantNumbers.TryParseInt(raw, out int gdm)) { FireConfig.GroundDamageMaxConcurrent.Value = gdm; Ok(args, key, gdm); }
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
                    if (InvariantNumbers.TryParseFloat(raw, out float fhr)) { FireConfig.FireHurtsObjectRadius.Value = fhr; Ok(args, key, FireConfig.FireHurtsObjectRadius.Value); }
                    else Bad(args, raw);
                    break;

                case "firewarmth":
                    if (bool.TryParse(raw, out bool fw)) { FireConfig.FireKeepsYouWarm.Value = fw; Ok(args, key, fw); }
                    else Bad(args, raw);
                    break;

                case "firewarmthradius":
                    if (InvariantNumbers.TryParseFloat(raw, out float fwr)) { FireConfig.FireWarmthRadius.Value = fwr; Ok(args, key, FireConfig.FireWarmthRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "firedamage":
                    if (InvariantNumbers.TryParseFloat(raw, out float fd)) { FireConfig.FireDamagePerTick.Value = fd; Ok(args, key, FireConfig.FireDamagePerTick.Value); }
                    else Bad(args, raw);
                    break;
                case "firetickinterval":
                    if (InvariantNumbers.TryParseFloat(raw, out float fti)) { FireConfig.FireDamageTickInterval.Value = fti; Ok(args, key, FireConfig.FireDamageTickInterval.Value); }
                    else Bad(args, raw);
                    break;
                case "extinguishradius":
                    if (InvariantNumbers.TryParseFloat(raw, out float exr)) { FireConfig.ExtinguishGroundRadius.Value = exr; Ok(args, key, FireConfig.ExtinguishGroundRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "rainsuppress":
                    if (bool.TryParse(raw, out bool rs)) { FireConfig.RainSuppressesGroundFire.Value = rs; Ok(args, key, rs); }
                    else Bad(args, raw);
                    break;
                case "rainmultiplier":
                    if (InvariantNumbers.TryParseFloat(raw, out float rm)) { FireConfig.RainGroundBurnDurationMultiplier.Value = rm; Ok(args, key, FireConfig.RainGroundBurnDurationMultiplier.Value); }
                    else Bad(args, raw);
                    break;
                case "rainobjects":
                    if (bool.TryParse(raw, out bool ro)) { FireConfig.RainSuppressesObjectFire.Value = ro; Ok(args, key, ro); }
                    else Bad(args, raw);
                    break;
                case "rainobjectmultiplier":
                    if (InvariantNumbers.TryParseFloat(raw, out float rom)) { FireConfig.RainObjectBurnDurationMultiplier.Value = rom; Ok(args, key, FireConfig.RainObjectBurnDurationMultiplier.Value); }
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
                    if (InvariantNumbers.TryParseFloat(raw, out float dpr)) { FireConfig.DirtPaintRadius.Value = dpr; Ok(args, key, FireConfig.DirtPaintRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "scorchlifetime":
                    if (InvariantNumbers.TryParseFloat(raw, out float sl)) { FireConfig.ScorchMarkLifetimeSeconds.Value = sl; Ok(args, key, FireConfig.ScorchMarkLifetimeSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampenabled":
                    if (bool.TryParse(raw, out bool re)) { FireConfig.FireRampEnabled.Value = re; Ok(args, key, re); }
                    else Bad(args, raw);
                    break;
                case "rampduration":
                    if (InvariantNumbers.TryParseFloat(raw, out float rd)) { FireConfig.FireRampDurationSeconds.Value = rd; Ok(args, key, FireConfig.FireRampDurationSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampstart":
                    if (InvariantNumbers.TryParseFloat(raw, out float rst)) { FireConfig.FireRampStartFraction.Value = rst; Ok(args, key, FireConfig.FireRampStartFraction.Value); }
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
                    if (InvariantNumbers.TryParseFloat(raw, out float fregrow)) { FireConfig.GroundFuelRegrowSeconds.Value = fregrow; Ok(args, key, FireConfig.GroundFuelRegrowSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "windbias":
                    if (bool.TryParse(raw, out bool wb)) { FireConfig.WindSpreadBiasEnabled.Value = wb; Ok(args, key, wb); }
                    else Bad(args, raw);
                    break;
                case "windupwindchance":
                    if (InvariantNumbers.TryParseFloat(raw, out float wuc)) { FireConfig.WindUpwindIgniteChance.Value = wuc; Ok(args, key, FireConfig.WindUpwindIgniteChance.Value); }
                    else Bad(args, raw);
                    break;
                case "windinfluence":
                    if (InvariantNumbers.TryParseFloat(raw, out float wi)) { FireConfig.WindInfluence.Value = wi; Ok(args, key, FireConfig.WindInfluence.Value); }
                    else Bad(args, raw);
                    break;
                case "dousingradius":
                    if (InvariantNumbers.TryParseFloat(raw, out float dbr)) { FireConfig.DousingBombRadius.Value = dbr; Ok(args, key, FireConfig.DousingBombRadius.Value); }
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
                case "ashlands":
                    if (bool.TryParse(raw, out bool fa)) { FireConfig.FireInAshlands.Value = fa; Ok(args, key, fa); }
                    else Bad(args, raw);
                    break;
                case "douseimmunity":
                    if (InvariantNumbers.TryParseFloat(raw, out float di)) { FireConfig.DouseImmunitySeconds.Value = di; Ok(args, key, FireConfig.DouseImmunitySeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "firematurity":
                    if (InvariantNumbers.TryParseFloat(raw, out float fm)) { FireConfig.SpreadMaturityFraction.Value = fm; Ok(args, key, FireConfig.SpreadMaturityFraction.Value); }
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
                    if (InvariantNumbers.TryParseFloat(raw, out float trs)) { FireConfig.TreeRegrowthSeconds.Value = trs; Ok(args, key, FireConfig.TreeRegrowthSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "groundleashenabled":
                    if (bool.TryParse(raw, out bool gle)) { FireConfig.GroundMaxSpreadDistanceEnabled.Value = gle; Ok(args, key, gle); }
                    else Bad(args, raw);
                    break;
                case "groundleashdistance":
                    if (InvariantNumbers.TryParseFloat(raw, out float gld)) { FireConfig.GroundMaxSpreadDistance.Value = gld; Ok(args, key, FireConfig.GroundMaxSpreadDistance.Value); }
                    else Bad(args, raw);
                    break;
                case "fireshadows":
                    if (bool.TryParse(raw, out bool fsh)) { FireConfig.FireShadowsEnabled.Value = fsh; Ok(args, key, fsh); }
                    else Bad(args, raw);
                    break;
                case "heathaze":
                    if (bool.TryParse(raw, out bool hhz)) { FireConfig.HeatHazeEnabled.Value = hhz; Ok(args, key, hhz); }
                    else Bad(args, raw);
                    break;
                case "barkchar":
                    if (bool.TryParse(raw, out bool bch)) { FireConfig.BarkCharEnabled.Value = bch; Ok(args, key, bch); }
                    else Bad(args, raw);
                    break;
                case "treefire":
                    if (bool.TryParse(raw, out bool tfd)) { FireConfig.TreeFireDamageEnabled.Value = tfd; Ok(args, key, tfd); }
                    else Bad(args, raw);
                    break;
                case "treetick":
                    if (InvariantNumbers.TryParseFloat(raw, out float ttk)) { FireConfig.TreeFireTickInterval.Value = ttk; Ok(args, key, FireConfig.TreeFireTickInterval.Value); }
                    else Bad(args, raw);
                    break;
                case "treekillfraction":
                    if (InvariantNumbers.TryParseFloat(raw, out float tkf)) { FireConfig.TreeFireKillFraction.Value = tkf; Ok(args, key, FireConfig.TreeFireKillFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "treedestruction":
                    if (InvariantNumbers.TryParseFloat(raw, out float tdr)) { FireConfig.TreeDestructionRate.Value = tdr; Ok(args, key, FireConfig.TreeDestructionRate.Value); }
                    else Bad(args, raw);
                    break;
                case "charreddelay":
                    if (InvariantNumbers.TryParseFloat(raw, out float cdl)) { FireConfig.CharredCollapseDelaySeconds.Value = cdl; Ok(args, key, FireConfig.CharredCollapseDelaySeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "charredcoalmin":
                    if (InvariantNumbers.TryParseInt(raw, out int ccn)) { FireConfig.CharredCoalMin.Value = ccn; Ok(args, key, FireConfig.CharredCoalMin.Value); }
                    else Bad(args, raw);
                    break;
                case "charredcoalmax":
                    if (InvariantNumbers.TryParseInt(raw, out int ccx)) { FireConfig.CharredCoalMax.Value = ccx; Ok(args, key, FireConfig.CharredCoalMax.Value); }
                    else Bad(args, raw);
                    break;
                case "charredhealth":
                    if (InvariantNumbers.TryParseFloat(raw, out float chf)) { FireConfig.CharredTreeHealthFraction.Value = chf; Ok(args, key, FireConfig.CharredTreeHealthFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "charredcrumble":
                    if (InvariantNumbers.TryParseFloat(raw, out float ccr)) { FireConfig.CharredLogCrumbleSeconds.Value = ccr; Ok(args, key, FireConfig.CharredLogCrumbleSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "charredglow":
                    if (InvariantNumbers.TryParseFloat(raw, out float cgl)) { FireConfig.CharredEmberGlowSeconds.Value = cgl; Ok(args, key, FireConfig.CharredEmberGlowSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "charredember":
                    if (InvariantNumbers.TryParseFloat(raw, out float cei)) { FireConfig.CharredEmberIntensity.Value = cei; Ok(args, key, FireConfig.CharredEmberIntensity.Value); }
                    else Bad(args, raw);
                    break;
                case "charredembercover":
                    if (InvariantNumbers.TryParseFloat(raw, out float cec)) { FireConfig.CharredEmberCoverage.Value = cec; Ok(args, key, FireConfig.CharredEmberCoverage.Value); }
                    else Bad(args, raw);
                    break;
                case "charredsmoke":
                    if (bool.TryParse(raw, out bool csm)) { FireConfig.CharredSmokeEnabled.Value = csm; Ok(args, key, FireConfig.CharredSmokeEnabled.Value); }
                    else Bad(args, raw);
                    break;
                case "charredsmokeseconds":
                    if (InvariantNumbers.TryParseFloat(raw, out float css)) { FireConfig.CharredSmokeSeconds.Value = css; Ok(args, key, FireConfig.CharredSmokeSeconds.Value); }
                    else Bad(args, raw);
                    break;
                // These three are read by the simulation but were reachable from no route at all,
                // which FireConfig's own "every value is live-settable" doc comment promised was
                // impossible. maxkills echoes the read-back value, not the token: its bind clamps
                // to 1..50, so the raw number would report a value the entry does not hold.
                case "waterblocks":
                    if (bool.TryParse(raw, out bool wbs)) { FireConfig.GroundWaterBlocksSpreadEnabled.Value = wbs; Ok(args, key, wbs); }
                    else Bad(args, raw);
                    break;
                case "maxkills":
                    if (InvariantNumbers.TryParseInt(raw, out int mkc)) { FireConfig.MaxKillsPerCycle.Value = mkc; Ok(args, key, FireConfig.MaxKillsPerCycle.Value); }
                    else Bad(args, raw);
                    break;
                case "firesmoke":
                    if (bool.TryParse(raw, out bool fsm)) { FireConfig.FireSmokeEnabled.Value = fsm; Ok(args, key, fsm); }
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
            _firesetKeyInFlight = null;
            ForwardToServerIfClient(key, raw);

            // 0.23: the server can refuse now, so the line above is about THIS machine only and
            // the server's own answer arrives separately as a [server] line (ApplyRemote replies
            // to every forwarded fireset, accepted or not). Found in play on the rig 2026-09-23:
            // a refused non-admin read "burntheworld = True (in force: True)" and took it for
            // the server's state.
            if (ValheimBridge.CanReachServer() && Settable().ContainsKey(key))
                Say(args, $"fireset {key}: sent to the server; its answer follows as a [server] line. Only an admin on the server's adminlist changes the server's copy.");
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

        /// <summary>
        /// Local-only (the textures live on this client). Forces the ember masks to exist first
        /// so a dump on a fresh client still shows something; the per-species albedo/normal
        /// only exist once a charred tree of that species has been drawn.
        /// </summary>
        private static void FireDumpTextures(Terminal.ConsoleEventArgs args)
        {
            if (!FireVFXController.GraphicsAvailable)
            {
                Say(args, "No graphics device here (headless); nothing is generated on this side.");
                return;
            }
            try
            {
                CharredTextures.EmberMask(0);
                string dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "FireFront-textures");
                int n = CharredTextures.DumpAll(dir);
                Say(args, $"Wrote {n} texture(s) to {dir}. Char a tree of each species first to get its albedo/normal.");
            }
            catch (System.Exception ex)
            {
                Say(args, $"Dump failed: {ex.Message}");
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

            if (FireManager.AshlandsBarsFireAt(posOrNull.Value)) { Say(args, AshlandsRefusal("firegroundignite")); return; }

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

            if (!FireConfig.EffectiveTreeRegrowthEnabled)
            {
                Say(args, "firetreeregrow: tree regrowth is switched off, so nothing was attempted. " +
                          "The queue is untouched and resumes if you turn TreeRegrowthEnabled back on.");
                return;
            }

            (int attempted, int regrown, int stillPending) = FireManager.Instance.ForceTreeRegrowthNow();
            int dropped = attempted - regrown - stillPending;
            Say(args, $"firetreeregrow: forced {attempted} pending entries — {regrown} tree(s) grew, {dropped} dropped " +
                       $"(built over, or out of spawn attempts), {stillPending} still pending. Entries sitting in live " +
                       "fire are deferred, not spent, so forcing costs them nothing.");
        }

        private static void FireTreeRegrowList(Terminal.ConsoleEventArgs args)
        {
            if (RelayIfClient(args)) return;

            var lines = FireManager.Instance.DumpPendingRegrowth();
            if (lines.Count == 0) { Say(args, "No pending tree regrowth entries."); return; }

            Say(args, $"{lines.Count} pending regrowth entries:");
            foreach (string line in lines) Say(args, "  " + line);
        }

        /// <summary>
        /// Weather as FireFront resolves it at the requester's position. Typed on
        /// a client it prints the client's own replay AND vanilla's live state side
        /// by side - they must agree; that is the whole check - then relays so the
        /// server prints what IT resolves for the same spot, which is what the
        /// simulation actually uses.
        /// </summary>
        private static void FireWeather(Terminal.ConsoleEventArgs args)
        {
            string verb = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
            if (verb == "force" || verb == "reset")
            {
                // Server-side only: the whole point is to reach the machine the
                // simulation runs on. Vanilla's 'env' already covers the client.
                if (RelayIfClient(args)) return;
                string name = verb == "reset" ? "" : (args.Args.Length > 2 ? args.Args[2] : "");
                if (verb == "force" && string.IsNullOrEmpty(name)) { Say(args, "Usage: fireweather force <EnvName> | fireweather reset"); return; }
                Say(args, "FireFront [server] " + ValheimBridge.SetDebugEnvironment(name));
                return;
            }

            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (!posOrNull.HasValue) { Say(args, "FireFront: no player position available."); return; }
            Vector3 pos = posOrNull.Value;

            EnvSetup env = ValheimBridge.ResolveEnvironmentAt(pos, out string source);
            string where = ValheimBridge.IsServer() ? "server" : "client";
            Say(args, $"FireFront [{where}] weather at ({pos.x:F0}, {pos.z:F0}): " +
                      (env == null ? "unresolved" : $"'{env.m_name}' wet={env.m_isWet}") +
                      $" via {source}; IsRainingAt={ValheimBridge.IsRainingAt(pos)}");
            if (!ValheimBridge.IsServer())
                Say(args, $"FireFront [client] vanilla shows you: {ValheimBridge.VanillaWeatherForStatus()} - the two lines above should agree.");

            RelayIfClient(args);
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
                { "fireweather", FireWeather },
                { "ignite", Ignite },
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
            Say(args, $"fireset {key} = {val}{WhereSet()}");

        /// <summary>
        /// " on this machine" on a client connected to a server, where a typed fireset writes the
        /// local copy and forwards the rest to a server that may refuse it; empty on the server,
        /// on a hosting player and at the main menu, where the local copy is the only one.
        /// </summary>
        private static string WhereSet() => ValheimBridge.CanReachServer() ? " on this machine" : "";

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
                { "ashlands", FireConfig.FireInAshlands },
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
                { "firewarmth", FireConfig.FireKeepsYouWarm },
                { "firewarmthradius", FireConfig.FireWarmthRadius },
                { "firedamage", FireConfig.FireDamagePerTick },
                { "firetickinterval", FireConfig.FireDamageTickInterval },
                { "extinguishradius", FireConfig.ExtinguishGroundRadius },
                { "douseimmunity", FireConfig.DouseImmunitySeconds },
                { "rainsuppress", FireConfig.RainSuppressesGroundFire },
                { "rainmultiplier", FireConfig.RainGroundBurnDurationMultiplier },
                { "rainobjects", FireConfig.RainSuppressesObjectFire },
                { "rainobjectmultiplier", FireConfig.RainObjectBurnDurationMultiplier },
                { "treeflames", FireConfig.TreeFlameScaling },
                { "crownsparks", FireConfig.CrownSparksEnabled },
                { "maxflameheight", FireConfig.MaxFlameHeight },
                { "tallfiremax", FireConfig.TallFireMaxConcurrent },
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
                { "fireshadows", FireConfig.FireShadowsEnabled },
                { "heathaze", FireConfig.HeatHazeEnabled },
                { "barkchar", FireConfig.BarkCharEnabled },
                { "treefire", FireConfig.TreeFireDamageEnabled },
                { "treetick", FireConfig.TreeFireTickInterval },
                { "treekillfraction", FireConfig.TreeFireKillFraction },
                { "treedestruction", FireConfig.TreeDestructionRate },
                { "charreddelay", FireConfig.CharredCollapseDelaySeconds },
                { "charredcoalmin", FireConfig.CharredCoalMin },
                { "charredcoalmax", FireConfig.CharredCoalMax },
                { "charredhealth", FireConfig.CharredTreeHealthFraction },
                { "charredcrumble", FireConfig.CharredLogCrumbleSeconds },
                { "charredglow", FireConfig.CharredEmberGlowSeconds },
                { "charredember", FireConfig.CharredEmberIntensity },
                { "charredembercover", FireConfig.CharredEmberCoverage },
                { "charredsmoke", FireConfig.CharredSmokeEnabled },
                { "charredsmokeseconds", FireConfig.CharredSmokeSeconds },
                { "waterblocks", FireConfig.GroundWaterBlocksSpreadEnabled },
                { "maxkills", FireConfig.MaxKillsPerCycle },
                { "firesmoke", FireConfig.FireSmokeEnabled },
                { "enabled", FireConfig.Enabled },
            };
            return _settable;
        }

        /// <summary>
        /// A numeric entry's value in the form BepInEx's invariant parser reads as meant; any other
        /// entry's value untouched (a prefab name may legitimately hold a comma).
        /// </summary>
        private static string NormalizeForEntry(BepInEx.Configuration.ConfigEntryBase entry, string raw)
        {
            System.Type t = entry?.SettingType;
            if (t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(long))
                return InvariantNumbers.Normalize(raw);
            return raw;
        }

        /// <summary>Forward a locally-typed fireset to the server, where the value actually matters.</summary>
        internal static void ForwardToServerIfClient(string key, string raw)
        {
            if (ValheimBridge.IsServer()) return;
            if (!Settable().TryGetValue(key, out BepInEx.Configuration.ConfigEntryBase target)) return;

            // 0.24: the server applies this with BepInEx's own parser, which is invariant, so a
            // decimal comma typed on a comma-decimal machine ("1,5") would land there as 15.
            // Sent in the invariant form instead; InvariantNumbers has the rule.
            raw = NormalizeForEntry(target, raw);

            // NEVER skipped as a duplicate. An earlier draft cached the last value sent per key and
            // dropped a repeat, which looked harmless and was not: the cache recorded what THIS
            // client had sent, not what the server held, so once the two diverged - a second admin,
            // a server restart under a still-connected client, a send that silently failed - the
            // client could never re-assert that key again, while the console still printed Ok.
            // The double-send it was meant to stop is prevented at the source instead, by the
            // in-flight key below.
            ValheimBridge.SendConfigSetToServer(key, raw);
        }

        // ---------------------------------------------------------------
        // Live config sync. ConfigurationManager (and anything else that writes a ConfigEntry at
        // runtime) edits only the machine it runs on. On a client that means every simulation
        // setting in its UI was a no-op: the slider moved, the client's own file was rewritten,
        // and the server - the only machine whose value the fire actually reads - never heard.
        // Exactly the trap the _settable comment above describes for fireset, with no console
        // line to hint at it. A change to a server-side setting is now forwarded as if the admin
        // had typed the equivalent fireset.
        // ---------------------------------------------------------------
        // The one key a `fireset` is in the middle of applying. Writing an entry there fires
        // SettingChanged, and FireSet forwards explicitly afterwards, so without this a typed
        // fireset would reach the server twice, with two different strings - the hook sends the
        // canonical serialized value, the command sends the raw token. The command's send wins.
        //
        // Scoped to ONE key and ONE second rather than a bool held across the whole command,
        // because BepInEx calls ConfigFile.Save() before it calls the handlers and does NOT wrap
        // it: a config file momentarily locked by an editor, a cloud sync or antivirus throws
        // straight out of the setter, past any "clear the flag" line after it. A global flag would
        // stay set and silently kill this feature for the rest of the session, and the next
        // fireset would hit the same lock and not heal it. This expires on its own.
        private static string _firesetKeyInFlight;
        private static float _firesetKeyInFlightAt;

        // Every runtime change waits here briefly before it is sent, keyed by setting so the last
        // write wins. Two jobs in one queue: it holds changes made with no server to send them to
        // (the config manager at the main menu, which is where people actually use it), and it
        // debounces a value being typed or dragged.
        //
        // The debounce is not politeness. ConfigurationManager raises a change per keystroke and
        // per drag frame: typing "120" sent 1, then 12, then 120, each a routed RPC that a live
        // server applied to a running simulation - so for a moment every fire on it burned out in
        // one second. A slider would do that tens of times a second.
        private static readonly System.Collections.Generic.Dictionary<string, string> _pendingSync =
            new System.Collections.Generic.Dictionary<string, string>();

        // Wait for the value to settle, but never sit on a change for longer than the ceiling -
        // a slider held down should still take effect while the admin is watching it.
        private const float SyncQuietSeconds = 0.4f;
        private const float SyncMaxHoldSeconds = 2f;
        private static float _syncQuietUntil;
        private static float _syncHoldingSince;
        private static System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase, string> _keyByEntry;

        /// <summary>
        /// Subscribe to the plugin's own config file so runtime edits reach the server. Called
        /// once from Plugin.Awake, after Bind, so the migration's own writes cannot trip it.
        /// </summary>
        public static void HookLiveConfigSync(BepInEx.Configuration.ConfigFile config)
        {
            if (config == null) return;
            config.SettingChanged += (_, e) => OnSettingChanged(e?.ChangedSetting);
        }

        /// <summary>
        /// Delivers anything held by <see cref="OnSettingChanged"/> once the server is actually
        /// reachable. Cheap enough to call every frame: it is a count check.
        ///
        /// It waits on CanReachServer, not on ZNet.instance. ZNet exists from the moment the world
        /// scene loads, several seconds before the handshake finishes, so a flush gated on "ZNet is
        /// up" ran while nothing could be delivered - and an earlier draft then concluded the
        /// player was not an admin (that list arrives at the END of the handshake) and threw the
        /// held changes away. Every single time, for the one workflow this feature exists to serve.
        /// </summary>
        public static void FlushPendingConfigSync()
        {
            if (_pendingSync.Count == 0) return;
            if (ValheimBridge.IsServer()) { _pendingSync.Clear(); return; }
            if (!ValheimBridge.CanReachServer()) return;

            // Send once the value has stopped moving, or once the ceiling is reached, whichever
            // comes first. A change made before joining is long past both by the time a connection
            // exists, so it goes out on the first frame that can carry it.
            float now = Time.realtimeSinceStartup;
            if (now < _syncQuietUntil && now - _syncHoldingSince < SyncMaxHoldSeconds) return;

            foreach (System.Collections.Generic.KeyValuePair<string, string> kv in _pendingSync)
            {
                ForwardToServerIfClient(kv.Key, kv.Value);
                FireLogger.Info($"[CONFIG-SYNC] {kv.Key} = {kv.Value} — sent to the server.");
            }
            _pendingSync.Clear();
            _syncHoldingSince = 0f;
        }

        private static void OnSettingChanged(BepInEx.Configuration.ConfigEntryBase entry)
        {
            // The server is the authority: it already has the value, and bouncing it back would
            // return it to the machine that just set it.
            if (entry == null || ValheimBridge.IsServer()) return;

            if (_keyByEntry == null)
            {
                _keyByEntry = new System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase, string>();
                foreach (System.Collections.Generic.KeyValuePair<string, BepInEx.Configuration.ConfigEntryBase> kv in Settable())
                    _keyByEntry[kv.Value] = kv.Key;
            }
            // Not in the table means the setting is genuinely client-side (extinguish key, dousing
            // radius, VFX budgets). Those stay local, which is correct, not a gap.
            if (!_keyByEntry.TryGetValue(entry, out string key)) return;

            // A `fireset` writing this very key is about to forward it itself, with the token the
            // user typed. Don't send it twice.
            if (key == _firesetKeyInFlight && Time.realtimeSinceStartup - _firesetKeyInFlightAt < 1f) return;

            string raw = entry.GetSerializedValue();

            // NO client-side admin gate here, on purpose, and it is worth stating why because an
            // earlier draft had one and it made the whole feature a silent no-op. `fireset` - the
            // typed route to the exact same server-side setter - has never had one either. Adding
            // it to only one of the two routes buys nothing: anyone who could abuse the manager
            // could type the command instead. Worse, ZNet.LocalPlayerIsAdminOrHost answers false
            // whenever the server has no adminlist at all, which is every private test server, so
            // the gate blocked the server's owner on their own machine with no message anywhere.
            //
            // The real check belongs on the SERVER, in ApplyRemote, against its own adminlist -
            // the unspoofable one, as the relayed commands already note. That is still the
            // deliberate scope left for a public release, and it now covers two routes, not one.
            // Queue, never send from here - see _pendingSync. A value being typed or dragged
            // arrives as a burst of changes, and only the one it settles on is worth sending.
            // FlushPendingConfigSync, called every frame from FireManager.Update, does the rest.
            if (_pendingSync.Count == 0) _syncHoldingSince = Time.realtimeSinceStartup;
            _pendingSync[key] = raw;
            _syncQuietUntil = Time.realtimeSinceStartup + SyncQuietSeconds;
        }

        /// <summary>
        /// Server-side landing for a client's forwarded fireset, typed or moved from a config
        /// manager: both routes end here. Since 0.23 the sender is checked against the server's
        /// own adminlist, the same vanilla check the relayed commands use (ExecuteRelayed), and
        /// nothing is applied for a peer that is not on it. The typist's local admin state is
        /// never consulted: it is false on every server without an adminlist file, which is how
        /// 0.21.6's client-side gate made the sync a silent no-op (docs/HANDOFF.md, 0.21.7).
        /// </summary>
        public static void ApplyRemote(long sender, string key, string raw)
        {
            if (!ValheimBridge.IsServer()) return;
            if (!ValheimBridge.PeerIsAdmin(sender))
            {
                ValheimBridge.SendStatusResponse(sender, $"FireFront: fireset {key} refused — you are not in the server's adminlist. The server's value is unchanged; the copy on your machine keeps what you set.");
                AuthLog.Refused(sender, "fireset", $"refused fireset '{key} = {raw}' from a peer not in the adminlist");
                return;
            }

            key = key?.ToLowerInvariant();
            if (key == null || !Settable().TryGetValue(key, out BepInEx.Configuration.ConfigEntryBase entry))
            {
                FireLogger.Warn($"fireset (remote from {sender}): unknown key '{key}'.");
                ValheimBridge.SendStatusResponse(sender, $"FireFront: fireset {key}: the server has no such key (a different FireFront version?).");
                return;
            }
            try
            {
                // 0.24: the same rewrite the sender now applies, for a sender older than 0.24.
                raw = NormalizeForEntry(entry, raw);
                entry.SetSerializedValue(raw);
                FireLogger.Info($"fireset (remote from {sender}): {key} = {entry.BoxedValue}");
                ValheimBridge.SendStatusResponse(sender, $"FireFront: fireset {key} = {entry.BoxedValue} on the server.");
            }
            catch (System.Exception ex)
            {
                FireLogger.Warn($"fireset (remote from {sender}): couldn't parse '{raw}' for {key}: {ex.Message}");
                ValheimBridge.SendStatusResponse(sender, $"FireFront: fireset {key}: the server couldn't read '{raw}'; its value is unchanged.");
            }
        }
    }
}