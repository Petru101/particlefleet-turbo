# Particle Fleet Turbo

BepInEx 5 plugin for Particle Fleet: Emergence (Steam 422900, Unity 5.3.6p6, 32-bit Mono, run via Proton)
that profiles and speeds up custom-map PRPL scripts and slow engine paths, without changing how maps play.

## Why
The simulation does one tick per rendered frame at a 30 fps target (`GameSpace.Update` → `GameUpdate`), so
any frame over ~33 ms makes the whole game run in slow motion. Map #1402 showed the cause: per-frame
`GetCoresWithVar` (engine scans every active `CrplCore` and allocates a HashSet per call) and redundant
`SetImage*` calls across ~170 script units. Patching the map's scripts made it ~2x faster.
Map #1494 (no scripts, 16 player ships with 230 particle-beam "space" modules, spreading mire) was engine-bound:
`SpaceModule.FindNearestEnemy` (every module scans ~2800 cells every tick) and full land-mesh re-uploads.
Map #1163 (~180 land omnis with lathes, spreading mire) felt like "a second lost every 10 s": not freezes but most
frames 34-50 ms. Cost was the omnis' per-tick target searches, which almost always find nothing.
Map #955 (mire spreading everywhere) ran at ~60% speed: land/plasma mesh writes, ~1.7 ms each in Unity 5.3
(see notes/mesh-uploads.md).
Map #1276 (16 big ships, ~1100 modules, 134 land omnis, 2500 particles) has no single hot spot: 92-106 ms off,
55 ms with 0.4.0, ~47 ms now. Left: lathe beam drawing (`LatheLineManager.SetPositions` restarts two particle
systems every call, 2.6 ms; skipping that would change the look), land redraws, hull updates, particles.

## Optimizations (all behind `Turbo.Enabled`, toggled live with F10)
- `CoresWithVarCache.cs` — caches `GetCrplCoreHoldersWithVar` per (var name, value). Invalidated per name by
  routing every `Dictionary<string, Data>` write in CrplCore (and nested types) through wrappers (transpiler),
  and globally on heap Clear / `baseUnit` setter (active-core set change). Results touching LIST-typed vars are not
  cached (lists mutate in place). Non-list `Data` is immutable after creation (only `Deserialize` on load writes it).
- `ImageFastPaths.cs` — `SetImage` no-op when the slot already holds that image; regex-free `IsAlphaNum`
  (same semantics incl. .NET `$` before final `\n`); memoized ToUpper in `GetImageData`; skip identical
  `SetColor` / localPosition / localScale / eulerAngles writes.
- `EngineFastPaths.cs` — `SpaceModule.FindNearestEnemy` as a tight row scan that breaks ties by the engine's ring
  scan order (first cell with the lowest key; `Rank`), calling the engine's own pure cell checks; land submeshes
  upload only the vertex arrays a `SetQuadNEW`/`UpdateNormalTexture` call actually changed (mire = colors only);
  plasma submeshes likewise; and during the per-frame refresh each chunk uploads only on its own staggered turn
  every `[Turbo] MeshUpdateInterval` frames (default 6; only the picture lags). `Struc.GetNearestStruc` / `Land.GetNearestCoveredLand`: same 3x3-block walk, but the
  empty-block test is inline and only occupied blocks call the engine's per-block method (reverse patch).
  `LatheWeapon.FireAtLandUnit`: a tight pre-scan of the pure part of its condition skips the engine's scan when
  no cell can pass (it would return false with no side effects).
- `ShipFastPaths.cs` — `CommandModule.SetAttractionFields` uses a per-offset vector table (built on first use with
  the engine's expressions, inside a tick, so float state matches; verified cell by cell). Ship range circles
  (`ModuleRangeIndicator.RefreshModuleRangeIndicators`) are rebuilt only while visible: deferred while the
  `CombinedCircles` object is inactive and done in a `CombinedCircles.Show` postfix (every activation goes there;
  the user checked select and hover in the real game: circles show fine).
  Tried and dropped: hoisting `cm.transform.position` out of `ShipPlan.UpdateHullSections` (no measurable gain).

## Verification
F11 self-test (`SelfTest.cs`): snapshots the mission, replays 300 ticks with Turbo off/off/on (auto-presses the
opening panel's Launch), saves each + `results.txt` timings into `<game>/BepInEx/turbo-selftest/`.
`tools/compare_saves.py A B` diffs saves field by field. The game is deterministic (off vs off = 0 diffs) as long as
nothing touches it: #1402's scripts react to mouse hover/clicks, so a cursor over the window changes the result.
#1402 save-5 result: off/on saves identical; frame 52-55 ms → 34 ms, scripts 40 → 19 ms (cap is 33.3 ms).
Cache invalidation: `tests/make_stress_save.py` injects `tests/TurboStress.prpl` (renames networks via
SetScriptVar, AddScriptToUnit, DestroyUnit, in-place list mutation, int/float/string queries; folds every
GetCoresWithVar result into a checksum) in place of the single-instance Help.prpl. Over 300 ticks: off = on
(0 diffs); with `SelfTest.BrokenCacheRun` (invalidation disabled) 330 diffs, so the test is sensitive.
UnityEngine.Random is seeded from the clock and some units use it (#1494 differed even off vs off), so each run
reseeds it (`Random.seed = 1`). `[SelfTest] VerifyFastPaths` runs the engine's original target search (reverse
patch) next to the fast one and counts mismatches. #1494 save-1, 600 ticks: off = off = on (0 diffs), 136,736
searches 0 mismatches; GameSpace.Update 45 → 30 ms (search 14.7 → 5.4, land mesh 9.5 → 3.6), headless frame 52 → 37.
#1163 save-1 (900 ticks, Turbo on in run 1): 413k block searches and 117k lathe skips 0 mismatches, off = on saves;
GameSpace.Update 29.8 → ~26 ms, 30 s of game took 33.2 s → 31.2-31.8 s headless (off: 37-42 s). Left: map scripts
~4.4 ms, land colour uploads 3.6 ms, many sub-1 ms costs (`FireAtUnit`, `PackSource.HookUp`...).
Later runs in one session are slower (+5-6 ms by run 3, heap/GC growth), so compare runs in the same position
(`[SelfTest] Order`, e.g. `on,off,on`).

## Notes files
- `notes/mesh-uploads.md` — why land/plasma mesh writes are slow, what was ruled out, the interval results.

## Layout
- `src/ParticleFleetTurbo/` — plugin (net35). `Plugin.cs` hooks + config/hotkeys, `Profiler.cs` per-script timing
  (optional per-statement timing: a transpiler inserts `dup; call Profiler.OnStatement` after the
  `Command::statement` load in `CrplCore.ProcessCommands`; it inflates cheap commands, so off by default).
  `Sections.cs` (`[Profiler] SectionTiming`, read at startup) splits engine time by call site: a transpiler marks
  every game/Unity call in the instrumented methods (list in `Plugin.Start`); parameterless virtual calls are split
  by receiver type (`[Profiler] SectionMethods` lists the methods). Marks are flat, so time in untimed code after
  a mark is charged to it, and every mark costs a little: fine lists overstate small, frequent calls.
  `MethodTimer.cs` (`[Profiler] TimedMethods`) times listed methods start to return instead (honest totals).
  That is how #1494's, #1163's and #1276's hot spots were found. Profiler reports list frames over 100 ms
  (with the previous frame's update time and GC count), and each self-test run writes `run<N>-frames.tsv`
  (frame ms, update ms, GCs) and times a forced GC (~45 ms at 250 MB, so GC pauses aren't the problem).
- `tools/` — Python `.pf1` reader/writer (`pfnbt.py`) and save differ. `tools/patch1402.py` (the one-off #1402
  map patcher) is git-ignored and kept local: it quotes the map author's scripts, which aren't ours to publish.
- `tests/` — cache-invalidation stress script and the save injector.
- `packaging/` — `package.sh` builds `dist/ParticleFleetTurbo-<ver>.zip`: BepInEx x86 + minimal `BepInEx.cfg`
  (entrypoint fix only; BepInEx fills in defaults, verified) + plugin + INSTALL.txt + third-party notices.
  Release defaults: profiler off unless the F9 overlay is open, self-test key unbound.
- `decompiled/` (git-ignored) — ILSpy output of Assembly-CSharp for reference. Regenerate:
  `ilspycmd -p -o decompiled "<game>/ParticleFleet_Data/Managed/Assembly-CSharp.dll"` (ilspycmd 9.1 works on .NET 8).
- `vendor/` (git-ignored) — BepInEx_win_x86_5.4.23.5 zip/extract; the build references its core DLLs.

## Build / install
The game is at `/run/media/Petru/Games/SteamLibrary/steamapps/common/Particle Fleet Emergence` (prefix in
`../../compatdata/422900`); pass it as `-p:GameDir=...` (the csproj default path doesn't exist here).
`dotnet build src/ParticleFleetTurbo -c Release`, copy `bin/Release/ParticleFleetTurbo.dll` to
`<game>/BepInEx/plugins/`. Profiler reports go to `<game>/BepInEx/LogOutput.log` every 10 s; F9 toggles overlay,
F10 toggles Turbo, F11 runs the self-test. Developer options in `[SelfTest]`: `AutoRunSave` (self-test a save
from the main menu, then quit), `BrokenCacheRun`, `DataDir` (redirect the game's data folder away from real,
cloud-synced Documents).

## Gotchas
- **BepInEx entrypoint must be `Type = MonoBehaviour`** in `BepInEx/config/BepInEx.cfg` (file is CRLF).
  The default `Application` .cctor fires during Unity 5.3's `MonoManager ReloadAssembly`: plugins loading
  then either crash natively or leave all UI MonoBehaviours "missing" (black screen).
- Even so, don't touch Assembly-CSharp types in plugin `Awake`; patch from `Start`.
- Proton needs `winhttp` = native,builtin: set in the prefix `user.reg` `[Software\\Wine\\DllOverrides]`
  (original backed up as `user.reg.before-bepinex`), so no Steam launch option is needed.
- Engine changes must keep script-visible results identical (including `GetCoresWithVar` result order) so maps
  behave the same.
- Memory (32-bit, 4 GiB): on #1402 the game settles at ~3.1 GiB mapped / ~2.2 GiB writable (measured
  over three 20-min self-test runs, Turbo off and on alike; it plateaus, it doesn't keep growing) and a
  map load spikes it briefly. Normal runs show ~3.2 GB VIRT. Out of address space crashed it at 08:04 on
  2026-10-16 with neither the mod nor Ferret (the user comparing save-5/save-6, several loads in 8 min:
  timeline from High Tide's PipeWire log, `~/Projects/high-tide-test/logs/`, JOIN/LEAVE ParticleFleet.exe).
  Freezes at 18:36 that day and at 02:00 on 10-17 were a Steam client overlay bug (alt-tab wedges
  gameoverlayui; ValveSoftware/steam-for-linux#13682), not the mod. Workaround: overlay off for the game.
  `Process.PrivateMemorySize64` returns 0 under Wine: the profiler reports only the managed heap.
- Hidden test copy: `/run/media/Petru/Games/pf-test/` holds reflinked copies of the game (`game/`, with
  `steam_appid.txt`), its prefix (`prefix/`) and data (`data/`, used via `DataDir`). `bin/start.sh [game dir]` (run on the
  host with host-spawn; `release/` is a clean copy with a release zip extracted) starts it in a headless gamescope (extra arguments after the game dir go to the game, e.g. `-force-d3d11`;
  `bin/start-wined3d.sh` uses WineD3D instead of DXVK; Ferret's `test-game/hidden.sh`, unit `hidden-pftest`) with
  Proton Experimental directly; with `AutoRunSave` set it self-tests and quits (~1 min per 600-tick run).
  Replace a DLL the running game has loaded by writing a new file and `mv` over it, never `cp` onto it.
- Loaded saves stop on the opening (fleet/Launch) panel; `GameSpace.GameUpdate` does nothing until it closes.
- The game runs without the Steam client when `steam_appid.txt` sits next to the exe (no DRM wrapper; SteamAPI
  init failure is only logged).

## Next / open
- Remaining cost on #1402 is the interpreter itself (~170k PRPL commands/frame: READ, PUSH, SELF, WRITE...).
- Windows 10 (VM, software rendering) passes: release zip + shipped BepInEx.cfg load cleanly, stress self-test
  runs 1-3 identical, broken-cache run differs, 0 missing scripts/crashes/exceptions. See `tests/vm/README.md`.
- Proton launch-option route (INSTALL.txt) passes: with the prefix `winhttp` override removed and no launch option
  BepInEx does not load; with `WINEDLLOVERRIDES="winhttp=n,b" %command%` it loads and the self-test passes.
  This machine now uses the launch option (backups: `localconfig.vdf.before-proton-test`,
  `user.reg.before-proton-test`). Steam must be stopped to edit `localconfig.vdf`, or it overwrites the change.
- License: MIT (chosen so map makers and other modders can reuse the code freely).
- Published: GitHub Petru101/particlefleet-turbo, releases v0.1.0 (scripts), v0.2.0 (engine fast paths), v0.3.0
  (omni searches, plasma uploads), v0.4.0 (staggered mesh redraws) and v0.5.0
  (attraction field table, deferred range circles; each release zip
  was tested on a fresh hidden game copy: loads, 0 missing scripts, self-test off = on), and a Steam discussions post
  (by the user). Knuckle Cracker forum: the user has an account; post in "Map Makers, Ship Builders, and
  Coders" (draft in drafts/, git-ignored).
- Long-run determinism: three 36000-tick runs of #1402 save-5 differ only in a script's MouseX/MouseY.
