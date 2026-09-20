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
- [Credits](#credits)

## Requirements

| | |
|---|---|
| Valheim | 1.0.15 (built and tested against; older builds are not supported) |
| BepInEx | BepInExPack Valheim 5.4.2350 or newer |

## Installation

Drop `FireFront.dll` into `BepInEx/plugins/` and launch. Nothing needs configuring to play.

On a dedicated server, **the server needs the dll too** — it runs the simulation and your client only draws it. Update server and client together: a mixed pair can leave ignition silently doing nothing.

## Features

**Spread**

- Structures, standing trees and felled logs all burn, with real drops. Trees fall through vanilla's own felling and leave logs; logs leave wood.
- Fire spreads by contact — structure to structure, tree to tree, structure to tree — and across open ground between things too far apart to light each other directly.
- Spread follows the game's real wind, both direction and strength. A gale drives a long narrow tongue; a calm day burns in a lazy circle.
- Fire spreads at the pace of its fuel. A tree must be properly alight, roughly a minute, before it torches its neighbours. Fires start small and build over about ten minutes rather than instantly raging.
- Burnt ground is spent for about 90 seconds, so the front advances instead of churning in place.

**Consequences**

- Standing in fire hurts, players and creatures alike, through vanilla's own burn mechanic.
- Standing near fire keeps you warm — it holds off Cold and Freezing exactly as a campfire does, so you cannot freeze to death inside a burning forest.
- Burnt ground leaves scars. Burnt trees regrow after about fifteen minutes if the spot is still clear.
- Fires remember who lit them. The whole front carries its arsonist even after crawling a long way from the first spark; natural and creature-lit fire belongs to nobody. Nothing surfaces in game yet — it feeds a companion mod's reputation system.
- Fires survive a server restart. Burning things come back burning with their remaining time, spent ground stays spent, and trees still waiting to regrow still do.

## Fighting a fire

A large fire is meant to be fightable rather than something you stand beside and hope.

- **Water is a real firebreak, and so are dirt paths and cultivated ground in a hosted game.** Where that applies it protects a base more than you would expect: the levelled, pathed ground most bases sit on counts as fuel-free, so a wildfire burns to the edge of the yard and stalls, and walls only catch if fire starts inside the perimeter. On a dedicated server that only holds near the world's centre, in the few zones the server itself keeps loaded; everywhere else the server cannot see the ground, so only water counts for now. That is a known gap, not a setting.
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
| `fireset <key> <value>` | Live-tune any setting, no restart |

Commands run on the server no matter where you type them, authorised against the server's own admin list, and the reply comes back to your console prefixed `[server]`.

Diagnostic-only: `firelistprefabs`, `firecheckprefab`, `firepurgevfx`, `firegroundignite`, `firetreeregrow`, `firetreeregrowlist`.

## Configuration

Everything is in `BepInEx/config/com.raveniron.firefront.cfg` and everything is live-tunable with `fireset`, no restart required. The config file migrates itself between versions: when a default changes, a value you never touched follows it, and a value you chose is kept and named in the log.

`fireset` keys:

| Area | Keys |
|---|---|
| Core | `enabled`, `burnduration`, `firematurity`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `burnbuildings` |
| Ground fire | `groundenabled`, `groundcellsize`, `groundradius`, `groundburnduration`, `groundmax`, `groundvfxmax`, `grounddamagemax`, `groundleashenabled`, `groundleashdistance` |
| Damage and warmth | `firehurts`, `firehurtsplayeronly`, `firehurtsradius`, `firedamage`, `firetickinterval`, `firewarmth`, `firewarmthradius` |
| Putting it out | `extinguishradius`, `dousingradius`, `douseimmunity`, `rainsuppress`, `rainmultiplier`, `rainobjects`, `rainobjectmultiplier`, `firebreaks` |
| Wind | `windbias`, `windupwindchance`, `windinfluence` |
| Aftermath | `scorchmarks`, `scorchlifetime`, `dirtpaint`, `dirtpaintradius`, `exhaustionenabled`, `fuelregrow`, `treeregrowth`, `treeregrowthseconds`, `persistfires` |
| Ramp | `rampenabled`, `rampduration`, `rampstart` |
| Visuals | `vfx`, `procedural`, `maxflameheight`, `treeflames`, `crownsparks`, `tallfiremax` |
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

- The fire visual is homemade, not a vanilla asset. It reads as fire; it is not Valheim's own.
- Defaults are tuned aggressive. Expect fire to spread fast and hungrily unless you dial it down.

## Credits

**Wu'barrk** — visual effects, and the config-migration machinery FireFront's own is built on, by way of Wings of the Valkyrie and Valkyrie's Cargo.
