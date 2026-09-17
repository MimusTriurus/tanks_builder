"""
Build the turret ring as its own part, and seat the turret on it.

Run from Blender's Text Editor (Alt+P) after split_turret.py has produced a
Turret and a Hull.

Why build it rather than find it: these generated meshes have no ring. Measured
on one model, the turret's bottom outline runs from r=0.18 to r=0.27 - an
octagon, roundness 0.66 - and it overlaps the hull's aperture by only 6mm, so at
some angles the rotation would show straight into the hull. There is no circular
collar to detect either: the best angular coverage of any circle fitted to the
geometry was 0.27, and on some models there is not even a free gap at the seam.

A ring built here is exactly circular and centred on the spin axis, so it is
invariant under rotation. That means it belongs with the *hull*: rendering it
once, static, covers the seam at every turret angle, and no compositing order
has to be reasoned about.

The turret usually has to rise to make room - it is generated sunk into the
deck. The script reports the lift it applied; set `lift` to 0.0 to refuse it and
see what the seam looks like without.
"""

import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

CONFIG = {
    "turret": "Turret",
    "hull": "Hull",
    "name": "Ring",

    # Axis to centre on. None -> the ring_axis stamped by split_turret.py if
    # present, else the XY centre of the turret's bottom slab.
    "axis": None,
    "slab": 0.12,               # bottom fraction of the turret = its footprint

    # Radius. None -> the turret's bottom outline, so the whole outline rests on
    # the ring at every angle and no gap can open. Anything smaller and the
    # octagonal corners hang over the edge.
    "radius": None,
    "radius_margin": 1.02,
    "footprint_band": 0.05,     # bottom fraction used to read that outline

    "height": 0.018,            # how far the ring stands above the deck
    "segments": 64,             # 64 reads as a circle at sprite resolution
    "sink": 0.008,              # start this far inside the hull, no hairline crack
    "seat_overlap": 0.002,      # let the turret sit this far into the cap

    "lift_turret": True,        # raise the turret onto the ring
    "parent_to_hull": True,     # the ring is static: it turns with the hull
    "material": "Ring_Mat",
    "color": (0.055, 0.06, 0.05, 1.0),
    "roughness": 0.55,
    "metallic": 0.4,
}


def world_co(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def subtree(ob):
    out, stack = [ob], [ob]
    while stack:
        for child in stack.pop().children:
            out.append(child)
            stack.append(child)
    return out


def turret_root(turret, hull):
    """The turret's own subtree root - never a parent it shares with the hull.

    Climbing to the topmost parent looks equivalent and is not: Turret and Hull
    are siblings under one empty, so that walk lands on the shared root and
    "lifting the turret" silently lifts the whole tank instead, leaving the
    turret exactly as sunk as before.
    """
    node = turret
    while node.parent is not None and hull not in subtree(node.parent):
        node = node.parent
    return node


def resolve_axis(turret, cfg):
    if cfg["axis"]:
        return np.array([float(v) for v in cfg["axis"]]), "config"
    if "ring_axis" in turret.keys():
        return np.array([float(v) for v in turret["ring_axis"]]), "ring_axis stamp"
    w = world_co(turret)
    z = w[:, 2]
    low = w[z <= z.min() + cfg["slab"] * (z.max() - z.min())]
    return (np.array([(low[:, 0].max() + low[:, 0].min()) / 2.0,
                      (low[:, 1].max() + low[:, 1].min()) / 2.0]),
            "turret bottom slab")


def build_mesh(name, axis, radius, z0, z1, segments, cfg):
    verts, faces = [], []
    for i in range(segments):
        a = 2.0 * math.pi * i / segments
        x, y = axis[0] + radius * math.cos(a), axis[1] + radius * math.sin(a)
        verts.append((x, y, z0))
        verts.append((x, y, z1))
    for i in range(segments):
        j = (i + 1) % segments
        faces.append([2 * i, 2 * j, 2 * j + 1, 2 * i + 1])       # side wall
    faces.append([2 * i + 1 for i in range(segments)])            # cap on top

    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    me.validate()
    for poly in me.polygons:
        poly.use_smooth = False

    ob = bpy.data.objects.new(name, me)
    if cfg["material"]:
        mat = bpy.data.materials.get(cfg["material"]) \
            or bpy.data.materials.new(cfg["material"])
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf:
            bsdf.inputs["Base Color"].default_value = cfg["color"]
            bsdf.inputs["Roughness"].default_value = cfg["roughness"]
            bsdf.inputs["Metallic"].default_value = cfg["metallic"]
        me.materials.append(mat)
    return ob


def make_ring(cfg):
    turret = bpy.data.objects[cfg["turret"]]
    hull = bpy.data.objects[cfg["hull"]]
    axis, axis_from = resolve_axis(turret, cfg)

    wt, wh = world_co(turret), world_co(hull)
    rt = np.hypot(wt[:, 0] - axis[0], wt[:, 1] - axis[1])
    rh = np.hypot(wh[:, 0] - axis[0], wh[:, 1] - axis[1])
    tz0, tz1 = float(wt[:, 2].min()), float(wt[:, 2].max())

    # the turret's bottom outline: the ring has to reach at least this far
    band = wt[:, 2] <= tz0 + cfg["footprint_band"] * (tz1 - tz0)
    outline_max = float(rt[band].max())
    outline_min = float(rt[band].min())
    radius = float(cfg["radius"]) if cfg["radius"] \
        else outline_max * cfg["radius_margin"]

    inside = rh <= radius
    if not inside.any():
        raise RuntimeError("no hull geometry under a ring of radius %.4f" % radius)
    deck_z = float(np.percentile(wh[inside, 2], 99.5))
    aperture = float(rh[(wh[:, 2] > tz0 - 0.06) & (wh[:, 2] < tz0 + 0.06)].min()) \
        if ((wh[:, 2] > tz0 - 0.06) & (wh[:, 2] < tz0 + 0.06)).any() else None

    z0 = deck_z - cfg["sink"]
    z1 = deck_z + cfg["height"]
    ob = build_mesh(cfg["name"], axis, radius, z0, z1, cfg["segments"], cfg)
    (hull.users_collection[0] if hull.users_collection
     else bpy.context.scene.collection).objects.link(ob)

    lift = 0.0
    if cfg["lift_turret"]:
        target = z1 - cfg["seat_overlap"]
        lift = target - tz0
        if lift > 0.0:
            root = turret_root(turret, hull)
            root.matrix_world = Matrix.Translation(
                Vector((0.0, 0.0, lift))) @ root.matrix_world
        else:
            lift = 0.0

    if cfg["parent_to_hull"]:
        keep = ob.matrix_world.copy()
        ob.parent = hull
        ob.matrix_parent_inverse = hull.matrix_world.inverted()
        ob.matrix_world = keep

    ob["ring_axis"] = [float(axis[0]), float(axis[1])]
    ob["ring_radius"] = radius
    bpy.context.view_layer.update()

    report = {
        "ring": ob.name,
        "axis": [round(float(axis[0]), 5), round(float(axis[1]), 5)],
        "axis_from": axis_from,
        "radius": round(radius, 5),
        "segments": cfg["segments"],
        "z": [round(z0, 5), round(z1, 5)],
        # what the ring is fixing: the turret's own bottom is nowhere near round
        "turret_outline": {"r_min": round(outline_min, 5),
                           "r_max": round(outline_max, 5),
                           "roundness": round(outline_min / outline_max, 3)},
        "hull_aperture_r_min": round(aperture, 5) if aperture else None,
        "deck_z": round(deck_z, 5),
        "turret_lifted_by": round(lift, 5),
        "parented_to": hull.name if cfg["parent_to_hull"] else None,
    }
    print("[ring]", report)
    return report


if __name__ == "__main__":
    make_ring(CONFIG)
