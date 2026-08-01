"""
Build a flat hexagonal tile sized to a model, and stand the model on it.

Run from Blender's Text Editor (Alt+P).

The hexagon is a single flat n-gon at `ground_z` with its normal up - no
thickness. It is sized from the model's *rotational* footprint, i.e. the circle
the model sweeps when it turns, so the model stays inside the tile at any
heading rather than only at its current one.

Orientation matters for a game grid:
    flat    vertices on +/-X, flat edges top and bottom, so the six neighbour
            directions run through the edges at +/-Y and +/-60 deg
    pointy  vertices on +/-Y, neighbour directions at +/-X and +/-60 deg
A hull whose front points down -Y lines up with a neighbour on a flat-top tile,
which is why that is the default.
"""

import math

import bpy
import numpy as np
from mathutils import Matrix, Vector

CONFIG = {
    "target": "world",          # hierarchy to fit and to stand on the tile
    "name": "Hex",
    "orientation": "flat",      # "flat" or "pointy"

    # Name hints whose *bottom slab* gives the axis the tile centres on - for a
    # tank, the turret. Centring the tile on the turret ring rather than on the
    # hull is what lets hull and turret sprites share one spin axis: otherwise
    # the ring's screen position depends on the hull's heading and the layers
    # stop compositing by plain overlay. None -> centre of the target.
    # Measure it from the bottom slab, never from the bounding box: a gun
    # barrel drags a turret's bbox centre well forward of its ring.
    "ring_from": None,
    # Name hints of what has to fit inside the tile, measured from that axis.
    # None -> the whole target. A gun barrel is normally allowed to overhang,
    # so point this at the hull alone.
    "fit_to": None,
    "slab": 0.12,               # bottom fraction that counts as the ring

    # Circumradius. None -> derived from the model's footprint so that the
    # tile's inradius clears the model's turning circle by `padding`.
    "size": None,
    "padding": 1.10,
    "ground_z": 0.0,            # height of the tile plane
    "move_target": True,        # drop the model onto the tile
    "center_on_origin": True,   # also centre the model over the tile
    "material": "Hex_Mat",      # None -> no material
    "color": (0.22, 0.24, 0.20, 1.0),
    "roughness": 0.85,
}


def hierarchy(root):
    out, stack = [root], [root]
    while stack:
        for child in stack.pop().children:
            out.append(child)
            stack.append(child)
    return out


def world_co(ob):
    me = ob.data
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world.to_4x4(), dtype=np.float64)
    return co.reshape(n, 3) @ m[:3, :3].T + m[:3, 3]


def by_hints(hints):
    """Mesh objects whose name contains any of `hints`, plus their children."""
    out = []
    for hint in hints or ():
        needle = hint.lower()
        for ob in bpy.context.scene.objects:
            if needle in ob.name.lower():
                out.extend(hierarchy(ob))
    return [o for o in dict.fromkeys(out)
            if o.type == "MESH" and o.data.vertices]


def footprint(objects):
    """(centre_xy, turning radius, lowest z) of a set of mesh objects."""
    co = np.vstack([world_co(o) for o in objects])
    cx = (co[:, 0].max() + co[:, 0].min()) / 2.0
    cy = (co[:, 1].max() + co[:, 1].min()) / 2.0
    radius = float(np.hypot(co[:, 0] - cx, co[:, 1] - cy).max())
    return (float(cx), float(cy)), radius, float(co[:, 2].min())


def slab_centre(objects, slab):
    """XY centre of the lowest `slab` fraction of some objects - a turret ring."""
    co = np.vstack([world_co(o) for o in objects])
    z = co[:, 2]
    low = co[z <= z.min() + slab * (z.max() - z.min())]
    return (float((low[:, 0].max() + low[:, 0].min()) / 2.0),
            float((low[:, 1].max() + low[:, 1].min()) / 2.0))


def radius_about(objects, axis):
    co = np.vstack([world_co(o) for o in objects])
    return float(np.hypot(co[:, 0] - axis[0], co[:, 1] - axis[1]).max())


def build_hex(name, centre, circumradius, z, orientation, cfg):
    start = 0.0 if orientation == "flat" else 30.0
    verts = [(centre[0] + circumradius * math.cos(math.radians(start + 60 * i)),
              centre[1] + circumradius * math.sin(math.radians(start + 60 * i)),
              z) for i in range(6)]

    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], [[0, 1, 2, 3, 4, 5]])   # CCW winding -> normal +Z
    me.update()
    me.polygons[0].use_smooth = False

    # planar UVs across the tile's bounding square, handy for texturing later
    uv = me.uv_layers.new(name="UVMap")
    span = 2.0 * circumradius
    for loop in me.loops:
        v = me.vertices[loop.vertex_index].co
        uv.data[loop.index].uv = ((v.x - centre[0]) / span + 0.5,
                                  (v.y - centre[1]) / span + 0.5)

    ob = bpy.data.objects.new(name, me)
    if cfg["material"]:
        mat = bpy.data.materials.get(cfg["material"]) \
            or bpy.data.materials.new(cfg["material"])
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf:
            bsdf.inputs["Base Color"].default_value = cfg["color"]
            bsdf.inputs["Roughness"].default_value = cfg["roughness"]
        me.materials.append(mat)
    return ob


def make_hex(cfg):
    root = bpy.data.objects[cfg["target"]]
    meshes = [o for o in hierarchy(root) if o.type == "MESH" and o.data.vertices]
    if not meshes:
        raise RuntimeError("nothing renderable under %r" % root.name)

    centre, radius, low_z = footprint(meshes)

    # the axis the tile centres on, and what has to fit around it
    axis_src = "target footprint"
    if cfg.get("ring_from"):
        ring = by_hints(cfg["ring_from"])
        if not ring:
            raise RuntimeError("ring_from matched nothing: %r" % cfg["ring_from"])
        # prefer the ring split_turret.py measured from the free gap over a
        # bottom-slab guess: the guess drifts with the slab fraction
        stamped = next((o for o in hierarchy(bpy.data.objects[cfg["target"]])
                        if "ring_axis" in o.keys()), None)
        if stamped is not None and cfg.get("use_stamped_ring", True):
            centre = tuple(float(v) for v in stamped["ring_axis"])
            axis_src = "ring_axis stamped on %s" % stamped.name
        else:
            centre = slab_centre(ring, cfg["slab"])
            axis_src = "bottom slab of %s" % ", ".join(cfg["ring_from"])
    fit = by_hints(cfg["fit_to"]) if cfg.get("fit_to") else meshes
    radius = radius_about(fit, centre)

    if cfg["size"]:
        circumradius = float(cfg["size"])
    else:
        # inradius = circumradius * cos(30) must clear the turning circle
        circumradius = radius * cfg["padding"] / math.cos(math.radians(30.0))

    tile_centre = (0.0, 0.0) if cfg["center_on_origin"] else centre
    ob = build_hex(cfg["name"], tile_centre, circumradius, cfg["ground_z"],
                   cfg["orientation"], cfg)
    root.users_collection  # noqa - keep the model's collection choice in mind
    (meshes[0].users_collection[0] if meshes[0].users_collection
     else bpy.context.scene.collection).objects.link(ob)

    moved = None
    if cfg["move_target"]:
        dx = (tile_centre[0] - centre[0]) if cfg["center_on_origin"] else 0.0
        dy = (tile_centre[1] - centre[1]) if cfg["center_on_origin"] else 0.0
        dz = cfg["ground_z"] - low_z
        delta = Vector((dx, dy, dz))
        root.matrix_world = Matrix.Translation(delta) @ root.matrix_world
        bpy.context.view_layer.update()
        moved = [round(v, 5) for v in delta]

    inradius = circumradius * math.cos(math.radians(30.0))
    report = {
        "hex": ob.name,
        "orientation": cfg["orientation"],
        "circumradius": round(circumradius, 5),
        "inradius": round(inradius, 5),
        "width_across_flats": round(2 * inradius, 5),
        "width_across_corners": round(2 * circumradius, 5),
        "tile_centre": [round(v, 5) for v in tile_centre] + [cfg["ground_z"]],
        "spin_axis": [round(v, 5) for v in tile_centre],
        "axis_from": axis_src,
        "fitted_radius_about_axis": round(radius, 5),
        "clearance": round(inradius - radius, 5),
        "model_moved_by": moved,
    }
    print("[hex]", report)
    return report


if __name__ == "__main__":
    make_hex(CONFIG)
