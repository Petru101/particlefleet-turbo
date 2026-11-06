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
  of that land chunk (about 2 MB). Now only the arrays that actually changed are uploaded. The plasma layer
  re-uploaded its mesh on every write even when nothing changed; it gets the same treatment.
- **Land and plasma redraws (mire-heavy maps).** In Unity 5.3 every change to a land or plasma chunk's mesh costs
  about 1.7 ms, because Unity rebuilds the chunk's whole display buffer. When mire spreads across the map, most
  chunks change every frame, which alone can exceed the frame budget. During play, a changed chunk now redraws on
  its own turn, once every 6 frames, with chunks taking turns so the work is spread evenly. This is the one change
  that affects what you see: the land's picture can trail the game by up to 1/6 of a second. The game itself is
  unaffected. `Turbo.MeshUpdateInterval` sets the number of frames (1 = redraw every frame, like the game).
- **Ship attraction fields.** Each time a ship moves a cell, it adds and removes its pull on about 2,000
  surrounding cells, recomputing an angle, distance, sine and cosine per cell. These depend only on the offset, so
  they now come from a table built with the engine's own formulas (checked cell by cell against the original).
- **Ship range circles.** Ships rebuilt their weapon range circles almost every tick in battle, though they are
  only visible on a selected or hovered ship. Hidden circles are now rebuilt when they are shown.
  Rebuilding a visible one took about 130 ms on a big ship, twice each time it gained or lost a module; a faster
  overlap check brings that to about 20 ms and draws the same circles.
- **Land omni target searches.** Every omni looks for something to build, shoot or repair every tick and usually
  finds nothing. The searches for structures and mire now skip empty map blocks without a function call per
  block, and the search for land units under construction first does a quick check whether any cell in range
  could qualify. Both find exactly what the engine would.

- **Tiny freezes from garbage collection.** Each tick the game threw away about 500 KB of temporary data on a map
  with big ships: an array per hull cell to check for ship collisions, a helper object per running effect, and scan
  buffers per ship. Cleaning that up pauses the game about 40 ms, every few seconds. These are now avoided when they
  aren't needed or reused, with the same results, so collections are about 10 times rarer.

Results:

- Exchange map #1402 (about 170 networked script units): frame time 52–55 ms → 34 ms (the game caps at 33.3 ms),
  script time 40 ms → 19 ms.
- Exchange map #1494 (no scripts, 16 ships with 230 particle-beam modules, spreading mire): game step
  45 ms → 30 ms (targeting 14.7 ms → 5.4 ms, land mesh 9.5 ms → 3.6 ms).
- Exchange map #1163 (about 180 land omnis, spreading mire): game step 30 ms → 26 ms; 30 seconds of game took
  33.2 s, now about 31.5 s (39–42 s with the plugin off).
- Exchange map #955 (mire spreading across the whole map): frame time 75–82 ms with the plugin off, about 35 ms
  with it on (median 33.2 ms: full speed).
- Exchange map #1276 (16 large ships with ~1,100 modules, 134 land omnis, 2,500 particles): frame time 92 ms with
  the plugin off, 55 ms with 0.4.0, 47 ms now. The rest is spread over many small costs.
- Exchange map #1990 (near full speed but with constant tiny freezes): a 65–80 ms frame every 6.5 seconds is gone.
  Over 60 seconds of game: 9 garbage collections → 0, average frame 35.2 ms → 33.6 ms.

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
engine's own searches next to the fast ones on every call and reports how many results differed.

`Profiler.SectionTiming = true` (read at startup) splits the engine's own frame time by call site in the profiler
report (`Profiler.SectionMethods` picks the methods), and `Profiler.TimedMethods` times listed methods from start
to return, which is how the engine hot spots above were found. `Profiler.AllocTracking = true` (with section
timing) also shows which sections create garbage. The profiler report also lists frames over 100 ms, and
each self-test run writes a per-frame log (`run<N>-frames.tsv`: frame time, game step time, garbage collections).

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
