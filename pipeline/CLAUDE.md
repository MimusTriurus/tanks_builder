# Tank sprite and model pipeline

Turns AI-generated tank models (ComfyUI → glTF) into game assets for the bench:
3D game variants in `Models/<TAG>/` (main line) and, for the legacy 2D line,
layered isometric sprite atlases. The contract with the bench and the
repo-wide hard rules are in the [root CLAUDE.md](../CLAUDE.md); the bench is
[godot/CLAUDE.md](../godot/CLAUDE.md).

## Hard rules

- **The main output is the 3D game variant**: `/repro`, steps 9–12,
  [docs/repro.md](docs/repro.md), «Игровой вариант». Atlases are the legacy 2D
  line: `/tank` and `/sprites` stay for sprite sets, nothing new is built for them.
- **Never report success from a return value.** Composite the layers without
  offsets, write a PNG and look at it: a wrong azimuth or axis is obvious there
  and invisible in numbers. Each output folder has `_check_composite.png`;
  include frames with the turret turned against the hull.
- **Before `bpy.ops.wm.open_mainfile` don't trust `bpy.data.is_dirty`**: it
  stays false after a scripted (MCP) edit, so unsaved work is thrown away. Save
  explicitly or ask.
- **`origin_set` doesn't work over MCP** and **`foreach_set("select", …)`
  silently does nothing** ("Blender traps").

## How it runs

Everything runs inside Blender: its Text Editor (`Alt+P`) or the Blender MCP
bridge. Scripts are plain modules: import and call the entry point, or run as
`__main__` to take its `CONFIG` as is. Modules find each other via `REPO`, the
absolute path to this directory. Blender caches modules between runs: call
`importlib.reload` first, as `/tank` does.

- Blender **5.2 LTS** from the Microsoft Store. Its `blender.exe` sits under
  `WindowsApps` and **can't be launched directly**, so `--background` is
  unavailable and the `*_for_cli` MCP tools fail. Drive the live instance,
  switching scenes with `bpy.ops.wm.open_mainfile`.
- Render engine — **EEVEE**. 12 frames of 256 px in three layers take seconds.
- Meshes are 0.4–1.5 M vertices. Use numpy over `foreach_get`, no Python loops
  over vertices.

## Layout

```
pipeline/            38 modules; docs/ - per-subsystem breakdown, on demand
pipeline/repro/      procedural copies of generator models, a module per tank (docs/repro.md)
pipeline/out/        scratch output, not in git
../assets/Input3D/   generator .glb, one per tank
../assets/Parts3D/   split models
../assets/Scenes/    working .blend: HT/MT/LT (single mesh) and *_PARTS (by parts),
                     MT_PARTS_1 is the canonical medium; plus WALL_scene and Hexes
../assets/Images/    reference renders; Environment/, Terrains/, Water/
../Sprites/          atlases, <TAG>/{hull,turret,hex,...}_atlas.{png,json}; five live
                     sets: LTP, MTP, HTP, TDP, HMP. Obsolete/ is on disk, not in git
../Models/           3D game variants, <TAG>/{tank.glb,tank.json}
```

Two scene structures: **old style** (`world` → `Hull`, `Turret`; HT, MT, LT,
split by hand) and **by parts** — canonical for everything new,
[docs/tank-scene.md](docs/tank-scene.md). By-parts has two shapes: turreted
(four roots) and **casemate** — the fixed superstructure in the hull layer, no
`Turret.World` root, and its absence is the declaration (`TD_StuG4`,
`HM_SturmTiger`). `Hex` is built at render time and never saved — in any scene.

## Sprite pipeline (legacy 2D)

1. **Import** the `.glb` into a clean scene.
2. **Split** hull, turret and tracks — done by the generator on a parts model;
   on a single mesh `split_turret.py` and `track_split.py`; models without
   tracks get them from `track_make.py`. All in [docs/tools.md](docs/tools.md).
3. **Effect geometry** — by hand, once per scene: `Barrel` gives the shot,
   `Engine` the exhaust, fire and smoke. Neither is mandatory; hits and marks
   need no hand work (plates are measured by ray).
   [docs/MUZZLE_FLASH.md](docs/MUZZLE_FLASH.md), [docs/effects.md](docs/effects.md).
4. **Rotation axis** — `turret_axis.py` stamps `ring_axis` on hull and turret.
   A casemate has no ring: the carousel turns around `parts_render.hull_axis()`
   and `turret_axis` is not run (a circle inscribed in the box gave
   `roundness` 0.70 and a radius larger than the box).
5. **Tile** — centred on that axis.
6. **Render** — `render_set` from `sprite_atlas.py`, one job for all layers.
7. **Check** by composite, by eye.

Steps 4–7 are one call, `tank_pipeline.run()`, on both structures. It measures
the axis only if there is none yet: a stamp in place may have been set by hand
and must not be silently overwritten. `/tank` drives the whole thing.

## Conventions

- **Front is −Y**, i.e. `front_dir = 270`. A declared import convention, not a
  measurement: confirm it on every model on frame zero, by eye — headlights,
  glacis.
- **Flat-top hex**, azimuth 0, tilt 30°, **24 steps**; frames alternate between
  facing a hex side and a corner.
- **Azimuth a multiple of 30 for a hex** (45 for a square): off the symmetry
  axis the tile projects skewed and stops reading as a cell. **`steps` a
  multiple of 6.**
- **24 steps, not 12, for every layer at once.** It is the only knob for
  turning smoothness; doubling only hull and turret is impossible — tracks,
  barrel, marks and plume are welded to them and would drift. Reason and byte
  cost — [docs/pipeline.md](docs/pipeline.md), «Память атласов».
- Hull, turret and tile **share one rotation axis** — the turret ring; that is
  what lets sprites stack at `anchor_px`. A casemate has no such axis: the
  common point is the centre of the hull-plus-tracks footprint (barrel
  excluded) — the same set the tile is fitted to, so there is one point, not
  two coinciding ones.

## Checks

- After a split: `exact_partition`, the turret kept the gun (by bbox), tracks
  still stand at z=0. After moving an origin: the world bbox didn't move.
- Nothing touches the tile edge (`max_edge_alpha == 0`).
- A new set in `Sprites/` or `Models/` is checked by the bench: `dotnet build`
  and a capture in `godot/` (`--selftest` only when the user asks) —
  [godot/docs/selftest.md](../godot/docs/selftest.md).

## Blender traps

The five measurement traps are in [docs/tools.md](docs/tools.md), the hierarchy
trap in [docs/tank-scene.md](docs/tank-scene.md). These bite any `bpy` code here:

- **`me.vertices.foreach_set("select", ...)` silently does nothing.** `select`
  is the lazily created `.select_vert` attribute, the write misses, and the
  next `bpy.ops.mesh.delete` acts on the post-import "all selected" state — a
  whole turret was deleted that way. Use bmesh and check that the deleted count
  equals the selected count.
- **`bpy.ops.object.origin_set` doesn't work over MCP** — the bridge context has
  no `selected_objects`. Move the origin by shifting vertices by −delta and the
  matrix by +delta; they cancel exactly.

## Docs — load on demand

Topic docs are in Russian; open one when its "when" matches.

| doc | when |
|---|---|
| [docs/repro.md](docs/repro.md) | making a clean procedural copy of a raw model, or its game variant |
| [docs/tank-scene.md](docs/tank-scene.md) | authoring a new tank scene by parts |
| [docs/assets.md](docs/assets.md) | looking for an asset or cutting a model (what the generator actually gives) |
| [docs/tools.md](docs/tools.md) | measuring, cutting, rendering tools and the five measurement traps |
| [docs/pipeline.md](docs/pipeline.md) | running `tank_pipeline.run()`, atlas memory, the `Sprites/` contract |
| [docs/layers.md](docs/layers.md) | layers that move relative to the hull |
| [docs/effects.md](docs/effects.md), [docs/MUZZLE_FLASH.md](docs/MUZZLE_FLASH.md) | effects on a tank |
| [docs/trees.md](docs/trees.md), [docs/tree-species-plan.md](docs/tree-species-plan.md) | trees and species |
| [docs/bunker.md](docs/bunker.md) | the bunker, its destruction, what Tank3D reads |
| [docs/brick-wall.md](docs/brick-wall.md) | the brick wall in Blender |
| [docs/HEX_TILES.md](docs/HEX_TILES.md) | multi-level ground tiles in Blender |
