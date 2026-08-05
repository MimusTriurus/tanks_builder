"""
The mark a shell leaves: soot, a gouge, a hole through the plate.

`build()` makes the object; hand `phase_hook(face)` to a `sprite_atlas` layer
per plate, with `"fit": False` and `"phases": len(LEVELS)`. The plates come from
`hit_point.set_hits()` - this module measures nothing of its own, for the reason
`engine_fire` measures nothing of its own: a second measurement of the same
armour would be a second thing to keep in agreement.

Welded to the tank, and that is the whole difference from the burst
--------------------------------------------------------------------
`hit_burst` renders **once** for all twelve headings, and it may: a hit is a
ball of light and a ball looks the same from every azimuth. A hole in a plate is
the opposite kind of object. It is flat, it is stuck to one face of the tank,
and it has to turn with the hull, foreshorten as its plate turns away and
disappear behind the tank when the plate does. So this one is rendered at every
heading, like the hull it sits on.

Which buys something the burst had to fake: the tank's own geometry can be a
**holdout**, so the occlusion is baked into the layer's alpha exactly where it
belongs. When the plate faces away the hull stands in front of the decal and
cuts all of it, so the layer goes empty by itself and the harness needs no
front-or-behind rule at all. The burst could not do this - one render used at
twelve headings would have heading zero's silhouette burnt into it - and paid
for it with a draw-order trick in the harness. Here the expensive layer is the
correct one.

It has to be lifted off the plate, though. Coplanar with the armour it is a
coin toss which surface the depth test keeps, and the holdout eats the decal in
patches that move with the heading. `lift` is in hull lengths and comes to
around a pixel and a half - enough to clear rivets and weld beads, small enough
that the parallax of holding it off the plate is invisible.

Phases are damage, not time
---------------------------
Every other effect here uses the phase axis for time and closes the cycle or
runs a hold table. A hole does not develop and does not fade: it is a *state*,
and the axis carries how bad it is - soot, gouge, breach. So there is no clock
in the harness and nothing to check for seamlessness; what there is instead is
an ordering, which the check asserts: each level has to be plainly heavier than
the one before, or the axis is three renders of one drawing.

It is seen against dark paint, and every colour here follows from that
-----------------------------------------------------------------------
Every other effect in this pipeline is judged over the game's pale field,
because that is what it is drawn on: `engine_fire`, `exhaust_plume` and
`hit_burst` all carry a paragraph about how little an additive or a thin
occluding layer survives over 0.65 grey. This one is drawn on the tank, whose
paint measures 0.17 to 0.27 of stored value, and the rule inverts twice over.

**A dark stop has to be an order of magnitude darker than it looks.** Colours
here are linear and the render encodes them, so a linear 0.05 - which reads as
"nearly black" in a config - arrives as 0.24, the same value as the paint. The
first soot was 0.048 and was invisible on the hull while measuring 71 levels
against the pale ground. Soot lives around 0.008 linear, a hole around 0.002.

**And soot alone cannot carry a mark at all.** The most a perfectly black stop
can move the picture is the paint's own value: about forty levels on the front
plate. So the readable part of damage on armour is the *metal it bares*, which
is why every level here has a light stop in it - even the one that is nominally
just a scorch. That is the exact mirror of the additive layers, which fail by
going too light and are held to a colour instead.

Sizes are from the hull, not from the plate
--------------------------------------------
A shell is a shell. Scaling the mark to the plate it lands on would make the
same round fired at the same tank leave a small mark on the glacis and a large
one on the side skirt, which is backwards - the side skirt is thinner. The same
argument that gives `engine_fire` its size from the hull rather than from the
exhaust port. What the plate does decide is whether the mark *fits*, and the
check says so when it does not.

One object, four layers
-----------------------
`Scar` is rebuilt per (face, level) by the hook, so the four plates share one
object and one material and the renderer walks them one layer at a time. The
faces are separate layers rather than one layer with a face-by-level phase axis
because the game turns them on separately: a tank with a holed glacis and a
clean flank is the ordinary case, and folding both into one number would make
"which plate" and "how bad" the same index.

Do not parent it to anything
----------------------------
Same trap as `Flash`, `Plume`, `Fire`, `Burn` and `Burst`: under `Hull` it would
be drawn into the hull atlas permanently, and every tank in the game would roll
off the line already holed.
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
    # the hull layer's root, when there is one. The probe below needs the same
    # group the hit rays are fired at and for the same reason: on a parts scene
    # the exhaust louvre is a *sibling* of the hull mesh, and the rear plate is
    # assembled out of both. A probe that saw only the hull mesh would read the
    # plate as clear exactly where the louvre stands in front of it.
    "hull_root": None,
    "name": "Scar",

    # How far off the plate the decal floats, in hull lengths. About 1.5px on
    # MT. Too little and the holdout eats it in patches (see the docstring);
    # too much and it reads as a sticker hovering over the armour.
    "lift": 0.010,

    # Push the decal out past the armour under it when, and only when, the
    # camera cannot see it where `lift` puts it.
    #
    # `lift` clears rivets and it is measured from the plate's *fitted plane*. On
    # a plate that is not flat the metal stands in front of that plane, and the
    # lift is spent climbing back to the surface instead of clearing it. HT's
    # rear plate is the case that named this: the plane is a good fit (the
    # surface under the mark deviates 0.4px from it), the decal sat its full
    # 1.57px proud, the plate itself is 98% visible - and only **30%** of the
    # mark survived, because the plate is recessed under the engine deck and from
    # a camera 30 degrees up the tank's own overhang covers the rest. Seated on
    # the local high point, 3.3px out, it is **100%** visible.
    #
    # Conditional, because the high point alone is too blunt a rule: HT's glacis
    # bulges 7.8px inside the mark's footprint - a towing hook or a lamp standing
    # proud - and a decal floating that far off a plate it is already 94% visible
    # on reads as a sticker hovering over the tank. So visibility is measured
    # first and the seat is only raised for a mark that is actually being eaten.
    # That makes it a no-op wherever things already work, which is what makes it
    # safe for tanks already rendered, and it only ever pushes *outward*, so a
    # mark that was visible cannot become hidden.
    "seat_on_bulge": True,
    # rays per pass, over the largest mark's own footprint. The footprint and not
    # the plate: the plate can bulge anywhere, but only what is under the mark
    # can eat it - and only the mark's own outline can reach past the plate's
    # edge into whatever stands beyond it, which is exactly what happens here.
    "seat_samples": 72,
    # Least of the mark the camera must see, at the plate's best heading, before
    # the seat is left alone. HT: rear 0.30 (raised), glacis 0.94 and both sides
    # 1.00 (left alone).
    "seat_min_visible": 0.70,

    # Angular resolution of the disc. 26 segments is where the outline stops
    # showing facets at these radii; rings are cheap and buy a smooth ramp.
    "segments": 26,
    "rings": 9,

    # The outline is wobbled by a few low-frequency waves rather than by
    # per-segment noise. Independent noise per segment gives a star - the same
    # failure `hit_burst` hit twice, where a construction that is regular by
    # design gets jitter applied to it and comes out regular and spiky instead
    # of irregular. Frequencies are deliberately not multiples of each other.
    "waves": (2, 3, 5, 7),
    # Wobble grows with radius: a shell hole is round and the soot around it is
    # not, so the outer rings carry it and the core stays circular.
    "wobble_power": 1.6,
    # Blotching of the soot, on its own set of waves, so the halo is not a
    # clean gradient.
    "blotch": 0.30,
    "blotch_waves": (3, 5, 8),

    "seed": 907,
    "opacity": 1.0,

    "material": "_hit_scar",
}

# Damage, one entry per phase. `radius` is in hull lengths; the ramp runs from
# the centre of the mark to `radius` and is sampled per ring.
#
# Everything dark is soot and everything light is bare metal, and the light
# stops are what make a hole read as a hole rather than as a stain: a black
# ellipse alone is a shadow. They are kept narrow for the reason the fire's
# ramp keeps its yellows narrow - a wide light band on a small mark reads as
# glare, not as torn steel.
LEVELS = (
    {
        # Nothing got through. What is left is the burn, and it is wide and
        # thin rather than small and deep - a round that skated off spreads its
        # soot further than one that went in.
        #
        # Bare metal at the *centre* rather than soot, and it is not a liberty:
        # see the docstring on what the backdrop is. Soot alone cannot move
        # dark green paint by more than the paint's own value - about forty
        # levels of 255 - however black the soot is, so a mark made only of
        # soot is one you have to be told is there. The first version was
        # exactly that, and measured perfectly well against the pale ground.
        "name": "scorch",
        "radius": 0.084,
        "wobble": 0.36,
        "stretch": 1.30,
        "crescent": 0.70,
        "crescent_at": 205.0,
        "ramp": [(0.00, (0.210, 0.195, 0.175), 0.86),
                 (0.16, (0.060, 0.055, 0.050), 0.85),
                 (0.34, (0.016, 0.015, 0.013), 0.80),
                 (0.62, (0.020, 0.018, 0.016), 0.42),
                 (1.00, (0.028, 0.026, 0.023), 0.00)],
    },
    {
        # A gouge: the round dug in and slid off, leaving bright metal in the
        # bottom of it and soot around.
        #
        # Stretched and one-sided, and both are load-bearing. A small bright
        # ring on dark paint is a rivet - the first version drew four of them
        # and they read as hardware, not as damage. A scrape has a direction:
        # long, with the metal bared at the end the round came out of and the
        # soot trailing behind it.
        "name": "gouge",
        "radius": 0.092,
        "wobble": 0.32,
        "stretch": 1.70,
        "crescent": 0.85,
        "crescent_at": 205.0,
        "ramp": [(0.00, (0.0060, 0.0055, 0.0050), 0.94),
                 (0.15, (0.0060, 0.0055, 0.0050), 0.94),
                 (0.26, (0.300, 0.278, 0.250), 0.90),
                 (0.38, (0.030, 0.027, 0.024), 0.72),
                 (0.66, (0.020, 0.018, 0.016), 0.34),
                 (1.00, (0.028, 0.026, 0.023), 0.00)],
    },
    {
        # Through. A black hole with the metal torn back around it.
        #
        # The torn rim is what makes this read, not the hole. Against the
        # game's pale field an additive effect needs something dark behind it;
        # here it is the other way round and for the same reason - the backdrop
        # is dark green paint, and the blackest possible hole in it is worth
        # forty levels while the bare metal torn back around it is worth a
        # hundred and thirty.
        "name": "breach",
        "radius": 0.100,
        "wobble": 0.28,
        "stretch": 1.10,
        "ramp": [(0.00, (0.0016, 0.0015, 0.0014), 0.99),
                 (0.28, (0.0016, 0.0015, 0.0014), 0.99),
                 (0.36, (0.420, 0.390, 0.350), 0.96),
                 (0.47, (0.070, 0.063, 0.056), 0.80),
                 (0.66, (0.022, 0.020, 0.018), 0.48),
                 (1.00, (0.028, 0.026, 0.023), 0.00)],
    },
)


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


def _hull(cfg):
    ob = _mod("tank_parts").mesh(cfg["hull"], required=False)
    if ob is None:
        raise RuntimeError("no %r in the scene" % cfg["hull"])
    return ob


def plates(cfg=None):
    """face -> (point, normal, tangent, extent), and the hull's length."""
    cfg = dict(CONFIG, **(cfg or {}))
    return _mod("hit_point").read_plates(_hull(cfg))


def faces(cfg=None):
    """The plates that were found, in the order they were stamped."""
    found, _scale = plates(cfg)
    return list(found.keys())


def _mark_extent(scale):
    """Half-extents of the largest mark, across the plate and up it.

    One definition, used by the seat probe and by `fits`. Across and along are
    separate because a gouge is stretched, and one number for both would call a
    long scrape on a wide plate too big.
    """
    tall = max(float(l["radius"]) for l in LEVELS) * scale
    wide = max(float(l["radius"]) * float(l.get("stretch", 1.0))
               for l in LEVELS) * scale
    return wide, tall


def _armour(cfg):
    """The meshes the decal has to sit clear of."""
    tp = _mod("tank_parts")
    scene = bpy.context.scene
    root = (scene.objects[cfg["hull_root"]] if cfg.get("hull_root")
            else _hull(cfg))
    return [o for o in tp.hierarchy(root)
            if o.type == "MESH" and len(o.data.vertices)]


def _footprint(point, normal, tangent, scale, cfg):
    """Sample points over the largest mark's outline, in world space."""
    n = Vector(normal).normalized()
    t = Vector(tangent).normalized()
    u = n.cross(t).normalized()
    wide, tall = _mark_extent(scale)
    rings = max(1, int(cfg["seat_samples"]) // 12)
    pts = []
    for ring in range(rings + 1):
        frac = ring / float(rings)
        spokes = 1 if ring == 0 else 12
        for k in range(spokes):
            th = 2.0 * math.pi * k / float(spokes)
            pts.append(Vector(point) + t * (frac * wide * math.cos(th))
                       + u * (frac * tall * math.sin(th)))
    return pts, n


def _seen_from_camera(pts, n, out, targets, dg, scale, cfg):
    """The share of `pts` the camera can see, at the best of the headings.

    The camera comes from `sprite_atlas`, not from a basis rebuilt out of the
    angles, for the reason the plate table takes it from there. The render spins
    the tank and holds the camera still, so the ray is spun back instead and the
    geometry stays where it is.
    """
    sa = _mod("sprite_atlas")
    hp = _mod("hit_point")
    hpc = hp.CONFIG
    cam = sa.camera_matrix(Vector((0.0, 0.0, 0.0)), hpc["azimuth"],
                           hpc["camera_elevation"], 1.0)
    toward = cam.col[2].to_3d().normalized()
    steps = max(1, int(hpc["check_steps"]))
    best, best_step = 0.0, 0
    for step in range(steps):
        spin = Matrix.Rotation(math.radians(360.0 * step / steps), 3, "Z")
        to_cam = spin.transposed() @ toward       # a rotation: transpose inverts
        if n.dot(to_cam) <= 0.0:
            continue                              # turned away; nothing to see
        clear = sum(1 for p in pts
                    if hp._cast(targets, dg, p + to_cam * 1e-4, to_cam,
                                scale * 3.0) is None)
        share = clear / float(len(pts))
        if share > best:
            best, best_step = share, step
    return best, best_step


def seat(face, cfg=None):
    """How far out the decal has to sit on this plate, and why that much.

    Two passes, and the order is the whole point. First: is the mark visible
    where `lift` puts it? If it is, that is the answer and nothing moves - see
    CONFIG["seat_on_bulge"] for why a rule that always seated on the high point
    is worse than no rule on a glacis with a towing hook on it.

    Only for a mark the camera cannot see are rays fired down the plate's normal
    over the mark's own footprint, and the seat becomes the high point they find.
    The largest mark's footprint for every level on purpose: one seat per plate
    means the three levels share a plane, and a deeper hit that floated
    differently from a lighter one would read as the armour moving.

    Probed against the hull group, not with `scene.ray_cast`, and that was worth
    two wrong answers here: the scene cast hits `Scar` itself and the effect
    objects that the last run leaves in the scene, so it reports a bulge which is
    really the previous frame's decal.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    found, scale = plates(cfg)
    if face not in found:
        raise RuntimeError("no %r plate stamped on %s" % (face, cfg["hull"]))
    point, normal, tangent, _extent = found[face]
    lift = float(cfg["lift"]) * scale
    if not cfg["seat_on_bulge"]:
        return lift, {"seat_on_bulge": False}

    hp = _mod("hit_point")
    targets = _armour(cfg)
    dg = bpy.context.evaluated_depsgraph_get()
    pts, n = _footprint(point, normal, tangent, scale, cfg)

    at_lift, best_step = _seen_from_camera([p + n * lift for p in pts], n, normal,
                                           targets, dg, scale, cfg)
    why = {"seat_on_bulge": True, "lift": round(lift, 6),
           "visible_at_lift": round(at_lift, 3), "best_heading": best_step,
           "probed": len(pts)}
    if at_lift >= float(cfg["seat_min_visible"]):
        why["raised"] = False
        return lift, why

    depths = []
    for q in pts:
        got = hp._cast(targets, dg, q + n * scale, -n, scale * 2.0)
        if got is not None:
            depths.append(float((got[1] - Vector(point)).dot(n)))
    # never inward: a surface sitting *behind* its own fitted plane under the
    # whole mark is already clear, and pulling the decal in to meet it would undo
    # the clearance `lift` is there to give
    bulge = max(max(depths), 0.0) if depths else 0.0
    seated = bulge + lift
    after, _ = _seen_from_camera([p + n * seated for p in pts], n, normal,
                                 targets, dg, scale, cfg)
    why.update({"raised": True, "bulge": round(bulge, 6),
                "visible_after": round(after, 3)})
    return seated, why


def seats(cfg=None):
    """Per plate: the seat, and the bulge that asked for it. In hull lengths."""
    cfg = dict(CONFIG, **(cfg or {}))
    _found, scale = plates(cfg)
    out = {}
    for face in faces(cfg):
        distance, why = seat(face, cfg)
        out[face] = dict(why, seat=round(distance, 6),
                         seat_over_lift=round(distance / max(why["lift"], 1e-9), 2)
                         if why.get("lift") else None)
    out["hull_length"] = round(scale, 6)
    return out


def _mark_visibility(cfg=None):
    """Per plate: how much of the mark the camera sees, where it is being put.

    The one number that says whether this layer has anything on it, and the one
    that was missing: `faceon` 0.999 and `visible` 0.98 both described HT's rear
    plate correctly while every mark drawn on it was eaten.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    found, scale = plates(cfg)
    targets = _armour(cfg)
    dg = bpy.context.evaluated_depsgraph_get()
    out = {}
    for face, (point, normal, tangent, _e) in found.items():
        pts, n = _footprint(point, normal, tangent, scale, cfg)
        distance, _why = seat(face, cfg)
        share, step = _seen_from_camera([p + n * distance for p in pts], n,
                                        normal, targets, dg, scale, cfg)
        out[face] = {"visible": round(share, 3), "at_heading": step}
    return out


# ---------------------------------------------------------------------------
# geometry
# ---------------------------------------------------------------------------

def _waves(theta, freqs, seed):
    """A smooth [-1, 1] wobble around the circle.

    Sum of a few sinusoids at hashed phases rather than noise per segment: the
    disc is regular by construction, and noise applied to something regular
    gives a star every time.
    """
    mf = _mod("muzzle_flash")
    out = np.zeros_like(theta)
    for i, f in enumerate(freqs):
        phase = mf._hash01(np.array(seed + i * 71)) * 2.0 * math.pi
        out += np.sin(f * theta + float(phase)) / math.sqrt(f)
    return out / sum(1.0 / math.sqrt(f) for f in freqs)


def _disc(cfg, level, scale):
    """A flat mark in its own plane: x, y across the plate, z off it."""
    mf = _mod("muzzle_flash")
    rings = int(cfg["rings"])
    segments = int(cfg["segments"])
    radius = float(level["radius"]) * scale

    theta = np.arange(segments) * (2.0 * math.pi / segments)
    edge = _waves(theta, cfg["waves"], cfg["seed"])
    blot = _waves(theta, cfg["blotch_waves"], cfg["seed"] + 1301)

    # Long one way, and the long axis is the plate's own tangent - which is
    # horizontal on the armour, so a scrape lies along the plate rather than
    # running up and down it.
    stretch = float(level.get("stretch", 1.0))
    # Which end of that the bare metal is at, and how much of the mark it
    # takes. `1` at the chosen bearing, `1 - crescent` opposite it.
    lean = float(level.get("crescent", 0.0))
    at = math.radians(float(level.get("crescent_at", 180.0)))
    side = (1.0 - lean) + lean * (0.5 + 0.5 * np.cos(theta - at))

    # ring 0 is the first ring out from the centre, so the fan has a proper
    # apex rather than a pinched quad
    t = (np.arange(1, rings + 1) / float(rings))
    verts = [np.zeros(3)]
    colour = [mf._ramp(level["ramp"], 0.0)[0]]
    for i, u in enumerate(t):
        # the wobble is applied to the *radius* and grows outward, so the core
        # stays round and the soot edge does not
        grow = u ** float(cfg["wobble_power"])
        r = radius * u * (1.0 + float(level["wobble"]) * grow * edge)
        ring = np.stack([r * np.cos(theta) * stretch, r * np.sin(theta),
                         np.zeros(segments)], axis=1)
        verts.append(ring)
        rgba = mf._ramp(level["ramp"], u)[0]
        band = np.repeat(rgba[None, :], segments, axis=0)
        # blotching only bites where there is soot to blotch - at the centre it
        # would punch a hole in the mark itself
        band[:, 3] *= 1.0 - float(cfg["blotch"]) * grow * (0.5 + 0.5 * blot)
        # and the crescent bites at the centre and lets go outward, so it
        # one-sides the crater without cutting the soot in half
        band[:, 3] *= 1.0 - (1.0 - side) * (1.0 - u)
        colour.append(band)

    verts = np.concatenate([v.reshape(-1, 3) for v in verts], axis=0)
    colour = np.concatenate([c.reshape(-1, 4) for c in colour], axis=0)

    # one apex, then rings; the outer edge is closed with a fan back to the
    # apex, which is degenerate but keeps _grid_faces' shape - so build the
    # faces directly instead
    faces_out = []
    for j in range(segments):
        k = (j + 1) % segments
        faces_out.append((0, 1 + j, 1 + k))
    for i in range(rings - 1):
        base = 1 + i * segments
        nxt = base + segments
        for j in range(segments):
            k = (j + 1) % segments
            faces_out.append((base + j, base + k, nxt + k, nxt + j))
    return verts, faces_out, colour


def _frame(point, normal, tangent, lift):
    """The plate's own axes as a matrix: x along the plate, z off it."""
    n = Vector(normal).normalized()
    t = Vector(tangent)
    t = (t - n * t.dot(n))
    if t.length < 1e-6:
        t = _mod("muzzle_flash")._perpendicular(n)
    t.normalize()
    b = n.cross(t)
    m = Matrix(((t.x, b.x, n.x, 0.0),
                (t.y, b.y, n.y, 0.0),
                (t.z, b.z, n.z, 0.0),
                (0.0, 0.0, 0.0, 1.0)))
    m.translation = Vector(point) + n * lift
    return m


def _place(cfg, material):
    scene = bpy.context.scene
    ob = scene.objects.get(cfg["name"])
    if ob is None:
        ob = bpy.data.objects.new(cfg["name"], bpy.data.meshes.new(cfg["name"]))
        scene.collection.objects.link(ob)
    if ob.parent is not None:
        # see the module docstring: under the hull it goes into the hull atlas
        ob.parent = None
    ob.data.materials.clear()
    ob.data.materials.append(material)
    return ob


def _material(cfg):
    """Emission straight off the vertex colour, transparent by its alpha.

    Its own material rather than `muzzle_flash.build_material`, which was tried
    first with the rim turned off and came out solid black. That material tints
    by the angle to the camera, which is a whole-surface property for something
    flat: a decal has one normal, so `Facing` is one number for the entire
    mark and the tint is either fully on or fully off with nothing in between.
    Set to "off" it lands on the wrong side of a mix and every stop in the ramp
    is discarded - the bare-metal stops simply were not there, and the three
    levels rendered as three sizes of the same dark smudge.

    Which is the general form of it: the puff material shades *across* a
    surface using the way a sphere turns away from the camera, and a flat mark
    has no across to shade.
    """
    mat = bpy.data.materials.get(cfg["material"]) \
        or bpy.data.materials.new(cfg["material"])
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()

    out = tree.nodes.new("ShaderNodeOutputMaterial")
    mix = tree.nodes.new("ShaderNodeMixShader")
    clear = tree.nodes.new("ShaderNodeBsdfTransparent")
    glow = tree.nodes.new("ShaderNodeEmission")
    attr = tree.nodes.new("ShaderNodeAttribute")
    by_opacity = tree.nodes.new("ShaderNodeMath")

    attr.attribute_name = "flame"        # the name _write_mesh writes
    glow.inputs["Strength"].default_value = 1.0
    by_opacity.operation = "MULTIPLY"
    by_opacity.inputs[1].default_value = float(cfg["opacity"])

    tree.links.new(attr.outputs["Color"], glow.inputs["Color"])
    tree.links.new(attr.outputs["Alpha"], by_opacity.inputs[0])
    tree.links.new(by_opacity.outputs[0], mix.inputs["Fac"])
    tree.links.new(clear.outputs[0], mix.inputs[1])       # Fac 0 -> clear
    tree.links.new(glow.outputs[0], mix.inputs[2])
    tree.links.new(mix.outputs[0], out.inputs["Surface"])

    # EEVEE Next names it one way and the older path the other; setting both
    # beats discovering which this build honours from a wrong-looking render.
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "BLENDED"
    if hasattr(mat, "blend_method"):
        mat.blend_method = "BLEND"
    mat.use_backface_culling = False
    return mat


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    ob = _place(cfg, _material(cfg))
    found = faces(cfg)
    set_state(found[0] if found else "front", 0, len(LEVELS), cfg)
    return ob


def set_state(face, index, count=None, cfg=None):
    """Rebuild the mark for one plate at one level of damage.

    `cfg["seats"]`, when given, is `face -> distance` measured **before** the
    render job started, and it is not an optimisation.

    A phase hook runs inside `render_atlas`, and the scene there is mid-flight:
    the layer's own root is put back before the hook is called, but the *holdout*
    objects - the tank - are still spun to the last angle of the previous phase.
    So a hook that measures the scene measures the plate's rest-frame normal
    against geometry standing at some other heading. Measured: seating on a
    bulge probed inside the hook put every mark clear of the hull, visible from
    nine or ten headings out of twelve instead of five, and the check called it
    "100% hangs off the tank" - which is what it was. This is the same trap that
    once made the camera fit read meshes a previous layer's hook had already
    moved, and it has the same rule: measure at rest, hand the number in.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    found, scale = plates(cfg)
    if face not in found:
        raise RuntimeError("no %r plate stamped on %s" % (face, cfg["hull"]))
    point, normal, tangent, _extent = found[face]
    level = LEVELS[max(0, min(int(index), len(LEVELS) - 1))]

    ob = bpy.context.scene.objects.get(cfg["name"])
    if ob is None:
        ob = build(cfg)
    # `seat` rather than `lift`: the plane is a fit, and on a plate that is not
    # flat the metal stands in front of it. See CONFIG["seat_on_bulge"]. Taken
    # from the caller when the caller measured it at rest - see the docstring.
    given = (cfg.get("seats") or {}).get(face)
    distance = float(given) if given is not None else seat(face, cfg)[0]
    ob.matrix_world = _frame(point, normal, tangent, distance)
    verts, faces_out, colour = _disc(cfg, level, scale)
    _mod("muzzle_flash")._write_mesh(ob.data, verts, faces_out, colour)
    return ob


def phase_hook(face, cfg=None):
    """A hook for one plate; the phase is the level of damage."""
    def hook(index, count):
        set_state(face, index, count, cfg)
    return hook


def fits(cfg=None):
    """Per plate: how much of its half-height the worst mark takes up.

    Over 1.0 the mark hangs off the armour it is supposed to be on. It is
    reported rather than clamped, because the answer is a smaller shell or a
    plate that was measured too tightly, and neither is something to guess at
    here.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    found, scale = plates(cfg)
    wide, tall = _mark_extent(scale)
    out = {}
    for name, (_p, _n, _t, extent) in found.items():
        half = [max(float(extent[0]), 1e-9), max(float(extent[1]), 1e-9)]
        out[name] = round(max(wide / half[0], tall / half[1]), 3)
    return out


def remove(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene
    ob = scene.objects.get(cfg["name"])
    if ob is not None:
        data = ob.data
        bpy.data.objects.remove(ob)
        bpy.data.meshes.remove(data)
    mat = bpy.data.materials.get(cfg["material"])
    if mat is not None:
        bpy.data.materials.remove(mat)


if __name__ == "__main__":
    build(CONFIG)
    print("[scar] plates %s" % (faces(CONFIG),))
    print("[scar] fit %s" % (fits(CONFIG),))
