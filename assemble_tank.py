"""
Strip the flat round plate off the turret, then seat the turret on the hull.

Run from Blender's Text Editor (Alt+P) with both models already in the scene.

The two steps are independent - set CONFIG["strip_plate"] / CONFIG["seat"] to
False to skip either one. Nothing is deleted unless the plate is positively
identified; if the detection is not confident the step is reported as skipped
instead of guessing.

Both steps are plain Blender edits, so Ctrl+Z undoes them.

Everything is vectorised through numpy - these meshes run to millions of
polygons, and a per-polygon Python loop over them takes minutes.
"""

import bpy
import numpy as np
from mathutils import Matrix, Vector

CONFIG = {
    # Substring of the object name, matched case-insensitively. The topmost
    # parent of the match is used, so hitting any child is enough.
    "turret": "turret",
    "base": "base",

    "strip_plate": True,
    "seat": True,

    # --- plate detection ----------------------------------------------------
    # The plate is looked for in the bottom `search_height` of the turret, as a
    # slab that is much wider than the body above it and separated from it by a
    # gap containing no geometry at all - cutting inside that gap cannot slice
    # a polygon in half.
    "search_height": 0.45,     # fraction of total turret height
    "min_width_ratio": 1.20,   # plate must be this much wider than the body
    "max_plate_height": 0.25,  # plate must be no taller than this fraction
    "min_plate_share": 0.02,   # and hold at least this share of the vertices

    # --- seating ------------------------------------------------------------
    # Where on the hull the turret goes.
    #   explicit  - mount_xy below, in world units
    #   top_plate - area-weighted centre of the hull's upward faces near the top
    #   bbox      - the hull's bounding-box centre
    # Note that "top_plate" only works when the deck is roughly symmetrical
    # about the ring. On a hull whose engine deck, stowage boxes or drums cover
    # a large area off to one side, their tops outweigh the ring platform and
    # drag the centroid towards them - measure the platform and use "explicit".
    "mount": "explicit",
    "mount_xy": [0.0, -0.07],  # centre of this hull's raised ring platform
    "top_band": 0.25,          # top_plate: fraction of hull height to consider
    # Deck height is sampled within probe_fraction of the ring radius around
    # the mount. Keep it small: a wide probe reaches the sloping glacis and
    # pulls the turret down into the deck.
    "probe_fraction": 0.4,
    "sink": 0.0,               # push the turret down by this fraction of its
                               # height, to hide the seam
    # Scale is left alone while the two models are within this factor of each
    # other. Beyond that they clearly came from different sources, and the
    # turret is resized so that its ring diameter is `ring_fraction` of the
    # hull's width. The ring is measured from the turret's bottom slab, not its
    # bounding box, so a protruding gun barrel does not inflate it.
    "scale_tolerance": 1.6,
    "force_scale": True,       # always match ring_fraction, ignoring the above
    "ring_fraction": 0.55,
    "footprint_slab": 0.12,    # bottom fraction of the turret = its footprint
    "parent": True,            # parent the turret to the hull, keeping its
                               # transform, so it still spins on its own Z
}


# ---------------------------------------------------------------------------
# scene lookup
# ---------------------------------------------------------------------------

def hierarchy(root):
    out, stack = [root], [root]
    while stack:
        for child in stack.pop().children:
            out.append(child)
            stack.append(child)
    return out


def match(hint):
    needle = hint.lower()
    hits = [o for o in bpy.context.scene.objects if needle in o.name.lower()]
    if not hits:
        raise RuntimeError("no object matching %r" % hint)
    hits.sort(key=lambda o: (o.parent is not None, o.name))
    return hits[0]


def resolve_pair(turret_hint, base_hint):
    """Subtree roots for the two models.

    Climbing blindly to the topmost parent breaks once the turret has been
    parented to the hull: both hints then resolve to the same root. So each one
    only climbs as long as the next parent does not swallow the other model.
    """
    t, b = match(turret_hint), match(base_hint)
    if t == b:
        raise RuntimeError("%r and %r both match %r - use more specific hints"
                           % (turret_hint, base_hint, t.name))

    def climb(node, other):
        while node.parent is not None and other not in hierarchy(node.parent):
            node = node.parent
        return node

    return climb(t, b), climb(b, t)


def is_ancestor(node, of):
    walk = of.parent
    while walk is not None:
        if walk == node:
            return True
        walk = walk.parent
    return False


def meshes_of(root):
    return [o for o in hierarchy(root) if o.type == "MESH" and o.data.vertices]


def world_co(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def world_bounds(objects):
    pts = []
    for ob in objects:
        m = ob.matrix_world
        pts.extend(m @ Vector(c) for c in ob.bound_box)
    lo = Vector(tuple(min(p[i] for p in pts) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in pts) for i in range(3)))
    return lo, hi


def poly_z_extents(me, wz):
    """Per-polygon min/max world Z, without a Python loop over polygons."""
    npoly = len(me.polygons)
    starts = np.empty(npoly, dtype=np.int32)
    me.polygons.foreach_get("loop_start", starts)
    loop_verts = np.empty(len(me.loops), dtype=np.int32)
    me.loops.foreach_get("vertex_index", loop_verts)
    zs = wz[loop_verts]
    starts = starts.astype(np.int64)
    return (np.minimum.reduceat(zs, starts),
            np.maximum.reduceat(zs, starts))


def upward_faces(objects, band_z):
    """Centres and areas of upward-facing polygons above `band_z`."""
    centres, areas = [], []
    for ob in objects:
        me = ob.data
        npoly = len(me.polygons)
        if not npoly:
            continue
        nrm = np.empty(npoly * 3, dtype=np.float64)
        me.polygons.foreach_get("normal", nrm)
        cen = np.empty(npoly * 3, dtype=np.float64)
        me.polygons.foreach_get("center", cen)
        ar = np.empty(npoly, dtype=np.float64)
        me.polygons.foreach_get("area", ar)
        m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
        # normals transform by the inverse transpose
        nm = np.linalg.inv(m[:3, :3]).T
        nrm = nrm.reshape(npoly, 3) @ nm.T
        length = np.linalg.norm(nrm, axis=1)
        length[length == 0.0] = 1.0
        nrm /= length[:, None]
        cen = cen.reshape(npoly, 3) @ m[:3, :3].T + m[:3, 3]
        sx, sy, _ = ob.matrix_world.to_scale()
        keep = (nrm[:, 2] > 0.9) & (cen[:, 2] >= band_z)
        if keep.any():
            centres.append(cen[keep])
            areas.append(ar[keep] * abs(sx * sy))
    if not centres:
        return np.zeros((0, 3)), np.zeros(0)
    return np.vstack(centres), np.concatenate(areas)


def weighted_median(values, weights):
    order = np.argsort(values)
    v, w = values[order], weights[order]
    cw = np.cumsum(w)
    return float(v[np.searchsorted(cw, cw[-1] * 0.5)])


# ---------------------------------------------------------------------------
# step 1 - strip the plate
# ---------------------------------------------------------------------------

def find_plate_cut(co, cfg):
    """Return (z_cut, diagnostics); z_cut is None when not confident."""
    z = co[:, 2]
    zmin, zmax = float(z.min()), float(z.max())
    height = zmax - zmin
    diag = {"z_range": [round(zmin, 5), round(zmax, 5)], "height": round(height, 5)}
    if height <= 0.0:
        return None, dict(diag, reason="object is flat")

    centre = co[:, :2].mean(axis=0)
    radius = np.hypot(co[:, 0] - centre[0], co[:, 1] - centre[1])

    bins = 512
    edges = np.linspace(zmin, zmax, bins + 1)
    idx = np.clip(np.searchsorted(edges, z, side="right") - 1, 0, bins - 1)
    counts = np.bincount(idx, minlength=bins)

    limit = zmin + cfg["search_height"] * height
    runs, start = [], None
    for i in range(bins):
        if counts[i] == 0 and start is None:
            start = i
        elif counts[i] != 0 and start is not None:
            runs.append((start, i))
            start = None

    for lo_bin, hi_bin in runs:
        z_lo, z_hi = float(edges[lo_bin]), float(edges[hi_bin])
        if z_hi > limit or counts[:lo_bin].sum() == 0:
            continue
        below, above = z < z_lo, z >= z_hi
        if not below.any() or not above.any():
            continue
        share = float(below.mean())
        plate_h = float(z[below].max() - z[below].min())
        r_below, r_above = float(radius[below].max()), float(radius[above].max())
        ratio = r_below / r_above if r_above else float("inf")
        detail = {"gap": [round(z_lo, 5), round(z_hi, 5)],
                  "vertex_share": round(share, 4),
                  "plate_height": round(plate_h, 5),
                  "width_ratio": round(ratio, 3)}
        if (share >= cfg["min_plate_share"]
                and plate_h <= cfg["max_plate_height"] * height
                and ratio >= cfg["min_width_ratio"]):
            return (z_lo + z_hi) / 2.0, dict(diag, **detail)
        diag.setdefault("rejected", []).append(detail)
    return None, dict(diag, reason="no empty Z gap with a wider slab below it")


def delete_below(ob, z_cut):
    """Delete every vertex under `z_cut`. Returns (dv, dp), or (None, reason).

    The deletion goes through bmesh on purpose. Selecting vertices with
    foreach_set("select", ...) and calling bpy.ops.mesh.delete looks faster,
    but `select` is a lazily created attribute in current Blender versions:
    the write can silently miss, leaving whatever selection the mesh already
    had - which after an import is everything, so the operator wipes the mesh.
    """
    import bmesh

    me = ob.data
    wz = world_co(ob)[:, 2]
    doomed = wz < z_cut
    n_doomed = int(doomed.sum())
    if not n_doomed:
        return 0, 0
    if n_doomed == len(wz):
        return None, "the cut would remove the whole of %s" % ob.name

    # a polygon spanning the cut would be left torn open; the empty Z gap makes
    # that impossible, but verify rather than trust
    pmin, pmax = poly_z_extents(me, wz)
    crossing = int(((pmin < z_cut) & (pmax >= z_cut)).sum())
    if crossing:
        return None, "%d polygons of %s cross the cut plane" % (crossing, ob.name)

    before = (len(me.vertices), len(me.polygons))
    bm = bmesh.new()
    try:
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        verts = bm.verts
        bmesh.ops.delete(bm, geom=[verts[int(i)] for i in np.nonzero(doomed)[0]],
                         context="VERTS")
        bm.to_mesh(me)
    finally:
        bm.free()
    me.update()
    after = (len(me.vertices), len(me.polygons))

    removed = before[0] - after[0]
    if removed != n_doomed:
        return None, ("%s: removed %d vertices but %d were selected"
                      % (ob.name, removed, n_doomed))
    return removed, before[1] - after[1]


def strip_plate(root, cfg):
    objects = meshes_of(root)
    co = np.vstack([world_co(ob) for ob in objects])
    z_cut, diag = find_plate_cut(co, cfg)
    if z_cut is None:
        return {"done": False, "diagnostics": diag}

    total_v = total_p = 0
    per_object = {}
    for ob in objects:
        dv, dp = delete_below(ob, z_cut)
        if dv is None:
            return {"done": False, "diagnostics": dict(diag, reason=dp)}
        if dv or dp:
            per_object[ob.name] = {"removed_verts": dv, "removed_polys": dp}
        total_v += dv
        total_p += dp

    bpy.context.view_layer.update()
    return {"done": True, "z_cut": round(z_cut, 5), "removed_verts": total_v,
            "removed_polys": total_p, "objects": per_object, "detected": diag}


# ---------------------------------------------------------------------------
# step 2 - seat the turret
# ---------------------------------------------------------------------------

def footprint(objects, slab):
    """(diameter, centre_xy) of the bottom `slab` of a hierarchy."""
    co = np.vstack([world_co(ob) for ob in objects])
    z = co[:, 2]
    cut = z.min() + slab * (z.max() - z.min())
    low = co[z <= cut][:, :2]
    if not len(low):
        low = co[:, :2]
    span = max(low[:, 0].max() - low[:, 0].min(),
               low[:, 1].max() - low[:, 1].min())
    centre = np.array([(low[:, 0].max() + low[:, 0].min()) / 2.0,
                       (low[:, 1].max() + low[:, 1].min()) / 2.0])
    return float(span), centre


def mount_point(base_objects, cfg, probe_radius):
    """Where the turret sits on the hull: (Vector(x, y, z), how)."""
    lo, hi = world_bounds(base_objects)
    band = hi.z - cfg["top_band"] * (hi.z - lo.z)
    centres, areas = upward_faces(base_objects, band)

    if cfg["mount"] == "explicit" and cfg.get("mount_xy"):
        x, y = (float(v) for v in cfg["mount_xy"])
        how = "explicit mount point"
    elif cfg["mount"] == "bbox" or not len(centres):
        x, y = (lo.x + hi.x) / 2, (lo.y + hi.y) / 2
        how = "bounding-box centre"
    else:
        total = areas.sum()
        x = float((centres[:, 0] * areas).sum() / total)
        y = float((centres[:, 1] * areas).sum() / total)
        how = "area-weighted centre of the hull's upward faces"

    if not len(centres):
        return Vector((x, y, hi.z)), how + ", hull top (no upward faces found)"

    # sample the deck only right around the mount, so that a sloping glacis or
    # a stowage box further out cannot drag the seating height off the plateau
    near = np.hypot(centres[:, 0] - x, centres[:, 1] - y) <= probe_radius
    if near.sum() >= 8:
        z = weighted_median(centres[near, 2], areas[near])
        return Vector((x, y, z)), how + ", deck height on the ring plateau"
    z = weighted_median(centres[:, 2], areas)
    return Vector((x, y, z)), how + ", median deck height"


def seat(turret_root, base_root, cfg):
    t_objs = meshes_of(turret_root)
    b_objs = meshes_of(base_root)
    t_lo, t_hi = world_bounds(t_objs)
    b_lo, b_hi = world_bounds(b_objs)

    ring_dia, ring_xy = footprint(t_objs, cfg["footprint_slab"])
    hull_width = min(b_hi.x - b_lo.x, b_hi.y - b_lo.y)
    hull_len = max(b_hi.x - b_lo.x, b_hi.y - b_lo.y)
    report = {
        "turret_bbox": [round(t_hi[i] - t_lo[i], 4) for i in range(3)],
        "turret_ring_diameter": round(ring_dia, 4),
        "hull_size": [round(b_hi[i] - b_lo[i], 4) for i in range(3)],
        "hull_width": round(hull_width, 4), "hull_length": round(hull_len, 4),
        "ring_over_hull_width": round(ring_dia / hull_width, 3) if hull_width else None,
    }

    # --- scale, only when the mismatch is gross ---------------------------
    report["scale_applied"] = None
    target_dia = hull_width * cfg["ring_fraction"]
    ratio = ring_dia / target_dia if target_dia else 1.0
    if ratio and (cfg.get("force_scale")
                  or ratio > cfg["scale_tolerance"]
                  or ratio < 1.0 / cfg["scale_tolerance"]):
        factor = 1.0 / ratio
        pivot = Vector((ring_xy[0], ring_xy[1], t_lo.z))
        turret_root.matrix_world = (Matrix.Translation(pivot)
                                    @ Matrix.Diagonal((factor,) * 3).to_4x4()
                                    @ Matrix.Translation(-pivot)
                                    @ turret_root.matrix_world)
        bpy.context.view_layer.update()
        t_lo, t_hi = world_bounds(t_objs)
        ring_dia, ring_xy = footprint(t_objs, cfg["footprint_slab"])
        report["scale_applied"] = round(factor, 5)
        report["turret_ring_diameter_after"] = round(ring_dia, 4)

    # --- move -------------------------------------------------------------
    target, how = mount_point(b_objs, cfg,
                              probe_radius=ring_dia * 0.5 * cfg["probe_fraction"])
    report["mount_point"] = [round(v, 5) for v in target]
    report["mount_method"] = how

    turret_h = t_hi.z - t_lo.z
    # line the ring up with the mount, and drop the ring onto the deck
    delta = Vector((target.x - ring_xy[0],
                    target.y - ring_xy[1],
                    target.z - t_lo.z - cfg["sink"] * turret_h))
    turret_root.matrix_world = Matrix.Translation(delta) @ turret_root.matrix_world
    bpy.context.view_layer.update()
    report["moved_by"] = [round(v, 5) for v in delta]

    # --- parent -----------------------------------------------------------
    # already hanging off the hull from an earlier run - leave the hierarchy be
    if cfg["parent"] and is_ancestor(base_root, turret_root):
        report["parented_to"] = "%s (already, via %s)" % (
            base_root.name, turret_root.parent.name)
    elif cfg["parent"] and turret_root.parent != base_root and turret_root != base_root:
        keep = turret_root.matrix_world.copy()
        turret_root.parent = base_root
        turret_root.matrix_parent_inverse = base_root.matrix_world.inverted()
        turret_root.matrix_world = keep
        bpy.context.view_layer.update()
        report["parented_to"] = base_root.name

    t_lo, t_hi = world_bounds(t_objs)
    report["turret_bounds_after"] = {"min": [round(v, 5) for v in t_lo],
                                    "max": [round(v, 5) for v in t_hi]}
    report["hull_top_z"] = round(b_hi.z, 5)
    return report


# ---------------------------------------------------------------------------

def assemble(cfg):
    turret_root, base_root = resolve_pair(cfg["turret"], cfg["base"])
    out = {"turret_root": turret_root.name, "base_root": base_root.name}
    if cfg["strip_plate"]:
        out["strip_plate"] = strip_plate(turret_root, cfg)
    if cfg["seat"]:
        out["seat"] = seat(turret_root, base_root, cfg)
    print("[assemble]", out)
    return out


if __name__ == "__main__":
    assemble(CONFIG)
