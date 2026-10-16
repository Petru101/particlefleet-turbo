# Particle Fleet Turbo

BepInEx 5 plugin for Particle Fleet: Emergence (Steam 422900, Unity 5.3.6p6, 32-bit Mono, run via Proton)
that profiles and speeds up custom-map PRPL scripts in the engine instead of in each map.

## Why
The simulation does one tick per rendered frame at a 30 fps target (`GameSpace.Update` → `GameUpdate`), so
any frame over ~33 ms makes the whole game run in slow motion. Map #1402 showed the cause: per-frame
`GetCoresWithVar` (engine scans every active `CrplCore` and allocates a HashSet per call) and redundant
`SetImage*` calls across ~170 script units. Patching the map's scripts made it ~2x faster.

## Optimizations (all behind `Turbo.Enabled`, toggled live with F10)
- `CoresWithVarCache.cs` — caches `GetCrplCoreHoldersWithVar` per (var name, value). Invalidated per name by
  routing every `Dictionary<string, Data>` write in CrplCore (and nested types) through wrappers (transpiler),
  and globally on heap Clear / `baseUnit` setter (active-core set change). Results touching LIST-typed vars are not
  cached (lists mutate in place). Non-list `Data` is immutable after creation (only `Deserialize` on load writes it).
- `ImageFastPaths.cs` — `SetImage` no-op when the slot already holds that image; regex-free `IsAlphaNum`
  (same semantics incl. .NET `$` before final `\n`); memoized ToUpper in `GetImageData`; skip identical
  `SetColor` / localPosition / localScale / eulerAngles writes.

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

## Layout
- `src/ParticleFleetTurbo/` — plugin (net35). `Plugin.cs` hooks + config/hotkeys, `Profiler.cs` per-script timing
  (optional per-statement timing: a transpiler inserts `dup; call Profiler.OnStatement` after the
  `Command::statement` load in `CrplCore.ProcessCommands`; it inflates cheap commands, so off by default).
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
- The game's own crash at 2026-10-16 08:04 (before BepInEx) was address-space exhaustion (69 MB free of 4 GB).
  Managed heap seen at 190-485 MB across reloads; cause not investigated. `Process.PrivateMemorySize64` returns 0
  under Wine, so only the managed heap is reported.
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
- License: MIT (chosen so map makers and other modders can reuse the code freely). Publishing to GitHub and the
  Knuckle Cracker community is pending the user's OK.
