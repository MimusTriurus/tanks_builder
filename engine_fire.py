"""
The tank burning: a red flame on the engine deck, under a column of black smoke.

Two objects and two layers, `Fire` and `Burn`, for the reason the muzzle flash
is two: fire adds light to what is behind it and smoke takes it away, and one
layer cannot do both. `build()` and `build_smoke()` make them; hand
`phase_hook()` and `smoke_phase_hook()` to `sprite_atlas` layers with
`"fit": False` and `"phases": N`. `tank_pipeline` does all of that.

The flame is built here; the column is the plume's geometry driven by the
`SMOKE` config below. See that config for why the smoke is not optional.

It reads the same ports the exhaust does - `exhaust_point` stamps them once on
the hull and both effects measure nothing of their own. That is deliberate: the
outlet is where the engine is, and the engine is what catches fire. A second
measurement of the same hole is a second thing to keep in step.

What it takes from the plume, and what it refuses
-------------------------------------------------
The loop is the plume's, unchanged, and for the plume's reason: a burning tank
burns for as long as it is on screen, so there is no first phase and no last
one. Each element carries an *age* that advances by `1/phases` and wraps, and
everything about it - where, how big, what colour, how solid - is a function of
that age alone. Phase N is phase 0 by construction. See `exhaust_plume`.

Everything else is the other way round, and each difference is one that shows:

- **It emits.** Additive, like the muzzle flash and unlike the plume. Smoke
  takes light away from what is behind it; fire adds it. One layer cannot do
  both, which is why the muzzle flash is two.

- **The elements bunch at the bottom, not at the top.** The plume's puffs slow
  as they rise, so they crowd at the tip and the plume reads as hanging. Flame
  is buoyant - it *accelerates* upward - so the same integral with the sign
  reversed crowds the elements at the seat and stretches them apart as they go.
  That is the dense base and the thin licks, and it comes out of the path rather
  than out of a size table.

- **They are stretched, not round.** A lick of flame is long along the flow.
  The plume's puffs are spheres because smoke is; scaling by `stretch` along the
  local direction of travel is the whole of the difference and it is most of
  what stops this reading as a stack of orange balls.

- **It tapers.** Wide at the deck, narrow at the tips. The plume grows as it
  rises because smoke entrains air; a flame's visible part is the reacting zone
  and it closes up.

The size is the hull's, not the port's
--------------------------------------
The plume is quoted mostly against the port because that is where the smoke
leaves from. This is quoted mostly against the hull, and the reason is not
symmetry - it is that a burning tank is not a bigger exhaust. The vent decides
where the fire sits; it does not decide how much of it there is. A model whose
outlet is a 0.02 pipe would otherwise get a flame the width of a pencil, and it
is the same tank on fire as the one with a 0.12 grille. So `seat_from_hull`
carries most of the base width and `seat_from_port` only tilts it, where the
plume has those weights the other way round.

Red, and how it stays red
-------------------------
The ramp has no near-white stop in it at all. That is the muzzle flash's
lesson turned into a rule: a hand-picked fire ramp drifts toward white over
exactly the stretch that should carry the most colour, and then additive
compositing takes the rest - any overlap of bright pixels goes white on its
own. So the hottest stop here is a yellow, not a white, and the white core (if
one appears) is made *by* the overlap out of coloured parts. `check` measures
the result as redness against the ground rather than as brightness, because
brightness is not the thing that fails.

Do not parent this to anything
------------------------------
Same trap as `Flash` and `Plume`. The hull layer is the whole tank minus the
turret, so a `Fire` parented under the hull would be drawn into the hull atlas
permanently - and a tank that is always on fire is a memorable bug. It sits at
scene root with its vertices in world space, one object for however many ports.
"""

import importlib.util
import math
import os

import bpy
import numpy as np
from mathutils import Matrix

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "hull": "Hull",
    "name": "Fire",
    # [(point, dir, radius)], hull_length - instead of reading the hull's
    # stamp. For a bench with no tank in it. In a tank scene leave this alone.
    "ports": None,

    # --- the two master knobs -----------------------------------------------
    # `scale` is how much fire there is, `brightness` how hard it burns.
    # Everything below is shape.
    #
    # brightness much past 1.2 stops buying fire and starts buying white, for
    # the reason in the docstring: alpha clips at 1 and the game adds. If more
    # punch is wanted without losing the colour, the lever is the blend mode -
    # screen rather than add - not this number.
    "scale": 1.0,
    "brightness": 1.0,

    # --- the column, as fractions of the hull's length -----------------------
    # How far the tips get from the port. 0.38 of a hull is 50 px of screen on
    # MT against a hull 113 px tall, i.e. a flame about half the height of the
    # tank. That is what "burning" looks like; 0.2 is a fire in a bin.
    #
    # Taller is not stronger, and on a pale field it is weaker. The tips are the
    # faintest part, the game adds this layer, and a faint addition to a light
    # background is nearly nothing - so height spent past what the colour can
    # carry comes back as grey wisps that read as steam. 0.45 did exactly that.
    "rise": 0.38,
    # sideways sway by full age, in world horizontal
    "spread": 0.075,
    # How far the path has turned toward world up by full age. Higher than the
    # plume's 0.80 and reached sooner (`turn_power` under 1): smoke drifts out
    # along the port and then rises, flame is buoyant from the first inch.
    "buoyancy": 0.95,
    "turn_power": 0.55,
    # Speed along the path as exp(gather * t). The plume has this with a
    # negative sign and calls it `slowing`; positive, it crowds the elements at
    # the seat and pulls them apart toward the tips, which is the shape of a
    # flame. Zero would give an evenly spaced ladder, which reads as a machine.
    #
    # It also decides how much of `rise` is ever lit, which is not obvious and
    # cost a render. The distance reached by age a is the integral, so at 1.35
    # an element is only a third of the way up at half its life - and its life
    # is over long before the top. The flame measured 26 px of a 69 px column
    # and read as "too short" when nothing was short.
    "gather": 0.70,

    # --- how wide it burns ---------------------------------------------------
    # Element radius at the seat: mostly the hull's, only tilted by the port's.
    # See the docstring - the weights are the other way round from the plume's
    # on purpose, and swapping them is the failure that makes a pipe-vented
    # tank burn like a cigarette.
    #
    # Watch these in pixels, not in fractions. The first pass had 0.052 and
    # 0.35, which is an 8.8 px radius before `seat_size` and the per-blob
    # spread take it to 20 - six of those overlapping on a port 25 px across,
    # and the seat came out a ball wider than it was tall with the whole flame
    # inside it. The column should be about three times as tall as it is wide.
    "seat_from_hull": 0.040,
    "seat_from_port": 0.30,
    # What is left of that radius at full age, and how fast it gets there.
    #
    # An element must end by *shrinking*, not by fading, and that is the one
    # trade in this file that is forced by the blend mode rather than chosen.
    # The game adds this layer, so a low-alpha pixel over the pale hex field can
    # only ever be that field very slightly tinted - grey, whatever colour it
    # was meant to be. Fading the tips out therefore does not remove them, it
    # turns them into steam. Shrinking removes them while every pixel that is
    # left is still saturated.
    "taper": 0.26,
    "taper_power": 0.90,
    # Length along the flow as a multiple of the width. The single biggest
    # thing separating this from a stack of orange balls.
    "stretch": 2.3,

    # --- the queue -----------------------------------------------------------
    "tongues": 30,
    # Elements evenly spaced in age tick like a metronome. The nudge is hashed
    # on the element, never on the phase: hash in the phase and every lick
    # becomes a different lick every frame, the fire boils in place instead of
    # rising, and no amount of shape tuning fixes it.
    "stagger": 0.42,
    "vary": 0.40,             # spread of per-element size
    # Lumpier than the plume's 0.16. That one lies over the armour for the whole
    # game and lumps on it read as dirt; this one is meant to be ragged.
    "wobble": 0.28,
    # How much hotter the trailing end of one lick is than its leading end, in
    # ramp positions. Cheap, and it is what makes an element read as a flame
    # rather than as a coloured blob.
    "gradient": 0.20,
    "rings": 7,
    "segments": 10,
    "seed": 61,

    # --- the seat ------------------------------------------------------------
    # A squat bright body sitting on the port in every phase. Without it the
    # column has nothing at its base - the elements fade in from zero at age 0 -
    # and a flame with no hot seat reads as fog. Same lesson as the muzzle
    # flash's core.
    "seat_blobs": 5,
    "seat_size": 1.15,
    "seat_stretch": 1.15,
    # how far across the port's face the blobs scatter, and how far the licks
    # are born off its centre - both as fractions of the port's own radius
    "seat_scatter": 0.60,
    "born_scatter": 0.70,
    # It flickers rather than moving. The wave is a whole number of cycles per
    # lap, hashed per blob, so it closes at the wrap for the same reason the
    # ages do - by construction, not by tuning.
    "seat_flicker": 0.45,
    "seat_gain": 0.95,

    # --- embers --------------------------------------------------------------
    # Small, bright, and further out than the flame. Named in VFX practice as
    # one of the things separating a mediocre fire from a good one, and they
    # cost nothing here: they are the same queue with a different reach.
    #
    # `reach` is a multiple of `rise`, so raising the flame raises them too.
    # Watch it against the tile: they are the tallest thing in the layer and
    # therefore the first thing to be clipped by the frame.
    "embers": 8,
    "ember_reach": 1.30,
    "ember_size": 0.26,       # of the seat radius
    "ember_spread": 0.20,     # of the hull, sideways by full age
    "ember_gain": 0.85,
    "ember_fade": 1.10,       # few and solid beats many and faint - see `taper`
    # Warm, but nowhere near the yellow a real spark is. Same reason as the
    # ramp and it bites harder here: an ember is a few pixels at low alpha, and
    # a low-alpha yellow added to a pale field is a grey speck. They read as
    # drizzle before this was pulled toward red.
    "ember_colour": (1.00, 0.38, 0.08),
    "ember_rings": 5,
    "ember_segments": 6,

    # --- life ----------------------------------------------------------------
    # Peak alpha of one element and the two ends of its life. Both ends must
    # reach exactly zero or the loop shows a seam: `onset` brings an element up
    # from nothing at age 0 and `fade` takes it back to nothing at age 1, where
    # the next lap starts. `onset` is much shorter than the plume's - a flame
    # is already burning when it leaves the deck.
    #
    # 0.82 was a third too much and it did not come out as "too bright", it
    # came out as white: several translucent surfaces lie along every ray
    # through the column, the game adds them, and the sum clipped all three
    # channels wherever the flame was solid.
    "alpha": 0.70,
    "onset": 0.07,
    # Much gentler than the plume's, and deliberately doing less work than a
    # smoke fade does: here it only has to reach zero at the wrap, while the
    # shape of the ending is `taper`'s job. See there.
    "fade": 0.55,

    # --- colour --------------------------------------------------------------
    # Stops of (position, colour, strength), position being the element's age.
    # A bare number in place of the triple is read as a colour temperature and
    # run through the blackbody curve.
    #
    # No stop is near white, on purpose - see the docstring. Most of the column
    # lives between 0.15 and 0.6, which is why the red sits there rather than
    # at the end where it would only ever be seen dying.
    #
    # Green and blue are held very low all the way along, and that is what
    # actually keeps the flame red under addition. Red saturating on its own is
    # not the failure - saturated red is red. The failure is green and blue
    # coming up with it, which is what a yellow stop anywhere near the mass
    # guarantees: the first pass opened at (1.00, 0.86, 0.48) and the whole
    # flame came out the colour of a lightbulb.
    "ramp": [
        (0.00, (1.00, 0.50, 0.13), 1.00),
        (0.08, (1.00, 0.26, 0.05), 0.95),
        (0.22, (1.00, 0.15, 0.03), 0.92),
        (0.60, (0.85, 0.09, 0.02), 0.66),
        (1.00, (0.55, 0.04, 0.01), 0.26),
    ],
    # A dark lip so the licks keep a shape instead of melting into each other.
    # The plume wants the opposite and has its rim nearly the body's colour:
    # there, shapes are the problem.
    "rim_colour": (0.35, 0.03, 0.01),
    "rim_reach": 0.60,
    # Lower than the plume's 1.25, and the same warning applies in reverse:
    # alpha goes as facing**this, so raising it to soften the elements quietly
    # takes a third of the fire with it.
    "rim_falloff": 1.15,
    "opacity": 0.72,
}

MATERIAL = "_engine_fire"


# ---------------------------------------------------------------------------
# the smoke column
# ---------------------------------------------------------------------------
#
# The other half of the effect, and it lives here rather than in
# `exhaust_plume` for the reason `muzzle_flash` keeps `SMOKE` next to `CONFIG`:
# the module that owns the effect owns both halves of it. The geometry is the
# plume's - a queue of puffs on an integrated path, ages that wrap - so this is
# a set of numbers and four entry points, not another implementation.
#
# Why it exists at all, beyond looking like a burning tank
# --------------------------------------------------------
# It fixes the flame. The fire is added, and an added pixel at low alpha over
# the game's pale field is that field very slightly tinted - which is why the
# flame had to be made to end by shrinking rather than fading (see `taper`).
# Black smoke *behind* the flame puts a dark background under it, and on a dark
# background addition does what it is supposed to. This is the same thing that
# makes the muzzle flash convincing: its own smoke goes down first.
#
# So the draw order is not decoration. Smoke with normal alpha, then fire added
# on top of it. Reversed, the smoke would grey out the flame it is meant to
# back, which is worse than having no smoke.
SMOKE = {
    "hull": "Hull",
    "name": "Burn",
    "material": "_burning_smoke",
    "ports": None,

    "scale": 1.0,
    "density": 1.0,

    # --- the column, as fractions of the hull's length -----------------------
    # Set so the column clears the flame by enough to be seen: 0.58 tops out
    # 131px above the anchor against the flame's 109. Solved by projecting the
    # mesh through the render's own camera basis at all twelve angles, not
    # guessed.
    #
    # The height of this column is decided by the flame, not by the tank, and
    # that is the thing to know before reaching for the number. Three targets
    # sound alike and are far apart: the tank's tallest pixel is 76.9px above
    # the anchor, the plain tile's edge is 118.9 - the tile is fitted to the
    # tank's *rotating* bounds with padding, not to its silhouette - and the
    # flame reaches 108.9. Anything at or under the flame is invisible, because
    # the flame is also twice as wide; it was tried at 77 and there was no smoke
    # on screen at all, only a darker fire. So the floor is the flame's top plus
    # something, and that put this past what a 256 tile can hold.
    #
    # It was 1.50 once, and changing it is not a one-number edit either way.
    # Everything about the puffs is sized against the path: thirty of them
    # growing 0.16 of a hull suited 1.50 and made a flat smear on the deck at
    # 0.33, because the path has to be several puffs long before there is a
    # column rather than a blob - see `exhaust_plume` under `puff_start`. The
    # puff geometry is scaled with the path, never left behind by it.
    "rise": 0.58,
    "spread": 0.133,
    "buoyancy": 0.95,
    # Most of the turn happens at once, unlike the exhaust's even 1.0. The vent
    # points back and up at 52 degrees on MT, and over a path this long the
    # plume's even turn sent the column half a hull along that aim before it
    # started climbing: it read as a jet rather than as a fire, and it pushed
    # the layer's frame out by 18px sideways for nothing. A burning tank's
    # column is driven by heat, not by what the vent is pointed at.
    "turn_power": 0.45,
    # Much larger than the exhaust's 0.45, and it is not a style choice. The
    # exhaust's jet gives its momentum up immediately - idle exhaust has none to
    # start with - so its puffs are almost at the tip by half their life. Over a
    # column four times as long that would leave every puff bunched at the top
    # and a bare thread underneath. A fire is driven by buoyancy the whole way,
    # so the speed decays slowly and the puffs space out along it.
    "slowing": 1.10,

    # --- the puffs -----------------------------------------------------------
    "puff_start": 0.55,       # of the port's radius
    "puff_grow": 0.060,       # of the hull's length
    "puff_power": 0.65,
    # Twenty-two, near the exhaust's twenty, because the path is now the
    # exhaust's length. It was thirty when the path was six times longer, by the
    # exhaust's own rule: puffs have to overlap *along the path* before a queue
    # reads as one body of smoke, and the count follows the length.
    "puffs": 22,
    "stagger": 0.35,
    "puff_vary": 0.45,
    "wobble": 0.22,
    "rings": 7,
    "segments": 10,
    "seed": 91,

    # --- life ----------------------------------------------------------------
    # Aim at the colour of the *stack*, not of one puff: twenty-two puffs of 7
    # to 16px along a 103px path overlap heavily, so this is well under what a
    # single puff should look like.
    "alpha": 0.50,
    "onset": 0.10,
    "fade": 1.25,

    # Near black, where the exhaust is a warm grey-brown. Idle exhaust is thin
    # and lit; this is unburnt fuel, and it is also doing a job for the flame -
    # a mid-grey column would back the fire with something the fire cannot beat.
    "colour": (0.10, 0.085, 0.075),
    "rim_colour": (0.06, 0.050, 0.045),
    "rim_reach": 0.85,
    "rim_falloff": 1.15,
    "opacity": 0.80,
}

SMOKE_MATERIAL = SMOKE["material"]

UP = np.array([0.0, 0.0, 1.0])
# world horizontal, for the sway. The column is vertical within an inch of the
# port, so swaying it in the port's own frame would tilt the sway with the vent.
SWAY_U = np.array([1.0, 0.0, 0.0])
SWAY_V = np.array([0.0, 1.0, 0.0])


def _sibling(name):
    """Load a module that lives next to this one - see exhaust_point._sibling."""
    here = (os.path.dirname(os.path.abspath(__file__))
            if "__file__" in globals() else REPO)
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(here, "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_MF = None
_PLUME = None


def _mf():
    global _MF
    if _MF is None:
        _MF = _sibling("muzzle_flash")
    return _MF


def _plume():
    global _PLUME
    if _PLUME is None:
        _PLUME = _sibling("exhaust_plume")
    return _PLUME


# ---------------------------------------------------------------------------
# the loop
# ---------------------------------------------------------------------------

def _ages(count, stagger, seed, index, phases):
    """Each element's age this phase, in [0, 1).

    `exhaust_plume._ages`, generalised over the count so the licks and the
    embers can be two queues of different lengths sharing one clock. The line
    is the loop: age advances by `1/phases` for every element at once, so phase
    `phases` reproduces phase 0 and there is no seam to blend.
    """
    k = np.arange(count)
    slot = (k + 0.5) / max(count, 1)
    nudge = stagger * (2.0 * _mf()._hash01(k, seed) - 1.0) / max(count, 1)
    return np.mod(slot + nudge + float(index) / max(int(phases), 1), 1.0)


def _life(cfg, ages, fade):
    """Up from nothing at age 0, back to nothing at age 1."""
    return (1.0 - np.exp(-ages / max(cfg["onset"], 1e-4))) \
        * np.power(np.maximum(1.0 - ages, 0.0), fade)


def _flicker(cfg, index, phases, count, seed):
    """A per-blob wave with a whole number of cycles per lap.

    Whole cycles, because anything else would not land back where it started
    and the seat would jump once a lap - which is the one part of the effect
    that never moves and so is the one part where a jump is obvious.
    """
    k = np.arange(count)
    cycles = 1 + (k % 3)
    turn = _mf()._hash01(k, seed)
    wave = 0.5 + 0.5 * np.cos(2.0 * math.pi
                              * (cycles * float(index) / max(int(phases), 1)
                                 + turn))
    return 1.0 - cfg["seat_flicker"] + cfg["seat_flicker"] * wave


# ---------------------------------------------------------------------------
# the path
# ---------------------------------------------------------------------------

def _path(cfg, axis, reach, samples=96):
    """Where an element is at every age, and which way it is going there.

    Returns (t, positions, directions). Integrated rather than a straight line
    for the plume's reason - swinging a line would move the whole column,
    including the part still on the deck - and with the speed *rising* rather
    than decaying, which is the difference between fire and smoke. See CONFIG's
    `gather`.
    """
    t = np.linspace(0.0, 1.0, samples)
    turn = np.clip(cfg["buoyancy"] * np.power(t, cfg["turn_power"]),
                   0.0, 1.0)[:, None]
    direction = (1.0 - turn) * axis[None, :] + turn * UP[None, :]
    direction /= np.maximum(np.linalg.norm(direction, axis=1, keepdims=True), 1e-9)
    speed = np.exp(cfg["gather"] * t)

    pos = np.cumsum(direction * speed[:, None], axis=0)
    pos -= pos[0]
    # normalise on the end *point*, so `rise` is how far the tip gets from the
    # port rather than how far it travelled getting there
    pos *= reach / max(float(np.linalg.norm(pos[-1])), 1e-9)
    return t, pos, direction


def _sample_path(t, pos, direction, ages):
    """Positions and unit directions at `ages`."""
    along = np.stack([np.interp(ages, t, pos[:, c]) for c in range(3)], axis=1)
    heading = np.stack([np.interp(ages, t, direction[:, c]) for c in range(3)],
                       axis=1)
    heading /= np.maximum(np.linalg.norm(heading, axis=1, keepdims=True), 1e-9)
    return along, heading


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

def _sphere(rings, segments):
    phi = np.linspace(0.08, math.pi - 0.08, rings)
    theta = np.linspace(0.0, 2.0 * math.pi, segments, endpoint=False)
    return np.stack([
        (np.sin(phi)[:, None] * np.cos(theta)[None, :]).reshape(-1),
        (np.sin(phi)[:, None] * np.sin(theta)[None, :]).reshape(-1),
        np.repeat(np.cos(phi), segments)], axis=1)


def _blob(base, centre, heading, radius, stretch, wobble, seed):
    """One element: a lumpy sphere stretched along the way it is going.

    The two poles are appended leading end first, matching the sphere's first
    ring, so `_grid_faces` caps it the way it was wound.
    """
    mf = _mf()
    u, v = mf._perpendicular(heading)
    lump = 1.0 + wobble * (2.0 * mf._hash01(np.arange(len(base)), seed) - 1.0)
    r = radius * lump
    piece = (centre[None, :]
             + (base[:, 0] * r)[:, None] * u[None, :]
             + (base[:, 1] * r)[:, None] * v[None, :]
             + (base[:, 2] * r * stretch)[:, None] * heading[None, :])
    return np.vstack([piece,
                      centre + heading * radius * stretch,
                      centre - heading * radius * stretch])


def _tint(cfg, base, age, gradient, alpha):
    """Per-vertex rgba for one element: hue from the ramp, strength in alpha.

    The ramp is read per *vertex*, offset along the element's own axis, so the
    trailing end is hotter than the leading one. Reading it once per element
    would be a flat colour per blob, which is what a stack of coloured balls
    looks like.
    """
    # base[:, 2] runs +1 at the leading pole to -1 at the trailing one
    t = np.clip(age + gradient * (base[:, 2] + 1.0) * 0.5, 0.0, 1.0)
    t = np.concatenate([t, [min(age + gradient, 1.0)], [age]])
    rgba = _mf()._ramp(cfg["ramp"], t)
    rgba[:, 3] = np.clip(rgba[:, 3] * alpha, 0.0, 1.0)
    return rgba


def _fire_arrays(cfg, ports, hull_length, index, phases):
    """Every port's seat, licks and embers at phase `index`, in world space."""
    mf = _mf()
    scale = float(cfg["scale"])
    bright = float(cfg["brightness"])
    body = _sphere(int(cfg["rings"]), int(cfg["segments"]))
    spark = _sphere(int(cfg["ember_rings"]), int(cfg["ember_segments"]))

    tongues = int(cfg["tongues"])
    embers = int(cfg["embers"])
    lick_age = _ages(tongues, cfg["stagger"], cfg["seed"] + 1, index, phases)
    ember_age = _ages(embers, cfg["stagger"], cfg["seed"] + 2, index, phases)
    lick_life = _life(cfg, lick_age, cfg["fade"])
    ember_life = _life(cfg, ember_age, cfg["ember_fade"])

    verts, faces, colour, offset = [], [], [], 0

    def add(piece, rgba, rings, segments):
        nonlocal offset
        faces.extend(mf._grid_faces(rings, segments,
                                    offset + len(piece) - 2,
                                    offset + len(piece) - 1, offset))
        verts.append(piece)
        colour.append(rgba)
        offset += len(piece)

    for p, (point, axis, radius) in enumerate(ports):
        point = np.asarray(point, dtype=np.float64)
        axis = np.asarray(axis, dtype=np.float64)
        axis = axis / max(float(np.linalg.norm(axis)), 1e-9)
        seed = int(cfg["seed"]) + p * 977
        reach = hull_length * cfg["rise"] * scale
        seat_r = (hull_length * cfg["seat_from_hull"]
                  + radius * cfg["seat_from_port"]) * scale

        t, pos, heading = _path(cfg, axis, reach)

        # --- the seat: on the port's face, flickering in place ---------------
        u, v = mf._perpendicular(axis)
        gain = _flicker(cfg, index, phases, int(cfg["seat_blobs"]), seed + 3)
        for k in range(int(cfg["seat_blobs"])):
            off_u = radius * cfg["seat_scatter"] * (
                2.0 * mf._hash01(k, seed + 4) - 1.0)
            off_v = radius * cfg["seat_scatter"] * (
                2.0 * mf._hash01(k, seed + 5) - 1.0)
            lift = seat_r * 0.35 * mf._hash01(k, seed + 6)
            size = seat_r * cfg["seat_size"] * (
                0.7 + 0.6 * mf._hash01(k, seed + 7))
            # proud of the grille along the port's own axis, or half of every
            # blob is inside the deck and only shows on the headings where the
            # holdout happens not to cut it
            centre = point + axis * seat_r * 0.45 \
                + off_u * u + off_v * v + lift * UP
            piece = _blob(body, centre, UP, size, cfg["seat_stretch"],
                          cfg["wobble"], seed + 8 + k)
            add(piece,
                _tint(cfg, body, 0.0, cfg["gradient"],
                      cfg["seat_gain"] * gain[k] * cfg["alpha"] * bright),
                int(cfg["rings"]), int(cfg["segments"]))

        # --- the licks -------------------------------------------------------
        along, going = _sample_path(t, pos, heading, lick_age)
        turn = 2.0 * math.pi * mf._hash01(np.arange(tongues), seed + 11)
        sway = hull_length * cfg["spread"] * scale \
            * np.power(lick_age, 0.8) * mf._hash01(np.arange(tongues), seed + 12)
        # off-centre at birth across the port's own width, or every lick leaves
        # through the exact middle and the base of the column is a needle -
        # wrong for a pipe and very wrong for a grille, where the whole panel
        # is alight
        born = radius * cfg["born_scatter"]
        born_u = born * (2.0 * mf._hash01(np.arange(tongues), seed + 13) - 1.0)
        born_v = born * (2.0 * mf._hash01(np.arange(tongues), seed + 14) - 1.0)
        size = seat_r * (1.0 - (1.0 - cfg["taper"])
                         * np.power(lick_age, cfg["taper_power"])) \
            * (1.0 - cfg["vary"]
               + 2.0 * cfg["vary"] * mf._hash01(np.arange(tongues), seed + 15))

        for k in range(tongues):
            centre = (point + along[k]
                      + born_u[k] * u + born_v[k] * v
                      + sway[k] * math.cos(turn[k]) * SWAY_U
                      + sway[k] * math.sin(turn[k]) * SWAY_V)
            piece = _blob(body, centre, going[k], size[k], cfg["stretch"],
                          cfg["wobble"], seed + 20 + k)
            add(piece,
                _tint(cfg, body, float(lick_age[k]), cfg["gradient"],
                      cfg["alpha"] * lick_life[k] * bright),
                int(cfg["rings"]), int(cfg["segments"]))

        # --- the embers ------------------------------------------------------
        if embers:
            t2, pos2, heading2 = _path(cfg, axis, reach * cfg["ember_reach"])
            along2, going2 = _sample_path(t2, pos2, heading2, ember_age)
            turn2 = 2.0 * math.pi * mf._hash01(np.arange(embers), seed + 31)
            sway2 = hull_length * cfg["ember_spread"] * scale \
                * np.power(ember_age, 0.9) \
                * mf._hash01(np.arange(embers), seed + 32)
            size2 = seat_r * cfg["ember_size"] * (
                0.5 + 1.0 * mf._hash01(np.arange(embers), seed + 33))
            rgb = np.asarray(cfg["ember_colour"], dtype=np.float64)
            for k in range(embers):
                centre = (point + along2[k]
                          + sway2[k] * math.cos(turn2[k]) * SWAY_U
                          + sway2[k] * math.sin(turn2[k]) * SWAY_V)
                piece = _blob(spark, centre, going2[k], size2[k], 1.6,
                              cfg["wobble"], seed + 40 + k)
                rgba = np.empty((len(piece), 4))
                rgba[:, :3] = rgb
                rgba[:, 3] = min(cfg["ember_gain"] * ember_life[k] * bright, 1.0)
                add(piece, rgba, int(cfg["ember_rings"]),
                    int(cfg["ember_segments"]))

    if not verts:
        return np.zeros((0, 3)), [], np.zeros((0, 4))
    return np.vstack(verts), faces, np.vstack(colour)


# ---------------------------------------------------------------------------
# entry points
# ---------------------------------------------------------------------------

def ports_of(cfg):
    """The same ports the plume uses - one measurement, two effects."""
    if cfg.get("ports") is not None:
        ports, scale = cfg["ports"]
        return [(np.asarray(p, dtype=np.float64), np.asarray(d, dtype=np.float64),
                 float(r)) for p, d, r in ports], float(scale)
    hull = _sibling("tank_parts").mesh(cfg["hull"])
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
    ob.data.materials.append(_mf().build_material(cfg, MATERIAL))
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
    _mf()._write_mesh(ob.data, *_fire_arrays(cfg, ports, hull_length,
                                             index, count))
    return {"phase": index, "of": count, "ports": len(ports),
            "ages": [round(float(a), 4) for a in
                     _ages(int(cfg["tongues"]), cfg["stagger"],
                           cfg["seed"] + 1, index, count)]}


def phase_hook(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))

    def hook(index, count):
        set_phase(index, count, cfg)

    return hook


def reach_px(cfg, units_per_pixel):
    """How far the flame and its embers get from the port, in atlas pixels.

    Worth reading before a render. Fractions of a hull do not say whether this
    is a burning tank or a bonfire, and the embers - not the flame - are what
    decides whether the layer fits its frame.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    _, hull_length = ports_of(cfg)
    reach = hull_length * cfg["rise"] * float(cfg["scale"]) / units_per_pixel
    return {"flame_px": round(reach, 1),
            "embers_px": round(reach * cfg["ember_reach"], 1)}


# --- the smoke column ------------------------------------------------------
#
# Four one-line delegations to `exhaust_plume`, mirroring `muzzle_flash`'s
# `build_smoke` / `set_smoke_phase` / `smoke_phase_hook`. The whole config is
# handed over rather than a few overrides, so SMOKE above reads as the complete
# description of this column instead of a diff against an idling engine.

def build_smoke(cfg=None):
    return _plume().build(dict(SMOKE, **(cfg or {})))


def set_smoke_phase(index, count, cfg=None):
    return _plume().set_phase(index, count, dict(SMOKE, **(cfg or {})))


def smoke_phase_hook(cfg=None):
    return _plume().phase_hook(dict(SMOKE, **(cfg or {})))


def smoke_reach_px(cfg, units_per_pixel):
    cfg = dict(SMOKE, **(cfg or {}))
    _, hull_length = ports_of(cfg)
    return round(hull_length * cfg["rise"] * float(cfg["scale"])
                 / units_per_pixel, 1)


def remove(cfg=None):
    """Take the flame and its smoke back out of the scene."""
    cfg = cfg or {}
    scene = bpy.context.scene
    for name in (cfg.get("name", CONFIG["name"]), SMOKE["name"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)
    for name in (MATERIAL, SMOKE_MATERIAL):
        mat = bpy.data.materials.get(name)
        if mat is not None:
            bpy.data.materials.remove(mat)


if __name__ == "__main__":
    build(CONFIG)
    build_smoke(SMOKE)
