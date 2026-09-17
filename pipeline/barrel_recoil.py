"""Barrel recoil: the gun tube slides back into the turret when the gun fires.

The tube is a rigid body sliding along its own bore, so unlike every other
effect in the project this one adds no geometry and builds no object. It moves
`Barrel` and renders it as a layer of its own. That is the whole mechanism; all
the thought here went into the four things that are not obvious.

Why it cannot be a sprite shift
-------------------------------
`Barrel` is a child of the turret root, so today it is baked into
`turret_atlas`. Shifting that sprite moves the whole turret. And the travel runs
along the bore, which is a third direction: the harness has ground-plane
directions (`GroundDirection`) and screen shear, and no machinery for an axis
that leaves the ground plane. So it is a render, and it is a new layer.

Two layers, one root
--------------------
The layer root may not be a child of another layer root - that rule costs more
than any other in the project, because a nested root is spun and not put back.
`Barrel.Geometry` is a child of `Turret.World`, so it cannot be a target.

It does not need to be. Both layers take `Turret.World` as their root and cut
each other out with `exclude`, exactly as the hull layer excludes the turret in
the old single-mesh structure. One root, spun and restored once per layer, and
the barrel rides it as a child - which is also what makes the travel turn with
the turret for free, since a local offset under a spun parent is a rotated
offset.

The turret is then held *out* of the barrel layer, and that holdout is legal by
the project's own test: a layer may only bake in a holdout for something that
does not turn relative to it. Turret and barrel hang off the same root and never
move relative to each other, so the occlusion baked into the barrel's alpha is
right at all twelve headings. This matters at the headings where the gun points
away from the camera and the turret body stands in front of the tube.

The framing does not move, and that is checkable
------------------------------------------------
`render_set` measures the fit once for the whole job, before any phase hook has
run, so the barrel contributes its **rest** pose to the fit - just as it does
today from inside the turret layer. Recoil only ever shortens it, so no phase
can reach outside that envelope and the layer needs no `fit: False`.

Which means the split is provably free: composite the barrel's phase 0 over the
turret-without-barrel and it has to come out as today's `turret_atlas`. That one
number covers the exclusion, the holdout, the draw order and the framing at
once. `verify()` is that check.

What the geometry allows, measured rather than assumed
-----------------------------------------------------
The generator builds no hidden surfaces, so the natural worry is that pulling
the tube back opens a hole into an empty turret. Probed on LT_PARTS with rays
along the bore: the turret has an opening around the tube of roughly 1.15-1.4
calibres, and a ray down the bore does not meet a mantlet face at all - it flies
through to the inside of the rear wall.

That opening is already open at rest, and this is why it is safe anyway: the
tube is 32 px long against a travel of a few px, so it never leaves its own
hole, and the annulus around it shows the same thing before and after. `reach()`
measures the real ceiling - how far the muzzle can go before it passes the
turret's own front - and it is the whole length of the tube.

Travel is a fraction of the tube, never a pixel count
-----------------------------------------------------
`units_per_pixel` is a *result* of fitting the camera to the carousel, so a
travel authored in pixels would be a guess at a number the job has not decided
yet - the same trap that puts the hit-plate table between the render and the
check. A gun recoils a fraction of its own tube, so that is the unit, and it is
the same figure on every tank without retuning.

The travel is small, and it was chosen knowing that: a physical 6-9% of tube
length comes out at 1-3 px here. The default is deliberately larger than
physical, for the reason class size is authored rather than measured - two
pixels of truth read as nothing at all.

The stroke is hidden, so the frames go on the return
----------------------------------------------------
A real gun goes back in 30-50 ms and returns over 200-400 ms. The muzzle flash
lasts about six frames, which is the whole of the stroke - so the recoil is
invisible exactly while it happens, and what reads is the tube sliding forward
again afterwards. One phase of kick is therefore all it is worth.

But the phases themselves are spaced **evenly in travel**, not eased towards the
end, and getting that backwards cost 24 of 72 frames on the first attempt. Time
and space are different axes here: how long a pose stays on screen is the hold
table's job, and what a phase owes the renderer is a pose distinguishable from
its neighbour. See `curve`.

Phase 0 is the rest pose, and this layer is never absent
--------------------------------------------------------
Every other event layer - flash, burst, dust - stops being drawn when its clock
runs out. The barrel is part of the tank, so its layer always draws something,
and phase 0 is the pose it draws when nothing is happening. A clock for this
walks 0 -> ... -> 0 rather than 0 -> ... -> off.
"""

import importlib
import math
import os

import numpy as np

CONFIG = {
    "barrel": "Barrel",
    # **The mount the tube slides into**, which is a turret on most vehicles and
    # a welded casemate box on the rest. Every use here is about the mount and
    # none is about a ring: how far the tube may travel before it is inside the
    # box (`reach`), where the trunnion sits (`trunnion`), and what the tube's
    # own layer holds out (`layers`). The name is historical; see
    # tank_parts.describe's `mount_mesh`.
    "turret": "Turret",
    # the layer root both the turret and the barrel are drawn from; the barrel
    # may not be a root of its own (see the module docstring)
    "root": "Turret.World",

    # how far the tube slides, as a fraction of its own length along the bore.
    # Physical is 0.06-0.09; this is larger on purpose - see the docstring.
    "travel": 0.13,

    # phases including the rest pose at index 0. Five, because the travel is
    # about four pixels and a phase that renders the pixels of its neighbour is
    # a duplicate - see `curve`. Raise this only with the travel.
    "phases": 5,

    # phases held at full travel. One: the flash covers the stroke, so spending
    # frames inside it buys nothing. How *long* the return takes is the hold
    # table's business, not this table's.
    "kick_phases": 1,

    # --- laying the gun in elevation ---------------------------------------
    #
    # The board has levels, so a target is not always at the muzzle's own
    # height, and a gun drawn level over a target that is not reads as a shot
    # angled away from its own tube. `Gunnery.ScatterOntoBore` already records
    # the flat-ground half of that as 21.3px of screen-vertical.
    #
    # The angles are *not* chosen. A level is `step_grade` of a cell in ground
    # units, so the world angle onto a target `cells` away and `levels` up is
    # `atan(step_grade*levels/cells)` - independent of the tank, of the scene
    # scale and of `units_per_pixel`. `ladder()` enumerates them; there is
    # nothing between them for the board to ask for.
    #
    # Read off the bench rather than invented: `HexField.StepGrade` defaults to
    # 0.25, `Shell` reaches four cells, and the field has three levels, so two
    # is the largest difference. See docs/gdd/field.md.
    "step_grade": 0.25,
    "range_cells": 4,
    "levels": 2,
    # Angles past this are dropped with a note. Two levels at one cell wants
    # 26.6 deg, which is a real board position and a gun mounting that mostly
    # is not: past the cap the shot exists and the pose for it does not.
    #
    # **It has to clear `atan(step_grade)` and 14.0 does not.** One level at one
    # cell is 14.036, and a cap of exactly 14 dropped the single most common
    # cross-level shot on the board while reporting a tidy-looking table - the
    # quiet direction. Set from the grade, not typed.
    "elev_cap": 15.0,
}


def _mesh(name):
    """The geometry to measure, on either scene structure."""
    return importlib.import_module("tank_parts").mesh(name)


def _world_verts(ob):
    n = len(ob.data.vertices)
    co = np.empty(n * 3)
    ob.data.vertices.foreach_get("co", co)
    M = np.array(ob.matrix_world)
    return co.reshape(n, 3) @ M[:3, :3].T + M[:3, 3]


def bore(cfg=None):
    """Where the bore is and which way is out, from the stamps.

    Read rather than re-measured. `muzzle_point.set_muzzle` already decided this
    axis, `muzzle_flash` builds on it and the pipeline check compares against
    it; a second fit of the same tube is a second thing to keep in agreement,
    and it is the kind that disagrees silently.

    The stamps keep meaning the **rest** pose. Recoil is a phase hook over a
    transform, and nothing here restamps anything.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    bar = _mesh(cfg["barrel"])
    for key in ("muzzle_point", "muzzle_dir"):
        if key not in bar.keys():
            raise RuntimeError(
                "%s carries no %r: run `muzzle_point.set_muzzle()` first. The "
                "bore is not re-measured here on purpose - one fit of the tube, "
                "not two that can drift." % (bar.name, key))
    point = np.array([float(v) for v in bar["muzzle_point"]], float)
    axis = np.array([float(v) for v in bar["muzzle_dir"]], float)
    n = float(np.linalg.norm(axis))
    if n < 1e-9:
        raise RuntimeError("%s has a zero-length muzzle_dir" % bar.name)
    return point, axis / n


def reach(cfg=None):
    """How far the tube may slide, and the numbers that bound it.

    The ceiling is the muzzle reaching the turret's own front along the bore:
    past that the tube is inside the turret and there is nothing left to see
    recoil. Measured, because it is the number that says whether a travel is
    absurd, and it differs per tank.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    point, axis = bore(cfg)
    bar, tur = _mesh(cfg["barrel"]), _mesh(cfg["turret"])
    tb = (_world_verts(bar) - point) @ axis
    tt = (_world_verts(tur) - point) @ axis
    length = float(tb.max() - tb.min())
    return {
        "bore_point": [round(float(v), 6) for v in point],
        "bore_dir": [round(float(v), 6) for v in axis],
        "barrel_along_bore": [round(float(tb.min()), 6), round(float(tb.max()), 6)],
        "barrel_length": round(length, 6),
        "turret_front_along_bore": round(float(tt.max()), 6),
        # the muzzle starts here and may travel until it reaches the turret front
        "max_travel": round(float(tb.max() - tt.max()), 6),
        "travel_world": round(length * float(cfg["travel"]), 6),
        "travel_over_max": round(length * float(cfg["travel"])
                                 / max(float(tb.max() - tt.max()), 1e-9), 4),
    }


def trunnion(cfg=None, cap=None):
    """The pivot the tube lays about, and the numbers that bound the angle.

    **The breech end of the tube, on the bore** - not the mantlet face, and not
    a bounding-box corner. `wreck_pose._trunnion` already decided this for the
    dropped gun of the wreck pose, and a second fit of the same pivot is a
    second thing to keep in agreement: it would agree wherever anybody compared
    the two and part company on the one tank nobody did. So that one now reads
    this, and elevation and the wreck lay the gun about one point.

    Measured on TD_StuG4: the tube reaches 29.6mm behind the casemate's front
    plate, and the aperture the plate leaves round it has a radius of 86.8mm
    against a bore radius of 41.1mm. **The clearance is what makes the angle
    safe, and it is 1.11 bore radii of free ring here** (an aperture 2.11 radii
    wide) - looser than the 1.15-1.4 calibres of opening `reach()` measured on
    LT_PARTS. Laying about a pivot 29.6mm behind that
    plane offsets the tube inside its own hole by `29.6*sin(theta)`, which is
    7.2mm at 14 deg - a sixth of the clearance. Nothing opens, and that is a
    measurement rather than a hope: see `_check_elev_comp2.png`.

    **The angle asked about is the ladder's, not `elev_cap`'s.** `cap` is the
    largest angle actually rendered, and `layers` passes the ladder's own
    maximum because a caller may hand `elev=[...]` a table that overruns the
    config figure - HMP's mortar ladder does, by four degrees. Reading the
    constant instead answers about an angle nobody rendered, and a check that
    answers about the wrong thing is indistinguishable from a check that never
    ran. Absent, it falls back to `elev_cap`, which is what the default ladder
    is built from anyway.

    What it does *not* cover is the eye looking down the aperture at the
    headings where the gun points at the camera and is raised: the tube
    foreshortens to a stub and stops filling the recess on screen even though
    it still fills it in its own plane. The cure for that is geometry - a
    mantlet that belongs to `Barrel` and lays with it - not a smaller angle.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    cap = float(cfg["elev_cap"] if cap is None else cap)
    r = reach(cfg)
    point = np.array(r["bore_point"], float)
    axis = np.array(r["bore_dir"], float)
    breech = float(r["barrel_along_bore"][0])
    pivot = point + axis * breech

    bar, tur = _mesh(cfg["barrel"]), _mesh(cfg["turret"])
    radius = float(bar.get("muzzle_radius") or 0.0)
    tv = _world_verts(tur)
    d = tv - point
    at = d @ axis
    rt = np.linalg.norm(d - np.outer(at, axis), axis=1)
    # the collar: casemate material within a few radii of the bore. Its front
    # is where the tube leaves the tank, and the tightest ring near that plane
    # is the aperture the tube has to keep filling.
    collar = rt < 3.0 * max(radius, 1e-6)
    face = float(at[collar].max()) if collar.any() else float("nan")
    near = collar & (at > face - 0.02)
    aperture = float(rt[near].min()) if near.any() else float("nan")

    warnings = []
    if breech > face:
        warnings.append(
            "the tube ends %.4f in front of the casemate, so there is no pivot "
            "inside the tank at all - it would lay about a point in mid air"
            % (breech - face))
    free = aperture - radius
    offset = abs(face - breech) * math.sin(math.radians(cap))
    # **The claim has to be about what laying changes, not about what the mount
    # already is.** A collar modelled tight enough to interpenetrate the tube at
    # rest is normal - LT_PARTS has a free ring of -0.16 radii and its mantlet
    # hides the seam at every heading. What matters is how much further laying
    # pushes the tube in that plane, and on LT that is 0.0018 world units: 0.28
    # px, a third of a pixel, against a tube 16 px wide. Complaining there is
    # complaining about a render that is right, which is how a check teaches
    # people to skip it. So: the offset has to eat the free ring *and* be worth
    # a fraction of the bore before it is anyone's problem.
    if offset > free and offset > 0.1 * radius:
        warnings.append(
            "laying to %.1f deg moves the tube %.4f across its own collar, "
            "which has only %.4f of free ring (aperture %.4f, bore %.4f) - it "
            "will bind or open a crescent, so the mount needs a shield that "
            "lays with the gun" % (cap, offset, free, aperture, radius))
    return pivot, axis, {
        "trunnion": [round(float(v), 6) for v in pivot],
        "trunnion_from": "breech end of the tube on the bore (wreck_pose's)",
        "bore_dir": r["bore_dir"],
        "bore_radius": round(radius, 6),
        "tube_behind_casemate_face": round(face - breech, 6),
        "aperture_radius": round(aperture, 6),
        "clearance_in_radii": round((aperture - radius) / max(radius, 1e-9), 3),
        # how far the tube shifts inside that hole at the largest angle asked
        "offset_at_cap": round(offset, 6),
        "offset_at_deg": round(cap, 4),
        "free_ring": round(free, 6),
        "warnings": warnings,
    }


def elev_axis(cfg=None):
    """The horizontal axis the tube lays about: across the bore, level.

    Signed so that a positive angle raises the muzzle, which is the only thing
    about it a caller should have to know.
    """
    _, axis, _ = trunnion(cfg)
    n = np.cross(axis, np.array([0.0, 0.0, 1.0]))
    ln = float(np.linalg.norm(n))
    if ln < 1e-9:
        raise RuntimeError("the bore is vertical - there is no laying axis")
    n /= ln
    # rotating u about n by +theta takes it to u*cos + (n x u)*sin, so the sign
    # of the vertical part of (n x u) is the sign of "raises the muzzle"
    if float(np.cross(n, axis)[2]) < 0.0:
        n = -n
    return n


def ladder(cfg=None):
    """The elevations the board can ask for, and nothing between them.

    A level is `step_grade` of a cell's reach in ground units, so the world
    angle onto a target `cells` away and `levels` up is
    `atan(step_grade*levels/cells)`. That makes the table a **fact about the
    board** rather than a taste: on the default quarter grade, four cells of
    reach and three levels it comes to 3.58, 4.76, 7.13, 9.46 and 14.04 deg,
    each of them exact for at least one legal position and none of them
    negotiable. Two levels over one cell wants 26.6 and is dropped by
    `elev_cap` - the position is legal and the mounting is not.

    **Index 0 is level, and that is a compatibility claim, not a convenience.**
    The bench reads `phase` off the barrel layer for the recoil table; with
    elevation as the outer axis and level first, phases 0..n-1 stay exactly the
    recoil table of a level gun. A reader that knows nothing about elevation
    keeps drawing what it draws today.

    Symmetric, because the board is: a level above and a level below are the
    same geometry. A real mounting is not symmetric - guns depress far less
    than they elevate - and nothing here models that.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    grade = float(cfg["step_grade"])
    cap = float(cfg["elev_cap"])
    wanted, dropped = {}, []
    for lv in range(1, int(cfg["levels"]) + 1):
        for cells in range(1, int(cfg["range_cells"]) + 1):
            deg = math.degrees(math.atan2(grade * lv, float(cells)))
            key = round(deg, 3)
            if key > cap + 1e-9:
                dropped.append({"levels": lv, "cells": cells, "deg": key})
                continue
            wanted.setdefault(key, []).append({"levels": lv, "cells": cells})
    table = [0.0]
    for deg in sorted(wanted):
        table.extend([deg, -deg])
    return table, {"table_deg": table, "cap_deg": cap,
                   "step_grade": grade, "asked_by": wanted,
                   "over_cap": dropped}


def curve(cfg=None, phases=None):
    """Travel per phase, as a fraction of the full stroke.

    **Evenly spaced, and that is the correction that matters here.** The first
    version eased the return geometrically, on the reasoning that the eye reads
    the tube coming home and so the late phases deserve the resolution. Measured
    on the render, that produced strokes of 3.68, 1.84, 0.93, 0.93, 0.00 px -
    two duplicate frames and a third indistinguishable from rest, which is 24
    wasted renders out of 72.

    The mistake was conflating two axes. How long a pose is held is *time*, and
    time is the hold table's job - the shot's own table already runs six frames
    at one, six at two and four at four for exactly this. What a phase owes the
    renderer is a pose that is **not the pose next door**, which is *space*. So
    the travel steps evenly and the clock decides how slowly to walk it.

    Which gives the phase count a rule instead of a preference: about one phase
    per pixel of stroke on the best heading. Fewer and the tube jumps; more and
    the extra frames render pixels that are already on disk.

    Index 0 is rest. The table never returns exactly to 0 - the clock goes back
    to phase 0 for that, and a final phase equal to rest is a duplicate render.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    n = int(phases if phases is not None else cfg["phases"])
    if n < 2:
        raise RuntimeError("recoil needs at least a rest phase and a kick")
    out = [0.0] + [1.0] * max(1, min(int(cfg["kick_phases"]), n - 1))
    back = n - len(out)
    for i in range(back):
        out.append((back - i) / float(back + 1))
    return out[:n]


_REST = {}


def pose(cfg=None, travel_world=0.0, elev_deg=0.0):
    """Lay the tube: `elev_deg` about the trunnion, then `travel_world` back.

    One rigid transform for both, and that ordering is the physical one - the
    gun recoils **along its own bore**, so the slide has to be taken in the
    direction the tube is pointing after it is laid, not before. Getting it the
    other way round leaves the tube sliding along the level bore while aimed
    somewhere else, which is a few tenths of a pixel at these angles and wrong
    for a reason that would outlive the angles.

    An object transform, not a vertex rewrite: the tube is rigid, so this is
    free and exactly reversible. Written into `matrix_basis` through the
    parent's space, so a scaled turret root (MT_PARTS carries 0.7) is divided
    back through and both the travel and the pivot come out in world units on
    every tank.
    """
    import bpy
    from mathutils import Matrix, Vector

    cfg = dict(CONFIG, **(cfg or {}))
    bar = _mesh(cfg["barrel"])
    if bar.name not in _REST:
        _REST[bar.name] = bar.matrix_basis.copy()

    world = Matrix.Identity(4)
    if float(elev_deg):
        pivot, _, _ = trunnion(cfg)
        n = elev_axis(cfg)
        world = (Matrix.Translation(Vector(pivot))
                 @ Matrix.Rotation(math.radians(float(elev_deg)), 4, Vector(n))
                 @ Matrix.Translation(-Vector(pivot)))
    if float(travel_world):
        _, axis = bore(cfg)
        along = world.to_3x3() @ Vector(axis)
        world = Matrix.Translation(-along * float(travel_world)) @ world

    parent = (bar.parent.matrix_world @ bar.matrix_parent_inverse
              if bar.parent is not None else Matrix.Identity(4))
    bar.matrix_basis = parent.inverted() @ world @ parent @ _REST[bar.name]
    bpy.context.view_layer.update()
    return {"travel_world": round(float(travel_world), 6),
            "elev_deg": round(float(elev_deg), 4),
            "location": [round(float(v), 6) for v in bar.location]}


def displace(cfg=None, travel_world=0.0):
    """Slide the tube along its bore, with the gun level. See `pose`."""
    return pose(cfg, travel_world=travel_world, elev_deg=0.0)


def restore(cfg=None):
    """Put the tube back where it was found.

    The renderer restores the matrix of a layer *root*; the barrel is a child,
    so nothing else will. Belongs in the caller's `finally`, like everything
    else that poses the scene.
    """
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    bar = _mesh(cfg["barrel"])
    if bar.name in _REST:
        bar.matrix_basis = _REST.pop(bar.name)
        bpy.context.view_layer.update()
        return True
    return False


def hook(cfg=None, travels=None, elevations=None):
    """`phase_hook` for the barrel layer, over two axes at once.

    `travels` overrides the curve with an explicit list of world distances,
    which is how the A/B sheet compares two, four and six pixels of stroke
    against each other in one render. `elevations` is the laying table in
    degrees; `None` keeps the gun level and the layer exactly as it ships
    today, `True` takes `ladder()`.

    **The two axes are a product and cannot be anything else.** The stroke runs
    along the bore, and the bore is a different direction at every elevation -
    which is the same argument that made recoil a render rather than a sprite
    shift in the first place, applied one level up. So the phase index is
    `elev*len(travels) + travel`, elevation outermost, level first: a reader
    that knows only the recoil table finds it unchanged at the front of the
    file. See `ladder`.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    if travels is None:
        stroke = reach(cfg)["travel_world"]
        table = [f * stroke for f in curve(cfg)]
    else:
        table = [float(t) for t in travels]
    if elevations is True:
        elevs = ladder(cfg)[0]
    elif elevations is None:
        elevs = [0.0]
    else:
        elevs = [float(e) for e in elevations]

    def lay(phase, count):
        phase = min(int(phase), len(elevs) * len(table) - 1)
        pose(cfg, travel_world=table[phase % len(table)],
             elev_deg=elevs[phase // len(table)])

    lay.table = table
    lay.elevations = elevs
    lay.phases = len(elevs) * len(table)
    return lay


def verify(cfg=None, output_dir=None):
    """Prove the split costs nothing: rest barrel + shorn turret == turret today.

    Renders three layers in **one** job, so the framing is identical by
    construction rather than by comparison: the turret as it is drawn today
    (tube included), the turret with the tube taken out, and the tube's own
    layer at phase 0. Compositing the last two has to reproduce the first.

    That single comparison covers the exclusion, the holdout, the draw order and
    the fit at once, and it is the reason option A is safe to take: nothing that
    already renders has to move.

    **A hole is a coverage failure, so coverage is what decides.** Judging this
    on colour was the first version and it called a correct split broken: it
    measured 107 levels and said "hole" about a render whose silhouette was
    right to within 90 pixels of edge antialiasing. Two things move colour
    without moving coverage, and both are expected:

    - *the seam.* Two separately antialiased edges meeting along the mantlet do
      not composite back to one coverage, so the boundary ring differs by tens
      of levels. Every layer boundary in the project has this.
    - *the tube's cast shadow on the turret,* which is lost because `exclude`
      hides the tube from the turret's render entirely.

    Losing that shadow is a correction rather than a regression, and it is worth
    being clear about why: a shadow baked into the turret sprite sits at the
    tube's **rest** position and cannot move with it, so it would be wrong at
    every phase except one. It is cross-layer information with one valid pose -
    the same thing the project already refuses when it forbids baking the hull's
    silhouette into the turret's alpha.

    Which is also why the turret must `exclude` the tube and not hold it out. A
    holdout would keep the shadow and cut a rest-shaped hole in the turret, and
    the retracted tube does not fill a rest-shaped hole - that is the one way
    this layer can genuinely show background through the turret.
    """
    import bpy
    import tempfile

    atlas = importlib.import_module("sprite_atlas")
    pr = importlib.import_module("parts_render")
    cfg = dict(CONFIG, **(cfg or {}))
    bar, tur = _mesh(cfg["barrel"]), _mesh(cfg["turret"])
    out_dir = output_dir or os.path.join(tempfile.gettempdir(), "_barrel_verify")
    os.makedirs(out_dir, exist_ok=True)

    body = pr.Body(dict(pr.CONFIG, output_dir=out_dir, track_phases=1,
                        seam_probe=False, hex=None))
    try:
        res = atlas.render_set({
            "shared": dict({"output_dir": out_dir, "steps": pr.CONFIG["steps"],
                            "tile": pr.CONFIG["tile"],
                            "azimuth": pr.CONFIG["azimuth"],
                            "elevation": pr.CONFIG["elevation"]},
                           **body.shared()),
            "layers": [
                {"name": "turret_ref", "target": cfg["root"]},
                {"name": "turret_cut", "target": cfg["root"],
                 "exclude": [bar.name]},
                {"name": "barrel_rest", "target": cfg["root"],
                 "exclude": [tur.name], "holdout": [tur.name],
                 "phases": 1, "phase_hook": lambda p, n: displace(cfg, 0.0)},
            ],
        })
    finally:
        restore(cfg)
        body.restore()

    import numpy as np
    frames = os.path.join(out_dir, "frames")

    def rgba(layer, i):
        img = bpy.data.images.load(
            os.path.join(frames, "%s_%03d.png" % (layer, i)))
        w, h = img.size
        buf = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(buf)
        bpy.data.images.remove(img)
        return buf.reshape(h, w, 4)

    n = len(res["angles"])
    cover_px, cover_max, cover_worst = 0, 0.0, None
    shade_px, shade_max = 0, 0.0
    per_frame = []
    for j in range(n):
        ref = rgba("turret_ref", j)
        got = rgba("turret_cut", j).copy()
        tube = rgba("barrel_rest", j)
        a = tube[:, :, 3:4]
        got[:, :, :3] = tube[:, :, :3] * a + got[:, :, :3] * (1.0 - a)
        got[:, :, 3:4] = a + got[:, :, 3:4] * (1.0 - a)

        # coverage: did the silhouette survive the split? This is the hole test.
        da = np.abs(got[:, :, 3] - ref[:, :, 3]) * 255.0
        # shading: colour where both are solid, so no partial coverage is mixed
        # in - the seam is edge pixels and is deliberately not counted here
        solid = (ref[:, :, 3] > 0.99) & (got[:, :, 3] > 0.99)
        dc = np.abs(got[:, :, :3] - ref[:, :, :3]).max(axis=2) * 255.0 * solid

        per_frame.append({"frame": j,
                          "coverage_over_1": int((da > 1.0).sum()),
                          "coverage_max": round(float(da.max()), 1),
                          "shading_over_1": int((dc > 1.0).sum()),
                          "shading_max": round(float(dc.max()), 1)})
        cover_px += int((da > 1.0).sum())
        shade_px += int((dc > 1.0).sum())
        shade_max = max(shade_max, float(dc.max()))
        if da.max() > cover_max:
            cover_max, cover_worst = float(da.max()), j

    tile = res["layers"]["turret_ref"]["tile"]
    area = int(tile[0]) * int(tile[1]) * n
    hole = cover_px > area * 0.002 or cover_max > 200.0
    return {
        "framing_identical": res.get("framing_identical"),
        "frames": n,
        # the hole test
        "coverage_over_1_level": cover_px,
        "coverage_worst_level": round(cover_max, 1),
        "coverage_worst_frame": cover_worst,
        "coverage_fraction": round(cover_px / float(area), 6),
        # the known, accepted cost: the tube's cast shadow on the turret
        "shading_over_1_level": shade_px,
        "shading_worst_level": round(shade_max, 1),
        "per_frame": per_frame,
        "verdict": ("a hole or a misplacement" if hole else
                    "coverage holds - the split is free, and the shading "
                    "difference is the tube's cast shadow, which could not "
                    "have survived recoil anyway"),
        "output_dir": out_dir,
    }


def check(output_dir, cfg=None, headings=(0, 2, 3, 5), ground=(0.72, 0.70, 0.66)):
    """Measure the stroke off the render, and write `_check_recoil.png`.

    No return value can see a gun that slides the wrong way, so the sheet is the
    check and the numbers only say where to look. Phases across, headings down,
    and the **whole tank** in every cell - a sheet that draws the tube alone
    would be a sheet on which the turret it slides into is never checked.

    Two things are measured rather than eyeballed:

    - *the stroke*, as the drop in how far the tube reaches from the anchor. This
      is the number that says whether the travel is worth its frames, and it
      varies four-fold across headings because the bore leaves the ground plane -
      the same projection that gives the muzzle its 71/60/16 px overhang.
    - *ordering*: every phase after the kick has to come back, and no phase may
      reach further than rest. A table that is transposed or a hook that never
      fired both look right at phase 0 and fail here.
    """
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    frames = os.path.join(output_dir, "frames")
    with open(os.path.join(output_dir, "barrel_atlas.json")) as fh:
        import json
        meta = json.load(fh)
    na = int(meta["count"])
    nph = int(meta["phases"] or 1)
    # elevation is the outer axis when the gun lays, so the stroke is measured
    # inside a rung and the sheet shows one - see `hook`. Flat, the first rung
    # would read as a sixth recoil phase that reaches further than rest.
    elev_tab = (meta.get("elev") or {}).get("table_deg") or [0.0]
    ntr = max(1, nph // max(1, len(elev_tab)))
    anchor = meta["anchor_px"]
    stack = [n for n in ("hex", "hull", "track_left", "track_right", "turret")
             if os.path.exists(os.path.join(frames, "%s_000.png" % n))]

    def rgba(layer, i):
        img = bpy.data.images.load(
            os.path.join(frames, "%s_%03d.png" % (layer, i)))
        w, h = img.size
        buf = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(buf)
        bpy.data.images.remove(img)
        return buf.reshape(h, w, 4)[::-1].copy()

    def over(dst, src):
        a = src[:, :, 3:4]
        dst[:, :, :3] = src[:, :, :3] * a + dst[:, :, :3] * (1.0 - a)
        dst[:, :, 3:4] = a + dst[:, :, 3:4] * (1.0 - a)
        return dst

    # ---- measure ------------------------------------------------------
    reach_px, edge = [], 0.0
    for p in range(nph):
        row = []
        for j in range(na):
            f = rgba("barrel", p * na + j)
            a = f[:, :, 3]
            ys, xs = np.nonzero(a > 0.5)
            if len(xs) == 0:
                row.append(None)
                continue
            row.append(round(float(np.hypot(xs - anchor[0],
                                            ys - anchor[1]).max()), 2))
            edge = max(edge, float(max(a[0].max(), a[-1].max(),
                                       a[:, 0].max(), a[:, -1].max())))
        reach_px.append(row)

    stroke = [[None if (reach_px[(p // ntr) * ntr][j] is None
                        or reach_px[p][j] is None)
               else round(reach_px[(p // ntr) * ntr][j] - reach_px[p][j], 2)
               for j in range(na)] for p in range(nph)]
    peak = max(s for s in stroke[1] if s is not None)
    # the tube may never reach further out than it does at rest
    overshoot = [[p, j, stroke[p][j]] for p in range(nph) for j in range(na)
                 if stroke[p][j] is not None and stroke[p][j] < -0.75]
    # and after the kick it has to be coming home
    table = curve(cfg, ntr)
    returning = all(table[i] > table[i + 1] for i in range(1, ntr - 1))

    # ---- the sheet ----------------------------------------------------
    cells = []
    for j in headings:
        row = []
        for p in range(ntr):
            base = None
            for name in stack:
                f = rgba(name, 0 if name == "hex" else j)
                if base is None:
                    base = np.zeros_like(f)
                    base[:, :, :3] = ground
                    base[:, :, 3] = 1.0
                over(base, f)
            over(base, rgba("barrel", p * na + j))
            row.append(np.clip(base[:, :, :3], 0.0, 1.0))
        cells.append(row)

    ch, cw = cells[0][0].shape[:2]
    sheet = np.zeros((len(cells) * ch, ntr * cw, 4), dtype=np.float32)
    sheet[:, :, 3] = 1.0
    for r, row in enumerate(cells):
        for c, img in enumerate(row):
            sheet[r * ch:(r + 1) * ch, c * cw:(c + 1) * cw, :3] = img
    path = os.path.join(output_dir, "_check_recoil.png")
    img = bpy.data.images.new("_check_recoil", sheet.shape[1], sheet.shape[0],
                              alpha=True)
    img.pixels.foreach_set(sheet[::-1].ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)

    warnings = []
    if edge > 0.0:
        warnings.append("the tube touches the frame edge (alpha %.3f)" % edge)
    if overshoot:
        warnings.append("the tube reaches further than rest at %s - the phase "
                        "table is not a recoil" % overshoot[:4])
    if not returning:
        warnings.append("the stroke does not come home after the kick")
    if peak < 2.0:
        warnings.append("the best heading only moves %.2f px, which reads as "
                        "nothing - raise `travel`" % peak)
    return {
        "phases": nph, "recoil_phases": ntr, "elevations": elev_tab,
        "headings": na,
        "travel_world": round(reach(cfg)["travel_world"], 6),
        "curve": [round(t, 4) for t in table],
        "muzzle_reach_px": reach_px,
        "stroke_px": stroke,
        "peak_stroke_px": peak,
        "stroke_px_range_at_kick": [
            round(min(s for s in stroke[1] if s is not None), 2), peak],
        "max_edge_alpha": round(edge, 4),
        "sheet": path,
        "warnings": warnings,
    }


def layers(cfg=None, travels=None, elevations=None, siblings=None):
    """The mount's layer and the barrel layer, as a pair.

    Returned together because they are one decision: the barrel is only a layer
    of its own because the mount gave it up, and a mount layer that still holds
    the tube beside a barrel layer that also draws it renders the gun twice.

    `siblings` is **everything else the layer root draws** - what the tube has
    to be taken out of, and what has to be taken out of the tube's own layer.
    It defaults to the mount alone, which is the turreted case exactly:
    `Turret.World` holds the mount and the tube and nothing else.

    A casemate's root is the *hull*, which also holds the hull plates, the
    engine and the box itself, so all of those are named instead. Getting this
    wrong is quiet in the worse direction: a barrel layer that excluded only the
    box would draw the whole hull a second time, on the turret's heading,
    slightly out of step with the hull layer under it.
    """
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    bar, tur = _mesh(cfg["barrel"]), _mesh(cfg["turret"])
    shorn = list(siblings) if siblings else [tur.name]

    # `exclude` and `holdout` match by substring, so an ambiguous name here
    # silently takes the wrong mesh out - the failure `L.Caterpillar.Rebuilt`
    # was renamed to avoid. Say so instead.
    for name in [bar.name] + shorn:
        clash = sorted(o.name for o in bpy.data.objects
                       if name in o.name and o.name != name)
        if clash:
            raise RuntimeError(
                "%r is a substring of %s, so excluding it would take those out "
                "too. Rename, or the gun is silently drawn twice or not at all."
                % (name, clash))

    lay = hook(cfg, travels, elevations)
    extra = {"recoil": {
        "travel_world": [round(t, 6) for t in lay.table],
        "bore_dir": reach(cfg)["bore_dir"],
        "barrel_length": reach(cfg)["barrel_length"]}}
    if len(lay.elevations) > 1:
        # the bench solves the bore in closed form off these rather than
        # measuring a muzzle per angle off the pixels: the induced projection
        # carries the axis exactly (0.0002px over 24 headings), and it is only
        # the tube's own thickness that no transform can carry. So the table
        # and the pivot travel with the atlas.
        _, _, rep = trunnion(cfg, cap=max(abs(e) for e in lay.elevations))
        extra["elev"] = {"table_deg": [round(e, 4) for e in lay.elevations],
                         "order": "phase = elev*len(travel) + travel",
                         "trunnion": rep["trunnion"],
                         "axis": [round(float(v), 6) for v in elev_axis(cfg)],
                         "aperture_radius": rep["aperture_radius"],
                         "clearance_in_radii": rep["clearance_in_radii"],
                         "offset_at_cap": rep["offset_at_cap"],
                         "offset_at_deg": rep["offset_at_deg"],
                         # what the mount says about laying at all. It rides in
                         # the atlas because `trunnion` is the only thing that
                         # measures it and nothing else would ever read it: a
                         # check whose answer goes nowhere and a check that was
                         # never run look the same from outside.
                         "warnings": rep["warnings"]}
    return (
        {"name": "turret", "target": cfg["root"], "exclude": [bar.name]},
        {"name": "barrel", "target": cfg["root"], "exclude": shorn,
         # legal: the mount never turns relative to the tube, so its silhouette
         # is right at every heading - and on a casemate neither does the hull
         "holdout": shorn,
         "phases": lay.phases, "phase_hook": lay, "meta_extra": extra},
    )
