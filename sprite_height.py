"""How high the surface under every pixel of a tank stands.

A byte per pixel: 0 where there is no tank, otherwise where that pixel's
surface sits between the tank's own lowest point and its highest. It exists for
one reader - the waterline - and it exists because the thing that reader used
before is not that line and cannot be made into it.

**The groundline is where a tank meets the bottom, not where it meets the
water.** `AtlasSet.Groundline` is the lowest drawn row per column, which is the
tank's contact with the ground; the bench drew the waterline by lifting it by
the depth of the water. That is exact only where the pixel that projects lowest
in a column sits at the same distance from the camera as the surface the water
plane crosses in that column - and it does not. Measured on `LT_PARTS` at the
bench's own 29 board px of water, over every column of four headings:

    heading   median    p90    worst
      0      -10.5 px   14.8   15.7
     45       -8.0      13.0   17.4
     90       -3.6       9.2   33.8
    180      -15.2      21.9   22.0

The sign is one-way: the drawn line stands *above* the true one, by about a
third of the whole depth on the median column, so a tank reads as less sunk
than it is. And it carries the shape of the tank's underside up to the
waterline, which is where the staircase across the hull came from - a six to
eight pixel step at each track guard, drawn on a surface that is one plane.

**Height, and not depth from the camera.** Depth would answer the same question
and answer several others too, and it is the harder thing to store: it moves
with the heading, it needs the camera's basis to be read back, and its range is
the tank's diagonal. Height is invariant under the turntable - the model spins
about Z - so one range serves all twenty-four frames, and the reader's question
is literally "is this surface under that plane".

**Colour and not alpha, which is the opposite of the shadow catcher's answer
and for a reason worth keeping.** A shadow's value *is* coverage, so alpha
carries it and blending is what draws it. Here two surfaces in one pixel must
not blend: the nearest one is the visible one and the far one must lose
outright. That is the depth buffer's job, and it only runs for an opaque
material - so the value goes in the colour, the material is emissive and
opaque, and the layer renders under a Raw view transform so the byte written is
the number computed. Alpha is left doing what it does everywhere else: saying
whether there is a tank there at all.

**A twin, not a repaint.** The parts stay untouched; what gets the ramp is a
set of linked duplicates - same mesh data, an object-level material - hung off
a root of their own. So nothing about the tank is modified even transiently,
the twin is invisible to every other layer (the renderer hides all that is not
under the layer's own root), and `remove()` is a delete rather than an unwind.

**It is built from the body layers themselves rather than from a list of
names.** Which belt is drawn - the delivered one or its rebuilt replacement -
is settled by each track layer's `exclude`, and a second list here would be a
second answer to that: the height map would describe a belt the atlas does not
draw. Same for the hull and its exhaust louvre.

**The turret is not in it, and that is not thrift.** The map is indexed by the
hull's heading and the turret turns against the hull, so a turret drawn into it
would claim, at every frame but one, that the surface at those pixels is
somewhere it is not. Nothing above the running gear is ever under water at any
depth this board allows, so the omission costs nothing and the alternative is a
number that is wrong and cannot say so - the same argument that keeps a baked
turret holdout out of a hull-indexed layer.
"""

import math
import os

import bpy


CONFIG = {
    "root": "Height.World",       # the twin's own layer root
    "material": "_height_ramp",
    "name": "height",             # -> height_atlas.png / .json
    # Which body layers can ever be under water. The hull carries the exhaust
    # louvre and the tracks carry their rolls; nothing else is ever wet.
    "wet_layers": ("hull", "track_left", "track_right"),
    "view_transform": "Raw",
}


# ---------------------------------------------------------------------------
# the ramp material
# ---------------------------------------------------------------------------

def _ramp_material(cfg, z0, z1):
    """Emissive grey = where this surface sits between `z0` and `z1`.

    Opaque on purpose: the value has to survive two surfaces landing in one
    pixel, and only the depth buffer settles that. An emission shader is not
    lit, so the rig above the tank cannot shade the number.
    """
    name = cfg["material"]
    old = bpy.data.materials.get(name)
    if old is not None:
        bpy.data.materials.remove(old)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()

    geo = tree.nodes.new("ShaderNodeNewGeometry")
    split = tree.nodes.new("ShaderNodeSeparateXYZ")
    ramp = tree.nodes.new("ShaderNodeMapRange")
    ramp.clamp = True
    ramp.inputs["From Min"].default_value = z0
    ramp.inputs["From Max"].default_value = z1
    # Not down to nought: a byte of 0 is how the reader is told there is no tank
    # in that pixel, and the lowest sliver of a track is a tank. One code of
    # headroom is enough to keep the two apart and costs 0.4% of the range.
    ramp.inputs["To Min"].default_value = 1.0 / 255.0
    ramp.inputs["To Max"].default_value = 1.0

    glow = tree.nodes.new("ShaderNodeEmission")
    glow.inputs["Strength"].default_value = 1.0
    out = tree.nodes.new("ShaderNodeOutputMaterial")

    tree.links.new(split.inputs["Vector"], geo.outputs["Position"])
    tree.links.new(ramp.inputs["Value"], split.outputs["Z"])
    tree.links.new(glow.inputs["Color"], ramp.outputs["Result"])
    tree.links.new(out.inputs["Surface"], glow.outputs["Emission"])

    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "DITHERED"    # i.e. opaque, depth-tested
    elif hasattr(mat, "blend_method"):
        mat.blend_method = "OPAQUE"
    mat.use_backface_culling = False
    return mat


# ---------------------------------------------------------------------------
# the twin
# ---------------------------------------------------------------------------

def _drawn_by(layer, atlas):
    """The meshes a body layer actually draws, its own exclusions applied."""
    root = bpy.data.objects[layer["target"]]
    gone = atlas.by_hints(layer.get("exclude"))
    return [o for o in atlas.hierarchy(root)
            if o.type == "MESH" and o not in gone]


def build(layers, atlas, cfg=None):
    """Stand a height-ramped twin of the wet parts beside the tank.

    `layers` is the body layer list - the twin takes its geometry from the ones
    named in `wet_layers`, exclusions and all, so it cannot describe a belt the
    atlas does not draw.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    remove(cfg)

    wet = [l for l in layers if l["name"] in cfg["wet_layers"]]
    if not wet:
        raise RuntimeError("no wet layers among %s"
                           % [l["name"] for l in layers])

    meshes = []
    for layer in wet:
        meshes.extend(_drawn_by(layer, atlas))
    if not meshes:
        raise RuntimeError("the wet layers draw nothing")

    lo = min(min((ob.matrix_world @ v.co).z for v in ob.data.vertices)
             for ob in meshes)
    hi = max(max((ob.matrix_world @ v.co).z for v in ob.data.vertices)
             for ob in meshes)
    mat = _ramp_material(cfg, lo, hi)

    scene = bpy.context.scene
    root = bpy.data.objects.new(cfg["root"], None)
    scene.collection.objects.link(root)
    made = []
    for ob in meshes:
        twin = ob.copy()                 # shares the mesh data, costs nothing
        twin.name = "%s.Height" % ob.name
        twin.animation_data_clear()
        scene.collection.objects.link(twin)
        twin.parent = root
        twin.matrix_parent_inverse = root.matrix_world.inverted()
        twin.matrix_world = ob.matrix_world.copy()
        for slot in twin.material_slots:
            slot.link = "OBJECT"
            slot.material = mat
        if not twin.material_slots:
            twin.data.materials.append(mat)  # only if the source had none
        made.append(twin.name)
    bpy.context.view_layer.update()
    return {"root": cfg["root"], "range": [lo, hi], "objects": made,
            "from": [l["name"] for l in wet]}


def remove(cfg=None):
    """Take the twin and its material away. Safe to call twice."""
    cfg = dict(CONFIG, **(cfg or {}))
    root = bpy.data.objects.get(cfg["root"])
    doomed = []
    if root is not None:
        doomed = [root] + [o for o in bpy.data.objects if o.parent is root]
    for ob in doomed:
        bpy.data.objects.remove(ob, do_unlink=True)
    mat = bpy.data.materials.get(cfg["material"])
    if mat is not None:
        bpy.data.materials.remove(mat)
    return len(doomed)


def layer(info, cfg=None):
    """The job entry for the map, given what `build` made."""
    cfg = dict(CONFIG, **(cfg or {}))
    return {
        "name": cfg["name"],
        "target": info["root"],
        # Never in the fit. It is the same geometry the body layers already
        # size the frame with, so it could not move it - but saying so is what
        # makes `framing_identical` a cross-check against the shipped set
        # rather than a statement about this job alone.
        "fit": False,
        "view_transform": cfg["view_transform"],
        # No rig: an emission shader is not lit, and asking for one would only
        # invite somebody to wonder later why the numbers change with it.
        "own_lighting": False,
        "samples": 1,
        "meta_extra": {"height_range": [round(v, 6) for v in info["range"]],
                       "height_floor": round(1.0 / 255.0, 6)},
    }


# ---------------------------------------------------------------------------
# rendering it onto a set that already shipped
# ---------------------------------------------------------------------------

def render_map(pipeline_cfg=None, cfg=None):
    """Render only this layer, into a set whose other layers already exist.

    The framing is a *result* of fitting the camera to the carousel, so this
    cannot simply render one layer on its own: the body layers have to be in
    the job to size the frame, and the tile has to be built, and the belts have
    to be in the shape they were shipped in. So the job is the real one and
    every layer but this one is stubbed - its framing handed back from the JSON
    lying beside it.

    Which turns `framing_identical` from a formality into the check that
    matters: it now compares this fresh layer against the numbers the shipped
    set was rendered with, and a scene that has drifted since - a belt reshaped,
    an axis restamped - says so instead of quietly writing a map that is a
    fraction of a pixel out of step with the tank it describes.
    """
    import importlib.util
    import json
    import sys

    cfg = dict(CONFIG, **(cfg or {}))
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:            # sprite_atlas imports atlas_pack
        sys.path.append(here)

    def _load(name):
        spec = importlib.util.spec_from_file_location(
            name, os.path.join(here, "%s.py" % name))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod

    pipe = _load("tank_pipeline")
    atlas = _load("sprite_atlas")
    pcfg = dict(pipe.CONFIG, **(pipeline_cfg or {}))
    have = pipe.parts(pcfg)
    if have["layout"] != "parts":
        raise RuntimeError("the height map is a parts-scene layer")

    layers, body = pipe._body_layers(pcfg, have)
    out_dir = pcfg["output_dir"]

    def _shipped(name):
        path = os.path.join(out_dir, "%s_atlas.json" % name)
        with open(path, encoding="utf-8") as fh:
            meta = json.load(fh)
        return os.path.join(out_dir, meta["atlas"]), meta

    real = atlas.render_atlas
    missing = []

    def _stub(c):
        if c["name"] == cfg["name"]:
            return real(c)
        try:
            return _shipped(c["name"])
        except OSError:
            missing.append(c["name"])
            raise

    info = None
    try:
        info = build(layers, atlas, cfg)
        layers = list(layers) + [layer(info, cfg)]
        shared = {"output_dir": out_dir, "steps": pcfg["steps"],
                  "tile": pcfg["tile"], "azimuth": pcfg["azimuth"],
                  "elevation": pcfg["elevation"],
                  "hex_labels": {"front_dir": pcfg["front_dir"],
                                 "orientation": "flat"}}
        if body is not None:
            shared.update(body.shared())
        atlas.render_atlas = _stub
        res = atlas.render_set({"shared": shared, "layers": layers})
    finally:
        atlas.render_atlas = real
        if body is not None:
            body.restore()
        remove(cfg)

    res["height"] = report(res, cfg)
    res["height"]["twin"] = {"from": info["from"], "meshes": len(info["objects"])} \
        if info else None
    return res


# ---------------------------------------------------------------------------
# reading it back
# ---------------------------------------------------------------------------

def span_px(meta):
    """How many screen pixels of rise one whole unit of the byte is worth.

    The reader wants "is this surface under a plane that stands N px above the
    tank's feet", and this is the conversion: a world rise of dz shows as
    dz*cos(elevation) on screen, and a pixel is `units_per_pixel` of world.
    """
    lo, hi = meta["height_range"]
    elev = math.radians(meta["view"]["elevation"])
    return (hi - lo) * math.cos(elev) / meta["units_per_pixel"]


def report(res, cfg=None):
    """What the map came out as, in the units anybody would check it in."""
    cfg = dict(CONFIG, **(cfg or {}))
    import json
    layer_out = res["layers"][cfg["name"]]
    path = os.path.join(os.path.dirname(layer_out["path"]),
                        "%s_atlas.json" % cfg["name"])
    with open(path, encoding="utf-8") as fh:
        meta = json.load(fh)
    return {"span_px": round(span_px(meta), 2),
            "range": meta["height_range"],
            "px_per_code": round(span_px(meta) / 255.0, 4),
            "frames": meta["count"], "tile": meta["tile"]}
