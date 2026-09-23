# FireFront

A Valheim mod that makes fire spread. Torch a wall and it can take the whole build with it, jump to the treeline, and crawl across open ground to get there — an actual moving front with a windward edge and a burnt-out middle, not one flagged object.

Fire is wind-driven, doused by rain, stopped by water (and by dirt paths in a hosted game), survives server restarts, and remembers who lit it.

- [Requirements](#requirements)
- [Installation](#installation)
- [Features](#features)
- [Fighting a fire](#fighting-a-fire)
- [Dedicated servers](#dedicated-servers)
- [Presets](#presets)
- [Console commands](#console-commands)
- [Configuration](#configuration)
- [Building from source](#building-from-source)
- [Reporting problems](#reporting-problems)
- [Licence](#licence)
- [Credits](#credits)
- [Support Raven Iron](#support-raven-iron)

## Requirements

| | |
|---|---|
| Valheim | 1.0.15 (built and tested against; older builds are not supported) |
| BepInEx | BepInExPack Valheim 5.4.2350 or newer |

## Installation

Drop `FireFront.dll` into `BepInEx/plugins/` and launch. Nothing needs configuring to play.

On a dedicated server, **the server needs the dll too** — it runs the simulation and your client only draws it. Update server and client together. Since 0.24 a mismatch is not silent: at connect the two compare versions, and if they do not work together the player sees it on screen and both logs say which versions met. A client or server older than 0.24 is detected too.

## Features

**Spread**

- Structures, standing trees and felled logs all burn. Buildings leave their drops; trees and logs burn to charcoal (see below).
- Fire climbs a tree by the tree's health. A burning tree takes unseen fire damage — no floating numbers — and the fire front starts at the foot and climbs the trunk as the tree weakens, the bark blackening and glowing with embers below it. Flames in the crown mean a tree about to go.
- Fire spreads by contact — structure to structure, tree to tree, structure to tree — and across open ground between things too far apart to light each other directly.
- Spread follows the game's real wind, both direction and strength. A gale drives a long narrow tongue; a calm day burns in a lazy circle.
- Fire spreads at the pace of its fuel. A tree must be properly alight, roughly a minute, before it torches its neighbours. Fires start small and build over about ten minutes rather than instantly raging.
- Burnt ground is spent for about 90 seconds, so the front advances instead of churning in place.

**Consequences**

- Standing in fire hurts, through vanilla's own burn effect. Players burn on every kind of server; creatures burn in a hosted game only, because a dedicated server has no physical world where they stand.
- Standing near fire keeps you warm — it holds off Cold and Freezing exactly as a campfire does, so you cannot freeze to death inside a burning forest.
- A tree the fire kills is charred in place: a black, bare, ash-dusted husk of the same species — real char plates split by fissures, built from that species' own bark — with a few scattered pockets of embers that glow in the splits and fade over a couple of minutes, and thin smoke off the trunk for a while after. Three seconds later it either collapses — the charred trunk falls with the game's own felling crash, drops a little coal, smokes where it lies and crumbles to ash — or stays standing as a snag you can chop later for the same coal. Charred wood never drops wood or seeds and never catches fire again. `fireset treedestruction <0-100>` is the collapse chance; `charredember`, `charredembercover` and `charredsmokeseconds` are the look.
- Join a server mid-blaze and you see the blaze: burning buildings and trees are sent to a connecting player as a snapshot, with how long each has burned.
- Burnt ground leaves a dark scorch mark that disappears after about five minutes. For a lasting mark, `fireset dirtpaint true` also lays real bare dirt where a player is near the fire; that dirt is saved with the world and never goes away, which is why it is off by default. Collapsed trees regrow after about fifteen minutes if the spot is still clear.
- Fires remember who lit them. When a player starts a fire, their player id goes with the whole front, even after it crawls a long way from the first spark; natural and creature-lit fire belongs to nobody. That id is saved to disk with the rest of the fire state, in `firefront_fires_<world id>.txt` in the server's `worlds_local` folder, so it survives a restart; with `persistfires` off nothing is written. Nothing surfaces in game yet: it feeds a companion mod's reputation system.
- Fires survive a server restart. Burning things come back burning with their remaining time, spent ground stays spent, and trees still waiting to regrow still do.

## Fighting a fire

A large fire is meant to be fightable rather than something you stand beside and hope.

- **Water is a real firebreak, and so are dirt paths and cultivated ground in a hosted game.** Where that applies it protects a base more than you would expect: the levelled, pathed ground most bases sit on counts as fuel-free, so ground fire crawling in from outside stalls at the edge of the yard. It stops only fire moving over the ground. A burning tree or building still lights a wall within about 8 m of it (`spreadradius`), and burning ground lights anything within about 4 m (`groundradius`), so keep trees and grass that far back from walls you want to keep. On a dedicated server that only holds near the world's centre, in the few zones the server itself keeps loaded; everywhere else the server cannot see the ground, so only water counts for now. That is a known gap, not a setting.
- **Rain douses fire, buildings included.** A burner caught in the rain stops passing fire on immediately and burns out in about a third of its normal time. Rain stops *spread*, not ignition — a torch, a fire arrow or lightning still lights something in a downpour.
- **Press `G`** to extinguish what you are looking at plus the fire around you, ground fire and burning structures alike.
- **The Dousing Bomb** puts out everything within about 6 m of where it lands. Hand-craftable anywhere and cheap on purpose: 3 Resin + 2 Leather scraps makes 3.
- Anything extinguished stays soaked for about 90 seconds and cannot relight, so a line of bombs cuts a real break ahead of the front instead of the fire refilling the hole behind you.
- Ground fire will not wander more than about 40 m from where the fire started.

## Dedicated servers

Fire is simulated entirely on the server. Several things that worked when hosting from your own game did **not** work on a dedicated server, silently, for a long time. If you tried FireFront on a server before and it felt tame, inert or oddly patchy, that was this — all of it is fixed:

| Symptom | Cause |
|---|---|
| Forest fires barely spread | The server could not see trees at all |
| Standing in fire never hurt | The server has no physical world where players stand |
| Burnt trees never came back | Regrowth never planted anything |
| Visible fire did not match the real fire | Ground fire was drawn from a running list of changes that was never reconciled |

**Anti-grief:** `fireset burnbuildings false` (or `BurnPlayerBuildings` in the config) means fire never ignites anything a player placed — not by spread, not by fire arrows, not by anything — while ruins and world structures still burn.

## Presets

**`fireset burntheworld true`** — fire catches instantly, dirt paths and even water stop stopping it, rain does not put it out, burnt ground relights, fires start at full strength, nothing grows back, and the caps go to maximum *per fire*, so several blazes can rage at once. Visual settings are left alone deliberately: those are what cost frames, so you still choose how much your machine draws.

**`fireset lowspec true`** — the first thing to reach for if big fires cost you frames. Fewer things burning at once, fewer visuals, no scorch decals, a slower spread tick. Fire still spreads and still burns your base down; there is simply less happening at once. Also available as `LowSpecPreset` in the config file.

Both leave your own settings intact — anything you have already set lower is kept, and turning a preset off restores what you had. `firestatus` shows exactly what is in force. If both are on, lowspec wins.

## Console commands

Press `` ` `` to open the console.

| Command | Does |
|---|---|
| `firestatus` | What is burning right now, plus every current setting |
| `ignite` | Ignite whatever is under your crosshair |
| `startfire [radius]` | Ignite everything burnable within radius of you |
| `stopfire` | Extinguish whatever is under your crosshair |
| `clearfires` | Every active fire out, instantly |
| `firedebug` | Toggle verbose logging |
| `fireset <key> <value>` | Live-tune any setting, no restart; a change to the server needs you on its admin list |

`ignite`, `startfire` and `clearfires` run on the server no matter where you type them, only for players on the server's own admin list, and the reply comes back to your console prefixed `[server]`. `fireset` changes the copy on your own machine and asks the server, which applies it only for its admins, whether typed or moved from a config manager; its answer comes back the same way. `firestatus` asks the server too and needs no admin. `stopfire` checks the admin flag your own game received from the server, then sends the same extinguish request the `G` key does. `firedebug` only changes logging on the machine you type it on.

Test commands that change the world, run on the server for its admins only, like `startfire`: `firegroundignite [radius]` starts real ground fire around you, and `firetreeregrow` makes every pending tree regrowth try now.

Diagnostic commands that change nothing: `firetreeregrowlist` lists the server's pending tree regrowth (admins only); `fireweather` shows the weather FireFront uses at your position as your game sees it, and as the server sees it if you are one of its admins; `firelistprefabs`, `firecheckprefab` and `fireinspecteffectarea` read prefab and effect details on your own machine; `firedumptex` writes the charred-wood textures your game generated as PNG under `BepInEx/config/FireFront-textures/`. `firepurgevfx` removes stray vanilla fire effects from your own game's scene only.

To test rain on a dedicated server, `fireweather force Rain` and then `fireweather reset` (admins only). Vanilla's `env` changes the weather on your own game only, so the server's fire never sees it.

## Configuration

Everything is in `BepInEx/config/com.raveniron.firefront.cfg`. Every setting except `ExtinguishKey` is live-tunable with `fireset` by a server admin, no restart required; `ExtinguishKey` (default `G`) is each player's own keybind, set in their own file. The config file migrates itself between versions: when a default changes, a value you never touched follows it, and a value you chose is kept and named in the log.

Every key is written to both the server's file and each client's, but most are read on one side only, and two presets (`lowspec`, `burntheworld`) silently override keys that still read true. [docs/CONFIG-KEY-MAP.md](docs/CONFIG-KEY-MAP.md) says, for all 81 keys, which side reads it and what overrides it.

`fireset` keys:

| Area | Keys |
|---|---|
| Core | `enabled`, `burnduration`, `firematurity`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `burnbuildings`, `maxkills` |
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

Worth playing with: `fireset windinfluence 0` ignores wind entirely for old-style even spread; `1` is full effect and the default. `firestatus` reports the live wind strength the fire is currently feeling.

## Building from source

Requires the .NET SDK. The mod targets `net472`.

```powershell
.\tools\fetch-libs.ps1     # populate libs\ from your local Valheim install, once per machine
dotnet build FireFront.csproj -c Release
```

`libs\` is gitignored deliberately — the game assemblies are not ours to redistribute. `fetch-libs.ps1` auto-detects Steam; pass `-ValheimPath` to override.

| Script | Does |
|---|---|
| `tools\fetch-libs.ps1` | Populates `libs\` from a local Valheim install |
| `tools\run-tests.ps1` | Runs the off-game test harness under `tests\` |
| `tools\package.ps1` | Builds the release zip into `dist\`. Refuses if the version in `Plugin.cs`, `FireFront.csproj` and `manifest.json` disagree |
| `tools\start-test-server.ps1` / `stop-test-server.ps1` | Drive a local dedicated test server |

A note for contributors: the mod compiles against a *publicized* copy of the game assembly, so any vanilla member compiles regardless of its real accessibility and fails only at runtime. When checking whether something is public, decompile the **shipping** assembly from a real install, never the publicized copy in `libs\`.

## Reporting problems

Send `LogOutput.log` from the BepInEx folder, especially if you see a wall of repeating red.

Screenshots of odd spread are genuinely useful — "this jumped further than it should have" is far easier to diagnose from a picture. For wind specifically, a shot of the burn scar plus the wind direction is exactly the evidence needed. Frametime graphs are gold; more than one stutter has been found and fixed from a tester's clip.

Known limits:

- The fire visual is assembled in code from the game's own fire materials and particle recipes (flipbook flames, ember motes, lit smoke, heat shimmer, a soft-shadowed light), so it should sit next to a campfire without looking like another game — but it is not an authored asset. The charred bark is generated the same way, from each species' own bark texture, on your machine the first time a tree of that species chars.
- Ray-traced lighting is not something any mod can add to Valheim: the game runs the built-in render pipeline with no ray-tracing support compiled in. Shadow-casting fire lights, HDR bloom, normal-mapped char, lit smoke and heat haze are the ceiling, and all of them are used.
- Defaults are tuned aggressive. Expect fire to spread fast and hungrily unless you dial it down.

## Licence

MIT; see [LICENSE](LICENSE). Use it, fork it, repackage it, ship it in a modpack; keep the copyright notice with it.

## Credits

**Wu'barrk** — visual effects, and the config-migration machinery FireFront's own is built on, by way of Wings of the Valkyrie and Valkyrie's Cargo.

---

## Support Raven Iron

Every Raven Iron mod is free, and stays free — all of it, always. Nothing is held
back for patrons, and nothing ever will be.

If you'd like to help cover server hosting and test hardware:

- **Website** — <https://ravenirongames.com>
- **Patreon** — <https://www.patreon.com/cw/RavenIronGames>
- **Discord** — <https://discord.gg/AGKDEurAVa> — a channel per mod, and where the
  testing happens
