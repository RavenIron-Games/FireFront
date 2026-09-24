# FireFront config key map

Which side reads each of the 82 keys in `com.raveniron.firefront.cfg`, how the console reaches it, and which preset can silently override it.

Produced 2026-09-20 against the branch at `c4ab08c` (0.22.1 plus that day's fixes, before the merge with `main` 0.21.16, which adds no keys). Six Sonnet agents classified the keys from the code; six Opus agents re-derived every classification independently and corrected four. Line numbers in the citations at the bottom are from that commit and will drift; the side and the override do not drift unless the code does. `FireInAshlands` was added in 1.0.1 and entered by hand from the code.

## How to read it

Every BepInEx install writes all 82 keys to its own file, client and server alike, but most keys are read on one side only:

- **server** (44 keys): read by the simulation, i.e. the dedicated server, or the host of a hosted game. The client's copy does nothing.
- **client** (17 keys): read by the machine drawing the fire. The server's copy does nothing.
- **both** (21 keys): each side reads its own copy for a different purpose. Notes below the table say which.

`fireset <token> <value>` sets the local entry and forwards the same token to the server, so typing it on a client sets both copies. Every key has a token except the extinguish keybind and the migration stamp. Sync runs client to server only; nothing ever pushes the server's values to a client, so a key marked *both* whose value differs between the two files will behave differently on each side (burn duration is the clearest case: the server ends the fire on its copy, every client times the smoulder and the mirrored flames on its own).

Two presets override keys that still read true in the file: `LowSpecPreset` (LowSpec) and `WatchTheWorldBurn` (Apocalypse). When both are on, LowSpec wins. The override is applied through the `Effective*` accessors in `Config/FireConfig.cs`; the raw key keeps its value and the file does not say it is being overridden. The exact effect per key is listed after the table.

## The map

| Section | Key | Read by | fireset | Preset | Dead copy |
|---|---|---|---|---|---|
| General | `Enabled` | server | `enabled` |  | client copy is dead |
| General | `LowSpecPreset` | both | `lowspec` |  |  |
| General | `MaxKillsPerCycle` | server | `maxkills` | LowSpec + Apocalypse | client copy is dead |
| General | `PersistFiresEnabled` | server | `persistfires` |  | client copy is dead |
| General | `WatchTheWorldBurn` | both | `burntheworld` | LowSpec + Apocalypse |  |
| Controls | `DouseImmunitySeconds` | server | `douseimmunity` | LowSpec + Apocalypse | client copy is dead |
| Controls | `ExtinguishGroundRadius` | both | `extinguishradius` |  | server copy is the ceiling for client requests (0.23) |
| Controls | `ExtinguishKey` | client |  |  | server copy is dead |
| Fire | `BurnDurationSeconds` | both | `burnduration` |  |  |
| Fire | `BurnPlayerBuildings` | both | `burnbuildings` |  |  |
| Fire | `BurnTreesAndLogs` | both | `trees` |  |  |
| Fire | `FireInAshlands` | server | `ashlands` |  | client copy is dead |
| Fire | `FireKeepsYouWarm` | both | `firewarmth` |  |  |
| Fire | `FireRampDurationSeconds` | server | `rampduration` |  | client copy is dead |
| Fire | `FireRampEnabled` | server | `rampenabled` | LowSpec + Apocalypse | client copy is dead |
| Fire | `FireRampStartFraction` | server | `rampstart` |  | client copy is dead |
| Fire | `FireWarmthRadius` | both | `firewarmthradius` |  |  |
| Fire | `MaxConcurrentBurning` | server | `maxburning` | LowSpec + Apocalypse | client copy is dead |
| Fire | `QueueSize` | server | `queuesize` |  | client copy is dead |
| Fire | `SpreadCheckInterval` | server | `spreadinterval` | LowSpec + Apocalypse | client copy is dead |
| Fire | `SpreadMaturityFraction` | server | `firematurity` | Apocalypse | client copy is dead |
| Fire | `SpreadRadius` | server | `spreadradius` | Apocalypse | client copy is dead |
| Fire | `TreeDestructionRate` | server | `treedestruction` |  | client copy is dead |
| Ground | `GroundBurnDurationSeconds` | server | `groundburnduration` |  | client copy is dead |
| Ground | `GroundCellSize` | both | `groundcellsize` |  |  |
| Ground | `GroundFirebreaksEnabled` | server | `firebreaks` | LowSpec + Apocalypse | client copy is dead |
| Ground | `GroundFuelExhaustionEnabled` | server | `exhaustionenabled` | LowSpec + Apocalypse | client copy is dead |
| Ground | `GroundFuelRegrowSeconds` | server | `fuelregrow` |  | client copy is dead |
| Ground | `GroundMaxConcurrent` | server | `groundmax` | LowSpec + Apocalypse | client copy is dead |
| Ground | `GroundMaxSpreadDistance` | server | `groundleashdistance` | Apocalypse | client copy is dead |
| Ground | `GroundMaxSpreadDistanceEnabled` | server | `groundleashenabled` | LowSpec + Apocalypse | client copy is dead |
| Ground | `GroundSpreadEnabled` | server | `groundenabled` |  | client copy is dead |
| Ground | `GroundSpreadRadius` | server | `groundradius` | LowSpec + Apocalypse | client copy is dead |
| Ground | `GroundVfxMaxConcurrent` | both | `groundvfxmax` | LowSpec + Apocalypse | server copy is dead |
| Ground | `GroundWaterBlocksSpreadEnabled` | server | `waterblocks` | LowSpec + Apocalypse | client copy is dead |
| Ground | `WindInfluence` | server | `windinfluence` |  | client copy is dead |
| Ground | `WindSpreadBiasEnabled` | server | `windbias` |  | client copy is dead |
| Ground | `WindUpwindIgniteChance` | server | `windupwindchance` |  | client copy is dead |
| Trees | `CharredCoalMax` | client | `charredcoalmax` |  | server copy is dead |
| Trees | `CharredCoalMin` | client | `charredcoalmin` |  | server copy is dead |
| Trees | `CharredCollapseDelaySeconds` | server | `charreddelay` |  | client copy is dead |
| Trees | `CharredEmberCoverage` | client | `charredembercover` |  | server copy is dead |
| Trees | `CharredEmberGlowSeconds` | client | `charredglow` |  | server copy is dead |
| Trees | `CharredEmberIntensity` | client | `charredember` |  | server copy is dead |
| Trees | `CharredLogCrumbleSeconds` | client | `charredcrumble` |  | server copy is dead |
| Trees | `CharredSmokeEnabled` | client | `charredsmoke` | LowSpec | server copy is dead |
| Trees | `CharredSmokeSeconds` | client | `charredsmokeseconds` |  | server copy is dead |
| Trees | `CharredTreeHealthFraction` | both | `charredhealth` |  |  |
| Trees | `TreeFireDamageEnabled` | both | `treefire` |  |  |
| Trees | `TreeFireKillFraction` | server | `treekillfraction` |  | client copy is dead |
| Trees | `TreeFireTickInterval` | server | `treetick` |  | client copy is dead |
| Trees | `TreeRegrowthEnabled` | server | `treeregrowth` | LowSpec + Apocalypse | client copy is dead |
| Trees | `TreeRegrowthSeconds` | server | `treeregrowthseconds` |  | client copy is dead |
| Visuals | `BarkCharEnabled` | client | `barkchar` |  | server copy is dead |
| Visuals | `CrownSparksEnabled` | client | `crownsparks` | LowSpec | server copy is dead |
| Visuals | `DirtPaintRadius` | server | `dirtpaintradius` |  | client copy is dead |
| Visuals | `FireShadowsEnabled` | client | `fireshadows` | LowSpec | server copy is dead |
| Visuals | `FireSmokeEnabled` | client | `firesmoke` |  | server copy is dead |
| Visuals | `HeatHazeEnabled` | client | `heathaze` | LowSpec | server copy is dead |
| Visuals | `MaxFlameHeight` | client | `maxflameheight` | LowSpec | server copy is dead |
| Visuals | `ScorchMarkLifetimeSeconds` | both | `scorchlifetime` |  |  |
| Visuals | `ScorchMarksEnabled` | both | `scorchmarks` | LowSpec |  |
| Visuals | `SmoulderAfterFraction` | both | `smoulderafter` |  | server copy is dead |
| Visuals | `SmoulderingVfxEnabled` | both | `smouldering` |  | server copy is dead |
| Visuals | `TallFireMaxConcurrent` | client | `tallfiremax` | LowSpec | server copy is dead |
| Visuals | `TreeFlameScaling` | client | `treeflames` |  | server copy is dead |
| Visuals | `UseProceduralVfx` | both | `procedural` |  |  |
| Visuals | `UseVanillaDirtPaint` | server | `dirtpaint` |  | client copy is dead |
| Visuals | `VfxPrefabName` | both | `vfx` |  |  |
| Damage | `FireDamagePerTick` | server | `firedamage` |  | client copy is dead |
| Damage | `FireDamageTickInterval` | server | `firetickinterval` |  | client copy is dead |
| Damage | `FireHurtsEnabled` | server | `firehurts` |  | client copy is dead |
| Damage | `FireHurtsObjectRadius` | server | `firehurtsradius` |  | client copy is dead |
| Damage | `FireHurtsPlayerOnly` | server | `firehurtsplayeronly` |  | client copy is dead |
| Damage | `GroundDamageMaxConcurrent` | server | `grounddamagemax` | LowSpec + Apocalypse | client copy is dead |
| Weather | `RainGroundBurnDurationMultiplier` | server | `rainmultiplier` |  | client copy is dead |
| Weather | `RainObjectBurnDurationMultiplier` | server | `rainobjectmultiplier` |  | client copy is dead |
| Weather | `RainSuppressesGroundFire` | server | `rainsuppress` | LowSpec + Apocalypse | client copy is dead |
| Weather | `RainSuppressesObjectFire` | server | `rainobjects` | LowSpec + Apocalypse | client copy is dead |
| Items | `DousingBombRadius` | both | `dousingradius` |  | server copy is the ceiling for client requests (0.23) |
| Debug | `DebugLogging` | both | `debug` |  |  |
| Meta | `ConfigVersion` | both |  |  |  |

## Preset overrides, exactly

- `MaxKillsPerCycle` (server): WatchTheWorldBurn - FireConfig.cs:262 raises the effective cap to at least 50 (BurnMaxKillsPerCycle) whenever Apocalypse is active. LowSpecPreset has no effect on this key (not referenced by EffectiveMaxKillsPerCycle).
- `WatchTheWorldBurn` (both): LowSpecPreset - FireConfig.cs:200-212, comment 'If BOTH presets are somehow on, LOW SPEC WINS': Apocalypse (and therefore every effect of WatchTheWorldBurn) silently evaluates false whenever LowSpecPreset is also true.
- `DouseImmunitySeconds` (server): WatchTheWorldBurn - FireConfig.cs:253 forces EffectiveDouseImmunitySeconds to 0 whenever Apocalypse is active (dousing no longer holds anything). LowSpecPreset has no effect on this key.
- `FireRampEnabled` (server): WatchTheWorldBurn (Apocalypse) forces it OFF: Config/FireConfig.cs:244-245 EffectiveFireRampEnabled => !Apocalypse && FireRampEnabled.Value, with Apocalypse = WatchTheWorldBurn && !LowSpec (Config/FireConfig.cs:211-212)
- `MaxConcurrentBurning` (server): LowSpecPreset mins it to 20 (LowSpecMaxConcurrentBurning); Apocalypse maxes it to 200 (BurnMaxConcurrentBurning) when LowSpec is not also on
- `SpreadCheckInterval` (server): LowSpecPreset floors it at 2s via Mathf.Max (the one 'cheaper = larger' exception, per the code comment at Config/FireConfig.cs:264-268); Apocalypse ceilings it at 0.25s via Mathf.Min when LowSpec is not also on
- `SpreadMaturityFraction` (server): WatchTheWorldBurn (Apocalypse) forces this to 0f while active (Config/FireConfig.cs:218-219)
- `SpreadRadius` (server): Apocalypse (WatchTheWorldBurn) raises it to at least BurnSpreadRadius=15m via Mathf.Max
- `GroundFirebreaksEnabled` (server): WatchTheWorldBurn forces EffectiveGroundFirebreaksEnabled to false (paths/cultivated ground stop blocking spread). LowSpecPreset does not touch this key.
- `GroundFuelExhaustionEnabled` (server): WatchTheWorldBurn forces EffectiveGroundFuelExhaustionEnabled to false (burned ground can relight immediately). LowSpecPreset does not touch this key.
- `GroundMaxConcurrent` (server): LowSpecPreset: EffectiveGroundMaxConcurrent = Mathf.Min(value,25). WatchTheWorldBurn (when LowSpec is off): Mathf.Max(value,500).
- `GroundMaxSpreadDistance` (server): No Effective accessor of its own, but every reader first checks EffectiveGroundMaxSpreadDistanceEnabled, which WatchTheWorldBurn forces to false — so under Apocalypse this raw value is never actually consulted by any of the three readers, though the ConfigEntry itself is untouched.
- `GroundMaxSpreadDistanceEnabled` (server): WatchTheWorldBurn forces EffectiveGroundMaxSpreadDistanceEnabled to false (no leash at all: fire can travel as far as fuel allows). LowSpecPreset does not touch this key.
- `GroundSpreadRadius` (server): WatchTheWorldBurn: EffectiveGroundSpreadRadius = Mathf.Max(GroundSpreadRadius.Value, 20f) when Apocalypse is in force (and LowSpec is off). LowSpecPreset does not touch this key.
- `GroundVfxMaxConcurrent` (both): LowSpecPreset: EffectiveGroundVfxMaxConcurrent = Mathf.Min(value,10). WatchTheWorldBurn (LowSpec off): Mathf.Max(value,500).
- `GroundWaterBlocksSpreadEnabled` (server): WatchTheWorldBurn forces EffectiveGroundWaterBlocksSpreadEnabled to false (fire can spread across water). LowSpecPreset does not touch this key.
- `CharredSmokeEnabled` (client): LowSpecPreset forces EffectiveCharredSmokeEnabled to false via `!LowSpec && CharredSmokeEnabled.Value`, overriding the raw CharredSmokeEnabled value whenever the low-spec preset is on.
- `TreeRegrowthEnabled` (server): WatchTheWorldBurn forces it OFF: Config/FireConfig.cs:248-249 `EffectiveTreeRegrowthEnabled => !Apocalypse && TreeRegrowthEnabled.Value`, with Apocalypse = WatchTheWorldBurn.Value && !LowSpec at Config/FireConfig.cs:211-212. Every functional reader goes through the Effective accessor, so with the burntheworld preset on the raw key is overridden to false.
- `CrownSparksEnabled` (client): LowSpecPreset forces EffectiveCrownSparksEnabled to false regardless of the raw value.
- `FireShadowsEnabled` (client): LowSpecPreset forces EffectiveFireShadowsEnabled to false.
- `HeatHazeEnabled` (client): LowSpecPreset forces EffectiveHeatHazeEnabled to false.
- `MaxFlameHeight` (client): LowSpecPreset clamps EffectiveMaxFlameHeight to at most 12.
- `ScorchMarksEnabled` (both): LowSpecPreset forces EffectiveScorchMarksEnabled to false.
- `TallFireMaxConcurrent` (client): LowSpecPreset clamps EffectiveTallFireMaxConcurrent to at most 4.
- `GroundDamageMaxConcurrent` (server): LowSpecPreset: EffectiveGroundDamageMaxConcurrent clamps to 20 (Config/FireConfig.cs:112,148) whenever LowSpecPreset is on. WatchTheWorldBurn/Apocalypse does not touch this accessor.
- `RainSuppressesGroundFire` (server): WatchTheWorldBurn: EffectiveRainSuppressesGroundFire is forced false whenever Apocalypse (WatchTheWorldBurn && !LowSpec) is active, regardless of the raw value (Config/FireConfig.cs:211-229). LowSpecPreset has no effect on this key.
- `RainSuppressesObjectFire` (server): WatchTheWorldBurn: EffectiveRainSuppressesObjectFire is forced false whenever Apocalypse is active, same mechanism as RainSuppressesGroundFire (Config/FireConfig.cs:211-233). LowSpecPreset has no effect.

## Keys read on both sides

- `LowSpecPreset`: On a genuine headless dedicated server, the client-facing Effective* accessors it feeds (scorch, crown sparks, shadows, haze, charred smoke) are still technically evaluated inside FireManager.cs's SpawnGroundVfxFor (server-run, but gated `!ValheimBridge.IsDedicatedServer()` at lines 1884/1890) and are no-ops there; the value only has visible effect on a listen host or a real client, even though the field itself is genuinely dual-purpose (server caps + client visuals).
- `WatchTheWorldBurn`: Commands/FireDevCommands.cs:338-343 also prints EffectiveSpreadMaturityFraction/EffectiveGroundFirebreaksEnabled/etc as a confirmation echo right after 'fireset burntheworld' is typed - treated the same as a status-line echo (not counted as a classification-relevant reader).
- `ExtinguishGroundRadius`: In a real dedicated-server + remote-client topology, HandleExtinguishRequest (Fire/FireManager.cs:1026-1036) applies whatever radius arrived over the RPC and never re-reads FireConfig.ExtinguishGroundRadius.Value on the server - the server's own branch at lines 861-872 is only reachable when the same peer also has a live local player, which a headless dedicated server never has. Until 0.22.1 that meant the server's own ConfigEntry was never consulted and the connecting client's copy determined the radius applied. Since 0.23 the server caps every request at the larger of its own ExtinguishGroundRadius and DousingBombRadius, so the client's copy can only make its own request smaller.
- `BurnDurationSeconds`: Genuinely read on both sides for different purposes. The server's copy sets the real expiry/kill pacing. Every peer that draws fire it did not itself simulate (a connected client, and a listen host's own client half for OTHER players' fires) independently re-derives the same formula from ITS OWN local copy, because duration itself is never put on the wire - only ignited-at age is synced (see HandleGroundSyncRequest, line 971). A client whose local BurnDurationSeconds has drifted from the server's real value will smoulder/finish its mirrored VFX at the wrong moment even though the true burn timing is unaffected.
- `BurnPlayerBuildings`: Same both-sides pattern as BurnTreesAndLogs, via the shared IsBurnable() function's Piece branch.
- `BurnTreesAndLogs`: Both copies are genuinely read, for different purposes: the server's is the real gate everywhere ignition actually happens; the LOCAL copy of whichever peer types the 'ignite' console command is read first as a client-side sanity pre-check before the request is even sent to the server.
- `FireKeepsYouWarm`: TryFindFireNearPlayer (line 3465) explicitly branches on IsServer(): it checks the authoritative _burning/_groundBurning collections when true, and the synced _remoteGroundCells/_remoteVfx mirror when false - doc comment at lines 3462-3464: 'so a listen host and a connected client both get an answer about the fire they can actually see.' It is inert only on a genuine headless dedicated server, because ValheimBridge.LocalPlayerPosition() returns null there (line 3452) - not because of a side-gate on the config key itself.
- `FireWarmthRadius`: Same both-sides reasoning as FireKeepsYouWarm: the single call site is reached by every peer type, and the function it feeds branches server/client internally on the data source, not on the radius itself.
- `GroundCellSize`: Real 'both, for different purposes' key: the server's own copy sizes the authoritative ignition/spread grid (cell keys, firebreak sampling, damage-zone radius, the vertical burn band); since 1.0.2 the server sends its cell size with every ground-fire sync, and a client places the fire it draws, the warmth it feels and the scorch decals it lays with the SERVER's size, so a client's own copy changes nothing on a server that sends it. (Before 1.0.2 the client used its own copy for all three, so a client set differently drew, felt and scorched the whole fire at scaled coordinates, not just its decals.)
- `GroundVfxMaxConcurrent`: On a TRUE dedicated server this key's own copy is functionally inert: the ignition-time cap checks (SpawnGroundVfxFor/UpgradeDarkGroundCells) never run there, and DrainRemoteVfxSpawnQueue's cap is computed every frame but never actually consulted for anything, because `_remoteVfxSpawnQueue` is only ever populated by HandleGroundFireSync, which itself returns immediately whenever IsServer() is true (line 3319) — so the queue is always empty on a real dedicated server and the cap comparison is dead weight. On a listen host or single player the SAME copy is genuinely live for the ignition-time cap, since !IsDedicatedServer() is true there. Meanwhile every real client's own copy is always live via DrainRemoteVfxSpawnQueue. Net effect: an admin's cap set on a real dedicated server never reaches the machines actually drawing the fire — each player's own local GroundVfxMaxConcurrent decides what they see, exactly as the 3384-3388 comment describes.
- `CharredTreeHealthFraction`: Read on both sides for genuinely different objects: the server's copy sizes the standing charred tree's health when it first chars; the owning client's copy separately sizes the fallen log's health when that charred tree later collapses.
- `TreeFireDamageEnabled`: Genuinely dual-purpose: the server's copy decides whether unseen fire-damage ticks actually happen (real gameplay); the client's own copy of the same key only decides which progress source that client's flame VFX reads from, independent of what the server is doing with its own copy.
- `ScorchMarkLifetimeSeconds`: Per its own bind description, ignored entirely when UseVanillaDirtPaint is true (real terrain paint is permanent) - not enforced by a check at either read site, just true in practice because the real-paint path (FlushPendingPaint) never consults this value.
- `ScorchMarksEnabled`: Ground-only; object-fire burnouts never leave a scorch mark. Client's own copy governs its own remote-mirror decal; the owning peer's copy only matters when that peer is a listen host.
- `SmoulderAfterFraction`: Always read alongside SmoulderingVfxEnabled, in the same two functions; identical side classification.
- `SmoulderingVfxEnabled`: Read on both sides for different purposes: the server-gated path governs the owning peer's own view (dead unless that peer happens to be a listen host); the unconditional client path governs every other peer's remote-fire mirror. On the intended headless-dedicated-server deployment the server's own copy never has any visible effect.
- `UseProceduralVfx`: Same both-sides split as VfxPrefabName, but this key additionally drives ground fire's own procedural VFX (CreateProceduralGroundFireVfx), which object fire's VfxPrefabName fallback does not have.
- `VfxPrefabName`: Ignored whenever UseProceduralVfx is true (checked first in both SpawnVfxFor and SpawnRemoteVfxOnly). Ground fire never reads this key - CreateProceduralGroundFireVfx has no vanilla-prefab fallback.
- `DousingBombRadius`: Same shape as ExtinguishGroundRadius: Fire/FireManager.cs's HandleExtinguishRequest (:1026-1036) applies whatever radius came over the wire and never consults the server's own FireConfig.DousingBombRadius. A real headless dedicated server never owns a thrown projectile (it has no player to throw one), so the throwing client's copy sets the radius asked for; since 0.23 the server's copy is part of the ceiling that request is clamped to (see ExtinguishGroundRadius above).
- `DebugLogging`: Field is still named VerboseLogging in code, but the BOUND key is "Debug"/"DebugLogging" (FireConfig.cs:376-384) - deliberately renamed from the old "VerboseLogging" key so BepInEx would not silently inherit a value on existing installs; ConfigLedger.cs:82 retires the old Debug.VerboseLogging slot on migration. Also directly toggled (bypassing the fireset switch's explicit forward) by the standalone `firedebug` command at Commands/FireDevCommands.cs:282-286; that still reaches the server because VerboseLogging is registered in Settable(), so the generic OnSettingChanged/_pendingSync live-config-sync hook (FireDevCommands.cs:1161-1200) forwards it anyway.
- `ConfigVersion`: Explicitly documented as 'Not a setting: leave it alone' (FireConfig.cs:18). Absent from the fireset switch and the Settable() forwarding map entirely, so it can never be set live. Each peer (client, server, listen host) has its own local BepInEx config file and stamps/reads its own copy independently during its own boot migration - there is no network sync of this value, so classifying it 'both' means 'each side does this for itself', not a client/server split of shared state the way the fire settings are.

## Reader citations

File and line per reader, at `c4ab08c`. Status-line echoes and the `fireset` setter itself are listed where the agents noted them but do not count as readers.

<details><summary><code>Enabled</code> (server)</summary>

- Fire/FireManager.cs:775 - `if (!FireConfig.Enabled.Value) return;`, placed AFTER the `if (!ValheimBridge.IsServer()) return;` gate at line 764 in Update(), so only the server-authoritative peer ever reaches it
- Fire/FireManager.cs:1135 - guard at the top of TryIgnite(Component,long); every real call path into TryIgnite is itself gated on ValheimBridge.IsServer() first (e.g. Patches/TreeBaseRpcDamagePatch.cs:85-88, Patches/TreeLogRpcDamagePatch.cs, Patches/WearNTearRpcDamagePatch.cs all branch on IsServer() before calling it), so this is a server-only read in practice too

</details>
<details><summary><code>LowSpecPreset</code> (both)</summary>

- Config/FireConfig.cs:127 - the private `LowSpec` helper (`LowSpecPreset.Value`) most Effective* accessors and the Apocalypse helper are built on
- Server side, via LowSpec: EffectiveMaxConcurrentBurning (:130) -> Fire/FireManager.cs:1152,2886,3915; EffectiveGroundMaxConcurrent (:135) -> FireManager.cs:1821; EffectiveGroundDamageMaxConcurrent (:148) -> FireManager.cs:1896,1923,1990; EffectiveSpreadCheckInterval (:270) -> FireManager.cs:795
- Client side, via LowSpec: EffectiveScorchMarksEnabled (:151) -> FireManager.cs:3361,3595 (scorch-decal spawn, gated `|| ValheimBridge.IsDedicatedServer()` at :3595, a real client path); EffectiveCrownSparksEnabled (:154-155) -> Fire/FireVFXController.cs:297, Utils/ValheimBridge.cs:2729; EffectiveMaxFlameHeight (:164) -> FireVFXController.cs:176, ValheimBridge.cs:2804; EffectiveTallFireMaxConcurrent (:167) -> FireVFXController.cs:677, ValheimBridge.cs:2770; EffectiveFireShadowsEnabled (:176) -> FireVFXController.cs:639; EffectiveHeatHazeEnabled (:179) -> FireVFXController.cs:299; EffectiveCharredSmokeEnabled (:185) -> Fire/CharredSmoke.cs:44,196; EffectiveGroundVfxMaxConcurrent (:143) -> FireManager.cs:3389 (DrainRemoteVfxSpawnQueue, 'Client-only in practice')
- Utils/ValheimBridge.cs:3264 - a SECOND, independent raw read of `FireConfig.LowSpecPreset.Value` (bypassing the LowSpec helper entirely) inside BuildCrownFlames, halving the client's particle budget directly

</details>
<details><summary><code>MaxKillsPerCycle</code> (server)</summary>

- Config/FireConfig.cs:261-262 - EffectiveMaxKillsPerCycle (`Apocalypse ? Mathf.Max(MaxKillsPerCycle.Value, BurnMaxKillsPerCycle) : MaxKillsPerCycle.Value`)
- Fire/FireManager.cs:2653 - ExpireTimers() batch-destroy cap, called from the main cycle after the server gate (line 764) and the per-cycle throttle (line 794)
- Fire/FireManager.cs:2744 - TickTreeFire() charring batch cap, same cycle

</details>
<details><summary><code>PersistFiresEnabled</code> (server)</summary>

- Fire/FireManager.cs:772 - inside Update(), after the server gate (line 764), guards RestorePersistedFires() at boot
- Fire/FireManager.cs:1245 - MaybePersistFires() periodic-save gate, called from the main server-gated cycle
- Fire/FireManager.cs:1254 - PersistFiresNow(), which also independently checks `!ValheimBridge.IsServer()` immediately after at line 1255

</details>
<details><summary><code>WatchTheWorldBurn</code> (both)</summary>

- Config/FireConfig.cs:212 - the private `Apocalypse` helper (`WatchTheWorldBurn.Value && !LowSpec`) every downstream Effective* accessor below is built on
- Server side, via Apocalypse: EffectiveSpreadMaturityFraction (FireConfig.cs:219) -> Fire/FireManager.cs:3717; EffectiveGroundFirebreaksEnabled (:223) -> FireManager.cs:1714,1798; EffectiveGroundWaterBlocksSpreadEnabled (:226) -> FireManager.cs:1843; EffectiveRainSuppressesGroundFire (:229) -> FireManager.cs:1618,2605; EffectiveRainSuppressesObjectFire (:233) -> FireManager.cs:2588,3733,3776; EffectiveGroundFuelExhaustionEnabled (:237) -> FireManager.cs:1789,2909,2921; EffectiveGroundMaxSpreadDistanceEnabled (:241) -> FireManager.cs:1815,3825; EffectiveFireRampEnabled (:245) -> FireManager.cs:613,841; EffectiveTreeRegrowthEnabled (:249) -> FireManager.cs:2804,2958,3149 and Commands/FireDevCommands.cs:773 (gated behind RelayIfClient at :771, so server-only); EffectiveDouseImmunitySeconds (:253) -> FireManager.cs:1064,1070,1092,1108,1188,1189,1789,2921; EffectiveSpreadRadius (:256) -> FireManager.cs:505,3660,3678,3724,3824; EffectiveGroundSpreadRadius (:259) -> FireManager.cs:3726,3771,3824; EffectiveMaxKillsPerCycle (:262) -> FireManager.cs:2653,2744; EffectiveMaxConcurrentBurning (:131, also LowSpec-gated) -> FireManager.cs:1152,2886,3915; EffectiveGroundMaxConcurrent (:136, also LowSpec-gated) -> FireManager.cs:1821; EffectiveSpreadCheckInterval (:271, also LowSpec-gated) -> FireManager.cs:795 - all of these call sites sit inside the server-gated cycle (past the IsServer() check at FireManager.cs:764)
- Client side, via Apocalypse: EffectiveGroundVfxMaxConcurrent (FireConfig.cs:144) -> Fire/FireManager.cs:3389 inside DrainRemoteVfxSpawnQueue, explicitly commented 'Client-only in practice' (line 3376)

</details>
<details><summary><code>DouseImmunitySeconds</code> (server)</summary>

- Config/FireConfig.cs:252-253 - EffectiveDouseImmunitySeconds (`Apocalypse ? 0f : DouseImmunitySeconds.Value`)
- Fire/FireManager.cs:1064,1070 - ExtinguishGroundNear, marking cleared cells as doused
- Fire/FireManager.cs:1092,1108 - same area, ground-exhaustion/douse bookkeeping
- Fire/FireManager.cs:1188-1189 - Extinguish(Component), marking an object doused
- Fire/FireManager.cs:1789 and 2921 - exhaustion-set pruning gates in the main server cycle

</details>
<details><summary><code>ExtinguishGroundRadius</code> (both)</summary>

- Fire/FireManager.cs:871-872 - inside TryPlayerExtinguish's `if (ValheimBridge.IsServer())` branch: this peer's own copy is used directly (ExtinguishGroundNear/ExtinguishObjectsNear), reachable only when the executing peer has BOTH a local player AND server authority - i.e. a listen host
- Fire/FireManager.cs:887 - the `else` branch: the CLIENT's own local copy is read and sent, verbatim, to the server via ValheimBridge.SendExtinguishRequestToServer

</details>
<details><summary><code>ExtinguishKey</code> (client)</summary>

- Fire/FireManager.cs:718 - `if (FireConfig.ExtinguishKey.Value.IsDown()) TryPlayerExtinguish();`, checked every Update() frame BEFORE the server-authority gate at line 764, so the statement executes on every peer's process but only has any effect on the peer with a live local player/crosshair

</details>
<details><summary><code>BurnDurationSeconds</code> (both)</summary>

- Fire/FireManager.cs:2046 - StartBurning() sets BurningState.ExpireAt = Time.time + value, the real expiry of a burning object - server (StartBurning is reachable only through TryIgnite, and every call site of TryIgnite is gated behind ValheimBridge.IsServer(), see Patches/TreeBaseRpcDamagePatch.cs:80-90 and Commands/FireDevCommands.cs:107-159)
- Fire/FireManager.cs:2107 - SpawnVfxFor() uses it as the fallback duration for this process's own procedural VFX; SpawnVfxFor only runs from StartBurning (server call chain) but this line only executes when wantVisual is true, which requires !IsDedicatedServer() - so this specific read only matters on a listen host/singleplayer, not a genuine dedicated server
- Fire/FireManager.cs:2294 - ProcessRemoteSmouldering(): 'after' = BurnDurationSeconds*SmoulderAfterFraction, used to decide when a mirrored (someone else's) fire should downgrade to embers locally - client (comment at line 751 explicitly labels the call 'client-side: the server is headless, THIS is what a player sees'; runs every Update before the IsServer gate at line 764)
- Fire/FireManager.cs:2493 - SpawnRemoteVfxOnly(): duration = BurnDurationSeconds.Value, the assumed total VFX lifetime for a fire this client did not itself simulate - client (reached only via DrainRemoteObjectVfxQueue, run unconditionally before the IsServer gate; its enqueue handlers HandleFireEventBroadcast/HandleObjectFireSync both early-return when IsServer() is true, so the queue stays empty on a dedicated server)
- Fire/FireManager.cs:2726 - TickTreeFire(): killSeconds = BurnDurationSeconds*TreeFireKillFraction sizes the per-tick tree damage - server (TickTreeFire only runs from the server-gated Update cycle, line 801)
- Fire/FireManager.cs:2829 - ProcessSmouldering(): times the server's own BurningState.Smouldering flag, later included in the join snapshot sent to a peer (HandleGroundSyncRequest, line 972) - server
- Fire/FireManager.cs:3717 - SpreadPass(): maturitySeconds = BurnDurationSeconds*EffectiveSpreadMaturityFraction paces when a burner may ignite neighbors - server
- Fire/FireManager.cs:1552 - StatusLine() echoes the raw value in the status string - status line, does not count as a reader
- Commands/FireDevCommands.cs:307 - fireset 'burnduration' case writes the LOCAL ConfigEntry on whichever peer typed the command - a setter, not a simulation read
- Commands/FireDevCommands.cs:983 - Settable() dictionary entry, used only to resolve the ConfigEntry for forwarding/live-sync lookups - not a simulation read

</details>
<details><summary><code>BurnPlayerBuildings</code> (both)</summary>

- Utils/ValheimBridge.cs:493 - IsBurnable(): Piece branch, the anti-grief switch ('with BurnPlayerBuildings off, anything a player placed is fireproof')
-   -> same SERVER call sites as BurnTreesAndLogs above (TryIgnite line 1137, PromoteFromQueue line 2882, SpreadPass lines 3747/3785, relayed ignite line 117, TryIgniteIfInRange line 208)
-   -> same CLIENT call site as BurnTreesAndLogs: Commands/FireDevCommands.cs:130, the local 'ignite' pre-check run by whichever peer typed the command
- Fire/FireManager.cs:3876,3950 - BuildCandidateList()/IgniteBurnablesNear() pass it through to ValheimBridge.CollectBurnableZdosNear - server
- Fire/FireManager.cs:1559 - StatusLine() echo - status line, does not count

</details>
<details><summary><code>BurnTreesAndLogs</code> (both)</summary>

- Utils/ValheimBridge.cs:497 - IsBurnable(): gates whether a Tree/Log target counts as burnable at all
-   -> reached SERVER-side from Fire/FireManager.cs:1137 (TryIgnite), 2882 (PromoteFromQueue), 3747 & 3785 (SpreadPass), and Commands/FireDevCommands.cs:117 (relayed 'ignite', reached only inside an IsServer() branch) and :208 (TryIgniteIfInRange inside StartFire, reached only after RelayIfClient(args) returns false)
-   -> reached CLIENT-side from Commands/FireDevCommands.cs:130 - the 'ignite' command's local crosshair pre-check, which runs on WHICHEVER peer typed the command (comment: 'only this machine has a crosshair, so the raycast happens here whichever side we are on'), BEFORE the IsServer() branch decides to ignite directly or relay; a client whose local BurnTreesAndLogs differs from the server's can locally refuse ('Not burnable') an ignite the server would actually have allowed, or the reverse
- Commands/FireDevCommands.cs:185 - StartFire(): gates whether the tree/log FindObjectsOfType scan runs at all - server (only reached after RelayIfClient(args) returns false and RequireAdmin passes)
- Fire/FireManager.cs:3876,3891 - BuildCandidateList(): gates whether trees/logs are scanned into the cached spread-candidate pool - server (called only from SpreadPass)
- Fire/FireManager.cs:3950 - IgniteBurnablesNear(): passed to ValheimBridge.CollectBurnableZdosNear as a filter - server (called only from StartFire, server-gated)
- Fire/FireManager.cs:1558 - StatusLine() echo - status line, does not count

</details>
<details><summary><code>FireInAshlands</code> (server)</summary>

- Fire/FireManager.cs:1198 - AshlandsBarsFireAt(), the only reader; every caller below runs on the server
-   -> TryIgnite line 1242 (every object ignition), TryIgniteGroundCell line 1907 (every ground ignition), HandleIgniteRequest line 2276 (by the ZDO's position, before an instance is built), PromoteFromQueue line 3009, RestorePersistedFires lines 1474 (ground) and 1516 (objects)
-   -> Commands/FireDevCommands.cs:118, 136, 177, 774 - ignite (relayed, and typed on the host), startfire, firegroundignite; each runs after the relay, on the server
- The three RPC_Damage patches do not read it: a client forwards every hit and the server decides, because a client's own copy is not the server's setting
- Fire/FireManager.cs:1670 - StatusLine() echo - status line, does not count

</details>
<details><summary><code>FireKeepsYouWarm</code> (both)</summary>

- Fire/FireManager.cs:3445 - ApplyFireWarmth(): master on/off gate, called every Update() from EVERY peer (line 723, ABOVE the IsServer() return at line 764) - genuinely both sides run it

</details>
<details><summary><code>FireRampDurationSeconds</code> (server)</summary>

- Fire/FireManager.cs:616 - GetRampFraction(int eventId): dur = Max(1, value) - server
- Fire/FireManager.cs:845 - GetRampFraction(): parameterless overload, same use - server
- Fire/FireManager.cs:1582 - StatusLine() echo of the raw value - status line, does not count

</details>
<details><summary><code>FireRampEnabled</code> (server)</summary>

- Fire/FireManager.cs:613 - GetRampFraction(int eventId): short-circuits to 1f (no ramp) when off - server (only called from TryIgnite, PromoteFromQueue, SpreadPass, IgniteZdoCandidatesNear, all confirmed server-gated)
- Fire/FireManager.cs:841 - GetRampFraction(): parameterless overload, same short-circuit - server
- Fire/FireManager.cs:1582 - StatusLine() echo of EffectiveFireRampEnabled - status line, does not count
- Commands/FireDevCommands.cs:341 - burntheworld echo of EffectiveFireRampEnabled - cosmetic, whichever peer typed the command

</details>
<details><summary><code>FireRampStartFraction</code> (server)</summary>

- Fire/FireManager.cs:615 - GetRampFraction(int eventId): start = Clamp01(value) - server
- Fire/FireManager.cs:842,846 - GetRampFraction(): parameterless overload's no-fire sentinel return value and the Lerp start bound - server
- Fire/FireManager.cs:1582 - StatusLine() echo of the raw value - status line, does not count

</details>
<details><summary><code>FireWarmthRadius</code> (both)</summary>

- Fire/FireManager.cs:3454 - ApplyFireWarmth(): passed as the search radius into TryFindFireNearPlayer(), executed by whichever peer is running ApplyFireWarmth (see FireKeepsYouWarm entry for the server/client branch inside TryFindFireNearPlayer)

</details>
<details><summary><code>MaxConcurrentBurning</code> (server)</summary>

- Fire/FireManager.cs:1152 - TryIgnite(): effectiveMax admits a new burner into its event's budget - server (TryIgnite is only ever called after an IsServer() check at every call site)
- Fire/FireManager.cs:2052 - StartBurning()'s FireLogger.Debug prints the raw MaxConcurrentBurning.Value in a diagnostic log line only, not the status line but purely cosmetic text - server, cosmetic
- Fire/FireManager.cs:2886 - PromoteFromQueue(): evMax gate before promoting a queued item into an event - server
- Fire/FireManager.cs:3915 - IgniteZdoCandidatesNear(): effectiveMax gate before igniting a ZDO-layer candidate - server (called only from SpreadPass)
- Fire/FireManager.cs:1549 - StatusLine() echo of EffectiveMaxConcurrentBurning - status line, does not count
- Commands/FireDevCommands.cs:342 - burntheworld echo of EffectiveMaxConcurrentBurning - cosmetic, whichever peer typed the command
- Commands/FireDevCommands.cs:357 - lowspec echo of EffectiveMaxConcurrentBurning - cosmetic, whichever peer typed the command

</details>
<details><summary><code>QueueSize</code> (server)</summary>

- Fire/FireQueue.cs:28 - Capacity property wraps FireConfig.QueueSize.Value
- Fire/FireManager.cs:1164,1166 - TryIgnite(): _queue.TryEnqueue()/_queue.Capacity when the burn cap is full - server
- Fire/FireManager.cs:2874 - PromoteFromQueue(): guard = _queue.Capacity + 1 bounds the promotion loop - server
- Fire/FireManager.cs:3922 - IgniteZdoCandidatesNear(): _queue.Capacity checked as part of the per-event admit gate - server
- Fire/FireManager.cs:1550 - StatusLine() echoes _queue.Capacity - status line, does not count

</details>
<details><summary><code>SpreadCheckInterval</code> (server)</summary>

- Fire/FireManager.cs:795 - Update(): _nextCycle = Time.time + EffectiveSpreadCheckInterval sets the real spread-cycle cadence - server (this line runs after the IsServer() return at line 764)
- Fire/FireManager.cs:1555 - StatusLine() echo of EffectiveSpreadCheckInterval - status line, does not count
- Commands/FireDevCommands.cs:343 - burntheworld echo of EffectiveSpreadCheckInterval - cosmetic, whichever peer typed the command
- Commands/FireDevCommands.cs:359 - lowspec echo of EffectiveSpreadCheckInterval - cosmetic, whichever peer typed the command

</details>
<details><summary><code>SpreadMaturityFraction</code> (server)</summary>

- Fire/FireManager.cs:3717 - SpreadPass(): maturitySeconds = BurnDurationSeconds*EffectiveSpreadMaturityFraction gates when a burning object may ignite neighbors, ZDO candidates, and ground cells - server (SpreadPass only runs from the server-gated Update cycle, line 823)
- Fire/FireManager.cs:1552 - StatusLine() echoes EffectiveSpreadMaturityFraction - status line, does not count
- Commands/FireDevCommands.cs:339 - the 'fireset burntheworld' confirmation Say() re-reads EffectiveSpreadMaturityFraction off whichever peer typed the command, purely to report what that peer's OWN local config now computes to; cosmetic echo, not simulation, and not necessarily the server's real value if typed on a client

</details>
<details><summary><code>SpreadRadius</code> (server)</summary>

- Fire/FireManager.cs:505 - EventJoinRadius get: adds EffectiveSpreadRadius when sizing how close an ignition must be to join an existing FireEvent - server (EventForPosition/EventJoinRadius reached only from TryIgnite and PromoteFromQueue, both server-gated)
- Fire/FireManager.cs:3660 - LogSpreadCandidateDiagnostic(): reports EffectiveSpreadRadius in a debug diagnostic string - server (called only from SpreadPass)
- Fire/FireManager.cs:3678 - SpreadPass(): diagnosticRadius = EffectiveSpreadRadius*GetRampFraction() sizes the diagnostic log's search radius - server
- Fire/FireManager.cs:3724 - SpreadPass(): effectiveSpreadRadius = EffectiveSpreadRadius*ramp is the real per-cycle object-ignition reach - server
- Fire/FireManager.cs:3824 - reach = Max(EffectiveSpreadRadius, EffectiveGroundSpreadRadius) sizes the ZDO sector sweep rebuilt in BuildCandidateList - server
- Fire/FireManager.cs:1553 - StatusLine() echo of EffectiveSpreadRadius - status line, does not count

</details>
<details><summary><code>TreeDestructionRate</code> (server)</summary>

- Fire/CharredTreeLifecycle.cs:195 - ReplaceWithCharred(): UnityEngine.Random.Range(0,100) < value decides collapse vs. stand - server (ReplaceWithCharred is only ever called from CharTree, which is only ever called from TickTreeFire, which only runs from the server-gated Update cycle, line 801)
- Fire/FireManager.cs:1567 - StatusLine() echo of the raw value - status line, does not count

</details>
<details><summary><code>GroundBurnDurationSeconds</code> (server)</summary>

- Fire/FireManager.cs:1857 - TryIgniteGroundCell, sets the new cell's ExpireAt = Time.time + duration (server-only, real on a dedicated server; called only from SpreadPass's IgniteGroundNear/IgniteAdjacentGroundCells path).

</details>
<details><summary><code>GroundCellSize</code> (both)</summary>

- Fire/FireManager.cs:1692 - IgniteGroundNear, converts a seed radius to a cell-grid step count (server-only, real on a dedicated server).
- Fire/FireManager.cs:1769 - GroundPathCrossesFirebreak, sizes the line-sampling step for the firebreak-line check (server-only, called from IgniteGroundNear).
- Fire/FireManager.cs:1935 - SpawnGroundVfxFor, sizes the damage-zone radius (GroundCellSize*0.5) passed to AttachFireDamageZone; unreachable on a real dedicated server because this line only runs if wantDamage is true, and wantDamage requires `!ValheimBridge.IsDedicatedServer()` (line 1882-1884) — so this specific read is listen-host/single-player-only in practice, even though it sits inside server-gated ignition code.
- Fire/FireManager.cs:2027 - KeyOf(pos), the (x,z) cell-bucketing helper; every call site (1695 IgniteGroundNear, 1735 IsFirebreakAt, 3071 IsStandingInFire, 3096 IsFireNear) is server-only, real on a dedicated server.
- Fire/FireManager.cs:2033 - CellCenter(key,y), the cell->world-position helper; most call sites (576, 618/1618, 689, 1085, 1704, 1792, 1861, 2008, 2612, 3475, 3770, 3840, 3868) are server-only/simulation-authority code, but two call sites are genuinely client-side — see below.
- Fire/FireManager.cs:3080 - IsStandingInFire, the vertical damage band for a burning ground cell (server-only, called from DamagePlayersInFireCore which runs after the IsServer() gate).
- Fire/FireManager.cs:3095 - IsFireNear, the search-step size when checking whether a pending tree-regrowth spot is too close to active fire (server-only, called from ProcessTreeRegrowth).
- Fire/FireManager.cs:3596 - LeaveScorchMark, sizes the scorch decal; only reached when `!ValheimBridge.IsDedicatedServer()` (line 3595), so on a real dedicated server this call never executes even though LeaveScorchMark itself is invoked from server-gated code (ExpireGroundTimers, ExtinguishGroundNear) — listen-host/single-player-only in practice.
- Fire/FireManager.cs:3365 - HandleGroundFireSync, sizes a scorch mark spawned locally from a synced ground-fire expiry delta. This function explicitly returns at line 3319 (`if (ValheimBridge.IsServer()) return;`) — genuine CLIENT-side read, real on every connected client (and on a listen host's client half).
- Fire/FireManager.cs:3343 - HandleGroundFireSync, via CellCenter(key,y), computes the remote-mirror world position of a newly-synced cell for the VFX spawn queue — same client-only handler as above.
- Utils/ValheimBridge.cs:2270 - SpawnScorchMark, used to key a deterministic per-cell hash/jitter for the decal's placement; this function is purely a drawing routine (its own doc comment: the spawn moved client-side because a dedicated server has no terrain collider) and is only ever invoked from the listen-host-only LeaveScorchMark path or the genuine client-only HandleGroundFireSync path — never from a true dedicated server.

</details>
<details><summary><code>GroundFirebreaksEnabled</code> (server)</summary>

- Config/FireConfig.cs:222-223 - EffectiveGroundFirebreaksEnabled accessor (Apocalypse forces false).
- Fire/FireManager.cs:1714 - IgniteGroundNear, gates the origin->destination firebreak line check for radius seeding (server-only).
- Fire/FireManager.cs:1798 - TryIgniteGroundCell, gates the destination cell's own firebreak check (server-only).

</details>
<details><summary><code>GroundFuelExhaustionEnabled</code> (server)</summary>

- Config/FireConfig.cs:236-237 - EffectiveGroundFuelExhaustionEnabled accessor (Apocalypse forces false).
- Fire/FireManager.cs:1789 - TryIgniteGroundCell, gates whether an exhausted cell's regrow-timer entry blocks reignition (server-only, real on a dedicated server).
- Fire/FireManager.cs:2909 - ExpireGroundTimers, decides whether a burned-out cell gets a regrow-timer entry at all (server-only).
- Fire/FireManager.cs:2921 - ExpireGroundTimers, gates the periodic sweep that prunes expired exhaustion entries (server-only).

</details>
<details><summary><code>GroundFuelRegrowSeconds</code> (server)</summary>

- Fire/FireManager.cs:2910 - ExpireGroundTimers, sets the exhausted-cell regrow timestamp `now + GroundFuelRegrowSeconds.Value` (server-only, real on a dedicated server).

</details>
<details><summary><code>GroundMaxConcurrent</code> (server)</summary>

- Config/FireConfig.cs:134-137 - EffectiveGroundMaxConcurrent accessor (LowSpec clamps to min(value,25); Apocalypse floors to max(value,500)).
- Fire/FireManager.cs:1821 - TryIgniteGroundCell, the per-event concurrent-ground-cell cap that gates a new ignition (server-only, real on a dedicated server).

</details>
<details><summary><code>GroundMaxSpreadDistance</code> (server)</summary>

- Fire/FireManager.cs:504 - EventJoinRadius property, added to the join radius only when EffectiveGroundMaxSpreadDistanceEnabled is true (server-only).
- Fire/FireManager.cs:1817 - TryIgniteGroundCell, the actual leash distance compared against the candidate cell's fire-event origin (server-only, only reached when EffectiveGroundMaxSpreadDistanceEnabled is true).
- Fire/FireManager.cs:3826 - BuildCandidateList, the sweep-radius cap used when rebuilding spread candidates (server-only, only reached when EffectiveGroundMaxSpreadDistanceEnabled is true).

</details>
<details><summary><code>GroundMaxSpreadDistanceEnabled</code> (server)</summary>

- Config/FireConfig.cs:240-241 - EffectiveGroundMaxSpreadDistanceEnabled accessor (Apocalypse forces false, i.e. no leash).
- Fire/FireManager.cs:504 - EventJoinRadius property, decides whether the leash distance or a fixed 100m fallback contributes to how close an ignition must be to join an existing fire event (server-only; EventJoinRadius/EventForPosition are only ever invoked from server-gated ignition paths: TryIgnite, TryIgniteGroundCell, AdoptOrphanedFires, PromoteFromQueue).
- Fire/FireManager.cs:1815 - TryIgniteGroundCell, gates the leash-distance check against the cell's own fire event's origin (server-only).
- Fire/FireManager.cs:3825 - BuildCandidateList, gates whether the ZDO-candidate sweep radius is capped at the leash distance or a 100m fallback (server-only; called only from SpreadPass).

</details>
<details><summary><code>GroundSpreadEnabled</code> (server)</summary>

- Fire/FireManager.cs:1690 - IgniteGroundNear() returns immediately if false; IgniteGroundNear is called only from SpreadPass (server-gated: SpreadPass runs only after Update()'s `if (!ValheimBridge.IsServer()) return;` at line 764) and from FireDevCommands.FireGroundIgnite, which calls `RelayIfClient(args)` first so its own body (including this call) only executes on the server. Server-side, real on a dedicated server.

</details>
<details><summary><code>GroundSpreadRadius</code> (server)</summary>

- Config/FireConfig.cs:258-259 - EffectiveGroundSpreadRadius accessor definition (Apocalypse takes Mathf.Max(value, 20f)).
- Fire/FireManager.cs:3726 - SpreadPass, object-burner's reach for seeding ground cells around it (server-only).
- Fire/FireManager.cs:3771 - SpreadPass, ground-burner's reach for igniting nearby real objects (server-only).
- Commands/FireDevCommands.cs:754 - FireGroundIgnite's default radius when no argument is given; the function returns immediately on a client via RelayIfClient(args) at line 748, so this default-value read only actually executes on the server.

</details>
<details><summary><code>GroundVfxMaxConcurrent</code> (both)</summary>

- Config/FireConfig.cs:142-145 - EffectiveGroundVfxMaxConcurrent accessor (LowSpec clamps to min(value,10); Apocalypse floors to max(value,500)).
- Fire/FireManager.cs:1896,1911 - SpawnGroundVfxFor's overall/visual cap checks. Unreachable on a real dedicated server: wantVisual and wantDamage both require `!ValheimBridge.IsDedicatedServer()` (lines 1882-1890), and the function returns at line 1891 before these lines when both are false, which is always the case on a true dedicated server. Live only on a listen host or single player.
- Fire/FireManager.cs:1986 - UpgradeDarkGroundCells' visual-headroom check. Also unreachable on a real dedicated server: `_groundVfxDark` is only ever populated when wantVisual was true (same !IsDedicatedServer() gate), so on a true dedicated server `_groundVfxDark.Count==0` short-circuits the function at line 1971 before this line runs. Live only on a listen host or single player.
- Fire/FireManager.cs:3389 - DrainRemoteVfxSpawnQueue's local render cap. This executes unconditionally every Update, including on a real dedicated server (called at line 749, before the IsServer() gate at 764), but is genuinely CLIENT-side: it throttles how many remote/synced ground-fire visuals THIS machine spawns per frame (comment at 3384-3388: the old code let the client mirror draw every synced cell with no ceiling). Real and load-bearing on every connected client and on a listen host's client half.

</details>
<details><summary><code>GroundWaterBlocksSpreadEnabled</code> (server)</summary>

- Config/FireConfig.cs:225-226 - EffectiveGroundWaterBlocksSpreadEnabled accessor (Apocalypse forces false).
- Fire/FireManager.cs:1843 - TryIgniteGroundCell, gates the real-terrain water-level check that blocks ignition on/under water (server-only; relies on the WorldGenerator terrain-height exception that works headless).

</details>
<details><summary><code>WindInfluence</code> (server)</summary>

- Fire/FireManager.cs:1640 - IgniteAdjacentGroundCells, scales the directional wind bias by vanilla's live wind intensity (server-only).

</details>
<details><summary><code>WindSpreadBiasEnabled</code> (server)</summary>

- Fire/FireManager.cs:1622 - IgniteAdjacentGroundCells, decides whether to read vanilla's live wind direction at all for this cycle's neighbor-ignition weighting (server-only; IgniteAdjacentGroundCells is only ever called from SpreadPass, line 3773).

</details>
<details><summary><code>WindUpwindIgniteChance</code> (server)</summary>

- Fire/FireManager.cs:1660 - IgniteAdjacentGroundCells, the ignite-chance floor for a neighbor directly upwind, lerped by wind direction (server-only).

</details>
<details><summary><code>CharredCoalMax</code> (client)</summary>

- Fire/FireManager.cs:1567 - StatusLine() echo - does not count
- Fire/CharredTreeLifecycle.cs:399 - RollCoal(): `int max = Mathf.Max(min, FireConfig.CharredCoalMax.Value);` same owner/client-only function as CharredCoalMin - CLIENT

</details>
<details><summary><code>CharredCoalMin</code> (client)</summary>

- Fire/FireManager.cs:1567 - StatusLine() echo - does not count
- Fire/CharredTreeLifecycle.cs:398 - RollCoal(): `int min = Mathf.Max(0, FireConfig.CharredCoalMin.Value);`. RollCoal is only called from CollapseStandingTree() and CrumbleLog(), both of which require `nv.IsOwner()` on a real, instantiated TreeBase/TreeLog GameObject (via CharredTreeController, which only attaches where the object actually exists as an instance, or via TreeBaseRpcDamagePatch's CharredHit on a live chop). A dedicated server has no such instances away from origin, so this executes on the owning client (or the client side of a listen host) - CLIENT

</details>
<details><summary><code>CharredCollapseDelaySeconds</code> (server)</summary>

- Fire/FireManager.cs:1567 - StatusLine() echo - does not count
- Fire/CharredTreeLifecycle.cs:197 - ReplaceWithCharred(): `long delayTicks = (long)(Mathf.Max(0f, FireConfig.CharredCollapseDelaySeconds.Value) * TimeSpan.TicksPerSecond);` bakes the CollapseAtHash timestamp into the freshly-created charred ZDO. ReplaceWithCharred is only ever invoked from FireManager.CharTree(), itself only reached from the server-gated ExpireTimers()/TickTreeFire() cycle - SERVER
- Fire/CharredTreeLifecycle.cs:266 - debug log of the same value, same server-only function - SERVER

</details>
<details><summary><code>CharredEmberCoverage</code> (client)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredTextures.cs:148 - ember mask/texture generation: `float coverage = Mathf.Clamp01(FireConfig.CharredEmberCoverage.Value);` reached only via the graphics-gated CharredTreeSkin.Apply()/CharredTreeController.TrySkin() path - CLIENT
- Fire/CharredTextures.cs:409 - a second mask builder, same coverage read, same gating - CLIENT
- Fire/CharredTreeSkin.cs:479 - doc-comment mention only, not a code read

</details>
<details><summary><code>CharredEmberGlowSeconds</code> (client)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredTreeController.cs:93 - CurrentEmber(): feeds CharredTreeSkin.EmberAt() for the current glow color. CharredTreeController's own doc comment says it fades embers 'independent of ownership' on every peer that has a live instance of the object - CLIENT
- Fire/CharredTreeController.cs:115 - Update(): `if (_renderers != null && FireConfig.CharredEmberGlowSeconds.Value > 0f)` gate before refreshing the ember color - CLIENT
- Fire/CharredTreeController.cs:118 - Update(): `if (age <= FireConfig.CharredEmberGlowSeconds.Value + 1f) ...` same cosmetic fade check - CLIENT
- Fire/CharredTreeSkin.cs:346 - doc-comment mention only, not a code read

</details>
<details><summary><code>CharredEmberIntensity</code> (client)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredTreeSkin.cs:80 - `public static float EmberPeakHdr => 4f * Mathf.Clamp(FireConfig.CharredEmberIntensity.Value, 0f, 2f);` feeds EmberHot/EmberCool material colors used only from the graphics-gated skinning path (CharredTreeController.TrySkin(), which returns early when `!FireVFXController.GraphicsAvailable`) - CLIENT

</details>
<details><summary><code>CharredLogCrumbleSeconds</code> (client)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredTreeLifecycle.cs:326-327 - CollapseStandingTree(): `if (FireConfig.CharredLogCrumbleSeconds.Value > 0f) lz.Set(CrumbleAtHash, now + (long)(FireConfig.CharredLogCrumbleSeconds.Value * TimeSpan.TicksPerSecond));` schedules the crumble timestamp on the log spawned when a charred tree falls. Owner/client-only, same as CharredTreeHealthFraction's second reader - CLIENT

</details>
<details><summary><code>CharredSmokeEnabled</code> (client)</summary>

- Config/FireConfig.cs:185 - definition of EffectiveCharredSmokeEnabled: `!LowSpec && CharredSmokeEnabled.Value` (accessor body, not itself a call site)
- Fire/FireManager.cs:1568 - StatusLine() echoes EffectiveCharredSmokeEnabled - does not count
- Fire/CharredSmoke.cs:44 - TryAttach(): `if (!FireConfig.EffectiveCharredSmokeEnabled) return null;`. Only reached from CharredTreeController.TrySkin()/TrySmoke(), past the `!FireVFXController.GraphicsAvailable` early return - CLIENT
- Fire/CharredSmoke.cs:196 - Tick(): `if (ageSeconds >= _seconds || !FireConfig.EffectiveCharredSmokeEnabled)` re-checks the live flag each tick, same graphics-gated component - CLIENT

</details>
<details><summary><code>CharredSmokeSeconds</code> (client)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredSmoke.cs:11 - doc-comment mention only, not a code read
- Fire/CharredSmoke.cs:45 - TryAttach(): `float seconds = FireConfig.CharredSmokeSeconds.Value;` sets how long the cosmetic smoke component lives, in the same graphics-gated component as CharredSmokeEnabled - CLIENT

</details>
<details><summary><code>CharredTreeHealthFraction</code> (both)</summary>

- Fire/FireManager.cs:1568 - StatusLine() echo - does not count
- Fire/CharredTreeLifecycle.cs:221 - ReplaceWithCharred(): `if (max > 0f) z.Set(ZDOVars.s_health, Mathf.Max(1f, max * FireConfig.CharredTreeHealthFraction.Value));` sets the newly-charred STANDING tree's ZDO health. Server-gated via CharTree() (see CharredCollapseDelaySeconds) - SERVER
- Fire/CharredTreeLifecycle.cs:325 - CollapseStandingTree(): `if (logProto != null) lz.Set(ZDOVars.s_health, Mathf.Max(1f, LogMaxHealth(logProto.m_health) * FireConfig.CharredTreeHealthFraction.Value));` sets the health of the LOG spawned when a standing charred tree collapses. CollapseStandingTree requires `nv.IsOwner()` on a live instance - CLIENT (the owning peer, not the headless server)

</details>
<details><summary><code>TreeFireDamageEnabled</code> (both)</summary>

- Fire/FireManager.cs:1566 - StatusLine() echo - does not count
- Fire/FireManager.cs:2719 - TickTreeFire(): `if (!FireConfig.TreeFireDamageEnabled.Value) return;` gates the whole unseen-damage-tick pass over burning trees/logs. TickTreeFire is called from Update() only past `if (!ValheimBridge.IsServer()) return;` (line 764) - SERVER, gameplay damage
- Fire/FireVFXController.cs:32 - doc-comment mention only, not a code read
- Fire/FireVFXController.cs:194 - ResolveHealthSource(): `if (!FireConfig.TreeFireDamageEnabled.Value) return;` decides whether the drawn flame column climbs on the tree's real (server-replicated) ZDO health or on the burn-duration clock instead. FireVFXController's own class doc says explicitly 'nothing here is built on a headless server' - CLIENT, purely a visual progress-source choice

</details>
<details><summary><code>TreeFireKillFraction</code> (server)</summary>

- Fire/FireManager.cs:1566 - StatusLine() echo - does not count
- Fire/FireManager.cs:2726 - TickTreeFire(): `float killSeconds = Mathf.Max(1f, FireConfig.BurnDurationSeconds.Value * Mathf.Clamp(FireConfig.TreeFireKillFraction.Value, 0.2f, 1f));` sizes each per-tick damage amount. Server-gated (see TreeFireDamageEnabled) - SERVER

</details>
<details><summary><code>TreeFireTickInterval</code> (server)</summary>

- Fire/FireManager.cs:1566 - StatusLine() echo - does not count
- Fire/FireManager.cs:2721 - TickTreeFire(): `float interval = Mathf.Max(0.5f, FireConfig.TreeFireTickInterval.Value);` sets the real tick cadence. Server-gated (see TreeFireDamageEnabled) - SERVER

</details>
<details><summary><code>TreeRegrowthEnabled</code> (server)</summary>

- Config/FireConfig.cs:248-249 - definition of EffectiveTreeRegrowthEnabled: `!Apocalypse && TreeRegrowthEnabled.Value` (accessor body, not itself a call site)
- Fire/FireManager.cs:1565 - StatusLine() echoes EffectiveTreeRegrowthEnabled in the heartbeat/status text - status-line echo, does not count as a functional reader
- Fire/FireManager.cs:2804 - CharTree(): `if (isTree && FireConfig.EffectiveTreeRegrowthEnabled)` gates whether a felled tree is queued for regrowth. CharTree is only called from ExpireTimers()/TickTreeFire(), which Update() only reaches after `if (!ValheimBridge.IsServer()) return;` at line 764 - SERVER
- Fire/FireManager.cs:2958 - ForceTreeRegrowthNow(): early-out check before forcing the regrowth queue. Called only from Commands/FireDevCommands.cs:780 (FireTreeRegrow), whose very first line is `if (RelayIfClient(args)) return;`, so execution only reaches here on the server (a client typing the command relays it and exits) - SERVER
- Fire/FireManager.cs:3149 - ProcessTreeRegrowth(): early-out gate. Called from Update() at line 805, past the same IsServer gate as CharTree - SERVER
- Commands/FireDevCommands.cs:341 - the 'burntheworld' command's confirmation Say() prints EffectiveTreeRegrowthEnabled as info only; not a functional reader (same status-line caveat, just in a command echo instead of StatusLine())
- Commands/FireDevCommands.cs:773 - FireTreeRegrow(): `if (!FireConfig.EffectiveTreeRegrowthEnabled)` guard, same function/gate as the 2958 call above - SERVER

</details>
<details><summary><code>TreeRegrowthSeconds</code> (server)</summary>

- Fire/FireManager.cs:1565 - StatusLine() echo - does not count
- Fire/FireManager.cs:2810 - CharTree(): `RegrowAt = Time.time + FireConfig.TreeRegrowthSeconds.Value` when enqueuing a PendingRegrowth entry. Same server-gated call chain as TreeRegrowthEnabled (CharTree only runs past the IsServer gate) - SERVER

</details>
<details><summary><code>BarkCharEnabled</code> (client)</summary>

- Fire/FireVFXController.cs:303 - Build(): via EffectiveBarkCharEnabled, whether to collect and drive the bark char-slot property blocks (CharredTreeSkin.CollectCharSlots) for a tree/log burner.
- Fire/FireManager.cs:1569 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>CrownSparksEnabled</code> (client)</summary>

- Fire/FireVFXController.cs:297 - Build(): via EffectiveCrownSparksEnabled, whether to build the ember/spark particle system for a tall tree burner.
- Utils/ValheimBridge.cs:2729 - via EffectiveCrownSparksEnabled, inside the dead CreateProceduralFireVfx cluster; unreachable.
- Fire/FireManager.cs:1561 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>DirtPaintRadius</code> (server)</summary>

- Fire/FireManager.cs:3265 - FlushPendingPaint(): passed as the radius to ValheimBridge.TryPaintScorchedDirtBatch(), the real-terrain writer. FlushPendingPaint runs inside the server-gated main cycle (Fire/FireManager.cs:807, behind `if (!ValheimBridge.IsServer()) return;` at line 764) and is NOT further gated by IsDedicatedServer - runs identically on a headless server or a listen host.

</details>
<details><summary><code>FireShadowsEnabled</code> (client)</summary>

- Fire/FireVFXController.cs:639 - BuildLight(): turns real-time soft shadows on/off for the fire's point light via EffectiveFireShadowsEnabled.
- Fire/FireManager.cs:1569 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>FireSmokeEnabled</code> (client)</summary>

- Fire/FireVFXController.cs:298 - Build(): builds the object-fire smoke particle system (_smoke) when true. FireVFXController is never built on a true dedicated server (its own class doc: 'nothing here is built on a headless server'); it runs on a listen host's own fires and on any client's local + remote-mirror fires.
- Utils/ValheimBridge.cs:2725 - inside CreateProceduralFireVfx(), an older object-fire VFX builder with zero callers anywhere in this codebase (superseded by FireVFXController.Build()); this read is unreachable/dead code, not a second live path.

</details>
<details><summary><code>HeatHazeEnabled</code> (client)</summary>

- Fire/FireVFXController.cs:299 - Build(): whether to build the heat-haze refraction particle system, via EffectiveHeatHazeEnabled (only for a 'full', i.e. not-past-the-tall-cap, fire).
- Fire/FireManager.cs:1569 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>MaxFlameHeight</code> (client)</summary>

- Fire/FireVFXController.cs:176 - MeasureGeometry(): clamps a tall tree's flame height to EffectiveMaxFlameHeight.
- Utils/ValheimBridge.cs:2804 - ResolveFlameHeight(), part of the dead CreateProceduralFireVfx cluster; unreachable.
- Fire/FireManager.cs:1561 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>ScorchMarkLifetimeSeconds</code> (both)</summary>

- Fire/FireManager.cs:3597 - LeaveScorchMark(): decal lifetime when the owning peer draws its own local decal. Same server-gated, listen-host-only-in-effect path as ScorchMarksEnabled.
- Fire/FireManager.cs:3366 - HandleGroundFireSync(): decal lifetime for a client's remote-mirrored decal. Same genuine per-client path as ScorchMarksEnabled.

</details>
<details><summary><code>ScorchMarksEnabled</code> (both)</summary>

- Fire/FireManager.cs:3595 - LeaveScorchMark(): `if (!EffectiveScorchMarksEnabled || IsDedicatedServer()) return;` decides whether the OWNING peer draws its own local ground scorch decal. Called only from server-gated sites (ExtinguishGroundNear <- HandleExtinguishRequest, which checks IsServer() at Fire/FireManager.cs:1028; and ExpireGroundTimers, part of the server-gated main cycle). Explicitly ANDed with !IsDedicatedServer, so dead on a real headless server and alive only if the server is a listen host.
- Fire/FireManager.cs:3361 - HandleGroundFireSync(): `if (EffectiveScorchMarksEnabled && ...)` decides whether THIS peer draws a decal for a cell it is mirroring from the network sync stream. The handler returns immediately when IsServer()==true (line 3319), so this is the genuine per-client reader.
- Commands/FireDevCommands.cs:360 - `fireset lowspec` reply line: echoes EffectiveScorchMarksEnabled for the admin; diagnostic only.

</details>
<details><summary><code>SmoulderAfterFraction</code> (both)</summary>

- Fire/FireManager.cs:2829 - ProcessSmouldering(): `BurnDurationSeconds * Clamp01(SmoulderAfterFraction)` sets when the OWNING peer's own drawn VFX downgrades. Same server-gated, listen-host-only-in-effect path as SmoulderingVfxEnabled.
- Fire/FireManager.cs:2294 - ProcessRemoteSmouldering(): same calculation for the remote-fire mirror. Same genuine-client-only path as SmoulderingVfxEnabled.

</details>
<details><summary><code>SmoulderingVfxEnabled</code> (both)</summary>

- Fire/FireManager.cs:2826 - ProcessSmouldering(): downgrades this peer's own drawn object-fire VFX (_vfx) to a smoulder look. Called from the main cycle at Fire/FireManager.cs:811, inside `if (!ValheimBridge.IsServer()) return;` (line 764) - executes only on the peer acting as server. On a true dedicated server _vfx is always empty (SpawnVfxFor's visual branch is ANDed with !IsDedicatedServer, see UseProceduralVfx/VfxPrefabName below), so this reader is a no-op there; it only has effect if the server role is a listen host viewing its own fire.
- Fire/FireManager.cs:2289 - ProcessRemoteSmouldering(): downgrades the remote-fire mirror (_remoteVfx) this peer draws for fires it does not own. Called at Fire/FireManager.cs:751, BEFORE the IsServer() gate, so it runs on every peer - but _remoteVfx is populated only on a genuine (non-host) client, since HandleFireEventBroadcast (Fire/FireManager.cs:2345) returns immediately when IsServer()==true. This is the real client-side reader.
- Commands/FireDevCommands.cs:327 - `fireset smouldering <bool>`: writes the local ConfigEntry, runs wherever typed.

</details>
<details><summary><code>TallFireMaxConcurrent</code> (client)</summary>

- Fire/FireVFXController.cs:677 - TryReserveTall(): the live tall-fire slot reservation check against FireVFXController's own s_tall list, used from Build().
- Utils/ValheimBridge.cs:2770 - TryReserveTallVfx(), the dead CreateProceduralFireVfx cluster's own separate _tallVfx list; unreachable since nothing calls CreateProceduralFireVfx.
- Fire/FireManager.cs:1561 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>TreeFlameScaling</code> (client)</summary>

- Fire/FireVFXController.cs:175 - MeasureGeometry(): whether a tree's flame column follows its measured height (capped by EffectiveMaxFlameHeight) or stays at TallBurnerMinHeight.
- Fire/FireVFXController.cs:281 - Build(): whether this burner is eligible to count as a 'tall' fire (reserved slot, crown treatment).
- Utils/ValheimBridge.cs:2802 - ResolveFlameHeight(), used only by the dead CreateProceduralFireVfx cluster (see FireSmokeEnabled); unreachable in practice.
- Fire/FireManager.cs:1561 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>UseProceduralVfx</code> (both)</summary>

- Fire/FireManager.cs:2071,2096 - SpawnVfxFor() (object fire, owning peer): part of `wantVisual` and the procedural-vs-prefab branch choice. Server-gated via StartBurning<-TryIgnite, and ANDed with !IsDedicatedServer - dead on a real headless server, listen-host-only in effect.
- Fire/FireManager.cs:1890 - SpawnGroundVfxFor() (ground fire, owning peer): `wantVisual = UseProceduralVfx.Value && !IsDedicatedServer()`. Same server-gated, listen-host-only-in-effect pattern as the object-fire path.
- Fire/FireManager.cs:2474,2481 - SpawnRemoteVfxOnly() (object fire, remote mirror): genuine client-only reader (see VfxPrefabName).
- Fire/FireManager.cs:3534,3536 - SpawnRemoteGroundVfxFor() (ground fire, remote mirror): genuine client-only reader, gates whether this peer builds a procedural ground-fire particle system for a cell it is mirroring; the debug log at 3536 fires when it's off.
- Fire/FireVFXController.cs:143 - Setup(): `if (!UseProceduralVfx.Value) return;` before Build(). Redundant/defensive - a FireVFXController is only ever attached when UseProceduralVfx was already true in one of the spawn functions above - but still executes wherever this component exists (never a true dedicated server).
- Fire/FireManager.cs:1560 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>UseVanillaDirtPaint</code> (server)</summary>

- Fire/FireManager.cs:3575 - LeaveScorchMark(): `if (UseVanillaDirtPaint.Value && !_groundPainted.Contains(key))` queues the cell into _pendingPaint for the batched REAL terrain paint (FlushPendingPaint). Reached only from server-gated call sites and, unlike the scorch-decal check three lines below it, is NOT further gated by IsDedicatedServer - a genuine simulation/terrain effect that runs the same whether the server is headless or a listen host.
- Fire/FireManager.cs:1563 - StatusLine(): echoed only; not counted.

</details>
<details><summary><code>VfxPrefabName</code> (both)</summary>

- Fire/FireManager.cs:2071 - SpawnVfxFor(): folded into `wantVisual`. Server-gated (SpawnVfxFor is only reached from StartBurning <- TryIgnite, server-only), and the whole expression is ANDed with !IsDedicatedServer, so this branch is dead on a real headless server and alive only if the server role is a listen host.
- Fire/FireManager.cs:2116-2118 - SpawnVfxFor(): looks up and spawns the named prefab when UseProceduralVfx is false. Same listen-host-only-in-effect path.
- Fire/FireManager.cs:2474 - SpawnRemoteVfxOnly(): folded into its own `wantVisual`. Called from DrainRemoteObjectVfxQueue() at Fire/FireManager.cs:750, BEFORE the IsServer() gate, so it runs on every peer - but the queue is populated only via network handlers that return early on the server, so in practice this fires only on a genuine (non-host) client.
- Fire/FireManager.cs:2503 - SpawnRemoteVfxOnly(): looks up/spawns the named prefab for the remote-fire mirror. Same genuine-client-only path.
- Fire/FireManager.cs:1560 - StatusLine(): echoed only; not counted as a reader per the status-line rule.

</details>
<details><summary><code>FireDamagePerTick</code> (server)</summary>

- Fire/FireManager.cs:1936 - passed to AttachFireDamageZone (ground-fire zone) inside SpawnGroundVfxFor - server
- Fire/FireManager.cs:2136 - passed to AttachFireDamageZone (object-fire zone) inside SpawnVfxFor - server
- Fire/FireManager.cs:3039 - `float damage = FireConfig.FireDamagePerTick.Value;` in DamagePlayersInFireCore, applied directly to the host's own local player or sent over RPC via ValheimBridge.SendFireDamageToPeer to a remote player's own machine - server

</details>
<details><summary><code>FireDamageTickInterval</code> (server)</summary>

- Fire/FireManager.cs:1936 - passed to AttachFireDamageZone (ground-fire zone) inside SpawnGroundVfxFor - server
- Fire/FireManager.cs:2136 - passed to AttachFireDamageZone (object-fire zone) inside SpawnVfxFor - server
- Fire/FireManager.cs:3037 - `_nextPlayerDamageTick = now + Mathf.Max(0.1f, FireConfig.FireDamageTickInterval.Value);` in DamagePlayersInFireCore, gating how often players are burned - server

</details>
<details><summary><code>FireHurtsEnabled</code> (server)</summary>

- Fire/FireManager.cs:1882 - `bool wantDamage = FireConfig.FireHurtsEnabled.Value && !FireHurtsPlayerOnly.Value && !IsDedicatedServer();` inside SpawnGroundVfxFor, reached only from server-gated ground ignition - server
- Fire/FireManager.cs:2086 - same wantDamage calc inside SpawnVfxFor, reached only from StartBurning, whose own comment states it 'is only ever called on the server (TryIgnite is server-gated)' - server
- Fire/FireManager.cs:3030 - `if (!FireConfig.FireHurtsEnabled.Value) return;` at the top of DamagePlayersInFireCore, called from DamagePlayersInFire at Fire/FireManager.cs:781, itself after the IsServer() gate at line 764 - server

</details>
<details><summary><code>FireHurtsObjectRadius</code> (server)</summary>

- Fire/FireManager.cs:2135 - passed to ValheimBridge.AttachFireDamageZone as the object-fire damage-zone radius, inside SpawnVfxFor (server-only, see FireHurtsEnabled note) - server
- Fire/FireManager.cs:3040 - `float objRadius = FireConfig.FireHurtsObjectRadius.Value;` in DamagePlayersInFireCore, then used by IsStandingInFire to test whether the local host player or a remote player is within range of a burning object - server

</details>
<details><summary><code>FireHurtsPlayerOnly</code> (server)</summary>

- Fire/FireManager.cs:1883 - part of the wantDamage calc in SpawnGroundVfxFor (`&& !FireHurtsPlayerOnly.Value`) - server
- Fire/FireManager.cs:1936 - passed as a parameter to ValheimBridge.AttachFireDamageZone for a ground-fire damage zone, inside SpawnGroundVfxFor - server
- Fire/FireManager.cs:2087 - part of the wantDamage calc in SpawnVfxFor - server
- Fire/FireManager.cs:2136 - passed to AttachFireDamageZone for an object-fire damage zone, inside SpawnVfxFor - server

</details>
<details><summary><code>GroundDamageMaxConcurrent</code> (server)</summary>

- Config/FireConfig.cs:148 - EffectiveGroundDamageMaxConcurrent = LowSpec ? Min(GroundDamageMaxConcurrent.Value, 20) : GroundDamageMaxConcurrent.Value - the accessor itself; evaluated only where the call sites below evaluate it
- Fire/FireManager.cs:1896 - overallCap = Max(EffectiveGroundVfxMaxConcurrent, EffectiveGroundDamageMaxConcurrent) inside SpawnGroundVfxFor, reached only from TryIgniteGroundCell in the spread cycle, which sits after the `if (!ValheimBridge.IsServer()) return;` gate at Fire/FireManager.cs:764 - server
- Fire/FireManager.cs:1923 - `if (wantDamage && _groundVfxDamage.Count >= EffectiveGroundDamageMaxConcurrent) wantDamage = false;` inside SpawnGroundVfxFor - server
- Fire/FireManager.cs:1990 - same cap check inside UpgradeDarkGroundCells, called from Update() at Fire/FireManager.cs:804, also after the IsServer() gate - server

</details>
<details><summary><code>RainGroundBurnDurationMultiplier</code> (server)</summary>

- Fire/FireManager.cs:2607 - `float extra = dt * (1f / Mathf.Max(0.05f, FireConfig.RainGroundBurnDurationMultiplier.Value) - 1f);` inside AgeFiresInRain, scaling how much extra time is subtracted from each rained-on ground cell's ExpireAt - server

</details>
<details><summary><code>RainObjectBurnDurationMultiplier</code> (server)</summary>

- Fire/FireManager.cs:2590 - `float extra = dt * (1f / Mathf.Max(0.05f, FireConfig.RainObjectBurnDurationMultiplier.Value) - 1f);` inside AgeFiresInRain, scaling how much extra time is subtracted from each rained-on burning object's ExpireAt - server

</details>
<details><summary><code>RainSuppressesGroundFire</code> (server)</summary>

- Config/FireConfig.cs:228-229 - `EffectiveRainSuppressesGroundFire => !Apocalypse && RainSuppressesGroundFire.Value;` - the accessor body
- Fire/FireManager.cs:1618 - checked in IgniteAdjacentGroundCells, called only from SpreadPass (Fire/FireManager.cs:823), which sits after the IsServer() gate at line 764 - server
- Fire/FireManager.cs:2605 - checked in AgeFiresInRain, called from Update() at Fire/FireManager.cs:799, also after the IsServer() gate - server

</details>
<details><summary><code>RainSuppressesObjectFire</code> (server)</summary>

- Config/FireConfig.cs:232-233 - `EffectiveRainSuppressesObjectFire => !Apocalypse && RainSuppressesObjectFire.Value;` - the accessor body
- Fire/FireManager.cs:2588 - checked in AgeFiresInRain (server, see RainSuppressesGroundFire entry) - server
- Fire/FireManager.cs:3733 - checked in SpreadPass's object-burner loop, skipping spread from a rained-on burning object to neighbours/ground - server
- Fire/FireManager.cs:3776 - checked in SpreadPass's ground-burner loop, skipping a rained-on ground cell igniting the object above it - server

</details>
<details><summary><code>DousingBombRadius</code> (both)</summary>

- Patches/DousingBombPatches.cs:289 - read once, unconditionally, at the top of ProjectileOnHitDousingPatch.Prefix, on whichever peer owns the thrown projectile ('runs on the thrower's peer only', per the class doc-comment at line 269)
- Patches/DousingBombPatches.cs:290-293 - if that owning peer IS the server (a listen host that threw the bomb), its own local value is used directly via FireManager.Instance.ExtinguishAt
- Patches/DousingBombPatches.cs:294-296 - otherwise (a remote client owns the projectile), that CLIENT's own local value is sent, verbatim, to the server via ValheimBridge.SendExtinguishRequestToServer

</details>
<details><summary><code>DebugLogging</code> (both)</summary>

- Utils/FireLogger.cs:19 - `DebugEnabled => FireConfig.VerboseLogging != null && FireConfig.VerboseLogging.Value`, the single choke point every FireLogger.Debug(...) call passes through
- Consumed identically from purely-server files (e.g. Fire/FireManager.cs simulation methods, Commands/FireDevCommands.cs relay logging) and purely client/visual files (e.g. Fire/FireVFXController.cs, Utils/ValheimBridge.cs) - each peer's own copy governs only its own process's own console/log output

</details>
<details><summary><code>ConfigVersion</code> (both)</summary>

- Config/ConfigMigration.cs:161 - `if (versionEntry.Value < ConfigLedger.CurrentVersion) versionEntry.Value = ConfigLedger.CurrentVersion;` inside Finish(), the forward-only stamp check/write
- Config/FireConfig.cs:961 - `ConfigMigration.Finish(config, ConfigVersion)` at the end of Bind(), run once per process at boot

</details>
