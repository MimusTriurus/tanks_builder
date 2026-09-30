---
description: Rebuild a generator tank as a clean procedural copy next to it, then its game variant for the 3D bench (glTF + sidecar) - measure, author, compare, bake, verify, export
argument-hint: [which scene or tank, e.g. "LT_PARTS" or "MT_PARTS_1 as mt_parts" or "continue lt_parts: thicker skirts" or "continue lt_parts: game"]
---

Make a procedural copy of the generator model that is open in Blender, and
from the same builders its game variant:

- **the copy** (steps 1-8): own geometry, own baked textures, the canonical
  parts structure, standing next to the original in the same scene. It is the
  sprite pipeline's input and the game variant's detailed source.
- **the game variant** (steps 9-12): the model the 3D bench runs - cel
  shading, up to ten tanks on screen, belts as links (decided 2026-09-27).
  Lighter, flat paint, one node per joint, exported to
  `Models/<GAME_TAG>/tank.glb` with a sidecar `tank.json`.

Report what the pictures show, not what the return values say.

Request: $ARGUMENTS

The order, the reasons and the traps are in pipeline/docs/repro.md - read it
first, every time (the game variant: its section "Игровой вариант"). This
command is the checklist. The shape of each tank is authored by hand from
measurements (a module in `pipeline/repro/`); everything around it is
`pipeline/repro_kit.py`. The finished example to copy from is
`pipeline/repro/lt_parts.py`.

"Continue <name>: ..." means the module exists: skip to step 5 with the change,
and after the copy passes, rebuild the game variant too (steps 9-12) - it is
made from the same builders and is stale otherwise. "Continue <name>: game"
means only the game variant: skip to step 9.

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
- the gun: what lays with it in elevation (mantlet, shield, block round the
  tube) and what stays with the turret (the frame, the face plate), and where
  the trunnion goes - across X, on the bore, mid-depth in the part that lays;
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
tone, not only the shape - but the copy is painted in the army's colours
(step 6), so a tone difference from the original is expected, not a defect.

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

Copy the skeleton of `repro/lt_parts.py` (flat plates, prisms) or
`repro/mt_parts.py` (a rounded hull and turret as lofts of plan rings, a belt
from a measured outline): the constants block (`NAME`, `SFX`,
`X_OFF`, `PREFIX`, `GROUND`, `RING_C`, `RING_Z0`, `TRACK_X`, `TRUNNION`,
`PALETTE = ARMY_PALETTE`, and `ORIGINAL` when the scene holds two versions of a
canonical part - the atlas JSONs' `rendered` say which one the sprites used),
then `hull`, `engine`, `turret`, `mantlet`, `barrel`, `belt`,
`rolls` and, if the turret has painted seams, `turret_ink`. Numbers in the
original's frame.

**A casemate** (no `Turret.World` in the scene: TD_StuG4, HM_SturmTiger) has
`casemate(mats)` instead of `turret` and no `RING_C` / `RING_Z0`, plus
`BLAST` (x, y, z: the fighting compartment the debris flies from); the kit
does the rest (docs/repro.md, "Каземат"; the example is
`repro/hm_sturmtiger.py`). A gun that rests raised (a mortar) sets `GUN_EL`
and is built level from the trunnion, then `tilt`ed up by it.

**The gun lays in elevation** - the board has levels, and `barrel_recoil`
renders the gun at every angle of `barrel_recoil.ladder()` (0, ±3.6 ... ±14.0
deg); the bench picks one. So every copy is built for it:

- `mantlet` is everything that lays with the gun and does **not** recoil (the
  block, shield or collar round the tube, its flange and rivets); `barrel` is
  the tube, which lays and recoils. The frame it turns in stays in `turret`.
  A block in `barrel` would slide back into the turret on every shot.
- `TRUNNION = (y, z)`: the axis across X the gun lays about, on the bore.
  `build` puts both objects' origins on it, so laying is `rotation_euler.x`.
- the tube's breech end sits **exactly** on the trunnion (a hidden stub inside
  the mantlet): `barrel_recoil.trunnion()` pivots about the breech end of the
  tube on the bore, so anything else renders the gun about another axis.
- the mantlet's top and bottom are arcs about the trunnion that pass just
  under the frame's inner front edges (lt_parts `mantlet`): the block then
  turns in place at any angle - no slot opens, nothing pokes through.

The other rules that already cost a round each (details in docs/repro.md,
"Ловушки"):

- the ring owns the lowest slices of the turret (dense 192-segment wall, the
  body's foot above it), and the hull's collar reaches up to just under the
  turret's foot, or the turret floats;
- overlapping solids never share a face: hide one 8+ mm inside the other;
- bevels are two segments (the inked outline needs hard edges); stepped lathes
  get no subdivision;
- a skirt's inner face stays outside the belt's outer edge;
- rivets only through `K.rivets` (open domes, the `Ink` rim).

**Write it for the game variant too** - the same builders run a second time
with `K.game()` true:

- constants `BODY_PIVOT = (y, z)` (mid-belt, level with the belt tops: the
  sprung mass rocks there and the fenders barely move against the belts),
  `EXHAUST` (points on the grilles), `GAME_TAG` (the `Models/` folder);
- the running gear in parts: `wheel_spec()` (name, axle, the radius the belt
  turns it at), `wheels(mats, xc, s)` (a Group per wheel), `running_gear`
  (what does not turn), `belt_spec()` (path, pitch, link builder); `rolls`
  and `belt` for the copy are made of these;
- `debris()`: what flies off when the tank blows up - a name, the node it
  comes off (`Hull` or `Turret`) and a box in the original's frame. Every
  piece of that node whose centre is in the box goes with it (a skirt panel
  takes its beam, rivets and handle), so the builders stay as they are. Keep
  boxes clear of pieces that must stay (LT: the fender rail's centre sits
  3 mm inside the skirt's x range, the hinges sit on the seams). Sides are
  the tank's own: L is +X;
- a dark disc over the deck inside the ring, `if game():` - it is what shows
  when the turret is gone or tipped;
- round things only through the kit's primitives (`lathe`, `cyl`, `sphere`,
  `bend_bar`, `K.rivets`) so the game variant halves their segments by
  itself; any other count that is geometry (loft sections, slices) goes
  through `K.segs(n)`, and anything only the copy needs (the ring's 16 slices
  for `turret_axis`) behind `K.game()`.

## 5. Build and compare - then stop and show

```python
stats = K.build(tank)                              # ~10 s
png = K.compare(tank, ["iso_fl", "iso_rr", "side", "front", "top"], "cmp_geo")
lay = K.lay_sheet(tank)          # the gun close up at +max, 0, -max of the ladder
```

Geometry reads fine on the source materials in EEVEE. `K.silhouettes(tank)`
overlays the two masks per view (red: only the original, blue: only the copy)
with an IoU each - a 2 cm offset invisible on a shrunk sheet shows at once, and
which part makes it. Iterate on the module until the silhouettes and parts match and the lay sheet shows the mantlet
turning inside its frame with the tube centred in its collar, **then send both
pictures to the user and wait for a go before baking**: textures cost a minute
a round, and a shape change afterwards means baking again. `K.lay(tank, deg)`
poses the gun by hand; `bake` puts it back to 0 itself.

## 6. Textures

The paint is the army's, not the original's: `PALETTE = K.ARMY_PALETTE`
(calibrated once, on LT_PARTS). The generator paints every tank a little
differently (MT_PARTS_1: 14 deg bluer, greyer, 15 % darker than LT_PARTS),
and one side's tanks must not look like two paint jobs - so do **not**
recalibrate to the original. Override a kind in the module only on purpose
(a kind the army has no entry for is a kit change), and after the bake check
the result, not the original:

```python
K.army_check(tank)     # the copy's roof top-down vs K.ARMY_ROOF: ratio ~1.0 per channel
```

`K.window_means` / `K.paint_band` measure any other render the same way.

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
true (a casemate instead: `casemate` = the hull's root, `turret_root` empty),
`ground_copy` equal to `ground_original`, one material per root,
`no_uvmap`, `custom_props`, `source_left` and `floating` (pieces touching
nothing, like a rivet left over a plate that moved) empty, `engine` back on EEVEE,
and under `gun`: `origin_off` 0 for mantlet and barrel, `rotation` 0 (the gun
is saved at rest), `breech_off` 0.
Then `K.compare(...)` over the full set of views and the close-ups plus
`K.lay_sheet(tank, name="lay_baked")`, and send them. Say plainly what still
differs from the original.

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

`barrel_recoil` does not know `Mantlet.Geometry` yet: until it does, the
pipeline draws the mantlet in the turret layer at rest and lays the tube alone
(about the right axis, thanks to the breech stub). Say so when you report.

## 9. Game variant: build to the budget

Every snippet from here on wears the game variant (own suffix `.Game`,
collection `<NAME>.Game`, one metre past the copy):

```python
tank = K.game_tank(K.load("<name>"))
stats = K.build_game(tank)        # ~2 s; stats["total_tris"] counts every link
```

`total_tris` must stay under `K.GAME_TRIS` (60 000; LT is 59 682). Over it:

```python
K.game_breakdown(tank)            # heaviest lines of the module first
```

(the running gear of one side, the link once). Cut the heaviest line **for
the game variant only** - under `if game():` in the module, or in the kit's
game rules - and never touch the copy's numbers. What LT already needed: a
bent bar swept as one tube instead of balls and cylinders (a shackle ring was
8 000), one-segment chamfers on small parts, a link without chamfers (it is
drawn ~90 times a side), wheel discs at 0.8 of the segments. Rivets stay
geometry: a toon ramp turns normal-map detail into speckle.

## 10. Game variant: bake

**One root per call**: the hull with its debris parts takes ~40 s and the
turret ~25, and the pair ran past the bridge's timeout.

```python
K.bake(tank, ("Hull",))
```
```python
K.bake(tank, ("Turret",))      # not on a casemate: it has no turret root
```
```python
K.bake(tank, ("TrackL",))      # then ("TrackR",) -- the pair ran past the timeout too
```

Flat paint: no painted light, no crease or edge ink - the engine's cel ramp
and outline do those. Seams, rivet rims and broad patches stay, occlusion goes
to the ORM's R (glTF occlusion). Grey count 0, as for the copy.

## 11. Game variant: verify, then look

```python
K.verify_game(tank)
```

must show `origin_off_max` 0 (every joint's origin on its axis: Body on
`BODY_PIVOT`, Turret on the ring, Mantlet and Barrel on the trunnion, each
wheel on its axle), `rotated`, `scaled`, `custom_props`, `no_uvmap`,
`source_left` empty, one material per root, `engine` EEVEE, `tris` under
`budget`. Then:

```python
K.game_sheet(tank)     # rest from four sides + every joint moved once
K.game_compare(tank)   # copy left, game right, close up
K.wreck_sheet(tank)    # knocked out / blowing up / destroyed
```

`verify_game` also counts the debris parts (`debris_missing` empty,
`debris_origin_off_max` 0: each turns about its own centre in flight).
`wreck_sheet` shows the two deaths the sprite bench has, carried over:
knocked out (gun dropped 18 deg, turret tipped into its ring by
wreck_pose's rule, belts slack over the front, the ring's dark opening
showing under the raised side) and destroyed (the turret thrown back and
lying on the engine deck, the debris gone; the middle row is a moment of the
explosion). The thrown turret must rest on the deck - not float, not sink:
`toss_spec` finds how it lies at each of 12 yaws, and `overhang_ok` must be
true for all of them.

When the user wants to see the motion (or the rig changed), render the
previews - one or two clips per call, ~0.17 s a frame:

```python
K.render_clip(tank, "shot"); K.render_clip(tank, "hits")
```
```python
K.render_clip(tank, "drive"); K.render_clip(tank, "knocked"); K.render_clip(tank, "destroyed")
```

then make GIFs from `out/repro/<NAME>/anim/<clip>/f*.png` with the system
Python's Pillow (`py`; Blender's Python has none), captioned, and send them.

`game_sheet` lays the links **from the sidecar's numbers**, the way the engine
will - a belt that does not sit on its wheels there is a wrong recipe, not a
wrong builder. On `game_compare` look for what lightening can take away:
rivet rims (they melted into the plate once, when the coarse dome's first ring
got too little ink), seams, small parts. Send both.

## 12. Game variant: export and read it back

```python
K.export_game(tank)    # Models/<GAME_TAG>/tank.glb + tank.json
K.save(tank)           # the scene copy, now with the game collection
```

`export_game` strips the suffix, puts `Tank` at the origin, and reads the
written file back (`check`): the tree must be `Tank → Body → Hull (Exhaust.N,
the debris parts), Turret (Hatch) → Mantlet → Barrel → Muzzle` and
`Track.L/.R` with every wheel, `Running` and one `Link`; `occlusion` and
`metal_rough` true on all four materials; `total_tris` plus the links' copies
equal to `verify_game`'s. The sidecar now also carries `wreck` (droop, tip
quaternion, slack paths), `toss` (throw, lift, spin, slide and the 12
landings), `debris` (node, parent, size, volume) and `blast`.

The contract the sidecar states, which engine code will lean on:

- glTF frame: +Y up, +Z the tank's front, **+X its left** - `Track.L` is on
  +X, while the canonical scene's `L.*` stands on -X (named from the viewer);
- negative `rotation.x` raises the gun (Mantlet) and the nose (Body);
  positive `rotation.z` rolls the roof to the tank's right; positive
  `rotation.y` turns the turret left; wheels turn `+distance / r`;
- a knocked-out turret is `basis = yaw * tip` (the tip in its own frame);
  a thrown one ends at a `toss.landing` transform, in Body's frame;
- units are the Blender scene's: one scale for every tank, against the hex,
  is not chosen yet.

`Models/` is in git with the .glb through LFS (`.gitattributes`) and the sidecar
as text: commit a rebaked tank's `tank.glb` and `tank.json` together, and only
once the variant is final - every commit of a .glb is another ~10 MB for good.
