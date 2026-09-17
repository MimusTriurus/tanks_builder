"""
Split a fused tank mesh into a turret and a hull.

Run from Blender's Text Editor (Alt+P).

These generated meshes are not one surface but thousands of small disconnected
shells, one per panel. That is what makes a clean split possible: shells are
assigned whole, so no polygon is ever cut and the two halves partition the
original exactly. Verify that - vertex and polygon counts of the halves must sum
to the original. If they do not, a polygon spanned the boundary and the halves
have holes.

Why not a simple Z plane: the turret's lower plates hang below the hull's
fenders and rear deck, so no horizontal cut separates them - measured on one
model, the best plane still sliced 44 shells. Instead the turret's XY footprint
is taken at a height where only the turret exists, filled, and extended
downwards as a prism. Shells are then classified against that prism.

The footprint has to be *filled*: at any single height the turret is a hollow
shell, so its raw cross-section is an outline, and anything over the middle of
the roof - a cupola, a hatch, an aerial - falls outside the ring and gets
misfiled onto the hull.
"""

import bpy
import bmesh
import numpy as np

CONFIG = {
    "source": "geometry_0",     # the fused mesh
    "turret_name": "Turret",
    "hull_name": "Hull",

    # Height above which *only* the turret exists. The one number that needs
    # measuring per model: slice the mesh and find where the X span collapses
    # from the hull's width (tracks and fenders) to the turret's. Leave as None
    # to detect it from that collapse.
    "clear_z": None,
    "span_drop": 0.75,          # detection: X span below this fraction of the
                                # hull's maximum counts as turret-only
    # Height of the slice the footprint is taken from. None means everything
    # above clear_z, which is what you want: above that height the turret is
    # the only thing there, so the union of it all is both safe and complete.
    # A thin slice silently loses whatever sticks out higher up - it cost a gun
    # barrel sitting at 0.096..0.216 when the slice ended at 0.108.
    "band": None,

    # --- turret ring -------------------------------------------------------
    # The ring is found as the annular free gap that has to exist for the
    # turret to turn at all: at the seam, the turret's skirt is inside, the
    # hull outside, and nothing occupies the space between. Measured, not
    # guessed - it gives the rotation axis the sprite atlases need.
    "find_ring": True,
    # Give the turret its base plate and whatever sits inside the race, using
    # the free gap as the boundary. Those parts are symmetric about the spin
    # axis, so it changes nothing when they turn, and the turret ends up with a
    # closed bottom rather than an opening.
    "ring_to_turret": True,
    "ring_extent": 0.15,        # how far from the mesh centre to search
    "ring_coarse": 0.012,
    "ring_fine": 0.004,
    "z_step": 0.008,            # level thickness for the radial scan
    "r_bin": 0.006,
    "min_side": 200,            # vertices needed on each side of the gap
    "min_run": 3,               # gap must persist over this many levels

    "grid": 0.004,              # footprint raster cell, world units
    "dilate": 4,                # cells; closes the gaps between shells
    "inside_frac": 0.60,        # shell joins the turret at this share of its
                                # vertices inside the prism...
    "inside_frac_low": 0.95,    # ...or this share, if it never reaches clear_z
    "hide_source": True,        # keep the original, hidden, rather than delete
}


def world_co(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def shell_labels(me):
    """Connected-component label per vertex, via union-find over the edges."""
    n = len(me.vertices)
    ne = len(me.edges)
    ev = np.empty(ne * 2, dtype=np.int32)
    me.edges.foreach_get("vertices", ev)
    ev = ev.reshape(ne, 2)
    parent = np.arange(n, dtype=np.int32)

    def find(x):
        root = x
        while parent[root] != root:
            root = parent[root]
        while parent[x] != root:
            parent[x], x = root, parent[x]
        return root

    for a, b in ev:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    roots = np.array([find(i) for i in range(n)], dtype=np.int32)
    _, inv = np.unique(roots, return_inverse=True)
    return inv


def detect_clear_z(w, cfg, bins=34):
    """Lowest height above which the X span has collapsed to the turret's."""
    z = w[:, 2]
    edges = np.linspace(z.min(), z.max(), bins + 1)
    spans = []
    for i in range(bins):
        m = (z >= edges[i]) & (z < edges[i + 1])
        spans.append(np.ptp(w[m, 0]) if m.sum() > 20 else np.nan)
    spans = np.array(spans)
    widest = np.nanmax(spans)
    for i in range(bins):
        if not np.isnan(spans[i]) and spans[i] < widest * cfg["span_drop"] \
                and np.all(np.isnan(spans[i:]) | (spans[i:] < widest * cfg["span_drop"])):
            return float(edges[i]), float(widest)
    raise RuntimeError("could not find where the hull ends; set clear_z by hand")


def _levels(w, cfg):
    z = w[:, 2]
    lvl = ((z - z.min()) / cfg["z_step"]).astype(np.int64)
    return lvl, int(lvl.max()) + 1, float(z.min())


def _gap_runs(w, axis, cfg, lvl, nl, lo, hi):
    """Per level, the annular free gap separating an inner body from an outer one."""
    r = np.hypot(w[:, 0] - axis[0], w[:, 1] - axis[1])
    nr = int(0.75 / cfg["r_bin"]) + 1
    occ = np.zeros((nl, nr), dtype=np.int64)
    np.add.at(occ, (lvl, np.clip((r / cfg["r_bin"]).astype(np.int64), 0, nr - 1)), 1)

    per = {}
    for level in range(lo, hi):
        row = occ[level]
        if row.sum() < 40:
            continue
        nz = np.nonzero(row)[0]
        if len(nz) < 2:
            continue
        j, last = int(nz[0]), int(nz[-1])
        while j <= last:
            if row[j] == 0:
                k = j
                while k <= last and row[k] == 0:
                    k += 1
                if k <= last and (k - j) >= 2:
                    inner, outer = row[:j].sum(), row[k:].sum()
                    if inner >= cfg["min_side"] and outer >= cfg["min_side"]:
                        per[level] = ((j + k) * cfg["r_bin"] / 2.0,
                                      (k - j) * cfg["r_bin"])
                        break
                j = k
            else:
                j += 1

    best, cur = [], []                       # longest consecutive run
    for level in range(lo, hi):
        if level in per:
            cur.append(level)
        else:
            if len(cur) > len(best):
                best = cur
            cur = []
    if len(cur) > len(best):
        best = cur
    return best, per


def detect_ring(w, cfg):
    """Axis, radius and height band of the turret ring, or None."""
    lvl, nl, zmin = _levels(w, cfg)
    height = w[:, 2].max() - zmin
    lo = int(0.20 * height / cfg["z_step"])
    hi = min(int(0.85 * height / cfg["z_step"]), nl)
    cx0 = (w[:, 0].max() + w[:, 0].min()) / 2.0
    cy0 = (w[:, 1].max() + w[:, 1].min()) / 2.0

    def search(centre, extent, step):
        best = None
        for dx in np.arange(centre[0] - extent, centre[0] + extent + 1e-9, step):
            for dy in np.arange(centre[1] - extent, centre[1] + extent + 1e-9, step):
                run, per = _gap_runs(w, (dx, dy), cfg, lvl, nl, lo, hi)
                if len(run) < cfg["min_run"]:
                    continue
                score = (len(run), sum(per[L][1] for L in run))
                if best is None or score > best[0]:
                    best = (score, (float(dx), float(dy)), run, per)
        return best

    best = search((cx0, cy0), cfg["ring_extent"], cfg["ring_coarse"])
    if best is None:
        return None
    best = search(best[1], cfg["ring_coarse"], cfg["ring_fine"]) or best

    _, axis, run, per = best
    return {"axis": [round(axis[0], 5), round(axis[1], 5)],
            "radius": round(float(np.median([per[L][0] for L in run])), 5),
            "gap_width": round(float(np.mean([per[L][1] for L in run])), 5),
            "z": [round(zmin + run[0] * cfg["z_step"], 5),
                  round(zmin + (run[-1] + 1) * cfg["z_step"], 5)],
            "levels": len(run)}


def verify_rotatable(w, vt, axis, cfg, tol=0.0):
    """Can the turret actually turn?

    A rotation about a vertical axis preserves both height and radius, so the
    turret is free exactly when, at every level where both parts exist, the
    turret's outermost radius stays inside the hull's innermost one. No need to
    rotate anything - the test is closed-form, and it is the real definition of
    a correct split.
    """
    lvl, nl, zmin = _levels(w, cfg)
    r = np.hypot(w[:, 0] - axis[0], w[:, 1] - axis[1])
    worst = None
    checked = 0
    for level in range(nl):
        m = lvl == level
        t, h = m & vt, m & ~vt
        if t.sum() < 10 or h.sum() < 10:
            continue                       # only the gun up here, or only hull
        checked += 1
        margin = float(r[h].min() - r[t].max())
        if worst is None or margin < worst["margin"]:
            worst = {"margin": round(margin, 5),
                     "z": round(zmin + level * cfg["z_step"], 5),
                     "turret_r_max": round(float(r[t].max()), 5),
                     "hull_r_min": round(float(r[h].min()), 5)}
    return {"levels_checked": checked, "tightest": worst,
            "rotatable": bool(worst is None or worst["margin"] > tol)}


def filled_footprint(w, cfg, clear_z):
    """Raster mask of the turret's XY footprint, interior holes closed."""
    g = cfg["grid"]
    x0, x1 = w[:, 0].min() - 0.02, w[:, 0].max() + 0.02
    y0, y1 = w[:, 1].min() - 0.02, w[:, 1].max() + 0.02
    nx, ny = int((x1 - x0) / g) + 1, int((y1 - y0) / g) + 1

    def cells(p):
        return (np.clip(((p[:, 1] - y0) / g).astype(int), 0, ny - 1),
                np.clip(((p[:, 0] - x0) / g).astype(int), 0, nx - 1))

    mask = np.zeros((ny, nx), dtype=bool)
    above = w[:, 2] >= clear_z
    if cfg["band"]:
        above &= w[:, 2] < clear_z + cfg["band"]
    band = w[above]
    if not len(band):
        raise RuntimeError("nothing in the footprint slice at z >= %g" % clear_z)
    iy, ix = cells(band)
    mask[iy, ix] = True

    for _ in range(cfg["dilate"]):                      # close shell gaps
        d = mask.copy()
        d[1:, :] |= mask[:-1, :]; d[:-1, :] |= mask[1:, :]
        d[:, 1:] |= mask[:, :-1]; d[:, :-1] |= mask[:, 1:]
        mask = d

    free = ~mask                                        # flood in from the rim
    outside = np.zeros_like(mask)
    outside[0, :] |= free[0, :]; outside[-1, :] |= free[-1, :]
    outside[:, 0] |= free[:, 0]; outside[:, -1] |= free[:, -1]
    while True:
        d = outside.copy()
        d[1:, :] |= outside[:-1, :]; d[:-1, :] |= outside[1:, :]
        d[:, 1:] |= outside[:, :-1]; d[:, :-1] |= outside[:, 1:]
        d &= free
        if np.array_equal(d, outside):
            break
        outside = d
    return ~outside, cells


def barrel_direction(turret_pts, axis, sectors=24):
    """Which way the gun points, as a weak cross-check on the import convention.

    The barrel is the turret's longest reach from the ring axis. On a boxy
    turret that is a thin margin - measured 0.40 against 0.379 for the rear
    corners on one model - so `confidence` matters: near 1.0 means the shape
    barely favours any direction and the result should not be trusted over a
    stated convention. It is here to catch a model imported back to front, not
    to decide the facing.
    """
    v = turret_pts[:, :2] - np.array(axis, dtype=np.float64)
    r = np.hypot(v[:, 0], v[:, 1])
    ang = (np.degrees(np.arctan2(v[:, 1], v[:, 0])) + 360.0) % 360.0
    step = 360.0 / sectors
    reach = np.full(sectors, np.nan)
    for s in range(sectors):
        m = (ang >= s * step) & (ang < (s + 1) * step)
        if m.sum() > 40:
            reach[s] = np.percentile(r[m], 99)
    if np.all(np.isnan(reach)):
        return {"direction": None, "confidence": 0.0}
    s = int(np.nanargmax(reach))
    med = float(np.nanmedian(reach))
    return {"direction": round((s + 0.5) * step, 1),
            "confidence": round(float(reach[s] / med) if med else 0.0, 3)}


def carve(src, name, keep):
    """A copy of `src` holding only the vertices flagged in `keep`."""
    me = src.data.copy()
    me.name = "geometry_%s" % name.lower()
    ob = bpy.data.objects.new(name, me)
    (src.users_collection[0] if src.users_collection
     else bpy.context.scene.collection).objects.link(ob)
    ob.matrix_world = src.matrix_world.copy()
    ob.parent = src.parent
    ob.matrix_parent_inverse = src.matrix_parent_inverse.copy()

    bm = bmesh.new()
    try:
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        bmesh.ops.delete(bm, context="VERTS",
                         geom=[bm.verts[int(i)] for i in np.nonzero(~keep)[0]])
        bm.to_mesh(me)
    finally:
        bm.free()
    me.update()
    return ob


def split(cfg):
    src = bpy.data.objects[cfg["source"]]
    w = world_co(src)
    inv = shell_labels(src.data)
    k = int(inv.max()) + 1
    cnt = np.bincount(inv, minlength=k)

    ring = detect_ring(w, cfg) if cfg["find_ring"] else None

    if cfg["clear_z"] is None:
        clear_z, widest = detect_clear_z(w, cfg)
        detected = {"clear_z": round(clear_z, 5), "hull_x_span": round(widest, 4)}
    else:
        clear_z = float(cfg["clear_z"])
        detected = {"clear_z": clear_z, "source": "config"}
    # the footprint slice must sit above the seam, or it would take in hull
    if ring and clear_z <= ring["z"][1]:
        detected["warning"] = ("clear_z %.4f is not above the ring top %.4f"
                               % (clear_z, ring["z"][1]))

    mask, cells = filled_footprint(w, cfg, clear_z)
    iy, ix = cells(w)
    inside = mask[iy, ix]

    frac_in = np.bincount(inv, weights=inside.astype(float), minlength=k) \
        / np.maximum(cnt, 1)
    zmax = np.full(k, -np.inf); np.maximum.at(zmax, inv, w[:, 2])
    zmin = np.full(k, np.inf);  np.minimum.at(zmin, inv, w[:, 2])

    turret = (frac_in >= cfg["inside_frac"]) & (zmax >= clear_z)
    if not turret.any():
        raise RuntimeError("no shell qualified as turret; check clear_z")

    if ring and cfg["ring_to_turret"]:
        # The free gap *is* the seam, so it decides which side owns the plates
        # that sit in it: above the gap turns with the turret, below stays with
        # the hull. Handing the turret its base plate and anything inside the
        # race costs nothing - those parts are rotationally symmetric about the
        # axis we spin around - and it closes the turret's underside instead of
        # leaving a hole into it.
        rr = np.hypot(w[:, 0] - ring["axis"][0], w[:, 1] - ring["axis"][1])
        r_out = ring["radius"] + ring["gap_width"] / 2.0
        in_ring = np.bincount(inv, weights=(rr <= r_out).astype(float),
                              minlength=k) / np.maximum(cnt, 1)
        z0, z1 = ring["z"]
        turret |= (in_ring >= cfg["inside_frac"]) & (zmin >= z0) & (zmax <= clear_z)
        floor = z1
    else:
        floor = float(zmin[turret].min())

    # shells wholly inside the prism that never reach clear_z: bolts, fittings
    turret |= (frac_in >= cfg["inside_frac_low"]) & (zmin >= floor)
    z_floor = float(zmin[turret].min())

    vt = turret[inv]
    t_ob = carve(src, cfg["turret_name"], vt)
    h_ob = carve(src, cfg["hull_name"], ~vt)

    # Stamp the measured ring onto the turret so later steps do not have to
    # re-estimate it. A bottom-slab guess lands within ~0.006 of this and drifts
    # with the slab fraction; a bounding-box centre is off by ~0.07 because the
    # gun drags it forward. hex_base and sprite_atlas read these back.
    front_guess = None
    if ring:
        t_ob["ring_axis"] = [float(v) for v in ring["axis"]]
        t_ob["ring_radius"] = float(ring["radius"])
        t_ob["ring_gap"] = float(ring["gap_width"])
        t_ob["ring_z"] = [float(v) for v in ring["z"]]
        front_guess = barrel_direction(w[vt], ring["axis"])
        t_ob["front_dir_guess"] = front_guess["direction"]
        t_ob["front_dir_confidence"] = front_guess["confidence"]
    if cfg["hide_source"]:
        src.hide_viewport = src.hide_render = True
    bpy.context.view_layer.update()

    tv, tp = len(t_ob.data.vertices), len(t_ob.data.polygons)
    hv, hp = len(h_ob.data.vertices), len(h_ob.data.polygons)
    ov, op = len(src.data.vertices), len(src.data.polygons)
    report = {
        "detected": detected,
        "ring": ring,
        "front_dir_guess": front_guess,
        "rotatable": (verify_rotatable(w, vt, ring["axis"], cfg)
                      if ring else None),
        "shells": {"total": k, "turret": int(turret.sum())},
        "turret": {"object": t_ob.name, "verts": tv, "polys": tp,
                   "dims": [round(v, 4) for v in t_ob.dimensions],
                   "z_floor": round(z_floor, 4)},
        "hull": {"object": h_ob.name, "verts": hv, "polys": hp,
                 "dims": [round(v, 4) for v in h_ob.dimensions]},
        # the halves must partition the original: any shortfall means a polygon
        # spanned the boundary and was dropped from both sides
        "exact_partition": (tv + hv == ov) and (tp + hp == op),
        "totals": {"verts": [tv + hv, ov], "polys": [tp + hp, op]},
    }
    print("[split]", report)
    return report


if __name__ == "__main__":
    split(CONFIG)
