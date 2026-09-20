"""A concrete tank shelter on one board cell - the hex capon.

A box the tank drives into from one side and fires out of through the
opposite one. Five sides are blank concrete, the sixth is the entrance, and
the side facing it carries a slit for the gun. A roof closes it from above,
so a parked tank is hidden entirely: only the barrel shows, through the slit.

Everything is a fraction of the hex circumradius `R`, as docs/HEX_TILES.md
demands, and the ruler is the MTP tank stood on `Tile.Plain` at R = 1 by the
pipeline's own rule (turning radius x 1.10 / cos 30 = R):

    hull + tracks   1.23 long, 0.84 wide, 0.53 high (ring axis is the cell centre,
                    the hull runs from y = -0.56 to +0.67 about it)
    turret top      1.06
    barrel          z 0.62 .. 0.75, muzzle at y = -0.69

Those numbers are why the walls are 0.12 thick (inner span 1.39 leaves the
hull 0.02 at the back) and the roof underside sits at 1.15. The slit is sized
against all five sets (`TANKS`, `slit_report`), not MTP alone.

A tank does not turn inside a capon: it drives in along the gate-to-slit
axis and stays there. So the interior is judged against the hull rectangle,
not the turning circle the tile rule sizes cells by (0.787 R, which no wall
inside a cell could clear). With the walls on the cell boundary the inner
inradius is 0.746 R: 1.49 R gate to slit, and 1.72 R corner to corner at
the widest.

Built as one flat-shaded mesh from hexahedra between an outer and an inner
hexagon, one per side, so corners mitre exactly and nothing is a boolean.
The entrance is a side with only a lintel; the slit side is two jambs, a sill
and a lintel. Flat-top hex: side `k` has its outward normal at 30 + 60k deg,
so the default `slit_side = 4` faces -Y (the project's front, toward the
camera at azimuth 0) and `gate_side = 1` faces +Y.

Orientation is by rotating the *mesh* by 60 deg steps, never the object: the
sprite renderer writes the layer root's matrix every frame (docs/HEX_TILES.md,
"Поворот идёт на меш").

Entry points
------------
`build(cfg)`   - `Capon.World` (empty) at `cfg["cell"]`, with `Capon.Geometry`
                 and `Capon.Tile` (the board's own `Tile.Plain`) under it.
`fit_report()` - the clearances against the MTP ruler above, in R.
"""

import math

import bmesh
import bpy
from mathutils import Matrix, Vector

CONFIG = {
    "name": "Capon",
    "cell": (0.0, 0.0, 0.0),   # where Capon.World goes; (3, 0) is two columns right
    "R": 1.0,
    "reach": 1.0,              # outer hexagon as a fraction of the tile: the walls
                               # stand on the cell boundary itself, no setback
    "wall_t": 0.12,
    "wall_h": 1.15,            # roof underside; turret top is 1.06
    "roof_t": 0.12,
    # The slit has to pass every tank's gun, not MTP's: muzzle heights run
    # 0.51 R (TDP) to 0.71 R (HMP) with barrel radii up to 0.12 R (HTP), and
    # the elevation ladder swings the muzzle +-14 deg (HMP to +18.5) about a
    # trunnion roughly 0.5 R behind it, another 0.12 R either way. See TANKS.
    "slit_z": (0.40, 0.90),
    "slit_jamb": 0.13,         # fraction of the side left as wall each end
    # No beam over the entrance: the roof's own edge is the lintel. A 0.20
    # beam under a 1.15 roof hangs at 0.95 and the medium's turret (1.06)
    # would strike it driving in. The stand (godot CaponKit) made the same
    # call, and for a second reason: a beam's centre sits on the gate's edge
    # ray, so the board read the gate as walled.
    "lintel_h": 0.0,
    "slit_side": 4,            # outward normal 270 deg = -Y = front
    "gate_side": 1,            # 90 deg = +Y, opposite
    "face": 0,                 # extra 60 deg steps applied to the mesh
    "material": "Capon.Concrete",
    "concrete": ((0.21, 0.21, 0.20), (0.38, 0.37, 0.35)),
    "tile_mesh": "Tile.Plain",
}

# MTP on Tile.Plain at R = 1, from PROP_base.blend - see the module docstring.
RULER = {"hull_y": (-0.56, 0.67), "width": 0.84, "turret_top": 1.0625,
         "barrel_z": (0.62, 0.75), "muzzle_y": -0.69}

# Every shipped set's muzzle in units of its own hex circumradius, read from
# Sprites/<TAG>/_run_report.json (muzzle_point against spin_pivot and
# ground_z, hex R from hex_atlas.json's frame width x units_per_pixel).
# `elev` is the span of the elevation ladder in degrees.
TANKS = {
    "LTP": {"muzzle_y": -0.560, "muzzle_z": 0.694, "barrel_r": 0.063, "elev": (-14.0, 14.0)},
    "MTP": {"muzzle_y": -0.685, "muzzle_z": 0.675, "barrel_r": 0.066, "elev": (-14.0, 14.0)},
    "HTP": {"muzzle_y": -0.635, "muzzle_z": 0.639, "barrel_r": 0.119, "elev": (-14.0, 14.0)},
    "TDP": {"muzzle_y": -0.998, "muzzle_z": 0.507, "barrel_r": 0.067, "elev": (-14.0, 14.0)},
    "HMP": {"muzzle_y": -0.586, "muzzle_z": 0.707, "barrel_r": 0.093, "elev": (0.0, 18.5)},
}
TRUNNION_BACK = 0.5    # muzzle to pivot along the barrel, R - a round figure, not a measurement


def slit_report(cfg=None):
    """Per tank: how much the slit clears the barrel, at rest and at the ends of
    the elevation ladder. Negative means the gun hits concrete."""
    cfg = dict(CONFIG, **(cfg or {}))
    z0, z1 = cfg["slit_z"]
    half_w = cfg["R"] * cfg["reach"] * (1.0 - 2.0 * cfg["slit_jamb"]) / 2.0
    out = {}
    for tag, t in TANKS.items():
        lo_e, hi_e = t["elev"]
        swing_dn = TRUNNION_BACK * math.tan(math.radians(-lo_e))
        swing_up = TRUNNION_BACK * math.tan(math.radians(hi_e))
        out[tag] = {
            "below_at_rest": round(t["muzzle_z"] - t["barrel_r"] - z0, 3),
            "above_at_rest": round(z1 - t["muzzle_z"] - t["barrel_r"], 3),
            "below_depressed": round(t["muzzle_z"] - swing_dn - t["barrel_r"] - z0, 3),
            "above_elevated": round(z1 - t["muzzle_z"] - swing_up - t["barrel_r"], 3),
            "side": round(half_w - t["barrel_r"], 3),
        }
    return out


def _hexring(radius):
    """Flat-top: two corners on the X axis, none on Y."""
    return [Vector((radius * math.cos(math.radians(60 * k)),
                    radius * math.sin(math.radians(60 * k)), 0.0)) for k in range(6)]


def _box(bm, o0, o1, i0, i1, z0, z1):
    """Hexahedron between outer edge o0->o1 and inner edge i0->i1, z0..z1."""
    lo = [bm.verts.new((p.x, p.y, z0)) for p in (o0, o1, i1, i0)]
    hi = [bm.verts.new((p.x, p.y, z1)) for p in (o0, o1, i1, i0)]
    bm.faces.new(list(reversed(lo)))
    bm.faces.new(hi)
    for a in range(4):
        b = (a + 1) % 4
        bm.faces.new((lo[a], lo[b], hi[b], hi[a]))


def build_mesh(cfg):
    R = cfg["R"]
    R_out = R * cfg["reach"]
    r_out = math.sqrt(3.0) / 2.0 * R_out
    R_in = (r_out - cfg["wall_t"]) / (math.sqrt(3.0) / 2.0)
    outer, inner = _hexring(R_out), _hexring(R_in)
    H = cfg["wall_h"]

    bm = bmesh.new()
    for k in range(6):
        o0, o1 = outer[k], outer[(k + 1) % 6]
        i0, i1 = inner[k], inner[(k + 1) % 6]
        if k == cfg["gate_side"]:
            if cfg["lintel_h"] > 0.0:
                _box(bm, o0, o1, i0, i1, H - cfg["lintel_h"], H)
        elif k == cfg["slit_side"]:
            a = cfg["slit_jamb"]
            z1, z2 = cfg["slit_z"]
            oa, ob_ = o0.lerp(o1, a), o0.lerp(o1, 1.0 - a)
            ia, ib = i0.lerp(i1, a), i0.lerp(i1, 1.0 - a)
            _box(bm, o0, oa, i0, ia, 0.0, H)
            _box(bm, ob_, o1, ib, i1, 0.0, H)
            _box(bm, oa, ob_, ia, ib, 0.0, z1)
            _box(bm, oa, ob_, ia, ib, z2, H)
        else:
            _box(bm, o0, o1, i0, i1, 0.0, H)

    lo = [bm.verts.new((p.x, p.y, H)) for p in outer]
    hi = [bm.verts.new((p.x, p.y, H + cfg["roof_t"])) for p in outer]
    bm.faces.new(list(reversed(lo)))
    bm.faces.new(hi)
    for k in range(6):
        bm.faces.new((lo[k], lo[(k + 1) % 6], hi[(k + 1) % 6], hi[k]))

    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    if cfg["face"]:
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0.0, 0.0, 0.0),
                         matrix=Matrix.Rotation(cfg["face"] * math.pi / 3.0, 3, "Z"))
    bm.normal_update()

    name = "%s.Geometry" % cfg["name"]
    me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return me


def concrete(cfg):
    mat = bpy.data.materials.get(cfg["material"])
    if mat is not None:
        return mat
    mat = bpy.data.materials.new(cfg["material"])
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    coord = nt.nodes.new("ShaderNodeTexCoord")
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 9.0
    noise.inputs["Detail"].default_value = 6.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    dark, light = cfg["concrete"]
    ramp.color_ramp.elements[0].color = (*dark, 1.0)
    ramp.color_ramp.elements[1].color = (*light, 1.0)
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.25
    # object space, like the bricks: the pattern must travel with the mesh
    nt.links.new(coord.outputs["Object"], noise.inputs["Vector"])
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.92
    return mat


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    n = cfg["name"]
    for suffix in ("Geometry", "Tile", "World"):
        old = bpy.data.objects.get("%s.%s" % (n, suffix))
        if old is not None:
            bpy.data.objects.remove(old, do_unlink=True)

    me = build_mesh(cfg)
    me.materials.clear()
    me.materials.append(concrete(cfg))

    col = bpy.context.scene.collection
    root = bpy.data.objects.new("%s.World" % n, None)
    col.objects.link(root)
    root.location = cfg["cell"]
    geo = bpy.data.objects.new("%s.Geometry" % n, me)
    col.objects.link(geo)
    geo.parent = root

    tile_me = bpy.data.meshes.get(cfg["tile_mesh"])
    if tile_me is None:
        raise RuntimeError("no %r mesh in this file; bring the board tile first "
                           "(brick_wall.hex_tile)" % cfg["tile_mesh"])
    tile = bpy.data.objects.new("%s.Tile" % n, tile_me)
    col.objects.link(tile)
    tile.parent = root
    bpy.context.view_layer.update()
    return {"root": root, "geometry": geo, "tile": tile,
            "report": fit_report(cfg), "slit": slit_report(cfg)}


def fit_report(cfg=None):
    """Clearances against the MTP ruler, in R. Negative means it does not fit."""
    cfg = dict(CONFIG, **(cfg or {}))
    r_out = math.sqrt(3.0) / 2.0 * cfg["R"] * cfg["reach"]
    r_in = r_out - cfg["wall_t"]
    hy0, hy1 = RULER["hull_y"]
    bz0, bz1 = RULER["barrel_z"]
    sz0, sz1 = cfg["slit_z"]
    return {
        "inner_span": round(2 * r_in, 4),
        "hull_rear_clearance": round(r_in - hy1, 4),
        "hull_front_clearance": round(r_in + hy0, 4),
        "muzzle_past_inner_wall": round(-RULER["muzzle_y"] - r_in, 4),
        "muzzle_past_outer_wall": round(-RULER["muzzle_y"] - r_out, 4),
        "gate_width_over_tank": round(cfg["R"] * cfg["reach"] - RULER["width"], 4),
        "slit_below_barrel": round(bz0 - sz0, 4),
        "slit_above_barrel": round(sz1 - bz1, 4),
        "roof_over_turret": round(cfg["wall_h"] - RULER["turret_top"], 4),
    }


# ---------------------------------------------------------------------------
# breaking it: the same box as separate slabs, and a shot through the front
# ---------------------------------------------------------------------------
#
# The board's wall is generated and knocked down in Godot (WallKit/WallFall),
# and the capon will go the same way: this is not the destruction effect, it
# is the *measurement* the Godot solver will be checked against - how many
# pieces, how they lie, whether they stay on the cell. Physics helpers are
# borrowed from brick_wall (arm_physics, hex_fence, pile_report), which take
# name prefixes; the slabs are `Slab.*` so the wall's own counters ignore them.

import random

PIECE = "Slab"

BREAK = {
    "seed": 3,
    "bays": 2,            # pieces along a blank side
    "courses": 2,         # pieces up a blank side
    "jitter": 0.18,       # how far a split wanders from even, fraction of the span
    "gap": 0.004,         # air between slabs, R - touching hulls jitter apart
    "roof_pieces": 6,     # triangles from the centre; concrete breaks big
}


def _shrink(pts, gap):
    """Pull a hexahedron's eight corners toward their centre by half a gap."""
    c = sum(pts, Vector()) / len(pts)
    out = []
    for p in pts:
        d = p - c
        out.append(c + d * max(0.0, 1.0 - gap * 0.5 / max(d.length, 1e-9)))
    return out


def _piece(name, corners, mat, root):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in corners]
    hull = bmesh.ops.convex_hull(bm, input=vs)
    bmesh.ops.delete(bm, geom=[g for g in hull["geom_interior"] if isinstance(g, bmesh.types.BMVert)],
                     context="VERTS")
    bm.to_mesh(me)
    bm.free()
    me.update()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    # origin at the centre: convex-hull bodies are happiest there, and pile
    # measures read translation.z as "where the piece is"
    c = sum((Vector(v.co) for v in me.vertices), Vector()) / len(me.vertices)
    for v in me.vertices:
        v.co -= c
    ob.location = c
    ob.parent = root
    return ob


def _splits(n, rng, jitter):
    """n-1 cut fractions between 0 and 1, evenly spaced then wandered."""
    return [0.0] + sorted((i / n) + rng.uniform(-jitter, jitter) / n
                          for i in range(1, n)) + [1.0]


def fracture(cfg=None, brk=None):
    """The box as separate convex slabs, at the origin, under `Capon.World`.

    Blank sides split into `bays` x `courses`; the slit side keeps its jambs,
    sill and lintel as the pieces they already are (jambs in `courses`); the
    gate side is its lintel in `bays`; the roof is `roof_pieces` triangles
    about the centre, each one falling when the wall under it goes.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    brk = dict(BREAK, **(brk or {}))
    rng = random.Random(brk["seed"])
    gap = brk["gap"]

    for ob in list(bpy.data.objects):
        if ob.name.split(".")[0] == PIECE:
            bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        if me.name.split(".")[0] == PIECE and me.users == 0:
            bpy.data.meshes.remove(me)

    root = bpy.data.objects.get("%s.World" % cfg["name"])
    if root is None:
        root = bpy.data.objects.new("%s.World" % cfg["name"], None)
        bpy.context.scene.collection.objects.link(root)
    root.location = (0.0, 0.0, 0.0)
    mat = concrete(cfg)

    R_out = cfg["R"] * cfg["reach"]
    r_out = math.sqrt(3.0) / 2.0 * R_out
    R_in = (r_out - cfg["wall_t"]) / (math.sqrt(3.0) / 2.0)
    outer, inner = _hexring(R_out), _hexring(R_in)
    H = cfg["wall_h"]
    made = []

    def slab(tag, o0, o1, i0, i1, z0, z1):
        pts = [Vector((p.x, p.y, z)) for z in (z0, z1) for p in (o0, o1, i1, i0)]
        made.append(_piece("%s.%s" % (PIECE, tag), _shrink(pts, gap), mat, root))

    for k in range(6):
        o0, o1 = outer[k], outer[(k + 1) % 6]
        i0, i1 = inner[k], inner[(k + 1) % 6]
        if k == cfg["gate_side"]:
            if cfg["lintel_h"] <= 0.0:
                continue
            u = _splits(brk["bays"], rng, brk["jitter"])
            for b in range(brk["bays"]):
                slab("W%d.Lintel%d" % (k, b), o0.lerp(o1, u[b]), o0.lerp(o1, u[b + 1]),
                     i0.lerp(i1, u[b]), i0.lerp(i1, u[b + 1]), H - cfg["lintel_h"], H)
        elif k == cfg["slit_side"]:
            a = cfg["slit_jamb"]
            z1, z2 = cfg["slit_z"]
            oa, ob_ = o0.lerp(o1, a), o0.lerp(o1, 1.0 - a)
            ia, ib = i0.lerp(i1, a), i0.lerp(i1, 1.0 - a)
            zs = _splits(brk["courses"], rng, brk["jitter"])
            for c in range(brk["courses"]):
                slab("W%d.JambL%d" % (k, c), o0, oa, i0, ia, H * zs[c], H * zs[c + 1])
                slab("W%d.JambR%d" % (k, c), ob_, o1, ib, i1, H * zs[c], H * zs[c + 1])
            slab("W%d.Sill" % k, oa, ob_, ia, ib, 0.0, z1)
            slab("W%d.Lintel" % k, oa, ob_, ia, ib, z2, H)
        else:
            u = _splits(brk["bays"], rng, brk["jitter"])
            zs = _splits(brk["courses"], rng, brk["jitter"])
            for b in range(brk["bays"]):
                for c in range(brk["courses"]):
                    slab("W%d.B%dC%d" % (k, b, c),
                         o0.lerp(o1, u[b]), o0.lerp(o1, u[b + 1]),
                         i0.lerp(i1, u[b]), i0.lerp(i1, u[b + 1]),
                         H * zs[c], H * zs[c + 1])

    n = brk["roof_pieces"]
    centre = Vector((0.0, 0.0, 0.0))
    ring = [Vector((R_out * math.cos(2 * math.pi * i / n), R_out * math.sin(2 * math.pi * i / n), 0.0))
            for i in range(n)]
    for i in range(n):
        tri = (centre, ring[i], ring[(i + 1) % n])
        pts = [Vector((p.x, p.y, z)) for z in (H, H + cfg["roof_t"]) for p in tri]
        made.append(_piece("%s.Roof%d" % (PIECE, i), _shrink(pts, gap), mat, root))

    bpy.context.view_layer.update()
    return {"pieces": len(made), "names": [o.name for o in made]}


def break_test(frames=170, travel=30, lead=12, shot="ram", balls=1, ball=0.30,
               height=0.45, run=1.0, through=1.1, spread=0.5, x=0.0,
               tile_radius=1.0, fence_reach=1.02):
    """One kinematic ball through the slit wall - the HM's concrete-piercer -
    then the pile, measured the way brick_wall.demo_impact measures the wall.

    Distances are fractions of the box: `ball` and `height` of its height,
    `run`/`through` of its depth. The shot goes in along -Y -> +Y, i.e. from
    the camera side through the slit side and out the gate.
    """
    import brick_wall
    scene = bpy.context.scene
    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] == PIECE]
    if not pieces:
        raise RuntimeError("fracture() first")
    if fence_reach:
        brick_wall.hex_fence(radius=tile_radius, reach=fence_reach)
    brick_wall.arm_physics(active_prefixes=(PIECE,))
    _calm(pieces)

    lo = Vector([min(min((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    hi = Vector([max(max((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    span = hi - lo
    rank = brick_wall._burst(lo, hi, shot=shot, balls=balls, ball=ball, height=height,
                             run=run, through=through, spread=spread)
    for ob, start, end in rank:
        start.x += x
        end.x += x
        ob.location = start

    scene.frame_start, scene.frame_end = 1, frames
    scene.rigidbody_world.point_cache.frame_start = 1
    scene.rigidbody_world.point_cache.frame_end = frames
    scene.frame_set(1)
    rest = {ob.name: ob.matrix_world.translation.copy() for ob in pieces}
    drift = 0.0
    for f in range(1, frames + 1):
        t = max(0.0, min(1.0, (f - 1 - lead) / float(travel)))
        for ob, start, end in rank:
            ob.location = start.lerp(end, t)
        scene.frame_set(f)
        if f == lead:
            drift = max((ob.matrix_world.translation - rest[ob.name]).length for ob in pieces)

    moved = sorted(((ob.matrix_world.translation - rest[ob.name]).length, ob.name) for ob in pieces)
    standing = [n for d, n in moved if d < 0.05 and rest[n].z > 0.3 * span.z]
    roof_up = [n for n in rest if "Roof" in n
               and bpy.data.objects[n].matrix_world.translation.z > 0.8 * rest[n].z]
    out = {
        "frames": frames, "travel": travel, "pieces": len(pieces),
        "drift_before_impact": round(drift, 5),
        "moved_over_1cm": sum(1 for d, _ in moved if d > 0.01),
        "max_travel": round(moved[-1][0], 4), "farthest": moved[-1][1],
        "median_travel": round(moved[len(moved) // 2][0], 4),
        "still_standing": standing, "roof_still_up": roof_up,
        "lowest_piece_z": round(min(ob.matrix_world.translation.z for ob in pieces), 4),
        "pile_top": round(max(ob.matrix_world.translation.z for ob in pieces), 4),
        "by_strip_y": [sum(1 for ob in pieces if lo_y <= ob.matrix_world.translation.y < hi_y)
                       for lo_y, hi_y in ((-9, -0.29), (-0.29, 0.29), (0.29, 9))],
    }
    pile = brick_wall.pile_report(tile_radius=tile_radius, reach=fence_reach, names=(PIECE,))
    pile.pop("pile_top")            # counts bricks; ours is above
    out.update(pile)
    return out


def _calm(pieces, lin=0.15, ang=0.30, damp_lin=0.10, damp_ang=0.35):
    """Let a slab that has landed go to sleep.

    Bullet's defaults keep a body awake while it moves faster than 0.4 m/s or
    0.5 rad/s, and a 0.12-thick slab lying on two others never quite gets
    there: the contacts keep nudging it and the picture shivers for the rest of
    the clip. Higher thresholds and some damping put it down. Applied after
    `arm_physics`, which sets the shared numbers."""
    for ob in pieces:
        rb = ob.rigid_body
        if rb is None:
            continue
        rb.deactivate_linear_velocity = lin
        rb.deactivate_angular_velocity = ang
        rb.linear_damping = damp_lin
        rb.angular_damping = damp_ang


def _radial_rank(lo, hi, balls=6, ball=0.18, height=0.40, reach=1.7):
    """The concrete-piercer as it is meant to read: the charge goes off
    *inside*, and every wall is shoved outward at once.

    One kinematic sphere per side, all starting at the centre at `height` of
    the box, each driven out along its side's normal to `reach` of the outer
    inradius. Kinematic bodies do not see each other, so six overlapping at
    the centre is fine, and none touches a wall on frame one (inner inradius
    0.75 against a 0.23 ball). The walls go outward, the roof loses its bearing
    and comes down inside - which is the order a real box fails in."""
    scene = bpy.context.scene
    span = hi - lo
    radius = ball * span.z
    r_out = math.sqrt(3.0) / 2.0 * CONFIG["R"] * CONFIG["reach"]
    z = lo.z + height * span.z
    built = []
    for k in range(balls):
        name = "_Wall.Shell.%d" % k
        ob = bpy.data.objects.get(name)
        if ob is None:
            me = bpy.data.meshes.new(name)
            bm = bmesh.new()
            bmesh.ops.create_icosphere(bm, subdivisions=2, radius=radius)
            bm.to_mesh(me)
            bm.free()
            ob = bpy.data.objects.new(name, me)
            scene.collection.objects.link(ob)
        ob.hide_render = True
        ob.display_type = "WIRE"
        ob.animation_data_clear()
        ang = math.radians(30.0 + 360.0 * k / balls)
        n = Vector((math.cos(ang), math.sin(ang), 0.0))
        start = Vector((0.0, 0.0, z))
        end = n * (r_out * reach) + Vector((0.0, 0.0, z))
        ob.location = start
        bpy.context.view_layer.update()
        if ob.name not in scene.rigidbody_world.collection.objects:
            scene.rigidbody_world.collection.objects.link(ob)
        if ob.rigid_body is None:
            prev = bpy.context.view_layer.objects.active
            bpy.context.view_layer.objects.active = ob
            bpy.ops.rigidbody.object_add()
            bpy.context.view_layer.objects.active = prev
        ob.rigid_body.type = "PASSIVE"
        ob.rigid_body.kinematic = True
        ob.rigid_body.collision_shape = "SPHERE"
        built.append((ob, start, end))
    return built


def rig_break(frames=170, travel=30, lead=12, shot="burst", balls=6, ball=0.18,
              height=0.40, run=1.0, through=1.1, spread=0.5, reach=1.7, bake=True,
              tile_radius=1.0, fence_reach=None):
    """Leave the break scene ready to press Space: the shell keyframed, the
    cache baked. Same shot as `break_test`; that one drives the ball from
    Python and shows nothing when Blender plays. brick_wall.rig_break has the
    argument."""
    import brick_wall
    scene = bpy.context.scene
    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] == PIECE]
    if not pieces:
        raise RuntimeError("fracture() first")
    if fence_reach:
        brick_wall.hex_fence(radius=tile_radius, reach=fence_reach)
    brick_wall.arm_physics(active_prefixes=(PIECE,))
    _calm(pieces)
    lo = Vector([min(min((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    hi = Vector([max(max((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    if shot == "burst":
        rank = _radial_rank(lo, hi, balls=balls, ball=ball, height=height, reach=reach)
    else:
        rank = brick_wall._burst(lo, hi, shot=shot, balls=balls, ball=ball, height=height,
                                 run=run, through=through, spread=spread)
    for ob, start, end in rank:
        for frame, pos in ((1, start), (lead, start), (lead + travel, end)):
            ob.location = pos
            ob.keyframe_insert("location", frame=frame)
        ob.location = start
        for fc in brick_wall._fcurves(ob.animation_data.action):
            for kp in fc.keyframe_points:
                kp.interpolation = "LINEAR"
    scene.frame_start, scene.frame_end = 1, frames
    scene.rigidbody_world.point_cache.frame_start = 1
    scene.rigidbody_world.point_cache.frame_end = frames
    scene.frame_set(1)
    baked = False
    if bake:
        try:
            with bpy.context.temp_override(scene=scene):
                bpy.ops.ptcache.bake_all(bake=True)
            baked = scene.rigidbody_world.point_cache.is_baked
        except RuntimeError:
            baked = False
    scene.frame_set(1)
    return {"frames": frames, "baked": baked, "ball_starts": lead, "ball_stops": lead + travel}


def record(folder, every=3, size=320, azimuth=0.0, elevation=30.0, padding=1.15, samples=16):
    """Render the baked break to numbered PNGs through the project camera rig,
    framed once on the standing box so the pile does not swim as it spreads.
    Assemble the GIF outside Blender (PIL is not in its Python)."""
    import os
    import sprite_atlas
    scene = bpy.context.scene
    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] == PIECE]
    scene.frame_set(1)
    lo = Vector([min(min((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    hi = Vector([max(max((ob.matrix_world @ Vector(c))[i] for c in ob.bound_box) for ob in pieces) for i in range(3)])
    lo.z = min(lo.z, -0.1)
    pivot = (lo + hi) * 0.5
    radius = (hi - lo).length * 0.5

    for name in ("_preview_cam", "_atlas_key", "_atlas_fill", "_atlas_rim"):
        ob = bpy.data.objects.get(name)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)
    data = bpy.data.cameras.new("_preview_cam")
    data.type = "ORTHO"
    data.ortho_scale = radius * 2.0 * padding
    data.clip_start, data.clip_end = 0.01, 100.0
    cam = bpy.data.objects.new("_preview_cam", data)
    scene.collection.objects.link(cam)
    cam.matrix_world = sprite_atlas.camera_matrix(pivot, azimuth, elevation, radius * 8.0)
    lights = sprite_atlas.build_lighting(
        {"light_rig": None, "key_energy": 4.0, "fill_energy": 1.2, "rim_energy": 2.0}, cam)
    scene.camera = cam
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "Standard"
    try:
        scene.eevee.taa_render_samples = samples
    except AttributeError:
        pass
    world = scene.world or bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs["Color"].default_value = (0.35, 0.36, 0.38, 1.0)
        bg.inputs["Strength"].default_value = 1.0

    os.makedirs(folder, exist_ok=True)
    written = []
    for f in range(scene.frame_start, scene.frame_end + 1, every):
        scene.frame_set(f)
        path = os.path.join(folder, "f%04d.png" % f)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        written.append(path)
    for ob in [cam] + list(lights):
        bpy.data.objects.remove(ob, do_unlink=True)
    scene.frame_set(1)
    return {"frames": len(written), "folder": folder}


if __name__ == "__main__":
    print(build()["report"])
