"""
The engine's exhaust, as geometry, built on the stamped ports.

Run `build()` to make the object, then hand `phase_hook()` to a `sprite_atlas`
layer with `"fit": False` and `"phases": N`. `tank_pipeline` does all of that.

It is not a small muzzle flash
------------------------------
The two share the machinery and almost nothing else, and every difference below
is one that shows on screen if it is got wrong.

- **It loops.** A flash is an event with a first frame and a last one; exhaust
  has neither, and a loop that pops at the seam is the first thing anyone
  notices about it. So there is no per-phase table here. Each puff carries an
  *age* that advances by `1/phases` every phase and wraps, and everything about
  the puff - where it is, how big, how solid - is a function of that age alone.
  The last phase then hands over to the first by construction rather than by
  tuning, because `frac(x + 1) == frac(x)`. It is checked anyway, on the
  picture, because that is the house rule.

- **It belongs to the hull.** The port is bolted to the engine deck, so this
  layer is indexed by the *hull's* heading where the flash and its smoke are
  indexed by the turret's. Getting that backwards leaves the tank trailing smoke
  from wherever the gun happens to point.

- **It occludes, it does not emit.** Normal alpha, drawn like the muzzle smoke
  and unlike the fire.

- **It is small.** At MT's scale the tank is 154 px long, so a plume that reads
  as an idling engine is twenty or thirty pixels of haze. A flash is an event
  and may shout; this runs for the whole game and must not.

Sizes are in two units, not one
-------------------------------
The flash is specified entirely in muzzle radii and therefore transfers between
tanks untouched. This cannot be, and the reason is worth stating because the
obvious arrangement silently produces a plume six times too big.

A bore is a bore on any tank. A port is not: it is a 0.02 pipe on one model and
a 0.12 grille on the next. But a bigger grille does not mean a six times taller
plume - it means a wider one. How far exhaust rises and how much it spreads are
properties of the engine and the air, so they are quoted against
`exhaust_scale`, the hull's length. Only the size the smoke *leaves at* is
quoted against the port.

Do not parent this to anything
------------------------------
Same trap as `Flash`. The hull layer is the whole tank minus the turret, so a
`Plume` parented under the hull would be drawn into the hull atlas permanently.
It sits at scene root, where no tank layer's hierarchy reaches it, and holds its
vertices in world space so that one object can carry however many ports the tank
has. `render_atlas` spins it about the shared pivot like everything else.
"""

import importlib.util
import math
import os

import bpy
import numpy as np
from mathutils import Matrix

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

MATERIAL = "_engine_exhaust"

CONFIG = {
    "hull": "Hull",
    "name": "Plume",
    # Named in the config rather than fixed to the module, because this file is
    # driven by two callers now: the idle exhaust below, and the burning tank's
    # smoke column in `engine_fire`. `build_material` bakes rim colour, reach,
    # falloff and opacity into the node tree, so two configs sharing one
    # material name do not coexist - the second build silently rewrites the
    # first one's material and the plume comes out wearing the fire's settings.
    "material": MATERIAL,
    # [(point, dir, radius)], hull_length - instead of reading the hull's
    # stamp. For a bench with no tank in it. In a tank scene leave this alone.
    "ports": None,

    # --- the two master knobs -----------------------------------------------
    # `scale` is how much plume there is, `density` how solid it is. Everything
    # below is shape.
    "scale": 1.0,
    "density": 1.0,

    # --- the path, as fractions of the hull's length -------------------------
    # How far the tip of the plume ends up from the port. 0.24 of a hull is
    # about 37 px on MT, which is haze standing over the deck rather than a
    # smokestack.
    "rise": 0.24,
    # sideways wander by the time a puff dies
    "spread": 0.055,
    # How far the path has turned toward world up by full age. Exhaust leaves
    # along the port and then rises regardless of which way the port pointed,
    # which is the whole reason the path is integrated rather than being a
    # straight line along the axis: a rear pipe vents backwards and its plume
    # still ends up above the deck.
    "buoyancy": 0.80,
    # How the turn is distributed along the path. 1.0 spreads it evenly, which
    # is right for exhaust: it really does leave along the pipe and bend up
    # afterwards. Under 1.0 does most of the turning at once, which is what a
    # fire column wants - see engine_fire.SMOKE, where following the vent's aim
    # for half a hull both looked wrong and cost a tile size.
    "turn_power": 1.0,
    # Time constant of the slowing. The jet entrains air and gives its momentum
    # up, so most of the travel happens early and the puff then hangs and fades.
    # Large values make an even conveyor, which reads as a machine, not as air.
    "slowing": 0.45,

    # --- the puffs -----------------------------------------------------------
    # Radius at birth, against the *port*; growth by full age, against the
    # *hull*. The second term is what makes a pipe and a grille converge on the
    # same cone a few pixels out, which is what they do.
    #
    # These two are the ones to watch, and the failure they cause does not look
    # like a size problem. First pass had them at 0.60 and 0.075, which is
    # puffs of 10 to 21 px climbing a 27 px path: every puff overlapped every
    # other and the plume came out a flat smear sitting on the deck. It reads
    # as "too faint" and it is not - it is too fat. The path has to be several
    # puffs long before there is a plume rather than a blob.
    "puff_start": 0.45,
    "puff_grow": 0.048,
    "puff_power": 0.70,
    # Twenty, not the muzzle smoke's fourteen, and for a different reason:
    # those are a cluster and these are a queue. A queue of nine reads as a
    # string of bubbles - the puffs have to overlap *along the path* before it
    # reads as one body of smoke, and the smaller they are the more it takes.
    "puffs": 20,
    # Puffs evenly spaced in age tick like a metronome. Nudging each one off its
    # slot is free and safe: the offset is hashed on the puff, so it is the same
    # every phase and the wrap still lands exactly.
    "stagger": 0.35,
    "puff_vary": 0.45,        # spread of per-puff size
    # Much smoother than the muzzle smoke's 0.35, and the reason is where the
    # two get seen. A muzzle cloud hangs in open air for a tenth of a second
    # and wants shape. This lies over the hull for the whole game, and lumps at
    # this size stop reading as a volume in front of the armour and start
    # reading as dirt on it.
    "wobble": 0.16,
    "rings": 7,
    "segments": 10,
    "seed": 33,

    # --- life ----------------------------------------------------------------
    # Peak alpha of one puff, and the two ends of its life. Both ends must reach
    # exactly zero or the loop shows a seam: `onset` fades a puff up from
    # nothing at age 0 and `fade` takes it back to nothing at age 1, where the
    # next lap begins.
    # 0.46 was tuned on the check composite, which lays the layers on a 0.16
    # grey. The game's ground is a pale hex field around 0.72, and grey smoke on
    # a light background is a far weaker signal than the same smoke on a dark
    # one: measured in the harness it moved the picture by 20 levels out of 255
    # at its densest, which is nearly nothing. Tune against the background the
    # thing will actually be seen against.
    "alpha": 0.60,
    "onset": 0.14,
    "fade": 1.60,

    # Warm grey-brown, a touch darker than the muzzle smoke: that one is seen
    # against sky and gun flash, this one mostly against a mid-green hull, and
    # a haze the same value as what is behind it is not haze, it is a smudge.
    "colour": (0.28, 0.26, 0.25),
    # Barely darker than the body, where the muzzle smoke's rim is much darker.
    # That rim exists to draw each puff as a shape; here shapes are the problem,
    # so the edge only has to stop the puff ending abruptly.
    "rim_colour": (0.21, 0.20, 0.19),
    "rim_reach": 0.85,
    # Higher than the muzzle smoke's 1.1, so alpha falls away faster toward
    # grazing angles and each puff melts into the next instead of drawing its
    # own silhouette. This is the number that turns twenty blobs into one body.
    #
    # It is also the biggest single lever on how much plume there is, which is
    # not obvious and cost a round trip. Alpha goes as facing**this, and most of
    # a sphere faces the camera obliquely, so raising it from 1.1 to 1.6 to
    # soften the lumps quietly took about a third of the density with it. 1.25
    # keeps most of the softening and gives that back.
    "rim_falloff": 1.25,
    "opacity": 0.70,
}


def _sibling(name):
    """Load a module that lives next to this one - see exhaust_point._sibling."""
    here = (os.path.dirname(os.path.abspath(__file__))
            if "__file__" in globals() else REPO)
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(here, "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# the path
# ---------------------------------------------------------------------------

def _path(cfg, axis, reach, samples=96):
    """Where a puff is at every age: an integrated path, not a straight line.

    Returns (ages, positions) to interpolate into. The direction turns from the
    port's axis toward world up as the puff ages and the speed decays, so the
    result is a curve that leaves the port the way the port points and ends up
    going up. Swinging a straight line instead would move the *whole* plume,
    including the part still inside the pipe.
    """
    up = np.array([0.0, 0.0, 1.0])
    t = np.linspace(0.0, 1.0, samples)
    turn = np.clip(cfg["buoyancy"] * np.power(t, cfg.get("turn_power", 1.0)),
                   0.0, 1.0)[:, None]
    direction = (1.0 - turn) * axis[None, :] + turn * up[None, :]
    direction /= np.maximum(np.linalg.norm(direction, axis=1, keepdims=True), 1e-9)
    speed = np.exp(-t / max(cfg["slowing"], 1e-4))

    pos = np.cumsum(direction * speed[:, None], axis=0)
    pos -= pos[0]
    # normalise on the end *point*, so `rise` is how far the tip gets from the
    # port rather than how far it travelled getting there
    pos *= reach / max(float(np.linalg.norm(pos[-1])), 1e-9)
    return t, pos


def _ages(cfg, index, count):
    """Each puff's age this phase, in [0, 1).

    This one line is the loop. Age advances by `1/count` per phase for every
    puff at once, so phase `count` reproduces phase 0 exactly - there is no
    last frame to blend into the first, because there is no last frame.

    The stagger is hashed on the puff and not on the phase, which is the whole
    trick and is easy to get wrong: hash in the phase and every puff becomes a
    different puff every frame, the plume boils instead of drifting, and no
    amount of tuning the shape fixes it.
    """
    mf = _mf()
    puffs = int(cfg["puffs"])
    k = np.arange(puffs)
    slot = (k + 0.5) / puffs
    nudge = cfg["stagger"] * (2.0 * mf._hash01(k, cfg["seed"] + 1) - 1.0) / puffs
    return np.mod(slot + nudge + float(index) / max(int(count), 1), 1.0)


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

_MF = None


def _mf():
    global _MF
    if _MF is None:
        _MF = _sibling("muzzle_flash")
    return _MF


def _sphere(rings, segments):
    phi = np.linspace(0.08, math.pi - 0.08, rings)
    theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
    return np.stack([
        (np.sin(phi)[:, None] * np.cos(theta)[None, :]).reshape(-1),
        (np.sin(phi)[:, None] * np.sin(theta)[None, :]).reshape(-1),
        np.repeat(np.cos(phi), segments)], axis=1)


def _plume_arrays(cfg, ports, hull_length, index, count):
    """Every port's puffs at phase `index`, in world space."""
    mf = _mf()
    scale = float(cfg["scale"])
    density = float(cfg["density"])
    rings, segments = int(cfg["rings"]), int(cfg["segments"])
    base = _sphere(rings, segments)
    ages = _ages(cfg, index, count)
    puffs = len(ages)

    # size and solidity are functions of age alone - that is what closes the
    # loop, and it is why there is no phase table in this file
    grow = hull_length * cfg["puff_grow"] * scale
    spread = hull_length * cfg["spread"] * scale
    life = (1.0 - np.exp(-ages / max(cfg["onset"], 1e-4))) \
        * np.power(np.maximum(1.0 - ages, 0.0), cfg["fade"])
    alpha = np.clip(cfg["alpha"] * density * life, 0.0, 1.0)

    verts, faces, colour, offset = [], [], [], 0
    for p, (point, axis, radius) in enumerate(ports):
        axis = np.asarray(axis, dtype=np.float64)
        axis = axis / max(float(np.linalg.norm(axis)), 1e-9)
        u, v = mf._perpendicular(axis)
        seed = int(cfg["seed"]) + p * 977
        t, path = _path(cfg, axis, hull_length * cfg["rise"] * scale)

        along = np.stack([np.interp(ages, t, path[:, c]) for c in range(3)], axis=1)
        turn = 2.0 * math.pi * mf._hash01(np.arange(puffs), seed + 2)
        wander = spread * np.power(ages, 0.7) \
            * mf._hash01(np.arange(puffs), seed + 3)
        # Off-centre at birth as well, across the port's own width. Without it
        # every puff leaves through the exact middle and the base of the plume
        # is a needle - which is wrong for a pipe and very wrong for a grille,
        # where the smoke comes off the whole panel.
        born = radius * 0.7 * (2.0 * mf._hash01(np.arange(puffs), seed + 4) - 1.0)
        size = (radius * cfg["puff_start"] * scale
                + grow * np.power(ages, cfg["puff_power"])) \
            * (1.0 - cfg["puff_vary"]
               + 2.0 * cfg["puff_vary"] * mf._hash01(np.arange(puffs), seed + 5))

        for k in range(puffs):
            centre = (np.asarray(point, dtype=np.float64) + along[k]
                      + (wander[k] * math.cos(turn[k]) + born[k]) * u
                      + (wander[k] * math.sin(turn[k]) + born[k]) * v)
            lump = 1.0 + cfg["wobble"] * (2.0 * mf._hash01(
                np.arange(rings * segments), seed + 6 + k) - 1.0)
            piece = base * (size[k] * lump)[:, None] + centre
            piece = np.vstack([piece,
                               centre - axis * size[k],
                               centre + axis * size[k]])
            faces.extend(mf._grid_faces(rings, segments, offset + len(piece) - 2,
                                        offset + len(piece) - 1, offset))
            rgba = np.empty((len(piece), 4))
            rgba[:, :3] = np.asarray(cfg["colour"]) * (
                0.75 + 0.5 * mf._hash01(k, seed + 7))
            rgba[:, 3] = alpha[k]
            verts.append(piece)
            colour.append(rgba)
            offset += len(piece)

    if not verts:
        return np.zeros((0, 3)), [], np.zeros((0, 4))
    return np.vstack(verts), faces, np.vstack(colour)


# ---------------------------------------------------------------------------
# entry points
# ---------------------------------------------------------------------------

def ports_of(cfg):
    if cfg.get("ports") is not None:
        ports, scale = cfg["ports"]
        return [(np.asarray(p, dtype=np.float64), np.asarray(d, dtype=np.float64),
                 float(r)) for p, d, r in ports], float(scale)
    hull = bpy.context.scene.objects[cfg["hull"]]
    return _sibling("exhaust_point").read_ports(hull)


def _place(cfg):
    scene = bpy.context.scene
    ob = scene.objects.get(cfg["name"])
    if ob is None:
        ob = bpy.data.objects.new(cfg["name"], bpy.data.meshes.new(cfg["name"]))
        scene.collection.objects.link(ob)
    if ob.parent is not None:
        # see the module docstring: under the hull it would be drawn into the
        # hull atlas, on every frame, for good
        ob.parent = None
    # identity, with the geometry in world space: one object, however many
    # ports. Assigned rather than reset in place - `matrix_world.identity()`
    # edits a copy and leaves the object where it was.
    ob.matrix_world = Matrix.Identity(4)
    ob.data.materials.clear()
    ob.data.materials.append(
        _mf().build_material(cfg, cfg.get("material") or MATERIAL))
    return ob


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    ob = _place(cfg)
    set_phase(0, 1, cfg)
    return ob


def set_phase(index, count, cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    ports, hull_length = ports_of(cfg)
    ob = bpy.context.scene.objects[cfg["name"]]
    _mf()._write_mesh(ob.data, *_plume_arrays(cfg, ports, hull_length,
                                              index, count))
    return {"phase": index, "of": count, "ports": len(ports),
            "ages": [round(float(a), 4) for a in _ages(cfg, index, count)]}


def phase_hook(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))

    def hook(index, count):
        set_phase(index, count, cfg)

    return hook


def reach_px(cfg, units_per_pixel):
    """How far the plume gets from the port, in pixels of the atlas.

    The one number worth reading before a render: a plume is haze standing over
    the deck at twenty or thirty pixels and a factory chimney at eighty, and
    which of those a config produces is not obvious from fractions of a hull.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    _, hull_length = ports_of(cfg)
    return hull_length * cfg["rise"] * float(cfg["scale"]) / units_per_pixel


def remove(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    ob = scene.objects.get(cfg["name"])
    if ob is not None:
        data = ob.data
        bpy.data.objects.remove(ob)
        bpy.data.meshes.remove(data)
    mat = bpy.data.materials.get(cfg.get("material") or MATERIAL)
    if mat is not None:
        bpy.data.materials.remove(mat)


if __name__ == "__main__":
    build(CONFIG)
