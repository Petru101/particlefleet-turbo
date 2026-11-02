# Particle Fleet Turbo

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Particle Fleet: Emergence** that makes heavy
custom maps run at full speed.

Particle Fleet advances the simulation by one step per rendered frame at 30 fps. When a step takes longer than a
frame's budget, the whole game runs in slow motion. This plugin speeds up the engine paths heavy maps spend that
time in, without changing the game's results.

Map scripts (PRPL):

- **`GetCoresWithVar`** results are cached. The engine normally scans every script instance on the map and
  allocates a new set on every call. The cache is invalidated whenever a script writes the queried variable or
  script instances are added or removed. Results that depend on list values (which can change in place) are
  never cached.
- **`SetImage`** is skipped when the slot already shows that image. The engine otherwise re-validates the slot name
  with a regular expression, reallocates and reapplies the texture on every call.
- Image position, scale, rotation and colour writes that would not change anything are skipped.

Engine:

- **Particle beam targeting.** Every particle-beam ("space") module searched all ~2800 grid cells in its range in
  rings every tick, even when nothing was there. The search now scans rows in a tight loop and breaks ties in the
  engine's ring order, so it picks the same target.
- **Land mesh uploads.** Spreading mire rewrote one quad's colour and the game re-uploaded all four vertex arrays
  of that land chunk (about 2 MB). Now only the arrays that actually changed are uploaded.

Results:

- Exchange map #1402 (about 170 networked script units): frame time 52–55 ms → 34 ms (the game caps at 33.3 ms),
  script time 40 ms → 19 ms.
- Exchange map #1494 (no scripts, 16 ships with 230 particle-beam modules, spreading mire): game step
  45 ms → 30 ms (targeting 14.7 ms → 5.4 ms, land mesh 9.5 ms → 3.6 ms).

## Installing

Download the release zip and follow `INSTALL.txt` inside it. In short: extract it into the game folder; on
Linux/Steam Deck run the Windows version through Proton with the launch option
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

The bundled `BepInEx/config/BepInEx.cfg` sets the BepInEx entrypoint to `MonoBehaviour`. With BepInEx's default
entrypoint, plugins load while Unity 5.3 is still registering scripts, and the game either crashes or shows a
black screen.

In game: **F10** toggles the optimizations, **F9** shows a profiler overlay (frame time, time per map script).

## Verifying behaviour

The game simulation is deterministic once Unity's random generator is reseeded (it is seeded from the clock and
some units use it; the self-test reseeds it at the start of each run), which makes behaviour easy to check. With
`SelfTest.Key` set (e.g. `F11`) in `BepInEx/config/petru.particlefleet.turbo.cfg`, pressing it during a mission
snapshots the game, replays the next 300 ticks with the optimizations off, off again, and on (`SelfTest.Order`),
and saves each result to `BepInEx/turbo-selftest/` with timings in `results.txt`. Compare the saves with:

```
python3 tools/compare_saves.py run1-turbo-off.pf1 run3-turbo-on.pf1
```

`SelfTest.BrokenCacheRun = true` adds a fourth run with cache invalidation disabled, which should produce
differences, as a check that the test can catch stale results. `tests/make_stress_save.py` injects
`tests/TurboStress.prpl` into a save: a script that renames networks, adds script instances, destroys units and
mutates list values every tick while querying `GetCoresWithVar`. `SelfTest.VerifyFastPaths = true` also runs the
engine's own targeting search next to the fast one on every call and reports how many results differed.

`Profiler.SectionTiming = true` (read at startup) splits the engine's own frame time by call site in the profiler
report, which is how the engine hot spots above were found.

## Building

Requires the .NET SDK (8 or later) and the game installed.

```
# reference DLLs come from vendor/BepInEx (extract BepInEx_win_x86_5.4.23.5.zip there) and the game folder
dotnet build src/ParticleFleetTurbo -c Release -p:GameDir="/path/to/Particle Fleet Emergence"
packaging/package.sh   # builds dist/ParticleFleetTurbo-<version>.zip
```

`tools/pfnbt.py` reads and writes the game's `.pf1` map/save format (4-byte length + gzip of a little-endian NBT
variant).

## License

MIT, see [LICENSE](LICENSE). The release zip also bundles BepInEx (LGPL-2.1) and its dependencies; see
`packaging/THIRD-PARTY-NOTICES.txt`.
