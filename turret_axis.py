"""
Measure a turret's true rotation axis and put its origin there.

Run from Blender's Text Editor (Alt+P), or import and call `set_axis(CONFIG)`.

Why this is not just "the centre of the turret": a turret's bounding box is
dragged forward by the gun, and its bottom slab is dragged down by the mantlet,
so both of the obvious estimators miss the ring. Measured on HT.blend, the
bottom-6%-slab estimate put the axis at y=-0.0715 and the correct answer is
y=-0.0075 - a 64mm error, because the mantlet hangs below the ring and joins the
slab. The band scan below avoids that by looking at many thin slices and keeping
the roundest one.

What "roundest" means here is a minimax annulus fit, not a least-squares circle.
Least squares minimises the average error and is therefore pulled around by the
non-circular majority of an octagonal mating face; the annulus width
(max sector radius - min sector radius) is both robust to that and the quantity
that actually governs how much the silhouette breathes on screen.

An axis error does not distort the sprite, it *orbits* it: at angle a the turret
lands `err` off in the direction a, so over a full turn it traces a circle of
radius err/units_per_pixel pixels. That is the "wobbly animation" failure, and
`wobble_px` in the report is the number to judge it by. Under about 1px it is
invisible; MT.blend was at 6.5px before this script.
"""

import importlib.util
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() \
    else r"D:\Projects\AgentCoding\BlenderMCP"
_SIBLINGS = {}


def _sibling(name):
    """Load a module that lives next to this one, once. See muzzle_point."""
    if name not in _SIBLINGS:
        spec = importlib.util.spec_from_file_location(
            name, os.path.join(REPO, "%s.py" % name))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        _SIBLINGS[name] = mod
    return _SIBLINGS[name]

CONFIG = {
    "turret": "Turret",         # name or name fragment
    "hull": "Hull",

    # Band scan. Bands are `band` tall and start every `stride`, both as a
    # fraction of turret height, over the lowest `search` of it. Keep the search
    # shallow: higher up, the roundest thing found is some cupola, not the ring.
    "search": 0.22,
    "band": 0.03,
    "stride": 0.01,
    "sectors": 72,              # angular resolution of the annulus fit
    "min_band_verts": 800,

    # A ring sits on the hull's plane of symmetry, which these models put at
    # x=0. A fit landing further off than this is not a ring, so say so rather
    # than silently moving the origin somewhere wrong.
    "symmetry_x": 0.0,
    "symmetry_tol": 0.01,
    # Force x onto the symmetry plane once the fit has confirmed it belongs
    # there. Halves the degrees of freedom and removes a sub-millimetre jitter.
    "snap_to_symmetry": True,

    # Quality gates. Below `min_roundness` the mating face is not round enough
    # for a static collar to hide the seam - the sprite will show the turret
    # edge moving against the hull. It is still the best axis available, so this
    # warns rather than fails.
    "min_roundness": 0.60,
    "spread_tol": 0.005,        # cross-band axis agreement, metres
    # Only bands at least this fraction as round as the best one are allowed to
    # vote on that agreement figure; the rest are measuring something else.
    "peer_quality": 0.90,
    # How far above the turret's floor the clearance test starts. Below this it
    # is measuring the unopened deck, not a collision. A little over the mating
    # band is right; 0.02 covers all three sample tanks.
    "seam_skip": 0.02,

    "apply": True,              # move the turret's origin onto the axis
    "stamp": True,              # record the measurement on the object
    "check_rotatable": True,
    # For the wobble figure only - what the atlas will actually render at.
    "tile_px": 256,
    "padding": 1.10,
}


def find(hint):
    """The mesh a part name means, by the project's one resolution rule.

    This used to keep its own fragment match and return the *first* scene-order
    hit, which is the stale-axis failure waiting to happen: two meshes mentioning
    "Turret" and the winner is whichever Blender lists first. `tank_parts` raises
    on an ambiguity instead of picking, which is the only safe answer for the one
    number in this project that goes wrong invisibly.
    """
    return _sibling("tank_parts").mesh(hint)


def world_co(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def sector_radii(pts, centre, sectors):
    """Outer radius per angular sector - the outline as seen from `centre`."""
    dx, dy = pts[:, 0] - centre[0], pts[:, 1] - centre[1]
    r = np.hypot(dx, dy)
    idx = ((np.arctan2(dy, dx) + np.pi) / (2 * np.pi) * sectors).astype(int) % sectors
    out = np.zeros(sectors)
    np.maximum.at(out, idx, r)
    return out


def annulus_fit(pts, centre, span, cfg):
    """Centre minimising the outline's annulus width, by nested grid refinement.

    Gradient methods are no use here: the sector maximum is a piecewise-constant
    function of the centre, so the objective has no usable derivative. Five
    refinements at 5x5 cost 125 evaluations and land inside a millimetre.
    """
    best_centre = tuple(centre)
    for step in (span / 6, span / 20, span / 70, span / 250, span / 800):
        best = np.inf
        found = best_centre
        for dx in np.arange(-2, 3) * step:
            for dy in np.arange(-2, 3) * step:
                c = (best_centre[0] + dx, best_centre[1] + dy)
                radii = sector_radii(pts, c, cfg["sectors"])
                # empty sectors mean the centre is outside the outline
                if (radii == 0).sum() > 2:
                    continue
                nz = radii[radii > 0]
                width = nz.max() - nz.min()
                if width < best:
                    best, found = width, c
        best_centre = found
    radii = sector_radii(pts, best_centre, cfg["sectors"])
    nz = radii[radii > 0]
    return best_centre, float(nz.min()), float(nz.max())


def scan_bands(pts, cfg):
    z = pts[:, 2]
    z0, z1 = float(z.min()), float(z.max())
    height = z1 - z0
    span = float(max(np.ptp(pts[:, 0]), np.ptp(pts[:, 1])))
    rows = []
    for f in np.arange(0.0, cfg["search"], cfg["stride"]):
        lo, hi = z0 + f * height, z0 + (f + cfg["band"]) * height
        band = pts[(z >= lo) & (z <= hi)]
        if len(band) < cfg["min_band_verts"]:
            continue
        seed = ((band[:, 0].max() + band[:, 0].min()) / 2.0,
                (band[:, 1].max() + band[:, 1].min()) / 2.0)
        centre, r_min, r_max = annulus_fit(band, seed, span, cfg)
        rows.append({"z": (float(lo), float(hi)), "verts": int(len(band)),
                     "axis": centre, "r_min": r_min, "r_max": r_max,
                     "roundness": r_min / r_max if r_max else 0.0})
    return rows, z0, z1


def verify_rotatable(turret_co, hull_co, axis, seam_skip):
    """Does the turret clear the hull at every height, about this axis?

    Rotation about a vertical axis preserves both z and r, so the turret is free
    exactly when its outermost radius stays inside the hull's innermost one at
    every level where both exist. The test is closed-form - nothing has to be
    rotated to run it.

    The mating plane itself always fails and the failure means nothing. These
    models have no hole in the deck, because a generator never builds surfaces
    it cannot see, so directly under the turret the hull is solid and every
    clearance test reads material there. All three sample tanks failed in their
    bottom slice alone for exactly that reason. `seam_skip` metres above the
    turret's floor are therefore excluded, and it is the remaining margin that
    says whether the turret will clip through the hull on screen.
    """
    rt = np.hypot(turret_co[:, 0] - axis[0], turret_co[:, 1] - axis[1])
    rh = np.hypot(hull_co[:, 0] - axis[0], hull_co[:, 1] - axis[1])
    lo = max(turret_co[:, 2].min(), hull_co[:, 2].min())
    hi = min(turret_co[:, 2].max(), hull_co[:, 2].max())
    if hi <= lo:
        return {"overlap": False, "free": True}
    floor = float(turret_co[:, 2].min())
    edges = np.linspace(lo, hi, 60)
    worst = {"all": (np.inf, None), "above_seam": (np.inf, None)}
    for a, b in zip(edges[:-1], edges[1:]):
        mt = (turret_co[:, 2] >= a) & (turret_co[:, 2] < b)
        mh = (hull_co[:, 2] >= a) & (hull_co[:, 2] < b)
        if not (mt.any() and mh.any()):
            continue
        margin = float(rh[mh].min() - rt[mt].max())
        if margin < worst["all"][0]:
            worst["all"] = (margin, (float(a), float(b)))
        if a >= floor + seam_skip and margin < worst["above_seam"][0]:
            worst["above_seam"] = (margin, (float(a), float(b)))
    m_all, z_all = worst["all"]
    m_up, z_up = worst["above_seam"]
    clear = m_up is np.inf or m_up >= 0.0
    return {
        "overlap": True,
        "min_margin_any_z": None if m_all is np.inf else round(m_all, 5),
        "at_z_any": z_all,
        "min_margin_above_seam": None if m_up is np.inf else round(m_up, 5),
        "at_z_above_seam": z_up,
        "seam_skip": round(seam_skip, 5),
        "free": bool(clear),
        # the seam-only failure is the expected no-hole-in-the-deck artifact
        "seam_only": bool(clear and m_all is not np.inf and m_all < 0.0),
    }


def set_origin(ob, axis, z):
    """Move the object's origin to a world point, leaving geometry where it is.

    Done on the data rather than through `bpy.ops.object.origin_set`, which
    needs a real window context: run over the MCP bridge it dies on
    `context.selected_objects`, and even where it survives it depends on the
    current selection and mode, which a library function has no business
    touching.

    Shifting the vertices by -delta and the matrix by +delta cancel exactly, so
    the mesh does not move in world space by construction.
    """
    me = ob.data
    if me.users > 1:
        raise RuntimeError("%r shares its mesh with %d other objects; moving the "
                           "origin would move theirs too" % (ob.name, me.users - 1))
    target = Vector((axis[0], axis[1], z))
    delta = ob.matrix_world.inverted() @ target      # new origin, in local space
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    co = co.reshape(n, 3) - np.array(delta, dtype=np.float64)
    me.vertices.foreach_set("co", co.reshape(-1))
    me.update()
    ob.matrix_world = ob.matrix_world @ Matrix.Translation(delta)


def measure(cfg):
    turret, hull = find(cfg["turret"]), find(cfg["hull"])
    wt, wh = world_co(turret), world_co(hull)

    rows, tz0, tz1 = scan_bands(wt, cfg)
    if not rows:
        raise RuntimeError("no band held enough vertices to fit")
    rows.sort(key=lambda r: -r["roundness"])
    best = rows[0]
    axis = list(best["axis"])
    warnings = []

    # Cross-band agreement: how much the answer moves if you pick a slightly
    # different slice. Wide spread means the fit is riding on some feature that
    # comes and goes, not on a ring.
    # Only bands of comparable quality get a vote. Taking the top five outright
    # let a 0.42-round band into HT.blend's agreement figure - a slice with the
    # gun in it, answering y=-0.082 against the ring's -0.0075 - and reported a
    # 0.075 spread for a fit whose real slice-to-slice movement is 0.0017.
    peers = [r for r in rows
             if r["roundness"] >= best["roundness"] * cfg["peer_quality"]][:5]
    xs = [r["axis"][0] for r in peers]
    ys = [r["axis"][1] for r in peers]
    spread = float(max(max(xs) - min(xs), max(ys) - min(ys)))

    off_axis = abs(axis[0] - cfg["symmetry_x"])
    if off_axis > cfg["symmetry_tol"]:
        warnings.append(
            "the fitted axis sits %.4f off the hull's symmetry plane; a real "
            "turret ring cannot, so this is probably not the ring" % off_axis)
    elif cfg["snap_to_symmetry"]:
        axis[0] = cfg["symmetry_x"]

    if best["roundness"] < cfg["min_roundness"]:
        warnings.append(
            "the mating face is only %.3f round, so its silhouette will change "
            "as the turret turns and no static collar can hide the seam"
            % best["roundness"])
    if spread > cfg["spread_tol"]:
        warnings.append(
            "neighbouring bands disagree by %.4f on where the axis is - treat "
            "the result as approximate" % spread)

    # what the error costs on screen, at the framing the atlas will use
    sweep = float(np.hypot(wt[:, 0] - axis[0], wt[:, 1] - axis[1]).max())
    hull_sweep = float(np.hypot(wh[:, 0] - axis[0], wh[:, 1] - axis[1]).max())
    upp = 2.0 * max(sweep, hull_sweep) * cfg["padding"] / cfg["tile_px"]
    org = turret.matrix_world.translation
    err = float(np.hypot(org[0] - axis[0], org[1] - axis[1]))

    report = {
        "turret": turret.name, "hull": hull.name,
        "axis": [round(v, 5) for v in axis],
        "seam_z": round(float(best["z"][0]), 5),
        "roundness": round(best["roundness"], 3),
        "radius": [round(best["r_min"], 5), round(best["r_max"], 5)],
        "band": [round(v, 5) for v in best["z"]],
        "band_verts": best["verts"],
        "cross_band_spread": round(spread, 5),
        "off_symmetry_plane": round(off_axis, 5),
        "origin_before": [round(v, 5) for v in org],
        "origin_error": round(err, 5),
        "units_per_pixel": round(upp, 6),
        "wobble_px_before": round(err / upp, 2) if upp else None,
        "turret_sweep_radius": round(sweep, 5),
        "warnings": warnings,
    }
    if cfg["check_rotatable"]:
        report["rotatable"] = verify_rotatable(wt, wh, axis, cfg["seam_skip"])
        if not report["rotatable"].get("free", True):
            warnings.append(
                "the turret still overlaps the hull %.4f above the seam, so it "
                "will clip through it as it turns"
                % abs(report["rotatable"]["min_margin_above_seam"]))
    return turret, hull, axis, float(best["z"][0]), report


def set_axis(cfg=CONFIG):
    turret, hull, axis, seam_z, report = measure(cfg)
    if cfg["apply"]:
        set_origin(turret, axis, seam_z)
        report["origin_after"] = [round(v, 5)
                                  for v in turret.matrix_world.translation]
        report["wobble_px_after"] = 0.0
    if cfg["stamp"]:
        turret["ring_axis"] = [float(axis[0]), float(axis[1])]
        turret["ring_radius"] = report["radius"][1]
        turret["ring_z"] = seam_z
        turret["ring_roundness"] = report["roundness"]
        # the hull needs the same axis: hull and turret sprites only composite
        # by plain overlay if they were spun about one shared point
        hull["ring_axis"] = [float(axis[0]), float(axis[1])]
    bpy.context.view_layer.update()
    print("[turret_axis]", report)
    return report


if __name__ == "__main__":
    set_axis(CONFIG)
