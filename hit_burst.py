"""
The impact burst: a shell arriving, as geometry.

`build()` and `build_dust()` make the objects; hand `phase_hook()` and
`dust_phase_hook()` to `sprite_atlas` layers with `"fit": False`,
`"static": True` and `"phases": N`. `bench()` renders the pair against the tank
and against bare ground and measures the result.

Two objects, for the reason every effect here has two
------------------------------------------------------
`Burst` emits and composites **additively**; `Dust` occludes and composites with
**normal alpha**, underneath. Fire adds light and dust takes it away, and one
layer cannot do both. The order is the effect, not a preference: on the game's
pale field an additive pixel over 0.65 grey is that grey very slightly tinted -
i.e. grey - so the dust laying something dark down first is what lets the burst
arrive at all.

No headings, and that is the whole economy of this layer
--------------------------------------------------------
Every other effect is rendered at all twelve headings because it is welded to
the tank: the muzzle flash is a directional cone on a barrel, the plume grows
out of a port on the deck. A hit is not welded to anything. It happens at an
arbitrary point, from an arbitrary direction, and what it looks like is a ball
of light - which is the same ball from every azimuth.

So this renders **once**, as a `static` layer with phases, and the harness puts
it where the hit was. Rendered per face and per heading it would be
4 x 12 x 8 x 2 = 768 frames and about 200 MB decoded; this is 20 frames and
about 3 MB. The layer keeps the job's `units_per_pixel`, so it is still the
right *size* - it just does not turn.

What is given up: there is no parallax. A burst on a plate seen edge-on is
drawn as round as one seen face-on, and the debris does not fan along the
plate. The harness gets some of that back by stretching the sprite along the
plate's stamped screen normal - unnormalised, so its length carries the
foreshortening, the same trick the flash's ground direction uses. If that ever
reads badly the layer can be promoted to twelve headings for 25 MB and nothing
else changes: the table, the clock and the harness stay as they are.

Symmetric about the view axis, not about anything on the tank
--------------------------------------------------------------
Since one render has to serve every plate, the burst cannot be oriented to any
of them. It is built symmetric about the direction the camera looks from, so it
presents the same face wherever it is stuck. The axis is read out of
`sprite_atlas.camera_matrix` rather than written down, because it is the
renderer's camera that decides it.

Where it sits in its frame is a convenience, not a contract
------------------------------------------------------------
The other layers all draw at `-anchor` and that is their whole alignment story.
This one draws at `-anchor + offset`, where the offset comes from the stamped
plates projected through the same camera. So the offset is measured **from the
burst's own origin**, not from the frame's anchor, and where the origin sits
only decides whether the frame is well used. Tying it to the anchor would mean
re-deriving the renderer's pivot here - two copies of one number, which is the
way sprites drift apart quietly.

Do not parent these to anything
-------------------------------
Same trap as `Flash`, `Plume`, `Fire` and `Burn`: under `Hull` they would be
drawn into the hull atlas permanently, and the tank would be taking a hit for
ever. They sit at scene root and `render_atlas` spins them about the shared
pivot - which for a static layer means not at all.
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
    "name": "Burst",
    # the turret, and the two layer roots - only used to measure the tank's own
    # height for the build origin. None -> the meshes; see tank_parts.py
    "turret": "Turret",
    "hull_root": None,
    "turret_root": None,
    # The camera the set is rendered through; the burst faces it. Same defaults
    # as sprite_atlas.CONFIG - override together with it or not at all.
    "elevation": 30.0,
    "azimuth": 0.0,
    # Hull length in world units, and the origin to build on. None -> read the
    # `hit_scale` stamp and the ring axis. Both are for a bench with no tank.
    "scale_units": None,
    "origin": None,

    # --- the two master knobs -----------------------------------------------
    # Size and alpha, multiplying through the tables rather than replacing them
    # so the animation keeps its curve. Past about 1.4, brightness stops buying
    # brightness and starts buying white - alpha clips at 1 and additive
    # compositing whitens any overlap on its own.
    "scale": 1.0,
    "brightness": 1.25,

    # --- size, in hull lengths ----------------------------------------------
    # A shell is a shell on any tank, so by the muzzle's logic these ought to be
    # in calibres - but nothing on these models measures a calibre, and the hull
    # is what `hit_point` already stamps. On MT one hull length is 153.7 px, so
    # a 0.055 core is 8.5 px of radius and the whole burst about 45 px across,
    # against a front plate that measures 76 x 54.
    "core": 0.078,
    "rings": 9,
    "segments": 14,
    "wobble": 0.16,
    "seed": 131,
    # Nested spheres: `at` is where each samples the colour ramp, so the middle
    # is hot and the outside is not. One sphere draws its own silhouette
    # crisply and reads as a bead; three translucent ones read as a glow.
    "shells": (
        {"scale": 1.00, "at": 0.62, "gain": 0.45},
        {"scale": 0.70, "at": 0.30, "gain": 0.70},
        {"scale": 0.38, "at": 0.03, "gain": 0.92},
    ),

    # --- the spatter ---------------------------------------------------------
    # Tapered streaks thrown out roughly in the screen plane: the flash running
    # over the armour. `tilt` is degrees away from the view axis, so 90 is the
    # screen plane exactly and the spread lets them out of it a little.
    #
    # These are *lobes of the core*, not needles. The first numbers made them
    # 3px thick and 20px long, eleven of them evenly spread, and the result was
    # the dandelion `muzzle_flash` warns about in as many words - a star of thin
    # rays with no mass behind it. Fatter, shorter, fewer, and jittered nearly a
    # whole slot so the spacing stops reading as a snowflake.
    # The mass thrown off the plate, as a cluster of blobs rather than as
    # streaks. Streaks were two attempts and both failed the same way: eight of
    # them radiating from one point is a rosette however they are jittered,
    # because the construction is symmetric and the jitter only decides where
    # the petals are. A cluster is irregular *by construction* - the same reason
    # the plume and the engine fire are built out of blobs - and the sizes,
    # distances and hues all vary independently.
    #
    # `at` walks the colour ramp with distance, so the near blobs are the hot
    # part and the far ones are already red. That gradient is what says
    # "expanding" without any element having to move.
    "spatter": {
        "count": 9, "reach": 0.115, "spread": 34.0, "even": 0.75,
        "radius": (0.016, 0.040),
        "distance": (0.30, 1.00),
        "at": (0.10, 0.85),
        "grow": 0.40,             # how much of `reach` the blobs themselves take
        "gain": 0.90,
    },
    # Thinner, faster, longer-lived: they are still flying after the fire has
    # gone. Deep red rather than orange, for the reason the fire's embers were:
    # a few pixels of low-alpha yellow added onto pale ground is a grey speck,
    # and a spark that has to be pointed out is not a spark.
    # A tube thinner than a pixel is *all* silhouette, so `rim_falloff` fades
    # the whole of it and the spark renders at an alpha that rounds to nothing.
    # At 0.0075 the sparks measured 1.15px across and simply did not appear -
    # the burst came out 35px wide when the numbers said 63.
    "sparks": {
        "count": 12, "tilt": 90.0, "spread": 48.0, "even": 0.80,
        "length": (0.35, 1.00),
        "reach": 0.150,
        "radius": 0.0110,
        "start": (0.30, 0.75),
        "colour": (1.00, 0.30, 0.06),
        "gain": 0.70,
    },

    # --- how it burns down ---------------------------------------------------
    # An impact is not a muzzle flash. The flash is brightest as it leaves the
    # barrel and keeps growing while it dims - expanding gas. This is brightest
    # at the instant of contact and the core *collapses*, because on pale ground
    # an additive element that fades turns into tinted ground, i.e. grey. It has
    # to end by shrinking while it is still saturated. Same lesson as the
    # engine fire's tips.
    # The first frame is already lit: a shell does not ramp up. Starting it at
    # half size read as the burst arriving late, which on a four-frame event is
    # most of the event.
    "phases": [
        # core   emission  splash reach
        (1.00,   1.00,     0.62),
        (1.00,   1.00,     0.92),
        (0.86,   0.94,     1.00),
        (0.62,   0.80,     1.00),
        (0.40,   0.58,     0.88),
        (0.22,   0.36,     0.66),
        (0.10,   0.18,     0.44),
        (0.03,   0.06,     0.24),
        (0.00,   0.00,     0.09),
        (0.00,   0.00,     0.00),
    ],
    # Sparks outlive the flame and keep travelling, so they carry their own two
    # curves rather than the flame's emission. They are cut off hard at the end
    # rather than left to fade: a thin low-alpha streak 30px out has nothing
    # dark behind it, and on the field that is not a dying spark, it is grit.
    "spark_life": (0.80, 1.00, 0.96, 0.84, 0.68, 0.50, 0.32, 0.17, 0.06, 0.00),
    "spark_reach": (0.30, 0.62, 0.85, 1.00, 1.10, 1.18, 1.24, 1.29, 1.32, 1.34),

    # --- colour --------------------------------------------------------------
    # Steel struck hard does go white-hot, and unlike the engine fire this lasts
    # two frames rather than for ever, so the top of the ramp is allowed to be a
    # hot yellow. It is still not a white stop: green and blue never meet red,
    # and the white core - if there is one - is left to additive overlap, which
    # makes it out of coloured parts and keeps the edges coloured.
    # Green is held down hard at the top. With red already at 1.0 the only way
    # the hot part can go pale is green climbing with it, and three additive
    # shells stacking is exactly what makes it climb: at (1.00, 0.78, 0.38) the
    # core composited to (1, 1, 0.68) - a clean pale yellow, saved from white
    # only by the blue that was never there.
    "ramp": [
        (0.00, (1.00, 0.55, 0.16), 1.00),
        (0.11, (1.00, 0.40, 0.09), 0.94),
        (0.30, (1.00, 0.26, 0.05), 0.80),
        (0.64, (0.92, 0.14, 0.03), 0.48),
        (1.00, (0.60, 0.06, 0.02), 0.15),
    ],
    # A dark warm lip holds the shape together; fading the rim to nothing is
    # what made the muzzle flash mushy. Kept gentler than the flash's 1.35
    # because half the mass here is streaks, and a streak is mostly rim.
    "rim_colour": (0.42, 0.17, 0.06),
    "rim_reach": 0.55,
    "rim_falloff": 1.15,
    "opacity": 0.92,
}


DUST = {
    "hull": "Hull",
    "name": "Dust",
    "elevation": 30.0,
    "azimuth": 0.0,
    "scale_units": None,
    "origin": None,

    "scale": 1.0,
    "density": 1.0,

    # Puffs around the point of impact, in hull lengths. They sit *around* it
    # rather than flying out with the fire - what leaves at speed is the sparks.
    # Wide enough to sit under the whole burst *and* solid enough to have no
    # holes in it, which are two requirements and only the first is obvious. A
    # ring of separated puffs measures a fine 98 levels at its darkest and still
    # lets the field through between them - and where the field comes through,
    # the fire added on top of it composites to (0.90, 0.71, 0.56), which is
    # tan. The burst's colour was never the problem either time it looked wrong.
    "puffs": 20,
    "spread": 0.070,
    "puff_radius": (0.028, 0.055),
    "rings": 7,
    "segments": 10,
    "seed": 57,

    # Dust grows while the fire dies and outlives it. It never shrinks, it
    # thins - the opposite of the burst, and legitimate, because it occludes:
    # a thinning occluder goes back to being ground, which is what it should do.
    # It has to be there from the first frame, not build up: what it is for is
    # backing the burst, and the burst is brightest at phase 0.
    "phases": [
        # size  alpha
        (0.40,  0.55),
        (0.68,  0.82),
        (0.86,  0.92),
        (0.98,  0.94),
        (1.06,  0.90),
        (1.12,  0.82),
        (1.17,  0.70),
        (1.21,  0.55),
        (1.24,  0.38),
        (1.27,  0.22),
    ],
    # Grey-brown, and *dark*: this is the thing the burst is added onto, so how
    # dark it is decides how much colour the burst can keep. At (0.25, 0.22,
    # 0.19) it laid down 0.33 grey and the fire composited to (1.00, 0.79,
    # 0.79) - peach, not fire. The lever for a pale-looking additive effect is
    # what is underneath it, not the effect.
    "colour": (0.15, 0.13, 0.12),
    "rim_colour": (0.07, 0.06, 0.055),
    "rim_reach": 0.75,
    "rim_falloff": 1.10,
    "opacity": 0.72,
}

BURST_MATERIAL = "_hit_burst"
DUST_MATERIAL = "_hit_dust"


def _sibling(name):
    """Load a module that lives next to this one."""
    here = (os.path.dirname(os.path.abspath(__file__))
            if "__file__" in globals() else REPO)
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(here, "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_CACHE = {}


def _mod(name):
    if name not in _CACHE:
        _CACHE[name] = _sibling(name)
    return _CACHE[name]


# ---------------------------------------------------------------------------
# where and how big
# ---------------------------------------------------------------------------

def scale_of(cfg):
    """One hull length in world units."""
    if cfg.get("scale_units") is not None:
        return float(cfg["scale_units"])
    hull = _mod("tank_parts").mesh(cfg["hull"])
    if "hit_scale" not in hull.keys():
        raise RuntimeError("no 'hit_scale' on %s - run hit_point.py first"
                           % hull.name)
    return float(hull["hit_scale"])


def origin_of(cfg):
    """Where the burst is built.

    The ring axis, at the middle of the tank's own height. Near enough to the
    frame's anchor that a 192px tile is well used, and *not* claimed to be the
    anchor: the offset table is measured from here, so this only has to be
    stable, not to agree with the renderer's pivot.
    """
    if cfg.get("origin") is not None:
        return Vector([float(v) for v in cfg["origin"]])
    hp = _mod("hit_point")
    tp = _mod("tank_parts")
    scene = bpy.context.scene
    hull = tp.mesh(cfg["hull"], scene)
    # The tank's own height, so both layer roots and everything under them. The
    # turret used to be looked up as the literal "Turret", behind the config's
    # back: on a parts-built scene that found nothing, and the burst sat at half
    # the hull's height instead of half the tank's.
    group = list(tp.hierarchy(scene.objects[cfg["hull_root"]]
                              if cfg.get("hull_root") else hull))
    turret = (scene.objects[cfg["turret_root"]] if cfg.get("turret_root")
              else tp.mesh(cfg["turret"], scene, required=False))
    if turret is not None:
        group += list(tp.hierarchy(turret))
    lo, hi = hp._bounds([o for o in group if o.type == "MESH"])
    if "ring_axis" in hull.keys():
        axis = [float(v) for v in hull["ring_axis"]]
        return Vector((axis[0], axis[1], (lo.z + hi.z) / 2.0))
    return Vector(((lo.x + hi.x) / 2.0, (lo.y + hi.y) / 2.0, (lo.z + hi.z) / 2.0))


def view_axis(cfg):
    """Unit vector from the subject toward the camera, straight out of the
    renderer's own camera rather than re-derived from the angles."""
    sa = _mod("sprite_atlas")
    m = sa.camera_matrix(Vector((0.0, 0.0, 0.0)), cfg["azimuth"],
                         cfg["elevation"], 1.0)
    return m.translation.normalized()


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

def _sphere(cfg, radius, rings, segments, seed):
    """A lumpy ball centred on the origin, as (verts, faces)."""
    mf = _mod("muzzle_flash")
    phi = np.linspace(0.08, math.pi - 0.08, rings)
    theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
    base = np.stack([
        (np.sin(phi)[:, None] * np.cos(theta)[None, :]).reshape(-1),
        (np.sin(phi)[:, None] * np.sin(theta)[None, :]).reshape(-1),
        np.repeat(np.cos(phi), segments)], axis=1)
    lump = 1.0 + cfg["wobble"] * (
        2.0 * mf._hash01(np.arange(rings * segments), seed) - 1.0)
    verts = base * (radius * lump)[:, None]
    verts = np.vstack([verts, [[0.0, 0.0, -radius]], [[0.0, 0.0, radius]]])
    faces = mf._grid_faces(rings, segments, len(verts) - 2, len(verts) - 1)
    return verts, faces


def _spray(spec, scale, reach, seed):
    """Tapered streaks thrown out near the screen plane.

    `muzzle_flash._streaks` puts its streaks in a cone about the axis, which is
    right for something leaving a barrel. These leave a *surface* seen face on,
    so they belong near the equator of the view axis - a ring, not a cap.

    The turn angles are spread evenly and then jittered rather than hashed
    outright: pure hashing clumps, and a spray with a bald patch reads as an
    error rather than as chaos.
    """
    mf = _mod("muzzle_flash")
    count = int(spec["count"])
    if count <= 0:
        return np.zeros((0, 3)), [], np.zeros((0, 4))

    index = np.arange(count)
    tilt = np.radians(spec["tilt"]
                      + spec["spread"] * (2.0 * mf._hash01(index, seed + 1) - 1.0))
    turn = 2.0 * math.pi * (
        (index + spec["even"] * (2.0 * mf._hash01(index, seed + 2) - 1.0)) / count)
    span = scale * spec["reach"] * reach
    lo, hi = spec["length"]
    length = span * (lo + (hi - lo) * mf._hash01(index, seed + 3))
    thick = scale * spec["radius"] * (0.6 + 0.8 * mf._hash01(index, seed + 4))
    s0, s1 = spec["start"]
    start = span * (s0 + (s1 - s0) * mf._hash01(index, seed + 5))

    rings, segments = 4, 5
    colour_rgb = np.asarray(spec["colour"], dtype=np.float64)
    verts, faces, colours, offset = [], [], [], 0
    for k in range(count):
        direction = np.array([
            math.sin(tilt[k]) * math.cos(turn[k]),
            math.sin(tilt[k]) * math.sin(turn[k]),
            math.cos(tilt[k])])
        u, v = mf._perpendicular(direction)
        t = np.linspace(0.15, 0.95, rings)
        r = thick[k] * (1.0 - t) ** 0.6
        theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
        along = start[k] + length[k] * t

        piece = (along[:, None, None] * direction
                 + (r[:, None] * np.cos(theta)[None, :])[:, :, None] * u
                 + (r[:, None] * np.sin(theta)[None, :])[:, :, None] * v)
        piece = piece.reshape(-1, 3)
        piece = np.vstack([piece, (start[k] * direction)[None, :],
                           ((start[k] + length[k]) * direction)[None, :]])
        faces.extend(mf._grid_faces(rings, segments, offset + len(piece) - 2,
                                    offset + len(piece) - 1, offset))
        verts.append(piece)

        fade = np.concatenate([np.repeat(1.0 - 0.75 * t, segments), [1.0, 0.08]])
        rgba = np.empty((len(piece), 4))
        rgba[:, :3] = colour_rgb
        rgba[:, 3] = fade
        colours.append(rgba)
        offset += len(piece)

    return np.vstack(verts), faces, np.vstack(colours)


def _spatter(cfg, spec, scale, reach, seed):
    """A cluster of hot blobs thrown out near the screen plane.

    Everything about a blob - where it is, how big, how hot - comes from its
    own hash, so no two are alike and the cluster has no axis of symmetry to
    read as a pattern. `at` maps distance onto the colour ramp, which puts the
    yellow in the middle and the red at the edge without moving anything.
    """
    mf = _mod("muzzle_flash")
    count = int(spec["count"])
    if count <= 0:
        return np.zeros((0, 3)), [], np.zeros((0, 4))

    index = np.arange(count)
    span = scale * spec["reach"] * reach
    turn = 2.0 * math.pi * (
        (index + spec["even"] * (2.0 * mf._hash01(index, seed + 1) - 1.0)) / count)
    tilt = np.radians(90.0 + spec["spread"]
                      * (2.0 * mf._hash01(index, seed + 2) - 1.0))
    d0, d1 = spec["distance"]
    dist = span * (d0 + (d1 - d0) * mf._hash01(index, seed + 3))
    r0, r1 = spec["radius"]
    # blobs grow a little as the cluster spreads, but not in step with it: at
    # `grow` 1.0 the whole thing is a uniform zoom and reads as one object
    # getting closer rather than as fragments flying apart
    rad = (scale * (r0 + (r1 - r0) * mf._hash01(index, seed + 4))
           * (1.0 - spec["grow"] + spec["grow"] * reach))
    a0, a1 = spec["at"]
    at = a0 + (a1 - a0) * (dist / max(float(span), 1e-9))

    verts, faces, colour, offset = [], [], [], 0
    for k in range(count):
        piece, quads = _sphere(cfg, rad[k], cfg["rings"], cfg["segments"],
                               seed + 11 + k)
        piece = piece + np.array([
            dist[k] * math.sin(tilt[k]) * math.cos(turn[k]),
            dist[k] * math.sin(tilt[k]) * math.sin(turn[k]),
            dist[k] * math.cos(tilt[k])])
        faces.extend(tuple(i + offset for i in face) for face in quads)
        colour.append(np.repeat(mf._ramp(cfg["ramp"], float(at[k])),
                                len(piece), axis=0))
        verts.append(piece)
        offset += len(piece)
    return np.vstack(verts), faces, np.vstack(colour)


def _burst_arrays(cfg, scale, core, emission, splash, spark_gain, spark_reach):
    mf = _mod("muzzle_flash")
    size = float(cfg.get("scale", 1.0))
    bright = float(cfg.get("brightness", 1.0))
    radius = scale * cfg["core"] * core * size

    parts = []
    for i, shell in enumerate(cfg["shells"]):
        verts, faces = _sphere(cfg, radius * shell["scale"], cfg["rings"],
                               cfg["segments"], cfg["seed"] + i * 101)
        rgba = np.repeat(mf._ramp(cfg["ramp"], shell["at"]), len(verts), axis=0)
        parts.append(((verts, faces, rgba), shell["gain"] * emission))

    parts.append((_spatter(cfg, cfg["spatter"], scale * size, splash,
                           cfg["seed"] + 401), cfg["spatter"]["gain"] * emission))
    parts.append((_spray(cfg["sparks"], scale * size, spark_reach,
                         cfg["seed"] + 811), cfg["sparks"]["gain"] * spark_gain))

    verts, faces, colour, offset = [], [], [], 0
    for (piece, quads, rgba), gain in parts:
        if not len(piece):
            continue
        verts.append(piece)
        faces.extend(tuple(i + offset for i in face) for face in quads)
        rgba = rgba.copy()
        rgba[:, 3] = np.clip(rgba[:, 3] * gain * bright, 0.0, 1.0)
        colour.append(rgba)
        offset += len(piece)
    if not verts:
        # every phase table ends at zero, and a mesh with no vertices is the
        # honest way to say "nothing here" - an invisible one still costs a draw
        return np.zeros((0, 3)), [], np.zeros((0, 4))
    return np.vstack(verts), faces, np.vstack(colour)


def _dust_arrays(cfg, scale, size, alpha):
    mf = _mod("muzzle_flash")
    size = size * float(cfg.get("scale", 1.0))
    alpha = min(alpha * float(cfg.get("density", 1.0)), 1.0)
    count = int(cfg["puffs"])
    index = np.arange(count)
    seed = cfg["seed"]

    spread = scale * cfg["spread"] * size
    off = [spread * (2.0 * mf._hash01(index, seed + 1 + i) - 1.0) for i in range(3)]
    r0, r1 = cfg["puff_radius"]
    puff = scale * (r0 + (r1 - r0) * mf._hash01(index, seed + 4)) * size

    verts, faces, colour, offset = [], [], [], 0
    for k in range(count):
        piece, quads = _sphere({"wobble": 0.32}, puff[k], cfg["rings"],
                               cfg["segments"], seed + 7 + k)
        piece = piece + np.array([off[0][k], off[1][k], off[2][k]])
        faces.extend(tuple(i + offset for i in face) for face in quads)
        rgba = np.empty((len(piece), 4))
        rgba[:, :3] = np.asarray(cfg["colour"]) * (0.72 + 0.5 * mf._hash01(k, seed + 5))
        rgba[:, 3] = alpha
        verts.append(piece)
        colour.append(rgba)
        offset += len(piece)
    return np.vstack(verts), faces, np.vstack(colour)


# ---------------------------------------------------------------------------
# entry points
# ---------------------------------------------------------------------------

def _place(cfg, material):
    scene = bpy.context.scene
    ob = scene.objects.get(cfg["name"])
    if ob is None:
        ob = bpy.data.objects.new(cfg["name"], bpy.data.meshes.new(cfg["name"]))
        scene.collection.objects.link(ob)
    if ob.parent is not None:
        # see the module docstring: parented to the hull it would be drawn into
        # the hull atlas, on every frame, for good
        ob.parent = None
    ob.matrix_world = (Matrix.Translation(origin_of(cfg))
                       @ Vector((0.0, 0.0, 1.0))
                         .rotation_difference(view_axis(cfg)).to_matrix().to_4x4())
    ob.data.materials.clear()
    ob.data.materials.append(material)
    return ob


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    mf = _mod("muzzle_flash")
    ob = _place(cfg, mf.build_material(cfg, BURST_MATERIAL))
    set_phase(0, len(cfg["phases"]), cfg)
    return ob


def set_phase(index, count, cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    mf = _mod("muzzle_flash")
    core, emission, splash = mf._sample(cfg["phases"], index, count, 3)
    spark_gain = mf._sample([(v,) for v in cfg["spark_life"]], index, count, 1)[0]
    spark_reach = mf._sample([(v,) for v in cfg["spark_reach"]], index, count, 1)[0]
    ob = bpy.context.scene.objects[cfg["name"]]
    mf._write_mesh(ob.data, *_burst_arrays(cfg, scale_of(cfg), core, emission,
                                           splash, spark_gain, spark_reach))
    return {"phase": index, "core": core, "emission": emission,
            "splash": splash, "sparks": spark_gain}


def phase_hook(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))

    def hook(index, count):
        set_phase(index, count, cfg)

    return hook


def build_dust(cfg=None):
    cfg = dict(DUST, **(cfg or {}))
    mf = _mod("muzzle_flash")
    ob = _place(cfg, mf.build_material(cfg, DUST_MATERIAL))
    set_dust_phase(0, len(cfg["phases"]), cfg)
    return ob


def set_dust_phase(index, count, cfg=None):
    cfg = dict(DUST, **(cfg or {}))
    mf = _mod("muzzle_flash")
    size, alpha = mf._sample(cfg["phases"], index, count, 2)
    ob = bpy.context.scene.objects[cfg["name"]]
    mf._write_mesh(ob.data, *_dust_arrays(cfg, scale_of(cfg), size, alpha))
    return {"phase": index, "size": size, "alpha": alpha}


def dust_phase_hook(cfg=None):
    cfg = dict(DUST, **(cfg or {}))

    def hook(index, count):
        set_dust_phase(index, count, cfg)

    return hook


def remove(cfg=None):
    """Take the burst and the dust back out of the scene."""
    cfg = cfg or {}
    scene = bpy.context.scene
    for name in (cfg.get("name", CONFIG["name"]), DUST["name"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)
    for name in (BURST_MATERIAL, DUST_MATERIAL):
        mat = bpy.data.materials.get(name)
        if mat is not None:
            bpy.data.materials.remove(mat)


if __name__ == "__main__":
    build(CONFIG)
    build_dust(DUST)
