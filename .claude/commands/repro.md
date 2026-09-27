---
description: Rebuild a generator tank as a clean procedural copy next to it - measure, author, compare, bake textures, verify
argument-hint: [which scene or tank, e.g. "LT_PARTS" or "MT_PARTS_1 as mt_parts" or "continue lt_parts: thicker skirts"]
---

Make a procedural copy of the generator model that is open in Blender: own
geometry, own baked textures, the canonical parts structure, standing next to
the original in the same scene. Report what the pictures show, not what the
return values say.

Request: $ARGUMENTS

The order, the reasons and the traps are in pipeline/docs/repro.md - read it
first, every time. This command is the checklist. The shape of each tank is
authored by hand from measurements (a module in `pipeline/repro/`); everything
around it is `pipeline/repro_kit.py`. The finished example to copy from is
`pipeline/repro/lt_parts.py`.

"Continue <name>: ..." means the module exists: skip to step 5 with the change.

## 0. The environment

One live Blender, no `--background`, scene changes via `bpy.ops.wm.open_mainfile`
after checking `bpy.data.is_dirty` - pipeline/CLAUDE.md, "Окружение" (and
`is_dirty` is blind to scripted edits: a copy built this session reads clean).
Every snippet starts with:

```python
import sys, importlib
sys.path.insert(0, r"D:\Projects\AgentCoding\BlenderMCP\pipeline")
import repro_kit as K
importlib.reload(K)
tank = K.load("<name>")          # re-executes repro/<name>.py every time
```

Scratch pictures go to `pipeline/out/repro/<NAME>/` (`K.out_dir(tank)`), never
to `Sprites/`. The bridge times out after a few minutes: **bake one or two roots
per call**, and if a call times out, Blender finishes on its own - query the
state, do not re-fire.

## 1. Read the scene, do not touch it

`mcp__Blender__get_objects_summary`, then `K.inspect()`. Write down, in the
tank module's docstring, what the copy must repeat:

- the ground: lowest belt vertex (LT_PARTS: z -0.3844, **not** 0);
- which belt is left (LT_PARTS: `L.*` on **-X**);
- the ring axis: a `ring_axis` stamp if the pipeline ran, else the round
  slice of the turret (`K.radial_profile`);
- the material layout and image sizes (the copy bakes the same layout);
- stamps (`ring_*`, `muzzle_*`, `hit_*`, `exhaust_*`) and effect meshes
  (`Burn`, `Flash`, ...) are tool output: the copy does not repeat them.

If the scene is not parts-built (`world` → `Hull`, `Turret`), stop and say so:
the copy repeats the canonical structure of docs/tank-scene.md, and a
single-mesh scene has to be split first.

## 2. Look at it

`K.shoot([...], path, (0, 0, 0), only=lambda o: o.name in <original meshes>)`
over `iso_fl`, `iso_rr`, `side`, `front`, `rear`, `top` and the close-ups
(`mantlet`, `front_close`, `track_close`, `rear_close`, `roof`). Look at the
tone, not only the shape: the paint colour is calibrated in step 6 against
these renders, never against the texture's median.

## 3. Measure off the vertices

- `K.sections(parts, cuts, path)`: x-z cuts at several y, y-z at several x,
  x-y at several z. Big flat faces have no vertices inside, so a thin cut
  "loses" walls - read walls off `K.envelope`, not off one cut.
- `K.envelope(...)` for the deck, glacis, fender and rear profiles;
  `K.radial_profile(...)` for the turret plan; barrel radii by y;
  `K.belt_profile(...)` for the stadium and pitch candidates (count links on
  a picture to pick one).
- A soup of shells lies about thickness: an inner face may exist only above
  the belt. Check an x histogram of the faces you think are thin.

## 4. Author `pipeline/repro/<name>.py`

Copy the skeleton of `repro/lt_parts.py`: the constants block (`NAME`, `SFX`,
`X_OFF`, `PREFIX`, `GROUND`, `RING_C`, `RING_Z0`, `TRACK_X`, `PALETTE`), then
`hull`, `engine`, `turret`, `barrel`, `belt`, `rolls` and, if the turret has
painted seams, `turret_ink`. Numbers in the original's frame. The rules that
already cost a round each (details in docs/repro.md, "Ловушки"):

- the ring owns the lowest slices of the turret (dense 192-segment wall, the
  body's foot above it), and the hull's collar reaches up to just under the
  turret's foot, or the turret floats;
- overlapping solids never share a face: hide one 8+ mm inside the other;
- bevels are two segments (the inked outline needs hard edges); stepped lathes
  get no subdivision;
- a skirt's inner face stays outside the belt's outer edge;
- rivets only through `K.rivets` (open domes, the `Ink` rim).

## 5. Build and compare - then stop and show

```python
stats = K.build(tank)                              # ~10 s
png = K.compare(tank, ["iso_fl", "iso_rr", "side", "front", "top"], "cmp_geo")
```

Geometry reads fine on the source materials in EEVEE. Iterate on the module
until the silhouettes and parts match, **then send the comparison to the user
and wait for a go before baking**: textures cost a minute a round, and a shape
change afterwards means baking again.

## 6. Textures

Calibrate the palette first: render both side by side and read
`K.window_means(png, {...})` on lit paint and on grey metal of each tank.
The copy's windows should come out within a few percent of the original's;
adjust `PALETTE` (sRGB hex as stored), not the lights.

```python
K.build(tank)                          # bake needs the source materials
K.bake(tank, ("Hull", "Turret"))       # one call
```
```python
K.bake(tank, ("TrackL", "TrackR"))     # next call
```

`bake` unwraps (Smart UV, one island per rivet, packed with a wide margin),
bakes base colour and roughness/metal at 2048, counts grey samples under every
green face at mip 0/2/3 and finalises one material per root. **Any grey count
above a stray one or two is a failure** - it was grey smears on rivets once;
look at where the grey faces are before going on.

## 7. Verify, then look

```python
K.verify(tank)
```

must show: `axis_minus_root` 0, `roundness` 1.0, `warnings` empty, `rotatable`
true, `ground_copy` equal to `ground_original`, one material per root,
`no_uvmap`, `custom_props` and `source_left` empty, `engine` back on EEVEE.
Then `K.compare(...)` over the full set of views and the close-ups, and send it.
Say plainly what still differs from the original.

## 8. Save without overwriting anything of the user's

```python
K.save(tank)      # texts into the scene, a *copy* to assets/Scenes/<NAME>_Repro.blend
```

Never save the open file: that is the user's decision, and it would overwrite
their `.blend1`. Tell them the copy is in the open scene, unsaved, and where
the saved copy is.

The pipeline must **not** run on a scene holding both tanks: `Hull.World.Repro`
contains `Hull.World`, and `by_hints`, `exhaust_point` and `muzzle_point` match
by substring or prefix. To send the copy through `/tank`, remove the original
and strip the suffix first - and say so rather than doing it unasked.
