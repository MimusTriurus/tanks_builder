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
import os

import numpy as np

CONFIG = {
    "barrel": "Barrel",
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


def displace(cfg=None, travel_world=0.0):
    """Slide the tube along its bore by `travel_world`, into the turret.

    An object transform, not a vertex rewrite: the tube is rigid, so this is
    free and exactly reversible. The offset is applied in the parent's space, so
    a scaled turret root (MT_PARTS carries 0.7) is divided back through and the
    travel comes out in world units on every tank.
    """
    import bpy
    from mathutils import Vector

    cfg = dict(CONFIG, **(cfg or {}))
    bar = _mesh(cfg["barrel"])
    if bar.name not in _REST:
        _REST[bar.name] = bar.location.copy()

    _, axis = bore(cfg)
    delta_world = Vector(-axis * float(travel_world))
    if bar.parent is not None:
        basis = (bar.parent.matrix_world @ bar.matrix_parent_inverse).to_3x3()
        delta_local = basis.inverted() @ delta_world
    else:
        delta_local = delta_world
    bar.location = _REST[bar.name] + delta_local
    bpy.context.view_layer.update()
    return {"travel_world": round(float(travel_world), 6),
            "location": [round(float(v), 6) for v in bar.location]}


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
        bar.location = _REST.pop(bar.name)
        bpy.context.view_layer.update()
        return True
    return False


def hook(cfg=None, travels=None):
    """`phase_hook` for the barrel layer.

    `travels` overrides the curve with an explicit list of world distances,
    which is how the A/B sheet compares two, four and six pixels of stroke
    against each other in one render.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    if travels is None:
        stroke = reach(cfg)["travel_world"]
        table = [f * stroke for f in curve(cfg)]
    else:
        table = [float(t) for t in travels]

    def pose(phase, count):
        displace(cfg, table[min(phase, len(table) - 1)])

    pose.table = table
    return pose


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

    stroke = [[None if (reach_px[0][j] is None or reach_px[p][j] is None)
               else round(reach_px[0][j] - reach_px[p][j], 2)
               for j in range(na)] for p in range(nph)]
    peak = max(s for s in stroke[1] if s is not None)
    # the tube may never reach further out than it does at rest
    overshoot = [[p, j, stroke[p][j]] for p in range(nph) for j in range(na)
                 if stroke[p][j] is not None and stroke[p][j] < -0.75]
    # and after the kick it has to be coming home
    table = curve(cfg, nph)
    returning = all(table[i] > table[i + 1] for i in range(1, nph - 1))

    # ---- the sheet ----------------------------------------------------
    cells = []
    for j in headings:
        row = []
        for p in range(nph):
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
    sheet = np.zeros((len(cells) * ch, nph * cw, 4), dtype=np.float32)
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
        "phases": nph, "headings": na,
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


def layers(cfg=None, travels=None):
    """The turret layer and the barrel layer, as a pair.

    Returned together because they are one decision: the barrel is only a layer
    of its own because the turret gave it up, and a turret layer that still
    holds the tube beside a barrel layer that also draws it renders the gun
    twice.
    """
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    bar, tur = _mesh(cfg["barrel"]), _mesh(cfg["turret"])

    # `exclude` and `holdout` match by substring, so an ambiguous name here
    # silently takes the wrong mesh out - the failure `L.Caterpillar.Rebuilt`
    # was renamed to avoid. Say so instead.
    for name in (bar.name, tur.name):
        clash = sorted(o.name for o in bpy.data.objects
                       if name in o.name and o.name != name)
        if clash:
            raise RuntimeError(
                "%r is a substring of %s, so excluding it would take those out "
                "too. Rename, or the gun is silently drawn twice or not at all."
                % (name, clash))

    pose = hook(cfg, travels)
    return (
        {"name": "turret", "target": cfg["root"], "exclude": [bar.name]},
        {"name": "barrel", "target": cfg["root"], "exclude": [tur.name],
         # legal: the turret never turns relative to the tube, so its
         # silhouette is right at every heading
         "holdout": [tur.name],
         "phases": len(pose.table), "phase_hook": pose,
         "meta_extra": {"recoil": {
             "travel_world": [round(t, 6) for t in pose.table],
             "bore_dir": reach(cfg)["bore_dir"],
             "barrel_length": reach(cfg)["barrel_length"]}}},
    )
