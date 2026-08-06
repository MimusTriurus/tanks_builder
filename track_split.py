"""Split the track belts off the hull into `Track.L` and `Track.R`.

The belt is the one part of the running gear that has to move while the tank
drives, so it needs to be its own object: a layer of its own can carry a phase
axis, and left and right can later be driven apart on a pivot turn. Everything
else down there - road wheels, belly, brackets - stays in the hull.

Why this is measured and not marked
-----------------------------------
`Barrel` and `Engine` are cut by hand because they are small ambiguous pieces in
a soup of shells. The belt is not ambiguous, it is just *numerous*: 276 shells on
the right of MT and 256 on the left, and the fifty biggest of them are only 69%
of it. A hand selection that stops early does not read as "a bit less belt", it
reads as a torn track - the links left behind in the hull stand still while their
neighbours move. So the same argument as `hit_point.py` applies: measuring is
better than marking here, not cheaper.

What separates the belt from the hull
-------------------------------------
Base colour, sampled through the UVs. The hull is green (G above both R and B),
the belt is a blue-ish steel (B highest). This is not a saturation test - that
one fails, because the steel is tinted enough to clear any sensible saturation
threshold. It is a *greenness* test, and the two populations do not overlap.

Colour alone is not enough: the tow hooks, the belly and the road wheels are the
same steel. Those are cut off by geometry - the belt is the steel that reaches
out into the outermost slice of the hull's width, low down. The threshold has no
plateau to sit in (shell counts slide smoothly from 654 at 0.320 to 4 at 0.345),
so it is deliberately set **generous**: extra hidden geometry riding along with
the belt is invisible and harmless, a missed link is a tear.

Traps this walks around
-----------------------
* `me.vertices.foreach_set("select", ...)` silently does nothing in current
  Blender - `select` is the lazily created `.select_vert` attribute and the write
  misses. Everything here goes through bmesh, and the selected count is checked
  against the intended count before anything is cut.
* A piece separated off the hull inherits a copy of every stamped property -
  `ring_axis`, `exhaust_*`, `hit_*`. `turret_axis.py` only re-stamps `Turret` and
  `Hull`, so the copies go stale on the next run and then compete for the pick.
  They are cleared here.
* The new objects are parented to `world`, as siblings of `Hull` and `Turret` -
  **not** to `Hull`, which is the obvious choice and is wrong. `render_atlas`
  spins each layer's own target, so a layer root that is itself a child of
  another spun root gets its world matrix written and restored while its parent
  is mid-spin, and it does not come back: the first run left both belts turned
  60 degrees, `rotation_euler` still reading zeros. `Turret` is the pattern to
  copy - anything that gets a layer of its own hangs off `world`.
  They still have to be excluded from the hull layer by name, because that layer
  targets `world` too. See `tank_pipeline.render()`.
"""

import numpy as np

CONFIG = {
    "hull": "Hull",
    "left": "Track.L",
    "right": "Track.R",

    # A shell joins the belt when it is not hull-green and it reaches out past
    # this fraction of the hull's half-width, low enough to be running gear.
    # Both are fractions of the hull's own bounding box, so the numbers carry to
    # the other tanks without a table.
    "outboard_frac": 0.895,     # of half-width; MT -> |x| > 0.325
    "below_frac": 0.70,         # of height, measured up from the ground

    # Greenness = G - max(R, B). Hull paint sits near +0.13, steel near -0.04.
    "green_cut": 0.0,

    # Properties a separated piece inherits and must not keep.
    "stamps": ("ring_axis", "ring_radius", "ring_z", "ring_roundness",
               "exhaust_points", "exhaust_dirs", "exhaust_radii",
               "exhaust_scale", "exhaust_from",
               "hit_faces", "hit_points", "hit_dirs", "hit_tangents",
               "hit_extents", "hit_scale", "hit_from"),
}


# --- measuring ------------------------------------------------------------

def shells(me):
    """Label every vertex with its connected shell.

    Pointer jumping over a fixed scatter order: the gather indices never change,
    so the sort that groups them is paid once and each round is one gather plus
    one reduceat. The mesh is a soup of ~31k shells with a median of 5 verts, so
    this converges in a couple of seconds on a million polygons.
    """
    nv, ne = len(me.vertices), len(me.edges)
    ev = np.empty(ne * 2, np.int32)
    me.edges.foreach_get("vertices", ev)
    ev = ev.reshape(ne, 2)

    idx = np.concatenate([ev[:, 0], ev[:, 1]])
    prt = np.concatenate([ev[:, 1], ev[:, 0]])
    order = np.argsort(idx, kind="stable")
    sidx, sprt = idx[order], prt[order]
    uniq, first = np.unique(sidx, return_index=True)

    lab = np.arange(nv, dtype=np.int32)
    for _ in range(80):
        prev = lab.copy()
        mins = np.minimum.reduceat(lab[sprt], first)
        np.minimum(lab[uniq], mins, out=mins)
        lab[uniq] = mins
        while True:                      # collapse the forest to its roots
            nxt = lab[lab]
            if np.array_equal(nxt, lab):
                break
            lab = nxt
        if np.array_equal(prev, lab):
            break
    _, lab = np.unique(lab, return_inverse=True)
    return lab.astype(np.int32), int(lab.max()) + 1


def base_colour(me, lab, nshell):
    """Mean base-colour texture value per shell, sampled at the loop UVs."""
    mat = me.materials[0]
    # `l.from_node is n` does not work: Blender hands out a fresh RNA wrapper on
    # every access, so identity never holds. Names are the stable key.
    feeds = {l.from_node.name for l in mat.node_tree.links
             if l.to_socket.name == "Base Color"}
    node = next(n for n in mat.node_tree.nodes
                if n.type == "TEX_IMAGE" and n.name in feeds)
    img = node.image
    w, h = img.size
    buf = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(buf)
    tex = buf.reshape(h, w, 4)

    nl = len(me.loops)
    uv = np.empty(nl * 2, np.float32)
    me.uv_layers[0].data.foreach_get("uv", uv)
    uv = uv.reshape(nl, 2)
    lv = np.empty(nl, np.int32)
    me.loops.foreach_get("vertex_index", lv)

    u = np.clip((uv[:, 0] * w).astype(np.int32), 0, w - 1)
    v = np.clip((uv[:, 1] * h).astype(np.int32), 0, h - 1)
    col = tex[v, u, :3]

    sl = lab[lv]
    cnt = np.bincount(sl, minlength=nshell).astype(np.float64)
    cnt[cnt == 0] = 1.0
    return np.stack([np.bincount(sl, weights=col[:, k], minlength=nshell) / cnt
                     for k in range(3)], 1)


def classify(obj, cfg):
    """Vertex masks for the right and left belts, plus what was measured."""
    me = obj.data
    nv = len(me.vertices)
    co = np.empty(nv * 3, np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(nv, 3)
    mw = np.array(obj.matrix_world)
    wc = co @ mw[:3, :3].T + mw[:3, 3]

    lab, ns = shells(me)
    rgb = base_colour(me, lab, ns)
    green = rgb[:, 1] - np.maximum(rgb[:, 0], rgb[:, 2])

    order = np.argsort(lab, kind="stable")
    start = np.searchsorted(lab[order], np.arange(ns))
    xmin = np.minimum.reduceat(wc[order, 0], start)
    xmax = np.maximum.reduceat(wc[order, 0], start)
    zcen = np.bincount(lab, weights=wc[:, 2], minlength=ns) / np.bincount(lab, minlength=ns)

    half = max(abs(wc[:, 0].min()), abs(wc[:, 0].max()))
    ground, roof = wc[:, 2].min(), wc[:, 2].max()
    reach = cfg["outboard_frac"] * half
    ceiling = ground + cfg["below_frac"] * (roof - ground)

    steel = (green < cfg["green_cut"]) & (zcen < ceiling)
    right = steel & (xmax > reach)
    left = steel & (xmin < -reach)

    return {
        "right": right[lab], "left": left[lab],
        "stats": {"shells": ns, "half_width": round(float(half), 4),
                  "reach": round(float(reach), 4), "ceiling": round(float(ceiling), 4),
                  "shells_right": int(right.sum()), "shells_left": int(left.sum())},
    }


# --- cutting --------------------------------------------------------------

def _select(obj, mask):
    """Select exactly the masked vertices, and say how many actually took."""
    import bmesh, bpy
    bpy.ops.object.mode_set(mode="OBJECT")
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")

    bm = bmesh.from_edit_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    for v in bm.verts:
        v.select = False
    for i in np.nonzero(mask)[0]:
        bm.verts[int(i)].select = True
    bm.select_flush(True)
    bmesh.update_edit_mesh(obj.data)
    took = sum(1 for v in bm.verts if v.select)
    return took


def _separate(obj, mask, name, cfg):
    """Cut the masked vertices out of `obj` into a new object called `name`."""
    import bpy
    want = int(mask.sum())
    took = _select(obj, mask)
    if took != want:
        bpy.ops.object.mode_set(mode="OBJECT")
        raise RuntimeError("selection did not take: wanted %d verts, got %d"
                           % (want, took))

    before = set(bpy.data.objects.keys())
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    fresh = [n for n in bpy.data.objects.keys() if n not in before]
    if len(fresh) != 1:
        raise RuntimeError("separate produced %d objects, expected 1" % len(fresh))

    new = bpy.data.objects[fresh[0]]
    new.name = name
    new.data.name = name
    for key in cfg["stamps"]:            # a copy of a stamp is not a stamp
        if key in new:
            del new[key]
    return new


def _parent_to(child, parent):
    """Parent keeping the world transform, without the operator.

    `bpy.ops.object.parent_set` needs `selected_objects`, which the MCP bridge
    does not have. The matrix form is exact and context-free.
    """
    keep = child.matrix_world.copy()
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()
    child.matrix_world = keep


def run(cfg=None):
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    hull = bpy.data.objects[cfg["hull"]]

    for name in (cfg["left"], cfg["right"]):
        if name in bpy.data.objects:
            raise RuntimeError("%s already exists - revert the file and re-run"
                               % name)

    v0, p0 = len(hull.data.vertices), len(hull.data.polygons)
    found = classify(hull, cfg)
    report = dict(found["stats"])
    report["hull_before"] = {"verts": v0, "polys": p0}

    # Both belts leave in one cut: separating the right one first would
    # renumber the vertices the left mask is written against.
    both = found["right"] | found["left"]
    track = _separate(hull, both, "Track", cfg)

    # Now split that small object down the middle. 76k verts, so re-measuring
    # is cheaper than carrying an index map across the first cut.
    nv = len(track.data.vertices)
    tc = np.empty(nv * 3, np.float32)
    track.data.vertices.foreach_get("co", tc)
    tc = tc.reshape(nv, 3)
    tw = tc @ np.array(track.matrix_world)[:3, :3].T + np.array(track.matrix_world)[:3, 3]
    tlab, tns = shells(track.data)
    xcen = (np.bincount(tlab, weights=tw[:, 0], minlength=tns)
            / np.bincount(tlab, minlength=tns))
    right_side = (xcen > 0.0)[tlab]      # by shell, so no link is cut in half

    _separate(track, right_side, cfg["right"], cfg)
    track.name = cfg["left"]
    track.data.name = cfg["left"]

    left, right = bpy.data.objects[cfg["left"]], bpy.data.objects[cfg["right"]]
    # Siblings of the hull, not children of it - see the module docstring.
    for o in (left, right):
        _parent_to(o, hull.parent or hull)
    bpy.context.view_layer.update()

    v1 = len(hull.data.vertices) + len(left.data.vertices) + len(right.data.vertices)
    p1 = len(hull.data.polygons) + len(left.data.polygons) + len(right.data.polygons)
    report.update({
        "left": {"verts": len(left.data.vertices), "polys": len(left.data.polygons)},
        "right": {"verts": len(right.data.vertices), "polys": len(right.data.polygons)},
        "hull_after": {"verts": len(hull.data.vertices),
                       "polys": len(hull.data.polygons)},
        # sums have to match the original exactly: nothing added, nothing lost
        "exact_partition": (v1 == v0 and p1 == p0),
        "vert_delta": v1 - v0, "poly_delta": p1 - p0,
        "parents": {o.name: (o.parent.name if o.parent else None)
                    for o in (left, right)},
        "stamps_left_behind": sorted(k for o in (left, right) for k in o.keys()),
    })
    return report


if __name__ == "__main__":
    import json
    print(json.dumps(run(CONFIG), indent=1))
