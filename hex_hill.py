"""
A hex plateau with sloping sides, for a tank to stand on.

Run from Blender's Text Editor (Alt+P), or import and call `build()`.

The bench's 3D stage models a raised cell as a *prism*: a hexagon with a
vertical earth wall dropped from its rim. That is the cheapest thing that is
correct, and it reads as a cut block rather than as ground. This is the same
cell with the wall laid back into a slope.

**The top face is the cell, and the slope goes outward and down.** The other
way round - base at the rim, top inset - keeps the hill inside one cell and
costs the thing the cell is *for*: the tile is sized so the tank's turning
circle clears its inradius, so a slope eating `run` off that inradius leaves the
tank overhanging its own plateau by however much the slope took. Measured on
HT_PARTS: a run of a third of the radius already leaves the tank's circle 0.29
world units wider than the flat top under it. So the slope is put where it costs
nothing measured and something drawn - it spills onto the neighbours, and
`spill` in the report says by how much, as a fraction of the distance to one.

**And a gentle slope of a full level does not fit beside its neighbours at
all.** That is arithmetic, not a setting: one bench level is `grade * reach`
= 0.606 of the circumradius, so a 40-degree hillside needs 1.08 of it in run and
the base lands a full cell away. Steep enough to stay home, gentle enough to
read as a hill - pick one. `run` is the knob and the report prints what each
choice bought.

**The crest and the toe are rounded, and that is what makes it a hill rather
than a truncated pyramid.** The radius grows linearly down the profile while the
drop follows a smoothstep, so the slope is zero at both ends and steepest
halfway - which puts the maximum at `1.5 * rise / run`, not `rise / run`. The
report prints the steepest angle rather than the average for that reason: the
average is not anywhere on the model.

Two things it deliberately is not:

**Not parented to anything.** The trap `Flash`, `Plume`, `Fire` and `Burn` are
all named for: a child of a layer root rides into that layer's atlas and stays
there. The hill sits at the scene root, and it is excluded from a render by
name, never by parentage.

**Not a second copy on every run.** Rebuilt in place, because
`sprite_atlas.by_hints` matches names by substring and a `Hill.001` left beside
`Hill` is two hills in every layer that asked for one.
"""

import math

import bpy
import numpy as np

CONFIG = {
    "name": "Hill",

    # What stands on it, and what its hexagon is measured from - the same two
    # answers `parts_render.ground_tile` gives, and for its reasons: the fit is
    # the part that drives over the cell, and the centre is the turret ring, so
    # hill, hull, turret and belts share one axis.
    # Roles, not object names, so this reads on both scene structures - see
    # `tank_parts.root`. The turret is left out for the reason the gun barrel
    # always is: what has to stay on the cell is the part that drives over it.
    "stand_on": ("hull", "track_left", "track_right"),
    "axis": None,               # None -> the stamped `ring_axis`
    "size": None,               # circumradius of the TOP face; None -> derived
    # Clearance of the flat top's inradius over the tank's turning circle.
    #
    # <b>1.0, not the tile's 1.10, and the difference is what the two are
    # for.</b> `hex_base` pads because the tile is a *cell*: the tank has to
    # stay inside it at every heading with room to be seen doing it. The summit
    # is not a cell, it is the ground the tank is standing on, and padded like
    # one it reads as a terrace with a tank parked in the middle of it. At 1.0
    # the swept circle is inscribed in the flat top exactly - the tank's widest
    # point meets the crest at every heading and passes it at none.
    #
    # <b>Zero clearance is not zero support, and that is the rounded shoulder
    # paying for itself.</b> The profile leaves the crest level, so the first
    # bands past it are still ground; what the tank overhangs at 1.0 is not air.
    "padding": 1.00,

    # One level, as the bench states it: a fraction of the distance to a
    # neighbour, which is sqrt(3) * circumradius. Kept in the bench's own units
    # so a hill built here is the height the board already draws.
    "rise": 0.35,
    # How far the toe flares past the crest, as a fraction of the top
    # circumradius. This is the whole argument above, as one number.
    #
    # <b>Chosen off `out/_check_hill.png` rather than picked.</b> Three were
    # rendered on HT_PARTS at the board's own camera: 0.70 is 52 degrees and
    # reads as a cut plinth, 1.80 is 27 and reads as a pad with the hill gone
    # out of it, 1.20 is 37 and is the only one of the three that reads as a
    # hill. What it costs is `spill` 0.69 - the toe lands past halfway to the
    # neighbour's centre - and that is the price of the word "gentle", not a
    # defect of this number.
    "run": 1.20,
    "rings": 12,
    # How steep a face may be and still wear the top's material.
    #
    # <b>Not zero, and the render is what named it.</b> Split at the crest
    # exactly, the rounded shoulder - which by construction starts out level -
    # came back in the earth colour, so the flat top read as a green plate set
    # on a sand pedestal rather than as the top of a hill. Ground runs over a
    # shoulder and gives out where the slope does, so the split is by slope and
    # not by ring.
    "grass_deg": 26.0,

    "orientation": "flat",
    "top_color": (0.22, 0.24, 0.20, 1.0),
    "side_color": (0.42, 0.30, 0.20, 1.0),
    "roughness": 0.9,
}


def _hex_base():
    import importlib
    return importlib.import_module("hex_base")


def _axis(cfg):
    """Where the hill centres: the stamped ring, never the footprint.

    An error rather than a fallback when the stamp is missing. A hill centred on
    the bounding box instead is off by however far the gun drags it forward, and
    nothing downstream says so - the same silent failure `turret_axis` exists to
    prevent, arriving through a different door.
    """
    if cfg["axis"] is not None:
        return float(cfg["axis"][0]), float(cfg["axis"][1])
    import importlib
    tp = importlib.import_module("tank_parts")
    hull = tp.mesh("Hull")
    if "ring_axis" not in hull:
        raise RuntimeError(f"{hull.name} carries no ring_axis - run turret_axis "
                           f"first, or pass an explicit axis")
    stamp = hull["ring_axis"]
    return float(stamp[0]), float(stamp[1])


def _top(cfg):
    """Centre, circumradius and height of the flat top.

    The height is the bottom of what stands on it, so the plateau lands under
    the tracks wherever the tank happens to sit - and the tank is not moved.
    That direction is not a convenience: the four layer roots are what the
    renderer writes and restores per frame and what every stamp is measured
    against, so the new thing moves and the measured thing does not.
    """
    import importlib
    hb, tp = _hex_base(), importlib.import_module("tank_parts")
    stand = [o for part in cfg["stand_on"]
             for o in hb.hierarchy(tp.root(part))
             if o.type == "MESH" and o.data.vertices and not o.hide_render]
    if not stand:
        raise RuntimeError("nothing to stand on the hill")
    axis = _axis(cfg)
    _, _, low_z = hb.footprint(stand)
    fit = hb.radius_about(stand, axis)
    circum = (float(cfg["size"]) if cfg["size"]
              else fit * cfg["padding"] / math.cos(math.radians(30.0)))
    return axis, circum, float(low_z), fit


def _material(name, colour, roughness):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = colour
        bsdf.inputs["Roughness"].default_value = roughness
    return mat


def _profile(rings):
    """The drop down the slope against the distance out from the crest.

    Linear in radius, smoothstep in height, so both ends run out flat: the crest
    is a shoulder rather than a crease and the toe blends into the ground rather
    than standing on it. The steepest point is the middle, at 1.5x the average.
    """
    t = np.linspace(0.0, 1.0, rings + 1)
    return t, t * t * (3.0 - 2.0 * t)


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    (cx, cy), circum, top_z, fit = _top(cfg)

    reach = math.sqrt(3.0) * circum
    rise = cfg["rise"] * reach
    run = cfg["run"] * circum
    t, drop = _profile(cfg["rings"])
    start = 0.0 if cfg["orientation"] == "flat" else 30.0
    ang = [math.radians(start + 60.0 * i) for i in range(6)]

    def ring(k):
        r = circum + run * t[k]
        z = top_z - rise * drop[k]
        return [(cx + r * math.cos(a), cy + r * math.sin(a), z) for a in ang]

    verts, faces, smooth, slot = [], [], [], []

    # The top, one n-gon, wound CCW so its normal is +Z.
    top = ring(0)
    verts.extend(top)
    faces.append(list(range(6)))
    smooth.append(False)
    slot.append(0)

    # The skirt, six panels each with its own vertices. Splitting them is what
    # keeps the six corners hard while the slope down each panel is smooth -
    # the alternative is smoothing the whole band, which rounds a hex hill into
    # a cone, and the alternative to *that* is sharp-edge bookkeeping for a
    # shape whose creases are known before it is built.
    rings = [ring(k) for k in range(cfg["rings"] + 1)]
    # The slope of each band, from the profile rather than from the built face:
    # one number per band, shared by all six panels, so a panel cannot come back
    # wearing a different material from the one beside it.
    dr = run / cfg["rings"] if cfg["rings"] else 0.0
    band = [math.degrees(math.atan2(rise * (drop[k + 1] - drop[k]), dr))
            if dr > 0 else 90.0 for k in range(cfg["rings"])]
    for i in range(6):
        j = (i + 1) % 6
        base = len(verts)
        for row in rings:
            verts.extend([row[i], row[j]])
        for k in range(cfg["rings"]):
            a, b = base + 2 * k, base + 2 * k + 1
            faces.append([a, b, b + 2, a + 2])
            smooth.append(True)
            slot.append(0 if band[k] <= cfg["grass_deg"] else 1)

    # The floor, so the hill is a closed solid: an open shell throws shadows
    # through itself and reads as a hole from any camera that gets under the
    # toe.
    base = len(verts)
    verts.extend(rings[-1])
    faces.append(list(range(base + 5, base - 1, -1)))
    smooth.append(False)
    slot.append(1)

    me = bpy.data.meshes.new(cfg["name"])
    me.from_pydata(verts, [], faces)
    me.update()
    for poly, sm, sl in zip(me.polygons, smooth, slot):
        poly.use_smooth = sm
        poly.material_index = sl

    # The top takes the tile's own planar UV, so it can wear the same ground art
    # the flat cell wears; the skirt takes a strip, u round the rim and v down
    # the slope, which is what a cliff texture wants.
    uv = me.uv_layers.new(name="UVMap")
    span = 2.0 * circum
    for poly in me.polygons:
        for loop in poly.loop_indices:
            v = me.vertices[me.loops[loop].vertex_index].co
            if poly.material_index == 0:
                uv.data[loop].uv = ((v.x - cx) / span + 0.5,
                                    (v.y - cy) / span + 0.5)
            else:
                a = math.atan2(v.y - cy, v.x - cx) / (2.0 * math.pi)
                uv.data[loop].uv = (a % 1.0, (v.z - top_z) / max(rise, 1e-6))

    me.materials.append(_material(f"{cfg['name']}_Top", cfg["top_color"],
                                  cfg["roughness"]))
    me.materials.append(_material(f"{cfg['name']}_Side", cfg["side_color"],
                                  cfg["roughness"]))

    old = bpy.data.objects.get(cfg["name"])
    if old is not None:
        stale = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if stale.users == 0:
            bpy.data.meshes.remove(stale)
    ob = bpy.data.objects.new(cfg["name"], me)
    bpy.context.scene.collection.objects.link(ob)

    steep = math.degrees(math.atan2(1.5 * rise, run)) if run > 0 else 90.0
    return ob, {
        "name": ob.name,
        "centre": [round(cx, 5), round(cy, 5)],
        "top_z": round(top_z, 5),
        "circumradius": round(circum, 5),
        "base_radius": round(circum + run, 5),
        "fit_radius": round(fit, 5),
        # Clearance of the tank's own turning circle over the flat top, in world
        # units. Positive is the tank inside its plateau; it goes negative the
        # moment somebody inverts the slope, which is the failure this module's
        # docstring is about.
        "clear": round(circum * math.cos(math.radians(30.0)) - fit, 5),
        "rise": round(rise, 5),
        "run": round(run, 5),
        "steepest_deg": round(steep, 2),
        # Bands wearing the top's material, of `rings`. Expect them at *both*
        # ends: the toe runs out level too, and the ground it runs out onto is
        # the neighbouring cell's top, so grass there is right rather than a
        # leak. Zero means the whole hillside is bare, which is what a `run`
        # short enough to make every band steep buys.
        "grass_bands": int(sum(1 for b in band if b <= cfg["grass_deg"])),
        # How far the toe reaches past the cell, as a fraction of the distance
        # to a neighbour. Above 0.5 the hill has crossed into the cell beyond
        # the next one.
        "spill": round(run / reach, 3),
        "verts": len(me.vertices),
        "faces": len(me.polygons),
    }


if __name__ == "__main__":
    _, report = build(CONFIG)
    for key, value in report.items():
        print(f"{key:>14}: {value}")
