# Particle Fleet Turbo

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Particle Fleet: Emergence** that makes script-heavy
custom maps run at full speed.

Particle Fleet advances the simulation by one step per rendered frame at 30 fps. When a map's PRPL scripts take
longer than a frame's budget, the whole game runs in slow motion, even with nothing happening on the map.
This plugin speeds up the engine paths those scripts use most, without changing what the scripts see:

- **`GetCoresWithVar`** results are cached. The engine normally scans every script instance on the map and
  allocates a new set on every call. The cache is invalidated whenever a script writes the queried variable or
  script instances are added or removed. Results that depend on list values (which can change in place) are
  never cached.
- **`SetImage`** is skipped when the slot already shows that image. The engine otherwise re-validates the slot name
  with a regular expression, reallocates and reapplies the texture on every call.
- Image position, scale, rotation and colour writes that would not change anything are skipped.

On Exchange map #1402 (about 170 networked script units), frame time went from 52–55 ms to 34 ms (the game caps
at 33.3 ms) and script time from 40 ms to 19 ms.

## Installing

Download the release zip and follow `INSTALL.txt` inside it. In short: extract it into the game folder; on
Linux/Steam Deck run the Windows version through Proton with the launch option
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

The bundled `BepInEx/config/BepInEx.cfg` sets the BepInEx entrypoint to `MonoBehaviour`. With BepInEx's default
entrypoint, plugins load while Unity 5.3 is still registering scripts, and the game either crashes or shows a
black screen.

In game: **F10** toggles the optimizations, **F9** shows a profiler overlay (frame time, time per map script).

## Verifying behaviour

The game simulation is deterministic, which makes behaviour easy to check. With `SelfTest.Key` set (e.g. `F11`)
in `BepInEx/config/petru.particlefleet.turbo.cfg`, pressing it during a mission snapshots the game, replays the
next 300 ticks with the optimizations off, off again, and on, and saves each result to
`BepInEx/turbo-selftest/` with timings in `results.txt`. Compare the saves with:

```
python3 tools/compare_saves.py run1-turbo-off.pf1 run3-turbo-on.pf1
```

`SelfTest.BrokenCacheRun = true` adds a fourth run with cache invalidation disabled, which should produce
differences, as a check that the test can catch stale results. `tests/make_stress_save.py` injects
`tests/TurboStress.prpl` into a save: a script that renames networks, adds script instances, destroys units and
mutates list values every tick while querying `GetCoresWithVar`.

## Building

Requires the .NET SDK (8 or later) and the game installed.

```
# reference DLLs come from vendor/BepInEx (extract BepInEx_win_x86_5.4.23.5.zip there) and the game folder
dotnet build src/ParticleFleetTurbo -c Release -p:GameDir="/path/to/Particle Fleet Emergence"
packaging/package.sh   # builds dist/ParticleFleetTurbo-<version>.zip
```

`tools/pfnbt.py` reads and writes the game's `.pf1` map/save format (4-byte length + gzip of a little-endian NBT
variant).
