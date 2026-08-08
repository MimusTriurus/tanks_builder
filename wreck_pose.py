"""The wreck: a knocked-out tank, posed rather than repainted.

Three deformations and no new geometry: the turret has dropped into its own
ring and sits canted, the gun has fallen to its lowest elevation, and the belts
have gone slack off the front of the loop. The paint is *not* burnt here - the
harness does that in a shader, which is cheaper and reversible, and which is
where this project has already put it.

Why the turret cannot be baked into one wreck sprite
----------------------------------------------------
The obvious economy is a single `wreck` layer: nothing on a dead tank moves
relative to anything else, so the whole point of the layered atlas has gone. It
is wrong, and the reason is the moment of death.

A tank dies with its turret at whatever bearing it had - one of the 24 the atlas
holds, and no way to know which in advance. A single baked layer bakes one
hull-to-turret angle, so a tank killed with the gun over its left sponson would
snap the turret to the baked angle the instant it died. That is the same failure
as rendering the wreck at 12 headings, and it is the more visible half of it:
the gun is what the eye was following.

So the wreck keeps the split the live tank has - hull by hull bearing, turret by
turret bearing - and only the layers whose *geometry* changes are re-rendered.

What that leaves is smaller than it looks
-----------------------------------------
The hull does not change at all. Not "changes little": the wreck's hull geometry
is the live hull's geometry, so the live `hull_atlas` is already the wreck's, to
the pixel. The same goes for the contact shadow (zenith light, and the turret's
footprint is inside the hull's) and for the scars, which sit on hull plates that
have not moved.

So the wreck costs one turret layer and two belt layers, 24 frames each, against
the live set's 46MB - see CLAUDE.md for the measured figures.

The void under the turret, which is a gift from the assets
----------------------------------------------------------
Probed with a ray fan over a 0.30 x 0.30 grid around the ring axis on
MT_PARTS_1: every ray goes straight down to the belly plate and crosses exactly
one surface. There is **no deck under the turret at all** - the generator builds
nothing it cannot see, and a seated turret hides the whole opening.

Lift the turret and that opening shows, dark, and it reads as a burnt-out
fighting compartment. It is the strongest single mark of a wreck available here
and it costs nothing to render.

It needs a real angle, though, and that is measured too. The skirt hangs 0.0316
below the ring against a ring radius of 0.2706, so the rim clears at 6.7 deg -
but at 12 deg the lift is 0.056, some 4px above the skirt, and at 30 deg of
camera elevation the near skirt covers it. Nothing shows. At 24 deg the lift is
0.110, about 17px, and the void reads.

The cant rides the turret, not the hull
---------------------------------------
Physically a turret slides down into the ring on one side, so the tip direction
belongs to the *hull*. Expressing that needs both the turret's bearing and the
tip direction as free axes - 24 x 24 frames - because the layer is indexed by
turret bearing alone.

So the cant is defined against the turret instead: it tipped towards its own
left, and the tip goes round with the gun. One turret layer of 24 frames, indexed
exactly as the live one. The cost is that a wreck whose turret happens to point
away from the camera shows less of its void - measured across six headings, two
of six read weakly. A little nose-up pitch is mixed in for that reason: it opens
the rim on the azimuths the roll alone leaves closed.

Why the belts sag at the front and not on top
---------------------------------------------
The first attempt dropped the top run, which is what a thrown track looks like
in a photograph and is invisible here. Measured on MT_PARTS_1: the hull sits
0.03 above the top run and overhangs it by 0.017 outboard, so from a 30 deg
camera the sponson covers it along its whole length. A 12px sag moved the belt
from one hidden place to another.

What the camera does see is the bottom run and the curves round the idler and the
sprocket. So the belt is pulled outboard and down over the front of the loop,
where nothing covers it.

Nothing is cut. A belt lying detached on the ground would leave the tile, and
`max_edge_alpha == 0` is the rule every layer in this project is held to.

Sizes in fractions, never pixels
--------------------------------
`units_per_pixel` is a *result* of fitting the camera to the carousel, so a
figure in pixels here would be a guess at a number the job has not decided yet -
the trap the plate table sits between the render and the check to avoid. The
cant is in degrees, the droop in degrees, and the slack in fractions of the
hull's own length, so all three carry to another tank without a table.
"""

import importlib
import math

import numpy as np


CONFIG = {
    # the layer root the turret and gun hang off. Not posed: the renderer writes
    # and restores a layer root's matrix per frame, so the pose goes on the
    # children - the same reason `barrel_recoil` moves the tube and not the root.
    "root": "Turret.World",
    "turret": "Turret",
    "barrel": "Barrel",
    "hull": "Hull",

    # The turret drops into its ring. None derives the angle - see
    # `_cant_deg` - and a number forces it, for an A/B.
    "cant_deg": None,
    # How far clear of its own skirt the rim has to rise, as a fraction of the
    # ring radius. **This is the number, and the angle is not**: what has to
    # happen is that the opening under the turret becomes visible, and how much
    # cant that takes depends on how deep the skirt hangs.
    #
    # Measured on two tanks rather than assumed. MT_PARTS_1 reads at 24 deg with
    # a skirt 0.117 of its ring radius, which fixes this at sin(24) - 0.117.
    # HT_PARTS hangs its skirt 0.193 - two thirds deeper - and at 24 deg shows no
    # opening whatever; the rule puts it at 28.9, which does. A per-tank angle
    # would have been a table with no rule in it, and it was very nearly written.
    #
    # What is *not* the explanation, checked before settling on this: the turret
    # overhanging its ring. Both turrets are narrower than their rings by almost
    # the same margin - 0.818 on the heavy, 0.828 on the medium.
    "rim_clear_frac": 0.2898,
    # nose-up on top of the roll, so the rim opens on more than one azimuth
    "pitch_deg": 8.0,
    # which way it tipped, in the turret's own frame: +1 rolls the gun's left
    # side down. Either is as true as the other; it is named so that a tank
    # posed the other way is a decision and not a sign slip.
    "cant_sign": 1.0,

    # the gun falls to its stop. Degrees about the trunnion, which is measured
    # off the bore rather than guessed at - see `_trunnion`.
    "droop_deg": 18.0,

    # the belt goes slack: outboard and down over the front of the loop, as
    # fractions of the hull's own length so this carries between tanks.
    "slack_out": 0.060,
    "slack_down": 0.072,
    # how much of the loop, measured from its front end, the slack covers
    "slack_reach": 0.40,
    # the ramp towards the front end. Above 1 the slack gathers at the very
    # front instead of spreading evenly, which is what a belt off its idler does.
    "slack_power": 1.5,
}

_REST = {}


def _parts():
    return importlib.import_module("tank_parts")


def _mesh(name):
    return _parts().mesh(name)


def _hull_scale(cfg):
    """The hull's own length, which the slack is quoted in fractions of.

    Read off the stamp `hit_point` already left rather than measured again: two
    opinions about how long the hull is would be two things to keep in step.
    """
    hull = _mesh(cfg["hull"])
    scale = hull.get("hit_scale")
    if scale is not None:
        return float(scale)
    import numpy as _np
    co = _np.empty(len(hull.data.vertices) * 3)
    hull.data.vertices.foreach_get("co", co)
    M = _np.array(hull.matrix_world)
    w = co.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
    return float((w.max(axis=0) - w.min(axis=0)).max())


def _ring(cfg):
    """Where the turret turns and how wide its rim is, from the stamps.

    The cant pivots on the ring, so it uses the ring `turret_axis` measured -
    not the empty, which on this scene stands 1.3px off it and on MT_PARTS 14.6.
    """
    import mathutils
    tur = _mesh(cfg["turret"])
    hull = _mesh(cfg["hull"])
    axis = tur.get("ring_axis") or hull.get("ring_axis")
    if axis is None:
        raise RuntimeError(
            "no ring_axis stamped on %r or %r - run turret_axis.set_axis first; "
            "the cant pivots on the ring and guessing it walks the turret round "
            "a circle" % (tur.name, hull.name))
    z = tur.get("ring_z")
    if z is None:
        z = hull.get("ring_z")
    return mathutils.Vector((float(axis[0]), float(axis[1]), float(z or 0.0)))


def _trunnion(cfg):
    """The gun's pivot: the breech end of the tube, on the bore.

    From the bore axis rather than from a bounding box, so it is right for a gun
    that does not happen to lie along an axis of the scene. `barrel_recoil`
    already measured both the axis and the tube's extent along it.
    """
    import mathutils
    br = importlib.import_module("barrel_recoil")
    r = br.reach({"barrel": cfg["barrel"], "turret": cfg["turret"],
                  "root": cfg["root"]})
    point = mathutils.Vector(r["bore_point"])
    axis = mathutils.Vector(r["bore_dir"])
    # `barrel_along_bore` is [breech, muzzle] as distances from the bore point
    return point + axis * float(r["barrel_along_bore"][0]), axis, r


def _cant_deg(cfg, radius, skirt):
    """How far to tip the turret, from the skirt it has to clear.

    Derived rather than set, because the angle is not the thing being asked for -
    the opening under the turret becoming visible is, and a deeper skirt hides it
    for longer. Set `cant_deg` to a number to force one.
    """
    if cfg.get("cant_deg") is not None:
        return float(cfg["cant_deg"])
    if not radius:
        raise RuntimeError(
            "no ring_radius stamped, so the cant cannot be derived - run "
            "turret_axis.set_axis, or set wreck_pose.CONFIG['cant_deg']")
    want = skirt / float(radius) + float(cfg["rim_clear_frac"])
    return math.degrees(math.asin(min(0.95, max(0.0, want))))


def measure(cfg=None):
    """Everything the pose is built from, and what it costs in world units.

    Reported rather than kept private: the lift over the skirt is the number
    that says whether the void will show at all, and it is a property of the
    tank, not of this module.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    import mathutils
    tur = _mesh(cfg["turret"])
    ring = _ring(cfg)
    rr = tur.get("ring_radius")
    bb = [tur.matrix_world @ mathutils.Vector(c) for c in tur.bound_box]
    skirt = float(ring.z - min(v.z for v in bb))
    cant = _cant_deg(cfg, rr, skirt)
    out = {
        "ring": [round(float(v), 6) for v in ring],
        "ring_radius": round(float(rr), 5) if rr is not None else None,
        "skirt_below_ring": round(skirt, 5),
        "skirt_over_radius": round(skirt / float(rr), 5) if rr else None,
        "cant_deg": round(cant, 3),
        "cant_from": "forced" if cfg.get("cant_deg") is not None
                     else "skirt + rim_clear_frac %.4f" % cfg["rim_clear_frac"],
        "pitch_deg": float(cfg["pitch_deg"]),
        "droop_deg": float(cfg["droop_deg"]),
        "hull_scale": round(_hull_scale(cfg), 5),
    }
    if rr:
        lift = float(rr) * math.sin(math.radians(cant))
        out["rim_lift"] = round(lift, 5)
        out["rim_over_skirt"] = round(lift - skirt, 5)
        out["deg_to_clear_skirt"] = round(math.degrees(
            math.asin(min(1.0, skirt / float(rr)))), 2)
    try:
        _piv, _axis, reach = _trunnion(cfg)
        out["barrel_length"] = reach["barrel_length"]
        out["muzzle_drop"] = round(float(reach["barrel_length"])
                                   * math.sin(math.radians(
                                       float(cfg["droop_deg"]))), 5)
    except (KeyError, RuntimeError) as exc:
        out["barrel"] = "skipped: %s" % exc
    return out


def _posed(cfg):
    """The children this pose moves: the turret, and the gun if there is one."""
    tur = _mesh(cfg["turret"])
    try:
        _mesh(cfg["barrel"])
    except (KeyError, RuntimeError):
        return [tur], None
    return [tur, _mesh(cfg["barrel"])], _mesh(cfg["barrel"])


def capture(cfg=None):
    """Remember where these parts rest, **before** the job starts.

    Not left to the first hook call, and that is a bug this has already had. The
    hook runs inside `render_atlas`, by which time the barrel layer has finished
    its recoil phases and left the tube part-way back in its bore - so a lazy
    capture recorded a *recoiled* tube as rest, posed the wreck's gun from there,
    and put the tube back 1.36px out at the end of the job. Neither shows in any
    picture: the tube is 32px long and never leaves its own aperture. It shows in
    the .blend, next time.

    The renderer restores a layer *root*; these are children of one, so the rest
    pose has to be taken while the scene is actually at rest, which is what
    `parts_render.Body.__init__` is.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    obs, _bar = _posed(cfg)
    for ob in obs:
        _REST.setdefault(ob.name, ob.matrix_basis.copy())
    return sorted(_REST)


def pose_turret(cfg=None, strict=False):
    """Cant the turret and drop the gun, from the rest pose every time.

    Always from rest rather than from wherever the last call left it: a phase
    hook runs once per phase and twice per job if a layer is re-rendered, and a
    pose that composes with itself would double the cant the second time.

    `strict` refuses to take that rest from the scene as it stands. The hook sets
    it, because by the time a hook runs the scene is mid-job - see `capture`.
    """
    import bpy
    import mathutils
    cfg = dict(CONFIG, **(cfg or {}))
    ring = _ring(cfg)
    posed, bar = _posed(cfg)
    tur = posed[0]
    piv = axis = None
    if bar is not None:
        piv, axis, _reach = _trunnion(cfg)
    for ob in posed:
        if strict and ob.name not in _REST:
            raise RuntimeError(
                "%r has no captured rest pose - call wreck_pose.capture() while "
                "the scene is at rest. Taking it here reads the scene mid-job, "
                "and the gun has already recoiled by then" % ob.name)
        _REST.setdefault(ob.name, ob.matrix_basis.copy())
    # back to rest before anything is applied, and *this* is what makes the pose
    # idempotent: a hook runs once per phase and again if a layer is re-rendered,
    # and a turret whose basis was composed with the tip a second time would come
    # out at twice the cant. The bbox below has to be read at rest too, or the
    # derived angle changes between calls.
    for ob in posed:
        ob.matrix_basis = _REST[ob.name].copy()
    bpy.context.view_layer.update()

    # the gun falls first, about its trunnion and about the horizontal
    # perpendicular to the bore, so the drop is elevation and not a twist
    if bar is not None:
        side = mathutils.Vector((0.0, 0.0, 1.0)).cross(axis)
        if side.length < 1e-6:                       # a gun pointing at the sky
            side = mathutils.Vector((1.0, 0.0, 0.0))
        side.normalize()
        drop = (mathutils.Matrix.Translation(piv)
                @ mathutils.Matrix.Rotation(
                    math.radians(float(cfg["droop_deg"])), 4, side)
                @ mathutils.Matrix.Translation(-piv))
        bar.matrix_basis = drop @ _REST[bar.name]

    # then the whole turret tips into the ring, and the gun rides with it. The
    # angle comes off the skirt it has to clear rather than out of the config -
    # see `_cant_deg`, and `measure` reports what it worked out.
    bb = [tur.matrix_world @ mathutils.Vector(c) for c in tur.bound_box]
    cant = _cant_deg(cfg, tur.get("ring_radius"),
                     float(ring.z - min(v.z for v in bb)))
    roll = mathutils.Matrix.Rotation(
        math.radians(cant * float(cfg["cant_sign"])), 4, "Y")
    pitch = mathutils.Matrix.Rotation(
        math.radians(-float(cfg["pitch_deg"])), 4, "X")
    tip = (mathutils.Matrix.Translation(ring) @ roll @ pitch
           @ mathutils.Matrix.Translation(-ring))
    for ob in posed:
        ob.matrix_basis = tip @ ob.matrix_basis
    bpy.context.view_layer.update()
    return [ob.name for ob in posed]


def slack(belt, cfg=None):
    """Pull one belt outboard and down over the front of its loop.

    `belt` is a `track_cycle.Belt`, so this goes through the same map the
    animation does: every vertex already carries its arc length along the loop,
    and the rest pose it is measured from is the one the belt arrived in.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    import mathutils
    co = belt.coords_at(0.0).copy()
    M = belt.ob.matrix_world
    Ri = M.to_3x3().inverted()
    w = np.array([(M @ mathutils.Vector(p))[:] for p in co])

    # the front of the tank is -Y by the project's import convention, and the
    # belt runs along it. Confirmed per tank off frame 0 of the render, not here.
    y0, y1 = float(w[:, 1].min()), float(w[:, 1].max())
    span = max(y1 - y0, 1e-9)
    cut = y0 + span * float(cfg["slack_reach"])
    t = np.clip((cut - w[:, 1]) / max(cut - y0, 1e-9), 0.0, 1.0)
    t = t ** float(cfg["slack_power"])

    scale = _hull_scale(cfg)
    # outboard is away from the tank's centre line, and which way that is comes
    # from where this belt sits rather than from its name: `track_split` leaves
    # both belts with the same rotation and tells them apart by an offset.
    sgn = 1.0 if float(w[:, 0].mean()) > 0.0 else -1.0
    d = mathutils.Vector((sgn * float(cfg["slack_out"]) * scale, 0.0,
                          -float(cfg["slack_down"]) * scale))
    local = np.array((Ri @ d)[:])
    co += t[:, None] * local[None, :]
    belt.write(co)
    return {"belt": belt.ob.name,
            "front_y": round(y0, 5), "cut_y": round(cut, 5),
            "moved": int((t > 0.01).sum()),
            "out_world": round(float(cfg["slack_out"]) * scale, 5),
            "down_world": round(float(cfg["slack_down"]) * scale, 5),
            "outboard": sgn}


def restore(cfg=None):
    """Put the turret and the gun back. The belts belong to their own owner.

    The renderer restores a layer *root*; both of these are children of one, so
    nothing else will. Belongs in the caller's `finally`, like every other pose
    in this project.
    """
    import bpy
    if not _REST:
        return {}
    put = {}
    for name, basis in list(_REST.items()):
        ob = bpy.data.objects.get(name)
        if ob is not None:
            # how far it had been moved, reported rather than promised - the
            # belts already say `belts_restored` for the same reason
            put[name] = round(
                max(abs(a - b) for ra, rb in zip(ob.matrix_basis, basis)
                    for a, b in zip(ra, rb)), 9)
            ob.matrix_basis = basis
        _REST.pop(name)
    bpy.context.view_layer.update()
    return put


def turret_layer(cfg=None):
    """The wreck's turret, gun included, as one pose over the whole carousel.

    Indexed by turret bearing exactly as the live turret layer is, which is the
    whole reason the wreck is not one baked sprite - see the module docstring.

    `fit: False`, and for the reason the muzzle flash has it: a canted turret
    stands higher than a seated one, so letting it size the frame would shrink
    the live tank in every other layer. It also makes `units_per_pixel` provably
    identical to the set that shipped before this layer existed.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    hook = _TurretHook(cfg)
    return {"name": "wreck_turret", "target": cfg["root"],
            "fit": False, "phases": 1, "phase_hook": hook,
            "meta_extra": {"wreck": measure(cfg)}}


def belt_layer(name, root, belts, cfg=None, drop=()):
    """One wreck belt layer: the same mesh the live track layer draws, slack.

    Two layers on one root drawing one mesh is safe because each poses it in its
    own hook, and `track_cycle`'s hook writes from the belt's captured rest pose
    rather than from whatever the previous layer left - so the order of the two
    cannot matter.

    **`drop` is not optional in practice and this layer shipped once without it.**
    Each track root carries two belts: the one delivered with the model and the
    rebuilt copy laid out link by link so its cycle closes. The live track layer
    excludes the original and draws the copy; this one omitted the exclusion
    entirely and drew *both* - the slack copy the hook had posed and the original
    still at rest, one inside the other. It reads as a doubled track, which is
    what it is, and no number in the report says so: the layer renders, it is not
    empty, and the belt that was meant to move is present.

    The original stays in the scene on purpose - `track_cycle.rebuild` is
    non-destructive - so the exclusion is the right form of hiding it rather than
    deleting it, and it has to be per layer because the renderer restores what it
    hid.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    layer = {"name": name, "target": root, "fit": False,
             # the hull is welded to the belts, so holding it out is legal
             "holdout": [cfg["hull"]],
             "phases": 1, "phase_hook": _BeltHook(belts, cfg)}
    if drop:
        layer["exclude"] = sorted(drop)
    return layer


class _TurretHook:
    """A callable rather than a closure so the report can name what it did."""

    def __init__(self, cfg):
        self.cfg = cfg
        self.posed = None

    def __call__(self, phase, phases):
        self.posed = pose_turret(self.cfg, strict=True)


class _BeltHook:
    def __init__(self, belts, cfg):
        self.belts = list(belts)
        self.cfg = cfg
        self.report = None

    def __call__(self, phase, phases):
        self.report = [slack(b, self.cfg) for b in self.belts]
