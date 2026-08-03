"""
Where the gun ends, and which way it points.

Run from Blender's Text Editor (Alt+P), or import and call `set_muzzle`.

Needs the gun tip separated into its own object - see the note below on why
that is worth doing by hand rather than searching for it inside the turret.
Measures the bore axis and the muzzle face from that object, cross-checks the
result against the stamped ring axis, and stamps `muzzle_point` / `muzzle_dir`
onto the turret so the flash pass can place an effect without guessing.

Why a separate object at all
----------------------------
The alternative is to find the barrel inside the turret mesh, and the sprite
harness already tried the flat version of that: take the furthest opaque pixel
along the heading. It puts the muzzle on the wireless aerial, because the aerial
stands higher than anything else and wins the moment the gun foreshortens. In 3D
the same trap is there in a different costume - furthest vertex along -Y is the
aerial too, on any model whose aerial leans forward. Splitting the tip off by
hand costs a minute per scene and removes the whole class of problem: the object
either is the gun or is visibly not.

Rod or disc
-----------
The axis comes from the principal directions of the piece, but *which* principal
direction depends on how much of the gun was separated, and getting this
backwards silently yields an axis at ninety degrees to the truth.

    a long piece of tube  -> a rod: one long spread, two short. Axis = longest.
    just the muzzle brake -> a disc: two equal long spreads, one short.
                             Axis = shortest, along the bore.

So the shape is decided from the singular values rather than assumed, and both
the verdict and its margin are reported. On MT the tip came out a disc with
spreads [0.035, 0.035, 0.019] - a clean factor of 1.9, and the resulting axis
agreed with the ring bearing to 0.7 degrees.

Front, without trusting the convention
--------------------------------------
A principal direction has no sign, so something has to say which end is the
muzzle. The import convention (`front_dir = 270`, nose along -Y) would do, but
using it here would make this measurement depend on the very thing it is best
placed to check. The gun points away from the turret's rotation axis instead:
that is true of any turret, needs nothing declared, and leaves the comparison
with `front_dir` as an independent result rather than an assumption.
"""

import importlib.util
import math
import os

import bpy
import numpy as np

REPO = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() \
    else r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    # the separated gun tip, and the turret it belongs to. Bare names: they
    # resolve to `Barrel.Geometry` / `Turret.Geometry` on a parts-built scene and
    # to themselves on the old ones - see tank_parts.py
    "barrel": "Barrel",
    "turret": "Turret",
    # the object the turret *layer* renders, which is what the barrel has to be
    # parented under. None -> the turret itself, which is what it is on a
    # single-mesh scene; on a parts scene it is `Turret.World`.
    "turret_root": None,
    # where turret_axis.py left the rotation axis
    "ring_prop": "ring_axis",
    # objects to stamp the result on
    "stamp_on": ["Turret", "Barrel"],
    # the bore radius is read off a percentile, not the maximum: one stray
    # vertex on a bracket would inflate a maximum and nothing would notice
    "radius_percentile": 98.0,
    # where along the bore the muzzle face is, same reasoning
    "face_percentile": 99.0,
    # how far the axis may disagree with the ring bearing before it is a
    # complaint rather than a note, in degrees
    "bearing_tolerance": 5.0,
}

_SIBLINGS = {}


def sibling(name):
    """Load a module that lives next to this one, once.

    Blender's Text Editor does not put the repo on `sys.path` and the pipeline
    loads every module by explicit path, so a plain `import` would work in one
    context and fail in the other. Cached, because the lookups below happen per
    call and re-executing a module per lookup is a silly way to spend a second.
    """
    if name not in _SIBLINGS:
        spec = importlib.util.spec_from_file_location(
            name, os.path.join(REPO, "%s.py" % name))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        _SIBLINGS[name] = mod
    return _SIBLINGS[name]


def world_vertices(ob):
    """Vertices of `ob` in world space, as an (n, 3) array."""
    count = len(ob.data.vertices)
    co = np.empty(count * 3, dtype=np.float64)
    ob.data.vertices.foreach_get("co", co)
    return (co.reshape(-1, 3) @ np.array(ob.matrix_world.to_3x3()).T
            + np.array(ob.matrix_world.translation))


def bore_axis(points):
    """The bore direction, unsigned, plus what the piece turned out to be.

    Returns (centre, axis, spread, shape, margin). `margin` is how much better
    the winning reading fits than the other one - near 1.0 means the piece is
    neither rod nor disc and the axis should not be trusted.
    """
    centre = points.mean(axis=0)
    _, singular, basis = np.linalg.svd(points - centre, full_matrices=False)
    spread = singular / math.sqrt(len(points))

    rod = spread[0] / max(spread[1], 1e-12)     # one long direction
    disc = spread[1] / max(spread[2], 1e-12)    # two long, one short
    if rod >= disc:
        return centre, basis[0], spread, "rod", rod
    return centre, basis[2], spread, "disc", disc


def _perpendicular(axis):
    """Two unit vectors spanning the plane across `axis`."""
    seed = np.array([0.0, 0.0, 1.0])
    if abs(float(np.dot(seed, axis))) > 0.9:
        seed = np.array([1.0, 0.0, 0.0])
    u = np.cross(axis, seed)
    u /= np.linalg.norm(u)
    return u, np.cross(axis, u)


def _bearing(vec):
    return math.degrees(math.atan2(vec[1], vec[0])) % 360.0


def _angle_between(a, b):
    return abs((a - b + 180.0) % 360.0 - 180.0)


def set_muzzle(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene

    tp = sibling("tank_parts")
    barrel = tp.mesh(cfg["barrel"], scene, required=False)
    if barrel is None or barrel.type != "MESH":
        raise RuntimeError(
            "no mesh object %r - separate the gun tip first: box-select the "
            "barrel, Ctrl+L to complete the panels it cuts through, P > "
            "Selection, then parent it to the turret with Ctrl+P > Keep "
            "Transform" % cfg["barrel"])
    turret = tp.mesh(cfg["turret"], scene)

    # The barrel must ride in the turret *layer*, and on a parts-built scene the
    # object that layer renders is `Turret.World`, not the turret mesh. Checking
    # against the mesh was right by accident on the old scenes, where the layer
    # target and the mesh are the same object.
    turret_root = (scene.objects[cfg["turret_root"]] if cfg["turret_root"]
                   else turret)
    if not tp.inside(barrel, turret_root) or barrel is turret_root:
        raise RuntimeError(
            "%r is not under %r, so the turret layer would not render it and "
            "the hull layer would not exclude it - the gun would end up in the "
            "hull atlas and turn with the hull"
            % (barrel.name, turret_root.name))

    points = world_vertices(barrel)
    centre, axis, spread, shape, margin = bore_axis(points)

    ring = turret.get(cfg["ring_prop"])
    if ring is None:
        raise RuntimeError("no %r on %s - run turret_axis.py first"
                           % (cfg["ring_prop"], turret.name))
    ring = np.array([float(v) for v in ring])

    # point it away from the rotation axis: the muzzle end, on any turret
    outward = centre[:2] - ring
    if float(np.dot(axis[:2], outward)) < 0.0:
        axis = -axis

    along = (points - centre) @ axis
    perpendicular = np.linalg.norm(
        (points - centre) - np.outer(along, axis), axis=1)
    radius = float(np.percentile(perpendicular, cfg["radius_percentile"]))

    # On the axis, at the muzzle face - not the furthest vertex.
    #
    # The furthest vertex is the obvious reading and it is wrong: it sits on
    # the *rim* of the muzzle face, one bore radius off centre, in whichever
    # direction the mesh happens to have a vertex. On MT that put the point
    # half a radius sideways and half a radius down, and the flash hung off the
    # underside of the barrel instead of coming out of it. Nothing in the
    # numbers said so - it took someone looking at the render.
    #
    # The face plane is a high percentile rather than the maximum, for the same
    # reason the radius is: one stray vertex on a lug would push the whole
    # muzzle forward and nothing would notice.
    reach = float(np.percentile(along, cfg["face_percentile"]))
    tip = centre + axis * reach

    # Cross-check that the centroid really is on the bore. It is, for a
    # rotationally symmetric piece, which a length of tube is - but a muzzle
    # brake with a lug or an uneven baffle would drag it off, and then the
    # flash would be off centre again in a way that looks like the bug above.
    #
    # The face centre is the middle of its extent, not the mean of its
    # vertices. A vertex mean is not an area centroid: these meshes are panel
    # soup with wildly uneven tessellation, so the mean drifts toward whichever
    # side has more geometry. Measured that way MT's face read 25% of a radius
    # off and tripped this warning, while the piece's own bounding box put the
    # bore within 0.003 of the centroid - the mesh was lopsided, the gun was
    # not. An extent is blind to vertex density, which is the whole point.
    face = points[along >= np.percentile(along, 90.0)] - centre
    u, v = _perpendicular(axis)
    fu, fv = face @ u, face @ v
    face_offset = float(math.hypot((fu.max() + fu.min()) / 2.0,
                                   (fv.max() + fv.min()) / 2.0))

    heading = _bearing(axis)
    heading_ring = _bearing(outward)
    disagreement = _angle_between(heading, heading_ring)

    stamp = {
        "muzzle_point": [round(float(v), 6) for v in tip],
        "muzzle_dir": [round(float(v), 6) for v in axis],
        "muzzle_heading": round(heading, 3),
        "muzzle_elevation": round(math.degrees(math.asin(
            max(-1.0, min(1.0, float(axis[2]))))), 3),
        "muzzle_radius": round(radius, 6),
        "muzzle_shape": shape,
        "muzzle_from": "%s principal axis (%s, margin %.2f), bearing checked "
                       "against %s" % (barrel.name, shape, margin,
                                       cfg["ring_prop"]),
    }
    for name in cfg["stamp_on"]:
        ob = tp.mesh(name, scene, required=False)
        if ob is None:
            continue
        for key, value in stamp.items():
            ob[key] = value

    warnings = []
    if margin < 1.3:
        warnings.append(
            "the piece is neither clearly a rod nor clearly a disc (margin "
            "%.2f) - the axis is poorly conditioned, check what was separated"
            % margin)
    if disagreement > cfg["bearing_tolerance"]:
        warnings.append(
            "the bore axis points %.1f deg but the turret ring puts the piece "
            "at %.1f deg, %.1f apart - either the wrong geometry was separated "
            "or the ring axis is off" % (heading, heading_ring, disagreement))
    if face_offset > 0.25 * radius:
        warnings.append(
            "the muzzle face sits %.3f off the axis through the centroid, %.0f%% "
            "of the bore radius - the piece is not symmetric about its bore, so "
            "the muzzle point is off centre and the flash will hang to one side"
            % (face_offset, 100.0 * face_offset / max(radius, 1e-9)))

    report = dict(stamp)
    report.update({
        "barrel": barrel.name,
        "verts": len(points),
        "spread": [round(float(v), 5) for v in spread],
        "shape_margin": round(float(margin), 3),
        "bearing_from_ring": round(heading_ring, 3),
        "bearing_disagreement": round(disagreement, 3),
        "face_offset_from_axis": round(face_offset, 6),
        "rim_vertex_offset": round(float(np.linalg.norm(
            points[int(np.argmax(along))] - tip)), 6),
        "reach_from_ring": round(float(np.hypot(*outward)), 6),
        "length_along_bore": round(float(along.max() - along.min()), 6),
        "warnings": warnings,
    })
    print("[muzzle] %s: %s, axis %.2f deg (ring says %.2f), %d warnings"
          % (barrel.name, shape, heading, heading_ring, len(warnings)))
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(set_muzzle(CONFIG))
