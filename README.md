🔥 **FireFront**

Fire that actually spreads. Torch a wall and it can take the whole build with it, jump to the treeline, and crawl across open ground to get there — not just "this one piece is on fire," an actual moving front.

**Requires:** BepInEx

**Install:** drop `FireFront.dll` into `BepInEx/plugins/`, launch, done. No config needed to just play — everything below is for people who want to poke at it. On a dedicated server, the server needs the dll too (it runs the actual fire; your client just shows it to you).

—

**What it does**
• Structures, standing trees, and felled logs can all catch fire and burn down for real — with real drops (trees fell via vanilla's own felling and leave logs, logs leave wood)
• Fire spreads to nearby burnable stuff, structure-to-structure, tree-to-tree, or structure-to-tree
• **Big fix in 0.17.4: forest spread now actually works on dedicated servers.** It turns out tree-to-tree and ground-to-tree spread had *never* worked on a dedicated server (fine in single-player) — the server literally couldn't see trees. If you tested on a server before and thought fire seemed weirdly tame, that was this. Burn a forest and see the difference.
• Fire also spreads across open ground/grass between things too far apart to ignite each other directly — you can *see* it crawling through grass as small ember flickers
• Standing in fire hurts, players and mobs alike — same mechanism vanilla campfires use
• **New: fire follows the wind.** The front stretches downwind and thins out upwind, using the game's real wind — and how *hard* it's blowing matters now too. A gale drives a long narrow tongue of fire; a calm day burns in a lazy, even circle. Watch a fire when the weather turns.
• Fires start small and ramp up over ~10 minutes instead of instantly raging
• **New in 0.18.5: fire spreads at the pace of its fuel.** A burning tree has to be properly alight (~a minute in) before it starts torching neighbors and dropping fire to the ground — no more front teleporting through a forest faster than anything actually burns. Tune with `fireset firematurity` (0 = old instant spread)
• **Fixed in 0.18.6/0.18.7: the periodic stutter during big fires is gone.** A tester clip (thank you — frametime graphs are gold) showed regular frame spikes every few seconds while a forest burned. Both feeders are dead: a periodic bookkeeping scan and debug logging that built its strings even when switched off. If big fires used to hiccup for you, try your worst on this build
• Rain douses grass fire (an already-burning building keeps going)
• Burned ground is spent — it can't relight for ~90s, so the front *advances* instead of churning in place, and it leaves burn scars behind
• Dirt paths and cultivated ground are real firebreaks; water stops spread too. **This protects your base more than you'd expect**: the leveled/pathed ground most bases sit on counts as fuel-free, so a wildfire will burn right up to the edge of your yard and stall there — your walls only catch if fire starts *inside* the perimeter (or you clear less ground). If it looks like "fire can't touch my buildings," it's actually your groundwork doing its job — keep a tended break around your base and it genuinely works, exactly like real firefighting
• Ground fire won't wander more than ~40m from where the fire started
• Burned trees regrow after ~15 minutes if the spot's still clear
• Press **G** to extinguish what you're looking at plus all fire around you — ground fire *and* burning structures/trees
• **New in 0.17.5: the Dousing Bomb.** A throwable that puts fire OUT — everything within ~6m of where it lands, grass fire and burning buildings/trees alike. Hand-craftable anywhere, cheap on purpose: 3 Resin + 2 Leather scraps makes 3 bombs. (It borrows the ooze bomb's look for now — yes, the fire extinguisher is green. Art later, function first.) Fight a fire for real instead of just G-spamming next to it.
• **Better in 0.18.4: dousing sticks.** Everything you extinguish is *soaked* for ~90s and can't re-light, so a line of bombs cuts a real firebreak ahead of the front instead of the fire instantly refilling the hole. Big fires are now genuinely fightable — get ahead of the front and cut it off, like actual wildfire crews do
• Fires now remember who lit them — the whole spreading front carries its arsonist, even fire that crawled a long way from the first spark (natural/creature fire belongs to nobody). Nothing visible in-game yet; it feeds a companion mod's reputation system
• **New in 0.18.0: fires survive server restarts.** Burning stuff comes back burning with its remaining time, burned-out ground stays spent, and trees waiting to regrow still regrow — a reboot no longer resets the world's fire state
• **New in 0.18.2: server owners can make player builds fireproof.** `fireset burnbuildings false` (or the `BurnPlayerBuildings` config) means fire never ignites anything a player placed — not by spread, not by fire arrows, not by anything — while ruins and world structures still burn. The anti-grief switch, for servers that want wildfires without arson
• **New in 0.19.0: every command works from anywhere.** `firestatus`, `startfire`, `clearfires` and friends used to refuse or answer wrong unless you typed them on the server itself — now they run on the server no matter where you type them (checked against the server's own admin list) and the output comes back to your console as `[server] ...`
• Everything's config-tunable and adjustable live via console — no restart needed

**Heads-up if updating from an older build:** update server and client together — a mixed-version pair (0.17.3 with anything older) means clicking `ignite` from a client silently does nothing.

**Want the world to actually burn? `fireset burntheworld true`**
The opposite switch. Fire catches instantly, dirt paths and even WATER stop stopping it, rain does not put it out, burned ground relights, fires start at full strength, nothing grows back, and the caps go to maximum — per fire, so several blazes can rage at once. Your visual settings are left alone on purpose: those are what cost frames, so you still choose how much of it your machine draws. Your own settings are untouched and turning it off puts everything back. (If you also have lowspec on, lowspec wins.)

**Struggling machine? Try `fireset lowspec true`**
New in 0.19.6, and the first thing to reach for if big fires cost you frames. One switch: fewer things burning at once, fewer fire visuals, no scorch decals, and a slower spread tick. Fire still spreads and still burns your base down — there's just less happening simultaneously. It doesn't touch your own settings (anything you've already set lower is kept, and turning it off puts everything back), and `firestatus` shows you exactly what's in force. Also settable as `LowSpecPreset` in the config file if you'd rather not use the console.

If you've been running an older build and big fires stuttered, **update first** — 0.18.6, 0.18.7 and 0.19.5 each removed a separate cause of that, and the newest one cut out a full-scene scan whose cost grew with how much stuff was lying around your world.

**What to expect / known limits**
• The fire visual is homemade, not a vanilla asset — it'll look like fire, just not *exactly* like Valheim's own fire
• Default settings are tuned aggressive for testing — expect fire to spread fast and hungrily unless you dial it down yourself

—

**Useful console commands** (` key to open console)
```
firestatus              — see what's currently burning + all current settings
ignite                  — ignite whatever's under your crosshair
startfire [radius]      — ignite everything burnable within radius of you
stopfire                — extinguish whatever's under your crosshair
clearfires              — nuke every active fire instantly
firedebug               — toggle verbose fire logging
fireset <key> <value>   — live-tune settings, no restart needed (applies on the server no matter where you type it)
```
Full list of tunable `fireset` keys: `burnduration`, `firematurity`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `burnbuildings`, `vfx`, `procedural`, `groundenabled`, `groundcellsize`, `groundradius`, `groundburnduration`, `groundmax`, `groundvfxmax`, `grounddamagemax`, `firehurts`, `firehurtsplayeronly`, `firehurtsradius`, `firedamage`, `firetickinterval`, `extinguishradius`, `douseimmunity`, `rainsuppress`, `rainmultiplier`, `scorchmarks`, `scorchlifetime`, `dirtpaint`, `dirtpaintradius`, `rampenabled`, `rampduration`, `rampstart`, `exhaustionenabled`, `fuelregrow`, `windbias`, `windupwindchance`, `windinfluence`, `dousingradius`, `persistfires`, `firebreaks`, `treeregrowth`, `treeregrowthseconds`, `groundleashenabled`, `groundleashdistance`, `lowspec`, `debug`, `burntheworld`, `enabled`

Wind ones worth playing with: `fireset windinfluence 0` ignores wind entirely (old-style even spread), `1` is full effect (the default). `firestatus` shows the live wind strength the fire is currently feeling.

There are a few more diagnostic-only commands (`firelistprefabs`, `firecheckprefab`, `firepurgevfx`, `firegroundignite`, `firetreeregrow`, `firetreeregrowlist`) mostly meant for dev-side debugging — ask in here if you're curious what they do.

—

**If something breaks**
Please grab your `LogOutput.log` (BepInEx folder) and send it over, especially if you see a wall of red repeating errors. Screenshots of weird spread behavior are also genuinely useful — "this jumped way further than it should have" is easier to diagnose with a picture than a description. For wind specifically: a screenshot of a burn scar plus which way the wind was blowing is exactly the evidence we need.

Thanks for testing — this thing has had a real rough-and-tumble development process (ask if you want the story), so any weirdness you catch now saves everyone a headache later.
