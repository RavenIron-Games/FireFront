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
   `Regrowth dedupe:` debug line or simply no double entries.
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
