"""A ruined brick wall, modelled brick by brick.

The picture this answers is `Images/raw/wall.png`: a stepped stub of masonry,
tall on the right and running down to two courses on the left, with a pale
stone shard leaning against its front and an apron of chips and pebbles round
the foot.

It is built as **one object per brick**, and that is the whole design. A wall
that is going to be knocked down cannot be a single mesh with a brick texture
on it: rigid-body simulation moves *objects*, so anything that has to fall
apart has to exist apart. Sixty-odd objects of ~200 verts is nothing next to
the tanks in this project (one hull is 929k verts), so the usual reason to
merge - cost - does not apply.

Three consequences follow, and they are why the rest of the file looks the way
it does.

**The material is procedural, in object space.** Breaking a brick exposes
faces that were never part of its surface, and a UV-baked texture has nothing
to put there: the inside of the brick has to be *defined*, not painted. Object
space rather than world space because the pattern has to travel with the brick
when it falls - a world-space pattern would swim as the wall collapses, which
is exactly the frame you were going to look at.

**What varies per brick is stored on the brick, not derived from where it
is.** Tone and mossiness go into `object.color` at build time and come back
through the Object Info node. Deriving moss from world height reads the same
today and is wrong the instant the wall moves: every falling brick would grow
and shed moss in mid-air.

**Grass is not here, and that is deliberate.** The reference has tufts at the
foot; they belong to the ground tile, because grass that falls over when the
wall is pushed is worse than no grass. Its colour is in the wall as moss on
the bottom courses, which is the half that does belong to it.

Everything is jittered from a seed, so the same seed is the same wall. Nothing
here is random at render time.

Entry points
------------
`build()`         - the wall. Returns what it made and the numbers to judge it by.
`arm_physics()`   - passive/active rigid bodies plus a floor, so you can press play.
`fit_to_hex()`    - size it to a board cell and lay that cell under it.
`hex_fence()`     - make that cell a boundary the debris cannot cross.
`preview(path)`   - one render through the project's own camera and light rig.
"""

import math
import os
import random

import bmesh
import bpy
from mathutils import Euler, Vector

# ---------------------------------------------------------------------------
# configuration
# ---------------------------------------------------------------------------

CONFIG = {
    "seed": 11,
    "root": "Wall.World",

    # Multiplies every length in this file and in the three tables below, so
    # the ruin keeps its proportions and changes its size. It exists because
    # the prop has to fit a board cell, and the board states its sizes as
    # fractions of the hex circumradius - see HEX_TILES.md, which is emphatic
    # that nothing here should be a pixel or an absolute metre. `fit_to_hex`
    # measures the fraction and sets this; 1.0 is the authored size.
    #
    # The number standing here is the one that fits a cell of R = 1 at 94%
    # coverage, which is what `Scenes/WALL_scene.blend` holds. It is written down
    # rather than left at 1.0 so that a plain `build()` reproduces the shipped
    # wall: a default that quietly rebuilds the prop 29% too big for its cell
    # is the kind of knob nobody remembers until a render comes out wrong.
    #
    # A scale rather than an object scale on `Wall.World`: the pieces are
    # rigid bodies, and a scaled parent is the same trap as a moved one - the
    # simulation writes world transforms and the parent's matrix goes on top.
    # Scaling at build time leaves every object at unit scale.
    "scale": 0.82275,

    # Slide the finished prop so its plan bounding box sits on the origin.
    # On by default because the thing is a cell's worth of scenery and a cell
    # has a centre; and because the fit is set by whichever piece pokes out
    # furthest, so an off-centre prop pays for the clearance it wastes on the
    # other side - centring first took the fitted scale from 0.778 to 0.819.
    "centre": True,

    # Brick in metres-ish. The art brick is chunkier than a real one - about
    # 2 : 1 : 1 rather than 4 : 2 : 1 - and the whole ruin has to sit inside a
    # hex cell next to a tank, whose turning circle is ~1.55 across.
    "brick": (0.26, 0.13, 0.12),      # length X, depth Y, height Z
    # The joint is not decoration: it is the budget the jitter spends.
    #
    # Two neighbours can approach each other by `2 * jitter_pos` and each grow
    # by `jitter_size * half_extent`, so the joint has to be wider than the sum
    # or the built wall self-intersects. That is invisible in a render and
    # fatal in a simulation - Bullet resolves deep penetration with a large
    # impulse, so the wall explodes on frame one instead of standing there.
    # Measured before this was fixed: up to 18 mm of overlap on the Y axis,
    # and the first `demo_impact` run put bricks 26 units under the floor.
    #
    # `build()` reports `worst_overlap` for exactly this: across a course it
    # must stay negative, and it must never pass the 2 mm collision margin.
    # Do not raise the jitters without raising the joint.
    # ...but only across the courses. Up the wall the bricks have to *touch*.
    #
    # The joint was applied on all three axes and that is what made the stack
    # collapse the moment physics was allowed to run: a brick held 15 mm above
    # the one below is not resting on it, it is hovering, and forty-one of
    # those released together is a heap. Settling it under gravity was tried
    # and it is not a small correction - the wall lost its shape entirely.
    #
    # So the bed joint is zero and the perpend joint is `gap`. Nothing bears on
    # a perpend, so a gap there is free and it is what draws the dark line
    # between bricks; everything bears on the bed.
    "gap": 0.018,                     # perpend joint, across the course only
    "bevel": 0.014,                   # rounded arris; this is what reads as "brick"
    "bevel_segments": 2,

    # How far a brick may wander from its slot. Small: past ~0.02 the courses
    # stop reading as courses and the thing looks like a pile.
    # Z is zero and the tilt is zero *for bricks in the wall*, for the same
    # reason the bed joint is: both lift one end of a brick off the course
    # below. A 1.6 degree tilt on a 216 mm brick is 3 mm of daylight at one end
    # and 3 mm of interpenetration at the other. Yaw is free - turning a brick
    # about the vertical does not change what it stands on - and so is the
    # per-vertex jitter, which is smaller than the contact tolerance.
    "jitter_pos": (0.004, 0.008, 0.0),
    "jitter_yaw": 3.0,                # degrees about Z
    "jitter_tilt": 0.0,               # degrees about X and Y
    "jitter_size": 0.022,             # fraction, length and depth only
    "jitter_vert": 0.0035,            # per-vertex in plan; see `_brick_mesh`
    "chip_chance": 0.45,              # fraction of bricks that lose a corner

    # The wall is two bricks thick at the bottom and one at the top.
    #
    # It was one leaf throughout for three passes and read as a screen rather
    # than as masonry: what gives the reference its mass is that the top of
    # every step shows *two* rows of top faces, so the eye is told the
    # thickness without being shown a section. The back leaf is dropped above
    # `back_courses`, which is also where the reference stops having one - the
    # crest is single bricks.
    #
    # `back_keep` is the fraction of it that survived. Below about 0.6 the
    # gaps start lining up with the front leaf's and you can see daylight
    # through the wall, which is a different building.
    "back_courses": 5,
    "back_keep": 0.78,
    "back_shift": 0.5,                # half a pitch, so the two leaves break bond
    # 2.4 and not 1.6, because the leaves are jittered *and* rotated: a
    # brick yawed 3 degrees grows its own depth by 7 mm, both leaves do it, and
    # at 1.6 the two boxes met with 4 mm to spare - inside Bullet's margin.
    "back_clear": 2.4,                # extra joint between the leaves, in `gap`

    # Moss fades out with height. Measured in courses, not in metres, so it
    # survives a change of brick size.
    "moss_courses": 2.6,

    # The apron is a *band along the foot*, not a scatter over a disc. First
    # pass used a disc and it read as a wall with objects lying near it: what
    # says "this fell off that" is debris touching the base, and the reference
    # keeps almost all of it within half a brick of the face.
    # `spread` and `depth` are measured *from the wall's faces outwards*, and
    # `stand_off` is the least clear air between a piece and the masonry.
    #
    # They used to be measured from the origin, and that was wrong in the way
    # that matters: the front leaf's face stands at D/2 + jitter = 73 mm, so a
    # chip placed 95 mm out with a 70 mm radius reached 25 mm *into* the bottom
    # course. It looked like debris against the wall and it was debris inside
    # it - `worst_overlap` reported 44 mm and the simulation threw the pieces
    # 55 units. A face is something a piece can lie against; a centreline is
    # not.
    # `front` is high because the back of the wall is behind the wall at every
    # heading the camera has: rubble put there is rubble nobody will see, and
    # it still costs the prop depth.
    "rubble": {"pebbles": 8, "chips": 6, "spread": 0.17, "depth": 0.22,
               "band": 0.90, "front": 0.88, "stand_off": 0.010},

    # Linear, and every one of these was fitted by measuring the render's
    # median against the reference's, not by eye.
    #
    # <b>Take the ink outline out of the reference before comparing, or you
    # will darken the brick to match a line.</b> 42.9% of that PNG's opaque
    # pixels are near-black outline, which drags its median down to
    # (113, 79, 50) and makes any render look washed out - three passes here
    # were spent chasing that. Excluding it (max channel >= 90) the reference
    # brick body is (125, 91, 60) and this material renders (124, 82, 64):
    # the brightness was right all along and only the hue was off, by G x1.26
    # and B x0.87 in linear, which is what these numbers now carry.
    #
    # The outline itself is not a material problem and must not be solved
    # here. If the sprites want it, it belongs to the layer that draws them -
    # the Godot stand already runs per-layer shaders - because an outline is a
    # function of the silhouette, and the material cannot see one.
    #
    # `dark` and `light` are far apart on purpose: brick-to-brick contrast is
    # most of what makes a painted wall read as courses rather than as one
    # extruded block.
    #
    # The stone is warm, not neutral. Measured on the shard itself (a colour
    # filter finds two pixels of it, because the outline eats the rest):
    # (133, 119, 97), ratio 1 : 0.89 : 0.73. The first pass rendered
    # (158, 157, 151) - lighter and flat grey, which is why it read as quartz.
    "colours": {
        "brick_dark":  (0.173, 0.060, 0.024),
        "brick_light": (0.372, 0.158, 0.059),
        "brick_pale":  (0.360, 0.268, 0.172),   # the grey-tan one, upper left
        "brick_wear":  (0.438, 0.258, 0.094),   # chipped arris, exposed grog
        "stone":       (0.325, 0.258, 0.180),
        "stone_wear":  (0.442, 0.352, 0.245),
        "moss":        (0.140, 0.185, 0.045),
    },
}

# Courses, bottom up. Each entry is an x-centre in brick pitches, or a dict
# overriding it. Written in continuous pitches rather than as slots-plus-offset
# because the ragged end is the point: a running bond expressed as an offset
# gives a clean staircase, and a ruin has not got one.
#
# `tone` 0..1 picks along dark -> light; `pale` swaps in the grey-tan brick.
#
# `yaw90` turns a header: a brick laid across the wall instead of along it, so
# it spans both leaves and ties them together. That is what a header is for in
# real masonry, and here it is also the only placement that fits - a header
# turned inside one leaf reaches 260 mm through a 130 mm leaf and lands in the
# other one. `worst_overlap` caught it at 36 mm and the back-leaf brick behind
# each header now gives way to it, the way the bond does.
#
# Seven courses over five bricks, not eight over six: the block in the
# reference is about 1.4 wide to 1 tall, and the first pass came out a wide
# low pyramid at 2.3 : 1. What fixes that is taking width off, not adding
# height - the stack has to be steep for the steps to read as steps.
#
# **Every course ends at the same x and starts further right than the one
# below: the break is a right triangle, not a pyramid.** This is the thing the
# silhouette is actually made of, and two passes missed it. Shortening both
# ends by the same amount reads as a heap someone piled up; a wall that broke
# has one ragged diagonal and one nearly vertical edge, because the vertical
# one is where the masonry parted along a joint. Everything else here - moss,
# chips, the pale brick - is detail on top of that one shape.
#
# `tip` leans a brick along the wall and `lean` tips it out of the face, both
# in degrees, and the brick is raised by whatever the angle digs into the
# course below. Only the top two courses get them: a brick that has visibly
# slid is what says the wall is falling rather than merely short, and one that
# has slid *low down* says the courses beneath it are holding nothing up.
#
# Small angles, and that is physics rather than taste: a brick propped at an
# angle on a flat course is resting on one edge, so the first frame of any
# simulation rocks it flat. At 2-3 degrees the drop is a millimetre or two.
#
# **And only on a brick with nothing on top of it.** The lift that keeps a
# tilted brick out of the course below raises its far corner by the same
# amount, into whatever is above - 9.3 mm, the last overlapping pair in the
# build. The crest is the one place a brick can lean without pushing on
# anything.
COURSES = [
    [-2.05, -1.05, -0.05, 0.95, 1.95],
    [-1.55, -0.55, {"x": 0.45, "tone": 0.95}, 1.45, {"x": 2.30, "yaw90": True}],
    [-1.05, -0.05, {"x": 0.95, "pale": True}, 1.95],
    [-0.58, 0.42, 1.40, {"x": 2.25, "yaw90": True}],
    [-0.05, 0.95, 1.95],
    [{"x": 0.50, "pale": True}, 1.95],   # the notch at the top is this gap
    [{"x": 1.45, "tip": 3.0, "lean": -2.0}],
]

# Bricks that never made it into a course: knocked off the top, or shoved out
# of the bottom. Position in metres, rotation in degrees.
#
# **These are hand-placed, so they are the one part of the layout that does not
# know where the wall is.** They were authored against a single-leaf wall and
# stayed put when the back leaf arrived, which put two of them inside it - 63
# mm, the worst overlap in the build. A rotated brick is also wider than a
# brick: at 71 degrees this one spans 288 mm across Y, not 130, so "clear of
# the face" has to be worked out for the angle it is lying at. Move one of
# these and read `worst_overlap` afterwards.
FALLEN = [
    {"pos": (0.88, -0.30, 0.047), "rot": (0, 4, 24)},
    {"pos": (-0.70, -0.27, 0.047), "rot": (3, -6, -37)},
    {"pos": (1.02, 0.02, 0.047), "rot": (0, 0, 71)},      # past the right end
    {"pos": (-0.34, -0.35, 0.070), "rot": (88, 0, 12)},   # stood on its edge
]

# The boulders. Sizes are the full extent of each; they are sunk so the flat
# bottom is under the ground plane and they read as coming out of it.
#
# The front one is deliberately about half the height of the wall. It is the
# second thing the reference is *about* - the pale shard against the red - and
# at the first pass's 0.44 it read as another lump of rubble.
#
# **Broad and blunt, and this took a measurement to get right for the reason
# the measurement is worth keeping.** These read as pale crystals for four
# passes, so the colour kept getting darkened; when it was finally measured
# against the shard in the reference it was already there - (126, 111, 93)
# against (133, 119, 97), ratio 1 : 0.88 : 0.74 against 1 : 0.89 : 0.73. It
# was never the colour. A stone half as wide as it is tall, brought to a
# point, is a crystal at any colour, and the fix is 0.44 wide with a taper of
# 0.10.
STONES = [
    {"pos": (0.00, -0.225, 0.190), "size": (0.44, 0.27, 0.46), "seed": 3,
     "sink": 0.05, "taper": 0.10},
    {"pos": (0.300, -0.215, 0.105), "size": (0.26, 0.21, 0.30), "seed": 8,
     "sink": 0.04, "taper": 0.14},
    {"pos": (-0.52, -0.165, 0.052), "size": (0.27, 0.22, 0.19), "seed": 15,
     "sink": 0.035, "taper": 0.08},
]


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

def _finish(bm, name):
    """Recalculate normals, centre the mesh on its origin, hand back the shift.

    **Centring is not tidiness: a rigid body spins about its object origin.**
    A hull comes out centred on its sample cloud, and then the flat base and
    the taper move the geometry without moving the origin - measured up to 20
    mm out on the boulders. Left alone, an active piece would rotate about a
    point outside itself, which reads as the stone being flicked rather than
    knocked. `_seat` puts the shift back into the object's location, so
    nothing moves in the world; only the pivot does.
    """
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    lo = Vector(tuple(min(v.co[i] for v in bm.verts) for i in range(3)))
    hi = Vector(tuple(max(v.co[i] for v in bm.verts) for i in range(3)))
    shift = (lo + hi) * 0.5
    if shift.length > 1e-9:
        for v in bm.verts:
            v.co -= shift
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    return me, shift


def _brick_mesh(name, dims, cfg, rng, chip):
    """A bevelled box, jittered. Flat-shaded, like everything else here."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * dims[0], v.co.y * dims[1], v.co.z * dims[2]))

    bmesh.ops.bevel(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                    offset=min(cfg["bevel"], min(dims) * 0.30),
                    segments=cfg["bevel_segments"], profile=0.55,
                    affect="EDGES", clamp_overlap=True)

    if chip:
        # Take a corner off with one plane. A chipped brick reads as broken; a
        # bevelled one only reads as old, and the reference has both.
        sx, sy, sz = (rng.choice((-1.0, 1.0)) for _ in range(3))
        no = Vector((sx * rng.uniform(0.5, 1.0),
                     sy * rng.uniform(0.3, 1.0),
                     sz * rng.uniform(0.5, 1.0))).normalized()
        corner = Vector((sx * dims[0] * 0.5, sy * dims[1] * 0.5, sz * dims[2] * 0.5))
        # A fraction of the brick, never a length in metres. This was
        # `uniform(0.012, 0.042)` and it was the one absolute size left in the
        # file, which made `scale` stop being a similarity: a fixed bite out of
        # a smaller brick lands somewhere else in the bevel, the cut fills with
        # a different number of vertices, the per-vertex jitter loop draws a
        # different number of times, and every random decision after it in the
        # build shifts. Measured: the wall came out with 38 bricks at scale
        # 0.778 against 37 at 1.0, and `worst_overlap` went from -0.9 mm to
        # +11 mm - a different wall, not a smaller one.
        co = corner - no * rng.uniform(0.10, 0.35) * min(dims)
        res = bmesh.ops.bisect_plane(
            bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
            dist=1e-5, plane_co=co, plane_no=no,
            clear_outer=True, clear_inner=False)
        cut = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
        if cut:
            bmesh.ops.holes_fill(bm, edges=cut)

    # In plan only. A brick's bed and top faces are what it stands on and what
    # the next course stands on, so they have to stay flat and exactly H apart:
    # 2.9 mm of jitter on each makes the brick 5.8 mm taller than nominal, and
    # with a zero bed joint that is 5.8 mm of interpenetration with the course
    # above. Measured before this: 48 overlapping pairs, every one of them a
    # vertical neighbour, every one about 5 mm.
    j = cfg["jitter_vert"]
    for v in bm.verts:
        v.co += Vector((rng.uniform(-j, j), rng.uniform(-j, j), 0.0))

    return _finish(bm, name)          # (mesh, shift) - see `_finish`


def _rock_mesh(name, size, seed, points=24, flat_base=True, taper=0.0):
    """Convex hull of a few random points in an ellipsoid.

    A hull is the right primitive for these: the reference stones are chipped,
    not weathered - large flat planes meeting at hard arrises - and that is
    exactly what a hull of a dozen points is. A displaced sphere gives potatoes.

    **The point count and the radius spread are what decide boulder or
    crystal.** Nineteen points over 0.80..1.0 of the radius gives a blocky
    lump; thirteen over 0.62..1.0 gave cones - few points with deep dents means
    the hull is mostly a handful of long triangles, and squashing that into a
    tall ellipsoid makes a pyramid. It came out as quartz shards standing in
    front of the bricks, which is not a thing the reference has. Twenty-four
    points and a shallow taper get to the reference's shape, which is a blunt
    broken slab and not a crystal.
    """
    rng = random.Random(seed)
    bm = bmesh.new()
    for _ in range(points):
        d = Vector((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1)))
        if d.length < 1e-6:
            d = Vector((1.0, 0.0, 0.0))
        d.normalize()
        d *= rng.uniform(0.80, 1.0)
        bm.verts.new((d.x * size[0] * 0.5, d.y * size[1] * 0.5, d.z * size[2] * 0.5))
    bm.verts.ensure_lookup_table()
    bmesh.ops.convex_hull(bm, input=bm.verts, use_existing_faces=False)
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context="VERTS")
    # Merge the near-coplanar triangles the hull leaves behind, so the facets
    # are as big as they look in the art.
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(7.0),
                             verts=bm.verts[:], edges=bm.edges[:])

    if taper:
        # Narrow it towards the top. Without this a hull is a lump; the big
        # stone in the reference is a broken slab standing on end, and the
        # thing that says so is that it comes to a point.
        lo = min(v.co.z for v in bm.verts)
        hi = max(v.co.z for v in bm.verts)
        span = max(hi - lo, 1e-6)
        for v in bm.verts:
            k = 1.0 - taper * ((v.co.z - lo) / span) ** 0.85
            v.co.x *= k
            v.co.y *= k

    if flat_base:
        lo = min(v.co.z for v in bm.verts)
        for v in bm.verts:
            if v.co.z < lo + size[2] * 0.08:
                v.co.z = lo
        bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-5)

    return _finish(bm, name)


# ---------------------------------------------------------------------------
# materials
# ---------------------------------------------------------------------------

def _sock(node, name, kind=None):
    for s in node.inputs:
        if s.name == name and (kind is None or s.type == kind):
            return s
    raise KeyError("%s has no input %r" % (node.bl_idname, name))


def _mix(nt, fac, a, b, blend="MIX"):
    """A colour mix, addressed by socket type rather than by index.

    `ShaderNodeMix` shows a different set of sockets per `data_type` and the
    indices have moved between releases; the two RGBA inputs have not.
    """
    n = nt.nodes.new("ShaderNodeMix")
    n.data_type = "RGBA"
    n.blend_type = blend
    cols = [s for s in n.inputs if s.type == "RGBA"]
    f = _sock(n, "Factor", "VALUE")
    if isinstance(fac, float):
        f.default_value = fac
    else:
        nt.links.new(fac, f)
    for socket, val in ((cols[0], a), (cols[1], b)):
        if hasattr(val, "is_output"):
            nt.links.new(val, socket)
        else:
            socket.default_value = (val[0], val[1], val[2], 1.0)
    return n.outputs["Result"]


def _range(nt, value, lo, hi, out_lo=0.0, out_hi=1.0):
    n = nt.nodes.new("ShaderNodeMapRange")
    n.clamp = True
    nt.links.new(value, _sock(n, "Value"))
    _sock(n, "From Min").default_value = lo
    _sock(n, "From Max").default_value = hi
    _sock(n, "To Min").default_value = out_lo
    _sock(n, "To Max").default_value = out_hi
    return n.outputs["Result"]


def _noise(nt, vector, scale, detail=4.0, roughness=0.6):
    n = nt.nodes.new("ShaderNodeTexNoise")
    nt.links.new(vector, _sock(n, "Vector"))
    _sock(n, "Scale").default_value = scale
    _sock(n, "Detail").default_value = detail
    _sock(n, "Roughness").default_value = roughness
    return n.outputs["Fac"]


def _mul(nt, a, b):
    n = nt.nodes.new("ShaderNodeMath")
    n.operation = "MULTIPLY"
    for i, val in enumerate((a, b)):
        if hasattr(val, "is_output"):
            nt.links.new(val, n.inputs[i])
        else:
            n.inputs[i].default_value = val
    return n.outputs["Value"]


def _shell(name):
    mat = bpy.data.materials.get(name)
    if mat:
        bpy.data.materials.remove(mat)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    _sock(bsdf, "Metallic").default_value = 0.0
    info = nt.nodes.new("ShaderNodeObjectInfo")
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    tex = nt.nodes.new("ShaderNodeTexCoord")
    return mat, nt, bsdf, info, geo, tex.outputs["Object"]


def _moss(nt, info, geo, obj, base, colour):
    """Moss: how much on this object, times where on it.

    The amount is baked per object (`object.color.r`); the placement is local -
    upward faces and the hollows between bricks, broken up by noise. Split that
    way because the amount is a fact about the wall as it was built and the
    placement is a fact about the brick, and only the first one stops being
    true when the brick falls.
    """
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(info.outputs["Color"], sep.inputs["Color"])
    up = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Normal"], up.inputs["Vector"])
    facing = _range(nt, up.outputs["Z"], -0.10, 0.60, 0.15, 1.0)
    hollow = _range(nt, geo.outputs["Pointiness"], 0.52, 0.42, 0.55, 1.0)
    patch = _range(nt, _noise(nt, obj, 22.0), 0.46, 0.64)
    m = _mul(nt, sep.outputs["Red"], patch)
    m = _mul(nt, m, facing)
    m = _mul(nt, m, hollow)
    return _mix(nt, _mul(nt, m, 0.75), base, colour), m


def brick_material(cfg):
    c = cfg["colours"]
    mat, nt, bsdf, info, geo, obj = _shell("Brick")

    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(info.outputs["Color"], sep.inputs["Color"])

    tone = _mix(nt, sep.outputs["Green"], c["brick_dark"], c["brick_light"])
    tone = _mix(nt, sep.outputs["Blue"], tone, c["brick_pale"])

    mottle = _noise(nt, obj, 28.0, detail=5.0)
    base = _mix(nt, _range(nt, mottle, 0.34, 0.66, 0.0, 0.55), tone, (0.0, 0.0, 0.0),
                blend="MULTIPLY")
    base = _mix(nt, _range(nt, _noise(nt, obj, 95.0, detail=6.0), 0.40, 0.62, 0.0, 0.22),
                base, c["brick_wear"])

    # Chipped arris: convex edges lose their skin. `Pointiness` is what the
    # bevel is for - without a bevel every edge is one pixel wide and there is
    # nothing for the wear to sit on.
    # 0.42 rather than 0.62: at 0.62 the pale wear dusted every face, not just
    # the arrises, and the wall came out pink. It is an edge effect or it is
    # nothing.
    wear = _mul(nt, _range(nt, geo.outputs["Pointiness"], 0.52, 0.61),
                _range(nt, _noise(nt, obj, 55.0), 0.30, 0.70, 0.35, 1.0))
    base = _mix(nt, _mul(nt, wear, 0.42), base, c["brick_wear"])

    base, _ = _moss(nt, info, geo, obj, base, c["moss"])
    nt.links.new(base, _sock(bsdf, "Base Color"))
    nt.links.new(_range(nt, mottle, 0.3, 0.7, 0.94, 0.76), _sock(bsdf, "Roughness"))
    return mat


def stone_material(cfg):
    c = cfg["colours"]
    mat, nt, bsdf, info, geo, obj = _shell("Stone")

    mottle = _noise(nt, obj, 16.0, detail=6.0)
    base = _mix(nt, _range(nt, mottle, 0.34, 0.66, 0.0, 0.52), c["stone"], (0.0, 0.0, 0.0),
                blend="MULTIPLY")
    base = _mix(nt, _range(nt, _noise(nt, obj, 70.0, detail=7.0), 0.44, 0.60, 0.0, 0.30),
                base, c["stone_wear"])
    # Stone reads paler on its arrises than brick does, and harder: the facets
    # are big, so the highlight has somewhere to run. 0.42 and not 0.80 - at
    # 0.80 a hull whose every edge is an arris went white all over, and the
    # boulders read as quartz rather than as the grey stone in the reference.
    base = _mix(nt, _range(nt, geo.outputs["Pointiness"], 0.48, 0.62, 0.0, 0.42),
                base, c["stone_wear"])

    base, _ = _moss(nt, info, geo, obj, base, c["moss"])
    nt.links.new(base, _sock(bsdf, "Base Color"))
    nt.links.new(_range(nt, mottle, 0.3, 0.7, 0.88, 0.66), _sock(bsdf, "Roughness"))
    return mat


# ---------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------

PREFIXES = ("Brick", "Fallen", "Stone", "Pebble", "Chip")


def _seat(name, me, shift, pos, rot):
    """Place a re-centred mesh so the geometry lands where it was going to.

    **The shift has to be rotated before it is added back.** The object
    transform is `T(loc) . R . v`, so moving the vertices in by `shift` and the
    location out by `shift` leaves `(I - R) . shift` of error - zero only while
    R is identity, which for a jittered wall it never is. Written the naive way
    this moved 11 122 pixels of the render, which is how it was caught: the
    whole point of re-centring is that the picture must not change.
    """
    rot_rad = tuple(math.radians(a) for a in rot)
    ob = bpy.data.objects.new(name, me)
    ob.rotation_euler = rot_rad
    ob.location = Vector(pos) + (Euler(rot_rad).to_matrix() @ shift)
    return ob


def _link(ob, root):
    bpy.context.scene.collection.objects.link(ob)
    ob.parent = root


def _wipe(cfg):
    for ob in list(bpy.data.objects):
        if ob.name == cfg["root"] or ob.name.split(".")[0] in PREFIXES:
            bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        if me.users == 0:
            bpy.data.meshes.remove(me)


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    _wipe(cfg)
    rng = random.Random(cfg["seed"])

    sc = cfg["scale"]
    cfg = dict(cfg,
               brick=tuple(v * sc for v in cfg["brick"]),
               gap=cfg["gap"] * sc,
               bevel=cfg["bevel"] * sc,
               jitter_pos=tuple(v * sc for v in cfg["jitter_pos"]),
               jitter_vert=cfg["jitter_vert"] * sc,
               rubble=dict(cfg["rubble"],
                           spread=cfg["rubble"]["spread"] * sc,
                           depth=cfg["rubble"]["depth"] * sc,
                           stand_off=cfg["rubble"]["stand_off"] * sc))
    L, D, H = cfg["brick"]
    pitch_x = L + cfg["gap"]
    pitch_z = H                      # bed joint zero: the courses bear on each other

    brick_mat = brick_material(cfg)
    stone_mat = stone_material(cfg)

    root = bpy.data.objects.new(cfg["root"], None)
    root.empty_display_type = "PLAIN_AXES"
    root.empty_display_size = 0.25
    bpy.context.scene.collection.objects.link(root)

    made = {"bricks": [], "stones": [], "rubble": []}

    def place_brick(name, pos, rot, dims, moss, tone, pale=0.0):
        me, shift = _brick_mesh(name, dims, cfg, rng,
                                rng.random() < cfg["chip_chance"])
        ob = _seat(name, me, shift, pos, rot)
        ob.data.materials.append(brick_mat)
        ob.color = (moss, tone, pale, 1.0)
        _link(ob, root)
        return ob

    # --- the courses -------------------------------------------------------
    thickness = D + cfg["back_clear"] * cfg["gap"]      # leaf centre to leaf centre
    n = 0
    for ci, course in enumerate(COURSES):
        z = pitch_z * (ci + 0.5)
        moss = max(0.0, 1.0 - (ci + 0.5) / cfg["moss_courses"]) ** 1.5
        headers = [(e["x"] if isinstance(e, dict) else e) for e in course
                   if isinstance(e, dict) and e.get("yaw90")]
        for entry in course:
            spec = entry if isinstance(entry, dict) else {"x": entry}
            s = 1.0 + rng.uniform(-cfg["jitter_size"], cfg["jitter_size"])
            # Height exact. Two per cent off a brick is 2.6 mm, and 2.6 mm is
            # either a gap under the next course or a bite out of it; the
            # courses have to add up, and of the three dimensions height is the
            # one nobody reads.
            dims = (L * s * spec.get("len", 1.0), D * s, H)
            yaw = 90.0 if spec.get("yaw90") else 0.0
            jp = cfg["jitter_pos"]
            # A tilted brick has to be lifted by what the tilt digs in, or it
            # ends up inside the course below - 12 mm at 6.5 degrees on a
            # 216 mm brick. The old 15 mm bed joint hid this; with the courses
            # bearing on each other there is nothing to hide it in.
            lean = math.radians(spec.get("lean", 0.0))
            tip = math.radians(spec.get("tip", 0.0))
            lift = (abs(math.sin(tip)) * dims[0] * 0.5
                    + abs(math.sin(lean)) * dims[1] * 0.5
                    + dims[2] * 0.5 * (abs(math.cos(tip) * math.cos(lean)) - 1.0))
            pos = (spec["x"] * pitch_x + rng.uniform(-jp[0], jp[0]),
                   rng.uniform(-jp[1], jp[1])
                   + (thickness * 0.5 if spec.get("yaw90") else 0.0),
                   z + lift + rng.uniform(-jp[2], jp[2]))
            rot = (spec.get("lean", 0.0)
                   + rng.uniform(-cfg["jitter_tilt"], cfg["jitter_tilt"]),
                   spec.get("tip", 0.0)
                   + rng.uniform(-cfg["jitter_tilt"], cfg["jitter_tilt"]),
                   yaw + rng.uniform(-cfg["jitter_yaw"], cfg["jitter_yaw"]))
            made["bricks"].append(place_brick(
                "Brick.%03d" % n, pos, rot, dims,
                moss, spec.get("tone", rng.uniform(0.02, 1.0)),
                1.0 if spec.get("pale") else 0.0))
            n += 1

            # ...and its opposite number in the back leaf, unless a header
            # is already occupying that stretch of it.
            if ci >= cfg["back_courses"] or spec.get("yaw90"):
                continue
            if rng.random() > cfg["back_keep"]:
                continue
            back_x = spec["x"] + cfg["back_shift"]
            if any(abs(back_x - hx) * pitch_x < (L + D) * 0.5 + cfg["gap"]
                   for hx in headers):
                continue
            bs = 1.0 + rng.uniform(-cfg["jitter_size"], cfg["jitter_size"])
            made["bricks"].append(place_brick(
                "Brick.%03d" % n,
                (pos[0] + cfg["back_shift"] * pitch_x + rng.uniform(-jp[0], jp[0]),
                 (D + cfg["back_clear"] * cfg["gap"]) + rng.uniform(-jp[1], jp[1]),
                 z + rng.uniform(-jp[2], jp[2])),
                (rng.uniform(-cfg["jitter_tilt"], cfg["jitter_tilt"]),
                 rng.uniform(-cfg["jitter_tilt"], cfg["jitter_tilt"]),
                 rng.uniform(-cfg["jitter_yaw"], cfg["jitter_yaw"])),
                (L * bs, D * bs, H),
                moss, rng.uniform(0.02, 1.0)))
            n += 1

    # --- bricks that came off ----------------------------------------------
    #
    # Named apart from the ones still in the wall, because the naming is what
    # decides whether they are simulated: see `ACTIVE_PREFIXES`.
    for i, spec in enumerate(FALLEN):
        s = 1.0 + rng.uniform(-cfg["jitter_size"], cfg["jitter_size"])
        made["bricks"].append(place_brick(
            "Fallen.%03d" % i, tuple(v * sc for v in spec["pos"]), spec["rot"],
            (L * s, D * s, H * s),
            1.0, rng.uniform(0.15, 0.85)))

    # --- boulders ----------------------------------------------------------
    for i, spec in enumerate(STONES):
        me, shift = _rock_mesh("Stone.%03d" % i,
                               tuple(v * sc for v in spec["size"]), spec["seed"],
                               taper=spec.get("taper", 0.0))
        ob = _seat("Stone.%03d" % i, me, shift,
                   (spec["pos"][0] * sc, spec["pos"][1] * sc,
                    (spec["pos"][2] - spec["sink"]) * sc),
                   (0.0, 0.0, rng.uniform(0.0, 360.0)))
        ob.data.materials.append(stone_mat)
        ob.color = (0.75, 0.0, 0.0, 1.0)
        _link(ob, root)
        made["stones"].append(ob)

    # --- the apron ---------------------------------------------------------
    # Runs last on purpose: it dodges the fallen bricks and the boulders, so
    # they have to exist before it starts.
    bpy.context.view_layer.update()
    rub = cfg["rubble"]
    jp = cfg["jitter_pos"]
    half = max(abs(spec if isinstance(spec, (int, float)) else spec["x"])
               for course in COURSES for spec in course) * pitch_x + L * 0.5
    front_face = D * 0.5 + jp[1]                       # the wall's near face
    back_face = D * 1.5 + cfg["back_clear"] * cfg["gap"] + jp[1]

    # Everything already lying on the ground, as (x, y, keep-out radius). The
    # apron has to dodge it: scattering blind put a chip 51 mm inside a fallen
    # brick, which is invisible in the render and is a bomb in the simulation
    # for the reason `gap` is written up for. `worst_overlap` found it.
    # Everything already lying on the ground, as (x, y, keep-out), read off the
    # objects rather than off the tables that placed them.
    #
    # **A rotated brick is wider than a brick, and the tables give the brick.**
    # `Fallen.000` lies at 24 degrees, so it spans 145 mm across X where its
    # length says 130, and a chip placed against the smaller figure sits 11 mm
    # inside it. The same understatement was fixed for the chips once already;
    # it survived here because these two lists are authored in metres and the
    # obvious keep-out is the number written next to the position.
    skipped = 0
    taken = []
    for ob in made["bricks"][-len(FALLEN):] + made["stones"]:
        pts = [ob.matrix_world @ Vector(c) for c in ob.bound_box]
        lo_p = Vector(tuple(min(q[i] for q in pts) for i in range(3)))
        hi_p = Vector(tuple(max(q[i] for q in pts) for i in range(3)))
        mid = (lo_p + hi_p) * 0.5
        half_p = (hi_p - lo_p) * 0.5
        taken.append((mid.x, mid.y, max(half_p.x, half_p.y)))

    def scatter(radius):
        """Along the foot mostly, spilled past the ends sometimes.

        Weighted to the front because that is the side anyone sees: the back
        of the wall is behind the wall at every heading the camera has.
        """
        clear = radius + rub["stand_off"]
        for _ in range(80):
            if rng.random() < rub["band"]:
                out = rng.uniform(0.0, rub["depth"])
                if rng.random() < rub["front"]:
                    y = -(front_face + clear + out)
                else:
                    y = back_face + clear + out * 0.7
                x = max(-half, min(half, rng.gauss(0.0, half * 0.62)))
            else:
                x = rng.choice((-1.0, 1.0)) * (half + clear
                                               + rng.uniform(0.0, rub["spread"]))
                y = rng.uniform(-(front_face + clear), back_face * 0.6)
            # Separation is tested per axis, not as a distance. Two boxes
            # are apart when ONE axis has daylight in it, so a Euclidean disc
            # test passes pieces that sit diagonally 19 mm inside each other:
            # `|d| > ra+rb` only gives `max(|dx|,|dy|) > (ra+rb)/sqrt(2)`.
            if all(max(abs(x - tx), abs(y - ty)) > radius + tr
                   for tx, ty, tr in taken):
                taken.append((x, y, radius))
                return x, y
        # Out of room: say so and place nothing.
        #
        # The version of this that parked the piece "somewhere clear" returned
        # the *same* somewhere every time, so once the band filled up it stacked
        # every remaining chip in one spot - 90 mm of overlap, worse than the
        # blind scatter it replaced. A fallback that quietly does the thing it
        # exists to prevent is the worst shape this could take; `build()`
        # reports `apron_skipped` instead.
        return None

    # The keep-out is the widest world half-extent the piece can reach on X
    # or Y at the rotations it is actually given: yaw is free, so the plan
    # diagonal, and the tilt is capped at 14 degrees, so a quarter of the
    # thickness on top of it. Half the length was the first guess and it let
    # chips overlap by 19 mm; `(dx+dy+dz)/2` was the second and it was so
    # cautious that five of nineteen pieces had nowhere to go.
    for i in range(cfg["rubble"]["pebbles"]):
        d = rng.uniform(0.030, 0.072) * sc
        dims = (d, d * rng.uniform(0.7, 1.1), d * 0.62)
        me, shift = _rock_mesh("Pebble.%03d" % i, dims,
                               cfg["seed"] * 100 + i, points=9)
        spot = scatter(0.5 * (math.hypot(dims[0], dims[1]) + 0.25 * dims[2]))
        if spot is None:
            skipped += 1
            bpy.data.meshes.remove(me)
            continue
        x, y = spot
        ob = _seat("Pebble.%03d" % i, me, shift, (x, y, d * 0.10),
                   (0.0, 0.0, rng.uniform(0.0, 360.0)))
        ob.data.materials.append(stone_mat)
        ob.color = (0.9, 0.0, 0.0, 1.0)
        _link(ob, root)
        made["rubble"].append(ob)

    for i in range(cfg["rubble"]["chips"]):
        s = rng.uniform(0.24, 0.48)
        dims = (L * s, D * s * rng.uniform(0.8, 1.4), H * s)
        me, shift = _brick_mesh("Chip.%03d" % i, dims, cfg, rng, True)
        spot = scatter(0.5 * (math.hypot(dims[0], dims[1]) + 0.25 * dims[2]))
        if spot is None:
            skipped += 1
            bpy.data.meshes.remove(me)
            continue
        x, y = spot
        ob = _seat("Chip.%03d" % i, me, shift, (x, y, dims[2] * 0.30),
                   (rng.uniform(-14.0, 14.0), rng.uniform(-14.0, 14.0),
                    rng.uniform(0.0, 360.0)))
        ob.data.materials.append(brick_mat)
        ob.color = (1.0, rng.uniform(0.15, 0.85), 0.0, 1.0)
        _link(ob, root)
        made["rubble"].append(ob)

    bpy.context.view_layer.update()
    offset = recentre() if cfg["centre"] else Vector((0.0, 0.0, 0.0))
    lo, hi = bounds()
    overlap, pair = worst_overlap()
    return {
        "scale": round(sc, 5),
        "centred_by": tuple(round(v, 4) for v in offset),
        "worst_overlap": round(overlap, 5),
        "worst_pair": pair,
        "apron_skipped": skipped,
        "bricks": len(made["bricks"]),
        "stones": len(made["stones"]),
        "rubble": len(made["rubble"]),
        "objects": sum(len(g) for g in made.values()),
        "verts": sum(len(o.data.vertices) for g in made.values() for o in g),
        "size": tuple(round(v, 4) for v in (hi - lo)),
        "min_z": round(lo.z, 4),
        "courses": len(COURSES),
    }


def _obb(ob):
    """Centre, unit axes and half extents of an object's oriented box."""
    m = ob.matrix_world
    pts = [Vector(c) for c in ob.bound_box]
    lo = Vector(tuple(min(p[i] for p in pts) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in pts) for i in range(3)))
    centre = m @ ((lo + hi) * 0.5)
    half_local = (hi - lo) * 0.5
    axes, half = [], []
    for i in range(3):
        col = m.col[i].to_3d()
        length = col.length or 1.0
        axes.append(col / length)
        half.append(half_local[i] * length)
    return centre, axes, half


def _penetration(a, b):
    """Overlap depth of two oriented boxes by separating-axis test.

    Negative is the width of the gap between them. Fifteen axes: three of each
    box plus the nine cross products, which is the whole of SAT for boxes.
    """
    ca, ua, ha = a
    cb, ub, hb = b
    d = cb - ca
    best = 1e9
    axes = list(ua) + list(ub)
    axes += [x.cross(y) for x in ua for y in ub]
    for ax in axes:
        n = ax.length
        if n < 1e-9:                      # parallel pair: its own axes cover it
            continue
        ax = ax / n
        ra = sum(ha[i] * abs(ax.dot(ua[i])) for i in range(3))
        rb = sum(hb[i] * abs(ax.dot(ub[i])) for i in range(3))
        gap = ra + rb - abs(d.dot(ax))
        if gap < best:
            best = gap
        if best < 0.0:
            break                          # one separating axis is enough
    return best


def worst_overlap(names=("Brick", "Fallen", "Chip")):
    """The deepest interpenetration between any two pieces, in metres.

    **Zero is the target, not a negative number, and that changed when the bed
    joint did.** Across a course the bricks are apart and this reads negative;
    up the wall they bear on each other, so the closest pair touches and it
    reads about +0.0002 - float noise on an exact contact. What must not happen
    is a real bite: anything past the collision margin (2 mm) is a brick inside
    a brick, and Bullet answers that with an impulse.

    It said "must stay negative" while the joint was on all three axes, and
    that guard was satisfiable only because every brick was hovering. Read
    the two apart: separation across the course, contact up it.

    **Oriented boxes, by separating-axis test, and the cheaper versions were
    all useless in the same way: they over-reported and so could never be
    satisfied.** A guard that cannot be driven to zero stops being read. Two
    were tried and both failed on geometry that is deliberately crooked - the
    top bricks are tipped 6.5 degrees, which swells an axis-aligned box by
    `length * sin(6.5)` = 29 mm and invents an overlap with the course below
    that is not there. And `ob.dimensions` is worse than an AABB, because it is
    the *local* box: for a header turned 90 degrees it reports the brick's
    length on the X axis while the brick lies along Y.

    The independent check on all of this is `demo_impact(strike=False)`: arm
    the physics, simulate, and see whether anything moves. It is the authority
    and this function is the fast version of the same question.
    """
    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] in names]
    boxes = {ob.name: _obb(ob) for ob in pieces}
    reach = max(sum(boxes[ob.name][2]) for ob in pieces) * 2.0 if pieces else 0.0
    worst, pair = -1e9, None
    for i, a in enumerate(pieces):
        for b in pieces[i + 1:]:
            if (boxes[a.name][0] - boxes[b.name][0]).length > reach:
                continue
            gap = _penetration(boxes[a.name], boxes[b.name])
            if gap > worst:
                worst, pair = gap, (a.name, b.name)
    return worst, pair


def bounds(names=None):
    """The prop's extent. The tile under it is not part of the prop.

    Named prefixes rather than "every mesh that is not scratch", because once
    `fit_to_hex` lays a cell the scene holds a 2.0-wide hexagon, and a bounding
    box that swallows it reports the cell's size under the wall's name - which
    is exactly the number you are looking at when you ask whether the wall fits
    the cell.
    """
    names = PREFIXES if names is None else names
    pts = []
    for ob in bpy.data.objects:
        if ob.type != "MESH" or ob.name.split(".")[0] not in names:
            continue
        pts += [ob.matrix_world @ Vector(c) for c in ob.bound_box]
    lo = Vector(tuple(min(p[i] for p in pts) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in pts) for i in range(3)))
    return lo, hi


# ---------------------------------------------------------------------------
# the cell it stands on
# ---------------------------------------------------------------------------

HEXES = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "Scenes", "Hexes.blend")


def hex_overhang(tile_radius, names=PREFIXES):
    """How far the prop pokes out of a flat-top hex. 1.0 is exactly the edge.

    The test is the hexagon's own two constraints, not a circle: a flat-top hex
    of circumradius R holds the points with `|y| <= sqrt(3)/2 R` (the flat top
    and bottom) and `|x| + |y|/sqrt(3) <= R` (the four slanted sides). A
    bounding circle would be the inradius and would throw away the 15% of extra
    room that lies along the vertex axis - which is the axis this wall runs
    along, so it is most of the room there is.
    """
    R = float(tile_radius)
    worst, who = 0.0, None
    for ob in bpy.data.objects:
        if ob.type != "MESH" or ob.name.split(".")[0] not in names:
            continue
        for corner in ob.bound_box:
            p = ob.matrix_world @ Vector(corner)
            x, y = abs(p.x), abs(p.y)
            f = max(y / (math.sqrt(3) * 0.5 * R), (x + y / math.sqrt(3)) / R)
            if f > worst:
                worst, who = f, ob.name
    return worst, who


def recentre(names=None):
    """Slide the whole prop so its plan bounding box is centred on the origin.

    Only X and Y: the feet stay on the ground.

    It is a step in placing the ruin on a cell rather than a fix to the layout,
    because the layout is not symmetric and should not be - the masonry runs
    off to one side and the debris falls to the other, which is the shape the
    reference has. What that costs is room: the fit is set by whichever piece
    pokes furthest out, so an off-centre prop wastes the clearance on the other
    side and has to be shrunk for it. Centring first buys that back.

    A translation of every piece, not a move of `Wall.World`: the root has to
    stay at the origin while the pieces are rigid bodies, and a pure
    translation applied to all of them leaves every relative distance - and so
    `worst_overlap` - exactly as it was.
    """
    names = PREFIXES if names is None else names
    lo, hi = bounds(names)
    off = Vector(((lo.x + hi.x) * 0.5, (lo.y + hi.y) * 0.5, 0.0))
    if off.length < 1e-9:
        return off
    for ob in bpy.data.objects:
        if ob.type == "MESH" and ob.name.split(".")[0] in names:
            ob.location = Vector(ob.location) - off
    bpy.context.view_layer.update()
    return off


def hex_tile(kind="Plain", radius=1.0, source=None):
    """Bring one of the board's own tiles in from `Scenes/Hexes.blend`.

    Appended rather than modelled again. The board already has these - plain,
    hill and ramp, with `Tile.Grass` on the top and `Tile.Earth` on the skirt -
    and a second hexagon built here would be a second answer to a question
    HEX_TILES.md has already settled, differing from the real one in whatever
    nobody checked. It comes in at the origin, whatever corner of that file it
    was parked in.

    The file's tiles are authored at R = 1. `radius` rescales the object, which
    is safe in a way that scaling the wall is not: the tile is scenery and
    never a rigid body.
    """
    name = "Hex.%s" % kind
    old = bpy.data.objects.get(name)
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)

    path = source or HEXES
    with bpy.data.libraries.load(path, link=False) as (src, dst):
        want = "Tile.%s" % kind
        if want not in src.objects:
            raise KeyError("%s has no %s; it has %s"
                           % (path, want, sorted(src.objects)))
        dst.objects = [want]
    ob = dst.objects[0]
    ob.name = name
    if ob.name not in bpy.context.scene.collection.objects:
        bpy.context.scene.collection.objects.link(ob)
    ob.location = (0.0, 0.0, 0.0)
    ob.scale = (radius, radius, radius)

    # Appending brings the tile's materials with it, and Blender numbers the
    # copies: call this a dozen times and the file carries `Tile.Grass.011`.
    # Point the slots back at the original name and drop the copy. Without it
    # the growth is silent - nothing looks wrong, the file just gets heavier
    # every time the tile is re-laid.
    for slot in ob.material_slots:
        mat = slot.material
        if mat is None:
            continue
        base = mat.name.rsplit(".", 1)[0] if mat.name[-3:].isdigit() else mat.name
        first = bpy.data.materials.get(base)
        if first is not None and first is not mat:
            slot.material = first
            if mat.users == 0:
                bpy.data.materials.remove(mat)
        elif first is None:
            mat.name = base
    for block in (bpy.data.meshes, bpy.data.materials):
        for b in list(block):
            if b.users == 0 and b.name.startswith("Tile."):
                block.remove(b)

    bpy.context.view_layer.update()
    return ob


def hex_fence(radius=1.0, reach=0.96, height=2.4, thickness=0.25, overlap=1.12):
    """Six passive panels on the cell's own edges, so debris cannot leave it.

    **This is a board constraint made physical, not a guard rail.** The prop
    belongs to one cell - that is what `fit_to_hex` is for - and the atlas
    renders one cell at a time, so a brick that rolls onto the neighbour is not
    a brick that went slightly too far, it is a brick in somebody else's
    sprite. Physics has no opinion about hexagons; this is how the hexagon gets
    one.

    **And it does real work, which is worth stating rather than implying.** The
    debris field of a wall is wider than the wall: 1.71 long and 0.76 high
    flattens into something over two metres across, on a cell two metres wide.
    Measured on the default shot - unfenced, 11 of 38 bricks end up outside the
    cell and the worst is 56% of a cell past the edge; fenced, none are outside
    and 10 come to rest against a panel. If that ever reads as a pile with a straight
    side, the lever is `coverage` in `fit_to_hex` - a smaller wall has a
    smaller debris field - and not a taller fence.

    **Tall enough that nothing goes over it, which is more than it looks.**
    `height` is in tile radii and 2.4 of them is far above the 0.76 wall,
    because the `mine` shot throws bricks upward: at height 1.0 one cleared the
    panel and finished 78% of a cell past the edge, and a fence a brick can
    clear is not a fence. It costs nothing - the panels are invisible.

    Six panels rather than one ring, because a convex hull cannot be a ring;
    `overlap` runs each panel past its own corner so a brick cannot squeeze
    through the mitre. `reach` and `height` are fractions of the cell, so the
    fence follows the tile and is never told a length in metres.

    Passive convex hulls and `hide_render`: they exist for the solver and for
    nothing else.
    """
    scene = bpy.context.scene
    R = float(radius)
    inner = math.sqrt(3) * 0.5 * R * reach          # inradius of the scaled hex
    made = []
    for k in range(6):
        name = "_Wall.Fence.%d" % k
        ob = bpy.data.objects.get(name)
        if ob is None:
            me = bpy.data.meshes.new(name)
            bm = bmesh.new()
            bmesh.ops.create_cube(bm, size=1.0)
            for v in bm.verts:
                v.co.x *= R * overlap               # edge length is R; run past it
                v.co.y *= thickness
                v.co.z *= height * R
            bm.to_mesh(me)
            bm.free()
            ob = bpy.data.objects.new(name, me)
            scene.collection.objects.link(ob)
        ang = math.radians(30.0 + 60.0 * k)         # edge normals of a flat-top hex
        n = Vector((math.cos(ang), math.sin(ang), 0.0))
        ob.location = (n * (inner + thickness * 0.5)
                       + Vector((0.0, 0.0, height * R * 0.5 - 0.05)))
        ob.rotation_euler = Euler((0.0, 0.0, ang - math.pi * 0.5))
        ob.hide_render = True
        ob.display_type = "WIRE"
        made.append(ob.name)
    bpy.context.view_layer.update()
    return {"panels": made, "reach": reach, "inner_radius": round(inner, 4)}


def pile_report(tile_radius=1.0, reach=0.96, names=None):
    """Where the rubble ended up, in units of the cell rather than of metres.

    `hex_factor` is the same test `hex_overhang` applies to the standing prop -
    the hexagon's own two constraints, not a circle - so "the wall fits" and
    "the rubble fits" are one measurement asked twice and cannot drift apart.

    `against_fence` is the honest half: a piece resting on a panel was placed
    by the fence and not by the fall.
    """
    names = PREFIXES if names is None else names
    rows = []
    for ob in bpy.data.objects:
        if ob.type != "MESH" or ob.name.split(".")[0] not in names:
            continue
        f = 0.0
        for corner in ob.bound_box:
            pt = ob.matrix_world @ Vector(corner)
            x, y = abs(pt.x), abs(pt.y)
            f = max(f, y / (math.sqrt(3) * 0.5 * tile_radius),
                    (x + y / math.sqrt(3)) / tile_radius)
        rows.append((f, ob.name))
    rows.sort()
    bricks = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] == "Brick"]
    return {
        "pieces": len(rows),
        "outside_cell": sum(1 for f, _ in rows if f > 1.0),
        "worst_hex_factor": round(rows[-1][0], 4) if rows else 0.0,
        "widest_piece": rows[-1][1] if rows else None,
        "against_fence": sum(1 for f, _ in rows if f > reach - 0.02),
        "pile_top": round(max([o.matrix_world.translation.z for o in bricks] or [0.0]), 4),
    }


def fit_to_hex(tile_radius=1.0, coverage=0.94, kind="Plain", cfg=None,
               source=None):
    """Build the ruin at the size that sits inside one cell, and lay the cell.

    Two passes, because the footprint is not a closed form: the apron is
    scattered with rejection and the boulders are hulls of random points, so
    what the prop actually spans is known by measuring it. The first build is
    thrown away; both are about a second.

    `coverage` is how much of the cell the prop is allowed to fill, edge
    included. Not 1.0 - a prop that touches its own cell boundary touches the
    neighbouring tile's art, and on a hex board the seam is where the eye goes.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    build(cfg)                       # `build` centres it; see CONFIG["centre"]
    over, who = hex_overhang(tile_radius)
    scale = cfg["scale"] * coverage / over
    final = build(dict(cfg, scale=scale))
    after, worst_piece = hex_overhang(tile_radius)
    tile = hex_tile(kind, radius=tile_radius, source=source)
    lo, hi = bounds()
    return {
        "tile": tile.name,
        "tile_radius": tile_radius,
        "scale": round(scale, 5),
        "centred_by": final["centred_by"],
        "overhang_before": round(over, 4),
        "overhang_after": round(after, 4),
        "widest_piece": worst_piece,
        "size": tuple(round(v, 4) for v in (hi - lo)),
        "height_in_levels": round((hi.z - max(lo.z, 0.0)) / (tile_radius * 0.5), 2),
        "worst_overlap": final["worst_overlap"],
        "apron_skipped": final["apron_skipped"],
        "objects": final["objects"],
    }


# ---------------------------------------------------------------------------
# destruction
# ---------------------------------------------------------------------------

# Only the bricks still standing in the wall. Everything that is already on
# the ground - the fallen bricks, the chips, the pebbles, the boulders - is
# scenery, and that is a measurement rather than a preference: a flat light
# body resting on the floor collider gets pinched between it and the masonry
# coming down, and Bullet answers a pinch with an enormous impulse. Runs at
# `travel=40`: with the chips active a chip went 75.9 units, with them passive
# a *fallen brick* went 36.8, and with only the wall active the median piece
# moves 0.49 m and nothing is thrown. The artefact walks down to whatever the
# smallest loose thing on the floor is, so the line has to be drawn at "was it
# still in the wall".
ACTIVE_PREFIXES = ("Brick",)


def arm_physics(active=True, floor=True, friction=0.82, margin=0.002,
                active_prefixes=ACTIVE_PREFIXES):
    """Give every piece a rigid body, so the wall can be pushed over.

    Convex hull, not mesh: every brick *is* convex (the corner chips are plane
    cuts, which keep it so), and a convex solver is both faster and stable at
    this size. Mesh collision on sixty bodies stacked eight high jitters itself
    apart while you watch.

    Asleep at the start (`use_start_deactivated`), because a wall that settles
    into its own gaps on frame one has already moved before anything hit it.

    **Only bricks and brick chips are active; the boulders and pebbles are
    not.** They are stone bedded in the ground, and a shell that hits the wall
    has no business launching a buried boulder. The measurable half of that
    argument is that they are *modelled* buried - the boulders are sunk 35-50
    mm so they read as coming out of the earth - so as active bodies they start
    inside the floor collider and Bullet either pops them out or, at these
    sizes, lets them through: the first run of `demo_impact` had a pebble
    57.9 units below the ground and still going.

    The floor is a box, not a plane. A plane's convex hull has no thickness,
    and a 26-frame projectile driving bricks into it tunnels through.

    `Wall.World` must stay at the origin once this is armed. The bodies are
    parented to it and the simulation writes world transforms, so a parent that
    moves applies its matrix on top and the wall drifts out of its own
    collisions. Parenting is kept anyway: an empty at identity costs nothing
    and losing the grouping costs a lot.
    """
    scene = bpy.context.scene
    if scene.rigidbody_world is None:
        bpy.ops.rigidbody.world_add()
    if scene.rigidbody_world.collection is None:
        scene.rigidbody_world.collection = bpy.data.collections.new("RigidBodyWorld")
    coll = scene.rigidbody_world.collection

    # Where the top of the floor goes. Chips are bedded a few mm into the
    # ground so they read as pressed in, and an active body that starts inside
    # the collider is the other way to make Bullet explode - so the collider
    # goes under the lowest thing that moves, not at z = 0. The boulders stay
    # buried: they are passive and never asked.
    sunk = [ob.matrix_world.translation.z - ob.dimensions.z * 0.5
            for ob in bpy.data.objects
            if ob.type == "MESH" and ob.name.split(".")[0] in active_prefixes]
    top = min([0.0] + sunk) - 0.001

    ground = None
    if floor:
        ground = bpy.data.objects.get("_Wall.Ground")
        if ground is None:
            me = bpy.data.meshes.new("_Wall.Ground")
            bm = bmesh.new()
            bmesh.ops.create_cube(bm, size=1.0)
            for v in bm.verts:
                v.co.x *= 8.0
                v.co.y *= 8.0
                v.co.z *= 0.5                          # centred, 0.5 thick
            bm.to_mesh(me)
            bm.free()
            ground = bpy.data.objects.new("_Wall.Ground", me)
            scene.collection.objects.link(ground)
        # The object goes half a thickness under the surface it is meant to be.
        #
        # **A BOX collision shape is centred on the object's origin, whatever
        # the mesh does inside it.** The first version offset the vertices so
        # the top face landed on z = 0 and left the object at the origin; the
        # picture was right and the collider was a box from -0.25 to +0.25, so
        # its top stood 250 mm above the ground everyone could see. Every brick
        # in the wall started a quarter of a metre inside it and came out at
        # 9.5 m/s.
        #
        # It cost every physics number in this file before it was found, and it
        # hid behind a plausible story each time - pinched debris, a projectile
        # that hits too hard - because the wall does explode, just not for any
        # of those reasons. What finally showed it was dropping a brick from
        # height: it came to rest on 0.250, and nothing in the scene is there.
        ground.location = (0.0, 0.0, top - 0.25)
        ground.hide_render = True

    # Bullet's defaults are tuned for a handful of bodies, not for sixty
    # stacked eight high with millimetre joints: at 1 substep a brick crosses
    # its own joint in one step and the contact is found late, which reads as
    # the stack shivering.
    scene.rigidbody_world.substeps_per_frame = 10
    scene.rigidbody_world.solver_iterations = 20

    n = 0
    for ob in bpy.data.objects:
        if ob.type != "MESH":
            continue
        piece = (ob is not ground
                 and ob.name.split(".")[0] in active_prefixes)
        if ob.name not in coll.objects:
            coll.objects.link(ob)
        if ob.rigid_body is None:
            prev = bpy.context.view_layer.objects.active
            bpy.context.view_layer.objects.active = ob
            bpy.ops.rigidbody.object_add()
            bpy.context.view_layer.objects.active = prev
        rb = ob.rigid_body
        rb.type = "ACTIVE" if (piece and active) else "PASSIVE"
        rb.collision_shape = "BOX" if ob is ground else "CONVEX_HULL"
        rb.collision_margin = margin
        rb.use_margin = True
        rb.friction = friction
        rb.restitution = 0.02
        if piece:
            d = ob.dimensions
            rb.mass = max(0.05, d.x * d.y * d.z * 1900.0)     # brick ~1.9 t/m3
            rb.use_deactivation = True
            rb.use_start_deactivated = True
            n += 1
    return {"bodies": n, "floor": bool(ground)}


def settle(frames=140, active_prefixes=("Brick", "Fallen", "Chip")):
    """Let the wall fall into its own joints, then keep where it landed.

    **The wall as built is not resting on anything.** The mortar joint that
    keeps the bricks from intersecting - the whole point of `gap` - also holds
    every one of them a few millimetres in the air, so the first frame of any
    simulation is 47 pieces dropping at once. Measured before this existed:
    untouched and awake, the median piece moved 28 mm and the loosest 0.95 m,
    which is a wall that collapses slightly the moment you press play and then
    gets hit.

    So the joints are closed here rather than in the layout. Doing it in the
    layout means either courses that touch - and then the size jitter puts them
    a couple of millimetres *inside* each other, which is the failure `gap`
    exists to prevent - or authoring the stack against the real heights of
    forty-one jittered bricks. Gravity already solves that exactly.

    Everything after this is the payoff: the pose is a rest pose, so a shot can
    start on frame 1, and `arm_physics` no longer needs its pieces asleep to
    look still.

    Returns how far the settle moved things, which is also the measure of how
    much the picture changed.
    """
    arm_physics(active_prefixes=active_prefixes)
    scene = bpy.context.scene
    for ob in bpy.data.objects:
        if ob.rigid_body and ob.rigid_body.type == "ACTIVE":
            ob.rigid_body.use_start_deactivated = False

    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] in PREFIXES]
    scene.frame_start, scene.frame_end = 1, frames
    scene.rigidbody_world.point_cache.frame_start = 1
    scene.rigidbody_world.point_cache.frame_end = frames

    scene.frame_set(1)
    before = {ob.name: ob.matrix_world.copy() for ob in pieces}
    for f in range(1, frames + 1):
        scene.frame_set(f)
    after = {ob.name: ob.matrix_world.copy() for ob in pieces}

    remove_demo()                       # frees the cache and the floor
    scene.frame_set(1)
    for ob in pieces:
        if ob.rigid_body is not None:
            prev = bpy.context.view_layer.objects.active
            bpy.context.view_layer.objects.active = ob
            bpy.ops.rigidbody.object_remove()
            bpy.context.view_layer.objects.active = prev
        ob.matrix_world = after[ob.name]
    if scene.rigidbody_world is not None:
        coll = scene.rigidbody_world.collection
        bpy.ops.rigidbody.world_remove()
        if coll is not None and coll.users == 0:
            bpy.data.collections.remove(coll)
    bpy.context.view_layer.update()

    moved = sorted((after[ob.name].translation - before[ob.name].translation).length
                   for ob in pieces)
    over, _ = hex_overhang(1.0)
    return {
        "frames": frames,
        "pieces": len(pieces),
        "max_settle": round(moved[-1], 4),
        "median_settle": round(moved[len(moved) // 2], 5),
        "moved_over_1cm": sum(1 for m in moved if m > 0.01),
        "overhang": round(over, 4),
    }


# ---------------------------------------------------------------------------
# preview
# ---------------------------------------------------------------------------

def preview(path, azimuth=20.0, elevation=30.0, size=640, padding=1.10, samples=96):
    """One render through the project's own camera and 3-point rig.

    Borrowed from `sprite_atlas` rather than rebuilt, so what you judge here is
    what the atlas would show. Standard view transform, not AgX: this is flat
    painted art, and a film curve takes the colour out of the brick.
    """
    import sprite_atlas

    for name in ("_preview_cam", "_atlas_key", "_atlas_fill", "_atlas_rim"):
        ob = bpy.data.objects.get(name)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)

    # Frame the cell too when there is one. `bounds()` answers about the prop,
    # which is what `fit_to_hex` needs and the wrong question here: a preview
    # framed on the wall crops the tile it is standing on, and the tile is half
    # of what the picture is for.
    lo, hi = bounds(PREFIXES + ("Hex",))
    pivot = (lo + hi) * 0.5
    radius = max((hi - lo).length * 0.5, 1e-3)

    data = bpy.data.cameras.new("_preview_cam")
    data.type = "ORTHO"
    data.ortho_scale = radius * 2.0 * padding
    data.clip_start, data.clip_end = 0.01, 100.0
    cam = bpy.data.objects.new("_preview_cam", data)
    bpy.context.scene.collection.objects.link(cam)
    cam.matrix_world = sprite_atlas.camera_matrix(pivot, azimuth, elevation, radius * 8.0)

    lights = sprite_atlas.build_lighting(
        {"light_rig": None, "key_energy": 4.0, "fill_energy": 1.2, "rim_energy": 2.0}, cam)

    scene = bpy.context.scene
    scene.camera = cam
    # EEVEE's identifier moved: it was BLENDER_EEVEE, then BLENDER_EEVEE_NEXT
    # while both shipped, then BLENDER_EEVEE again. Ask the enum rather than
    # naming one, so the preview does not stop working on the next release.
    engines = scene.render.bl_rna.properties["engine"].enum_items.keys()
    for name in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        if name in engines:
            scene.render.engine = name
            break
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    try:
        scene.eevee.taa_render_samples = samples
    except AttributeError:
        pass

    # Ray-traced occlusion, and this is the one render setting the model
    # genuinely needs rather than prefers. The joints between bricks are gaps
    # between *separate objects*, so nothing in the material can darken them -
    # `Pointiness` is per mesh and sees a brick's own concavities, of which a
    # box has none. Without occlusion the whole stack is lit as if the bricks
    # were not touching and reads as one soft mass; the dark line round every
    # brick is what the reference draws as an outline.
    #
    # Worth carrying into `sprite_atlas` when this prop is rendered for real.
    ray = getattr(scene.eevee, "use_raytracing", None)
    if ray is not None:
        scene.eevee.use_raytracing = True
        opts = getattr(scene.eevee, "ray_tracing_options", None)
        if opts is not None and hasattr(opts, "use_denoise"):
            opts.use_denoise = True
    elif hasattr(scene.eevee, "use_gtao"):
        scene.eevee.use_gtao = True                       # pre-Next EEVEE
        scene.eevee.gtao_distance = 0.12
    
    world = scene.world or bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        # Strength only, and neutral. `sprite_atlas` sets the strength and
        # never the colour, so the tank scenes render against Blender's own
        # dark grey world; a preview that invents a brighter, bluer ambient
        # measures a wall nobody will ever see. The first pass here used
        # (0.55, 0.56, 0.60) and it put +20 of blue into every brick - caught
        # by measuring the render's median against the reference's, not by
        # looking, because "a bit washed out" is not a diagnosis.
        bg.inputs["Strength"].default_value = 0.25
        bg.inputs["Color"].default_value = (0.0509, 0.0509, 0.0509, 1.0)

    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

    for ob in [cam] + list(lights):
        bpy.data.objects.remove(ob, do_unlink=True)
    return {"path": path, "azimuth": azimuth,
            "ortho_scale": round(data.ortho_scale, 4),
            "pivot": tuple(round(v, 4) for v in pivot)}


def _burst(lo, hi, shot="mine", balls=3, ball=0.26, spread=0.62,
           rise=0.80, sink=1.40, through=1.07, height=0.52, run=1.38):
    """Build the shot: a rank of kinematic spheres, with a start and an end each.

    Two shapes, because "knock the wall down" and "leave the rubble spread over
    the cell" are different requests and one shot cannot answer both.

    - **`ram`** drives the rank horizontally through the face and out the back.
      It is what a shell does, and the rubble lands where a pushed-over wall
      puts it: a band on the far side. Measured across the cell in three
      strips of y, that is 1 / 26 / 32 pieces - the near third of the tile
      bare.
    - **`mine`** raises the rank from under the footing and up through the
      courses, so the wall is thrown off its own base outwards in every
      direction rather than downwind. Same strips: 16 / 25 / 18. That is the
      only way to fill the cell, and it is not a trick - a line of bricks
      pushed one way makes a line of rubble, whatever the energy.

    **A rank rather than one ball, because the ask is that the wall comes down
    and not that it gets a hole.** One shell knocks out what it touches and the
    courses above drop into the gap, which is a good picture and a partial one:
    measured, it leaves 6 bricks still stacked and a pile 0.36 high out of
    0.76. Three abreast reach every course and leave nothing standing.

    **The `mine` rank starts below the floor, and that is what makes it
    possible at all.** A sphere that begins inside the wall is a body
    overlapping other bodies on frame one, and Bullet resolves deep
    interpenetration with a large impulse - the wall does not fall, it leaves.
    Below the floor there is nothing to overlap: the ground and the spheres are
    both passive, and passive bodies do not see each other.

    **The spheres are built at the radius they want and never scaled, and that
    is the same family of trap as the floor collider.** A `SPHERE` collision
    shape takes its radius from the mesh; the object's scale does not shrink
    it. A unit sphere "scaled to 0.1" collides as a unit sphere, and it reads
    exactly like a shot tuned far too hard - max travel 2.05 against 0.81 for
    the same numbers, every brick off the cell - so it gets blamed on the
    energy. Two of these now, and one lesson: a collision shape is built from
    the mesh, and the object transform is not what you think.

    Every distance is a fraction of the prop's own box, so the shot survives
    `fit_to_hex` picking a different scale: `ball`, `height`, `rise` and `sink`
    of its height, `spread` of its length, `run` and `through` of its depth.
    """
    scene = bpy.context.scene
    span = hi - lo
    radius = ball * span.z
    xs = ([0.0] if balls < 2
          else [(-0.5 + i / (balls - 1.0)) * spread * span.x for i in range(balls)])

    built = []
    for i, x in enumerate(xs):
        name = "_Wall.Shell.%d" % i
        ob = bpy.data.objects.get(name)
        if ob is None:
            me = bpy.data.meshes.new(name)
            bm = bmesh.new()
            bmesh.ops.create_icosphere(bm, subdivisions=2, radius=radius)
            bm.to_mesh(me)
            bm.free()
            ob = bpy.data.objects.new(name, me)
            scene.collection.objects.link(ob)
        ob.hide_render = True                     # a tool, not part of the prop
        ob.display_type = "WIRE"
        ob.animation_data_clear()

        if shot == "mine":
            y = (lo.y + hi.y) * 0.5
            start = Vector((x, y, lo.z - sink * span.z))
            end = Vector((x, y, lo.z + rise * span.z))
        else:
            z = lo.z + height * span.z
            start = Vector((x, lo.y - run * span.y, z))
            end = Vector((x, lo.y + through * span.y, z))
        ob.location = start                # in place before the sim is built
        bpy.context.view_layer.update()

        if ob.name not in scene.rigidbody_world.collection.objects:
            scene.rigidbody_world.collection.objects.link(ob)
        if ob.rigid_body is None:
            prev = bpy.context.view_layer.objects.active
            bpy.context.view_layer.objects.active = ob
            bpy.ops.rigidbody.object_add()
            bpy.context.view_layer.objects.active = prev
        ob.rigid_body.type = "PASSIVE"
        ob.rigid_body.kinematic = True            # driven, not simulated
        ob.rigid_body.collision_shape = "SPHERE"
        built.append((ob, start, end))
    return built


def demo_impact(frames=170, travel=34, lead=12, strike=True, shot="mine",
                fence=True, tile_radius=1.0, reach=0.96,
                active_prefixes=ACTIVE_PREFIXES, **kw):
    """Fire the shot, step it, and say what moved and where it came to rest.

    Not part of the prop, and it does not pretend to be the destruction effect.
    It is here because "the wall can be knocked down" is a claim, and the one
    thing that settles a claim about a rigid-body setup is watching the bricks
    separate - a build that returns sixty-six objects and a set of masses looks
    identical whether the bodies collide or fall through each other.

    Kinematic and passive, not active: an active projectile has to be given a
    velocity, and Blender has no way to set one but to let gravity do it, which
    means aiming a drop. A body that is told where to be goes there at the
    speed it is told, so the test is repeatable, which is the whole point.

    Told, and not keyframed. A kinematic body takes its transform from the
    object, so writing the location each frame does the same job as an f-curve
    and skips the one part of this that has moved between releases: Blender's
    slotted actions dropped `Action.fcurves`, so setting the interpolation to
    linear - which a moving projectile has to have - now means walking layers,
    strips and channelbags. There is nothing to walk if there is no action.

    **`travel` is the only lever on how hard it hits, and there is no mass
    argument because a kinematic body has no mass.** Bullet treats one as
    infinitely heavy - it drives the contact and nothing pushes back - so the
    ball's `mass` field is read by nobody. This took a measurement to notice:
    runs at 12 kg, 40 kg and 90 kg returned `max_travel` 34.3456 to four
    decimals, identically, which is not a suspicious result so much as the same
    result. Fewer `travel` frames is faster is harder.

    **`strike=False` is the more important run of the two.** It arms everything
    and simulates with the shot parked off to one side, so what it reports is
    whether the wall stands up on its own: `max_travel` near zero over a
    hundred frames says the courses are resting on each other and not inside
    each other, which is the only thing about this model that physics can
    prove.

    Everything it makes is named `_Wall.*` and `remove_demo()` takes it away.
    """
    scene = bpy.context.scene
    if fence:
        hex_fence(radius=tile_radius, reach=reach)     # before, so it gets a body
    arm_physics(active_prefixes=active_prefixes)
    lo, hi = bounds()
    rank = _burst(lo, hi, shot=shot, **kw)

    # The prop, not the cell it stands on: `Hex.Plain` is a mesh that never
    # moves, and counting it drags the median of "how far did things travel"
    # towards zero with a piece that was never in the wall.
    pieces = [ob for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] in PREFIXES]
    scene.frame_start = 1
    scene.frame_end = frames
    scene.rigidbody_world.point_cache.frame_start = 1
    scene.rigidbody_world.point_cache.frame_end = frames

    scene.frame_set(1)
    rest = {ob.name: ob.matrix_world.translation.copy() for ob in pieces}
    drift = 0.0
    for f in range(1, frames + 1):
        # The lead frames are the test's own check that the wall is at rest:
        # anything that has moved by frame `lead` moved on its own, and a
        # destruction shot that begins with the wall shrugging is a defect.
        t = max(0.0, min(1.0, (f - 1 - lead) / float(travel)))
        for ob, start, end in rank:
            ob.location = start.lerp(end, t) if strike else start
        scene.frame_set(f)
        if f == lead:
            drift = max((ob.matrix_world.translation - rest[ob.name]).length
                        for ob in pieces)

    moved = sorted(((ob.matrix_world.translation - rest[ob.name]).length, ob.name)
                   for ob in pieces)
    # Still standing: high enough up to have been masonry, and barely shifted.
    # The bottom course staying put is a wall coming down correctly, so the
    # question is asked of the courses above it.
    intact = [ob.name for ob in bpy.data.objects
              if ob.type == "MESH" and ob.name.split(".")[0] == "Brick"
              and rest[ob.name].z > lo.z + 0.22 * (hi.z - lo.z)
              and (ob.matrix_world.translation - rest[ob.name]).length < 0.05]
    out = {
        "frames": frames,
        "travel": travel if strike else None,
        "strike": strike,
        "shot": shot if strike else None,
        "balls": len(rank),
        "pieces": len(pieces),
        "drift_before_impact": round(drift, 5),
        "moved_over_1cm": sum(1 for d, _ in moved if d > 0.01),
        "moved_over_1_brick": sum(1 for d, _ in moved
                                  if d > CONFIG["brick"][0] * CONFIG["scale"]),
        "max_travel": round(moved[-1][0], 4),
        "median_travel": round(moved[len(moved) // 2][0], 4),
        "lowest_piece_z": round(min(ob.matrix_world.translation.z
                                    for ob in pieces), 4),
        "still_standing": len(intact),
        "farthest": moved[-1][1],
    }
    out.update(pile_report(tile_radius=tile_radius, reach=reach))
    return out


def _fcurves(action):
    """Every f-curve of an action, on either side of the slotted-action change.

    Blender 4.4 moved keyframes from `Action.fcurves` into
    `layers[].strips[].channelbags[].fcurves`. `demo_impact` dodges this by not
    keyframing at all, which it can afford because it drives the ball itself;
    `rig_break` cannot, because the whole point of it is that the ball moves
    when *Blender* plays the scene.
    """
    if hasattr(action, "fcurves"):
        return list(action.fcurves)
    out = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", ()):
                out += list(bag.fcurves)
    return out


def rig_break(travel=34, frames=170, lead=12, bake=True, shot="mine",
              fence=True, tile_radius=1.0, reach=0.96,
              active_prefixes=ACTIVE_PREFIXES, **kw):
    """Leave the scene ready to press play, and watch the wall come down.

    The difference from `demo_impact` is only who drives it. That one steps the
    frames from Python and writes the shot's position each step, which measures
    fine and shows nothing: the scene it leaves behind has spheres with no
    animation on them, so pressing Space in Blender moves nothing. This one
    keyframes them, sets the range and bakes, and then hands the scene over.

    **The pieces stay asleep until something hits them, and that is what makes
    this watchable rather than a shrug followed by a collapse.** A dry stack of
    stepped courses is not a stable structure - a running bond puts half of
    each end brick over air, and nothing but mortar holds that. Left awake and
    untouched the wall settles and topples on its own in about forty frames;
    measured, the median piece moves 16 mm and the loosest 0.74 m. Asleep, it
    stands exactly still, and only what the shot reaches wakes up.

    **The cell fence goes in with it.** Without it 11 of 38 bricks finish on a
    neighbouring tile - see `hex_fence`, which is where that argument is.

    Afterwards: `remove_demo()` takes the rig away, and `build()` or
    `fit_to_hex()` puts the wall back up.
    """
    scene = bpy.context.scene
    if fence:
        hex_fence(radius=tile_radius, reach=reach)     # before, so it gets a body
    arm_physics(active_prefixes=active_prefixes)
    lo, hi = bounds()
    rank = _burst(lo, hi, shot=shot, **kw)

    for ob, start, end in rank:
        for frame, pos in ((1, start), (lead, start), (lead + travel, end)):
            ob.location = pos
            ob.keyframe_insert("location", frame=frame)
        ob.location = start
        for fc in _fcurves(ob.animation_data.action):
            for kp in fc.keyframe_points:
                kp.interpolation = "LINEAR"   # a shell with bezier ease is a lob

    scene.frame_start, scene.frame_end = 1, frames
    scene.rigidbody_world.point_cache.frame_start = 1
    scene.rigidbody_world.point_cache.frame_end = frames
    scene.frame_set(1)

    baked = False
    if bake:
        try:
            bpy.ops.ptcache.bake_all(bake=True)
            baked = scene.rigidbody_world.point_cache.is_baked
        except RuntimeError:
            baked = False                          # play forward still works
    scene.frame_set(1)
    return {"frames": frames, "shot": shot, "balls": len(rank), "baked": baked,
            "ball_starts": lead, "ball_stops": lead + travel,
            "touches_wall_about": lead + int(travel * 0.62),
            "fenced": bool(fence), "asleep_until_hit": True}


def remove_demo():
    """Take the test rig away and put the frame back to one."""
    scene = bpy.context.scene
    for ob in list(bpy.data.objects):
        if ob.name.startswith("_Wall."):
            bpy.data.objects.remove(ob, do_unlink=True)
    scene.frame_set(1)
    if scene.rigidbody_world is not None:
        bpy.ops.ptcache.free_bake_all()


if __name__ == "__main__":
    print(build())
