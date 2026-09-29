# qnova

![QNOVA splash screen](docs/splash.png)

A small C# Quake clone focused on Quake 1 movement and weapons.

- `src/Qnova.Core` – headless simulation (no graphics): swept-AABB collision, Quake ground/air movement
  (friction, accelerate, air-strafe/strafe-jumping, step-up, no-autohop jump), weapons and splash damage.
- `src/Qnova.Game` – Raylib-cs 3D frontend with a test arena and target dummies.
- `tests/Qnova.Tests` – xunit tests for movement and weapons.

```
dotnet test
dotnet run --project src/Qnova.Game
```

Controls: WASD, Space (jump, release between hops), mouse look, LMB fire, 1-9 or wheel to change weapon, M mute, R reset, ` (~) opens the console.

Weapons (Q1 stats): Axe, Shotgun (6x4), Double Shotgun (14x4), Nailgun (9), Super Nailgun (18),
Grenade Launcher (bouncing, 2.5s fuse), Rocket Launcher (100-120 direct, 120 splash, self-damage halved,
knockback = damage x 8 so rocket jumps work).

Sound: all effects (weapon fire, grenade bounce, dry-fire click, explosions) are synthesized in code by
`SoundSynth` in `Qnova.Core`, so there are no audio assets to ship. Volume falls off with distance.

## Console

Press `~` to drop the console (game pauses; Esc or `~` closes it). Type a cvar name to see it, `name value`
to set it; `;` separates commands; TAB completes; Up/Down recall history; PgUp/PgDn scroll.

- `help [name]`, `cvarlist [prefix]`, `cmdlist`, `find <text>`, `set`, `toggle`, `reset <cvar>|all`, `echo`, `clear`, `quit`
- Movement cvars: `sv_gravity`, `sv_maxspeed`, `sv_accelerate`, `sv_airaccelerate`, `sv_aircap`, `sv_friction`,
  `sv_stopspeed`, `sv_jumpspeed`, `sv_stepheight`, `sv_autohop`. Client: `sensitivity`, `fov`, `volume`.
- Gameplay commands: `weapon <1-7|name>`, `getpos`, `stats`, `respawn`, `kill`, `mute`.
- Cheats (need `sv_cheats 1`; turning it back off reverts them): `god`, `noclip`, `sv_infiniteammo`,
  `give <all|health|ammo|shells|nails|rockets|weapon> [n]`, `setpos x y z`, `spawn [n]`, `killtargets`, `host_timescale`.

## Map and bots

The arena (`Arena.Build`) is 4096 x 4096 units: a central mesa with stairs on all four sides, four walled
bunkers with doorways, a ring of pillars, an east ledge, crates you can jump onto and cover walls.
One bot spawns at start. Bots use the same movement, weapons and damage rules as you (they obey `sv_gravity` etc.):
they see you by line of sight, hunt your last known position, strafe and hop in a fight, lead projectile shots and
aim rockets at your feet, and pick a weapon by range. Both sides respawn after 3 seconds at the spawn point
farthest from the enemy. The scoreboard (top right) shows frags / deaths; suicide costs a frag.

- `bot_add [n]`, `bot_removeall`, `bots` (scoreboard), `bot_skill 1-5` (aim error, reaction time, turn speed, shot delay), `bot_ai 0|1` (freeze bots)

## Pickups

Health (+25), shells, nails and rockets boxes, and a floor weapon for each gun. You start with only the axe and
shotgun and lose extra guns when you die. Weapons: double shotgun on the central mesa, nailgun / super nailgun /
grenade launcher in the bunkers, rocket launcher on the east ledge. A collected item is gone for **30 seconds**
(`sv_pickup_respawn`), then respawns with a sound. Items you can't use (full health, full ammo) stay put; ammo caps at
100 shells / 200 nails / 100 rockets. Bots collect items too and head for health when hurt. `pickups` lists what's ready.
Pickups are drawn as procedurally painted pixel-art sprites (no image files): a distinct silhouette for each of the eight guns, a first-aid case, and shells / nails / rockets / cells / slugs, each on a coloured glow that matches its light. They bob over a floor pad and always face the camera; `r_plain 1` swaps them back to flat coloured cubes.

## Capture the flag

Pick **GAME MODE: CAPTURE THE FLAG** in the menu (or `gamemode ctf` in the console; `gamemode dm` goes back). It reloads the current map
as a 2v2: you and one bot are **Red**, two bots are **Blue**, with no friendly fire (the crosshair stays white over teammates).

- Touch the enemy flag to pick it up; carry it to your own flag while your flag is home to score (+1 for the team, +5 frags).
- Killing a carrier drops the flag there (+2 frags); a teammate touching a dropped flag returns it, or it goes home by itself after 30 s.
- First to `capturelimit` (default 5) wins; a few seconds later scores reset for a new match. `flags` shows score and flag states.
- The Classic Arena has bases at the north and south walls, each behind a cover wall; random maps put them at the two farthest-apart spawns.
- Bots play it: one attacker per team goes for the enemy flag (the second red attacker escorts you), defenders loiter near base, everyone
  returns a dropped flag or hunts the thief, and a carrier runs home instead of duelling. Bot navigation is simple steering, so on
  random maps with stairs and walls between the bases they may struggle to get across.

## Team Fortress

Pick **GAME MODE: TEAM FORTRESS** in the menu (or `gamemode tf`, or start with `--tf`). It is capture the flag (same 2v2 teams, flags and
scoring) with **classes**: every spawn gives you a fixed loadout, health pool and run speed instead of scavenging for guns.

| Class | Health | Speed | Weapons |
|---|---|---|---|
| Scout | 75 | 1.30x | Axe, Shotgun, **Double Shotgun** |
| Soldier | 125 | 0.90x | Axe, Shotgun, **Rocket Launcher** |
| Demoman | 100 | 1.00x | Axe, Shotgun, **Grenade Launcher** |
| Medic | 90 | 1.10x | Axe, Shotgun, **Nailgun**; heals teammates in line of sight within 320 units (8 hp/s) and itself (2 hp/s) |
| Heavy | 200 | 0.75x | Axe, Double Shotgun, **Super Nailgun** |
| Sniper | 80 | 1.00x | Axe, Shotgun, **Railgun** |

Choose with `class <name|1-6>` or Options > CLASS; it applies when you next respawn (`kill` to switch now). `class` alone lists them. Bots get a
spread of classes and only use weapons their class owns. Class logic is in `Qnova.Core/Classes.cs` and `TeamFortress.cs`.

## Splash screen and menu

The game opens on a dark, gritty splash: **QNOVA** in riveted steel with a furnace burning in the O that detonates every
few seconds (fireball, sparks, smoke, shockwave, screen shake). Everything is drawn procedurally, with no image assets.

- **START GAME / RANDOM MAP / CLASSIC ARENA / GAME MODE / OPTIONS / QUIT** — arrows or W/S to move, Enter to confirm, mouse hover/click also works.
- **OPTIONS** — mouse sensitivity, field of view, volume, bot skill, number of bots (0-4), auto bunny-hop, zoom FOV, plain blocks (flat untextured rendering; also `r_plain 1` in the console), damage numbers, key bindings. Left/Right adjust; Esc goes back.
- **Esc in game** opens the same screen as a pause menu (START becomes RESUME GAME); Esc again resumes.

Menu logic lives in `Qnova.Core/Menu.cs` (unit-tested); drawing is in `Qnova.Game/Splash.cs`.

### Developer flags

Handy for headless screenshots (e.g. under `xvfb-run` with `LIBGL_ALWAYS_SOFTWARE=1`): `--start` (skip the menu), `--paused`,
`--options`, `--console`, `--exec "<console line>"`, `--shot <name.png> --shot-after <seconds>` (saved in the working directory, then exit).

## Map look

The arena is lit by a custom shader (`Qnova.Game/MapRenderer.cs`) with no image assets: procedural stone-slab floors,
concrete-block walls, bolted metal plates and dark ceiling plating, all grimed and cracked, plus point lights
(torches, ceiling lamps, cold bunker lamps, flickering red beacons), dynamic lights for rockets, explosions and muzzle flashes,
and dark distance fog. Map data (surfaces, lights, girders/trim/fixtures) lives in `Qnova.Core` (`MapVisuals.cs`, `Arena.cs`).

## Key bindings

Options > **KEY BINDINGS** lists every action (move, jump, fire, each weapon, next/previous weapon, mute, respawn). Press Enter on
a row, then press the new key, mouse button or wheel notch. Esc cancels, Backspace/Delete unbinds. Binding a key that is already
in use takes it from its old action (the menu tells you). Esc and ` (console) are fixed. "Reset to defaults" restores the originals.

Bindings are saved to `bindings.cfg` in your config folder (`~/.config/qnova` on Linux, `%APPDATA%\qnova` on Windows; override with
`QNOVA_CONFIG_DIR`). Console equivalents: `bind <key> <action>`, `unbind <key|action>`, `unbindall`, `bind_reset`, `bindlist`.
Key names are the upper-case Raylib names (`W`, `SPACE`, `LEFTSHIFT`, `UP`, `ONE`...) plus `MOUSE1`-`MOUSE5`, `MWHEELUP`, `MWHEELDOWN`.
Actions: forward back moveleft moveright jump attack weapon1-weapon7 nextweapon prevweapon mute respawn.
Dev flags: `--no-config` (ignore the saved file), `--keybinds [--capture]` (open the bindings screen).

## Zoom, kill messages and cheats

- **Zoom**: hold **MOUSE3** (middle button; rebindable, action `zoom`) to ease into a narrower view; look speed scales with the view so aiming stays precise.
  Set the magnification with Options > ZOOM FOV or `zoom_fov` (default 30; smaller = more zoom).
- **Kill messages** name the weapon: `Bot1 killed You with the Rocket Launcher`, `You killed Bot1 with the Double Shotgun`,
  and a self-kill reads `You suicided (Rocket Launcher)`.
- **Cheats** (need `sv_cheats 1`): `giveall` (every weapon + full ammo, health untouched), `impulse 9` (same, Quake-style),
  and the existing `give all` (everything *and* full health).

## Grappling hook and launchpads

- **Grappling hook**: press **F** (rebindable, action `grapple`) to fire a fast hook along your view. It sticks to the first wall, floor or
  ceiling it hits and reels you in at `sv_hookspeed` (default 800) for as long as you hold F; you hang against the anchor once you arrive.
  **Hold jump while hooked to swing instead**: the rope becomes a fixed-length line, gravity swings you like a pendulum, and you can strafe to
  pump the swing; let go of jump to start reeling in again. **Release F to let go and keep your momentum**, which is how you slingshot. Range is `sv_hookrange` (1600). The hook doesn't grab players,
  bots or dummies, and lets go if you die or get snagged on geometry and stop making progress. Press again after a brief cooldown.
- **Launchpads** (Quake 3 style): glowing cyan plates. Step on one and you're flung along a ballistic arc whose *apex* is the pad's target
  point (same formula as Q3's `trigger_push`), so it adapts to `sv_gravity`. The arena has four: up to the east ledge, over the stairs onto the
  mesa, a long hop from the north wall onto the mesa, and back off the ledge. Bots get launched too.

## The holy trifecta: Rocket Launcher, Lightning Gun, Railgun

- **Lightning Gun** (slot 8, `weapon lg`): a continuous beam, Quake 3 numbers: 8 damage every 50 ms (~160 dps), 768 range, one **cell** per
  shot. Hits the first target only. Drawn as a crackling blue bolt.
- **Railgun** (slot 9, `weapon rail`): instant 100 damage, **pierces every player and dummy in line** (stopped only by walls), 1.5 s refire,
  one **slug** per shot, lingering blue corkscrew trail. One shot kills a full-health player.
- New ammo: cells (max 200) and slugs (max 50), with ammo boxes and gun pickups: the Lightning Gun is in the last bunker, the Railgun at the far
  end of the east ledge. You spawn with 50 cells and 5 slugs. Bots use both (lightning at mid range, rockets further out, the rail at long range
  once they are skilled enough) and now only chase items on their own level.
- Cheats: `give cells|slugs [n]`, `giveall` / `impulse 9` include everything.

## Hit feedback

- **Hit flash**: enemies (bots and dummies) light up bright white for a moment when you damage them (`cl_hitflash 0` turns it off).
- **Red crosshair**: the crosshair turns red and thickens when the weapon in your hands would hit a living enemy right now, so it respects reach:
  the lightning gun only goes red within 768 units, the axe within 64, the rail across the whole map, and walls in the way keep it white.
- **Damage numbers**: arcade-style numbers pop above an enemy's head when you hit it, then float up and fade. Rapid hits (the lightning gun, shotgun
  pellets) merge into one running total that re-pops as it grows. Colour goes white, yellow, orange, red with size, and killing blows are big, red and end in `!`.
  Toggle with Options > DAMAGE NUMBERS or `cl_damagenumbers 0|1` (on by default).

## Random maps

Pick **RANDOM MAP** on the main menu (or `map random [seed]` in the console) to play a freshly generated arena; **CLASSIC ARENA** / `map arena`
returns to the hand-built one. Each map prints its seed (`map random 42177` replays it exactly) and shows its name in the bottom-right corner.
Loading a map swaps the world in place: your console, key bindings, settings and bots carry over, scores reset, and everyone respawns.

Each generated map is 3072-5120 units across with a 640-896 ceiling and contains: 2-3 raised platforms reached by stairs, up to 2 walled bunkers with
doorways, pillars with torches, cover walls and jumpable crates, ceiling girders, wall and ceiling lights, 1-3 launch pads, all seven pick-up
weapons (the best on platforms and in bunkers), health and every ammo type, 8+ spawn points and 3 target dummies.

The generator never trusts its own output: a map is only used if it passes `MapGenerator.Validate` — spawns and pickups sit on solid ground, a
flood fill proves every spawn, floor pickup, pad and stair foot is reachable on foot from the player spawn, and every launch pad's arc is simulated
with the player hull to make sure it lands on a platform. A map that fails is discarded and another attempt is made from the same seed.
Dev flag: `--map arena|random|random:<seed>`.
