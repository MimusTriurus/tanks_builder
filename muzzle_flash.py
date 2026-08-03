"""
The muzzle flash, as geometry, built on the stamped muzzle.

Run `build()` and `build_smoke()` to make the objects, then hand `phase_hook()`
and `smoke_phase_hook()` to `sprite_atlas` layers with `"fit": False` and
`"phases": N`. `tank_pipeline` does all of that.

Two objects, and they are two layers on purpose
-----------------------------------------------
`Flash` emits and is composited **additively**; `Smoke` occludes and is
composited with **normal alpha**. That is not a stylistic split, it is the only
honest one: fire adds light to what is behind it and smoke takes it away, and a
single layer cannot do both. The same conclusion was reached the hard way when
keying the bought sheet - see key_on_white.ps1 in CLAUDE.md.

The whole reason to render it rather than paint it
--------------------------------------------------
A flat sheet has to be rotated and squashed into place, and that only works
where the gun runs across the screen. Rendered, the foreshortening is not
modelled at all - it just happens, because the effect is in the scene with the
gun. It also removes the muzzle search from the harness, keeps the flash on the
same 30-degree frame grid as the turret it belongs to, and lets the tank occlude
it. See MUZZLE_FLASH.md.

Do not parent these to the turret
---------------------------------
Mirror of the trap that catches `Barrel`, and it bites the other way. `Barrel`
*must* be a child of `Turret`. `Flash` and `Smoke` must *not* be, because the
turret layer targets `Turret` and would draw them into the turret atlas - on
every frame, permanently. They sit at scene root, where no tank layer's
hierarchy reaches them, and `render_atlas` spins them about the shared pivot.

Phases rebuild the mesh
-----------------------
Not a shader parameter, not a keyframe. Rebuilding is deterministic, holds no
scene state between passes, and needs no frame number - which matters because
the renderer already drives one loop over angles. It also makes the whole
animation a table of numbers, which is the form that gets edited when someone
looks at the result and wants it different.

What the reference taught
-------------------------
Tuned against a hand-drawn 8-frame sheet and against VFX practice, after the
first version came out pale. Four things changed, and each was a distinct
mistake:

- **One element is not enough.** A good flash is a bright round core, a
  directional plume, smoke and sparks. The first version had only the plume,
  and a plume with no hot spot reads as fog. Sparks in particular are named in
  practice as one of the three things separating a mediocre flash from a good
  one - and games exaggerate them past the reference.
- **Even brightness reads as fog.** Shells all at one strength spread the light
  through the volume. The core has to be much brighter than everything else,
  and everything else has to fall away from it.
- **Hand-picked fire colour drifts to white.** The first ramp spent its first
  18% at (1.00, 0.97, 0.90) - all but white - over exactly the stretch that
  should carry the most colour contrast. The stops below are measured off the
  reference sheet; a stop may also be given as a colour temperature in kelvin,
  which cannot drift white by construction.
- **A silhouette needs a dark edge.** The reference is cel-shaded and outlined;
  its rim measures (0.48, 0.29, 0.16), a dark brown, not a faded orange. Fading
  the rim to nothing is what made the shape mushy, so the rim now darkens
  toward that colour before it fades.

Sizes are in muzzle radii, never world units
--------------------------------------------
`muzzle_radius` comes off the model, so a flash specified as "six radii long"
is the same flash on a light tank and a heavy one - which is why there is no
separate asset .blend and nothing to keep in sync between the three scenes.
Tune it on `flash_bench.py`, which reads this same CONFIG without a tank in the
way.
"""

import importlib.util
import math
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() \
    else r"D:\Projects\AgentCoding\BlenderMCP"
_SIBLINGS = {}


def _sibling(name):
    """Load a module that lives next to this one, once. See muzzle_point."""
    if name not in _SIBLINGS:
        spec = importlib.util.spec_from_file_location(
            name, os.path.join(REPO, "%s.py" % name))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        _SIBLINGS[name] = mod
    return _SIBLINGS[name]

CONFIG = {
    "turret": "Turret",
    "name": "Flash",
    # (point, direction, bore radius) instead of reading the turret's stamp.
    # For the tuning bench, which has no tank in it. In a tank scene leave this
    # alone: the stamp is the measurement, and a muzzle typed in by hand is one
    # that stops tracking the model.
    "muzzle": None,

    # --- the three master knobs ---------------------------------------------
    # Everything below is shape; these three are how much of it there is, and
    # they are what gets turned when someone looks at the result and says
    # bigger, brighter, less see-through. They multiply through the tables
    # rather than replacing them, so the animation keeps its curve.
    #
    #   scale       overall size - multiplies both length and width
    #   brightness  overall alpha - multiplies every strength and gain
    #   opacity     how solid a single surface is, before anything stacks
    #
    # brightness past about 1.5 stops buying brightness and starts buying
    # white: alpha clips at 1, and additive compositing turns any overlap of
    # bright pixels white on its own. If more punch is wanted without losing
    # the colour, the lever is the game's blend mode - screen instead of add -
    # not this number. VFX practice says the same: additive goes to white too
    # drastically.
    "scale": 1.35,
    "brightness": 1.40,

    # --- size, in muzzle radii ----------------------------------------------
    "length": 6.0,
    # The body has to be the biggest thing on screen or the tongues and sparks
    # take over and it reads as a firework rather than as burning gas.
    "width": 3.2,
    # start the base inside the brake so the mesh's closed end never shows
    "recess": 0.6,

    # --- shape ---------------------------------------------------------------
    "segments": 44,           # around the bore; the spikes need the resolution
    "rings": 18,              # along it
    # A flash is not a cone. Seen from the side it is a bloom that swells just
    # past the muzzle and tapers to a point, which is a Beta profile: t**a
    # falling in from the base, (1-t)**b tapering out to the tip. The peak
    # lands at a/(a+b), so these put the widest part a third of the way out.
    "profile": (0.45, 0.90),
    # Ripple on the body, and only ripple. Driving this hard was an attempt to
    # get the reference's starburst out of the cross-section, and it cannot
    # come from there: modulating the radius makes points that stick out
    # *sideways*, so at depth 0.62 the flash came out a dandelion. The
    # reference's spikes run forward and outward - they are separate tongues,
    # below. An odd count avoids the mirror symmetry that reads as machined.
    "petals": 9,
    "petal_depth": 0.20,
    "petal_sharpness": 1.6,
    # lobes of one size read as a gear; hashed per lobe they read as fire
    "petal_jitter": 0.35,
    # radians of twist from base to tip, so the spikes are not parallel ridges
    "twist": 0.55,
    "lump": 0.11,
    "seed": 7,

    # --- how it burns down ---------------------------------------------------
    # length, width and brightness per phase, as fractions of the peak.
    #
    # The sizes are the reference sheet's own curve: its lit area runs
    # 0.16 0.57 1.00 1.00 0.66 0.52 0.35 0.06 of peak, and area goes as the
    # square of size. The shape therefore peaks at phases 2-3 while the light
    # peaks at phase 0, and that gap is the effect - the flash is brightest the
    # instant it leaves the barrel and is still growing as it dims, which is
    # what makes it read as expanding gas rather than a lamp switched on.
    "phases": [
        # length  width  emission
        (0.42,    0.40,  1.00),
        (0.80,    0.76,  1.00),
        (1.00,    1.00,  0.92),
        (1.02,    1.00,  0.78),
        (0.86,    0.81,  0.54),
        (0.74,    0.72,  0.32),
        (0.62,    0.59,  0.15),
        (0.30,    0.25,  0.04),
    ],

    # --- colour --------------------------------------------------------------
    # Stops of (position, colour, strength). Colour is either an rgb triple or
    # a temperature in kelvin - a bare number is read as kelvin and run through
    # the blackbody curve.
    #
    # The hue stays inside 0..1 and the strength goes into alpha, rather than
    # the obvious arrangement of an HDR colour at full alpha. The scene renders
    # on the Standard view transform - no tone mapping - so an emission of 9.0
    # does not come out nine times brighter, it comes out clipped to white.
    # Alpha has the range that colour does not, and the game composites this
    # additively, so overlapping bright pixels make their own white-hot core
    # out of coloured parts.
    # The white core is kept deliberately short. Additive compositing turns any
    # overlap of bright pixels white on its own, so a ramp that is pale for the
    # first 40% ends up white for the first 70% on screen - which is how the
    # measured colour kept failing to arrive.
    "ramp": [
        (0.00, (1.00, 0.97, 0.73), 1.00),
        (0.10, (0.99, 0.86, 0.42), 0.95),
        (0.28, (0.99, 0.62, 0.16), 0.78),
        (0.60, (0.94, 0.38, 0.07), 0.46),
        (1.00, (0.62, 0.26, 0.09), 0.12),
    ],
    # Colour of the silhouette's edge, and how far in it reaches. Measured off
    # the reference's outline. Without it the rim just fades and the shape goes
    # mushy; with it there is a dark lip holding the spikes together.
    "rim_colour": (0.48, 0.29, 0.16),
    "rim_reach": 0.55,
    # How hard the silhouette fades. Layer Weight's Facing is 1 head-on and 0
    # at grazing, so raising it to a power and mixing toward Transparent makes
    # the rim melt while the middle stays bright.
    "rim_falloff": 1.35,
    # How solid one surface is head-on, before the shells stack. Kept under 1
    # so the shells behind still show through; at 1.0 the outermost one becomes
    # a wall and the volume inside it is wasted.
    "opacity": 0.95,

    # --- shells --------------------------------------------------------------
    # Nested copies: width and length as fractions of the full size, and `gain`
    # scaling the strength. Translucent shells accumulate into a gradient where
    # one surface would draw its own silhouette crisply and read as a lumpy
    # solid - the first attempt looked like a seed pod.
    #
    # The gains are what came out of the flash reading pale. Even strength
    # spreads the brightness through the plume, and evenly bright reads as fog;
    # practice is to keep the core extremely bright and let it fall away
    # outward. So the outer shells are dimmed and a short, fat, near-solid core
    # is added at the muzzle - the hot spot the first version had nowhere.
    # The core gain used to be 1.35, which drove alpha to 1 over a colour that
    # is nearly white to begin with. Additive compositing then piled the
    # tongues and sparks on top of it and the whole muzzle clipped out - all
    # the colour measured off the reference never reached the screen. Keeping
    # every gain at or under 1 leaves headroom for the sum to be the hot part.
    "shells": (
        {"scale": 1.00, "length": 1.00, "gain": 0.62},
        {"scale": 0.72, "length": 0.86, "gain": 0.80},
        {"scale": 0.46, "length": 0.62, "gain": 0.94},
        {"scale": 0.26, "length": 0.32, "gain": 1.00},   # the core
    ),

    # --- tongues and sparks --------------------------------------------------
    # The reference's starburst, built the way it is actually drawn: fat
    # tapered tongues of flame splaying forward from the muzzle, and thinner,
    # longer, brighter sparks flying further. Both are streaks in a cone; they
    # differ in width, reach and colour.
    #
    # Sparks are named in VFX practice as one of the three things separating a
    # mediocre flash from a good one, and games exaggerate them past reference.
    # But eighteen of them at 1.7 plume lengths reached 100px against a 59px
    # tile budget and read as a firework, so they start further out and stop
    # sooner now. `start` keeps them off the muzzle: bunched at the origin they
    # add their brightness where the core already clips.
    # Tongues are flame *mass*, not needles. Thirteen of them at 0.4 radii
    # thick came out a sparkler: a spray of bright pins with no body behind it,
    # while the reference is a big burning shape that spikes at its edge. Fewer
    # and much fatter, so each one reads as a lobe of fire.
    "tongues": {
        "count": 8, "cone": 50.0,
        "length": (0.35, 0.78),     # of the plume's length
        "radius": 1.05,             # of the bore radius, at the base
        "start": (0.05, 0.70),      # bore radii out from the muzzle
        "colour": (1.00, 0.60, 0.14),
        "gain": 0.85,
    },
    "sparks": {
        "count": 9, "cone": 26.0,
        "length": (0.45, 0.95),
        "radius": 0.085,
        "start": (1.2, 3.0),
        "colour": (1.00, 0.88, 0.52),
        "gain": 0.70,
    },
    # Sparks outlive the flame and keep travelling - on the reference they are
    # still flying three frames after the fire has gone.
    "spark_life": (0.55, 1.00, 1.00, 0.95, 0.84, 0.68, 0.48, 0.28),
    "spark_reach": (0.45, 0.80, 1.00, 1.08, 1.16, 1.24, 1.32, 1.40),
}


SMOKE = {
    "turret": "Turret",
    "name": "Smoke",
    "muzzle": None,

    # the same two master knobs as the fire, so the pair can be grown together
    "scale": 1.30,
    "density": 1.20,

    # Puffs, in muzzle radii. They hang *around and behind* the muzzle rather
    # than flying out with the fire - on the reference the smoke's centre sits
    # at -44 px against the flame's -27, i.e. further back every frame.
    # The first pass had 22 puffs spread 2.6 radii wide with puffs up to 1.35
    # radii across, and the cloud swallowed the barrel: about 46px of smoke
    # around a 55px flame. The reference's smoke is a modest cluster hugging
    # the muzzle, not a weather system.
    "puffs": 14,
    "spread": 1.35,            # across the bore
    "along": (-1.0, 2.2),      # behind the muzzle .. ahead of it
    "puff_radius": (0.35, 0.80),
    "rings": 7,
    "segments": 10,
    "seed": 21,

    # Smoke grows while the fire dies and outlives it: on the reference the
    # last frame is 3353 px of smoke against 1136 of flame, and its alpha falls
    # 0.99 -> 0.43. It never shrinks - it thins.
    "phases": [
        # size   alpha
        (0.30,   0.50),
        (0.58,   0.78),
        (0.80,   0.90),
        (0.95,   0.90),
        (1.05,   0.84),
        (1.12,   0.72),
        (1.16,   0.58),
        (1.20,   0.40),
    ],
    # Warm dark grey, measured off the reference's late frames - its smoke sits
    # near (0.30, 0.24, 0.19) - with a darker edge so each puff reads as a
    # shape rather than as a smudge. It came out pale first time round because
    # a dozen translucent puffs stack, and each one lightens what is behind it:
    # the colour to aim at is the *stack's*, not one puff's.
    "colour": (0.34, 0.29, 0.24),
    "rim_colour": (0.16, 0.13, 0.11),
    "rim_reach": 0.7,
    "rim_falloff": 1.1,
    "opacity": 0.68,
}

FIRE_MATERIAL = "_muzzle_flash"
SMOKE_MATERIAL = "_muzzle_smoke"


# ---------------------------------------------------------------------------
# colour
# ---------------------------------------------------------------------------

def blackbody(kelvin):
    """Approximate sRGB of a blackbody at `kelvin`, normalised to max 1.

    Normalised because hue is all that is wanted here - how much of it there is
    lives in alpha. A ramp stop given as a bare number goes through this, which
    is the parameterisation that cannot drift toward white by accident.
    """
    t = np.clip(np.asarray(kelvin, dtype=np.float64), 1000.0, 40000.0) / 100.0
    red = np.where(t <= 66.0, 255.0,
                   329.698727446 * np.power(np.maximum(t - 60.0, 1e-6), -0.1332047592))
    green = np.where(t <= 66.0,
                     99.4708025861 * np.log(np.maximum(t, 1e-6)) - 161.1195681661,
                     288.1221695283 * np.power(np.maximum(t - 60.0, 1e-6), -0.0755148492))
    blue = np.where(t >= 66.0, 255.0,
                    np.where(t <= 19.0, 0.0,
                             138.5177312231 * np.log(np.maximum(t - 10.0, 1e-6))
                             - 305.0447927307))
    rgb = np.clip(np.stack([red, green, blue], axis=-1), 0.0, 255.0) / 255.0
    return rgb / np.maximum(rgb.max(axis=-1, keepdims=True), 1e-6)


def _stop_colour(value):
    if isinstance(value, (int, float)):
        return blackbody(float(value))
    return np.asarray(value, dtype=np.float64)


def _ramp(stops, t):
    """Sample the ramp at `t` -> (n, 4): hue in rgb, strength in alpha."""
    t = np.atleast_1d(np.asarray(t, dtype=np.float64))
    positions = np.array([s[0] for s in stops])
    colours = np.stack([_stop_colour(s[1]) for s in stops])
    strength = np.array([s[2] for s in stops])
    channels = [np.interp(t, positions, colours[:, c]) for c in range(3)]
    channels.append(np.interp(t, positions, strength))
    return np.stack(channels, axis=1)


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

def _hash01(*keys):
    """Deterministic values in [0, 1) from integer arrays.

    Hashed rather than random so two runs - or a render and the screenshot it
    is compared against - get the same flash.
    """
    x = np.zeros(np.broadcast(*keys).shape, dtype=np.uint64)
    for i, key in enumerate(keys):
        # mask the constant: the two odd multipliers sum past 2**64 and numpy
        # refuses to build a uint64 from it
        mix = np.uint64((0x9E3779B97F4A7C15 + i * 0x632BE59BD9B4E019)
                        & 0xFFFFFFFFFFFFFFFF)
        x ^= np.asarray(key).astype(np.int64).astype(np.uint64) * mix
    x ^= x >> np.uint64(29)
    x = (x * np.uint64(0xBF58476D1CE4E5B9)) & np.uint64(0xFFFFFFFFFFFFFFFF)
    x ^= x >> np.uint64(32)
    return (x & np.uint64(0xFFFFFF)).astype(np.float64) / float(0x1000000)


def _perpendicular(axis):
    axis = np.asarray(axis, dtype=np.float64)
    seed = np.array([0.0, 0.0, 1.0])
    if abs(float(np.dot(seed, axis))) > 0.9:
        seed = np.array([1.0, 0.0, 0.0])
    u = np.cross(axis, seed)
    u /= np.linalg.norm(u)
    return u, np.cross(axis, u)


def _grid_faces(rings, segments, base, tip, offset=0):
    faces = []
    for i in range(rings - 1):
        for j in range(segments):
            k = (j + 1) % segments
            faces.append((offset + i * segments + j, offset + i * segments + k,
                          offset + (i + 1) * segments + k,
                          offset + (i + 1) * segments + j))
    for j in range(segments):
        k = (j + 1) % segments
        faces.append((base, offset + k, offset + j))
        faces.append((tip, offset + (rings - 1) * segments + j,
                      offset + (rings - 1) * segments + k))
    return faces


def _spindle(cfg, radius, length, width, seed, spiky):
    """One spindle along local +Z: vertices, quad faces, and per-vertex rgba."""
    rings, segments = cfg["rings"], cfg["segments"]
    a, b = cfg["profile"]

    # keep the ends off 0 and 1: the profile vanishes there, and a ring of
    # coincident vertices makes degenerate faces. The caps are poles instead.
    t = np.linspace(0.06, 0.97, rings)
    profile = (t ** a) * ((1.0 - t) ** b)
    profile /= profile.max()

    theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
    if spiky:
        count = cfg["petals"]
        # |cos| to a power: a plain cosine gives soft scallops at any depth,
        # and the reference silhouette is made of points, not scallops
        ridge = np.abs(np.cos(count * theta / 2.0)) ** cfg["petal_sharpness"]
        # each spike its own length, or the whole thing reads as a gear
        which = np.floor(count * theta / (2.0 * math.pi)).astype(np.int64)
        ridge = ridge * (1.0 - cfg["petal_jitter"] * _hash01(which, seed))
        petal = 1.0 - cfg["petal_depth"] + cfg["petal_depth"] * ridge * 2.0
        # twist along the length so the spikes are not parallel ridges
        twist = cfg["twist"] * t
    else:
        petal = np.ones_like(theta)
        twist = np.zeros_like(t)

    rough = 1.0 + cfg["lump"] * (2.0 * _hash01(
        np.arange(rings)[:, None], np.arange(segments)[None, :], seed) - 1.0)

    angle = theta[None, :] + twist[:, None]
    if spiky:
        shape = np.empty((rings, segments))
        for i in range(rings):
            shape[i] = np.interp(np.mod(theta + twist[i], 2.0 * math.pi),
                                 theta, petal, period=2.0 * math.pi)
    else:
        shape = np.broadcast_to(petal[None, :], (rings, segments))

    r = radius * width * profile[:, None] * shape * rough
    span = radius * length
    base_z = radius * -cfg.get("recess", 0.0)

    verts = np.empty((rings * segments, 3))
    verts[:, 0] = (r * np.cos(angle)).reshape(-1)
    verts[:, 1] = (r * np.sin(angle)).reshape(-1)
    verts[:, 2] = np.repeat(base_z + span * t, segments)
    verts = np.vstack([verts, [[0.0, 0.0, base_z]], [[0.0, 0.0, base_z + span]]])

    faces = _grid_faces(rings, segments, len(verts) - 2, len(verts) - 1)
    colour = np.vstack([
        np.repeat(_ramp(cfg["ramp"], t), segments, axis=0),
        _ramp(cfg["ramp"], 0.0), _ramp(cfg["ramp"], 1.0),
    ])
    return verts, faces, colour


def _streaks(spec, radius, plume, reach, seed):
    """Tapered streaks splaying forward inside a cone.

    Both the flame tongues and the sparks are this; they differ only in how
    fat, how far and what colour. The reference's starburst is made of these,
    not of a lumpy cross-section - a radial ripple makes points that stick out
    sideways, which is a dandelion, not a muzzle flash.
    """
    count = int(spec["count"])
    if count <= 0:
        return np.zeros((0, 3)), [], np.zeros((0, 4))

    index = np.arange(count)
    cone = math.radians(spec["cone"])
    # sqrt so the directions spread evenly over the cap rather than crowding
    # the axis, which would put every streak on top of the plume
    tilt = cone * np.sqrt(_hash01(index, seed + 1))
    turn = 2.0 * math.pi * _hash01(index, seed + 2)
    lo, hi = spec["length"]
    length = plume * reach * (lo + (hi - lo) * _hash01(index, seed + 3))
    thick = radius * spec["radius"] * (0.6 + 0.8 * _hash01(index, seed + 4))
    s0, s1 = spec["start"]
    start = radius * (s0 + (s1 - s0) * _hash01(index, seed + 5))

    rings, segments = 4, 5
    colour_rgb = np.asarray(spec["colour"], dtype=np.float64)
    verts, faces, colours, offset = [], [], [], 0
    for k in range(count):
        direction = np.array([
            math.sin(tilt[k]) * math.cos(turn[k]),
            math.sin(tilt[k]) * math.sin(turn[k]),
            math.cos(tilt[k])])
        u, v = _perpendicular(direction)
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
        faces.extend(_grid_faces(rings, segments, offset + len(piece) - 2,
                                 offset + len(piece) - 1, offset))
        verts.append(piece)

        fade = np.concatenate([np.repeat(1.0 - 0.75 * t, segments), [1.0, 0.1]])
        rgba = np.empty((len(piece), 4))
        rgba[:, :3] = colour_rgb
        rgba[:, 3] = fade
        colours.append(rgba)
        offset += len(piece)

    return np.vstack(verts), faces, np.vstack(colours)


def _flash_arrays(cfg, radius, length, width, emission, spark_gain, spark_reach):
    scale = float(cfg.get("scale", 1.0))
    bright = float(cfg.get("brightness", 1.0))
    length, width = length * scale, width * scale
    plume = radius * length * cfg["length"]
    parts = [(_spindle(cfg, radius,
                       length * cfg["length"] * shell["length"],
                       width * shell["scale"], cfg["seed"] + i * 101, True),
              shell["gain"] * emission)
             for i, shell in enumerate(cfg["shells"])]
    # tongues burn with the flame; sparks keep flying after it has gone, so
    # they carry their own life curve rather than the flame's emission
    parts.append((_streaks(cfg["tongues"], radius, plume, 1.0, cfg["seed"] + 401),
                  cfg["tongues"]["gain"] * emission))
    parts.append((_streaks(cfg["sparks"], radius, plume, spark_reach,
                           cfg["seed"] + 811),
                  cfg["sparks"]["gain"] * spark_gain))

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
    return np.vstack(verts), faces, np.vstack(colour)


def _smoke_arrays(cfg, radius, size, alpha):
    """A cluster of puffs around the muzzle."""
    size = size * float(cfg.get("scale", 1.0))
    alpha = min(alpha * float(cfg.get("density", 1.0)), 1.0)
    count = int(cfg["puffs"])
    index = np.arange(count)
    rings, segments = cfg["rings"], cfg["segments"]
    seed = cfg["seed"]

    lo, hi = cfg["along"]
    along = radius * (lo + (hi - lo) * _hash01(index, seed + 1)) * size
    spread = radius * cfg["spread"] * size
    off_u = spread * (2.0 * _hash01(index, seed + 2) - 1.0)
    off_v = spread * (2.0 * _hash01(index, seed + 3) - 1.0)
    r0, r1 = cfg["puff_radius"]
    puff = radius * (r0 + (r1 - r0) * _hash01(index, seed + 4)) * size

    phi = np.linspace(0.08, math.pi - 0.08, rings)
    theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
    base = np.stack([
        (np.sin(phi)[:, None] * np.cos(theta)[None, :]).reshape(-1),
        (np.sin(phi)[:, None] * np.sin(theta)[None, :]).reshape(-1),
        np.repeat(np.cos(phi), segments)], axis=1)

    verts, faces, colour, offset = [], [], [], 0
    for k in range(count):
        wobble = 1.0 + 0.35 * (2.0 * _hash01(
            np.arange(rings * segments), seed + 5 + k) - 1.0)
        piece = base * (puff[k] * wobble)[:, None]
        piece += np.array([off_u[k], off_v[k], along[k]])
        piece = np.vstack([piece,
                           np.array([off_u[k], off_v[k], along[k] - puff[k]]),
                           np.array([off_u[k], off_v[k], along[k] + puff[k]])])
        faces.extend(_grid_faces(rings, segments, offset + len(piece) - 2,
                                 offset + len(piece) - 1, offset))
        rgba = np.empty((len(piece), 4))
        rgba[:, :3] = np.asarray(cfg["colour"]) * (0.7 + 0.5 * _hash01(k, seed + 6))
        rgba[:, 3] = alpha
        verts.append(piece)
        colour.append(rgba)
        offset += len(piece)
    return np.vstack(verts), faces, np.vstack(colour)


def _write_mesh(me, verts, faces, colour):
    me.clear_geometry()
    me.from_pydata(verts.tolist(), [], faces)
    # flat shading shows every facet and every ring of the construction, which
    # on a lumpy spindle reads as a machined part rather than as burning gas
    me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
    me.update()
    attr = me.color_attributes.get("flame")
    if attr is None or attr.domain != "POINT" or attr.data_type != "FLOAT_COLOR":
        if attr is not None:
            me.color_attributes.remove(attr)
        attr = me.color_attributes.new(name="flame", type="FLOAT_COLOR",
                                       domain="POINT")
    attr.data.foreach_set("color", colour.reshape(-1))
    me.update()


# ---------------------------------------------------------------------------
# material
# ---------------------------------------------------------------------------

def build_material(cfg, name):
    """Emission tinted by the vertex attribute, fading and darkening at the rim.

    The rim does two things, not one. It fades - Layer Weight's Facing is 1
    head-on and 0 at grazing - and it also *darkens* toward `rim_colour` before
    it goes. Fading alone left the silhouette mushy; the reference sheet is
    outlined, and its outline measures a dark brown rather than a faded orange.
    """
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()

    out = tree.nodes.new("ShaderNodeOutputMaterial")
    mix = tree.nodes.new("ShaderNodeMixShader")
    clear = tree.nodes.new("ShaderNodeBsdfTransparent")
    glow = tree.nodes.new("ShaderNodeEmission")
    attr = tree.nodes.new("ShaderNodeAttribute")
    weight = tree.nodes.new("ShaderNodeLayerWeight")
    fade = tree.nodes.new("ShaderNodeMath")
    by_alpha = tree.nodes.new("ShaderNodeMath")
    by_opacity = tree.nodes.new("ShaderNodeMath")
    edge = tree.nodes.new("ShaderNodeMath")
    tint = tree.nodes.new("ShaderNodeMixRGB")

    attr.attribute_name = "flame"
    glow.inputs["Strength"].default_value = 1.0
    weight.inputs["Blend"].default_value = 0.5
    fade.operation = "POWER"
    fade.inputs[1].default_value = cfg["rim_falloff"]
    by_alpha.operation = "MULTIPLY"
    by_opacity.operation = "MULTIPLY"
    by_opacity.inputs[1].default_value = cfg["opacity"]
    edge.operation = "POWER"
    edge.inputs[1].default_value = max(cfg["rim_reach"], 1e-3)
    tint.blend_type = "MIX"
    tint.inputs["Color2"].default_value = tuple(cfg["rim_colour"]) + (1.0,)

    tree.links.new(weight.outputs["Facing"], edge.inputs[0])
    tree.links.new(edge.outputs[0], tint.inputs["Fac"])       # 0 at the rim
    tree.links.new(attr.outputs["Color"], tint.inputs["Color1"])
    tree.links.new(tint.outputs["Color"], glow.inputs["Color"])

    tree.links.new(weight.outputs["Facing"], fade.inputs[0])
    tree.links.new(fade.outputs[0], by_alpha.inputs[0])
    tree.links.new(attr.outputs["Alpha"], by_alpha.inputs[1])
    tree.links.new(by_alpha.outputs[0], by_opacity.inputs[0])
    tree.links.new(by_opacity.outputs[0], mix.inputs["Fac"])
    tree.links.new(clear.outputs[0], mix.inputs[1])           # Fac 0 -> clear
    tree.links.new(glow.outputs[0], mix.inputs[2])
    tree.links.new(mix.outputs[0], out.inputs["Surface"])

    # EEVEE Next: surface_render_method is the one that decides, blend_method
    # is the older name kept alongside it. Set both - which one a given build
    # honours is not worth discovering from a wrong-looking render.
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "BLENDED"
    if hasattr(mat, "blend_method"):
        mat.blend_method = "BLEND"
    mat.use_backface_culling = False
    if hasattr(mat, "show_transparent_back"):
        mat.show_transparent_back = True
    return mat


# ---------------------------------------------------------------------------
# entry points
# ---------------------------------------------------------------------------

def muzzle_of(cfg):
    if cfg.get("muzzle") is not None:
        point, direction, radius = cfg["muzzle"]
        return (np.array(point, dtype=np.float64),
                np.array(direction, dtype=np.float64), float(radius))
    turret = _sibling("tank_parts").mesh(cfg["turret"])
    for key in ("muzzle_point", "muzzle_dir", "muzzle_radius"):
        if key not in turret.keys():
            raise RuntimeError("no %r on %s - run muzzle_point.py first"
                               % (key, turret.name))
    return (np.array([float(v) for v in turret["muzzle_point"]]),
            np.array([float(v) for v in turret["muzzle_dir"]]),
            float(turret["muzzle_radius"]))


def _place(cfg, material):
    scene = bpy.context.scene
    point, direction, _ = muzzle_of(cfg)
    ob = scene.objects.get(cfg["name"])
    if ob is None:
        ob = bpy.data.objects.new(cfg["name"], bpy.data.meshes.new(cfg["name"]))
        scene.collection.objects.link(ob)
    if ob.parent is not None:
        # see the module docstring: parented to the turret it would be drawn
        # into the turret atlas, on every frame, for good
        ob.parent = None
    z = Vector(direction.tolist()).normalized()
    ob.matrix_world = (Matrix.Translation(Vector(point.tolist()))
                       @ Vector((0.0, 0.0, 1.0)).rotation_difference(z)
                         .to_matrix().to_4x4())
    ob.data.materials.clear()
    ob.data.materials.append(material)
    return ob


def _sample(table, index, count, width):
    """Row `index` of `count` out of a table, interpolated."""
    position = 0.0 if count <= 1 else index * (len(table) - 1) / (count - 1)
    lo = int(math.floor(position))
    hi = min(int(math.ceil(position)), len(table) - 1)
    f = position - lo
    return [table[lo][k] * (1.0 - f) + table[hi][k] * f for k in range(width)]


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    ob = _place(cfg, build_material(cfg, FIRE_MATERIAL))
    set_phase(0, len(cfg["phases"]), cfg)
    return ob


def set_phase(index, count, cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    length, width, emission = _sample(cfg["phases"], index, count, 3)
    spark_gain = _sample([(v,) for v in cfg["spark_life"]], index, count, 1)[0]
    spark_reach = _sample([(v,) for v in cfg["spark_reach"]], index, count, 1)[0]
    _, _, radius = muzzle_of(cfg)
    ob = bpy.context.scene.objects[cfg["name"]]
    _write_mesh(ob.data, *_flash_arrays(cfg, radius, length, width, emission,
                                        spark_gain, spark_reach))
    return {"phase": index, "length": length, "width": width,
            "emission": emission, "sparks": spark_gain}


def phase_hook(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))

    def hook(index, count):
        set_phase(index, count, cfg)

    return hook


def build_smoke(cfg=None):
    cfg = dict(SMOKE, **(cfg or {}))
    ob = _place(cfg, build_material(cfg, SMOKE_MATERIAL))
    set_smoke_phase(0, len(cfg["phases"]), cfg)
    return ob


def set_smoke_phase(index, count, cfg=None):
    cfg = dict(SMOKE, **(cfg or {}))
    size, alpha = _sample(cfg["phases"], index, count, 2)
    _, _, radius = muzzle_of(cfg)
    ob = bpy.context.scene.objects[cfg["name"]]
    _write_mesh(ob.data, *_smoke_arrays(cfg, radius, size, alpha))
    return {"phase": index, "size": size, "alpha": alpha}


def smoke_phase_hook(cfg=None):
    cfg = dict(SMOKE, **(cfg or {}))

    def hook(index, count):
        set_smoke_phase(index, count, cfg)

    return hook


def remove(cfg=None):
    """Take the flash and the smoke back out of the scene."""
    cfg = cfg or {}
    scene = bpy.context.scene
    for name in (cfg.get("name", CONFIG["name"]), SMOKE["name"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)
    for name in (FIRE_MATERIAL, SMOKE_MATERIAL):
        mat = bpy.data.materials.get(name)
        if mat is not None:
            bpy.data.materials.remove(mat)


if __name__ == "__main__":
    build(CONFIG)
    build_smoke(SMOKE)
