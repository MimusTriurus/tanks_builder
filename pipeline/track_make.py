"""Build a default pair of track belts, for a tank that arrived without any.

The generator hands over hulls with no running gear at all - the references are
`Images/*_no_tracks.png` - so `track_split.py` has nothing to cut: it takes a
belt off a hull that has one. This makes one instead, out of a shape, and leaves
the sizing to the bench.

What it makes is a real track and not a band
--------------------------------------------
One shoe, repeated `links` times at `perimeter / links` round a `rounded_loop`.
That is the construction `track_cycle.rebuild` uses, and it buys the same thing:
sliding by one pitch puts copy k where copy k+1 was and copy `links-1` onto copy
0, so the phase cycle closes by construction rather than by tuning.

A belt drawn as a smooth extruded band would have no pitch to find, and
`Belt._pitch` reads the pitch off the *tread relief* on the straight runs - so
the grouser bar is not decoration, it is the thing that makes the belt
measurable at all. `verify` checks that it was read that way and not off the
vertex density, which is the one fallback that would pass silently.

The layout goes through `Belt.transport` itself rather than a copy of it. The
map that lays the shoes out is then literally the map that later slides them,
which is the one property the closure argument rests on.

Sized off the hull, fitted by hand afterwards
---------------------------------------------
Every size defaults to a fraction of the hull's own box, so this runs on a tank
it has not seen. They are defaults and not answers: the loop's length, height
and corner are what `track_shape.py` exists to drag, and the numbers here only
have to be close enough to drag from.

Two of them the bench cannot touch, and they are therefore the two to read in
the report: `width` across the belt, and `x_centre`, how far out it sits. The
bench reshapes a *loop*, and a loop is a curve in the belt's own xz - how wide
the tread sits on it, and which side of the tank it is on, are not loop
dimensions and no amount of dragging will reach them.

Where it sits, and why the root turns
-------------------------------------
`Track.Left.World` and `Track.Right.World` are root-level empties, never children
of `Hull.World`: a layer root nested under another layer root gets its world
matrix written and restored mid-spin by the renderer and does not come back. See
docs/tank-scene.md, which is also where the two-name convention comes from - the
side spelled in the middle for a root, in front for a mesh.

Both roots carry the same rotation, -90 degrees about Z, and differ only in the
X of their location. That is the delivered convention (LT_PARTS carries it as a
quaternion, which is why its `rotation_euler` reads zeros) and it is what makes
`track_shape.py`'s instruction true: with the root turned exactly 90 degrees,
global Y is the belt's own length and global Z its height, so `S Y` and `S Z` in
the viewport land in the scale components `track_cycle.preview` reads back.

Unlike the delivered belts, nothing here carries a squash: root and mesh are
both at scale 1, so mesh units are world units and `in_world` costs nothing.
Worth knowing before comparing any number below against MT_PARTS or LT_PARTS,
where the same figure arrives multiplied by 0.94 along the loop and 0.66 up.
"""

import math

import numpy as np

import track_cycle

CONFIG = {
    "hull": "Hull.Geometry",            # measured, never touched
    "roots": ("Track.Left.World", "Track.Right.World"),
    "belts": ("L.Caterpillar.Geometry", "R.Caterpillar.Geometry"),
    "rolls": ("L.Rolls.Geometry", "R.Rolls.Geometry"),   # None -> belts only

    # The loop, in world units. None measures it off the hull with the fractions
    # below - which is the whole reason this runs on an unseen tank.
    "length": None,                     # of the path, not of the mesh box
    "height": None,
    "width": None,                      # across the belt
    "corner": 1.0,                      # radius as a fraction of half the height
    "x_centre": None,                   # |x| of the belt's own centreline
    "y_centre": None,
    "ground_z": None,                   # where the tread's lowest point lands

    # Fractions of the hull's own box. LT_PARTS for scale: its loop is 0.91 of
    # the hull long and 0.66 of it tall, the tread 0.21 of it wide, riding 0.13
    # of its height below the belly.
    "length_frac": 0.94,
    "height_frac": 0.62,
    "width_frac": 0.21,
    "drop_frac": 0.12,                  # below the hull's underside
    # how much of the belt's width tucks inboard of the hull's lower side: 0 is
    # entirely outboard of it, 1 entirely under it. The lower side and not the
    # widest point, because the widest point is usually the fender.
    "overlap_frac": 0.35,
    "body_percentile": 99.9,            # shrugs off aerials and other spikes
    "low_band_frac": 0.30,              # the bottom slice that side is read on

    # 47 on LT_PARTS, 46 on MT_PARTS. It has to leave `Belt`'s two windows
    # happy: 25..80 for the vertex density, and 5..30 grousers across the 60%
    # of the length the tread is read on.
    "links": 47,

    # The shoe. Lengths along the belt are fractions of the link pitch, so the
    # tread keeps its proportions when the loop is dragged afterwards.
    "plate": 0.26,                      # thickness of the shoe plate
    # Between one shoe and the next, and it wants to be 0 - see `_check_gap`.
    # An open gap is a wedge of bearing with no geometry in it at all, and
    # `Belt._trace` refuses a loop with an empty bearing bin, so a visible gap
    # makes a belt nothing downstream can measure. The grousers separate the
    # shoes to the eye; the gap does not have to.
    "gap": 0.0,
    "grouser": 0.30,                    # how far the bar stands off the plate
    "grouser_len": 0.34,                # along the belt
    # points round one shoe's side outline. None derives the floor below and
    # then doubles it until no bearing bin is empty - see `_points_floor`. A
    # number here is a floor of your own; the doubling still applies.
    "profile_points": None,
    "s_margin": 2.0,                    # headroom on that floor before doubling
    "across_segments": 2,               # so the ground run carries more than
                                        # `_run_profile`'s 1000-vertex minimum

    # The running gear the belt wraps. A bare belt reads as a floating band, and
    # the canonical track layer carries both meshes.
    "wheels": 5,                        # road wheels between the two ends
    "wheel_segments": 24,
    "end_wheel_frac": 0.86,             # of half the loop height
    "road_wheel_frac": 0.52,
    "wheel_width_frac": 0.60,           # of the belt's width

    "path_nodes": 1024,
    "samples": 8192,

    # One material per root, which is the canon. Dark, and metal enough to catch
    # the key light the way the delivered belts do.
    "material": {"base_color": (0.050, 0.048, 0.045, 1.0),
                 "metallic": 0.65, "roughness": 0.55},
}


# --- the hull, as a box worth measuring against ---------------------------

def _hull_box(ob, pct):
    """The hull's extent in world, with the thin spikes shrugged off.

    A percentile and not a bounding box: HM carries an aerial 0.17 tall and two
    vertices wide, and a box that believes it makes the hull half again as tall
    as the tank is - which would then be 62% of nothing like the right height.
    """
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(n, 3).astype(np.float64)
    m = np.array(ob.matrix_world)
    w = co @ m[:3, :3].T + m[:3, 3]
    return w, np.percentile(w, 100.0 - pct, axis=0), np.percentile(w, pct, axis=0)


def _low_half_width(w, lo, hi, frac, pct):
    """How wide the hull is down where the running gear goes.

    The belt hangs off the hull's *lower* side, which on a tank with fenders is
    not the widest point and on a tank without them is.
    """
    band = w[:, 2] < lo[2] + frac * (hi[2] - lo[2])
    x = w[band, 0]
    return float(max(abs(np.percentile(x, 100.0 - pct)),
                     abs(np.percentile(x, pct))))


def sizes(cfg=None):
    """Every number the belts get built from, measured or taken as given."""
    import bpy
    cfg = dict(CONFIG, **(cfg or {}))
    hull = bpy.data.objects.get(cfg["hull"])
    if hull is None or hull.type != "MESH":
        raise RuntimeError("no hull mesh %r to measure against" % (cfg["hull"],))

    w, lo, hi = _hull_box(hull, cfg["body_percentile"])
    box = hi - lo
    side = _low_half_width(w, lo, hi, cfg["low_band_frac"], cfg["body_percentile"])

    length = cfg["length"] or box[1] * cfg["length_frac"]
    height = cfg["height"] or box[2] * cfg["height_frac"]
    width = cfg["width"] or box[0] * cfg["width_frac"]
    ground = cfg["ground_z"] if cfg["ground_z"] is not None \
        else lo[2] - box[2] * cfg["drop_frac"]
    x_centre = cfg["x_centre"] if cfg["x_centre"] is not None \
        else side + width * (0.5 - cfg["overlap_frac"])
    y_centre = cfg["y_centre"] if cfg["y_centre"] is not None \
        else 0.5 * (lo[1] + hi[1])

    return {
        "hull_box": [round(float(v), 5) for v in box],
        "hull_min": [round(float(v), 5) for v in lo],
        "hull_max": [round(float(v), 5) for v in hi],
        "hull_lower_half_width": round(side, 5),
        "length": float(length), "height": float(height), "width": float(width),
        "corner": float(cfg["corner"]),
        "ground_z": float(ground), "x_centre": float(x_centre),
        "y_centre": float(y_centre),
    }


# --- the loop, in the shape the transport wants ---------------------------

class _Path:
    """A `rounded_loop` carrying `Belt`'s own map from belt space back to xyz.

    Borrowed rather than reimplemented on purpose: closure holds only while the
    layout and the later slide use the same map, and two copies of a map are two
    maps.
    """

    transport = track_cycle.Belt.transport

    def __init__(self, length, height, corner, nodes, samples):
        c, self.analytic = track_cycle.rounded_loop(length, height, corner,
                                                    samples)
        self.perimeter, self.node, self.ds, self.tan, self.nor = \
            track_cycle._resample(c, int(nodes))
        self.centre = np.zeros(3)


# --- one shoe -------------------------------------------------------------

def _densify(poly, target):
    """A closed polygon resampled to about `target` points, corners kept.

    Every edge is subdivided on its own and starts at its own corner, so the
    grouser keeps its square shoulders however coarse this gets.
    """
    loop = np.vstack([poly, poly[:1]])
    seg = np.linalg.norm(np.diff(loop, axis=0), axis=1)
    total = float(seg.sum())
    out = []
    for i, ln in enumerate(seg):
        k = max(1, int(round(target * ln / total)))
        t = np.linspace(0.0, 1.0, k, endpoint=False)[:, None]
        out.append(poly[i] + (loop[i + 1] - poly[i]) * t)
    return np.vstack(out)


def shoe_profile(pitch, cfg):
    """One shoe seen from the side, as (arc length, offset from the path).

    The offset is positive *inward*: `rounded_loop` runs anticlockwise, so the
    normal `_resample` builds points at the loop's centre and the tread is the
    negative side of the band.
    """
    half = 0.5 * cfg["gap"] * pitch
    t2 = 0.5 * cfg["plate"] * pitch
    gh = cfg["grouser"] * pitch
    g0 = 0.5 * (pitch - cfg["grouser_len"] * pitch)
    g1 = 0.5 * (pitch + cfg["grouser_len"] * pitch)
    poly = np.array([
        (half, t2), (pitch - half, t2),          # the back of the shoe
        (pitch - half, -t2),                     # trailing edge
        (g1, -t2), (g1, -t2 - gh),               # the grouser bar
        (g0, -t2 - gh), (g0, -t2),
        (half, -t2),                             # tread face to the leading edge
    ])
    return _densify(poly, int(cfg["profile_points"]))


def _extrude(profile, width, across):
    """The shoe pushed across the belt: belt-space (s, d, y) and its faces."""
    p = len(profile)
    ys = np.linspace(-0.5 * width, 0.5 * width, int(across) + 1)
    s = np.tile(profile[:, 0], len(ys))
    d = np.tile(profile[:, 1], len(ys))
    y = np.repeat(ys, p)

    faces = [list(range(p))[::-1], list(range((len(ys) - 1) * p, len(ys) * p))]
    for j in range(len(ys) - 1):
        for i in range(p):
            k = (i + 1) % p
            faces.append([j * p + i, j * p + k, (j + 1) * p + k, (j + 1) * p + i])
    return s, d, y, faces


def _loop_uv(faces, u, v):
    """Per-loop UVs, in the order Blender stores polygon corners."""
    idx = np.concatenate([np.asarray(f, np.int64) for f in faces])
    return np.stack([u[idx], v[idx]], 1)


def _points_floor(pitch, height, cfg):
    """The fewest outline points that leave no bearing bin empty.

    `Belt._trace` bins the belt by bearing about its own centre and refuses a
    loop with an empty bin. Which bin starves is not the obvious one: the ends
    are far from the centre and so a bin there spans plenty of arc, while in the
    *middle of a straight run* the radius is only half the height and one bin
    spans `(height / 2) * 2*pi / angular_bins` of belt. That is where a vertex
    has to land, and it is why this is a floor computed from the pitch and the
    height rather than a number sitting in CONFIG.

    Only about 62% of an outline's points differ in `s` - the rest stand on the
    vertical edges, sharing one - hence the divisor.
    """
    span = 0.5 * height * 2.0 * np.pi / track_cycle.CONFIG["angular_bins"]
    return int(np.ceil(cfg["s_margin"] * pitch / span / 0.62))


def _check_gap(pitch, height, cfg):
    """Refuse a gap between shoes wider than one bearing bin, and say why.

    Subdividing does not fix an empty bearing bin, because an open gap has no
    geometry in it to subdivide: 47 wedges of nothing, widest where the loop is
    *closest* to its centre - the middle of the straight runs, radius half the
    height - and that is where a bin spans the least belt. Measured on HM: a gap
    of a tenth of a pitch left 15 bins empty at 48 outline points and the same
    15 at 7904.

    Not clamped, raised. A gap quietly shaved to a hairline is a gap that is not
    there, reported as one that is.
    """
    limit = 0.5 * height * 2.0 * np.pi / track_cycle.CONFIG["angular_bins"]
    if cfg["gap"] * pitch > limit:
        raise RuntimeError(
            "a gap of %.4f of a pitch is %.5f of belt, and anything over "
            "%.5f - one bearing bin at the middle of the straight run - leaves "
            "`Belt._trace` a bin with nothing in it. Let the grousers separate "
            "the shoes and set `gap` to 0."
            % (cfg["gap"], cfg["gap"] * pitch, limit))


def _empty_bearings(co):
    """Bearing bins of `Belt._trace` that this geometry would leave empty."""
    plane = co[:, [0, 2]] - 0.5 * (co.min(0) + co.max(0))[[0, 2]]
    nb = track_cycle.CONFIG["angular_bins"]
    th = np.arctan2(plane[:, 1], plane[:, 0])
    b = np.clip(((th + np.pi) / (2 * np.pi) * nb).astype(int), 0, nb - 1)
    return int((np.bincount(b, minlength=nb) == 0).sum())


def build_belt(path, pitch, height, width, links, cfg, tries=5):
    """The belt itself: one shoe transported onto the loop `links` times.

    The outline is refined until the belt is traceable, rather than trusted to
    be: the floor is derived from the geometry, and then checked against the
    very test `Belt` will apply. Costs milliseconds and removes the one way this
    fails - a belt that looks right in the viewport and cannot be measured.
    """
    _check_gap(pitch, height, cfg)
    points = max(int(cfg["profile_points"] or 0),
                 _points_floor(pitch, height, cfg))
    for _ in range(int(tries)):
        cur = dict(cfg, profile_points=points)
        profile = shoe_profile(pitch, cur)
        s0, d0, y0, faces0 = _extrude(profile, width, cfg["across_segments"])
        nv = len(s0)
        co = np.concatenate([path.transport(s0 + k * pitch, d0, y0)
                             for k in range(links)])
        empty = _empty_bearings(co)
        if empty == 0:
            break
        points *= 2
    faces = [[i + k * nv for i in f] for k in range(links) for f in faces0]

    # u round one shoe, v across the belt - enough for a texture to be laid on
    # later without unwrapping forty thousand vertices by hand
    u = np.tile(s0 / pitch, links)
    v = np.tile(y0 / width + 0.5, links)
    return {"co": co, "faces": faces, "uv": _loop_uv(faces, u, v),
            "band": float(np.ptp(d0)), "profile_points": points,
            "empty_bearing_bins": empty}


def build_rolls(length, height, width, cfg):
    """Road wheels, an idler and a sprocket, as discs inside the loop."""
    n = int(cfg["wheels"])
    rw = cfg["wheel_width_frac"] * width
    r_end = cfg["end_wheel_frac"] * 0.5 * height
    r_road = cfg["road_wheel_frac"] * 0.5 * height
    ex = 0.5 * (length - height)                 # where the loop's ends curve
    centres = [(-ex, 0.0, r_end), (ex, 0.0, r_end)]
    if n > 0:
        span = max(ex - r_road, 0.0)
        for x in np.linspace(-span, span, n):
            centres.append((float(x), -(0.5 * height - r_road), r_road))

    seg = int(cfg["wheel_segments"])
    th = np.linspace(0.0, 2.0 * np.pi, seg, endpoint=False)
    co, faces, base = [], [], 0
    for cx, cz, r in centres:
        ring = np.stack([cx + r * np.cos(th), np.zeros(seg),
                         cz + r * np.sin(th)], 1)
        a, b = ring.copy(), ring.copy()
        a[:, 1], b[:, 1] = -0.5 * rw, 0.5 * rw
        co.append(np.vstack([a, b]))
        faces.append(list(range(base, base + seg))[::-1])
        faces.append(list(range(base + seg, base + 2 * seg)))
        for i in range(seg):
            k = (i + 1) % seg
            faces.append([base + i, base + k, base + seg + k, base + seg + i])
        base += 2 * seg

    tiled = np.tile(th, len(centres) * 2)
    return np.vstack(co), faces, _loop_uv(faces, 0.5 + 0.5 * np.cos(tiled),
                                          0.5 + 0.5 * np.sin(tiled))


# --- putting it in the scene ----------------------------------------------

def _replace(name, me):
    """A fresh object under `name`, taking the old one's place if there is one."""
    import bpy
    old = bpy.data.objects.get(name)
    if old is not None:
        data = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if data is not None and data.users == 0:
            bpy.data.meshes.remove(data)
    return bpy.data.objects.new(name, me)


def _material(name, spec):
    import bpy
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = spec["base_color"]
        bsdf.inputs["Metallic"].default_value = spec["metallic"]
        bsdf.inputs["Roughness"].default_value = spec["roughness"]
    return mat


def _mesh_from(name, co, faces, mat, uv):
    import bpy
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in co], [], faces)
    me.validate()
    if mat is not None:
        me.materials.append(mat)
    me.uv_layers.new(name="UVMap").data.foreach_set(
        "uv", uv.astype(np.float32).ravel())
    # flat all through, like every other mesh in these scenes
    me.polygons.foreach_set("use_smooth", np.zeros(len(me.polygons), bool))
    me.update()
    return me


def _collection(cfg):
    """The collection the hull lives in, so the belts land beside it."""
    import bpy
    hull = bpy.data.objects.get(cfg["hull"])
    if hull is not None and hull.users_collection:
        return hull.users_collection[0]
    return bpy.context.scene.collection


def _place(ob, root, coll):
    import bpy
    if ob.name not in coll.objects:
        coll.objects.link(ob)
    ob.parent = root
    if root is not None:
        ob.matrix_parent_inverse.identity()
    ob.location = (0.0, 0.0, 0.0)
    ob.rotation_mode = "QUATERNION"
    ob.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
    ob.scale = (1.0, 1.0, 1.0)


def run(cfg=None):
    """Build both belts, their wheels and their roots. Returns the report."""
    import bpy
    from mathutils import Quaternion

    cfg = dict(CONFIG, **(cfg or {}))
    got = sizes(cfg)
    length, height, width = got["length"], got["height"], got["width"]
    links = int(cfg["links"])

    path = _Path(length, height, cfg["corner"], cfg["path_nodes"], cfg["samples"])
    pitch = path.perimeter / links

    belt_mesh = build_belt(path, pitch, height, width, links, cfg)
    co, faces, uv = belt_mesh["co"], belt_mesh["faces"], belt_mesh["uv"]
    roll = build_rolls(length, height, width, cfg) if cfg["rolls"] else None

    # the tread's lowest point, so that `ground_z` names the ground and not the
    # path the tread hangs off
    z_centre = got["ground_z"] - float(co[:, 2].min())

    coll = _collection(cfg)
    report = {"sizes": got, "links": links, "pitch": round(pitch, 6),
              "perimeter": round(path.perimeter, 6),
              "band_thickness": round(belt_mesh["band"], 6),
              "profile_points": belt_mesh["profile_points"],
              "empty_bearing_bins": belt_mesh["empty_bearing_bins"],
              "sides": {}}

    for i, (root_name, belt_name) in enumerate(zip(cfg["roots"], cfg["belts"])):
        sign = -1.0 if i == 0 else 1.0
        mat = _material("Track." + ("L" if i == 0 else "R"), cfg["material"])

        root = bpy.data.objects.get(root_name)
        if root is not None and root.type != "EMPTY":
            bpy.data.objects.remove(root, do_unlink=True)
            root = None
        if root is None:
            root = bpy.data.objects.new(root_name, None)
            root.empty_display_type = "PLAIN_AXES"
            root.empty_display_size = 0.15
        if root.name not in coll.objects:
            coll.objects.link(root)
        root.parent = None                       # see the module docstring
        root.location = (sign * got["x_centre"], got["y_centre"], z_centre)
        root.rotation_mode = "QUATERNION"
        root.rotation_quaternion = Quaternion((0.0, 0.0, 1.0),
                                              math.radians(-90.0))
        root.scale = (1.0, 1.0, 1.0)

        belt = _replace(belt_name, _mesh_from(belt_name, co, faces, mat, uv))
        _place(belt, root, coll)
        side = {"root": root_name, "belt": belt_name,
                "verts": len(belt.data.vertices),
                "polys": len(belt.data.polygons)}

        if roll is not None:
            rname = cfg["rolls"][i]
            rob = _replace(rname, _mesh_from(rname, roll[0], roll[1], mat, roll[2]))
            _place(rob, root, coll)
            side["rolls"] = {"name": rname, "verts": len(rob.data.vertices),
                             "polys": len(rob.data.polygons)}
        report["sides"][root_name] = side

    bpy.context.view_layer.update()
    report["checks"] = verify(cfg, got, links)
    return report


def verify(cfg, got, links):
    """Measure the belts back the way the renderer will, and say what came out.

    None of this is a success report - a wrong azimuth or a wrong axis is
    invisible in every number here, and the picture is the check. But the pitch
    being read off the tread rather than off the vertex density is exactly the
    property a picture cannot show, and it is the quiet failure: a belt with no
    readable relief still animates, 9% out against the ground.
    """
    import bpy
    out = {}
    for root_name, belt_name in zip(cfg["roots"], cfg["belts"]):
        ob = bpy.data.objects.get(belt_name)
        if ob is None:
            out[belt_name] = {"built": False}
            continue
        try:
            b = track_cycle.Belt(ob)
        except RuntimeError as exc:
            # loud rather than raised: the belts are in the scene by now, and a
            # report that names what is wrong with them beats a traceback that
            # leaves the caller guessing which half got built
            out[belt_name] = {"measurable": False, "why": str(exc)}
            continue
        me = ob.data
        n = len(me.vertices)
        co = np.empty(n * 3, np.float32)
        me.vertices.foreach_get("co", co)
        m = np.array(ob.matrix_world)
        w = co.reshape(n, 3).astype(np.float64) @ m[:3, :3].T + m[:3, 3]
        root = bpy.data.objects.get(root_name)
        out[belt_name] = {
            "measurable": True,
            "links_asked": links,
            "links_measured": int(b.links),
            "links_match": int(b.links) == links,
            "links_by_vertex_density": int(b.density_links),
            "pitch_from": b.pitch_from,
            "tread_reads_off_the_run": b.pitch_from.startswith("tread"),
            # a laid-out belt closes by construction, so this asks a question
            # about an irregular band that does not arise here. Near 1.0 anyway.
            "tread_repeat": round(float(b.tread_repeat), 3),
            "pitch_world": round(b.world_pitch(), 6),
            "worst_foot_gap": round(b.foot_gap, 6),
            "world_min": [round(float(v), 5) for v in w.min(0)],
            "world_max": [round(float(v), 5) for v in w.max(0)],
            "stands_on_ground_z": abs(float(w[:, 2].min()) - got["ground_z"]) < 1e-4,
            "root_is_root_level": root is not None and root.parent is None,
        }
    return out


FIT = {
    "hull": CONFIG["hull"],
    "roots": CONFIG["roots"],
    "belts": CONFIG["belts"],

    # which of the three corrections to actually make. Dropping one is how you
    # keep a placement you put in by hand while letting the others be measured:
    # "xz" leaves the fore-aft alone, "z" only sets the tank down on its tracks.
    "axes": "xyz",
    # measure and report, move nothing. Worth a first pass every time: the
    # deltas below say what is wrong before anything in the scene is different.
    "apply": True,

    # the same rule `sizes()` places a built belt by, so a copied-in belt and a
    # generated one end up in the same place. Overrides in world units.
    "x_centre": None, "y_centre": None, "ground_z": None,
    "overlap_frac": CONFIG["overlap_frac"],
    "drop_frac": CONFIG["drop_frac"],
    "low_band_frac": CONFIG["low_band_frac"],
    "body_percentile": CONFIG["body_percentile"],
}


def _world_box(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, np.float32)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world)
    w = co.reshape(n, 3).astype(np.float64) @ m[:3, :3].T + m[:3, 3]
    return w.min(0), w.max(0)


def fit(cfg=None):
    """Sit a pair of belts that already exist onto the hull they belong to.

    The other half of `run()`, and the one a belt copied in from another scene
    needs: `track_shape.py` fits the *loop* - length, height, corner, all of it
    in the belt's own xz - and a loop has no opinion about which side of which
    tank it is on. So a belt lifted out of LT_PARTS arrives the right shape and
    in LT_PARTS' place, buried inside a narrower hull.

    Three corrections, each a translation of the layer root and nothing else:
    the scale and the rotation are what the bench and the source scene decided,
    and this does not touch them.

      x  the belt's centreline out to where the hull's *lower* side is, plus the
         part of its own width that is meant to tuck under it. The lower side
         and not the widest point, because the widest point is the fender - and
         on a hull with no fender at all, like HM, the two are the same number
         and the rule does not have to know which kind it is looking at.
      y  the belt centred on the hull, fore and aft.
      z  the tread set down on `ground_z`, which defaults to a belly clearance
         below the hull's underside. Nothing else in the pipeline decides this:
         `hex_base` and `parts_render.ground_tile` lay the tile at the lowest
         point of hull *plus* belts, so where the belts sit is where the ground
         goes, and a belt left 30 thousandths high floats the whole tank.

    Both sides are put at the same |x| by construction rather than fitted apart
    and hoped to agree. The roots' own origins are not symmetric - the mesh
    carries an offset of its own, which is why LT_PARTS' two roots sit at -0.171
    and +0.357 - so everything here is measured off the belt's world box and
    never off the root's location.

    `apply: False` measures and moves nothing, which is the pass worth running
    first.
    """
    import bpy
    cfg = dict(FIT, **(cfg or {}))
    hull = bpy.data.objects.get(cfg["hull"])
    if hull is None or hull.type != "MESH":
        raise RuntimeError("no hull mesh %r to fit against" % (cfg["hull"],))

    w, lo, hi = _hull_box(hull, cfg["body_percentile"])
    box = hi - lo
    side = _low_half_width(w, lo, hi, cfg["low_band_frac"], cfg["body_percentile"])

    belts = []
    for root_name, belt_name in zip(cfg["roots"], cfg["belts"]):
        root = bpy.data.objects.get(root_name)
        belt = bpy.data.objects.get(belt_name)
        if root is None or belt is None or belt.type != "MESH":
            raise RuntimeError("expected %r with %r under it" % (root_name, belt_name))
        if root.parent is not None:
            raise RuntimeError(
                "%r is a child of %r. A layer root under another layer root gets "
                "its world matrix written and restored mid-spin by the renderer "
                "and does not come back - see docs/tank-scene.md."
                % (root_name, root.parent.name))
        if belt.parent is not root:
            raise RuntimeError("%r is parented to %r, not to %r"
                               % (belt_name, belt.parent and belt.parent.name,
                                  root_name))
        belts.append((root, belt) + _world_box(belt))

    # one width for both sides, so the two answers cannot disagree
    width = float(np.mean([bhi[0] - blo[0] for _, _, blo, bhi in belts]))
    x_centre = cfg["x_centre"] if cfg["x_centre"] is not None \
        else side + width * (0.5 - cfg["overlap_frac"])
    y_centre = cfg["y_centre"] if cfg["y_centre"] is not None \
        else 0.5 * (lo[1] + hi[1])
    ground = cfg["ground_z"] if cfg["ground_z"] is not None \
        else lo[2] - box[2] * cfg["drop_frac"]

    axes = str(cfg["axes"]).lower()
    report = {
        "hull_box": [round(float(v), 5) for v in box],
        "hull_lower_half_width": round(side, 5),
        "belt_width": round(width, 5),
        "target": {"x_centre": round(float(x_centre), 5),
                   "y_centre": round(float(y_centre), 5),
                   "ground_z": round(float(ground), 5)},
        "axes": axes, "applied": bool(cfg["apply"]), "sides": {},
    }

    for root, belt, blo, bhi in belts:
        was = 0.5 * (blo + bhi)
        sign = 1.0 if was[0] >= 0.0 else -1.0
        want = np.array([sign * x_centre, y_centre,
                         was[2] + (ground - blo[2])])
        delta = np.where([a in axes for a in "xyz"], want - was, 0.0)

        row = {"root": root.name, "belt": belt.name,
               "side": "right" if sign > 0 else "left",
               "was_centre": [round(float(v), 5) for v in was],
               "was_min": [round(float(v), 5) for v in blo],
               "was_max": [round(float(v), 5) for v in bhi],
               "delta": [round(float(v), 5) for v in delta]}

        if cfg["apply"]:
            root.location = tuple(np.array(root.location) + delta)
        report["sides"][root.name] = row

    bpy.context.view_layer.update()

    if cfg["apply"]:
        for root, belt, _, _ in belts:
            blo, bhi = _world_box(belt)
            row = report["sides"][root.name]
            row["now_min"] = [round(float(v), 5) for v in blo]
            row["now_max"] = [round(float(v), 5) for v in bhi]
            # the two numbers worth looking at on the picture afterwards
            row["outboard_of_hull"] = round(
                float(max(abs(blo[0]), abs(bhi[0])) - side), 5)
            row["below_hull_underside"] = round(float(lo[2] - blo[2]), 5)
            row["stands_on_ground_z"] = bool(abs(float(blo[2]) - ground) < 1e-4)

    # the renderer makes these afresh on every run, so one carried in from
    # another scene is a second belt drawn inside the first - the exact failure
    # `docs/tank-scene.md` records for a layer that came without its `exclude`
    stale = sorted(o.name for o in bpy.context.scene.objects
                   if o.name.endswith(".Rebuilt"))
    if stale:
        report["stale_rebuilt"] = stale
    return report


if __name__ == "__main__":
    import json
    print(json.dumps(run(CONFIG), indent=1))
