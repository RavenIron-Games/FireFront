# Changelog

## 0.21.7

- **The config-manager sync did not work at all, and said nothing about it.** 0.21.6 gated it
  on a client-side admin check. That check asks Valheim whether the local player is on the
  server's admin list, and it answers no whenever the server has no admin list, which is every
  private test server, so the owner was blocked on their own machine with no message anywhere:
  the slider moved, the client's file was rewritten, the server never heard. The gate bought
  nothing even where it worked, because `fireset` is the typed route to the same server-side
  setter and has never been gated either, so anyone who could abuse the manager could type the
  command instead. It is gone. The real check belongs on the server, against its own admin list,
  and is still the deliberate scope left for a public release; it now covers both routes rather
  than one.
- **A setting changed at the main menu is delivered when the handshake finishes**, not thrown
  away a frame after the world scene loads. The held changes were flushed as soon as Valheim's
  networking object existed, which is several seconds before a connection or an admin list, so
  the flush concluded the player was not an admin and discarded everything. Every time, for the
  one workflow the feature exists to serve.
- **A `fireset` whose config file is momentarily locked no longer kills the sync for the
  session.** Suppression while a command applies a value was a single flag cleared afterwards,
  and BepInEx writes the config file before it calls change handlers without guarding that
  write, so an editor or antivirus holding the file threw straight past the line that cleared
  it. It is now scoped to one key for one second and expires on its own.
- **A restored fire can no longer light a bystander when an earlier line has claimed its
  neighbour.** Candidates already claimed still count towards "is this ambiguous", so two halves
  of one trunk cannot resolve into a confident wrong match once the first is taken.
- **Regrowth plants at most five trees per cycle.** A burned forest comes due all at once, and
  every plant is an object creation that every client in range then receives.
- **The blaze age of a restored fire cannot leak into an unrelated later fire.** Adoption now
  happens inside the restore rather than later in the same frame, which closes the window where
  an early return could strand it.
- **A setting changed in the config manager is sent once, when it settles.** ConfigurationManager
  raises a change per keystroke and per drag frame, so typing "120" sent 1, then 12, then 120,
  each one applied immediately to a running server - for a moment every fire on it burned out in
  a single second, and a slider would do that tens of times a second. Changes now wait for a
  short quiet period, coalesced per setting, with a ceiling so a slider still takes effect while
  you watch it. A typed `fireset` is unaffected and still goes immediately.
- A restore that cannot find an object now says whether nothing matched or too much did.

## 0.21.6

- **Fire persistence was restoring the wrong objects, and lighting fires that were never
  burning.** A burning object was saved under its ZDOID, and a ZDOID does not survive a world
  reload: `ZDO.Load` opens with `m_uid.SetID(++ZDOID.m_loadID)`, so every object read from disk
  is handed a fresh sequential id and the one it was saved under is discarded. The user half
  becomes ZDOID's "unknown former user" sentinel, which is why every persisted entry prints as
  `1:NNNNN`. So the id in the store named a different object each boot. The existence check
  passed, because the id did exist, and that is how this survived from 0.18.0 to 0.21.5 with
  nothing in the log. Caught on the test server on 2026-09-18: one restart resolved 23 burning
  trees onto stumps, wood drops and rocks and restored none of them; the next resolved 12 onto
  live trees that had not been burning and set them alight, and the fire ran to 45 objects
  within a minute of a boot that was supposed to resume 23.

  A burning object is now stored with its prefab name and its LIVE position, and found again by
  both through the same sector scan the spread system uses. Live position matters more than it
  sounds: the position on a burner was captured once at ignition on the basis that "trees and
  pieces do not move", and a felled log has a Rigidbody, is thrown force and torque as it
  spawns, and is shoved again by every hit, while the server owns and simulates it. Logs are
  most of what a forest fire leaves burning, 18 of 21 entries in the store that caught this.

  The match must be unambiguous: exactly one object of that prefab within 0.75 m, with each
  object claimed once. Two candidates means there is no way to tell which was burning, so the
  line is dropped. That is deliberate. A refusal costs one fire, while a guess starts one, and a
  bystander picked by nearest-match would inherit the dead object's burn age at full spread
  maturity. Nothing moves while the server is down, so the tolerance only has to absorb the
  round trip through the text store. One sector scan serves a whole cluster of lines, since they
  are all one fire.

  The ZDOID is still written, as a diagnostic and so an older build can still read the file, and
  is no longer followed. **Object lines written before 0.21.6 are dropped with a warning rather
  than guessed at**, once, on the first boot; ground fire, scorched ground and tree regrowth
  were never affected because they were always keyed by position.
- **A restored fire with no surviving objects no longer ramps from cold.** Ground cells come back
  without an event and are adopted into one a moment later, after the point where the restore had
  already released the blaze age it was supposed to hand over.
- **A repeated `fireset` reaches the server again.** A cache meant to stop a double send recorded
  what the client had sent rather than what the server held, so once the two diverged the client
  could never re-assert that setting, while the console still printed success. The double send is
  prevented at its source instead.
- **A setting changed before joining a world is delivered on connect** rather than dropped, which
  is the config manager's main-menu workflow and the reason the feature exists.
- **`firetreeregrow` with regrowth switched off says so**, instead of silently discarding the
  queue's retry schedule and reporting the result as though fire were in the way.
- **`firestatus` keeps its REFUSED suffix** even if writing the config file throws, which is the
  one failure the migration is built around.

## 0.21.5

- **Tree regrowth had never grown a tree on a dedicated server, and could not.** Every entry
  in `firetreeregrowlist` waited behind `ZNetScene.IsAreaReady`, which (decompiled from the
  1.0.15 server) first requires the zone to be in the zone system's loaded table. Headless,
  every zone a player stands in is a ghost zone and never enters that table, so the gate was
  false everywhere but world origin, and each entry burned its twenty 30-second retries and
  was dropped in silence. The test server's log holds 2,085 heartbeats over three weeks with
  `regrowntrees 0`; a forced attempt on 2026-09-18 answered "16 forced, 15 still pending"
  with the counter unmoved. Behind the gate it was worse: `ZNetScene.SpawnObject` is a void
  in 1.0.x that broadcasts a "SpawnObject" RPC to every peer, each of which instantiates its
  own copy, and its null return was read as failure - so on a hosting client it would have
  planted one tree per connected machine, up to twenty times. Regrowth now instantiates the
  prefab directly on the server, which is exactly what vanilla's own RPC handler does on each
  receiver: one persistent ZDO, every client in range receives it. The readiness gate is
  gone, an attempt counts only when a spawn actually fails, a spot with something
  player-built within 3 m is dropped with a log line instead of retried, and one `[REGROW]`
  line per cycle says how many came back. Every entry dropped before this release is gone
  for good.
- **Settings changed in ConfigurationManager now reach the server.** Its UI edits only the
  machine it runs on, so on a client every server-side setting in it was a no-op: the slider
  moved, the client's own file was rewritten, and the simulation never changed. The same trap
  `fireset` was given a relay for in 0.18.3, with no console line to hint at it. Any runtime
  change to a setting the server owns is now forwarded exactly as the equivalent `fireset`,
  gated on the same admin check, deduplicated so a typed command does not go twice, and
  forgotten when the connection changes. Genuinely client-side settings stay local.
- **Regrowth no longer plants into a live fire.** A tree that came back into ground that was
  still burning caught immediately: on 2026-09-18 five of the first eight trees ever regrown
  on the dedicated server reignited within seconds, because ground fire routinely outlives the
  900 second regrowth timer, leaving a burn that looks as dead as before. Fire within 4 m now
  defers the entry 30 seconds at a time, and a deferral is not an attempt.
- **Turning tree regrowth off now stops trees already queued.** The switch gated only the
  enqueue. That was invisible while nothing headless could spawn; with regrowth working, an
  admin who turned it off mid-fire would have watched the queue keep planting for another
  fifteen minutes, and survive a restart. Queued entries are kept, so turning it back on
  resumes.
- **A tree that grows is saved immediately** instead of at the next 60 second tick. The entry
  left memory the moment it succeeded, so a hard kill inside that window read it back and
  planted a second tree inside the first.
- **`firetreeregrow` reports what actually happened.** It returned only "still pending", which
  falls the same way whether an entry grew a tree or was dropped, so a deletion read as a
  success. It now names grown, dropped and pending separately.
- **A regrowth spot whose build check cannot run is left alone** rather than planted. The
  check failed open, so a server where the sector scan was unavailable would have grown trees
  up through people's floors.
- **Attempt counts from an older store are no longer trusted.** Before this release the counter
  incremented on every deferral, which headless meant every cycle, so a carried-over count sat
  near its cap for a reason that no longer exists and would drop the tree on its first real
  failure. They restore at zero.
- **One scan serves a cluster of regrowth entries.** The build check walks a 192 m block of
  ZDOs to answer a 3 m question, and entries come due together; it is now done once per cluster
  instead of once per entry.
- **`firestatus` no longer reports a crashed migration as a clean boot.** The state that knew
  is reset before the console can read it, so the outcome is folded into the line itself.
- **`firestatus` from a client now shows the server's config migration line.** The status
  reply carried only the fire counts, so the one check 0.21.4's handoff asked for could not
  be made from a client. It arrives as a second `[server]` line.
- **A restored blaze no longer restarts its ramp cold.** `FireEvent.RestoredRampAge` had been
  declared in 0.19.x and never assigned (the Ragnarok's Wrath session spotted the compiler
  warning), so after every server restart each fire fell to its ramp-start intensity and
  climbed again. The sidecar stores no per-event age, but every restored burner carries its
  own and an event is as old as its oldest burner; that age is applied and logged per event
  at restore.

## 0.21.4

- **Eight corrections to 0.21.3's config migration, and a harness that can reach it.**
  0.21.3 shipped the migration with a test project that compiled the *pure* ledger only —
  every engine-side rule was unmeasured, in the one mod of the three with a live rung of
  each kind. The harness now stubs BepInEx and compiles the real `FireConfig`, and went
  35 → 75 assertions. Each fix below was proven by reverting it and watching a named test
  fail; thirteen such mutations, all caught.

  - **The pre-bind snapshot matched keys case-INSENSITIVELY.** BepInEx's own
    `ConfigDefinition.Equals` is ordinal and case-sensitive, so a mis-cased line is a
    *different* key to the game. An ignore-case snapshot answered "present" for a key
    BepInEx treats as absent, which cancels the one step whose entire safety is that
    absence test. Read out of `BepInEx.dll` rather than assumed; the test stub had it
    backwards too, so every apply assertion would have passed for a reason that does not
    hold on a real machine.
  - **A retirement that failed was reported as harmless, and the version stamped anyway.**
    Binding and removing a key both touch the file, so a transient lock — antivirus, cloud
    sync, a config manager, a second process in the same directory — takes the drop down
    through nobody's fault. The stamp then wrote "already migrated" and the retirement was
    never retried: a one-second lock made permanent. The drop now reports upward and the
    version is left unstamped, so the next boot tries again.
  - **A rebase row naming a key this build does not bind was skipped in silence**, and the
    file stamped as though it had moved. It warns now.
  - **A retirement could be applied to a key this build STILL binds**, deleting a live
    setting outright. Relying on BepInEx's cast to throw was not protection: `Bind` returns
    the existing entry for an already-bound definition, so the cast only fails when the type
    differs. Refused explicitly now, with a warning that names the row.
  - **The version stamp only ever rises.** It was assigned unconditionally, so opening a
    world with an older build dragged a newer file's stamp *down*; rolling forward then
    replayed rungs against values the owner had since chosen, and a rebase cannot tell a
    deliberate choice from the old default it happens to equal.
  - **A negative stamp made the migration walk two billion rungs**, freezing the boot thread
    on a hand-edited file. Clamped.
  - **One key could be decided twice in one boot** when two rungs named it, the later
    silently winning. First rung wins now, and the same key can no longer be rebased and
    retired in the same pass.
  - **The status line reported the plan's INTENT, not what happened.** `firestatus` printed
    "1 retired key dropped" for a key still sitting in the file, with the only contradiction
    a warning hundreds of log lines earlier. Refusals now correct the summary.

## 0.21.3

- **The config file migrates itself.** BepInEx persists every bound value to disk, so a
  changed default never reaches an install that already wrote the key. This mod has hit
  that twice and answered by hand both times: 0.18.7 renamed `VerboseLogging` to
  `DebugLogging` so a stored `true` could not follow it, and 0.19.14 could only tell people
  to set the smoulder threshold themselves after 0.19.13 moved its default from 0.45 to
  0.65. Now the family's machinery, Wu'barrk's from Wings of the Valkyrie by way of
  Valkyrie's Cargo, does it: the raw file is read before any bind, a `[Meta] ConfigVersion`
  stamps the layout, a value still equal to an OLD default moves to the new one while
  anything an admin set stays (and is named in the log), a key no current build binds is
  dropped instead of riding along as an orphan, a copy of the previous file lands beside it
  as `.v0.bak` first, and a migration that cannot back the file up changes nothing and
  retries next boot. A failed migration never stops the mod loading.

  Version 1, this release: `SmoulderAfterFraction` still at 0.45 becomes 0.65; the orphan
  `Debug.VerboseLogging` is removed. Every install seen on the owner's machines already
  carries 0.65, so on those the boot line reads "nothing to migrate" and stamps the version;
  the rung is for the testers 0.19.14 could only advise. `firestatus` prints this machine's
  own migration line. The decisions live in `Config/ConfigLedger.cs`, pure and off-game,
  with a harness under `tests/` (`tools\run-tests.ps1`, 35 checks) - the mod's first.

## 0.21.2

- **Flames on the outside of the crown, where a leafy tree can show them.** The first
  burning beech looked at on 0.21.1 showed sparks only and no flame. The column from
  0.20.1 is a cone under a metre wide up the middle of the tree, and a beech's crown is
  solid foliage from about three metres up, so the leaves drew over every flame inside
  it; only the sparks, thrown on a 35 degree cone, escaped. A fir's bare trunk shows
  the column, which is why it was judged fine there and never on a broadleaf.

  Tall burners now also get `CrownFlames`: a hemispherical shell of flame sized to the
  tree's measured canopy (`MeasureBurnerCrownRadius`, the same renderer-bounds walk as
  the height, 4-6m on a wild beech), centred a little above mid-height, emitting from
  the outer third of the radius so the fire sits on the leaves rather than in them, in
  particles big enough to read at distance and licking upward. The trunk column stays.
  Cost scales with crown area, halves under the low-spec preset, and the existing
  tall-fire cap bounds how many burners get a shell at all. Judged by eye the same day
  on a beech and a fir in the Mountains: the beech shows flame over its crown, the fir
  keeps its column and reads fine - which also closes out the column from 0.20.1 and
  the additive fire from 0.20.5, both unjudged until now.

## 0.21.1

- **`fireweather force <EnvName>` / `fireweather reset`: a server-side weather override
  for testing.** The first live `fireweather` on 0.21.0 showed the machinery working and
  exposed the gap: vanilla's `env Rain` writes a debug override on the client it is typed
  on and nowhere else, so a player standing in forced rain saw their client and vanilla
  agree on 'Rain' while the server, correctly, answered 'Clear' for that spot. This sets
  the same field on the server (relayed, admin-gated like every relayed command), where
  the resolver already honours it ahead of the roll. Names are vanilla's, case-sensitive.
  Not saved; a restart clears it.

## 0.21.0

- **Rain now reaches a dedicated server, and it douses fire.** Two things, because the
  first one turned out to be broken: `RainSuppressesGroundFire` read `EnvMan.s_isWet`,
  and `EnvMan.UpdateEnvironment` returns before choosing an environment when there is
  no main camera. A dedicated server has no camera, so its environment never changes
  from the startup value and that flag is false forever - the test server logged
  `raining False` in every heartbeat of every run since August, including with the
  player standing in rain. The key only ever did anything in single-player or on a
  player-hosted world. (Wind updates on a separate path, which is why the wind
  reflection verified live and this never could.)

  The server now replays vanilla's own selection for the FIRE's position: weather is
  deterministic per environment period and biome sector, drawn from a Random seeded
  with the period number, so it computes the same answer every client does and
  caches it per 64m zone per period. The overrides vanilla applies ahead of the roll
  (forceenv, the `env` command, a random event whose area covers the fire, an
  alt-biome forced environment, a persistent event whose radius covers the fire) are
  honoured in order; the two event kinds are scoped to the fire's position, because
  vanilla scopes them to the local player's and headless that is the origin. Clients
  blend into a new environment over 2s,
  so the server can lead a player's view by about that much. Not replicated: EnvZone
  (a per-player trigger volume) and the Ashlands/Deepnorth edge fixup that needs a
  loaded heightmap.

  And rain now acts on object fire, which by design it never touched: while rain
  falls on a burning tree or building it passes fire to nothing - no neighbours, no
  ZDO candidates, no ground seeds - and its clock runs faster, at
  `RainObjectBurnDurationMultiplier` (0.3: about 3x, so a tree that catches in rain
  is out in ~72s at the 240s default). Ground fire gets the same clock treatment via
  the existing `RainGroundBurnDurationMultiplier`, which used to shorten only cells
  lit during rain and now also reaches the cells rain arrives on later. Direct
  ignitions still work in rain - a torch or a lightning strike lights the tree, the
  rain then puts it out - because rain stops spread, it does not forbid fire. Both
  new keys (`RainSuppressesObjectFire`, `RainObjectBurnDurationMultiplier`) are under
  `[Weather]`, live as `fireset rainobjects` / `rainobjectmultiplier`, and off under
  burntheworld like every other restraint.

  `fireweather` prints the environment FireFront resolves at your position; on a
  client it also prints what vanilla is showing you, and the two must agree, then it
  relays so the server prints its answer for the same spot. That is the verification.
  The status line's `raining` is now `wet/total` burners.

## 0.20.7

- **The spread diagnostic only logs when debug logging is on.** `[SPREAD-DIAGNOSTIC]` was
  added in 0.17.4 to prove that tree spread works on a dedicated server, where
  `WearNTear.AllPieces` is always empty. It did that, and then kept reporting the same
  counts every 5 seconds for as long as anything burned: 3,283 lines in one day on the
  test server, more than twice the heartbeat, none of them saying anything new. It is now
  gated behind the existing `DebugLogging` key (`fireset debug true`, which reaches the
  server like every other fireset key), so the tool is still there for the next "why
  won't it spread" report and silent otherwise. No new config key: the default-off flag
  that already exists is exactly the switch this line should have had.

## 0.20.6

- **Object fire VFX is budgeted per frame, like ground fire already was.** Measured on a
  live client with 34 objects alight: CPU spikes of 3-4x the median arriving roughly every
  1.4s, against a 0.75s spread interval. The cause is that `HandleFireEventBroadcast` built
  each burner's whole rig - two or three ParticleSystems plus a realtime Light - inline and
  synchronously, so an entire batch of ignitions from one spread pass was constructed in a
  single frame. Ground cells have gone through a queue with a per-frame budget since
  0.18.6; object fire never did, and it is the more expensive of the two per instance.

  Ignitions now queue and drain at 2 per frame (lower than ground's 3, because a ground
  cell builds one cheap system and no light). An ignition that is extinguished or unloaded
  while queued is dropped rather than built and orphaned.

  This cost was latent before 0.20.1 and that release is what exposed it: a tall burner
  builds a taller particle column, an extra crown-spark system and a longer-range light,
  which is several times the construction work of the small flame every burner used to get.

## 0.20.5

- **The blocky fire is fixed, and the blend was never the cause.** `Custom/Particle (Unlit)`
  exposes `[Enum(Red,0,Green,1,Blue,2,Alpha,3)] _AlphaChannel` and **defaults to 0, Red** -
  it takes alpha from whichever channel you nominate. FireFront's generated particle
  texture is white RGB with its falloff in the ALPHA channel, so red read 1.0 across the
  whole quad, every particle drew as a solid square, and no value of `_SrcBlend` could have
  helped. Two were tried (3 SrcColor, then 5 SrcAlpha) and both produced blocks.

  Fixed at both ends deliberately: the material now sets `_AlphaChannel = 3` (Alpha), and
  the additive material gets its own texture with the falloff baked into RGB as well, so
  the edges go to black whichever channel the shader actually samples - and under additive
  blending black adds nothing. `_Cull` is set to Off so billboards are never wound away.

  Worth recording for next time: the `.shader` file in the AssetRipper export is a
  `//DummyShaderTextExporter` stub, because shader bytecode cannot be decompiled. Its
  PROPERTY LIST is real and is what solved this; its body is not and must not be read as
  the shader's behaviour.

## 0.20.4

- **Fire rendered as hard-edged squares at 0.20.3. Fixed.** The additive material copied
  vanilla's `_SrcBlend 3`, which is SrcColor - a blend that ignores the alpha channel
  completely. FireFront's particle texture is white RGB that fades out THROUGH ALPHA, so
  every quad contributed at full strength right to its corners and the fire came out as a
  cloud of red squares. Vanilla can use SrcColor because its own textures bake the falloff
  into RGB; ours does not. Now `_SrcBlend 5` (SrcAlpha) x `_DstBlend 1` (One), the classic
  additive pairing for an alpha-faded texture. The lesson: copy vanilla's values only when
  you also have vanilla's texture.

- Confirmed in play: `Shader.Find("Custom/Particle (Unlit)")` MISSES ON THE CLIENT TOO, not
  just on the headless server, so 0.20.2 would have silently fallen back to the old look
  and taught us nothing. The 0.20.3 borrow-from-a-vanilla-material route is what actually
  gets the shader, and the log says so: `additive shader acquired via borrowed from a
  vanilla fire material: "Custom/Particle (Unlit)"`.

## 0.20.3

- **The additive flame material no longer depends on `Shader.Find`.** 0.20.2 asked for
  `Custom/Particle (Unlit)` by name, and `Shader.Find` only sees shaders currently
  resident - so it can miss one the game definitely ships, and it misses every time on a
  headless server, which loads none at all. It now falls back to reading the shader
  straight off a vanilla fire material (`fire_pit`, then `bonfire`, then
  `piece_groundtorch`), preferring one that exposes `_SrcBlend`/`_DstBlend` so the
  additive blend can still be forced rather than inherited. A prefab registered in
  ZNetScene carries its materials, and a material always carries a live shader, so that
  route is not subject to load-order timing. Names are avoided on purpose: the only thing
  tying vanilla flame materials to shader names is AssetRipper's builtin fileID table,
  which is its own mapping and not the game's, and trusting it inverted the shader survey
  twice. The log now records which route won and what the shader actually turned out to be.

- **`tools/stop-test-server.ps1` stopped crying wolf.** It confirmed saves by grepping the
  log for `World saved ( ...ms )`, which Valheim 1.0.12 no longer emits - a clean shutdown
  now logs `Saving` and then Unload lines. The result was "world state is lost" after every
  clean stop: four in a row on 2026-09-12, all false, each disproved by looking at the world
  on disk. It now checks the world's own write time against the moment the stop began and
  treats the log line as a secondary signal, so the warning means something again. Takes
  `-World` and `-SaveDir` for servers that keep saves somewhere other than LocalLow. Note
  1.0.12 writes a world as a DIRECTORY, so a loose `<World>.db` beside it is a stale pre-1.0
  backup whose timestamp means nothing.

## 0.20.2

- **Fire is drawn additively, so it reads as fire instead of as dots.** Photographed in
  play at 0.20.1: a mass of flame particles looked like a hundred separate orange discs
  hanging in the air, not a body of fire. The cause is that the only particle shader that
  resolves in Valheim's build is `Sprites/Default`, which is ALPHA BLENDED - overlapping
  particles occlude one another rather than accumulating light, so density never becomes
  brightness. Flames, crown sparks and ground fire now render through the game's own
  `Custom/Particle (Unlit)` with `_SrcBlend 3` / `_DstBlend 1` / `_ZWrite 0`, the exact
  values vanilla uses on `ashrain_cinder.mat`. Smoke deliberately stays alpha blended,
  because additive smoke glows instead of darkening.

  If that shader ever fails to resolve, flames fall back to the old alpha material and say
  so once in the log - a miss costs the old look rather than invisible fire.

## 0.20.1

- **Fire climbs what it is burning.** A wild Valheim fir stands 15.8 to 31.7 m - FirTree is
  10.55 m at scale 1, and the world plants it at 2-2.5x in Black Forest and 1.5-3x in
  Mountain - and every one of them used to get the same 1.5 m plume as a burning bush,
  parked at the foot of the trunk. A forest fire read as a row of campfires standing next
  to untouched trees. Fire on anything over 3 m now spans the burner's measured height: the
  flame emitter becomes a cone VOLUME along the trunk, so Unity distributes the particles up
  it natively and there is no per-frame cost to this; its smoke starts at the canopy instead
  of the ground where the trunk hid it; and its light reaches a little further. Height comes
  from the renderers' own bounds, so it follows the actual silhouette rather than a guess
  per prefab, and it is measured once at ignition, never per frame.

- **Sparks off the crown.** Tall burners throw stretched, falling sparks from their upper
  half. This is the cheapest of the new effects and carries most of the read: flames say
  there is fire here, sparks say the fire is ABOVE YOU. They stop the moment that tree drops
  to smouldering - a smouldering tree throwing sparks reads as still-raging.

- **Cost and height are now two separate dials, and both are bounded.** `MaxFlameHeight`
  (default 30 m) bounds how tall a fire is DRAWN; particle counts, sizes and lifetimes stop
  growing at 14 m regardless, so covering a 30 m fir stretches the same particles further
  rather than buying more of them. `TallFireMaxConcurrent` (default 12) bounds how many tall
  columns exist at once - past it, further ignitions get the ordinary small flame and still
  burn, spread and damage exactly as before. Object fire has never had the aggregate visual
  cap that ground fire gives itself, and a tall burner costs about four times a short one,
  so the ceiling matters. It is enforced at spawn, not by a per-frame sweep over the nearest
  N, which is the shape of work behind every frametime spike this mod has had.

- **Fixed: every particle effect in the mod was firing SIDEWAYS.** Unity emits a cone along
  its local +Z, and a system built in code gets no rotation - the Editor hides this by
  pre-rotating the GameObject it creates for you. FireFront builds all of its effects in code
  and never set a rotation, so flames, smoke and ground fire had been emitting horizontally
  along world +Z since they were written. Vanilla Valheim settles the convention: in
  `fire_pit.prefab` the directional emitters (flames, low_flames, flames (1), smoke (1),
  smok_small, and sparcs (1), which carries it on its ShapeModule rather than its Transform)
  all aim up at -90 on X, leaving only `flare` - a billboard glow with no direction to point -
  legitimately at zero. All three builders now aim up, which is also why smoke never read as
  a rising column before.

- **New config, all under Visuals:** `TreeFlameScaling` (on), `CrownSparksEnabled` (on),
  `MaxFlameHeight` (30), `TallFireMaxConcurrent` (12). All four are live-settable through
  `fireset treeflames|crownsparks|maxflameheight|tallfiremax` and reported by `firestatus`.
  `LowSpecPreset` forces crown sparks off, caps height at 12 m and tall columns at 4 - the
  column itself survives the preset, because it is a correctness fix as much as a visual one.

- **Diagnostic: the particle-shader fallback chain now logs which candidate it resolved**
  (`[SHADER-DIAG]`). Settling it properly was worth the trouble, because a first pass got it
  backwards. Reading the ScriptMapper in `globalgamemanagers` (225 entries, laid out
  PPtr-then-name; parsing it name-first shifts every mapping by one and inverts the answer)
  against the object table of `unity_builtin_extra`: `Particles/Standard Unlit`,
  `Particles/Standard Surface` and both Legacy Particles shaders are all stripped from the
  build, and the chain actually lands on candidate 5, `Sprites/Default`. Standard Unlit misses
  for a mundane reason - the game ships it renamed to `Particles/Standard Unlit2`, with a
  trailing 2, so `Shader.Find` on the plain name finds nothing. Sprites/Default is alpha
  blended, so smoke composites correctly and the flames are not truly additive despite
  reading that way; it also draws an untextured particle as a hard-edged quad, which is
  exactly why the generated soft-particle texture exists.

## 0.20.0

- **Valheim 1.0.7 support. This release REQUIRES it, and does not run on 0.2x.** Eleven
  separate breaks. Only two of them failed to compile; the other nine are reflection lookups,
  which fail by returning null, which means a feature switches itself off in silence.

  The two loud ones:
  - `World.GetWorldSavePath` was deleted, so the fire store could not resolve a path and
    **fires stopped surviving a restart**. It now goes through
    `SaveSystem.GetWorldsSaveRootPath`, the same method rehoused — your existing
    `firefront_fires_*.txt` is found exactly where it was, with nothing to move.
  - `Terminal.ConsoleEventArgs` gained a third parameter, which broke the admin command
    relay — the path that lets a remote admin run FireFront commands on the server.

  The quiet ones, each of which built perfectly:
  - **Fire stopped hurting anything.** `Character.AddFireDamage(float)` gained a required
    `short variant`, so the lookup missed and every damage tick did nothing.
  - **Ground fire stopped seeing anything to burn.** `ZDOMan.FindSectorObjects` was retyped
    to take a `Vector2s` and a `SimulationDistance`, so the scan that finds burnable objects
    came back empty.
  - **Water stopped being a firebreak.** Valheim renamed `ZoneSystem`'s singleton field
    `m_instance` to `s_instance`; the world's water level then read as -10000 and fire
    crossed rivers and shorelines, with only a debug line to say so.
  - Burning status effects, scorch marks, felled-log cleanup, the dirt-paint piece and
    on-screen messages were all broken the same way, by
    `SEMan.AddStatusEffect` (whose fourth parameter changed type *and* which gained a fifth),
    `TerrainComp.PaintCleared` (restructured into a settings object), `TerrainComp.Save`,
    `TreeLog.Destroy`, `Player.PlacePiece` and `Player.Message` (each gained a parameter —
    a default argument still changes the signature).

  All 50 of the mod's compiler-invisible game dependencies are now verified to resolve
  against the real 1.0.7 assemblies, by a probe kept in the Ragnarok's Wrath repo so the
  next game update gets checked instead of guessed at.
- No gameplay, balance or config change. Same spread, same damage, same defaults.

## 0.19.14

- **`fireset smoulderafter <0.05-1>` and `fireset smouldering true|false`.**
  The smoulder threshold is an aesthetic value that needs iterating by eye, and
  it was the one thing that could only be changed by editing a config and
  restarting the server — the worst possible loop for something you tune by
  looking at it. Both are now live-settable and relayed like every other key.
  (Audit: 51 switch cases, 51 map entries.)
- Note for anyone confused by a default that did not apply: BepInEx persists
  config values to disk, so 0.19.13's new default of 0.65 was silently
  overridden by the 0.45 that 0.19.12 had already written. A changed default
  only reaches an install that has never run an older build. Set it explicitly.
## 0.19.13

- **Retuned smouldering — 0.19.12's version read as "the fire went out".**
  Reported straight from play, along with "trees aren't falling". The trees
  *were* falling (the log showed burn-downs, a growing regrowth queue and zero
  kill failures) — the simulation was never affected. But at a 60s burn
  duration a fire spent its last 33 seconds looking extinguished while still
  being contagious and still burning anything standing in it, so both
  complaints were the same bug: the downgrade was far too aggressive.

  What changed:
  - **The light is shrunk, not destroyed.** Deleting it took the glow with it.
    Range is the dominant cost of a realtime light — it decides how many
    objects the light must touch — so halving range and intensity keeps most
    of the saving while the fire still visibly has heat in it.
  - **Flames drop to 35%, not 12%**, and stay a warm ember orange instead of a
    near-black red. 12% was invisible.
  - **Intermittent flare-ups**, which is what the original suggestion actually
    asked for and the first version missed. A steady weak trickle reads as
    dying; irregular bursts read as still burning, just not raging — and they
    cost nothing between bursts, which is the point.
  - **Smoke barely reduced** (80%), since smoke is the signature of smouldering.
  - Turbulence stays on but weaker; off entirely left embers rising in dead
    straight lines.
  - **Default threshold moved 0.45 → 0.65**, so flames carry most of the burn
    and smouldering is the tail rather than the majority of it.

  Confirmed by eye 2026-08-29 — "that reads better". The failure of the first
  version was treating smouldering as LESS fire; the suggestion had asked for
  INTERMITTENT fire, which is a different thing and the part that makes it read
  as still alive rather than finished.
## 0.19.12

- **Fires stop drawing full flames forever — they drop to smouldering.** A
  tester's idea, and their own measurement is what justified it: their residual
  frametime spike was *worse looking toward the fire and better looking away*,
  which is a rendering cost, not a simulation one. A burn lasts
  `BurnDurationSeconds` (240 by default) and rendered a full flame effect for
  every second of it.

  After `SmoulderAfterFraction` of its burn (45% by default) a fire drops to
  embers and smoke: **the real-time Light is destroyed** — the single most
  expensive part per burner, and there was one per burning object — flames fall
  to a few dull embers with turbulence off, and smoke is kept but thinned,
  because smoke is what actually reads as "this is still smouldering".

  **The simulation is completely untouched.** It burns for exactly as long,
  spreads exactly the same, and hurts exactly as much. This is only what gets
  drawn.

  Done on both sides, and the client half is the one that matters: a dedicated
  server is headless, so its own effects render nothing — what a player sees is
  the mirror spawned from fire broadcasts. Each client runs the downgrade on
  its own clock from when it started showing that fire, so this costs no extra
  network traffic. The effect is mutated in place rather than destroyed and
  respawned, so there is no VFX churn. Disable with `SmoulderingVfxEnabled`.

  Server-side pass VERIFIED LIVE 2026-08-29: `[SMOULDER] 24 fire(s) dropped to
  embers.` — one cycle, 24 burners past the threshold, latched, no throw. The
  CLIENT half is the one that saves frames and can only be judged by eye; it
  logs solely on failure, and none appeared.
## 0.19.11

- **`WatchTheWorldBurn` — one switch for maximum devastation.** The opposite
  number to `LowSpecPreset`, live-settable with `fireset burntheworld true`.
  Fire becomes contagious the instant it lights; dirt paths, cultivated ground
  and **water** stop being firebreaks; rain no longer suppresses it; burned
  ground can relight immediately; fires start at full strength instead of
  ramping; nothing regrows; extinguishing no longer keeps anything wet; spread
  reach goes to maximum and the spread cycle to its fastest; the burning and
  ground caps go to their ceiling — **per fire**, which since 0.19.9 means
  several simultaneous blazes each get one.

  Two deliberate restraints:
  - **Your visual caps are left exactly as you set them.**
    `GroundVfxMaxConcurrent` and `GroundDamageMaxConcurrent` are what actually
    cost frames, so someone who wants the world to burn still decides how much
    of it their machine renders. A preset that maxed those too would just be a
    way to lock up a GPU.
  - **If `LowSpecPreset` is also on, low spec wins.** A machine that cannot
    cope is a harder constraint than a preference for spectacle, and getting
    that precedence backwards ends in somebody's game freezing.

  Like `LowSpecPreset` it resolves at read time and never writes to your
  config, so turning it off restores your own values exactly. `firestatus`
  shows a `burntheworld` flag reporting whether it is actually in force
  (which is false while low-spec overrides it).

  **VERIFIED LIVE 2026-08-29.** `fireset burntheworld true` relayed to a
  dedicated server mid-burn and every flag flipped in the next heartbeat:

  ```
  before: burning   22/150,  ground   39/150,  fires  3, maturity 25%, radius  8m, interval 0.75s,
          firebreaks True,  waterblocks True,  exhaustion True,  leash True,  ramp enabled True
  after:  burning 1316/2600, ground 6500/6500, fires 13, maturity  0%, radius 15m, interval 0.25s,
          firebreaks False, waterblocks False, exhaustion False, leash False, ramp enabled False
  ```

  Ground pinned at its ceiling (500 x 13 fires), so it was cap-limited rather
  than out of fuel. Server load: **one core saturated** (10.2 CPU-seconds per
  10s wall, single-threaded simulation) and still keeping cadence — the
  practical ceiling, which is the honest answer to "how much devastation fits".
  For scale, at 1316 burners the pre-0.19.8 code would have been doing ~2.6
  MILLION distance checks per cycle four times a second; this configuration
  only exists because of the spatial grid.

## 0.19.10

- **`fireset debug true|false` — debug logging is now live-settable and
  server-relayable.** `firedebug` only ever toggled the machine it was typed
  on, so turning verbose logging off on a dedicated server meant editing the
  config and restarting, which kicks everyone. It is now a normal `fireset`
  key like everything else, forwarded to the server and authorized there.
- **`firestatus` stopped reporting a nonsense cap.** Caps went per-event in
  0.19.9, so a global total printed against a per-event cap read as
  `ground 81/50` — which looks like a broken cap and is not: with two fires
  the real ceiling is 50 *each*. The line now shows capacity as cap x live
  events, so that reads `ground 81/100`.
## 0.19.9

- **Fires in different places are now separate fires.** Everything about a
  blaze used to be global — one origin, one ramp clock, one arsonist, one
  budget — and that had three consequences:
  - **A second fire beyond the first one's radius could not spread at all.**
    The spread-candidate sweep centred on wherever the FIRST fire started and
    reached a bounded distance. Light a fire, travel past that radius, light
    another: the second one burned but never caught anything, because it had
    no candidates. Reported from play ("tp'd far away... nothing propagates")
    and confirmed in the code. This is the headline fix.
  - **The first big fire starved every later one.** `MaxConcurrentBurning` was
    one global budget, so a maxed-out blaze denied any other fire the right to
    exist until it burned out. The cap is now per fire.
  - **A later fire inherited the first one's ramp and its arsonist**, so a
    natural fire could be attributed to whoever lit something else entirely.

  A fire event now owns its origin, ramp, igniter and budget. Ignitions join
  the nearest event within reach of it (ground leash plus a spread radius),
  otherwise they start their own; an event ends when its last burner and last
  ground cell go out. Ground spread is leashed against its own event's origin
  rather than a global one. Fires restored from the sidecar, which stores no
  event id, are clustered back into events by position on the first tick.
  `firestatus` reports a `fires N` count.

  **VERIFIED LIVE 2026-08-29** on a clean single-mod test server. Two fires lit
  ~1035m apart produced two events, and the second one spread — the case that
  was dead before:

  ```
  [EVENT] event 1 born at (-78.86, 83.44, 165.06) (igniter 775624); 1 active.
  [EVENT] event 2 born at (-230.65, 33.51, -859.07) (igniter 775624); 2 active.

  burning 1/50, queued 0/20, ground  0/50, fires 1     <- first fire only
  burning 3/50, queued 0/20, ground 10/50, fires 2     <- second fire lit AND spreading
  ```

  `zdoCandidates=21` throughout, so both blazes were getting a candidate sweep
  rather than one starving the other.

## 0.19.8

- **Spread stopped testing every burnable in the world against every fire.**
  A tester's own log finally showed the shape of the problem: hosting on
  0.19.3 with a maxed fire, they had **2176 spread candidates against 50
  burning objects and 46 ground cells**, and `SpreadPass` compared every
  candidate to every burner on each 0.75s cycle. That is roughly a quarter of
  a million distance checks a cycle, each carrying a type dispatch and a ZDO
  lookup — and their measured frametime spikes were 101.9-146.3ms, which is
  where that arithmetic lands.

  Candidates are static — trees and walls do not move — so they are now
  bucketed into a 16m spatial grid whenever the candidate list is rebuilt, and
  a burner only examines the cells its own reach touches. Cost follows the
  size of the fire instead of how much wood is lying around the map.
  Two smaller wins came with it: the cheap distance check now runs BEFORE the
  type dispatch and ZDO lookup rather than after, and bucket lists are pooled
  so re-bucketing does not allocate. Behaviour is unchanged — the same
  candidates ignite, they are just found without walking the whole world.

  Note for anyone reading the old advice: the earlier guess that affected
  testers were simply on the pre-0.18.6 build was WRONG. The tester was on
  0.19.3 and already had every prior performance fix; this loop was the part
  none of them touched.

## 0.19.7

- **`fireset lowspec` typed on a client never reached the server.** 0.19.6
  added `lowspec` to the command's switch but not to the map that forwards a
  setting to the server, so the command set the CLIENT's own config, reported
  success, and left the simulation untouched — the client console showed the
  reduced caps while the server's `firestatus` still read `lowspec False`.
  Caught the first time it was tested live. Every other key was already
  forwarded correctly; an audit of all 47 switch cases against the 47 map
  entries now shows them matching exactly, and the map carries a comment
  saying that adding a case without a map entry produces exactly this silent
  disagreement.

  Verified live afterwards, server-side, against a burning front — three
  consecutive status lines, with `fireset (remote from ...)` in the server log
  proving the command crossed the wire:

  ```
  burning 17/50, ground 42/50, vfxcap 30, dmgcap 50, interval 0.75s, lowspec False
  burning 16/20, ground 18/25, vfxcap 10, dmgcap 20, interval 2s,    lowspec True
  burning 17/50, ground 50/50, vfxcap 30, dmgcap 50, interval 0.75s, lowspec False
  ```

  Every cap dropped and every one came back. Ground cells fell 42 → 18 while
  the preset was on, so the simulation actually shed load rather than merely
  reporting smaller numbers, and the third line is the design's real claim
  observed: turning it off restored the configured values exactly, because
  the preset resolves at read time and never wrote to the config at all.

## 0.19.6

- **`LowSpecPreset` — one switch for a machine that struggles.** Instead of
  learning eight settings, set `LowSpecPreset = true` (or `fireset lowspec
  true`, live, no restart). It caps burning pieces at 20, ground cells at 25,
  ground-fire visuals at 10 and damage zones at 20, drops scorch decals, and
  slows the spread cycle to at least 2s. Fire still spreads and still burns
  things down — there is simply less of it at once.

  Two properties worth stating, because both are easy to get wrong:
  - **It never writes to your config.** The preset resolves at read time, so
    your own values are untouched and switching it back off restores them
    exactly. Implemented by assigning values instead, BepInEx would have
    persisted the preset's numbers over the player's and there would be no
    way back.
  - **It only ever makes things cheaper.** Every cap takes the *lower* of
    yours and the preset's, so anyone already tuned below these keeps their
    own number; the spread interval takes the *higher*, since a longer
    interval is the cheap direction. The preset is a ceiling on cost, never
    an instruction to raise anything.

  `firestatus` reports the values actually in force rather than the
  configured ones, plus a `lowspec` flag, so the line can never disagree with
  what the simulation is enforcing.

## 0.19.5

- **Cut the size of the periodic frametime spike, not just how often it
  happens.** 0.18.6 stopped rebuilding the spread-candidate picture every
  0.75s and cached it for 5s — which made the hitch rarer without making it
  any smaller. Three costs went into each rebuild, and two of them scaled
  with how much stuff was lying around the world rather than with the fire:
  - The **ZDO sector sweep now follows the live fire front** instead of the
    leash. The leash is a lifetime maximum, so a fire five cells across swept
    the same 150m radius as one that had burned for an hour — 7x7 = 49 zones
    every rebuild regardless of size. The radius is now the furthest burner
    plus one full spread reach, still capped at the old figure, so it can
    never sweep more than before and a young fire sweeps one or nine zones.
  - The **`FindObjectsOfType` tree and log scans are now a fallback**, not
    the default. They walk every loaded GameObject in the scene, so their
    cost rides the world's object count — worst exactly where a tester has a
    forest and a field of dropped wood. The ZDO sweep already resolves trees
    and logs authoritatively, so the scans now run only on a peer that could
    not read the ZDO layer at all.
- **Scorch marks and fire VFX stopped allocating per spawn.** Every scorch
  mark called `CreatePrimitive`, which builds a fresh mesh *and* a collider
  only to destroy the collider on the next line, plus a new `Material`; every
  particle effect instantiated its own `Material` too, always with the same
  shader and texture. One shared quad mesh and one shared material each now.
  Marks spawn per burned ground cell, so on a spreading front that churn was
  continuous — and allocation churn on the render thread is the same shape of
  problem 0.18.7 chased out of the logging path.

  Verified in-game on the dedicated server the same day: a relayed
  `startfire 10` lit three trees, and across the burn `zdoCandidates` read 4
  and then 7 — the sweep tracking the front outward exactly as intended —
  while ground fire seeded and spread (0 → 10 cells, then decaying as cells
  exhausted). The failure mode this had to rule out was a sweep too tight to
  find fuel, which would have shown as `zdoCandidates=0` and a fire that sat
  still; neither happened. The SIZE of the saving is still unmeasured — that
  needs a frametime capture, not a log.

## 0.19.4

- **The Dousing Bomb was never missing — the warning was wrong.** Every start,
  client and server alike, logged `donor prefab 'BombOoze' not found in ObjectDB —
  Dousing Bomb unavailable`, then created the bomb successfully four log lines
  later. Registration is hooked to both `ObjectDB.Awake` and `CopyOtherDB` precisely
  because the first Awake fires on the bootstrap ObjectDB, before the game's items
  exist; the retry was always working. Only the logging was wrong, and wrong in the
  worst direction — it told every user a feature was broken when it was not, and it
  spent the one-shot `_failureLogged` flag on a non-event, so a *genuine* absence
  could never have been reported afterwards. The warning now fires only when a fully
  loaded ObjectDB is missing the donor, which is the real fault it was meant to
  describe. No behaviour change: the bomb crafted before this and crafts now.

## 0.19.3

- **`startfire` actually finds targets on a dedicated server.** Caught live
  during relay verification: `startfire 10` in a meadow full of burnables
  answered "attempted 0 targets" — its target scan still walked instance
  lists, which are empty on a headless server (the same root cause the
  0.17.4 spread fix addressed; this command's own scan was never converted).
  It now also sweeps the ZDO layer — the census a headless server actually
  keeps — creating instances only for real ignitions, with anything the
  instance pass already lit skipped so the count never doubles.

## 0.19.2

- **A burned spot can no longer queue two regrown trees.** Seen live: the same
  Beech1 position pending regrowth twice — one entry deep into its retry
  attempts, one fresh — which would have spawned two overlapping trees. Both
  paths that queue regrowth (a tree burning down, and restoring the fire
  sidecar after a restart) now dedupe by position; the existing entry wins
  because its attempt count is real history.

## 0.19.1

- **Fixed relayed commands dying at "Admin only." on clients.** The local
  admin check ran BEFORE the relay, and a client's admin flag only syncs
  after running `devcommands` — so genuine admins got blocked while the
  server's real authorization never got a say (caught live: three commands,
  three rejections, zero relays). Relayable commands now relay first; the
  server judges the sending peer against its own adminlist — the check that
  actually matters — and the local gate only guards direct host/server
  console execution. (Workaround on 0.19.0 clients: run `devcommands` once.)

## 0.19.0

- **Every server command now works from anywhere.** `startfire`,
  `clearfires`, `firegroundignite`, `firetreeregrow`, and
  `firetreeregrowlist` used to refuse with "only works run from the server"
  when typed on a client. They now relay: the command runs on the server —
  authorized against the server's own adminlist for the SENDING peer,
  vanilla's exact kick/ban check, never the typist's local claim — and every
  line of output streams back to your console as `[server] ...`. Radius
  commands act around the requesting player (the server-tracked position, not
  a client-supplied one). Only a fixed whitelist of FireFront's own commands
  can relay; crosshair commands (`ignite`, `stopfire`) keep their dedicated
  forwards since target picking is inherently local.

## 0.18.8

- **`firestatus` answers from the server.** A client's local status always
  read burning 0 / ground 0 — the counts live on the server. It now requests
  the authoritative line and prints it as `[server] FireFront: ...`.

## 0.18.7

- **Killed the GC frame spikes — debug logging is now free when off.** A tester
  clip (steady ~10ms baseline, CPU and GPU both far from saturated, isolated
  spikes to ~80ms every few seconds) showed the signature of Mono GC pauses.
  The feeder: every debug trace built its log string BEFORE checking whether
  debug logging was on — hundreds of dead strings a second during a big fire —
  and debug logging also defaulted ON, adding BepInEx console/file I/O on top.
  Debug calls now use an interpolated-string handler (the compiler skips all
  formatting when disabled, verified in the compiled output), and the config
  key was renamed VerboseLogging → DebugLogging (default off) so existing
  configs stuck on the old always-on default go quiet on upgrade. `firedebug`
  still toggles it live when you actually want the firehose.

## 0.18.6

- **Fixed the periodic frametime spike during big fires.** Two causes, both
  cadence-shaped: the server rebuilt its whole spread-candidate picture every
  0.75s cycle (three scene scans plus a ZDO sector sweep whose radius follows
  the ground leash — at leash 150m that walked 49 zones per cycle), and the
  client spawned a full second's batch of ground-fire particle systems in one
  frame on every sync flush. Candidates are static trees and walls, so the
  scan is now cached and rebuilt every 5s (immediately on a new fire); remote
  VFX spawns drain a few per frame from a queue. No behavior change — same
  fire, smoother frames, biggest win on machines hosting server and client
  together.

## 0.18.5

- **Front pace now tied to burn time.** A burning object must burn
  `SpreadMaturityFraction` of its burn duration (default 0.25 — about a minute
  at the default 240s burn) before it can ignite neighbors or seed the ground
  under itself. Before, a just-caught tree could torch its entire reach on the
  very next spread cycle while itself burning for four minutes, so the front
  raced ahead at a pace disconnected from the fuel. A burning-but-immature
  tree still glows and hurts — it just isn't throwing fire yet. Ground fire's
  own cell-to-cell crawl is unchanged. `fireset firematurity`, 0 restores the
  old instant contagion. Burn age persists across restarts (restored fires
  keep their maturity).

## 0.18.4

- **Dousing now holds — firefighting is winnable.** Anything deliberately
  extinguished (dousing bomb, extinguish key, stopfire) is soaked for
  `DouseImmunitySeconds` (default 90s, `fireset douseimmunity`) and can't
  re-ignite. Before this, the surrounding fire re-lit every doused cell and
  object within a cycle or two, so a bomb's cleared hole refilled itself in
  seconds and a ramped fire was hopeless to fight ("the ramp is too aggressive
  to fight" — the ramp was fine; the dousing just didn't stick). Now a line of
  bombs cuts a genuine firebreak ahead of the front. 0 disables.

## 0.18.3

- **`fireset` now applies on the server no matter where you type it.** Console
  commands run where they're typed, and every FireFront setting that matters is
  read by the server's simulation — so a client's fireset used to change its own
  irrelevant config copy and silently do nothing ("rampstart didn't work",
  "burnbuildings didn't work"). A client's fireset now also forwards to the
  server over a new RPC and lands on the server's real config, with the same
  value parsing and range clamping. The server logs every remote set with the
  sender's peer id.

## 0.18.2

- **New config `BurnPlayerBuildings` (default true) + `fireset burnbuildings`.**
  Set false for an anti-grief server: fire never ignites anything carrying a
  placement creator stamp — walls, floors, furniture — by any path (spread, fire
  arrows, console), while world-generated structures still burn. The wildfire
  still crawls past a protected base and still hurts anyone standing in it. This
  is the hard guarantee on top of the terrain firebreak, which already stops
  spread at leveled/pathed base ground but not deliberate arson.

## 0.18.1

- **Fixed: fire went invisible for a client that reconnected without relaunching.**
  Valheim creates a fresh routing instance per connection; FireFront registered its
  network handlers once per process, so a client kicked by a server restart that
  auto-rejoined got none of the fire broadcasts — the fire burned, invisibly, until
  the game was fully relaunched. Handlers now re-register whenever the routing
  instance changes. Receipt-side `[SYNC-DIAG]` debug traces are kept so this class
  of silent drop is diagnosable from a single log in future.

## 0.18.0

- **Fires survive server restarts.** Burning objects and ground fire come back with
  their remaining burn time, spent fuel stays spent, the fire keeps its origin,
  ramp age, and arsonist, and trees waiting to regrow still regrow. Stored in a
  small sidecar file next to the world save, written every 60s and on shutdown —
  a hard kill loses at most the last minute of fire drift. Toggle with `fireset
  persistfires`.

## 0.17.6

- Fixed a `FieldAccessException` spamming the main menu from 0.17.5's item
  registration (a private-in-the-real-assembly ObjectDB field). Registration
  failures now degrade to "item missing" with one warning, never menu errors.

## 0.17.5

- **New item: the Dousing Bomb.** A throwable that extinguishes everything within
  ~6m of impact — ground fire and burning structures/trees alike. Hand-craftable:
  3 Resin + 2 Leather scraps makes 3. Tune the blast with `fireset dousingradius`.
- The extinguish key's radius now also clears burning objects around you, not just
  ground fire.

## 0.17.4

- **Forest spread now actually works on dedicated servers.** Object-to-object and
  ground-to-object spread had never worked there — the headless server tracks the
  world as ZDOs and never instantiates the GameObjects the old candidate scan
  looked for, so the server literally could not see trees. Spread candidates now
  come from the ZDO layer, and an instance is created only for objects that
  actually catch fire.

## 0.17.3

- **Fires remember who lit them.** The spreading front carries its igniter's player
  id, captured once per fire event from the actual attacker (not the network sender),
  and reset when the fires die. Natural and creature fire belongs to nobody. This
  feeds Ragnarok's Wrath's arson attribution.
- The ignite request RPC was renamed so a mixed-version server/client pair quietly
  no-ops instead of desyncing — update both sides together.

## 0.17.2

- **Wind bias now scales with real wind strength.** A gale drives a long narrow
  tongue of fire; dead calm burns evenly in all directions. `WindInfluence` (0–1,
  default 1) dials how much weather shapes the front.
- Public read API (`FireManager.CollectActiveFirePositions`) for companion mods —
  Ragnarok's Wrath reads it to scar burning zones.

## 0.17.0 – 0.17.1

- Procedural fire visuals now default **on** — earlier builds simulated fire fully
  but rendered nothing until a console toggle. Upgrading an existing install? BepInEx
  keeps your old `UseProceduralVfx = false`; flip it to true or delete the line.
- Floating ground fire fixed for real: terrain height now raycasts Valheim's own
  terrain layer instead of trusting a call that echoed its input back.

## Earlier (0.1 – 0.16)

The road here, condensed: ignition from vanilla fire damage; spread with caps, a
queue, and ramp-up; ground fire as an invisible cell grid with its own visuals,
damage, and a 40m leash; rain suppression; firebreaks on dirt and cultivated ground;
water blocking; fuel exhaustion and burn scars; tree felling with real drops and
timed regrowth; the G-key extinguisher. The full engineering log — every bug and
what it taught — lives in the repo at `docs/DEVLOG.md`.
