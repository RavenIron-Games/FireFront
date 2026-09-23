# FireFront — 1.0.0 (2026-09-23): released

**Unreleased on main (2026-09-23): the build no longer embeds the build machine's folders.**
Every shipped DLL through 1.0.0 carried the absolute PDB path (C:\Users\<name>\…) in its PE
debug directory. The csproj now sets DeterministicSourcePaths and always names the repo root
as a SourceRoot, so the DLL carries /_/…/FireFront.pdb and neither the DLL nor the PDB names a
local path; the IL is unchanged. At the next cut, say in the changelog that the DLL no longer
carries an absolute build path that included the build machine's user name (quote no path),
and name the commit the DLL was built from: the md5 follows the commit and no longer the checkout folder (the PDB's Source Link URL carries the commit).

**Resume point.** `main` carries 1.0.0, tagged v1.0.0: 0.24.0's code with the version string
changed, the README pass, and the 1.0 rig evening recorded in the CHANGELOG. Work after 1.0 is
the list in docs/ROADMAP.md ("The shorter road": moved after 1.0), including per-fire arson
attribution. Problems found in play are fixed as they come.

# FireFront — 0.24.0 (2026-09-23): the connect-time version check, invariant numbers, MIT

**State at 0.24.0.** `main` carries 0.24.0, tagged v0.24.0: the version exchange
(Fire/VersionCheck.cs), culture-invariant numbers everywhere (Utils/InvariantNumbers.cs, with
1,680 harness checks), and the MIT licence. Played on the rig 2026-09-23 as far as the owner
chose (matching versions, the decimal comma; CHANGELOG "Rig evidence"); the steps below marked
there as not played are the 1.0 evening. Next: the README pass (docs only), then the 1.0
evening (docs/ROADMAP.md, "1.0 Verify, then tag": only the never-played checks), then the tag.
The shipped build adds one fix found by review after the rig session: on-screen version
notices wait for the character to spawn (display only; the matching-version path is unchanged).

## 0.24.0 (2026-09-23): the rig evening that gates it

1. Both sides on 0.24.0. Server log on the owner's join: `[VERSION] peer <id> (Steam_...) runs
   FireFront 0.24.0, same as this server.`; client log: `[VERSION] the server runs FireFront
   0.24.0, same as this game.` No message on screen.
2. An old client: 0.23.0's DLL in Gale's `testing` profile, 0.24.0 on the server. About 60 s
   after joining: a centre-screen message and a console line, `FireFront: this server runs
   FireFront 0.24.0. Your game has an older FireFront, or none...`; server log `[VERSION] peer
   ... has not answered FireFront's version check in 60 s`.
3. An old server: 0.23.0 on the server, 0.24.0 on the client. About 30 s after joining: a
   centre-screen message, `FireFront: the server did not answer the version check...`.
4. Numbers on a German machine: the rig-only `FireFrontCultureProbe.dll` (session scratchpad,
   `cultureprobe/`; not part of FireFront) sets the process culture to de-DE at load. On the
   server, with a fire burning at the previous shutdown (the store is written in the invariant
   culture from 0.24, or in en-US before): restored fires come back where they were (`[PERSIST]`
   restore line, positions sane). On the client: `fireset spreadradius 1,5` and `fireset
   spreadradius 2.5` both read `1.5` / `2.5` on this machine and on the server; before 0.24 the
   German client would have stored 25 locally and the server 15. Put the value back after.
5. Remove both probes from the profile and the server; record the lines under "Rig evidence".

# FireFront — 0.23.0 (2026-09-21): Authority

**State at 0.23.0.** `main` carries 0.23.0, tagged v0.23.0: the first release on
docs/ROADMAP.md, reviewed (four Sonnet lenses, two Opus refuters per finding) and played on
the rig 2026-09-23 with every check below passing; the CHANGELOG's "Rig evidence" paragraph
has the log lines. One defect was found in play and fixed the same evening (the `fireset`
wording). Next on the roadmap is 0.24, the connect-time exchange. Read the 0.23.0 CHANGELOG
entry first, then docs/ROADMAP.md.

## 0.23.0 (2026-09-21): what changed, and the rig evening that gates it

What changed (CHANGELOG 0.23.0 has the detail): `FireDevCommands.ApplyRemote` refuses a
sender not on the server's adminlist; `HandleExtinguishRequest` clamps the radius to the
server's own ceiling and refuses a position more than 200 m from the peer's reference
position; `Patches/RoutedRpcSenderGuardPatch.cs` with `ValheimBridge.ValidateRoutedSender`
drops a FireFront or `RPC_Damage` package whose claimed sender is not its connection;
`HandlePaintAssign` drops more than ten assignments in five seconds; `Utils/AuthLog.cs`
gates every `[AUTH]` Warn to one line per peer, per kind of refusal, per ten seconds, with a count.

Review before the rig (2026-09-21 morning): four Sonnet lenses (engine correctness against
the shipping decompiles, bypasses by a modified client, honest traffic the checks could
drop, docs and log lines against the code), two Opus refuters per finding. Five findings,
all refuted by both refuters; two were tightened anyway because the observation was right
even where the defect was not: the `[AUTH]` gate is keyed per kind of refusal as well as
per peer, so a forged package and a refused `fireset` from the same peer in the same ten
seconds both show, and the README row on `fireset` says a change to the server needs the
admin list rather than "admins only" (a client can still tune its own client-read keys).
Refuted and left alone: the 200 m reach check reads `ZNetPeer.m_refPos`, which the client
reports, but it is vanilla's only server-side notion of where a peer is and the whole
server already acts on it; the guard's peer scan is the same loop vanilla's `RouteRPC`
runs on the same packet; a portal trip or respawn cannot put an input-capable player 200 m
from the last reported position inside the 2 s refresh.

**The rig evening.** Both sides on the same build. The owner's Steam id is on the rig's
adminlist (`C:\Users\donfr\AppData\LocalLow\IronGate\Valheim\adminlist.txt`); vanilla's
`SyncedList` re-reads that file within 10 s of an edit, so switching between the two halves
needs no restart.

1. As admin (id on the list). Server boot: `[AUTH] routed-sender guard armed: 13 methods`.
   `fireset burntheworld true` applies (server log `fireset (remote from <peer>):
   burntheworld = True`), then `fireset burntheworld false`. Drag a ConfigurationManager
   slider: same. `G` on a fire and a Dousing Bomb work as before.
2. As non-admin (remove the owner's line from adminlist.txt, wait 10 s). `fireset
   burntheworld true` answers `[server] FireFront: fireset burntheworld refused — you are
   not in the server's adminlist.` and the server logs `[AUTH] refused fireset
   'burntheworld = true' ...`. A slider drag: the same refusal. `G` and the bomb still
   work; they are not admin actions.
3. The forged packets, from the scratch plugin `FireFrontForgeProbe.dll` (source in the
   session scratchpad under `forgeprobe/`; it is not part of FireFront, goes into Gale's
   `testing` profile for the evening only and comes out afterwards). `forgeprobe paint` (a
   PaintAssign claiming to be the server, addressed to everybody): nothing at your feet;
   server log `[AUTH] dropped a routed RPC (method ...) from connection <id>: it claims
   sender <server uid>, the connection is peer <yours>`. `forgeprobe burst` (thirty at
   once): one `[AUTH]` line, and the next refusal after ten seconds carries `(29 more from
   this peer ... not logged)`. `forgeprobe tree` (an `RPC_Damage` with FireFront's tick
   marker, claiming the server, at the tree under the crosshair): dropped the same way, no
   `[TREE-HP]` line, health unchanged. `forgeprobe dmg` (a FireDamage of 1e9 claiming the
   server, addressed to yourself): no burn. `forgeprobe radius` (an honest request with a
   100 km radius): `[AUTH] extinguish radius 100000.0 m clamped to the server's 15.0 m`.
   `forgeprobe far` (an honest request 5 km east): `[AUTH] refused an extinguish request at
   ... 5000 m from where the server last saw the player`.
4. Put the adminlist line back. Record the log lines in the CHANGELOG under "Rig
   evidence", commit, tag v0.23.0, package with tools/package.ps1, hand the zip over.

Not changed in 0.23 and not to be changed here: the RPC names, payloads and the
`IsFromServer` filters (0.24 is the protocol release), and the absence of a client-side
`fireset` gate (by design; the 0.21.7 section below).

## 0.22.1 (2026-09-20): late-join fire sync, real charred wood, post-fire smoke

**State at 0.22.1.** The `wubarrk` branch carries 0.22.1 on top of 0.22.0, compiling (0 errors)
and booted headless on the real `l-1.0.15` Linux dedicated server (every patch applied, 0
exceptions). NomadicWar play-tested `720f520` clean (four sessions, 0 exceptions); the six
look items from that review are closed on top of it (CHANGELOG, last 0.22.1 entry). **Seen in
game 2026-09-20 evening** (owner, dedicated test rig, both sides on the 7cbfe09 build = main
b8c9ee5): the `[SHADER-DIAG] scorch decal material` read-back matched the expected line word
for word (blend 10/5, alphaChannel 0, skyMask 0, softParticles 0, cameraFadeFactor 1000,
fejdFog 1), the first five `[SCORCH] mark` lines were 5/5 terrain hits with 8-19 cm lift on
a 2-7 degree slope, the owner saw the blots on the ground and liked the fire and the charred
trees; 0 exceptions either side. Tagged v0.22.1 on the commit that records this. Read the
0.22.1 and 0.22.0 CHANGELOG entries first.

**Headless boot check on this box (one command, ~40 s):** the Steam-installed Linux dedicated
server plus `~/valheim-testbed/boot-check.sh` (libs-Tools' `DEDICATED-SERVER-TESTBED` lineage):

```
P=~/valheim-testbed/profiles/firefront; rm -rf $P; cp -r ~/valheim-testbed/base-profile $P
mkdir -p $P/BepInEx/plugins/RavenIron-FireFront && cp bin/Release/net472/FireFront.dll $P/BepInEx/plugins/RavenIron-FireFront/
~/valheim-testbed/boot-check.sh firefront 2530 420 'FireFront|FIRE|CHARRED'
```

Expect `BOOTED`, `Loading [FireFront 0.22.1]`, `All 12 FireFront RPCs registered` and an
empty errors section. This proves load-time binding and Harmony patching only: the world
clock stops on an empty server, and everything visual is client-side.

**0.22.1 client checks, on top of the 0.22.0 list below:**

- `firedebug` on, char a tree: expect `[CHARRED] charred albedo built from <bark texture>`
  once per species and `[CHARRED] ember masks built: 4x512² at coverage 0.20` once. If the
  first line is a `Warn` about reading the bark back, the tint look is in use (still fine,
  just flatter). `firedumptex` then writes the PNGs under `BepInEx/config/FireFront-textures/`
  — compare with `libs-Tools/CSharp/TexturePreview` output (same generator, same seed).
- The whole-tree pink glow from NomadicWar's 17:01 run is fixed at the mask (premultiplied,
  alpha 255, always bound, atlas needles excluded, distance fade) - if any trunk still glows
  end to end, `firedumptex` and look at `FireFront_EmberMask_0.png`: it must be BLACK between
  the pockets. Burning pines must show embers on the bark only, never the canopy.
- Scorch marks: `[SCORCH] mark 1..5` lines with `terrain-hits=5/5 lift=0.04m` (more lift on
  a bumpy patch; a Debug `too rough ... mark dropped` line is a hollow, not a bug) and
  `shader=Custom/Particle (Unlit) blend=10/5`; on the ground, soft dark blots 1.6-2.2 m
  across with no grid. THIS is the first build in which the multiply decal can draw at all -
  0.22.1 as reviewed multiplied the ground by white (the shader outputs vertex colour, not
  the texture; CHANGELOG, last 0.22.1 entry) - so one blot on flat ground is the check. The
  `[SHADER-DIAG] scorch decal material` line must read `blend 10/5 ... alphaChannel 0, cull
  0, skyMask 0, softParticles 0 (SOFTPARTICLES_ON off, nearFade -1000, fadeFactor 1),
  cameraFadeFactor 1000, fejdFog 1`. If `blend=n/a` the alpha-disc fallback is in use (no
  ember donor found). Blots must stay visible under a canopy and while walking right up to
  them - those were the two donor fades that would have hidden them - and in mist at 50 m
  they must fade into the grey like everything else, not sit on it as black blobs (that is
  what the black vertices + `_FejdFog` are for).
- Charred pine/fir (the atlas species): embers on the bark only, never on the bare branch
  cards of the dead atlas; then `fireset charredembercover 0.6` mid-glow: after ~10 s of the
  new masks existing, `firestatus` should show `retired masks 0` and a Debug `[CHARRED] freed
  retired mask set generation N (M textures)`, with NO tree lighting up whole at that moment
  (that is the destroyed-texture-samples-white failure the reaper is argued against).
- Walk away from a burning tree and from a charred one side by side: both glows fade on the
  same ramp between 30 and 90 m; nothing steps at 70 m any more except the far emitters.
- The charred trunk should be black plates with a few glowing pockets, not a lit lattice;
  `fireset charredember 1` makes it the Ashlands strength, `fireset charredembercover 0.6`
  spreads the pockets (masks rebuild in a few ms; watch for `ember masks built`).
- Thin smoke off the trunk and off a fallen charred log for 90 s, thinning after 60 s;
  `fireset charredsmokeseconds 0` stops new ones.
- Late join: light a few things, connect a SECOND client afterwards; its log should show
  `[SYNC-DIAG] object snapshot from <server>: N burning, N new to this client` and the fires
  should be drawn (smouldering ones already as embers). Without a second machine, disconnect
  and reconnect the same client mid-fire.

**Shared tooling this release added to libs-Tools** (the user's standing rule: tools and
methods live there): `SharedMedia/ProceduralTextures.cs` (the generator; FireFront's
`Utils/ProceduralTextures.cs` is a byte-identical vendored copy — change both),
`CSharp/TexturePreview/` (offline harness: `~/.dotnet/dotnet run -- --tex <textures> --out
<dir>`), `UNITY-ASSET-TOOLS/` (the UnityPy scripts that read the bundles), `1.0/ASSET-DATA/`
(extracted textures, shader source, material dumps, the 2026-09-20 research reports) and
`RENDERING-AND-VEGETATION-SHADER-FACTS.md`.

---

# FireFront — 0.22.0 (2026-09-20): tree fire rework, charred trees, rebuilt VFX

**Reviewed 2026-09-20 evening (0.22.1, 66 Opus agents) and first run - five things fixed in
place, the look handed back.** Fixed here: (1) `CharredTextures` built the 512² crack field and
all four 512² ember masks synchronously, reached from `FireVFXController.Update` on the first
bark tick of the first burning tree - several hundred milliseconds of hashing on the main
thread. Fields, masks and the atlas erosion now build on worker threads (`Task.Run`; the
generator touches no Unity object), one variant at a time on demand; `EmberMask` /
`EmberMaskForAtlas` return null until ready, which every caller already treats as black, and
`OnEmberMasksRebuilt` re-points the clones when variant 0 lands. The GPU read-backs stay on
the main thread. The charred albedo/normal at charring time are still synchronous (once per
species, not on the ignition frame) - a follow-up if it shows. A tiered review of this fix
(Haiku map, Sonnet, Opus) found the one hole: a tree that streams in already charred skins
on its spawn frame with no local burn first, so `CharredAlbedoFor` joined the field build
synchronously. `CharredTextures.Prewarm()` now starts it from `Plugin.Awake` on any client
(`GraphicsAvailable`), a few MB once per process, so the build is done before a world loads. (2) `HandleObjectFireSync` read
the burn age off the wire unchecked (a NaN poisons the smoulder skip test and the VFX
progress) and stored it as an age, stale by however long the burner took to instantiate; now
clamped and stored as an ignition instant. (3) The scorch thinning hashed the floored metre,
not the cell. (4) Found in play, not on review: Unity logged `Particle Velocity curves must
all be in the same mode` every frame a flame was alive - 80,000 lines in eight minutes,
each a BepInEx console + disk write. Shuriken requires a velocity (and force) module's
x/y/z `MinMaxCurve`s to share one mode; `BuildFlames` set y to two constants with x/z left
constant, and `SetVelocity` assigned bare floats (implicit Constant) on every wind change.
Same shape in `CharredSmoke` and in `ValheimBridge.BuildCrownFlames` (main, since 0.21.2).
Every site writes all three in one mode now; `SetVelocity` reads `vel.y.mode` first.
(5) Also found in play: the client spawned a scorch decal the moment the sync stream said a
cell went out, wherever that cell was and even during the loading screen - both sessions
logged five of five marks with `terrain-hit=False`, the second set 500 m from the player
before any heightmap existed. `QueueScorchMark` / `DrainPendingScorch` (FireManager, client
path of Update) hold the mark until `ZoneSystem.IsZoneLoaded(pos)` and spawn it with the
lifetime it has left, so the 300 s contract in the config text stays true. A tiered review
of this fix (Sonnet, Opus) then shaped it: the 42 % keep is applied before queuing
(`ValheimBridge.ScorchMarkKept`), the drain runs per frame with a scan window (256) and a
spawn budget (8) so a region that loads at once fills in over seconds, the cap (4000) evicts
the entry at the cursor (O(1): this runs inside the sync handler), and anything with under
15 s or half the configured lifetime left is dropped rather than flashed. A second Opus pass
over that shape added one entry per cell (a cell that re-burns unseen would have spawned
coincident multiply blots, N deep, in one batch), made the scan bound a single pass rather
than a re-walk, and made the drain drop everything when marks are switched off while they
wait. A third pass on that: the cell set became an index (`Dictionary<cell, int>`, kept in
step through swap-remove), so a cell that re-burns while its mark waits refreshes the entry;
the floor is stored per entry from its own lifetime, so a runtime `scorchlifetime` change
cannot wipe the queue; eviction walks its own cursor so a burst at the cap cannot push the
drain past unchecked entries. A fourth pass then removed the direct-spawn path altogether
(a sync batch of expiries in the zone the player stands in is the COMMON case, and inline
spawning was the one-frame burst the budget exists to prevent; a loaded cell now spawns from
the drain within a frame or two) and rebuilt the probe: the synced height is not a terrain
height on a dedicated server (every cell inherits the seed object's Y for the whole spread),
so `SpawnScorchMark` casts from y=6000 down 10 km on the terrain layer, the way
`ZoneSystem.GetGroundHeight` does, and answers false with no quad when nothing is there.
`ResetScorchDiagnostics` restarts the five-mark `[SCORCH]` log on reconnect. Accepted, not
fixed: a cell that re-burns after its mark has already spawned gets a second coincident
blot for the overlap of their lifetimes (pre-existing; the multiply material squares it),
and the quad's spin is `Random.Range`, the one property peers do not agree on (radially
symmetric blot, invisible); `ScorchMarkKept` keys on each machine's own `GroundCellSize`, so
two clients with different values draw different keep sets (the same key already sizes
their decals differently; the real fix is a server-to-client config broadcast, see
`docs/CONFIG-KEY-MAP.md`), a runtime `GroundCellSize` change re-rolls queued cells, and the `IsDedicatedServer`
gate fails open if its reflection misses (pre-existing, every visual path shares it). Same
hole exists on `main` since 0.21.15 (the remote-mirror path); `DrainRemoteVfxSpawnQueue` has
no zone gate either and spawns ground VFX at any synced cell world-wide, capped only by
`EffectiveGroundVfxMaxConcurrent` - not touched, worth the same treatment.
Two changelog numbers corrected to the code (32 smoking objects, 10 s retry).
Handed to Wu'barrk on PR #3, look-side: the blot is 1.5x bigger than the changelog says (both
callers pass GroundCellSize*1.5 and SpawnScorchMark scales again) and at 42 % keep that is
~2.7 multiply blots deep everywhere, so burnt ground may go near-black; the multiply material
inherits the spark donor's tint (needs an in-game look - neutralise `_Color`/`_TintColor`
explicitly); the charred twin of an atlas species binds the plain mask, not the bark-confined
one; `SetEmber` drives `_EmissionColor` on slots that never got a mask; the live-burn glow
steps at 70 m where the charred path fades. Retired mask sets are parked until unload (5.5 MB
per coverage change; admin action, left alone on purpose - a destroyed texture samples white).
**Merge with main (0.21.16, dd22ad4) conflicts in six files**, all resolvable: the four
version/changelog files take 0.22.1 on top with main's manifest `description`; FireManager
and ValheimBridge both add an RPC in `RegisterFireRpcs` (make it 12 and say `All 12`), and
main's `LeaveScorchMark` no longer queues paint locally. First play run 2026-09-20 afternoon
(test rig, both sides on this build): 0 exceptions either side, the 8-object join snapshot
was sent, five `[SCORCH]` marks logged at join with `terrain-hit=False` (spawned before the
terrain loaded - inconclusive), and the velocity-mode error above. Tester's client had
`LowSpecPreset` on, which silently turns off scorch marks, charred smoke, crown sparks,
haze and shadows (`Effective*` flags are `!LowSpec && ...`), and `[CHARRED]` / snapshot
arrival lines are Debug level - `fireset lowspec false` and `fireset debug true` on the
client before judging the look.

**Reviewed 2026-09-20, before any run - two blockers fixed in place.** `LightFlicker.m_baseIntensity`
(written every frame per burner from `UpdateLight`) and `LightLod.m_baseRange` (from `SetSmoulder`)
are both `private float` in the shipping assembly and public only in the publicized reference.
Both writes compiled clean and would have thrown `FieldAccessException` on the first fire on every
client - and because Mono aborts JIT of the whole method, the first one also killed `UpdateLight`
before the light could track the front AND escaped `Update()` before the bark-char block, so the
bark blackening would never have shown. Same trap as `ZNet.m_peers`. Both now go through
`ValheimBridge.TrySetFlickerBaseIntensity` / `TrySetLightLodBaseRange`; the smoulder path also
writes `Light.range` directly (lowering the base alone does nothing inside 40 m) and sheds the
soft shadow via the public `m_shadowLod`. Also in the same commit: the two tick prefixes bind
`sender`, filter on `IsFromServer` and reject non-finite damage, matching `HandleFireDamage`;
`ApplyBurnChar` classifies its material slots once at collect time instead of allocating a
`Material[]` per renderer at 5 Hz per burner. Still NOT run in-game. Step 1 below stands.

**Resume point (superseded by 0.22.1 above; the test list still applies).** Read the 0.22.0
CHANGELOG entry first; it is the design record. Then do this, in order:

1. **Look at the fire.** Ignite a beech and a pine with `ignite`, `firedebug` on. Expect
   `[SHADER-DIAG] flame material cloned from fire_pit/flames (1)` (and ember/smoke/glow/haze
   lines) once, then `[TREE-HP]` lines every 2 s with the health dropping, and the front
   climbing the trunk on the same schedule. If the flames are white or invisible, the first
   suspect is `FireFrontTextureGenerator.FlameMaterialIsGradientMapped()` and the custom
   vertex streams - log `renderer.activeVertexStreamsCount` and the material's shader name.
2. **Watch a tree die.** At the health floor the tree should swap for a black one with the
   same silhouette minus leaves (`[CHARRED] ... replaced by charred twin ... fate=`), then 3 s
   later either fall (`sfx_tree_fall`, crash on landing, coal at the foot, log crumbles 20 s
   later) or stay. Chop a standing one: damage text shows, it falls the same way, coal only.
3. **Dedicated server.** The same, on the test server with a client attached. The ticks route
   to the CLIENT (`routed to owner <id>` in the server log, `owner-side tick` in the client
   log). The charred swap happens ZDO-only on the server; the client must see the swap.
   Watch for `ReleaseNearbyZDOS` ownership ping-pong in the tick log lines.
4. **Config on existing installs**: nothing was renamed or re-defaulted, so no ledger rung
   was added. `TreeDestructionRate` gained a range and `fireset treedestruction`.

Facts established this session live in the CHANGELOG and in three memory notes
(`valheim-rendering-facts`, `valheim-tree-lifecycle-facts`, `firefront-no-burnt-tree-prefabs`)
so the next session does not re-derive them: no burnt tree prefab exists; trees are
fire-Immune; `RPC_Damage` damage text is unconditional; the server is not the tree's owner
on a dedicated server; `Shader.Find` misses bundle shaders; normal maps are AG-packed.

**Left out in 0.22.0, closed in 0.22.1:** a late-joining client learned about object fires
only through the `FireEvent` RPC. 0.22.1 answers the join-time snapshot request with the
object list (`FireFront_ObjectFireSync`) rather than a ZDO flag: a server-written flag on a
client-owned ZDO can be discarded by the DataRevision race (ITEMDROP-OWNERSHIP-AND-PICKUP-
SYNC-FACTS.md §6) and structures have no owner-side tick to write it from.

---

# FireFront — addendum from the Ragnarok's Wrath session, 2026-09-18

**Scope note: this section is NARROW on purpose.** It was written by a session working in
Ragnarok's Wrath that touched this repo for two specific things. It does NOT supersede the
handoff below, which that repo's own session updated the same day and which remains the
authority on 0.21.x. Everything below this section stands.

## 1. The store listing stopped calling a shipped mod a tester build (commit `047b5e8`)

`README.md`'s first line read `🔥 **FireFront — Tester Build v0.19.11**`. Hexium renders the
PACKAGED README as the listing body, so that is what the public page said while serving **0.21.2**
— a two-minor-version-old tester label on a mod with 106 downloads that three other mods depend on.
Verified by fetching `valheim.hexium.gg/mods/RavenIronStudios/FireFront` and finding that exact
string in the rendered page.

The header now carries **no version at all**. The store displays the version it is serving on the
same page; a number written in the README can only ever go stale, which is what happened. Also
dropped "if you're testing Valheim mods" from the requirements line, same framing.

**Left alone deliberately:** the `New in 0.17.5` / `Fixed in 0.18.6` notes. Those are accurate
history, not claims about what the mod currently is.

**Still stale, and NOT fixed because it is product copy, not a defect:** the feature list stops at
**0.19.6** while the mod is 0.21.x, so it is missing roughly two minor versions of features. That
is an editorial pass for whoever owns the wording.

## 2. Repackaged 0.21.4 so the zip actually carries that fix

The existing `dist\RavenIron-FireFront-0.21.4.zip` had been built at 10:18, BEFORE the README fix,
and was confirmed by opening the archive to still contain the old tester-build line. **Uploading it
would have shipped the exact text the fix was for.** Repackaged via `tools\package.ps1`:

- `dist\RavenIron-FireFront-0.21.4.zip`, 261,426 B — README inside now opens `🔥 **FireFront**`
- 75/75 `ConfigLedgerTests` pass
- revprobe: `FireFront 0.21.4.0 — BINDS CLEAN` against Valheim **1.0.15**

**It was NOT uploaded, and should not be yet** — this repo's own HEAD commit
(`b0959a7 Record the in-game run 0.21.4 owes...`) says 0.21.4 is unverified. The zip is correct and
waiting; the verification is what is missing. Also **not pushed**: this repo had 2 unpushed commits
from its own session, and pushing mine would have published those too.

## 3. Two things found in passing, neither acted on

- **`libs\` was refreshed to the Valheim 1.0.15 publicized set** (`tools\fetch-libs.ps1` was run
  here, as in all seven repos, after the owner regenerated
  `valheim_Data\Managed\publicized_assemblies` by hand at 11:40). The 0.21.4 repackage above is the
  first FireFront build against 1.0.15 references. 1.0.15 needed no code change anywhere: 93/93
  apiprobe surfaces resolve, network version still 40, save formats unmoved.
- **`FireManager.FireEvent.RestoredRampAge` is declared and never assigned** (`CS0649`,
  `Fire/FireManager.cs:399`) — it always reads 0. One of 7 pre-existing build warnings, the other 6
  being `FindObjectsOfType` deprecations. Not investigated; flagged because a never-assigned field
  with a name like that usually means a restore path is silently doing nothing.

## 4. FireFront was WRONGLY accused of a bug, and the accusation is retracted

The RW session found that its own lightning gate was reading `EnvMan.IsWet()`, which is frozen on a
headless dedicated server. It initially concluded FireFront shared the blind spot, citing the
`raining 0/0` in FireFront's heartbeat. **That was wrong.** `RainingBurnersForStatus()` returns
`wet + "/" + _burning.Count`, so `0/0` simply meant nothing was alight. FireFront already resolves
the event through `RandEventSystem.GetCurrentRandomEvent()` — the server-authoritative
`m_randomEvent`, not the local-player-gated `GetEnvOverride()` — and carries its own replica of the
weather roll that does not depend on `Utils.GetMainCamera()`. **FireFront had this right before
Ragnarok's Wrath did.** The retraction is recorded in RW's changelog and CLAUDE.md as well.

---

# FireFront — session handoff (updated 2026-09-16, afternoon)

Resume point for the next working session. Read this before touching anything;
the memory notes in the assistant's store point here.

## Where everything stands

- **Repo**: `main` at **0.21.1** (committed 2026-09-16 as one commit spanning
  0.20.3 through 0.21.1; NOT yet pushed at the time of writing - check
  `git status -sb`), remote github.com/RavenIron-Games/FireFront. GitHub releases published for
  v0.19.8, v0.19.9, v0.19.11 and v0.19.14 (each with the bare DLL and the
  mod-manager zip attached, SHA256s in the notes).
- **Deployed builds: 0.21.1** on the test server and in Gale's `testing`
  profile (enabled). The four `RavenIron*` Gale profiles were put back to the
  versions Gale's database records for them (0.19.14 x3, Ravenrest 0.19.3) on
  2026-09-16, after carrying a stray 0.20.1 dev build for four days.
  Verify a deployment by **SHA256 against the DLL you just built**, or by the
  boot log line. Do NOT trust
  `[System.Diagnostics.FileVersionInfo]::GetVersionInfo(path)` on its own:
  PowerShell caches it per path within a session, so straight after
  overwriting a file it reports the PREVIOUS version. That misled this deploy
  twice on 2026-09-12. (Hashing is still the wrong tool for comparing two
  separate BUILDS — a rebuild of identical source hashes differently — but it
  is exactly right for "is the file I copied the file that is there now".)
  Never grep the DLL for version-shaped strings; FireFront's own log text
  contains literals like `0.17.2`, so a grep returns a list, not an answer.
- **ALWAYS use Gale's `testing` profile** (owner's instruction, 2026-09-12).
  It is the one carrying the whole RavenIron suite — Cairn, FireFront,
  RagnaroksWrath, RavenEye, Undertow, ValkyriesCargo — plus
  ConfigurationManager, Server_devcommands and Njord. Previous claims in this
  document that the owner plays `Default`, and before that `raveniron`, were
  BOTH wrong and each cost a session's client deploys. `Default` is a separate
  modpack with no FireFront in it at all.
  Two traps in that profile: mods are often parked as `<Name>.dll.off`, which
  is Gale's disabled marker (all six RavenIron mods were off on 2026-09-12),
  so enable through Gale's own toggle rather than renaming the file; and Gale
  LINKS profile files to its cache, so copying onto a profile DLL rewrites the
  cached copy of whatever version it came from. Delete the destination first,
  then copy.
- **Config defaults only reach installs that never ran an older build.**
  BepInEx persists values to disk, so changing a default in code does nothing
  where the key is already written. This bit twice in one day: 0.19.13's new
  `SmoulderAfterFraction` of 0.65 was silently overridden by the 0.45 that
  0.19.12 had written. Set it explicitly on existing installs.
- **The test server was rebuilt on Valheim 1.0.12 (2026-09-12).** It had been
  stranded on 0.221.12 since the 1.0 update, which is why 0.20.0 would not run
  there: `SimulationDistance` is a 1.0.x type the port adopted, and it failed
  with a TypeLoadException on the old build. There is no SteamCMD on this
  machine by the owner's instruction — the fix was that Steam already keeps a
  **Valheim dedicated server** install at 1.0.12 under `steamapps\common`, so
  the server was rebuilt from those files in place at
  `C:\Users\donfr\FireFrontTestServer`, with the BepInEx loader taken from
  `ValheimServers\Storm10` (the one server already proven on 1.0.12) and the
  tuned `com.raveniron.firefront.cfg` carried across. The previous install is
  kept at `FireFrontTestServer.old-0.221.12`. The world was untouched: the
  start script passes no `-savedir`, so saves live in LocalLow.
- **Testers**: at least one is on **0.19.3** (their log proved it), others may
  still be on the **0.18.0** Discord zip. Everything they are missing is in
  the v0.19.14 release.
  **Owner's call 2026-08-28: no Discord post needed** — the regenerated split
  in `dist\DISCORD_POST_READY.txt` exists but is not to be shipped unless the
  owner asks.
## Config migration (0.21.3, 2026-09-18): the next default change goes in the ledger

`Config/ConfigLedger.cs` (pure) decides, `Config/ConfigMigration.cs` (engine) applies:
`[Meta] ConfigVersion`, a rebase table keyed by the version it produces, "a stored value
still at an OLD default moves, anything else is the admin's", retired keys dropped,
`.vN.bak` beside the file, never blocks loading. **To move a default from now on: change
the Bind default AND add a row to `Rebases[n+1]` with the old default's on-disk text, bump
`CurrentVersion`, add a check to `tests/ConfigLedgerTests/Program.cs`, run
`.\tools\run-tests.ps1`.** Do not rename keys to dodge a stored value again. Verified on
the test server 2026-09-18 with a hand-aged file (0.45 + the orphan key): see CHANGELOG.

**DONE 2026-09-18 12:18-12:20, server side:** hand-aged file, boot line named both steps, no
refusal warning (every `_refused++` logs one on the next line, so a silent log IS the proof),
file stamped 1 with 0.65 and the orphan gone, `.v0.bak` byte-equal to the aged file, second boot
silent with no second backup. The in-game half could not be done from a client because the
status reply omitted the server's line - fixed in 0.21.5, see below. Original text kept:

⚠️ **0.21.4 OWES ONE IN-GAME RUN.** 0.21.3's verification above does not carry over: the engine
changed underneath it. This is boot-time config code, and house rule "a clean build proves nothing
about member access" applies. The check is the 0.21.3 protocol again — a hand-aged file (0.45 plus
the orphan `Debug.VerboseLogging`), boot, then `firestatus` — plus one thing that is new: the
summary line must now name the retirement AND, if anything was refused, say `REFUSED`. A clean
migration must not say it. Nothing here can be settled headless in a harness, because the whole
failure class is "the mod said it did something it did not do".

**0.21.4 corrected eight things in that machinery, and every one of them was invisible from
outside the game.** The harness that shipped with it compiled the PURE ledger only, so no
engine-side rule was measured at all — in the mod of the three with a live rung of each
kind. It now stubs BepInEx, compiles the real `FireConfig`, and stands at 75 assertions
with thirteen mutations each caught by a named test. Four facts worth carrying, all read
out of `libs\BepInEx.dll` with `ilspycmd` rather than assumed:

- **`ConfigDefinition.Equals` is ORDINAL and case-SENSITIVE** —
  `string.Equals(Key, other.Key) && string.Equals(Section, other.Section)`, the two-argument
  overload, over a case-sensitive `GetHashCode`. A mis-cased line is a DIFFERENT key to
  BepInEx. The snapshot compared ignoring case, so it answered "present" for a key BepInEx
  treats as absent — cancelling the one step whose entire safety is that absence test. The
  test stub had it backwards too, so the assertions would have passed for a reason that does
  not hold on a real machine. All three mods had this, all three are fixed.
- **`ConfigEntryBase.SetSerializedValue` swallows EVERY exception** and leaves the value
  untouched, logging a BepInEx warning. A try/catch around it is unreachable code. Read the
  value back and compare instead — which also catches `ConfigEntry<T>`'s setter running
  `ClampValue` and clamping silently.
- **`ConfigFile.Bind` returns the EXISTING entry for an already-bound definition**, cast to
  T. So relying on the cast to throw is NOT protection against retiring a live key: it only
  fails when the type differs, and a string key would be deleted in silence.
- **`ConfigFile.OrphanedEntries` is private, and every orphan is rewritten on each `Save`.**
  A key you simply stop binding rides along in the file forever, which is why a retirement
  has to bind-then-remove rather than just not bind.

**And the rule the whole thing turns on: a stamped file NEVER migrates again.** So any step
that failed for a reason that could succeed next time — today only a retirement whose drop
threw, because a transient file lock is nobody's bug — must WITHHOLD the stamp. A ledger row
naming a key this build does not bind must NOT withhold it: that cannot succeed next time
either, and withholding would re-run the migration on every boot forever. `firestatus` reads
`LastSummary`, which is written from the plan's INTENT before anything runs, so refusals have
to be folded back into it or the one line an admin reads is confidently wrong.

## 0.21.16 (2026-09-20): the dormant dirt painter, made real

Prompted by a Mists of Avalor write-up (`PATHER-REPAINT.md`, in the owner's Downloads) of how
that mod paints its road through vanilla's terrain ops. FireFront already had the same idea
sitting behind `UseVanillaDirtPaint` (off by default, "test-world only"). Against the shipping
1.0.15 assembly it had three defects, and the first repair of them - "every client paints what it
sees" - had three more, found by a 31-agent Opus review before it was ever run. What shipped:

**The three original defects.**

- **Headless-dead.** `LeaveScorchMark` queued cells on the simulating machine; on a dedicated
  server `Heightmap.FindHeightmap` / `TerrainComp.FindTerrainCompiler` scan static instance
  lists, and the server keeps real zones only around its own reference position, which never
  leaves world origin - everywhere a player is it makes ghost zones (`CreateGhostZones`
  instantiates a zone only to generate it and destroys it in the same call). So wherever a fire
  burns, every flush dropped everything at Debug level.
- **No compiler for pristine ground.** `FindTerrainCompiler` returns null for a zone nobody has
  hoed. Now `Heightmap.GetAndCreateTerrainCompiler`, which is what vanilla's `TerrainOp.Awake`
  uses.
- **No regenerate.** The batch called `Save(false)` only. Vanilla's `DoOperation` is
  `Save(paintOnly)` + `m_hmap.Poke(1, paintOnly)` + `ClutterSystem.ResetGrass`; the painter's
  own ZDO revision is already current after Save, so it never saw its own paint and the grass
  stayed.

**The three the review caught in the first repair, and the design that answers them.**

- Every client painting the cells it saw expire meant N peers writing the same zone. Two peers
  creating a compiler for a pristine zone in the same second destroy each other's
  (`TerrainComp.Awake` removes the OTHER compiler it finds, on both machines), and the next
  flush creates two more. And `ClaimOwnership` on a compiler another peer holds is a lost
  update: `Save` republishes the whole zone (heights too), ZDOMan keeps whichever revision lands
  first, and the loser's `m_lastDataRevision` already matches so it never reloads.
  **Now: exactly one painter per ZONE, elected by the server** (`FireManager.AssignPendingPaint`,
  once a second): the compiler's owner if the zone has one (the ZDO from
  `ValheimBridge.FindTerrainCompilerZdo`, then `ZDO.GetOwner()`; a headless server has the ZDO,
  just no instance) and the owner is connected and in reach; else the peer elected for that zone
  in the last 15 s (the window between a painter creating a compiler and its ZDO reaching the
  server); else the nearest ready peer in reach, who becomes that zone's painter for the window.
  "In reach" is standing in the zone or one next to it, which every simulation-distance setting
  keeps loaded - except that a zone with NO compiler needs its painter standing IN it, because
  `ZNetScene.IsAreaReady` (the create gate) checks the 3x3 around the zone, which at the lowest
  simulation distance is only instantiated around the zone the player stands in (fourth review
  round, 14 agents, the one finding). Delivered by a new routed RPC, `FireFront_PaintAssign`, server to ONE peer,
  radius + world positions + a per-cell MayCreate; a listen host is a candidate like any peer and
  queues for itself. The painter never takes a compiler from a live owner (`HasOwner` and not
  ours: drop); an unowned one in a zone it was elected for it claims, as vanilla's own `Awake`
  does.
- Calling `PaintCleared` directly skips `InternalDoOperation`'s stamp of `m_operations`,
  `m_lastOpPoint`, `m_lastOpRadius`. `CheckLoad` on every OTHER peer resets grass over that
  point and radius only when `m_operations` advanced by exactly one; otherwise it rebuilds the
  whole zone's clutter, per peer, per flush. The batch now stamps one op covering itself.
- `PaintCleared` reads each vertex through `getMask`, which returns the heightmap's last-BAKED
  texture unless `m_doLateUpdate == 1`, in which case it reads the compiler's accumulating mask.
  A batch that pokes only after painting has its second disc overwrite its first from stale
  pixels. The batch pokes BEFORE painting; still one regenerate.

A second review round (25 agents) on that design found four more, all fixed:

- **A local "no compiler here" is not "no compiler".** A zone's real compiler ZDO can exist and
  not be instantiated on this client yet (ZNetScene creates instances over several frames after a
  zone's heightmap is up). Creating one then makes a duplicate, and `TerrainComp.Awake` on any
  machine that HAS the real one instantiated destroys the OTHER through `ZNetScene.Destroy` - the
  real compiler, with every hoe mark in that zone, for everyone. So the server, which sees every
  ZDO, sends a per-cell `MayCreate` that is true only when it finds no `_TerrainCompiler` ZDO in
  the zone, and the painter creates only with it and only when `ZNetScene.IsAreaReady(pos)` says
  every ZDO of the zone has an instance here. Otherwise it paints an existing instance or drops.
- **An orphaned owner blocked a zone for good.** A compiler owned by a peer that left (or by the
  server itself) is never a candidate, so the painter refused it forever. The server releases
  such an owner (`ZDO.SetOwner(0)`, what `ReleaseNearbyZDOS` does within 2 s when a peer's
  active area holds the zone - a burning zone is often in the loaded ring outside one).
- **The sticky window never expired.** It was re-armed on every pass, including the pass it
  supplied the painter for, and had no distance test, so a painter that could not reach the zone
  kept it. Now armed only on a fresh nearest-peer election, and reused only while that peer is
  still within reach.
- **Cell indices on the wire.** The painter rebuilt positions with its OWN `GroundCellSize`; a
  mismatch would have laid permanent dirt at the wrong coordinates. World positions now.
- Also: the op stamp is rolled back when `Save(paintOnly)` short-circuits (else `m_operations`
  drifts ahead of the ZDO and every later batch lands remote peers in the whole-zone clutter
  branch); and `_groundPainted` is a counter for `firestatus`, not a gate.

A third round (16 agents) found three more, all fixed:

- **Election was per cell; what must be unique is the writer of a zone.** Two cells of one
  pristine zone could go to two peers in one pass, both with MayCreate (a zone's diagonal is
  89 m; reach was a 96 m radius), and both would create - the duplicate-compiler destroy again.
  Election is per zone now, and reach is zone adjacency, which is also what guarantees the
  painter has the heightmap loaded.
- **The owner branch had no reach test.** An owner that teleported away keeps a far compiler for
  good (`ReleaseNearbyZDOS` only scans around each peer's current position), and it was
  re-elected every pass. An owner out of reach is released like a departed one.
- **The neighbour spill could claim.** A disc crossing into the next zone claimed that zone's
  compiler if unowned - while that zone's own elected painter might be claiming it the same
  second. The spill now paints only a compiler this machine already owns; otherwise the sliver
  is dropped.

Load-bearing facts, all from the shipping DLL. The publicized copy has the same method bodies;
what it gets wrong is visibility, which is why the private/public list is the part that had to
come from the real file: `m_hmap`, `m_nview`, `m_operations`, `m_lastOpPoint`, `m_lastOpRadius`,
`PaintCleared`, `Save` private; `FindTerrainCompiler`, `FindHeightmap`,
`GetAndCreateTerrainCompiler`, `IsOwner`, `HasOwner`, `ClaimOwnership`, `Poke`, `ResetGrass`,
`ZNet.GetPeers`, `ZNetPeer.m_uid/GetRefPos/IsReady`, `ZDO.GetOwner/GetPrefab` public.
`PaintCleared` preserves alpha (`color2.a = a2`), so `PaintType.ClearVegetation` does nothing
different from Reset through it. Clutter suppresses grass where any mask channel > 0.5
(`Heightmap.IsCleared`). `FindSectorObjects` with `SimulationDistance(0, 0, true)` walks exactly
one sector.

Known and accepted: an owner that walks away keeps the compiler for up to 2 s
(`ReleaseNearbyZDOS`), during which cells elected to it may find its heightmap unloaded and be
dropped. A player hoeing a pristine zone in the same second the elected painter creates its
compiler is vanilla's own two-hoes race, narrowed by `IsAreaReady`, not closed. The dirt is
permanent; `ScorchMarkLifetimeSeconds` applies to the decal only. `FireFront_PaintAssign`'s
sender check is a filter, not an authenticator (same as every routed RPC here): a forged message
is bounded to 4096 cells at a clamped radius, but nothing bounds how many arrive, and it can
make this client create and claim a compiler for any pristine zone it has loaded.

**Found in passing, NOT fixed: dirt paths are not firebreaks on a dedicated server, except in
the ring of real zones it keeps around world origin.** `ValheimBridge.IsClearedOrCultivated` goes
through `Heightmap.FindHeightmap`, which is null anywhere a player is. The README now says so
(near the world's centre only, on a dedicated server; water everywhere). The fix is
to read the zone's `_TerrainCompiler` ZDO (`ZDOVars.s_TCData`, compressed ZPackage: version, op
count, last op point/radius, per-vertex height deltas, then per-vertex paint mask) and test the
vertex under the sample point, cached per zone by DataRevision. Own release.

Test: `fireset dirtpaint true` (server setting; the command forwards from a client),
`fireset scorchmarks false` on the client to see the dirt alone, light a ground fire, walk it.
Expect on the client: `[IGNITE-TRACE] All 11 FireFront RPCs registered`, then nothing about
paint unless a cell could not be laid (`Paint flush:` Debug lines). Expect on the server:
`Paint assign:` Debug lines only for zones no player was in or next to. (On the merged branch
the boot line says `All 12`.) **Run in play 2026-09-20 evening** on the merged build (wubarrk
`de4ca34`, dedicated test rig): the owner turned it on and reported it works - the first real
dirt under a fire on a dedicated server. Earlier that day, on the pre-merge build, every flush
dropped, which is the old server-side path and was expected.

## 0.21.10 (2026-09-19): the three left open by 0.21.9

1. **Join snapshot.** `FireFront_GroundSyncRequest`, a tenth RPC. The CLIENT asks, off the same
   `ZRoutedRpc` reference guard that drives `ResetRemoteMirror`, once `CanReachServer()` is true -
   the RPC instance exists several seconds before there is a server peer to address, same wait the
   config sync already uses. The server replies to that ONE peer in the ordinary delta format with
   an empty expiry list, so it lands in the existing `HandleGroundFireSync` and there is no second
   parser to disagree. Rate-limited per peer (5s): a routed RPC's sender is read off the wire and
   can be forged, so a peer that asks constantly is throttled rather than trusted.
2. **`IsDedicatedServer()`** - reflected `ZNet.IsDedicated()`, which the shipping SERVER assembly
   compiles to `return true`. Gates `wantVisual` in the two SERVER-side spawn paths only
   (`SpawnGroundVfxFor`, `SpawnVfxFor`). NOT `SpawnRemoteVfxOnly` - that is the client's own path
   and the test is false there anyway. A listen host is not dedicated and keeps its visuals.
3. **Restored cells** are added to `_groundVfxDark` instead of being skipped, so the 0.21.9 upgrade
   pass builds their ground object a few per cycle rather than fifty in the restore frame. That
   pass is no longer visual-only: it now refuses to touch a cell unless it can actually change
   something for it, or a cell waiting on a full visual budget would be rebuilt every cycle.

**Watch for:** the heartbeat's `[lit N, waiting M]` is the fastest read on all of this. On a
DEDICATED server `lit` should now be 0 always, and `waiting` should drain to 0 rather than sitting
at the cell count. On a listen host `lit` should track the burning cell count.

## 0.21.9 (2026-09-19): the drawn ground fire was never the same fire as the simulated one

Four divergences, all found after the owner said "the visual spread is different to the cells
burning" and then confirmed ALL FOUR directions at once - less flame than fire, flame without
fire, flame in the wrong place, and a front that spreads differently. That combination is the
tell: no single bug does all four. **There is no reconciliation anywhere in this path** - the
client is fed deltas and never corrected - so every error is permanent until the cell dies.

1. **`GroundVfxMaxConcurrent` defaulted to 30 against `GroundMaxConcurrent` 50**, so two cells in
   five burned invisibly, and a cell that lost that race at ignition was never revisited -
   `SpawnGroundVfxFor` is called once and returns early on `ContainsKey` ever after. Now 200, with
   `UpgradeDarkGroundCells` handing visuals to waiting cells as headroom frees. **This was sitting
   in a tester's log from 2026-08-30**: `ground 50/50` and `vfxcap 30` on the same line, every
   heartbeat, unread for three weeks. The heartbeat now prints `[lit N, waiting M]`.
2. **`FlushGroundFireSync` sat below the `_nextCycle` gate**, so its 1s interval quantized up to
   the next spread-cycle boundary: a real cadence of 1.5s at defaults, up to 10s if an admin slows
   the spread cycle. **This is the second subsystem that gate has swallowed** - `DamagePlayersInFire`
   had the identical bug earlier the same day. Check for a third before putting anything below it.
3. **`ValheimBridge.GetGroundHeight` is a no-op on a dedicated server** and always has been. Its
   raycast needs terrain colliders, which do not exist headless, and its `ZoneSystem` fallback is
   the same raycast wrapped - so it returns its own input `y`. Every ground cell therefore stores
   the Y of whatever object seeded the fire and carries it unchanged across the whole spread, and
   the client drew it verbatim. The client now re-samples its own terrain and keeps the wire value
   only as the fallback. 14,518 "found nothing" lines and zero successes in one session's log.
4. **`_remoteGroundVfx` was never cleared on logout.** FireManager lives on the plugin GameObject
   and survives the scene change; the VFX it spawned do not. The dictionary kept keys pointing at
   destroyed objects, `ContainsKey` refused to redraw them, and it compounded every session.
   `ResetRemoteMirror` runs off the existing `ZRoutedRpc` reference guard.

**Still open, deliberately not done:**
- **No join/reconnect snapshot.** A player who connects mid-fire never learns about cells that lit
  before they arrived. Restored-from-persistence cells ARE broadcast (FireManager.cs:1252), so a
  server restart is covered; a late join is not. The fix is a full `_groundBurning` send on peer-
  ready, and it is the last place the client is not reconciled.
- **Restored cells never get a `FireBurnZone`** - the restore path writes `_groundBurning` directly
  and skips `SpawnGroundVfxFor`. Players are unaffected since 0.21.8 (server-side ZDO damage), but
  creatures on a listen host walk through restored fire unharmed. Adding them to `_groundVfxDark`
  would let the new upgrade pass drain them a few per cycle.
- **A dedicated server builds particle systems nobody can see.** `lit 37` in a headless heartbeat
  is 37 real ParticleSystems on a machine with no camera. Suspect for the tester's unexamined
  "fire prods physics hard" report.

## 0.21.8 (2026-09-19): fire damage to players does NOT come from physics

`Fire/FireBurnZone.cs` polls `Physics.OverlapSphere`. **On a dedicated server that never finds a
player**, because there is no Character instance or collider where players are, only ZDOs — so
fire never hurt anyone there from 0.1 to 0.21.7, silently, while working on a listen host. The
zone's own staged diagnostics are what proved it: with DebugLogging on, `OverlapSphereNonAlloc
found` fired 4 times (trees, `viewblock`) and `resolved a real Character` fired **0** times.

Players are now found by `ValheimBridge.CollectPlayerTargets` and burned by `FireFront_FireDamage`,
a routed RPC the OWNING machine acts on, because vanilla's `SE_Burning` needs a live Character.
`FireBurnZone` skips players entirely so a listen host is not burned twice, and is no longer even
attached when PlayerOnly is set.

**`ZNet.m_peers` is `private readonly` in the shipping assembly and must be reflected.** Reading it
directly compiled fine and threw `FieldAccessException` once per cycle on the real server, which
also killed every step *after* it in the fire tick. I had checked a decompile — of the publicized
copy, which rewrites everything to public. Decompile
`FireFrontTestServer\valheim_server_Data\Managed\assembly_valheim.dll`, never `libs\` or
`publicized_assemblies\`; they differ by size alone (2560000 vs 2561536 on 1.0.12). `ZNetPeer.m_uid`,
`m_characterID`, `m_playerName` and `IsReady()` genuinely are public.

**`ValheimBridge.IsFromServer` is a filter, not an authenticator, and no new code should treat it as
one.** `ZRoutedRpc.RoutedRPCData.Deserialize` reads `m_senderPeerID` off the wire and `RouteRPC`
re-serializes it verbatim — the server never stamps the real sender — so a modded client can forge
it and address the result to `Everybody`. What actually protects `FireFront_FireDamage` is the clamp
in `HandleFireDamage`: reject non-finite and non-positive, cap at `FireConfig.MaxFireDamagePerTick`.
The cap is the setting's *maximum*, not this machine's value, so a client configured lower than the
server still takes full legitimate damage. Two Opus reviewers disagreed outright on this; the
decompiled body settled it.

`IsStandingInFire` must keep its Y band. `GroundCellKey` is (x,z) only, so a bare `ContainsKey` makes
every burning cell an infinite vertical column. The damage pass also runs *above* the `_nextCycle`
gate, on its own clock, and backs off rather than latching off after a failure — both were real
defects in the first cut, and both fail in the same silent direction as the bug this release fixes.

**This is the third instance of one trap** (spread 0.17.4, regrowth 0.21.5, player damage 0.21.8).
**Anything that reaches for physics, colliders or instances on the server is wrong by default** —
see [[dedicated-server-is-headless-at-origin]]. Grep for `Physics.` and `FindObjectsOfType` before
assuming a feature works on a dedicated server just because it works when you host.

## 0.21.7 (2026-09-18): the config sync, and why it had no admin gate

**Do not add a client-side admin gate to the config sync.** 0.21.6 did, and the feature was a
total no-op: `ZNet.LocalPlayerIsAdminOrHost` returns false when the server has no adminlist file,
which is every private test server, and the failure was silent on both sides. `fireset` reaches
the identical server-side setter and has never been gated, so gating only the manager route buys
nothing. **The real check belongs in `FireDevCommands.ApplyRemote`, server side, against the
server's own adminlist** — the unspoofable one the relayed commands already rely on. Still open,
and it would now cover both routes.

Also: **flush held changes on `ValheimBridge.CanReachServer()`, never on `ZNet.instance != null`.**
ZNet exists from the moment the world scene loads, seconds before the socket handshake and the
admin list; an earlier draft flushed there and threw everything away.

**Known limitation, unfixed:** after an UNGRACEFUL kill, a burning object's stored position (from
the 60 s sidecar) is compared against ZDO positions restored from Valheim's world autosave, which
defaults to 1800 s. A felled log that rolled in between lands further than the 0.75 m match radius
and is dropped. A graceful stop saves the world, so the two agree. The skip now logs which of
"nothing matched" or "too many matched" happened.

## 0.21.6 (2026-09-18): a ZDOID is NOT a persistence key

**`ZDO.Load` runs `m_uid.SetID(++ZDOID.m_loadID)`** — read out of the 1.0.15 server with
`ilspycmd`. Every object loaded from disk gets a fresh sequential id, and `SetID` also sets the
user half to ZDOID's `UnknownFormerUser` (1), which is why everything persisted prints as
`1:NNNNN`. **An id carried across a restart names a different object, and an existence check
passes on it.** Fire persistence keyed burning objects that way from 0.18.0 to 0.21.5, so every
restart either dropped the fire or lit trees that were not burning.

`obj` lines carry the prefab name in field 8 and the object's **live** position, and are resolved
by `FireManager.ResolveBurnerAt`: exactly one object of that prefab within **0.75 m**, each claimed
once, one sector scan per 32 m cluster. Two candidates means the line is DROPPED, on purpose — a
refusal costs one fire, a guess starts one on a bystander and hands it the dead object's burn age.
Fields 1-2 still hold the ZDOID for diagnostics and file compatibility; **do not read them.**

**Write the LIVE position, never `BurningState.Position`.** That field is captured once at ignition
under the comment "trees and pieces don't move", and `TreeLog` breaks it: the 1.0.15 decompile gives
it a Rigidbody, force and torque at spawn, and `AddForceAtPosition` on every hit, and FireFront
claims ownership so the server simulates it. Logs are most of what a forest fire leaves burning —
48 of the 50 entries in the store that proved this.

Pre-0.21.6 lines are dropped with a warning. Ground, spent and regrow lines were always
position-keyed and were never wrong. **Anything else that wants to remember a specific object across
a restart has the same problem — store position and prefab.**

Verified 2026-09-18: 50 persisted (48 of them logs) -> 47 restored, 3 skipped, no mis-resolutions.
The same store on 0.21.5 restored 0 of 23 and lit bystanders instead.

## 0.21.5 (2026-09-18): three defects found in play, all verified on the test server

1. **Regrowth never worked headless, and the spawn API is a broadcast RPC.** Read the CHANGELOG
   entry before touching `ValheimBridge.TrySpawnTree` again: it instantiates directly on the
   server ON PURPOSE. `ZNetScene.IsAreaReady` demands `ZoneSystem.m_zones`, which ghost zones
   never enter; `ZNetScene.SpawnObject(Vector3,Quaternion,GameObject)` is `void` and routes
   "SpawnObject" to `ZRoutedRpc.Everybody` (real 1.0.15 server, `ilspycmd`). Verify: heartbeat
   `regrowntrees` climbs, `[REGROW] N tree(s) regrew this cycle` in the server log, trees
   visible in the world. `firetreeregrow` no longer costs a blocked entry one of its twenty
   attempts, but it is NOT free: an entry whose spot is now built over is dropped for good, and
   the reply names grown/dropped/pending separately so a deletion cannot read as a success.
   **Regrowth refuses to plant within 4 m of live fire**, deferring 30 s at a time - without
   that, trees regrow into a burn that is still alight and are eaten within seconds (five of the
   first eight, live, 2026-09-18).
2. **The status reply carries the server's config line.** `firestatus` on a client prints two
   `[server]` lines: `FireFront config: ...` then `FireFront: burning ...`. That is how the
   REFUSED check is made from now on.
3. **ConfigurationManager edits reach the server** (`FireDevCommands.HookLiveConfigSync`, hooked
   from `Plugin.Awake`). Anything in `Settable()` is forwarded as the equivalent `fireset`, admin
   gated, deduplicated against the typed command, and dropped when ZNet is replaced. A setting
   NOT in `Settable()` stays local on purpose. **If you add a server-side setting, add it to
   `Settable()` or it is a no-op from a client in both the console and the manager.**
4. **`RestoredRampAge` is assigned at restore** from the oldest burner of each event created by
   the restore, from the store's own meta line (the true blaze age) and at the moment each event
   is BORN - not afterwards from the oldest live burner, whose age is capped at
   BurnDurationSeconds and which arrives too late to stop the restore truncating itself against a
   cold ramp. One `[PERSIST] restored fires resume at a ramp age of Xs` line. Restart the server
   under a burning fire to see it.

## Rain: verify this before anything else (0.21.0, built 2026-09-16, not yet run)

Rain never registered on a dedicated server - `EnvMan` only picks an environment
when a main camera exists, so headless `s_isWet` is false forever, and the test
server has never logged `raining True`. 0.21.0 replays vanilla's per-period,
per-biome-sector selection for the fire's own position instead, and rain now
blocks spread from and shortens any fire it falls on (objects too, new keys
`RainSuppressesObjectFire` / `RainObjectBurnDurationMultiplier`). To verify:

1. On a client, in NATURAL rain, type `fireweather`. The first line is FireFront's
   replay at your position, the second is what vanilla is showing you. They must
   agree. A third `[server]` line follows: the server's answer for the same spot,
   which is what the simulation uses. If the client agrees with vanilla but the
   server disagrees with the client, the world clock or sector lookup differs
   headless. Done once on 2026-09-16 (0.21.0): the client lines agreed; the server
   line could not be compared because the rain was vanilla's `env Rain`, which is
   CLIENT-LOCAL - the server correctly said 'Clear'. Natural-rain agreement is
   still unverified.
2. To test the fire behaviour without waiting for weather: `fireweather force Rain`
   (0.21.1) overrides the SERVER's weather; `fireweather reset` clears it. Then
   light one tree (`startfire`). Expect: no spread at all, and the tree out in
   roughly 72s at defaults instead of 240s. The heartbeat's `raining` field is now
   `wet/total` burners and should read `1/1`.
3. Light a tree in clear weather and wait for rain to arrive: the burn should
   shorten from that moment, not restart.

## Do this first: look at the fire

**2026-09-16, 0.21.2:** the first beech judged by eye showed sparks only, no flame -
the trunk column is inside the foliage and the leaves draw over it. 0.21.2 adds
`CrownFlames`, a hemispherical shell of flame on the outside of the measured canopy,
on top of the column. **Judged the same day, both species, Mountains at night:**
beech shows flame over its crown, fir keeps its column and looks fine. That verdict
also covers the 0.20.1 column and the 0.20.5 additive fire, which the rest of this
section still describes as unjudged - read it as history.


**0.20.1 and 0.20.2 are committed, pushed and deployed, and NEITHER has been
judged by eye.** Both are visual changes, so the log proves only that they
loaded. Start the test server, join, torch a STANDING fir, and answer two
questions.

1. **Does flame span the trunk?** (0.20.1) Everything photographed so far was
   the ground layer, which is not the code path that changed. A standing tree
   is. Wild firs are 15.8-31.7 m; `MaxFlameHeight` defaults to 30.
2. **Does it read as fire or as dots?** (0.20.2) At 0.20.1 a mass of flame
   particles looked like separate orange discs, because `Sprites/Default` is
   alpha blended and density never became brightness. Flames, sparks and
   ground fire now try the game's own `Custom/Particle (Unlit)` at
   `_SrcBlend 3 / _DstBlend 1 / _ZWrite 0`, vanilla's own values from
   `ashrain_cinder.mat`.

**The open risk on (2): `Shader.Find("Custom/Particle (Unlit)")` may not
resolve on the client.** It already fails on the dedicated server, which is
expected and harmless there (`-nographics` has no shaders resident at all),
and the fallback correctly warns once and reverts to the old look. But nobody
has yet seen a client log say which way it went. Look for one of these:

```
[SHADER-DIAG] additive flame material built from "Custom/Particle (Unlit)" ...   <- worked
[SHADER-DIAG] BuildFlameParticles: "Custom/Particle (Unlit)" not found ...       <- fell back
```

If it fell back, do NOT reach for another `Shader.Find` name. Pull the shader
off a material that is already loaded — `fire_pit`'s flame material through
ZNetScene — which is guaranteed resident because the prefab is registered.
That approach is strictly more reliable and was the planned next step.

## Two traps this session walked into

- **`tools/stop-test-server.ps1` cries wolf.** It greps the log for
  `World saved ( …ms )`, which **Valheim 1.0.12 no longer emits in that form**,
  so it prints "NO shutdown save found … world state is lost" after a perfectly
  clean shutdown. Verified three times by checking the world file instead: the
  save landed within seconds of the stop every time. Note 1.0.12 also writes
  the world as a **directory** (`worlds_local\Dedicated\`) rather than a single
  `.db`; the loose `Dedicated.db` files there are stale backups. Fixing the
  detection is an open task — a save warning that is always wrong trains you to
  ignore the one that isn't.
- **Storm10 has no FireFront.** Its `RavenIronStudios-FireFront\` folder is
  EMPTY, with a `RavenIronStudios-FireFront.off\` sibling — someone removed it
  deliberately. A session was lost to testing fire on a server that could not
  broadcast any. It runs the `NjordTest` world, not `Storm10`. Do not add
  FireFront back to it without asking; it is the owner's Storm infrastructure.

## In flight — finish these first

1. ~~**Relay verification, 3 of 5 commands outstanding.**~~ **DONE 2026-08-28
   — ALL FIVE RELAYABLE COMMANDS VERIFIED END-TO-END at 0.19.2 both sides.**
   `firestatus` and `firetreeregrowlist` were green on 0.19.0;
   `firegroundignite`, `startfire 10`, `clearfires` verified live today: zero
   "Admin only." refusals, `[RELAY] <cmd> from peer -428794145` on the server
   for each, and the matching `[server] ...` reply in the client log
   (`Seeded ground fire...`, `startfire: attempted 0 targets within 10m`,
   and `All fires cleared.` after clearing 8 entries). That "0 targets" was
   NOT harmless staging ground — it exposed a real bug, fixed in 0.19.3:
   startfire's target scan was still instance-only (AllPieces +
   FindObjectsOfType), which see nothing headless — the 0.17.4 root cause,
   never converted for this command. It now also sweeps the ZDO layer via
   `FireManager.IgniteBurnablesNear` (own scratch list — never clobbers the
   spread cycle's `_zdoCandidates` cache). **VERIFIED LIVE same day after a
   server bounce (0.19.3 both sides, load lines confirmed):** relayed
   `startfire 10` near real trees answered `attempted 3 targets within 10m`
   and the next heartbeat showed `burning 3/50` — three real fires on the
   headless server where the identical command had found zero. The
   doubled startfire in the server log was the owner running it twice (two
   separate client "sent to server" lines), not a double-send. The 0.19.1
   relay-before-local-gate fix is proven. Historical root cause kept for the
   record: vanilla's client-side `PlayerIsAdmin` exact-string match never
   matches a crossplay `Steam_7656...` id against a bare adminlist entry;
   the optional adminlist append (`Steam_76561198392625778`) remains a
   nice-to-have for vanilla's own commands, nothing of ours needs it.
2. ~~**Duplicate tree-regrowth entries.**~~ **DONE in 0.19.2 (2026-08-28):**
   position-keyed dedupe (`EnqueueRegrowth`, 0.5m radius, existing entry wins —
   its attempt count is real history) guards BOTH enqueue sites in
   `Fire/FireManager.cs` (burn-down and sidecar restore; a pre-fix sidecar can
   itself hold duplicates, and a deduped restore entry now counts as skipped,
   not restored). Builds clean; in-game verification pending — rerun
   `firetreeregrowlist` after the next burn and look for the
   `Regrowth dedupe:` debug line or simply no double entries. **Verified 2026-09-18:** 21
   entries, all distinct - and that same list exposed that regrowth itself had never spawned
   a tree headless (0.21.5).
3. **Ship to testers** — ON HOLD, owner said no Discord post needed
   (2026-08-28). The zip stays ready in dist\ if that changes.

4. ~~**The tester frametime spike.**~~ **DIAGNOSED AND FIXED — one measurement
   still outstanding.** Resolved from the tester's own Player.log, and the
   guesswork it replaced is worth remembering:
   - They run **0.19.3, hosting** (`IsServer=True`) — NOT the 0.18.0 zip. The
     earlier "they're just on an old build" theory was WRONG; they already had
     every prior performance fix, which is why the spike survived them.
   - Their log: `total candidates (pieces+trees+logs)=2176` (only 131 of them
     pieces), `burning 50/50, queued 20/20, ground 46/50`. `SpreadPass`
     compared every candidate to every burner each 0.75s cycle — roughly a
     quarter of a million distance checks per cycle, each carrying a type
     dispatch and a ZDO lookup. Measured spikes: 101.9-146.3ms. The arithmetic
     lands exactly on the symptom.
   - **0.19.8** put both candidate lists in a 16m spatial grid so a burner only
     examines the cells its reach touches; **0.19.5** had already cut ~5x by
     moving trees out of the instance list. VERIFIED, and then stress-proven:
     `burntheworld` ran **1316 burning objects and 6500 ground cells across 13
     fires on one saturated core**. The pre-0.19.8 code would have needed ~2.6
     MILLION checks per cycle for that — the configuration is only reachable
     because of the grid.
   - **0.19.12-14** then attacked the OTHER half. The tester's spike was worse
     *looking toward* the fire than away from it, i.e. rendering, not
     simulation — and every burning object carried its own realtime Light for
     its whole burn. Fires now drop to embers, glow and intermittent flare-ups
     past `SmoulderAfterFraction`. Confirmed by eye ("that reads better") after
     a first attempt that read as "the fire went out".

   **STILL OUTSTANDING — the only real gap left: nobody has MEASURED whether
   smouldering buys frames.** It shipped on a sound argument and the owner's
   visual approval, not a number. CapFrameX is installed on the owner's box and
   both toggles are live commands, so it is two captures with no restart:
   `fireset smouldering false` -> capture 60s at a burn -> `fireset smouldering
   true` -> capture the same spot. Compare P1/P0.2 lows and max frametime. That
   number is what tells the tester whether their problem is actually solved —
   and if it does NOT help, the next suspects are the ground damage-zone
   objects and the felled physics logs, not the particles.

5. **Dedicated FireFront test server — USE THIS, not the Steam install.**
   `C:\Users\donfr\FireFrontTestServer` (created 2026-08-29): a full copy of
   the dedicated server stripped to TWO plugins, FireFront and Server
   Devcommands, with its own `ff-test.log`. Port **2458** so it never collides
   with Ravenrest on 2456.
   WHY it exists: the Steam server install is now the live Ravenrest modpack
   (26 plugins). Two servers sharing that install share one FireFront.dll — so
   a test server could not run a different build than Ravenrest — and its
   mandatory-mod list (Jotunn, Seasonality, VikingOS, WardIsLove...) rejected
   the owner's client with "incompatible version" every time. A minimal server
   demands nothing of a client and restores the property CLAUDE.md asks for:
   a failure there is unambiguously ours. Ravenrest's install is untouched;
   do not stop Ravenrest without asking.

   **START AND STOP IT WITH THE SCRIPTS IN `tools\`, NOT BY HAND:**
   ```powershell
   .\tools\start-test-server.ps1     # prints the join code; password 'firetest'
   .\tools\stop-test-server.ps1      # graceful, and VERIFIES the save
   ```
   Both take `-ServerDir` if the install ever moves. Copies also live in the
   server directory itself.

   **Why they exist, which is the important part.** The original CTRL_BREAK
   helper lived in a session scratchpad that got cleaned between sessions.
   Every "graceful stop" after that was launching PowerShell against a file
   that no longer existed, failing SILENTLY, timing out, and falling through to
   a force-kill — which skips Valheim's shutdown save. It only failed
   harmlessly because nobody was connected at the time. So:
   - `stop-test-server.ps1` fails LOUDLY (a distinct message per failure mode)
     and confirms "World saved" **from the log**, never from a file mtime —
     mtime races the write and already produced one false "it didn't save"
     alarm. It refuses to force-kill unless given `-AllowForceKill`.
   - `start-test-server.ps1` passes **no `-RedirectStandardOutput`** (Unity's
     `-logfile` already captures everything, and the redirect can leave the
     process with no console for CTRL_BREAK to attach to) and **refuses to
     start a second instance** — double-starting on one port happened twice in
     one session, and the loser lingers without binding.
   - `_ctrlbreak-helper.ps1` returns meaningful exit codes. NOTE exit
     `-1073741510` (STATUS_CONTROL_C_EXIT) is the SUCCESS case: the helper
     attaches to the target's console, so the break it raises kills the helper
     too. It looks like a failure and is proof of delivery.

   Test-server state as of 2026-09-03: FireFront 0.19.14, `BurnDurationSeconds`
   240, `SmoulderAfterFraction` 0.65, smouldering on, both presets off, debug
   logging off. Password `firetest` (changed from `secret` after a client-side
   cached-password rejection).
6. **Tester's other observation, unexamined: fire prods physics hard.** They
   reported lag on a scale they had not seen in ~7k hours of Valheim, needing
   three people to cut up a bonfire to recover, and thought they had a
   screenshot. Plausible mechanism: felled trees spawn physics logs via
   vanilla's own felling and are UNCAPPED by design, while ground fire also
   creates damage-zone objects (`GroundDamageMaxConcurrent` caps those).
   Ask them for the screenshot or a clip — a visible log pile in frame would
   distinguish physics objects from particle cost immediately, and the two have
   different fixes.


## Operational facts that cost real time — do not relearn

- **The owner plays on the `Default` Gale profile, NOT `raveniron`.**
  Corrected 2026-08-29: an earlier note here said `raveniron`, and a whole
  session's client deploys went to the wrong profile before the mistake
  showed up (it was masked because every profile ended up byte-identical
  anyway — SHA256-checked). `Default` is the one whose config file gets
  written during play; it carries FireFront, Undertow, LetItGrow, Jotunn and
  ConfigurationManager. `raveniron` (FireFront + Ragnarok's Wrath + Server
  Devcommands) matches the dedicated server's plugin set. When in doubt, the
  profile whose `BepInEx\config\com.raveniron.firefront.cfg` has the newest
  mtime is the one that was just played.
- **Client deploys go through Gale, never hand-copies to
  `plugins\FireFront\`.** A hand-copied folder next to Gale's managed
  `RavenIronStudios-FireFront\` folder means two DLLs with one GUID and
  BepInEx loads whichever it finds first — this caused days of "wrong version
  loaded" chaos. Correct paths: Gale cache
  `%APPDATA%\com.kesomannen.gale\cache\RavenIronStudios-FireFront\<ver>\` and
  profile `...\profiles\<profile>\BepInEx\plugins\RavenIronStudios-FireFront\`.
  Gale's enable/disable state lives in its SQLite db (`data.sqlite3`) — don't
  edit it; worst case the user clicks enable in Gale's UI. `.old` suffixes on
  files = Gale's "disabled" convention.
- **Deploy ritual**: build Release → stop server (`Stop-Process`) → copy DLL
  to server + Gale profile (profile copy needs the game CLOSED; arm an
  until-loop background copy if it isn't) → restart server → verify by log
  strings ("All 8 FireFront RPCs", version line), never by trusting the copy.
- **Console commands run where typed.** `fireset`/`firestatus` and the five
  relayables forward to the server themselves since 0.18.3/0.18.8/0.19.0;
  `ignite`/`stopfire` forward by ZDOID (crosshair is local). Anything new that
  touches server state joins the `_relayable` whitelist in
  `Commands/FireDevCommands.cs` and inherits relay + server-side auth.
- **Server log**: `AppendLog = true` (accumulates across launches — slice from
  the LAST "Preloader started"). BepInEx header timestamps are unreliable;
  date by Unity log lines or mtime. Heartbeat every 15s while fire burns is
  the server's pulse; debug logging (`DebugLogging`, renamed from
  VerboseLogging in 0.18.7) is OFF by default and free when off.
- **The publicized DLL lies about visibility.** Compile-time public ≠ runtime
  public (`ObjectDB.m_itemByHash`, `ZNet` members). Reflect vanilla members,
  verify shapes with `ilspycmd` against `libs\assembly_valheim_publicized.dll`,
  and read the decompiled BODY, not the signature.
- **ffmpeg** is installed (winget, Gyan.FFmpeg) for analyzing tester clips;
  the Linux tester runs MangoHud under Proton — frametime-graph clips are
  gold, ask for them.

## Architecture landmarks from this arc (0.17.2 → 0.19.14)

Wind-strength-scaled spread; igniter attribution; ZDO-layer spread candidates
(instance scans see NOTHING headless — proven) with a 5s candidate cache;
Dousing Bomb (BombOoze clone, no Jotunn); fire persistence sidecar next to the
world save (remaining-seconds encoding, `_restoredRampAge` for the −1
sentinel); reconnect re-registration (ZRoutedRpc is per-connection — guard by
instance reference); BurnPlayerBuildings (creator-stamp gate, ZDO-readable);
douse immunity (extinguished = soaked 90s); spread maturity (front pace tied
to burn time — user-confirmed tuned); allocation-free debug logging
(interpolated-string handler, net472 attribute polyfill); generic command
relay (whitelist + server-side `PeerIsAdmin` + peer refPos standing in for
"local player").

All of it field-verified on the live dedicated server; the changelog carries
the evidence per version.
