# FireFront: the road from 0.22.1 to 1.0

Written 2026-09-21 against main `d5aa62d` (0.22.1). Inputs: docs/HANDOFF.md, CHANGELOG.md, README.md, docs/CONFIG-KEY-MAP.md, the code's own comments, GitHub issues and PR #3, read by four agents; two release proposals from opposite angles (player-facing failure first, engineering risk first); an Opus judge and an Opus completeness critic; and the owner's and this author's own knowledge of what the last four weeks found in play. Sixty-six open items came out of that. This document is a proposed order for them; every item carries its source so it can be checked or argued with.

## What 1.0 means

Someone who has never talked to either of us can run this on a public dedicated server and get what the store page describes: settings that do what they say, no way for a non-admin to change the server, and a world that survives an upgrade or a downgrade. Four conditions, each with a check that can be run:

1. **No unauthenticated client can change server behaviour.** Every server-side mutation that a client can request passes the server's own admin check or is clamped against the server's own config. Check: a non-admin client types `fireset burntheworld true`, drags a ConfigurationManager slider, throws a Dousing Bomb with a forged radius, sends a forged tree-damage RPC and a `FireFront_PaintAssign` burst; all are refused and logged, and the owner's admin equivalents still work.
2. **Nothing configurable fails silently.** A key that has no effect on the machine reading it says so in its own description in the generated `.cfg`; a preset that overrides a key logs the override when it takes effect; a client and server on different versions are told so at connect time. Check: turn on LowSpecPreset and read the five things it disabled in the log; join with a mismatched DLL and read the warning.
3. **The README describes what happens on a dedicated server**, which is the platform the mod is built for, rather than on a hosted game. Check: read README.md end to end with the dedicated server as the reference. Two lines do not match yet: README.md:45 (creatures do not burn headless) and README.md:57 (dirt paths, outside the ring of real zones near world origin).
4. **Nothing ships on a reading alone.** Every release below merges after one evening on the rig with the owner, and every release that touches the look after one with Wu'barrk present. 0.22.1's real defects, the velocity-curve error that wrote 80,000 log lines, the decal that multiplied the ground by white, the five marks spawned 500 m away during a loading screen, were all found in play after review passes had missed them. Evidence means a log line, a screenshot or a frametime capture recorded in the CHANGELOG entry.

And 1.0 comes with the things a new admin looks for: a licence, an admin guide, a note on what the mod leaves in a world save and how to remove it, a checksum on the release, and a line about the one piece of player data it keeps.

**What 1.0 does not include, stated up front:** no rebuilt ground-fire flame visual (the scale of the 0.22.0 tree-fire rewrite; after 1.0); no in-game surface for arson attribution (it feeds the companion mod, documented as such); no compatibility below Valheim 1.0.15; creatures still do not burn on a dedicated server (a platform limit: the server has no physical world where they stand, so 1.0 fixes the sentence, not the physics); and no automated tests for the simulation core, the bridge, the patches or the console (they need live Unity and Valheim types; the rig evening is the accepted substitute, and the off-game harness grows only where the code is pure).

## Where 0.22.1 stands

Fire spreads, climbs trees by their health, chars them, leaves soot and, on request, real dirt; it runs headless, survives restarts, remembers who lit it, and a late joiner sees it. All of that is play-verified on the rig. Not yet verified: the same things with a real second client; anything under load; anything on a non-English machine; and the packaging side of a public release.

## The releases

Seven before 1.0. Each is one rig evening to test. If the calendar slips, the cut line is stated at the end.

### 0.23 Authority

Everything after this ships new RPC surface, and it should be tested against the secured path rather than retrofitted.

- Server-side admin check in `FireDevCommands.ApplyRemote` (Commands/FireDevCommands.cs:1208-1226). It applies any forwarded value without a check; the comment there marks server-side validation as scope left for a public release, which is this. Both the typed `fireset` forward and the ConfigurationManager relay land there, so this one function closes the four separately filed items about it. The pattern to copy is `ExecuteRelayed` twenty lines above, which refuses a non-admin peer and logs it. Do not add a client-side gate: 0.21.6's client gate was false on every server without an adminlist file and made the feature a silent no-op (docs/HANDOFF.md:634-643).
- Clamp the extinguish and douse radii server-side. `HandleExtinguishRequest` (Fire/FireManager.cs:1026-1036) applies whatever radius arrived and never consults the server's own `ExtinguishGroundRadius` or `DousingBombRadius` (docs/CONFIG-KEY-MAP.md). It was filed as a documentation item about dead config copies, but it fits here: the client currently decides a radius the server applies.
- Both halves of the forged-sender problem, not one: bound and rate-limit `FireFront_PaintAssign` (docs/HANDOFF.md:507-510), and harden the tree-damage prefixes, whose only guard is the same easily forged sender filter (Patches/TreeBaseRpcDamagePatch.cs:47, Patches/TreeLogRpcDamagePatch.cs:32) on writes that replicate to every peer and are saved with the world.
- Restored ground-fire cells get a `FireBurnZone`; today creatures on a listen host walk through restored fire unharmed (docs/HANDOFF.md:586-589). A known bug with a known fix, easy to lose in a longer list.
- The one-line README change for creatures (README.md:45) goes here rather than waiting for the docs pass, so the store page stops promising it early.

Rig test: the five refusals above from a non-admin client, then the same five as admin.

### 0.24 The connect-time exchange

The version handshake and three dead-copy no-ops are consequences of one missing channel. Build the channel once.

- Server-to-client config broadcast on join (docs/CONFIG-KEY-MAP.md: sync is client-to-server only today). It settles the scorch keep-set divergence (each client keys `ScorchMarkKept` on its own `GroundCellSize`), `GroundVfxMaxConcurrent` never reaching the drawing machine, and `BurnDurationSeconds` drift making clients smoulder at the wrong moment.
- The plugin version rides in that exchange; a mismatch warns loudly on both sides. There is no handshake to extend: `Plugin.cs:15` is the only version token and nothing puts it on the wire. README.md:30 already admits a mixed pair "can leave ignition silently doing nothing".
- A wire-format version on every hand-packed payload. Twelve RPCs are addressed by fixed name strings and three carry bare count-then-tuples; if a field changes, a mismatched pair does not fail cleanly, it half-deserialises and can restore fire at wrong coordinates.
- Culture-invariant formatting and parsing everywhere: there is not one `InvariantCulture` in the tree. The persistence sidecar writes floats with the machine's culture (Fire/FireManager.cs, the store writer), `fireset` parses with it, and a large share of dedicated-server admins run de-DE, fr-FR, pt-BR, ru-RU or es-ES. It round-trips on one machine and stops matching the moment a world or a value crosses a locale boundary. This is the first off-game test worth adding: the sidecar's format and parse are pure string code.
- A downgrade story for the sidecar. Today any version mismatch, including a store from a newer build, discards every burning object, spent cell and pending regrowth with one Warn line, which is the loss the sidecar was built to prevent. Keep the old file beside the new one and refuse to read a newer format loudly rather than silently emptying it.

Rig test: a second machine, Wu'barrk's, joining mid-fire (docs/HANDOFF.md:64-67 has only ever been run by reconnecting one client); a deliberate version mismatch; a German-culture client and server against a saved world.

### 0.25 Headless truth

The trap that has bitten three times (spread 0.17.4, regrowth 0.21.5, player damage 0.21.8): anything on the server that reaches for physics, colliders or instances is wrong by default.

- Zone-gate `DrainRemoteVfxSpawnQueue` (Fire/FireManager.cs, the remote ground-VFX drain). It spawns effects at synced cells anywhere on the map, capped only by the concurrency key; the proven fix is in the scorch queue beside it.
- `IsDedicatedServer()` fails closed. Utils/ValheimBridge.cs:3812-3818 returns false when ZNet is null, when the reflected method is missing, and on any throw, so a reflection miss turns every `!IsDedicatedServer()` visual gate on, headless.
- The `Physics.` and `FindObjectsOfType` sweep the handoff has asked for three times (docs/HANDOFF.md:629-632): 26 call sites across six files, one evening to triage. Triage and file; fix only what is headless-dead.
- The ground-fire ignition channel. Object fire forwards a client's ignite request to the server; ground fire has no ZDOID to key on and never got its own channel (Utils/ValheimBridge.cs:3775-3777 calls it a follow-up). 0.26 is a ground-fire release; it needs this first.
- The handoff open-versus-resolved pass, here rather than at the end, because three items in it are already done and would otherwise get planned twice: the stop-script false data-loss warning (tools/stop-test-server.ps1 fixed it; the world directory's mtime is the authority), the PR #3 look items (closed at `7cbfe09`), and retired mask sets being parked (freed by `ReapRetired` since the same commit).

Rig test: a big fire far from the player with debug on, watching for any server-side allocation or spawn; the sweep's list attached to the CHANGELOG entry.

### 0.26 Firebreaks where players are

Dirt paths and cultivated ground are firebreaks only in a hosted game, or within the ring of real zones near world origin on a dedicated server (README.md:57, docs/HANDOFF.md:512-518). `IsClearedOrCultivated` goes through `Heightmap.FindHeightmap`, which is null everywhere a player actually stands.

- Read the zone's own `_TerrainCompiler` ZDO (`ZDOVars.s_TCData`, a compressed package: version, operations, then the paint mask) instead, cached per zone by data revision. The handoff scopes this as its own release and every reader agreed.
- Document the `UseVanillaDirtPaint` and `ScorchMarkLifetimeSeconds` pairing in the README: real terrain paint is permanent like a hoe mark; the lifetime key expires only the decal.
- Leave the two-hoes race narrowed, not closed; it is vanilla's own race (docs/HANDOFF.md:503-506).

Rig test: hoe a path 300 m from origin, light grass on one side, watch it stop.

### 0.27 The look and its memory, with Wu'barrk on the rig

- The three decal items the 0.22.1 reviews accepted for the time being: a cell that re-burns after its mark spawned gets a second coincident blot for the overlap of their lifetimes (replace the old mark rather than stack); the quad's spin is random per peer (seed it from the cell); and the 0.22.1 client checks nobody has confirmed run, the atlas-species ember masks, the retired-mask reaper mid-glow, the shared 30-90 m fade ramp (docs/HANDOFF.md:28-63).
- Move the charred albedo and normal build off the main thread (docs/HANDOFF.md:88-90); the release that made everything else asynchronous scoped this as its follow-up.
- Bound charred-texture memory. The charred albedo and normal are built at the source bark texture's full resolution as uncompressed RGBA32 with mipmaps, cached one pair per vanilla texture with no cap on species and no compression, and freed only when the game exits, not on world unload. Cap the cache, compress, and release on world unload.
- `TreeDestructionRate` 65 to 80, the number the owner asked for, through a ConfigLedger rung; and a harness test that a forgotten rung fails the build, because every release in this plan moves a default and the ledger is hand-maintained (CHANGELOG.md:586-624 records eight corrections to it the last time).

Rig test: both of us on the rig, a burnt pine forest, a coverage change mid-glow, an hour of play with the memory profiler open.

### 0.28 Scale

The showpiece preset has no budget and the network has no interest filter. A 1.0 needs a number an admin can plan against.

- Interest filtering on the three `Everybody` broadcasts (Utils/ValheimBridge.cs, `GetEverybodyTarget()`): a player mining in the mountains receives, decodes and books every ground cell of a forest fire across the map. Send fire events and cell deltas to peers within the fire's reach plus the join snapshot to everyone.
- A global ceiling under Apocalypse. `BurnGroundMaxConcurrent` is 500 per fire with no total, so simultaneous blazes multiply it. State the budget (cells, burning objects, broadcast bytes per second) in the README and enforce it.
- The measurements never taken: the CapFrameX capture of smouldering on and off (docs/HANDOFF.md:862-870; both are live commands, two captures, no restart), and the forced-rain suppression and mid-burn rain-arrival tests (docs/HANDOFF.md:729-736; schedulable, unlike natural rain).

Rig test: `fireset burntheworld true` on a ten-player-sized forest with the capture running; the numbers go in the CHANGELOG.

### 0.29 The release itself

What a new admin or a store reviewer will look for, done once, after the code has stopped moving.

- The README truth pass with the dedicated server as the reference: the feature list is stale at 0.19.6 (docs/HANDOFF.md:233-235), arson attribution surfaces only through the companion mod, presets and their overrides.
- Every dead-side key says so in its own `Bind()` description, so the generated `.cfg` tells an operator which of its 81 keys do nothing on this machine; presets log what they override at the moment they take effect. Regenerate docs/CONFIG-KEY-MAP.md last, since its citations are pinned to a commit.
- An admin guide: where the fire sidecar lives and that it does not travel with a cloud save; what the mod writes into a world save (seven `FireFront_*` keys on charred trees and logs, the vanilla health field on burning trees, terrain paint saved per zone) and what an uninstall leaves behind; the version window; how to report a problem and which log to attach.
- Localisation tokens for the Dousing Bomb's name and description; today they are English literals in a crafting menu that Valheim ships in thirty languages.
- A licence in the repository and the package. There is none yet, which means the mod managers mirror it without stated permission and Wu'barrk's contributions have no stated terms. Small to add, and needed for a public release.
- A compatibility position: the tree-damage prefixes cancel the original method with no `HarmonyPriority` declared, so every other mod's prefix and postfix on `TreeBase.RPC_Damage` and `TreeLog.RPC_Damage` is skipped. Declare the priority, state it in the README, and name the mods tested against.
- A CI workflow that builds and runs the off-game harness on every pull request, and a checksum published with every release; `dist/` is untracked and holds 33 zips that exist only on one machine.
- One disclosure: fires are attributed to a player id, the id spreads with the front, is written to disk and fed to the companion mod's reputation system. A sentence in the README and the store text covers it.
- The tester pipeline restarted with this as the release candidate. "Shipping to testers is on hold; testers remain on very old builds" is itself an open item (docs/HANDOFF.md:333-338), and the gate every release above depends on has been closed for a month.

Rig test: a clean install on a fresh world by someone who has never run the mod, following only the README.

### 1.0 Verify, then tag

No new code. Re-run the blocking checks only, not seven prior checklists in one sitting: non-admin refusal, the connect-time exchange with a version mismatch, zone-gated effects, an off-origin firebreak, the README read with the dedicated server as reference. Tag when all five pass on the rig with both of us present.

## The cut line

Seven releases for two people, one of whom volunteers the look. If it slips: 0.27 can ship as the current look with only the memory bound; 0.28 can ship as the interest filter alone with the budget stated rather than measured. What should not be cut: 0.23, 0.24, the licence, and the README pass, because each one affects people who only have the store page to go on.

## Corrections to the inputs

Three items the readers carried forward are already done: the six PR #3 look items (closed at `7cbfe09`), the stop-script false data-loss warning (fixed; the directory mtime is the authority), and retired ember-mask sets held until unload (freed by `ReapRetired`). One item is a platform limit, not a bug: creatures on a dedicated server. One item is misfiled: the extinguish and douse radii are an authority problem, not a documentation one.
