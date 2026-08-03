---
description: Hand-drive sprite_atlas.render_set for a scene the pipeline cannot handle - for a whole tank use /tank instead
argument-hint: [what to render, e.g. "hull+turret+hex, 12 dirs, 256px" or "turret only, 32 dirs"]
---

**For a whole tank, use `/tank`.** It runs `tank_pipeline.run()`, which measures
the axis, finds the armour plates, builds the effects, renders every layer in one
job and checks the result - and it works on both scene layouts. This command is
the route underneath it: hand-assembled `render_set` calls, for a scene that is
not a tank or a tank the pipeline cannot yet drive. Some of what follows predates
the pipeline (`assemble_tank.py`, `spin_pivot_from`, `target: "world.001"`) and
describes a scene shape the current tanks no longer have.

Render sprite atlases from the live Blender scene using `sprite_atlas.py`.

Request: $ARGUMENTS

Work through the steps below. Do not skip the measuring ones — every value here
was wrong at least once when it was assumed instead of measured.

## 1. Read the scene, do not assume it

Call `mcp__Blender__get_objects_summary`. The scene changes between sessions:
object names, which parts are present, and whether parts are parented to each
other are all different from last time. Decide the layer list from what is
actually there.

A layer is one atlas. Typical split for a tank:

| layer | target | note |
|---|---|---|
| `hull` | hull root | `exclude` the turret if it is parented underneath |
| `turret` | turret root | rotates independently in game |
| `hex` / ground | tile object | `"static": True` — terrain never spins |

Render parts as separate layers whenever they move independently in game. A
single combined atlas cannot show a turret at a different angle from its hull.

## 2. Check the geometry before rendering

- **Stray base plate.** AI-generated parts often sit on a flat round slab. Test
  with `find_plate_cut` from `assemble_tank.py`; if it reports a cut, remove it
  with `strip_plate` first, or the slab shows up in every sprite.
- **Front axis.** Needed for facing labels. Render one top-down ortho view and
  look at which end the headlights/glacis are on. Do not guess from the name or
  from the bounding box.
- **Parts not yet assembled?** Seat them with `assemble_tank.py` first, and
  measure the mount from a height map rather than a face centroid — a large
  engine deck or stowage box outweighs the ring platform and pulls a centroid
  off target.

## 3. Pick the camera azimuth from the tile shape

This one is not cosmetic. Screen coordinates of a flat shape equal that shape
rotated by −azimuth and then squashed vertically by `sin(elevation)`. If the
azimuth does not land on one of the tile's mirror axes, the tile projects
skewed and stops reading as a grid cell.

- hex tile → azimuth **multiple of 30** (its axes sit every 30°). On a flat-top
  hex, 0/60/120 render it flat-top on screen, 30/90/150 pointy-top.
- square tile → azimuth 45
- no tile → anything; 45 is the usual three-quarter view

`render_set` warns when a hex job uses a bad azimuth, but choose it correctly
up front.

## 4. Match the step count to the grid

Facings must land on grid directions. For a hex, `steps` should be a multiple
of 6; 12 covers all six edges plus all six vertices, which is what a hex game
needs. Pass `hex_labels` with the measured `front_dir` so every frame carries a
`side @ N deg` / `corner @ N deg` label in the JSON and the game does not have
to re-derive angles.

## 5. Render

Use `render_set` — not repeated `render_atlas` calls. It resolves the spin axis,
the fitted geometry, the fit angles and the camera once and pushes them into
every layer. Hand-syncing those four across passes is exactly how layers end up
misaligned.

```python
import importlib.util
spec = importlib.util.spec_from_file_location(
    "sprite_atlas", r"D:\Projects\AgentCoding\BlenderMCP\sprite_atlas.py")
mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
report = mod.render_set({
    "shared": {"output_dir": r"D:\Projects\AgentCoding\BlenderMCP\out",
               "steps": 12, "tile": 256, "azimuth": 0.0, "elevation": 30.0,
               "spin_pivot_from": ["Hex"],
               "hex_labels": {"front_dir": 270.0, "orientation": "flat"}},
    "layers": [{"name": "hull", "target": "world", "exclude": ["Turret"]},
               {"name": "turret", "target": "world.001"},
               {"name": "hex", "target": "Hex", "static": True}],
})
```

Spin axis: for a tank use the turret ring (`spin_pivot_from` the turret, or the
tile if it is centred on the ring). Hull and turret must share it — that is what
lets the sprites be drawn straight on top of each other.

## 6. Verify — do not report success on the return value alone

1. `report["framing_identical"]` must be `True` and `report["warnings"]` empty.
2. Composite the layers with **no offsets** (ground tile underneath, part on
   top, alpha blend at the same pixel), write a PNG, and **read the image**.
   Confirm the part sits on the tile and the tile is symmetric about the
   vertical. A wrong azimuth or spin axis is obvious here and invisible in the
   numbers.
3. Check the sprite is not needlessly small: a shared frame must contain the
   tile, so `units_per_pixel` grows. If detail is short, raise `tile` or reduce
   the tile's `padding` in `hex_base.py`.

## 7. Report

Give the file paths, grid, tile size, `units_per_pixel`, `anchor_px`, and the
frame→facing table. State the azimuth and spin axis chosen and why. Flag
anything measured that the user may want to change (front axis, mount point,
scale ratios) rather than burying it.
