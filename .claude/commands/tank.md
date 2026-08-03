---
description: Run the whole tank pipeline on the live Blender scene - measure, build, render every layer, check the pictures
argument-hint: [which tank, e.g. "MTP" or "MT_PARTS into Sprites/MTP" or "MT, skip the effects"]
---

Run `tank_pipeline.run()` on the scene that is open in Blender and report what
the pictures show, not what the return value says.

Request: $ARGUMENTS

This is one call now. The work is in choosing the output directory, knowing what
the scene can and cannot produce **before** spending eight minutes on it, and
looking at the sheets afterwards.

## 0. The environment, which constrains everything below

Blender **5.2 from the Microsoft Store**: its `blender.exe` sits under
`WindowsApps` and will not launch directly, so `--background` and every
`*_for_cli` MCP tool are unavailable. There is one live Blender and you drive it.
To change scene use `bpy.ops.wm.open_mainfile` and **check `bpy.data.is_dirty`
first** - the open file may hold hand work that is not on disk.

Every snippet needs this preamble, because the modules get edited between runs
and Blender caches them:

```python
import sys, importlib, os
sys.path.insert(0, r"D:\Projects\AgentCoding\BlenderMCP")
import tank_pipeline
importlib.reload(tank_pipeline)
```

## 1. Read the scene, do not assume it

Call `mcp__Blender__get_objects_summary`, then ask the pipeline what it sees:

```python
tank_pipeline.parts(dict(tank_pipeline.CONFIG, output_dir=out_dir))
```

That returns `layout` (`"parts"` or `"single"`), whether `flash`, `exhaust` and
`hit` are available, and under `named` the resolved mesh and root names. Report
it before rendering. Two things to check by eye in the summary:

- **`layout` must be what the scene actually is.** `tank_parts.layout()` needs
  **all four** roots - `Hull.World`, `Turret.World`, `Track.Left.World`,
  `Track.Right.World` - to call a scene parts-built. Three of four falls through
  to looking for `world` and raises if there is none. The canonical structure is
  in CLAUDE.md and it is a requirement, not a preference.
- **The belt meshes must be named `L.Caterpillar.Geometry` and
  `R.Caterpillar.Geometry`.** `track_cycle.CONFIG["belts"]` holds those literal
  names and `belts()` looks them up with `objects.get()`, no resolution. A belt
  named anything else is **not found silently**: both track layers render still,
  nothing complains, and it reads as "the animation does not work". Check the
  names in the summary and say so if they differ.

## 2. Say what this scene will and will not produce

Ten layers come out with no hand work at all: `hull`, `turret`, `track_left`,
`track_right`, `hex`, `burst`, `dust` and `scar_{front,rear,left,right}`. The
plates are found by ray fan, so every scene with a hull gets the hit layers.

Five more need a piece separated by hand, once per scene, and each is skipped
with a note if it is missing:

| piece | parent | layers it unlocks |
|---|---|---|
| `Barrel` | the turret layer root | `flash`, `smoke` |
| `Engine` | the hull layer root | `exhaust`, `fire`, `burn` |

Either bare or `.Geometry`-suffixed name resolves. If one is missing, say which
layers will be absent **before** rendering, and give the recipe: select it,
`Ctrl+L` to complete the panels it cuts through, `P > Selection`, rename, then
`Ctrl+P > Object (Keep Transform)` onto the root. See MUZZLE_FLASH.md.

## 3. Output directory

`Sprites/<TAG>/` - one folder per scene, the tag the harness reads. The default
in `CONFIG` is `out/`, which is the scratch directory, so **always pass
`output_dir` explicitly**. Never write a real tank into `out/`.

## 4. Run it, and expect the call to time out

A full sixteen-layer set is around 1000 frames and takes **eight minutes or so**
on a parts scene. `mcp__Blender__execute_blender_code` will time out long before
that. Blender keeps going; you just lose the return value. So:

1. Delete `<output_dir>/_run_report.json` first - `run()` writes it at the very
   end, so its reappearance is the finish signal.
2. Fire `tank_pipeline.run(cfg)` and let the call time out. Do **not** re-fire
   it: a second render into the same directory while the first is running is how
   you get half of one set and half of another.
3. Poll from the shell, not from Blender. `Sprites/<TAG>/frames/` fills up as it
   goes and is the progress meter; `_run_report.json` appearing means done.
4. Read `_run_report.json` for the numbers.

Anything you compute yourself in that snippet runs *after* the render and can
throw away eight minutes of work if it has a typo in it. Keep the snippet to the
`run()` call and read the report off disk.

## 5. Look at the pictures

**Never report success from the return value.** Every number here has been green
while the picture was wrong. `run()` writes six sheets into the output directory;
read the ones the scene produced:

| sheet | what it answers |
|---|---|
| `_check_shot.png` | phase across, heading down - the flash comes out of the gun |
| `_check_exhaust.png` | one full loop across - the plume is visible on pale ground |
| `_check_fire.png` | one full loop - smoke under, flame added over |
| `_check_hit.png` | face across, heading down - hits land on the right plate, and a turned-away plate is drawn *behind* the tank |
| `_check_scar.png` | plate across, all twelve headings down - the mark turns with the hull and disappears behind it |
| `_check_damage.png` | level across, plate down, each on the heading that shows it best |

On a parts scene the tank is drawn with its belts on every sheet. A tank that
looks like it is hovering means the track layers are missing from the sheet, not
that the tracks are fine.

**Confirm the front by eye.** `front_dir = 270` (nose along −Y) is a *declared*
convention. The only cross-check is the muzzle bearing against the ring bearing,
and that exists only when `Barrel` does. Without it, nothing in the pipeline
confirms the front - look at frame 0 and find the headlights and the glacis.

## 6. Report

`problems` empty is the headline. Then the numbers that carry a judgement, with
their thresholds, from `_run_report.json` and the check:

- `framing_identical` **must** be true, and `max_edge_alpha` zero on every tank
  layer - anything else means the frame is clipping the sprite
- `stands_on_tile`, and `hit_off_tank` empty
- the axis: `roundness`, `cross_band_spread`, and `wobble_px_if_empty_trusted` -
  the orbit the tank would walk if the empty were trusted instead of the fit
- the muzzle: shape, margin, and `bearing_disagreement` against the ring
- `exhaust_contrast` ≥ 28 and `burn_contrast` ≥ 28 - an occluding layer that
  does not move the picture does not exist
- `fire_redness` ≥ 30 with `fire_white_fraction` 0, `burst_redness` ≥ 30,
  `dust_contrast` ≥ 40 - additive layers fail by going white, so they are judged
  by hue
- the loops (`exhaust`, `fire`, `burn`) all seamless
- `scar_contrast` ≥ 45 rising per level, `scar_off_armour` ≤ 6%,
  `scar_fit` ≤ 0.85
- `belts_restored` all zero - the belts are posed by *writing* vertex
  coordinates, so that the rest pose came back is a result, not a promise

State plainly which layers this scene did not produce and why. If a threshold is
only just met, say so with the number: `exhaust_contrast` at 28.4 against 28 is
not the same news as 91.

## 7. If it fails

- **A layer rendered the wrong objects.** `exclude` and `holdout` match by
  **substring** and pull in descendants. Excluding `L.Caterpillar.Geometry` once
  took out `L.Caterpillar.Geometry.Rebuilt` with it, and the layer still rendered
  - the road wheels were in it - so nothing looked empty. Check each layer's
  `rendered` list against what you meant.
- **`framing_identical` false.** Something resolved the camera twice. Every layer
  must go through one `render_set` job; a second job re-resolves the spin axis,
  the fitted geometry, the angle list and the camera, and nothing makes it
  resolve them the same way.
- **A part was not found.** `tank_parts` raises and names the candidates. It
  refuses to guess on purpose: a wrong axis does not distort the sprite, it walks
  the whole tank round a circle, and no output number reports that.
- **The scene is left dirty.** `run()` restores the belts and removes the tile
  and the ring-cut box, but it leaves the stamps and the effect objects
  (`Flash`, `Smoke`, `Plume`, `Fire`, `Burn`, `Burst`, `Dust`, `Scar`, and the
  `*.Rebuilt` belts). Those are rebuilt every run, so saving them is harmless
  and not saving them loses nothing. Do not save the `.blend` without asking -
  it is a ~250MB LFS write.

## 8. Getting it into the Godot bench

Two C# edits, not pipeline ones: add the tag to `Main.Tags` and a profile to
`MovementProfile.All`. Without the profile the tank drives on MT's numbers,
which is the fallback for an unknown tag and reads as "the class does nothing".
Then `dotnet build` and `--selftest`.
