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

## 0. The environment

One live Blender, no `--background`, scene changes via `bpy.ops.wm.open_mainfile`
after checking `bpy.data.is_dirty` - CLAUDE.md, "Окружение". Every snippet needs
this preamble, because the modules get edited between runs and Blender caches
them:

```python
import sys, importlib, os
sys.path.insert(0, r"D:\Projects\AgentCoding\BlenderMCP\pipeline")
import tank_pipeline
importlib.reload(tank_pipeline)
```

## 1. Read the scene, do not assume it

Call `mcp__Blender__get_objects_summary`, then ask the pipeline what it sees:

```python
tank_pipeline.parts(dict(tank_pipeline.CONFIG, output_dir=out_dir))
```

That returns `layout` (`"parts"` or `"single"`), `turret` (whether the vehicle
has a ring at all), whether `flash`, `exhaust` and `hit` are available, and under
`named` the resolved mesh and root names. Report it before rendering. Three
things to check by eye in the summary:

- **`layout` must be what the scene actually is.** `tank_parts.layout()` needs
  the **three** roots that every parts scene has - `Hull.World`,
  `Track.Left.World`, `Track.Right.World` - to call a scene parts-built. Missing
  one falls through to looking for `world` and raises if there is none. The
  canonical structure is in CLAUDE.md and it is a requirement, not a preference.
- **`turret` must be what the vehicle is.** `Turret.World` present or not, and
  its absence is how a casemate declares itself - `Casemate.Geometry` and
  `Barrel.Geometry` hang off `Hull.World` instead. A casemate gets no
  `turret_atlas` and no `wreck_turret_atlas`; it gets `wreck_barrel`, the gun
  drooped. If `turret` is true on a vehicle whose gun does not traverse, the
  scene is wrong and the render will spin a box - see
  pipeline/docs/tank-scene.md, "Машина без башни". The class table has to agree:
  `MovementProfile.Turreted`, and the selftest checks the pair.
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

Three more come free from the stamps: `wreck_turret`, `wreck_track_left` and
`wreck_track_right` - the knocked-out pose, which needs only `ring_axis` and so
arrives with `turret_axis`. There is deliberately no wreck hull: a dead tank's
hull is a live tank's hull to the pixel. See `wreck_pose.py`.

On a casemate the count is different and so is the list: no `turret`, no
`wreck_turret`, and `wreck_barrel` in their place. The box is in the hull layer,
so the hull layer is also the wreck's - which is the point, because the hole the
generator leaves under a mount only shows when the mount layer is not drawn.

Five more need a piece separated by hand, once per scene, and each is skipped
with a note if it is missing:

| piece | parent | layers it unlocks |
|---|---|---|
| `Barrel` | the turret layer root | `flash`, `smoke` |
| `Engine` | the hull layer root | `exhaust`, `fire`, `burn` |

Either bare or `.Geometry`-suffixed name resolves. If one is missing, say which
layers will be absent **before** rendering, and give the recipe: select it,
`Ctrl+L` to complete the panels it cuts through, `P > Selection`, rename, then
`Ctrl+P > Object (Keep Transform)` onto the root. See pipeline/docs/MUZZLE_FLASH.md.

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
   you get half of one set and half of another. **The timed-out call can be
   re-delivered without you asking.** On HT the addon ran `run()` twice four
   minutes apart; the second pass came through with `elev` gone, rendered the
   tube level, and its 120-frame atlas overwrote the 1320-frame one the first
   pass had just packed - leaving 1200 orphan frames on disk that no atlas
   referenced. Two write bursts over the same atlases is the signature: compare
   `ls -lt *_atlas.png` against the report's own mtime, and the report must be
   the *last* write in the directory.
3. Poll from the shell, not from Blender, and **wait on a key the run was asked
   for, not on the file**. `_run_report.json` existing only proves that some
   tank rendered: a level pass writes a perfectly well-formed report with
   `problems` and every check in it. Asked for the elevation ladder, wait for
   `elev_table_deg`; asked for a reshape, wait for `belts[*].shaped`. A
   completion check that a wrong render satisfies is not a check.

   For progress, **read the newest frame's name and mtime, not the file
   count**: a re-render into a directory that already has a set overwrites the
   same filenames, so the count stops growing while the render is perfectly
   alive - `ls -t frames | head -1` says which layer it is on and whether it
   moved. The count is a progress meter only on the first run into a new tag.
4. Read `_run_report.json` for the numbers - and read back the knobs you set,
   not only the results. `barrel_atlas.json` carries `phases` and its `elev`
   block; `phases` 5 and `elev` null after asking for a ladder means the knob
   never arrived. Assert the knob is live *inside* the snippet, before the eight
   minutes: `assert "elev" in parts_render.CONFIG` catches a cached module,
   which swallows an unknown key in silence.

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
| `_check_wreck.png` | live against knocked-out, every heading down - the whole question is the comparison, and the void under the canted turret opens towards the camera on some headings and away on others |

On a parts scene the tank is drawn with its belts on every sheet. A tank that
looks like it is hovering means the track layers are missing from the sheet, not
that the tracks are fine.

**Confirm the front by eye.** `front_dir = 270` (nose along −Y) is a *declared*
convention. The only cross-check is the muzzle bearing against the ring bearing,
and that exists only when `Barrel` does. Without it, nothing in the pipeline
confirms the front - look at frame 0 and find the headlights and the glacis.

## 5a. The stamps, which `run()` now re-applies itself

**The render rewrites every `*_atlas.json` from scratch, and two stamps live
inside those files** - the gun's bore in the shot layers, the engine port in the
burning ones. So a re-render of a stamped set used to un-stamp it, silently.

`run()` re-stamps at the end now (`tank_pipeline._stamp`, ports before bore),
and reports it: `stamps` carries what each said, plus `bore_in_flash` and
`ports_in_fire` read back **out of the files** rather than believed from the
return value. A set that has a barrel layer and comes back without a bore block
raises a `problems` line. So there is nothing to remember here - read those two
flags in the report and move on.

What to do by hand, and only then: a set rendered **before 2026-09-10** carries
whatever it carried. From the repo root, because `CONFIG["root"]` is relative:

```bash
python pipeline/stamp_ports.py     # ports first
python pipeline/stamp_bore.py      # bore lifts hull_length off the ports block
```

Why it had to become a mechanism. Neither loss says anything: without the bore
block `AtlasSet.HasBore` is false, `ActiveSource` returns `Sheet`, and the shot
still draws - out of a hand-painted image whose muzzle sits 110px into a 256px
cell. It reads as "the flash moved behind the muzzle", not as "a stamp is
missing". Measured on 2026-09-09: three re-renders - LTP, MTP, HTP - each
dropped a bore block stamped on 2026-08-25, one of those commits claimed in its
own message that the flash had got *better*, and it came back a day later as a
question from someone looking at the screen. TDP and HMP were not re-rendered
and kept theirs, which is what finally made the three stand out.

The selftest catches half of it - `MTP carries a stamped port` - and nothing
catches the bore, because a set with no bore draws a flash anyway.

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
- the wreck: `wreck_turret_moved_px`, `wreck_void_px` and
  `wreck_track_moved_px`. "The pose was applied" is exactly the claim a hook can
  make while doing nothing, so all three are measured against the live layer.
  `wreck_void_px` is dark hull seen through the ring at the best heading, not
  turret pixels that moved - a cant moves the silhouette whether or not it opens
  anything, and the earlier measure read 1676 on a pose that opened nothing.
  **On a casemate it is `wreck_barrel_moved_px` and `wreck_barrel_moved_share`
  instead**, and no void: nothing lifts. The share is what carries the judgement
  (≥ 0.25 of the tube's own area), because a tube is a few dozen pixels and the
  200px floor written for a canted turret passes on any droop at all
- `stale_removed` must be empty, or be the change you just made. A layer that
  stops being rendered does not stop being read: the render rewrites only what
  it produces, so `run()` deletes the atlases of layers this scene no longer
  has and raises a `problems` line naming them. Two casemates carried
  `turret_atlas` for months and no re-render would have taken it away
  And `units_per_pixel` must come out unchanged from the previous run: the wreck
  layers carry `fit: False`, so a shifted figure means something else moved

State plainly which layers this scene did not produce and why. If a threshold is
only just met, say so with the number: `exhaust_contrast` at 28.4 against 28 is
not the same news as 91.

## 7. If it fails

- **A layer rendered the wrong objects.** `exclude` and `holdout` match by
  **substring** and pull in descendants. Excluding `L.Caterpillar.Geometry` once
  took out `L.Caterpillar.Geometry.Rebuilt` with it, and the layer still rendered
  - the road wheels were in it - so nothing looked empty. Check each layer's
  `rendered` list against what you meant. The opposite slip costs the same: a
  belt layer with *no* `exclude` draws both belts on its root, one inside the
  other, and every existing number passes. `Body.verify` now demands equality.
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
