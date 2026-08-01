"""
Where the engine breathes out, and which way.

Run from Blender's Text Editor (Alt+P), or import and call `set_exhaust`.

Needs the exhaust outlet separated into its own object - the same hand step as
`Barrel`, and for the same reason. Measures the port's axis, face and size, and
stamps them onto the hull so the plume pass can place an effect without
guessing.

The same measurement as the muzzle, pointed at different geometry
--------------------------------------------------------------
`muzzle_point.bore_axis` is reused verbatim: it reads the principal directions
of a piece and decides from the singular values whether it is a rod (axis =
longest spread) or a plate (axis = shortest, i.e. the normal). That covers both
kinds of exhaust without a switch:

    a pipe stub out of the rear plate -> a rod.  Axis along the pipe.
    a louvred grille on the deck      -> a plate. Axis is its normal.

MT is the second kind and has no pipe anywhere on it - the rear plate carries
tow hooks and hatches and nothing else. That is worth knowing before going
looking: on these generated models the engine deck grille is usually the only
exhaust-shaped thing there is.

Which way is out
----------------
A principal direction has no sign. The muzzle resolves it by pointing away from
the turret's rotation axis; here the equivalent is pointing away from the
**hull's centre**, which is true of any outlet on any hull - a deck grille vents
upward, a rear pipe vents rearward, a fender pipe vents sideways, and all three
are away from the middle of the tank.

One object per port
-------------------
Two pipes are two objects: `Exhaust` and `Exhaust.001`, or `Exhaust_L` and
`Exhaust_R` - anything whose name starts with the prefix. Splitting them
automatically was the first plan and it does not survive this geometry: a
louvred grille *is* a dozen separate slats with air between them, so any
clustering that pulls two pipes apart also shatters one grille into twelve
ports. Naming is unambiguous, costs one keystroke, and the user is doing the
hand split anyway.

The hull's length is stamped too
--------------------------------
Unlike the flash, the plume cannot be sized in port radii alone. A bore is a
bore on every tank, but a port is a 0.02 pipe on one model and a 0.12 grille on
the next - six times over - and a plume six times bigger is not what a bigger
grille means. So the port radius sets how wide the smoke *leaves*, and the hull's
length sets how far it rises and how far it spreads, which are properties of the
engine and the air rather than of the hole. `exhaust_scale` carries the second
one.
"""

import importlib.util
import math
import os

import bpy
import numpy as np

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    # every mesh object whose name starts with this is one port
    "prefix": "Exhaust",
    "hull": "Hull",
    "turret": "Turret",
    # objects to stamp the result on
    "stamp_on": ["Hull"],
    # the port's width is read off a percentile, not the maximum: one stray
    # vertex on a bracket would inflate a maximum and nothing would notice
    "radius_percentile": 92.0,
    # where along the axis the outlet face is, same reasoning
    "face_percentile": 97.0,
    # flip every port's direction, for the model where the sign rule loses
    "flip": False,
    # a port wider than this fraction of the hull is not a port, it is the deck
    "width_limit": 0.30,
}


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


def ports_of(scene, cfg):
    """The port objects, in name order so the ports keep their numbering."""
    prefix = cfg["prefix"].strip().lower()
    found = [o for o in scene.objects
             if o.type == "MESH" and o.name.strip().lower().startswith(prefix)]
    return sorted(found, key=lambda o: o.name)


def set_exhaust(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    mp = _sibling("muzzle_point")

    hull = scene.objects[cfg["hull"]]
    turret = scene.objects.get(cfg["turret"])
    found = ports_of(scene, cfg)
    if not found:
        raise RuntimeError(
            "no mesh object whose name starts with %r - separate the exhaust "
            "outlet first: select it, Ctrl+L to complete the panels it cuts "
            "through, P > Selection, rename it %r, then select it, Ctrl-click "
            "%r and Ctrl+P > Object (Keep Transform). On these models that is "
            "usually the louvred grille on the engine deck; MT has no pipe."
            % (cfg["prefix"], cfg["prefix"], cfg["hull"]))

    hull_pts = mp.world_vertices(hull)
    hull_lo, hull_hi = hull_pts.min(axis=0), hull_pts.max(axis=0)
    hull_mid = (hull_lo + hull_hi) / 2.0
    # the hull's length: the larger horizontal extent, which is the fore-aft
    # one on every tank and is the number a plume's reach is worth quoting in
    hull_length = float(max(hull_hi[0] - hull_lo[0], hull_hi[1] - hull_lo[1]))

    points, dirs, radii, warnings, detail = [], [], [], [], []
    for ob in found:
        if turret is not None:
            up = ob
            while up is not None:
                if up is turret:
                    raise RuntimeError(
                        "%r is under %r, so the port would swing round with the "
                        "gun. Parent it to %r instead."
                        % (ob.name, turret.name, hull.name))
                up = up.parent
        if ob.parent is not hull:
            warnings.append(
                "%s is not parented to %s - it still renders in the hull layer "
                "(that layer is the whole tank minus the turret), but nothing "
                "keeps it there if the layer is ever narrowed" % (ob.name, hull.name))

        pts = mp.world_vertices(ob)
        centre, axis, spread, shape, margin = mp.bore_axis(pts)

        # away from the middle of the hull: up for a deck grille, back for a
        # rear pipe, out for a fender one
        outward = centre - hull_mid
        if float(np.dot(axis, outward)) < 0.0:
            axis = -axis
        if cfg["flip"]:
            axis = -axis

        along = (pts - centre) @ axis
        across = np.linalg.norm((pts - centre) - np.outer(along, axis), axis=1)
        radius = float(np.percentile(across, cfg["radius_percentile"]))
        face = centre + axis * float(np.percentile(along, cfg["face_percentile"]))

        kind = "pipe" if shape == "rod" else "grille"
        elevation = math.degrees(math.asin(max(-1.0, min(1.0, float(axis[2])))))
        if margin < 1.3:
            warnings.append(
                "%s is neither clearly a pipe nor clearly a plate (margin %.2f) "
                "- its axis is poorly conditioned, check what was separated"
                % (ob.name, margin))
        if elevation < -20.0:
            warnings.append(
                "%s vents %.0f degrees *downward* - either the outward rule "
                "lost (set flip) or the wrong geometry was separated"
                % (ob.name, -elevation))
        if radius > cfg["width_limit"] * hull_length:
            warnings.append(
                "%s is %.0f%% of the hull's length across - that is a deck, not "
                "a port; the plume will be sized off it and swamp the tank"
                % (ob.name, 100.0 * radius / max(hull_length, 1e-9)))

        points.extend(round(float(v), 6) for v in face)
        dirs.extend(round(float(v), 6) for v in axis)
        radii.append(round(radius, 6))
        detail.append({
            "object": ob.name,
            "kind": kind,
            "verts": int(len(pts)),
            "point": [round(float(v), 5) for v in face],
            "dir": [round(float(v), 5) for v in axis],
            "radius": round(radius, 5),
            "bearing": round(math.degrees(math.atan2(axis[1], axis[0])) % 360.0, 2),
            "elevation": round(elevation, 2),
            "shape_margin": round(float(margin), 3),
            "spread": [round(float(v), 5) for v in spread],
            "length_along": round(float(along.max() - along.min()), 5),
        })

    stamp = {
        "exhaust_points": points,
        "exhaust_dirs": dirs,
        "exhaust_radii": radii,
        "exhaust_scale": round(hull_length, 6),
        "exhaust_from": "%s (%s), outward from the hull centre"
                        % (", ".join(o.name for o in found),
                           ", ".join(d["kind"] for d in detail)),
    }
    for name in cfg["stamp_on"]:
        ob = scene.objects.get(name)
        if ob is None:
            continue
        for key, value in stamp.items():
            ob[key] = value

    report = dict(stamp)
    report.update({"ports": detail, "hull_length": round(hull_length, 5),
                   "warnings": warnings})
    print("[exhaust] %d port(s): %s, %d warnings"
          % (len(detail), ", ".join("%s %s" % (d["object"], d["kind"])
                                    for d in detail), len(warnings)))
    for line in warnings:
        print("[exhaust]   ! %s" % line)
    return report


def read_ports(hull):
    """The stamped ports, as [(point, dir, radius)], plus the hull's length."""
    for key in ("exhaust_points", "exhaust_dirs", "exhaust_radii"):
        if key not in hull.keys():
            raise RuntimeError("no %r on %s - run exhaust_point.py first"
                               % (key, hull.name))
    pts = np.array([float(v) for v in hull["exhaust_points"]]).reshape(-1, 3)
    dirs = np.array([float(v) for v in hull["exhaust_dirs"]]).reshape(-1, 3)
    radii = [float(v) for v in hull["exhaust_radii"]]
    scale = float(hull["exhaust_scale"])
    return [(pts[i], dirs[i], radii[i]) for i in range(len(radii))], scale


if __name__ == "__main__":
    from pprint import pprint

    pprint(set_exhaust(CONFIG))
