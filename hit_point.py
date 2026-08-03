"""
Where the armour plates are, and which way each one faces.

Run from Blender's Text Editor (Alt+P), or import and call `set_hits`.

Needs nothing separated by hand, and that is the point
------------------------------------------------------
`Barrel` and `Engine` are hand splits because a bore and a louvred port are
small, ambiguous pieces buried in a soup of 5-vertex shells. An armour plate is
neither: it is the largest flat thing on its side of the tank, and a fan of
rays fired at it from outside finds it without caring how the shells connect.

So this measures rather than reads a selection. Four directions, a square fan
each, and the plate falls out of the normals. `Plate.Front` and friends stay
supported as an override for a hull where no plate stands out, but on MT none
is needed.

Measuring beats marking here, which is the opposite of the muzzle
-----------------------------------------------------------------
A hand-picked `Front` would be one patch with one centroid. The front of a tank
is not one patch: MT's is three facets - a lower plate rolled under at -51
degrees, a near-vertical band, and a glacis laid back at +47. Marked as one
object, the centroid lands between them, in the narrowest part of the whole
front. Measured, the three separate and the glacis wins on area.

And the +47 is not decoration. It is the number that later decides how much a
burst on that plate is foreshortened, and no selection can express it - it
comes out of the normals or it does not come out at all.

The plate is a mode of the normals, not a band of rows
-------------------------------------------------------
The first version stacked rows of rays up the side and joined adjacent rows
whose median normals agreed. It read plausibly and was not stable: at 7 rows
MT's front came out as two facets and a glacis, at 9 rows the elevations went
-55, -53, -19, -4, +23, +38, +48, +16, +14, and the rear picked the engine deck
instead of the rear plate. The fault is in the instrument - a row of 11 rays
straddles two facets and its median reports an angle that exists nowhere on the
tank, and where the straddle happens depends on where the rows land.

Firing a square fan and histogramming the hit normals asks the question
directly. MT's front comes out unmistakably trimodal - 142 rays at -51, 153
near vertical, 264 on the glacis - and the answer does not move when the fan
does. The plate is the biggest mode; a couple of mean-shift passes then let it
settle onto its own hits rather than onto the bin it was seeded from.

Two filters, both derived rather than tuned:

  facing  A plate shows the camera `cos(its elevation - the camera's)` of
          itself at the heading that suits it best. Below `min_facing` of its
          own area at its *best* heading it is never worth drawing on. This is
          what throws away the belly-ward facets - MT's front and rear both
          roll under at about -50 degrees, and those are legal armour that no
          isometric camera ever sees.

  plane   Hits are kept only near the plate's own plane. Two facets can share a
          normal and sit a hand apart - the glacis and the deck behind it - and
          nothing but distance tells them apart.

Left and right are the crew's
-----------------------------
`right = forward x up`, so with the import convention (nose along -Y) the
crew's right is -X. Getting this backwards costs nothing in geometry and
everything in a hit report that names the wrong side, so it is derived from
`front_dir` rather than written down.
"""

import importlib.util
import math
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "hull": "Hull",
    "turret": "Turret",
    # The objects the hull and turret *layers* render. What the rays must see is
    # the whole hull, and on a parts-built scene the hull mesh has no children -
    # the exhaust louvre is a sibling under `Hull.World`, so firing at the mesh
    # alone would leave the rear plate with a hole in it exactly where the louvre
    # was cut out. None -> the meshes, which on a single-mesh scene already carry
    # the cut pieces as children.
    "hull_root": None,
    "turret_root": None,
    # objects to stamp the result on
    "stamp_on": ["Hull"],
    # Import convention, same value and same standing as everywhere else: a
    # declaration, not a measurement. Every face direction is derived from it.
    "front_dir": 270.0,
    # A mesh named `Plate.Front` (or `.Rear`, `.Left`, `.Right`) overrides the
    # measurement for that face. Kept for a hull where no plate stands out of
    # the histogram; unused on the three current tanks.
    "plate_prefix": "Plate",

    # --- the ray fan --------------------------------------------------------
    # Rays per side of a square fan: 27 is 729 a face, about a second each. The
    # count only has to resolve the smallest facet worth hitting, and it is the
    # *shape* of the normal histogram that decides, not any single ray.
    "grid": 27,
    # Keep the fan off the silhouette edge, where rays graze fenders, tow hooks
    # and the track run rather than the plate behind them.
    "inset": 0.10,

    # --- finding the plate --------------------------------------------------
    # Seeding bin for the normal histogram, in degrees of both elevation and
    # bearing. Wide enough that one facet lands in one bin, narrow enough that
    # two facets do not share one.
    "bin_deg": 12.0,
    # Passes of mean-shift after the seed, so the plate settles onto its own
    # hits instead of onto the bin it was found in.
    "mean_shift": 3,
    # A hit belongs to the plate while its normal agrees this well.
    "cluster_cos": 0.90,
    # ...and while it lies this close to the plate's plane, as a fraction of
    # the hull's length. Two parallel facets a hand apart are two plates.
    "plane_tol": 0.05,
    # The camera this atlas is rendered through. A plate presents
    # cos(plate elevation - this) of itself at its best heading. Kept as the
    # same pair of numbers `sprite_atlas.CONFIG` uses, and `project_plates`
    # builds the real camera out of them rather than re-deriving a basis.
    "camera_elevation": 30.0,
    "elevation": 30.0,
    "azimuth": 0.0,
    "min_facing": 0.5,
    # Half-extents are read off a percentile for the same reason the port's
    # radius is: one stray hit on a bracket would set a maximum and nothing
    # would notice.
    "extent_percentile": 95.0,
    # A plate narrower than this fraction of the hull's length is not a plate.
    "min_extent": 0.06,
    # ...and one accounting for less than this share of what the camera can see
    # on its side is not the face, it is a detail on the face.
    "min_share": 0.20,

    # --- the check render ---------------------------------------------------
    # The whole tank: with the turret off, the open ring lets the far side's
    # spike show through the deck. See `check`.
    "root": "world",
    "check_steps": 12,
    "check_tile": 256,
    "check_dir": os.path.join(REPO, "out", "plates"),
    "marker_length": 0.11,      # of the hull's length
    "marker_radius": 0.030,     # of the hull's length
}

# Distinct enough to tell apart in a 256px frame, and warm/cool paired so
# opposite faces read as opposites.
MARKER_COLOURS = {
    "front": (1.00, 0.12, 0.06),
    "rear": (0.10, 0.35, 1.00),
    "left": (0.15, 1.00, 0.20),
    "right": (1.00, 0.85, 0.05),
}

FACES = ("front", "rear", "left", "right")

UP = Vector((0.0, 0.0, 1.0))


def _sibling(name):
    """Load a module that lives next to this one.

    Blender's Text Editor does not put the repo on `sys.path` and the pipeline
    loads every module by explicit path, so a plain `import` would work in one
    context and fail in the other.
    """
    here = (os.path.dirname(os.path.abspath(__file__))
            if "__file__" in globals() else REPO)
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(here, "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# directions
# ---------------------------------------------------------------------------

def face_dirs(front_dir):
    """Outward direction of each face, from the import convention.

    `right = forward x up` in a right-handed Z-up world: facing east, the right
    hand points south. With the nose along -Y that puts the crew's right at -X.
    """
    a = math.radians(float(front_dir))
    forward = Vector((math.cos(a), math.sin(a), 0.0))
    right = forward.cross(UP)
    return {"front": forward, "rear": -forward, "left": -right, "right": right}


def _elevation(v):
    return math.degrees(math.asin(max(-1.0, min(1.0, float(v.z)))))


def _facing(elevation, cfg):
    """How much of itself a plate at this elevation shows the camera, at the
    heading that suits it best."""
    return math.cos(math.radians(elevation - cfg["camera_elevation"]))


# ---------------------------------------------------------------------------
# the ray fan
# ---------------------------------------------------------------------------

def _targets(hull):
    return [o for o in [hull] + list(hull.children_recursive) if o.type == "MESH"]


def _bounds(objects):
    lo = Vector((1e18, 1e18, 1e18))
    hi = Vector((-1e18, -1e18, -1e18))
    for ob in objects:
        for corner in ob.bound_box:
            w = ob.matrix_world @ Vector(corner)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
    return lo, hi


def _cast(targets, dg, origin, direction, distance):
    """Nearest hit over the whole hull group, in world space."""
    best = None
    for ob in targets:
        inv = ob.matrix_world.inverted()
        ok, loc, nor, _idx = ob.ray_cast(inv @ origin,
                                         (inv.to_3x3() @ direction).normalized(),
                                         distance=distance, depsgraph=dg)
        if not ok:
            continue
        world_loc = ob.matrix_world @ loc
        world_nor = (ob.matrix_world.to_3x3().inverted().transposed()
                     @ nor).normalized()
        t = (world_loc - origin).length
        if best is None or t < best[0]:
            best = (t, world_loc, world_nor, ob.name)
    return best


def _fan(targets, dg, lo, hi, out, cfg):
    """A square fan fired at one face.

    (u, UP, out) is orthonormal with `out` horizontal, so a ray's start is just
    its three components added up - no matrix needed.
    """
    u = out.cross(UP).normalized()
    corners = [Vector((x, y, z)) for x in (lo.x, hi.x)
               for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
    span_u = (min(c.dot(u) for c in corners), max(c.dot(u) for c in corners))
    span_v = (lo.z, hi.z)
    reach = (hi - lo).length
    start = max(c.dot(out) for c in corners) + reach

    n = int(cfg["grid"])
    inset, hits = cfg["inset"], []
    for iv in range(n):
        fv = inset + (1.0 - 2.0 * inset) * iv / max(n - 1, 1)
        for iu in range(n):
            fu = inset + (1.0 - 2.0 * inset) * iu / max(n - 1, 1)
            origin = (u * (span_u[0] + fu * (span_u[1] - span_u[0]))
                      + UP * (span_v[0] + fv * (span_v[1] - span_v[0]))
                      + out * start)
            hit = _cast(targets, dg, origin, -out, reach * 3.0)
            if hit is not None:
                hits.append({"point": hit[1], "normal": hit[2], "object": hit[3]})
    return u, hits


def _mode(hits, hull_length, cfg):
    """The biggest facet among these hits: seed from the fullest normal bin,
    then let mean-shift settle it onto its own hits."""
    if not hits:
        return None, [], 0.0
    bins = {}
    step = float(cfg["bin_deg"])
    for h in hits:
        n = h["normal"]
        key = (int(math.floor(_elevation(n) / step)),
               int(math.floor(math.degrees(math.atan2(n.y, n.x)) / step)))
        bins.setdefault(key, []).append(h)
    seed = Vector((0.0, 0.0, 0.0))
    for h in max(bins.values(), key=len):
        seed += h["normal"]
    if seed.length < 1e-9:
        return None, [], 0.0
    seed.normalize()

    kept = []
    for _ in range(max(1, int(cfg["mean_shift"]))):
        kept = [h for h in hits if h["normal"].dot(seed) >= cfg["cluster_cos"]]
        if not kept:
            return None, [], 0.0
        moved = Vector((0.0, 0.0, 0.0))
        for h in kept:
            moved += h["normal"]
        if moved.length < 1e-9:
            return None, [], 0.0
        seed = moved.normalized()

    # two facets can share a normal and sit a hand apart - the glacis and the
    # deck behind it. Only distance from the plane tells them apart.
    depth = np.array([float((h["point"]).dot(seed)) for h in kept])
    middle = float(np.median(depth))
    tol = cfg["plane_tol"] * hull_length
    kept = [h for h, d in zip(kept, depth) if abs(d - middle) <= tol]
    if not kept:
        return None, [], 0.0
    settled = Vector((0.0, 0.0, 0.0))
    for h in kept:
        settled += h["normal"]
    return settled.normalized(), kept, len(kept) / float(len(hits))


def _measure_face(targets, dg, lo, hi, name, out, hull_length, cfg):
    u, hits = _fan(targets, dg, lo, hi, out, cfg)
    step = float(cfg["bin_deg"])
    seen = [h for h in hits if _facing(_elevation(h["normal"]), cfg) >= cfg["min_facing"]]
    # what the fan found, before anything was chosen - the histogram is the
    # evidence for the choice and worth keeping when it goes wrong
    hist = {}
    for h in hits:
        key = int(math.floor(_elevation(h["normal"]) / 15.0)) * 15
        hist[key] = hist.get(key, 0) + 1
    trace = {"face": name, "rays": int(cfg["grid"]) ** 2, "hits": len(hits),
             "camera_can_see": len(seen),
             "elevation_hist_15deg": {"%+d" % k: hist[k] for k in sorted(hist)}}

    normal, kept, share = _mode(seen, hull_length, cfg)
    if normal is None:
        return None, trace

    pts = np.array([[h["point"].x, h["point"].y, h["point"].z] for h in kept])
    point = Vector(np.median(pts, axis=0).tolist())
    # in-plate axes: the horizontal one first, straightened against the normal
    tangent = (u - normal * normal.dot(u))
    tangent = tangent.normalized() if tangent.length > 1e-9 else u
    up_slope = normal.cross(tangent).normalized()
    rel = pts - np.array([point.x, point.y, point.z])
    p = cfg["extent_percentile"]
    extent = (float(np.percentile(np.abs(rel @ np.array(tangent[:])), p)),
              float(np.percentile(np.abs(rel @ np.array(up_slope[:])), p)))
    # the fan is inset, so the point sits in the middle of what was sampled
    # rather than of the plate, and both extents are conservative - an impact
    # scattered inside them stays on the metal
    plate = {
        "face": name, "point": point, "normal": normal,
        "tangent": tangent, "up_slope": up_slope, "extent": extent,
        "hits": len(kept), "share": share,
        "faceon": float(normal.dot(out)),
        "elevation": _elevation(normal),
        "objects": sorted({h["object"] for h in kept}),
        "source": "rays",
    }
    trace["chosen"] = {"elevation": round(plate["elevation"], 1),
                       "share": round(share, 3), "hits": len(kept)}
    return plate, trace


def _override(scene, cfg, name, hull_mid, out):
    """A hand-marked `Plate.<face>`, if one exists."""
    want = ("%s.%s" % (cfg["plate_prefix"], name)).strip().lower()
    ob = next((o for o in sorted(scene.objects, key=lambda o: o.name)
               if o.type == "MESH" and o.name.strip().lower().startswith(want)),
              None)
    if ob is None:
        return None
    mp = _sibling("muzzle_point")
    pts = mp.world_vertices(ob)
    centre, axis, _spread, _shape, _margin = mp.bore_axis(pts)
    normal = Vector([float(v) for v in axis])
    point = Vector([float(v) for v in centre])
    # a principal direction has no sign; outward is away from the hull's middle
    if normal.dot(point - hull_mid) < 0.0:
        normal = -normal
    tangent = (out.cross(UP) - normal * normal.dot(out.cross(UP)))
    tangent = tangent.normalized() if tangent.length > 1e-9 else out.cross(UP)
    up_slope = normal.cross(tangent).normalized()
    rel = pts - np.array([point.x, point.y, point.z])
    p = cfg["extent_percentile"]
    return {"face": name, "point": point, "normal": normal,
            "tangent": tangent, "up_slope": up_slope,
            "extent": (float(np.percentile(np.abs(rel @ np.array(tangent[:])), p)),
                       float(np.percentile(np.abs(rel @ np.array(up_slope[:])), p))),
            "hits": int(len(pts)), "share": 1.0,
            "faceon": float(normal.dot(out)), "elevation": _elevation(normal),
            "objects": [ob.name], "source": "object %s" % ob.name}


# ---------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------

def set_hits(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    dg = bpy.context.evaluated_depsgraph_get()

    tp = _sibling("tank_parts")
    hull = tp.mesh(cfg["hull"], scene, required=False)
    if hull is None:
        raise RuntimeError("no object named %r" % cfg["hull"])
    targets = _targets(scene.objects[cfg["hull_root"]] if cfg["hull_root"]
                       else hull)
    lo, hi = _bounds(targets)
    mid = (lo + hi) / 2.0
    # the larger horizontal extent, which is the fore-aft one on every tank -
    # the same length the plume is sized in, and for the same reason
    hull_length = float(max(hi.x - lo.x, hi.y - lo.y))

    dirs = face_dirs(cfg["front_dir"])
    plates, traces, warnings = [], [], []
    for name in FACES:
        out = dirs[name]
        plate = _override(scene, cfg, name, mid, out)
        if plate is None:
            plate, trace = _measure_face(targets, dg, lo, hi, name, out,
                                         hull_length, cfg)
            traces.append(trace)
        if plate is None:
            warnings.append(
                "no plate on the %s: every facet the camera can see was too "
                "small or too broken to be one surface. Mark it by hand as "
                "%s.%s if this face needs to take hits."
                % (name, cfg["plate_prefix"], name.capitalize()))
            continue
        if min(plate["extent"]) < cfg["min_extent"] * hull_length:
            warnings.append(
                "the %s plate is only %.0f x %.0f mm of hull - that is a strip, "
                "not a face; impacts scattered on it will look pinned"
                % (name, 1000.0 * plate["extent"][0], 1000.0 * plate["extent"][1]))
        if plate["share"] < cfg["min_share"]:
            warnings.append(
                "the %s plate is only %.0f%% of what the camera can see on that "
                "side - that is a detail on the face, not the face"
                % (name, 100.0 * plate["share"]))
        if plate["faceon"] < cfg["min_facing"]:
            warnings.append(
                "the %s plate faces only %.2f of the way outward (%.0f deg of "
                "slope) - it is armour, but a burst on it is mostly edge-on"
                % (name, plate["faceon"], abs(plate["elevation"])))
        plates.append(plate)

    if not plates:
        raise RuntimeError("no plate found on any face of %s" % hull.name)

    stamp = {
        "hit_faces": [p["face"] for p in plates],
        "hit_points": [round(float(v), 6) for p in plates for v in p["point"]],
        "hit_dirs": [round(float(v), 6) for p in plates for v in p["normal"]],
        "hit_tangents": [round(float(v), 6) for p in plates for v in p["tangent"]],
        "hit_extents": [round(float(v), 6) for p in plates for v in p["extent"]],
        "hit_scale": round(hull_length, 6),
        "hit_from": ", ".join("%s: %s" % (p["face"], p["source"]) for p in plates),
    }
    for name in cfg["stamp_on"]:
        ob = tp.mesh(name, scene, required=False)
        if ob is None:
            continue
        for key, value in stamp.items():
            ob[key] = value

    detail = [{
        "face": p["face"],
        "point": [round(float(v), 5) for v in p["point"]],
        "normal": [round(float(v), 4) for v in p["normal"]],
        "tangent": [round(float(v), 4) for v in p["tangent"]],
        "extent": [round(v, 5) for v in p["extent"]],
        "faceon": round(p["faceon"], 3),
        "elevation": round(p["elevation"], 1),
        "hits": p["hits"], "share": round(p["share"], 3),
        "objects": p["objects"], "source": p["source"],
    } for p in plates]

    report = dict(stamp)
    report.update({"plates": detail, "hull_length": round(hull_length, 5),
                   "trace": traces, "warnings": warnings})
    print("[hits] %d plate(s): %s, %d warnings"
          % (len(detail), ", ".join("%s %.0f deg %.0f%%"
                                    % (d["face"], d["elevation"], 100 * d["share"])
                                    for d in detail), len(warnings)))
    for line in warnings:
        print("[hits]   ! %s" % line)
    return report


def read_plates(hull):
    """The stamped plates as face -> (point, normal, tangent, extent), plus the
    hull's length."""
    for key in ("hit_faces", "hit_points", "hit_dirs", "hit_tangents",
                "hit_extents"):
        if key not in hull.keys():
            raise RuntimeError("no %r on %s - run hit_point.py first"
                               % (key, hull.name))
    faces = [str(v) for v in hull["hit_faces"]]
    pts = np.array([float(v) for v in hull["hit_points"]]).reshape(-1, 3)
    dirs = np.array([float(v) for v in hull["hit_dirs"]]).reshape(-1, 3)
    tans = np.array([float(v) for v in hull["hit_tangents"]]).reshape(-1, 3)
    ext = np.array([float(v) for v in hull["hit_extents"]]).reshape(-1, 2)
    out = {}
    for i, name in enumerate(faces):
        out[name] = (Vector(pts[i].tolist()), Vector(dirs[i].tolist()),
                     Vector(tans[i].tolist()), tuple(float(v) for v in ext[i]))
    return out, float(hull["hit_scale"])


# ---------------------------------------------------------------------------
# the plates, seen through the atlas camera
# ---------------------------------------------------------------------------

def project_plates(origin, units_per_pixel, angles, cfg=None):
    """Every plate as the game sees it: pixels from `origin`, per heading.

    The renderer owns the projection because the renderer owns the camera. The
    harness reading numbers out of the atlas JSON is the same arrangement that
    keeps frame directions honest (`"side @ 270 deg"` rather than a hard-coded
    `front_dir`): re-render and the table is regenerated with it.

    Offsets are from `origin` rather than from the frame's anchor, because the
    burst layer is built on `origin` and the anchor is only where its frame
    happens to sit. See `hit_burst.origin_of`.

    Per heading, per face:
        offset   pixels from `origin` to the plate, screen y downward
        tangent  pixels for one half-extent along the plate's horizontal axis
        slope    pixels for one half-extent up the plate's slope
        normal   pixels per world unit along the outward normal, *unnormalised* -
                 its length is what survived the projection, which is exactly
                 how much the plate is foreshortened. Normalise it and a burst
                 on a plate turned away from the camera grows to full size.
        facing   +1 straight at the camera, -1 straight away. The sign alone
                 decides whether the burst is drawn in front of the tank or
                 behind it; measured against the hull, that agreed on all
                 sixteen heading-face pairs tested.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    sa = _sibling("sprite_atlas")
    hull = _sibling("tank_parts").mesh(cfg["hull"])
    plates, _scale = read_plates(hull)
    cam = sa.camera_matrix(Vector((0.0, 0.0, 0.0)), cfg["azimuth"],
                           cfg["elevation"], 1.0)
    right, up, toward = (cam.col[i].to_3d().normalized() for i in range(3))
    origin = Vector([float(v) for v in origin])
    upp = float(units_per_pixel)

    def to_px(v):
        return [round(float(v.dot(right)) / upp, 4),
                round(-float(v.dot(up)) / upp, 4)]

    out = {}
    for name, (point, normal, tangent, extent) in plates.items():
        rows = []
        for angle in angles:
            spin = Matrix.Rotation(math.radians(float(angle)), 3, "Z")
            n = spin @ normal
            rows.append({
                "offset": to_px(spin @ (point - origin)),
                "tangent": to_px(spin @ (tangent * extent[0])),
                "slope": to_px(spin @ (normal.cross(tangent) * extent[1])),
                "normal": to_px(n),
                "facing": round(float(n.dot(toward)), 4),
            })
        # Where the plate looks, relative to where the tank looks. This is the
        # one number the game needs that is not a pixel: given a shooter's
        # bearing it picks the plate, and it has to be an offset from the hull's
        # heading rather than a world angle because the hull turns.
        bearing = (math.degrees(math.atan2(normal.y, normal.x))
                   - float(cfg["front_dir"]))
        out[name] = {"bearing": round((bearing + 180.0) % 360.0 - 180.0, 3),
                     "frames": rows}
    return out


# ---------------------------------------------------------------------------
# looking at it
# ---------------------------------------------------------------------------

def _emissive(name, colour):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()
    out = tree.nodes.new("ShaderNodeOutputMaterial")
    emit = tree.nodes.new("ShaderNodeEmission")
    emit.inputs["Color"].default_value = tuple(colour) + (1.0,)
    emit.inputs["Strength"].default_value = 3.0
    tree.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    return mat


def _spike(name, point, normal, length, radius, colour, parent, segments=14):
    """A cone standing on the plate, apex pointing out along its normal."""
    verts = [(0.0, 0.0, length)]
    for i in range(segments):
        a = 2.0 * math.pi * i / segments
        verts.append((radius * math.cos(a), radius * math.sin(a), 0.0))
    faces = [(0, 1 + i, 1 + (i + 1) % segments) for i in range(segments)]
    faces.append(tuple(range(1, segments + 1)))

    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    ob = bpy.data.objects.new(name, me)
    ob.data.materials.append(_emissive("%s_mat" % name, colour))
    bpy.context.scene.collection.objects.link(ob)
    # recessed a little so the base is inside the metal rather than floating
    ob.matrix_world = (Matrix.Translation(point - normal * (radius * 0.5))
                       @ UP.rotation_difference(normal).to_matrix().to_4x4())
    if parent is not None:
        ob.parent = parent
        ob.matrix_parent_inverse = parent.matrix_world.inverted()
    return ob


def markers(cfg=None, parent=True):
    """A spike on every stamped plate. Temporary - `check` removes them."""
    cfg = dict(CONFIG, **(cfg or {}))
    hull = _sibling("tank_parts").mesh(cfg["hull"])
    plates, scale = read_plates(hull)
    built = []
    for name, (point, normal, _tangent, _extent) in plates.items():
        built.append(_spike("_hit_%s" % name, point, normal,
                            cfg["marker_length"] * scale,
                            cfg["marker_radius"] * scale,
                            MARKER_COLOURS.get(name, (1.0, 1.0, 1.0)),
                            hull if parent else None))
    return built


def drop(built):
    for ob in built:
        me = ob.data
        bpy.data.objects.remove(ob, do_unlink=True)
        if me.users == 0:
            bpy.data.meshes.remove(me)


def check(cfg=None):
    """Render the whole tank with a spike on each plate and look at the picture.

    The only real proof that the four faces are where they are named. No number
    shows a spike planted on the track guard instead of the glacis.

    The whole tank, not just the hull, and that is not tidiness. Rendered
    hull-only, the turret ring stands open and the sightline threads it: the
    *far* side's spike shows through the middle of the deck, which reads as a
    marker planted in the wrong place. With the turret on, it is hidden, as it
    should be.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    atlas = _sibling("sprite_atlas")
    built = markers(cfg)
    try:
        job = dict(atlas.CONFIG)
        job.update({
            "target": cfg["root"],
            "name": "plates",
            "output_dir": cfg["check_dir"],
            "steps": cfg["check_steps"],
            "tile": cfg["check_tile"],
            "keep_frames": False,
            "front_dir": cfg["front_dir"],
        })
        os.makedirs(cfg["check_dir"], exist_ok=True)
        path, meta = atlas.render_atlas(job)
    finally:
        drop(built)
    print("[hits] check written to %s" % path)
    return {"path": path, "grid": meta["grid"], "tile": meta["tile"],
            "units_per_pixel": meta["units_per_pixel"]}


if __name__ == "__main__":
    from pprint import pprint

    pprint(set_hits(CONFIG))
