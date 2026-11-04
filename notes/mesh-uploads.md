# Land and plasma mesh uploads

Read before touching `LandSubmeshUploads`, `PlasmaSubmeshUploads` or `MeshUploads` in `EngineFastPaths.cs`.

## What costs
Each land/plasma chunk is one 126x126-quad mesh (63504 vertices; land also has uv, uv2, colours and tangents).
In Unity 5.3 every channel write (`mesh.colors32 = ...`, `uv`, `uv2`, `vertices`) costs ~1.5-2 ms whatever the
channel: Unity rebuilds the whole vertex buffer into freshly allocated memory (~3 MB). Measured on map #955:
- An undrawn copy of the mesh costs the same as the drawn one, and `UploadMeshData` alone costs as much as a write.
- ~3 million minor page faults in 10 s of play, 4.5 s of 10 s in the kernel: ~790 faults (3 MB) per write.
- Not the render thread (`-force-gfx-direct`: same), not DXVK (`PROTON_USE_WINED3D=1`: same), not CPU clocks
  (all cores at 4.3 GHz, main thread 98%). `Mesh.MarkDynamic()` changes nothing. `-force-d3d11` works (game
  starts, nothing missing in the log) and is ~15% faster with the mod off, but wasn't checked visually.
- Unity 5.3 has no partial or multi-channel upload API, so the only lever is fewer writes.

## What the mod does
1. Upload only the arrays a quad write actually changed (`SetQuadNEW` / `UpdateNormalTexture` / plasma `SetQuad`
   prefix+postfix compare the quad's 4 entries). Exact: the picture is unchanged.
2. `[Turbo] MeshUpdateInterval` (default 6): during the per-frame refresh in `GameSpace.GameUpdate`, a chunk may
   upload only on its own turn, `(Time.frameCount + phase) % Interval == 0`, phases handed out per chunk. Staggering
   matters: when chunks waited in step, every Nth frame took all the uploads and the 30 fps cap ate the savings
   (interval 10 unstaggered: 37.8 ms frames despite 18.8 ms game steps). Only the picture can lag (Interval - 1
   frames, ~170 ms at 6); game state is untouched. Refreshes outside `GameSpace.Update` (load, editor) upload at once
   (`InFrameRefresh` is set only inside it, reset in a postfix and finalizer).

## Results on #955 save-1 (spreading mire from ~70 mire spawners, 600 ticks, Turbo on, section timing on)
| interval | uploads ms/frame | writes/frame | game step | real frame |
|---|---|---|---|---|
| (changed arrays only) 1 | 26.6 | 26.4 | 39-47 | 46-54 |
| 3 staggered | 16.8 | 13.6 | 31.3 | 39.2 |
| 6 staggered | 9.3 | 7.4 | 22.9 | 34.5 (median 33.2: full speed) |
Mod off: 64-75 ms frames.

## Other
- The user checked #955 in the real game at interval 6: mire spreading looks fine.
- `[Profiler] SectionTiming` also turns on per-array upload timing (`MeshUploads.Report` in the profiler report).
