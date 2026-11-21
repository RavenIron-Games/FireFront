<!-- README schema v2 -->
# 🔥 FireFront

![FIREFRONT](https://img.shields.io/badge/FIREFRONT-FIRE_THAT_ACTUALLY_SPREADS-c9a227?style=for-the-badge&labelColor=1b1b1b)
![VERSION](https://img.shields.io/badge/VERSION-1.0.4-8b1e1e?style=for-the-badge&labelColor=1b1b1b)
![CLIENT & SERVER](https://img.shields.io/badge/CLIENT_%26_SERVER-SAME_VERSION_ON_BOTH-1f4e79?style=for-the-badge&labelColor=1b1b1b)

> *Every hearth in Midgard holds a little of Muspelheim, and it has never forgiven the stones that keep it in. Give it one dry wall and a wind at its back, and it will walk to the treeline to settle the score. Sweep your paths. Watch for rain.*

### Torch one wall and the fire can take the whole build, jump to the treeline, and cross open ground to get there.

FireFront makes fire spread in Valheim. Structures, standing trees and felled logs all burn, and fire moves between them and across the grass in between: a real moving front with a windward edge and a burnt-out middle, not one flagged object.

Fire follows the game's own wind, is doused by rain, is stopped by water, survives a server restart and remembers who lit it. When it is over, the forest stands charred, and in time it grows back.

---

![NEW IN 1.0.4](https://img.shields.io/badge/NEW_IN_1.0.4-VALHEIM_1.0.21-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**This version is FireFront rebuilt for Valheim 1.0.21. Nothing else is new: fire spreads, burns and goes out exactly as it did.**

| You | It |
|---|---|
| **Update the server and every player together** | Valheim 1.0.21 uses network version 41, so the server and every player move to it, and to this build, at the same time |

---

![BEFORE YOU INSTALL](https://img.shields.io/badge/BEFORE_YOU_INSTALL-READ_THIS-b8860b?style=for-the-badge&labelColor=1b1b1b)

| | If… | Then… |
|---|---|---|
| 🤝 | you play on a **server** | install FireFront on the server **and** on every player, the same version. The server runs the fire; your game only draws it. Update them together. |
| 🔥 | you care about **your base** | the defaults are tuned aggressive: a torched wall can take the whole build. Keep trees and grass back from walls, or turn off `BurnPlayerBuildings` (`fireset burnbuildings false`) and fire never lights anything a player built. |
| 🛤️ | you run a **dedicated server** | dirt paths and cultivated ground stop ground fire only near the world's centre, in the few zones the server keeps loaded. Everywhere else only water stops it. That is a known gap, not a setting. |
| 🐺 | you expect **creatures to burn** | they do in single player and in a hosted game. A dedicated server has no physical world where creatures stand, so there only players burn. |
| 🖥️ | big fires **cost you frames** | turn on `fireset lowspec true` first. Fire still spreads and still burns your base down; there is just less happening at once. |
| 🌋 | you are in the **Ashlands** | FireFront starts no fire there by default, because the Ashlands' own fire touches wood all the time. The Ashlands' own fire works as it always did. |
| 📏 | you change **`GroundCellSize`** | change it only while no ground fire burns, and not across a restart that saved ground fire. Burning cells move to scaled positions on the new grid. |

---

![I](https://img.shields.io/badge/I-THE_SPREAD-c9a227?style=for-the-badge&labelColor=1b1b1b)

**Fire goes where the fuel and the wind take it.**

| | |
|---|---|
| 🏚️ **Everything wooden burns** | structures, standing trees and felled logs; buildings leave their drops |
| 🔗 **By contact** | structure to structure, tree to tree, structure to tree, within about 8 m of a burning thing |
| 🌾 **Across open ground** | ground fire crawls through the grass between things too far apart to light each other |
| 🌬️ **With the wind** | the game's real wind, direction and strength: a gale drives a long narrow tongue, a calm day burns in a lazy circle |
| ⏳ **At the pace of its fuel** | a tree must be properly alight, about a minute, before it torches its neighbours |
| 📈 **It builds** | a fire starts small and grows to full strength over about ten minutes |
| 🪨 **Spent ground** | burnt ground cannot relight for about 90 seconds, so the front moves on instead of churning in place |
| 🧭 **On a leash** | ground fire does not wander more than about 40 m from where the fire started |

<details>
<summary>📖 <b>Read the saga</b> — why a fire moves the way it does</summary>

**Two kinds of fire work together.** Objects burn on their own clocks and light anything burnable within reach. Between them, an invisible grid of ground cells carries fire over the grass. A burning tree or building lights a wall within about 8 m of it (`SpreadRadius`), and burning ground lights anything within about 4 m (`GroundSpreadRadius`).

**The wind decides the shape.** Ground fire leans downwind and struggles upwind, so the burn scar points the way the wind blew. `fireset windinfluence 0` ignores the wind entirely for an even spread; `firestatus` reports the wind strength the fire is feeling right now.

**Nothing starts at full roar.** A new fire starts at a fraction of its strength and ramps up, and a burning thing must have burned a while before it can pass fire on. That gives you time to react to a small fire, and none at all once it has taken hold.

**The Ashlands are left alone by default.** Catching from every cinder made the biome unplayable. A server that wants fire there turns on `FireInAshlands` (`fireset ashlands true`).

</details>

---

![II](https://img.shields.io/badge/II-THE_BURNING_FOREST-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**Trees burn from the foot up, and leave a charred forest behind.**

| | |
|---|---|
| 🌲 **The fire climbs** | a burning tree takes unseen fire damage, with no floating numbers, and the flames climb the trunk as it weakens; flames in the crown mean a tree about to go |
| 🖤 **Charred in place** | a tree the fire kills becomes a black, bare, ash-dusted husk of the same species, with ember pockets glowing in the bark's splits |
| 🪵 **Falls or stands** | three seconds later it either collapses with the game's own felling crash and crumbles to ash, or stays standing as a snag you can chop |
| ⚫ **Coal, not wood** | a charred tree drops a little coal; charred wood never drops wood or seeds and never catches fire again |
| 💨 **Smoke after the fire** | thin smoke rises off charred trunks for a while after the flames are gone |
| 🌱 **The forest returns** | a collapsed tree grows back after about fifteen minutes, if the spot is still clear |

<details>
<summary>📖 <b>Read the saga</b> — how the char is made</summary>

**Built from the tree's own bark.** Vanilla ships no burnt trees, so FireFront makes each species' charred bark on your machine the first time a tree of that species chars: real char plates split by fissures, a few scattered pockets of embers that glow and fade over a couple of minutes.

**You choose how much survives.** `fireset treedestruction <0-100>` is the chance a dead tree collapses rather than standing as a snag. `charredember`, `charredembercover` and `charredsmokeseconds` tune the look on your own game.

</details>

---

![III](https://img.shields.io/badge/III-FIGHTING_A_FIRE-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**A large fire is meant to be fought, not watched.**

| You | It |
|---|---|
| **Press G** | puts out what you are looking at, plus the ground fire around you |
| **Throw a Dousing Bomb** | puts out everything within about 6 m of where it lands, ground fire and burning buildings alike |
| **Craft Dousing Bombs** | by hand, anywhere: 3 Resin + 2 Leather scraps makes 3 |
| **Douse a line** | anything put out stays soaked for about 90 seconds and cannot relight, so a line of bombs cuts a real break ahead of the front |
| **Build by water** | water is a firebreak: ground fire never crosses it |
| **Pave the yard** | in single player or a hosted game, dirt paths and cultivated ground carry no ground fire, so a fire crawling in stalls at the edge of a levelled, pathed yard |
| **Wait for rain** | rain douses fire, buildings included: a burner in the rain stops passing fire on at once and burns out in about a third of its normal time |

<details>
<summary>📖 <b>Read the saga</b> — what a firebreak can and cannot do</summary>

**A firebreak stops fire moving over the ground, and only that.** A burning tree or building still lights a wall within about 8 m of it, and burning ground lights anything within about 4 m. Keep trees and grass that far back from the walls you want to keep.

**Paths and fields depend on where the server can see.** On a dedicated server the paths and cultivated ground hold only near the world's centre, in the few zones the server itself keeps loaded. Everywhere else the server cannot see the ground, so only water counts for now.

**Rain stops spread, not ignition.** A torch, a fire arrow or lightning still lights something in a downpour.

</details>

---

![IV](https://img.shields.io/badge/IV-THE_AFTERMATH-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

**Fire has consequences, and it keeps them.**

| | |
|---|---|
| 🩸 **It hurts** | standing in fire burns you, through vanilla's own burn effect, on every kind of server |
| ♨️ **It warms** | standing near fire holds off Cold and Freezing exactly as a campfire does, so you cannot freeze to death inside a burning forest |
| 🟫 **Burn scars** | burnt ground leaves a dark scorch mark that fades after about five minutes |
| ⛏️ **Lasting dirt, if you want it** | `fireset dirtpaint true` also lays real bare dirt where a player is near the fire; it is saved with the world for good, which is why it is off |
| 👀 **Join mid-blaze** | a player who connects during a fire sees it: burning buildings and trees arrive as a snapshot, with how long each has burned |
| 💾 **Survives a restart** | burning things come back burning with their remaining time, spent ground stays spent, and trees waiting to regrow still do |
| 🕵️ **Remembers who lit it** | each fire records the player whose hit set it alight, and fire that spreads keeps that name |

<details>
<summary>📖 <b>Read the saga</b> — who lit it, and where that is kept</summary>

**One fire at a time.** The player whose hit sets an object alight is recorded on it, and fire that spreads, to other objects or to the ground around them, keeps the igniter of the fire it came from. Two fires burning at once on the same server are attributed separately. A fire that lightning or a creature starts, or one lit with the admin commands, records nobody.

**Saved with the fire.** The igniter is written with the rest of the fire state, in `firefront_fires_<world id>.txt` in the server's `worlds_local` folder, so it survives a restart. A save from an older build restores with the igniter unknown, and with `PersistFiresEnabled` off nothing is written.

**Nothing shows it in game.** A companion mod can read it for its reputation system, fire by fire rather than one player for the whole server.

</details>

---

![V](https://img.shields.io/badge/V-THE_LOOK-5b2c83?style=for-the-badge&labelColor=1b1b1b)

**Fire that sits next to a campfire without looking like another game.**

| | |
|---|---|
| 🔥 **Built from the game's own fire** | flipbook flames, ember motes, lit smoke, heat shimmer and a soft-shadowed light, made from the game's own fire materials |
| 🌲 **Tall fire** | flames climb a tall burner as a column instead of one small plume at its base, with sparks falling off the crown |
| 🖤 **Bark char** | the bark below the fire front blackens and glows with embers as the flames climb |
| 🌫️ **Smouldering** | a fire that has burned a while drops to embers and smoke |
| 🎛️ **Your frames, your choice** | flames, smoke, sparks, shadows and char are drawn by your own game; turn any of them down without changing the fire for anyone else |

<details>
<summary>📖 <b>Read the saga</b> — the ceiling</summary>

**It is not an authored asset.** The fire is assembled in code from the game's own fire materials and particle recipes, and the charred bark is generated the same way, from each species' own bark texture.

**No ray tracing.** No mod can add it to Valheim: the game runs the built-in render pipeline with no ray-tracing support compiled in. Shadow-casting fire lights, HDR bloom, normal-mapped char, lit smoke and heat haze are the ceiling, and FireFront uses all of them.

</details>

---

![VI](https://img.shields.io/badge/VI-TWO_SWITCHES-b8860b?style=for-the-badge&labelColor=1b1b1b)

**One switch for a struggling machine, one for watching the world burn.**

| You | It |
|---|---|
| **`fireset lowspec true`** | fewer things burning at once, fewer visuals, no scorch marks, a slower spread tick. Fire still spreads and still burns your base down; there is less happening at once |
| **`fireset burntheworld true`** | fire catches instantly, paths and even water stop stopping it, rain does not put it out, burnt ground relights, fires start at full strength, nothing grows back, and the caps go to maximum per fire, so several blazes rage at once |
| **`fireset burnbuildings false`** | anti-grief: fire never ignites anything a player placed, by spread, by fire arrows, by anything, while ruins and world structures still burn |

<details>
<summary>📖 <b>Read the saga</b> — what the presets touch</summary>

**Both leave your own settings intact.** Anything you have already set lower is kept, and turning a preset off restores what you had. `firestatus` shows exactly what is in force. If both are on, lowspec wins.

**Burn the world leaves the visuals alone** on purpose: those are what cost frames, so you still choose how much your machine draws. Lowspec is also in the config file as `LowSpecPreset`, and burn the world as `WatchTheWorldBurn`.

</details>

---

![THE TOOLBOX](https://img.shields.io/badge/THE_TOOLBOX-QUICK_START_%C2%B7_KEYS_%C2%B7_HOW--TO-1b1b1b?style=for-the-badge&labelColor=8b1e1e)

**First ten minutes:** install with your mod manager, on the server too → find a quiet stand of trees → light one with a torch or a fire arrow → watch the wind take it → press **G**, or craft Dousing Bombs (3 Resin + 2 Leather scraps) and throw a line ahead of the front.

Press `` ` `` to open the console.

| Key | | Command | |
|---|---|---|---|
| **G** | put out what you look at, and the fire around you | `firestatus` | what is burning, and every setting in force |
| | | `ignite` | light what is under your crosshair |
| | | `startfire [radius]` | light everything burnable around you |
| | | `stopfire` | put out what is under your crosshair |
| | | `clearfires` | every fire out, at once |
| | | `fireset <key> <value>` | change a setting live, no restart |
| | | `firedebug` | verbose logging on or off |

<details>
<summary>🛠️ <b>Who may run what on a server</b></summary>

`ignite`, `startfire` and `clearfires` run on the server no matter where you type them, only for players on the server's own admin list, and the reply comes back to your console marked `[server]`.

`fireset` changes the copy on your own machine and asks the server, which applies it only for its admins, whether typed or moved from a config manager; its answer comes back the same way. `firestatus` asks the server too and needs no admin.

`stopfire` puts out only the one thing under your crosshair, unlike **G**, which also clears the fire around you. It checks the admin flag your own game holds, which can say no even to a real admin on a dedicated server; **G** needs no admin. `firedebug` turns verbose logging on or off on your own machine; on a client connected to a server it also sends the new value to the server like `fireset debug`, which the server applies only for its admins.

</details>

<details>
<summary>🎛️ <b>Tune the fire with <code>fireset</code></b></summary>

Every setting except the extinguish key can be changed live, no restart. Settings the server reads change there only for its admins. Visual settings are read by each player's own game, so `fireset` changes them for whoever types it and nobody else.

| Area | Keys |
|---|---|
| Core | `enabled`, `burnduration`, `firematurity`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `burnbuildings`, `ashlands`, `maxkills` |
| Ground fire | `groundenabled`, `groundcellsize`, `groundradius`, `groundburnduration`, `groundmax`, `groundvfxmax`, `grounddamagemax`, `groundleashenabled`, `groundleashdistance` |
| Damage and warmth | `firehurts`, `firehurtsplayeronly`, `firehurtsradius`, `firedamage`, `firetickinterval`, `firewarmth`, `firewarmthradius` |
| Putting it out | `extinguishradius`, `dousingradius`, `douseimmunity`, `rainsuppress`, `rainmultiplier`, `rainobjects`, `rainobjectmultiplier`, `firebreaks`, `waterblocks` |
| Wind | `windbias`, `windupwindchance`, `windinfluence` |
| Aftermath | `scorchmarks`, `scorchlifetime`, `dirtpaint`, `dirtpaintradius`, `exhaustionenabled`, `fuelregrow`, `treeregrowth`, `treeregrowthseconds`, `persistfires` |
| Tree fire and charring | `treefire`, `treetick`, `treekillfraction`, `treedestruction`, `charreddelay`, `charredcoalmin`, `charredcoalmax`, `charredhealth`, `charredcrumble`, `charredglow`, `charredember`, `charredembercover`, `charredsmoke`, `charredsmokeseconds` |
| Ramp | `rampenabled`, `rampduration`, `rampstart` |
| Visuals | `vfx`, `procedural`, `firesmoke`, `maxflameheight`, `treeflames`, `crownsparks`, `tallfiremax`, `fireshadows`, `heathaze`, `barkchar` |
| Smouldering | `smouldering`, `smoulderafter` |
| Presets and debug | `lowspec`, `burntheworld`, `debug` |

Worth playing with: `fireset windinfluence 0` ignores the wind for an old-style even spread; `1`, the default, is full effect.

</details>

<details>
<summary>🌧️ <b>Test rain on a dedicated server</b></summary>

Type `fireweather force Rain`, then `fireweather reset` when you are done (admins only). Vanilla's `env` changes the weather on your own game only, so the server's fire never sees it.

</details>

<details>
<summary>🧪 <b>Test and diagnostic commands</b></summary>

**They change the world** (on the server, for its admins only, like `startfire`): `firegroundignite [radius]` starts real ground fire around you; `firetreeregrow` makes every waiting tree regrowth try now.

**They change nothing:** `firetreeregrowlist` lists the server's waiting tree regrowth (admins only). `fireweather` shows the weather FireFront uses at your position as your game sees it, and as the server sees it if you are one of its admins. `firelistprefabs`, `firecheckprefab` and `fireinspecteffectarea` read prefab and effect details on your own machine. `firedumptex` writes the charred-wood textures your game generated as PNG under `BepInEx/config/FireFront-textures/`.

**Leave this one alone:** `firepurgevfx` is an old emergency cleanup. It deletes every vanilla fire object in your own game's scene, real Ashlands fires included, so use it only if you are asked to.

</details>

<details>
<summary>🐞 <b>Report a problem</b></summary>

Send `LogOutput.log` from the BepInEx folder, especially if you see a wall of repeating red. Screenshots of odd spread help a great deal: "this jumped further than it should have" is far easier to diagnose from a picture. For wind, a shot of the burn scar plus the wind direction is exactly the evidence needed. Frametime graphs are gold.

</details>

<details>
<summary>🧰 <b>Build it from source</b></summary>

Needs the .NET SDK; the mod targets `net472`.

```powershell
.\tools\fetch-libs.ps1     # populate libs\ from your local Valheim install, once per machine
dotnet build FireFront.csproj -c Release
```

`libs\` is not in the repository on purpose: the game assemblies are not ours to redistribute. `fetch-libs.ps1` finds Steam by itself; pass `-ValheimPath` to override. `tools\run-tests.ps1` runs the off-game tests, `tools\package.ps1` builds the release zip into `dist\` (and refuses if the version in `Plugin.cs`, `FireFront.csproj` and `manifest.json` disagree), and `tools\start-test-server.ps1` / `stop-test-server.ps1` drive a local dedicated test server.

The mod compiles against a publicized copy of the game assembly, so any vanilla member compiles whatever its real accessibility and fails only at runtime. To check whether something is public, decompile the shipping assembly from a real install, never the copy in `libs\`.

</details>

---

![MULTIPLAYER](https://img.shields.io/badge/MULTIPLAYER-THE_SERVER_RUNS_THE_FIRE-1f4e79?style=for-the-badge&labelColor=1b1b1b)

| A player without FireFront | Decided by the server | Needs FireFront on both ends |
|---|---|---|
| can still join, and is told on screen to install the same version as the server · fire will not show or sync correctly for them | the whole fire: spread, ground fire, wind, rain, damage, charring, regrowth and the fire save · who may run `ignite`, `startfire`, `clearfires` and `fireset` | drawing the fire · burning and warming each player · the extinguish key and Dousing Bombs · the snapshot a player gets on joining mid-blaze |

**Versions are compared at connect.** Same version: nothing to see. A different version that still works together: a notice in the corner. A version that does not: a message in the middle of the screen, and both logs say which versions met. A player or server with an older FireFront, or none, is caught too. Update the server and its players together.

---

![COMPATIBILITY](https://img.shields.io/badge/COMPATIBILITY-PLAYS_WELL_WITH_OTHERS-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Mod | |
|---|---|
| **Ragnarok's Wrath** | Reads where FireFront's fires burn and who lit each one, and its storm lightning starts FireFront fires. |
| **A config manager** | Moving a setting there works like typing `fireset`: the server applies it only for its admins. |

---

![SETTINGS](https://img.shields.io/badge/SETTINGS-BEPINEX_CONFIG_%C2%B7_FIRESET-b8860b?style=for-the-badge&labelColor=1b1b1b)

All safe to leave alone. They are in `BepInEx/config/com.raveniron.firefront.cfg`, and every one except `ExtinguishKey` can be changed live with `fireset`. Every key is written to both the server's file and each player's, but most are read on one side only. Nothing pushes the server's values to players. The file updates itself between versions: a value you never touched follows a changed default, and a value you chose is kept and named in the log.

<details>
<summary>🔒 <b>On the server</b> — the server's copy decides; change it there, or with <code>fireset</code> as one of its admins</summary>

| Section | Setting | What it does |
|---|---|---|
| General | `Enabled` | master switch: no burn timers, no spread |
| General | `WatchTheWorldBurn` · `LowSpecPreset` | the two presets (lowspec also trims each player's own visuals) |
| General | `MaxKillsPerCycle` · `PersistFiresEnabled` | how many things fire may destroy per cycle; whether fire survives a restart |
| Fire | `BurnDurationSeconds` · `SpreadMaturityFraction` · `SpreadRadius` | how long a piece burns, how long before it spreads, and how far |
| Fire | `MaxConcurrentBurning` · `QueueSize` · `SpreadCheckInterval` | how many things burn at once, how many wait, how often spread is checked |
| Fire | `BurnTreesAndLogs` · `BurnPlayerBuildings` · `FireInAshlands` | what may catch fire, and where |
| Fire | `TreeDestructionRate` | the chance a dead tree collapses rather than standing as a snag |
| Fire | `FireRampEnabled` · `FireRampDurationSeconds` · `FireRampStartFraction` | how a new fire builds to full strength |
| Ground | `GroundSpreadEnabled` · `GroundCellSize` · `GroundSpreadRadius` · `GroundBurnDurationSeconds` · `GroundMaxConcurrent` | ground fire: on or off, cell size, reach, burn time, cap |
| Ground | `GroundFuelExhaustionEnabled` · `GroundFuelRegrowSeconds` | how long burnt ground stays spent |
| Ground | `GroundFirebreaksEnabled` · `GroundWaterBlocksSpreadEnabled` | paths, fields and water as firebreaks |
| Ground | `WindSpreadBiasEnabled` · `WindUpwindIgniteChance` · `WindInfluence` | how much the wind shapes the fire |
| Ground | `GroundMaxSpreadDistanceEnabled` · `GroundMaxSpreadDistance` | the leash on ground fire |
| Damage | `FireHurtsEnabled` · `FireHurtsPlayerOnly` · `FireHurtsObjectRadius` · `FireDamagePerTick` · `FireDamageTickInterval` · `GroundDamageMaxConcurrent` | who fire hurts, how much and how often |
| Weather | `RainSuppressesGroundFire` · `RainGroundBurnDurationMultiplier` · `RainSuppressesObjectFire` · `RainObjectBurnDurationMultiplier` | what rain does to ground fire and to burning things |
| Trees | `TreeFireDamageEnabled` · `TreeFireTickInterval` · `TreeFireKillFraction` | how a burning tree loses health |
| Trees | `CharredCollapseDelaySeconds` · `CharredTreeHealthFraction` | how long a charred tree stands before it falls, and how tough a standing snag is |
| Trees | `TreeRegrowthEnabled` · `TreeRegrowthSeconds` | whether and when burnt trees grow back |
| Visuals | `UseVanillaDirtPaint` · `DirtPaintRadius` | lasting bare dirt where ground fire burned |
| Controls | `DouseImmunitySeconds` | how long something put out stays soaked |
| Controls · Items | `ExtinguishGroundRadius` · `DousingBombRadius` | the most a player's **G** or Dousing Bomb may clear; each player's own copy asks, up to this |

</details>

<details>
<summary>🖥️ <b>In your own game</b> — yours alone</summary>

| Section | Settings |
|---|---|
| **Controls** | `ExtinguishKey` (default **G**) · `ExtinguishGroundRadius` |
| **Items** | `DousingBombRadius` |
| **Fire** | `FireKeepsYouWarm` · `FireWarmthRadius` |
| **Visuals** | `UseProceduralVfx` · `VfxPrefabName` · `FireSmokeEnabled` · `TreeFlameScaling` · `CrownSparksEnabled` · `MaxFlameHeight` · `TallFireMaxConcurrent` · `FireShadowsEnabled` · `HeatHazeEnabled` · `BarkCharEnabled` · `SmoulderingVfxEnabled` · `SmoulderAfterFraction` · `ScorchMarksEnabled` · `ScorchMarkLifetimeSeconds` |
| **Ground** | `GroundVfxMaxConcurrent` |
| **Trees** | `CharredEmberIntensity` · `CharredEmberCoverage` · `CharredEmberGlowSeconds` · `CharredSmokeEnabled` · `CharredSmokeSeconds` · `CharredLogCrumbleSeconds` · `CharredCoalMin` · `CharredCoalMax` (read by the game that owns the charred tree when it falls, not the server) |
| **General** | `LowSpecPreset` (your own visuals) |
| **Debug** | `DebugLogging` |

</details>

---

![CREDITS](https://img.shields.io/badge/CREDITS-THANK_YOU-1b1b1b?style=for-the-badge&labelColor=c9a227)

**Needs:** [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)

- **Wu'barrk**: visual effects, and the config-migration machinery FireFront's own is built on, by way of Wings of the Valkyrie and Valkyrie's Cargo.
- **Support Raven Iron.** Every Raven Iron mod is free, and stays free: all of it, always. Nothing is held back for patrons, and nothing ever will be. To help cover server hosting and test hardware: the [website](https://ravenirongames.com/) · [Patreon](https://www.patreon.com/cw/RavenIronGames) · [Discord](https://discord.gg/AGKDEurAVa), a channel per mod and where the testing happens.

*A Raven Iron mod.* **License:** MIT. Use it, fork it, repackage it, ship it in a modpack; keep the copyright notice with it. The full text is in `LICENSE`.
