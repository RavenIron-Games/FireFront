# Changelog

## 1.0.6 — unreleased

- **`BurnPlayerBuildings` is off by default: fire no longer burns what players build** unless
  an admin turns it on (`fireset burnbuildings true`, or `BurnPlayerBuildings = true` in the
  server config). A config file that still holds the old default `true` moves to `false` on
  first boot (config layout version 4); a server that wants builds to burn sets it to `true`
  again after that boot. Ruins and other world structures are not player builds and still burn.

## 1.0.5 — 2026-11-21

- **A creature on fire spreads fire as it runs.** Set a bear alight with a torch, shoot a
  skeleton with a fire arrow, or let a boar run through a blaze, and while it burns it lights the
  grass along its path and any tree, log or burnable piece it passes within 4 m, so a burning
  animal running under the trees sets the forest alight. It burns for vanilla's time and takes
  vanilla's damage: FireFront changes neither. One burn carries fire for up to 30 seconds:
  a creature standing in the fire it started keeps catching again, and without the limit it would
  carry fire for as long as it lived. Past it the creature still burns and still hurts, but lights
  nothing until it has stopped burning for a few seconds. Players and tames never carry fire. The
  fire is booked to the player whose fire last hit the creature; one nobody's fire hit lately is
  booked to nobody. All the usual rules hold: nothing in the Ashlands, in
  water, in dungeons or in rain that stops fire, and `BurnPlayerBuildings` still protects
  builds. Four new server settings: `BurningCreaturesSpreadFire` (on), `BurningCreatureFireRadius`
  (grass, 2 m), `BurningCreatureTreeRadius` (trees, 4 m) and `BurningCreatureCarrySeconds` (30 s).
  Their `fireset` keys are `creaturefire`, `creaturefireradius`, `creaturefiretreeradius` and
  `creaturefireseconds`.
  A creature is run by the game of the player near it, so that player needs 1.0.5 for it to
  carry fire. This adds one new message (`FireFront_CreatureFire`); an older server ignores it,
  so the wire protocol stays 1.
- **Fire inside a dungeon no longer lights the forest outside it.** Dungeons and crypts sit high
  above the map, directly over their entrance, and FireFront's ground fire is laid out by map
  position only, each patch placed on the land below. So anything burning inside one (Brenna's
  fire in the Smouldering Tomb, most often) lit the grass under the dungeon, and that fire then
  spread into the forest around the entrance. Nothing inside a dungeon lights ground fire now.
  Objects in there still burn and still spread to each other, and fire outside, creature fire
  included, is unchanged. The wire protocol stays 1, so 1.0.4 and 1.0.5 play together; the fix
  needs only the server, or the player hosting, to run 1.0.5.
- **The extinguish key is now `` ` `` (backquote, left of 1), not G**, which clashes with the
  hotbar. A config file that still
  holds the old default G moves to the new key on first boot (config layout version 3); a key you
  picked yourself, G with a modifier included, is kept and named in the log. To keep G, set
  `ExtinguishKey = G` again after that boot. The key is read by each player's own game, so this
  changes on each player's machine as they update.

## 1.0.4 — 2026-11-01

Updated due to the Valheim 1.0.21 patch. Rebuilt and checked against Valheim 1.0.21 (network
version 41); no code changes beyond the version string, and the wire protocol stays 1, so 1.0.3
and 1.0.4 play together. The store page's website link is https://ravenirongames.com/. The
BepInExPack dependency is now 5.4.2360.

## 1.0.3 — 2026-09-25

Updated due to 1.0.16 Patch.

## 1.0.2

Fixes from a code review of 1.0.1. The wire protocol stays 1, so 1.0.1 and 1.0.2 play together.

- **Changing worlds no longer carries fire into the next one.** In single player or when
  hosting, logging out of a world with fire burning and loading another (or the same one again)
  kept the old world's fire running in the new one. Its burning objects pointed at unrelated
  objects in the new world, so pieces there could be destroyed, the old ground fire burned at
  its old coordinates, trees waiting to regrow were planted into the new world, and the new
  world's fire save was overwritten. Now the fire is saved to its own world when that world
  closes and then forgotten, and the next world loads its own. Dedicated servers were not
  affected.
- **A new fire's first trees no longer char almost at once.** The tree-fire clock stood still
  while nothing burned, so the first tick of the next fire charged the whole quiet spell as
  burn time: after a few quiet minutes a tree lit by a fire arrow went to its last health in one
  tick and charred before it was old enough to spread. The clock now stops with the fire, so a
  new fire's trees burn at the configured pace from the first tick.
- **The server checks what a client asks it to light or put out.** An ignite or extinguish
  request names an object, and the server used to build that object and take it over whatever
  it was, however far away. A modified client could name any object, another player's character
  included, and the server would take it over. The server now lights only trees, logs and
  burnable pieces, refuses a request for something more than 320 m from where the player's
  game last reported being, and puts out only an object that is already burning, without building anything. Pressing the extinguish key while aiming at a
  piece that is not burning no longer takes that piece from the player standing next to it.
- **Ground fire is drawn where it burns, whatever each player's `GroundCellSize`.** The server
  now sends its own cell size with the ground fire, and each client places the flames, the
  warmth and the scorch marks with it. A player who had set a different size saw the whole
  fire at scaled coordinates, far from where it really burned, and was not warmed by it.
  Against a server older than 1.0.2 a client uses its own size, as before.
- **Soaked trees are left alone.** Spread skipped a doused tree (a Dousing Bomb line) or a tree
  past the Ashlands edge only after building it and taking it from the nearby player, every
  0.75 s. It now skips them before building anything.
- **Each fire remembers who lit it.** Until now the igniter FireFront saved and offered to
  companion mods was one for the whole map, so Ragnarok's Wrath, for one, could only blame
  whoever lit the first fire since the map was last clear of fire, even after that fire had
  gone out. Now every burning object and ground cell records the player who lit it, fire
  that spreads keeps the igniter of the fire it came from, and the igniter is saved and
  restored with the fire. A save from an older version loads with the igniter unknown. The
  old map-wide value is unchanged, so older companion builds keep working. Newer ones can ask
  for each fire's igniter through `FireManager.CollectActiveFiresWithIgniters` and
  `FireManager.FireIgniterNear`. Nothing new goes over the network for it (a client's ignite
  request already carried the attacker's id), so it changes nothing for 1.0.1 clients.
- **Rain no longer puts out every fire at once after a pause.** Turning fire back on with
  `fireset enabled true` charged the whole pause as rain time, so every fire in the rain went
  out on resume. Rain now ages fire by at most two spread cycles at a time.
- The store page's website link now goes to the Raven Iron website.

This DLL was built from the commit tagged `v1.0.2`; the GitHub release names that commit and
gives the DLL's md5.

Rig evidence (2026-09-24, on the dedicated test rig, from a build of `e720f2b` — on the
same code as this release, since only documents and the store page's website link changed
after it). A dedicated server on Valheim 1.0.15 (crossplay), plus single-player worlds on
the same build; one client throughout. A fire stayed in its own world across a world change
in single player: the next world loaded with nothing burning and no fire lines, and
re-entering the first world restored its one burning tree. The same held joining the
dedicated server: no local fire carried over, and no restore or burn lines appeared at join.
Five arrow hits on the dedicated server all carried the same igniter id, and the fire save
file's `meta` line and its ground lines, read while the fire burned, matched it; a second fire was also lit by arrow
(the plan was `startfire`), so it carried the same id and a fire with no igniter was not
shown there — the only such fire seen was the rain check's `startfire` fire below, whose
fire event logged igniter 0 (its per-fire igniter was not read back). A client running a
different `GroundCellSize` than the server (2 against the server's 1) drew scorch marks
sized and placed for the server's cells once it joined, not its own. Extinguish by id put a
fire out with no forced re-creation and no re-light, and after `clearfires`, pressing the
extinguish key (G) at an unlit tree built nothing. After about two minutes with nothing
burning, a tree lit by a fire arrow took a first tick of under 1 % of its health and burned
at the configured pace from there (1.0.1 would have charged the whole two minutes in that
first tick, more than half the tree's health), and was still burning two minutes later. With rain
forced on (`fireweather force Rain`), one tree lit with `startfire`, and fire paused for
40 s with `fireset enabled false`, the tree kept burning after the resume and went out
15–18 s later on its rain-shortened timer, not at the resume itself — one tree and a 40 s
pause is a smaller check than the planned two to four trees and 60 s. `BurnPlayerBuildings
false` held throughout: `ignite` aimed at a player's wall answered `Not burnable`, and the
only piece that caught was part of an unowned world ruin. Settings were confirmed back to
their pre-test values afterward. Zero exceptions on either side.

Not tried in game: soaked or Ashlands-barred trees being skipped rather than re-created
mid-spread (no burning neighbour was near the doused tree in this run); the late-ZDO ignite
retry (every request in this run resolved directly, so the retry path was never used); the
tree tick under a slower spread cycle (`spreadinterval 10`); being warmed by ground fire
under a mismatched `GroundCellSize` (only where the flames and scorch marks sat was
checked); a 1.0.1 client against a 1.0.2 server and the reverse (checked by code review, not
restaged here); a queued fire keeping its igniter; a fire started by lightning or a creature
recording no igniter; a save from before 1.0.2 restoring with the igniter unknown (covered
off-game); a companion build that still reads the old map-wide igniter; a dedicated-server
restart with fire still burning; a world change while hosting with another player connected;
the server ignoring an ignite request for something that cannot burn, which any fire hit on
a stone wall sends; the server refusing a request for a target out of reach (320 m to
light, 200 m to put out), which only a modified client would send; and a second
player: two players lighting separate fires at once, which would test igniter attribution
between two real attackers, and the extinguish key pressed at an unlit piece beside another
player. Off-game: 1815 checks, 0 failed.

## 1.0.1

- **FireFront no longer sets the Ashlands alight.** The Ashlands' own fire (cinders, lava,
  burning ground) touches wood there all the time, and FireFront caught from every touch, so
  the whole biome burned and could not be played. A new setting, `FireInAshlands`
  (`fireset ashlands`), is off by default. With it off, FireFront starts no fire in the
  Ashlands by any path: vanilla fire, spread from outside, a client's ignite request, console
  commands, or a fire save written by 1.0.0, whose Ashlands fires are dropped at load. The
  Ashlands' own fire works as it does without FireFront. It is a new key, so the default
  reaches installs that already have a config file; `burntheworld` does not change it; the
  server's copy is the one read. While the server refuses fire there it logs at most one
  `[ASHLANDS]` line a minute, and `ignite`, `startfire` and `firegroundignite` say why they
  did nothing.
- **The DLL no longer carries the build machine's folder.** Every DLL through 1.0.0 carried an
  absolute build path that included the build machine's user name. The build now maps source
  paths to a neutral root, so the DLL's checksum follows the commit it was built from, not the
  folder it was built in. This DLL was built from the commit tagged v1.0.1.

Rig evidence (2026-09-23, on the dedicated test rig, this source built just before the
commit). In the Ashlands at about (51, -9126): `startfire` lit nothing, and the server refused
13 ignite requests the client forwarded for four Ashlands objects, logging its `[ASHLANDS]`
line. `fireset ashlands true`, typed on the client, set the server's value and `startfire`
then lit an Ashlands tree; `fireset ashlands false` turned it off again and the next
`startfire` lit nothing. At spawn, `startfire` lit a beech as before. No errors from the
mod in the session.

## 1.0.0

The first release. The code is 0.24.0's with the version string changed; the wire protocol
stays 1, so 0.24 and 1.0 play together (each side shows the "different version, works
together" notice).

- **What 1.0 means** is in docs/ROADMAP.md, "The shorter road": the server is the authority
  for everything a client can ask of it (0.23), client and server compare versions at
  connect and numbers read the same on every language setting (0.24), the MIT licence, and
  a README that describes what happens on a dedicated server.
- **The README pass**: every sentence checked against the code with a dedicated server as
  the reference, by five section auditors and a missing-feature finder with two refuters
  per finding, then rechecked by two independent readers. Nineteen corrections, among
  them: scorch marks fade after five minutes and lasting dirt is the opt-in `dirtpaint`;
  firebreak ground stops only fire crossing the ground; which commands run on the server
  and need its admin list; three `fireset` keys the table was missing; and the arson
  disclosure, stated as the code behaves: one igniter id for the whole server, the first
  player to light a fire while nothing burns, saved in the fire save file and read by a
  companion mod.

Rig evidence (2026-09-23, on the dedicated test rig). A 0.23 client on the 1.0 server:
the server logged `[VERSION] peer ... has not answered FireFront's version check in 60 s ...
This server runs 1.0.0 (protocol 1).` and the tester saw the centre-screen message. A 1.0
client on a 0.23 server: the client logged `[VERSION] FireFront: the server did not answer
the version check ...` about 30 s after joining and the tester saw the centre-screen message.
German number format: the 1.0 server ran as de-DE under Mono (the probe logged `1.5 formats
as '1,5', '1.5' parses as 15`, the trap 0.24 removed) and booted clean; the save file's
cross-culture reading is covered by the harness. Admin refusal (0.23), matching versions and
the decimal comma (0.24) were played on code 1.0 does not change.

## Before 1.0

Every release from the first build to 0.24.0 is in [CHANGELOG-0.x.md](CHANGELOG-0.x.md).
