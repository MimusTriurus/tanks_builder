"""Stylised broadleaf trees from a seed: a sprite pair and a game model each.

The picture this answers is `Images/Environment/Vegetation/Tree/Tree_1.png`: a
cartoon oak with a thick trunk on a flared root collar, a fork a little under
half-way up, and a dome of leaf puffs - each puff a rosette of pointed leaves
with a dark rim, lighter on top. Its burnt twin, `Tree_1_burnt.png`, is the
same trunk bare: charred wood, a fine net of twigs where the crown was, and a
few ember cracks.

**Puffs are placed first, branches are grown to them.** The obvious order -
grow a branching skeleton, then hang foliage on its tips - leaves the crown's
silhouette to chance, and the silhouette is the thing that has to read at 125
px on the board. So the crown is an ellipsoid envelope, the puffs are spread
over its shell by best-candidate sampling (even spacing, no clumping), and the
skeleton is routed to them: puffs grouped by azimuth become limbs, each limb
forks towards its puffs and ends inside one. Every branch therefore leads into
foliage and every puff has a branch - the two things a hand-drawn tree always
has and a random one usually does not.

**The burnt tree is the live one with its twigs showing.** Each branch end
inside a puff sprouts a few forked twigs that fill the puff's volume. They are
drawn from the same random stream whether or not they are built, so the live
tree and the burnt one share every vertex of trunk, limbs and roots - the foot
the board measures on each sprite is the same pixel, which `PropSet` requires
("a tree that steps sideways when it burns").

**Branch thickness is the pipe model**: a segment is as thick as the square
root of the share of puffs it feeds, so the trunk thins where a limb leaves it
and nothing needs a hand-set radius but the trunk's. **A child leaves its
junction at an angle and thinner than its parent** (`skeleton`): aimed straight
at its puffs, a limb ran metres along the trunk and the two read as one
trunk of two strands.

**Wood is one surface, a puff is one object.** Every skeleton chain becomes a
tube of our own, a side chain starting on its parent's centre line; roots are
chains that start inside the trunk. Trunk, limbs, branches and roots are then
fused by a voxel remesh into one closed surface and smoothed, so a junction
is a fillet and not two tubes through each other; twigs stay tubes
(`_wood_objects` says why Skin, the first wood, could do neither). Each puff
is an icosphere pushed out by noise; the geometry nodes group scatters leaves
over it and keeps the sphere as a dark core that fills the gaps between them.

Two details, two looks
----------------------
*Sprite* is what gets rendered: a 16-sided rimmed leaf, ~220k vertices, and
the light is painted by the materials themselves (a diffuse ramp cut into three
tones, emitted) so the render is the look, outline included (inverted hull).

*Game* is what gets exported, for `godot/scripts/Toon.cs` to dress: it ignores
vertex colour and has no alpha, so every tone a leaf has lives in a 8x64
palette texture - V is the leaf's tone (top of the puff lighter, random jitter,
puff depth), U crosses into the rim columns at the leaf's edge. No light is in
the palette (Toon brings its own three tones) and no ink is in the mesh (Toon
draws a shell). Leaves stay under 0.035 of the tree's diagonal, which is the
size Toon gives no shell - a leaf is a piece, and a shell per leaf would be
mud; the wood and each puff core do get one.

**The game model burns itself; there is no burnt model.** The sprite has to
swap one picture for another because it is a picture. The model does not: live
and burnt share every vertex of wood, so the one model carries its twigs from
the start, hidden in the puffs, and fire only takes away and darkens. TEXCOORD_1
X is each part's threshold on one `burn` fraction (`BURN`, scattered): a leaf
and a puff core are gone once burn passes theirs, a twig shows once it passes
its own, bark ignores it and chars with burn. The material says which reading
applies. `toon_preview(burn=...)` plays it in Blender.

Everything is jittered from `random.Random(seed)` and `mathutils.noise` with a
seeded offset, so the same seed is the same tree wherever it stands.

Entry points
------------
`build(cfg)`        - one tree under `<name>.World`; `state` live/burnt, `detail` sprite/game.
`make(seed)`        - both sprites and the burning .glb for one seed, `tree.json`, check sheets.
`grid(seeds)`       - live sprite trees side by side, for choosing seeds.
`rig()`, `preview()`- camera, sun and one render.
`reset()`           - forget materials, groups and leaves so the next build remakes them.
"""

import json
import math
import os
import random

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector, noise
from mathutils.bvhtree import BVHTree

REPO = (os.path.dirname(os.path.abspath(__file__))
        if "__file__" in globals() and os.path.isfile(__file__)
        else r"D:\Projects\Godot\tanks_builder\pipeline")

# ---------------------------------------------------------------------------
# configuration
# ---------------------------------------------------------------------------

# Metres. The authored size matches Tree_1: ~7 m tall, crown ~6.6 m wide
# including the leaves that stand out of the envelope.
CONFIG = {
    "seed": 1,
    "name": "Tree",
    "collection": "TreeGen",
    "offset": (0.0, 0.0, 0.0),
    "state": "live",         # live | burnt
    "detail": "sprite",      # sprite | game

    "height": 7.0,           # top of the crown envelope
    "crown_width": 6.0,      # envelope, X; leaves add ~0.6 to what you see
    "crown_depth": 5.6,      # envelope, Y
    "crown_base": 0.33,      # bottom of the envelope, fraction of height
    "fork": 0.40,            # where the leader leaves the trunk, fraction of height
    "lean": 0.03,            # trunk top offset, fraction of height

    "base_radius": 0.55,     # trunk at the collar
    "trunk_radius": 0.34,    # trunk where the first limb leaves
    "tip_radius": 0.045,     # branch end, inside its puff
    "twig_radius": 0.016,    # twig end
    "twig_spacing": 0.24,    # m between side twigs along a branch; x0.7 each level down
    "twig_angle": (35, 58),  # degrees off the axis they leave
    "twig_levels": 3,        # side twigs of side twigs, down to this level
    "limbs": (3, 5),         # main limbs, besides the leader
    "top": 0.62,             # puffs whose direction has z above this hang on the leader
    "roots": (5, 7),

    "clumps": (11, 14),      # puffs on the shell
    "clump_radius": (0.62, 0.92),
    "fill": 4,               # dark puffs inside the crown, no branch of their own
    "lumps": 0.30,           # noise push on a puff, fraction of its radius
    "squash": 0.85,          # puffs are a little wider than tall
}

# What `detail` changes.
#
# Wood is tubes fused by voxels in both (`_wood_objects`); `voxel` must stay
# under a branch tip's radius (0.045). The game's is then collapsed to
# `wood_tris`. Twigs are never collapsed: on a tube 0.03 m across Decimate
# folds the section to a line and the twig comes out in dashes. The game's are
# four-sided, a twig is 1-2 px on the board, and its burnt crown stops at the
# second twig level; the third was two thirds of the triangles.
DETAIL = {
    "sprite": {"leaf_size": 0.42, "density": 60.0, "leaf_sides": 16, "puff_subdiv": 3,
               "twig_sides": 6, "twig_step": 0.06, "ink": True, "voxel": 0.02, "wood_tris": 0,
               "twig_depth": 3, "min_up": -1.0, "puff_scale": 1.0},
    "game":   {"leaf_size": 0.30, "density": 18.0, "leaf_sides": 4, "puff_subdiv": 1,
               "twig_sides": 4, "twig_step": 0.25, "ink": False, "voxel": 0.03, "wood_tris": 3000,
               "twig_depth": 2, "min_up": -0.35, "puff_scale": 1.15},
}
# Game leaves are 0.30 m and at most 1.25 of that: a leaf's box diagonal is
# then under 0.04 of the tree's (~10.9 m), where Toon's ink weight is ~0.03.
# At 0.40 the big ones got half a shell each. What 0.30 leaves uncovered the
# core now carries: its colour is mid-green, the mass of the puff, and the
# leaves are the light on it. `min_up` skips faces whose normal points down
# more than that - under 30 degrees the board never sees a puff's underside.
# `puff_scale` stands in for the sprite's fringe: 0.42 m leaves tilted tip-out
# push a puff's outline ~15% past its core, and the game's short flat leaves
# do not, so without it the model's puffs came out smaller than the sprite's.

# px of the tree itself, foot to crown, not of the canvas: PropTier draws trees
# at 1/8 and was tuned on art 1009..1041 tall. Fitting the canvas to 1024
# instead left 978 of tree after margins and ink - 122 px on the board.
SPRITE_RISE = 1024
SPRITE_ELEVATION = 30.0     # the board's tilt, so sprite and model agree
OUT = os.path.join(REPO, "out", "trees")


# ---------------------------------------------------------------------------
# colour
# ---------------------------------------------------------------------------

# Sampled off Tree_1 / Tree_1_burnt and linearised; the view transform is Standard.
LEAF_PALETTE = [(0.00, (0.025, 0.042, 0.011)), (0.30, (0.048, 0.085, 0.019)),
                (0.55, (0.155, 0.205, 0.042)), (0.80, (0.33, 0.40, 0.08)),
                (1.00, (0.52, 0.56, 0.15))]
LEAF_RIM = (0.012, 0.017, 0.005)
CORE = (0.03, 0.05, 0.013)
BARK = [(0.30, (0.05, 0.022, 0.01)), (0.42, (0.11, 0.055, 0.022)),
        (0.55, (0.19, 0.09, 0.035)), (0.72, (0.30, 0.17, 0.075))]
CHAR = [(0.35, (0.010, 0.009, 0.008)), (0.50, (0.028, 0.024, 0.022)),
        (0.66, (0.060, 0.052, 0.047))]
EMBER = (0.55, 0.14, 0.025)
INK = (0.012, 0.008, 0.004)
# Charcoal in shadow (0.45 x 0.028) is darker than INK, so the brown ink drew
# a light halo round every twig; burnt wood gets near-black ink.
INK_CHAR = (0.002, 0.002, 0.002)
# Ink by part, not by state. Twigs get the thin shell - at 0.035 m it was 4.5
# px on a 2 px twig. The wood gets the same shell live and burnt, so the pair
# has one silhouette at the collar: with the burnt wood's ink thin as well,
# the edge of a root lay 3 px higher on the burnt sprite, on seed 2 a root tip
# sat on the top row of PropSet's foot band in one picture and not the other,
# and the two feet came out 50 px apart.
INK_WIDTH = {"wood": 0.035, "twig": 0.014}

# Game: flat albedo, lit by Toon. The palette is sampled from 0.30 up because
# Toon's shade tone (0.36) already takes the dark end.
GAME_BARK = (0.15, 0.075, 0.03)
GAME_CHAR = (0.032, 0.028, 0.026)
GAME_CORE = (0.12, 0.16, 0.034)
PALETTE_FROM = 0.30


def _srgb(c):
    c = np.clip(np.asarray(c, dtype=float), 0.0, 1.0)
    return np.where(c <= 0.0031308, 12.92 * c, 1.055 * c ** (1 / 2.4) - 0.055)


def _ease(stops, x):
    """Blender's EASE colour ramp: smoothstep between neighbouring stops."""
    if x <= stops[0][0]:
        return np.array(stops[0][1])
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        if x <= p1:
            t = (x - p0) / (p1 - p0)
            t = t * t * (3 - 2 * t)
            return np.array(c0) * (1 - t) + np.array(c1) * t
    return np.array(stops[-1][1])


# ---------------------------------------------------------------------------
# sprite materials: the toon ramp baked into emission, so the render is the look
# ---------------------------------------------------------------------------

def _new_mat(name):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    return m, nt.nodes, nt.links


def _ramp(nodes, stops, interp="LINEAR", loc=(0, 0)):
    r = nodes.new("ShaderNodeValToRGB")
    r.location = loc
    cr = r.color_ramp
    cr.interpolation = interp
    while len(cr.elements) > len(stops):
        cr.elements.remove(cr.elements[-1])
    while len(cr.elements) < len(stops):
        cr.elements.new(0.5)
    for el, (p, c) in zip(cr.elements, stops):
        el.position = p
        el.color = (*c, 1.0)
    return r


def _toon(nodes, links, loc=(0, 0)):
    """Diffuse light cut into three tones: 0.45 / 0.72 / 1.0."""
    d = nodes.new("ShaderNodeBsdfDiffuse")
    d.location = loc
    s = nodes.new("ShaderNodeShaderToRGB")
    s.location = (loc[0] + 180, loc[1])
    links.new(d.outputs[0], s.inputs[0])
    r = _ramp(nodes, [(0.0, (0.45,) * 3), (0.12, (0.72,) * 3), (0.45, (1.0,) * 3)],
              "CONSTANT", (loc[0] + 360, loc[1]))
    links.new(s.outputs["Color"], r.inputs[0])
    return r


def _math(nodes, op, loc, a=None, b=None):
    n = nodes.new("ShaderNodeMath")
    n.operation = op
    n.location = loc
    for i, v in enumerate((a, b)):
        if v is not None:
            n.inputs[i].default_value = v
    return n


def _attr(nodes, name, loc):
    a = nodes.new("ShaderNodeAttribute")
    a.attribute_name = name
    a.attribute_type = "GEOMETRY"
    a.location = loc
    return a


def _emit(nodes, links, colour, loc):
    e = nodes.new("ShaderNodeEmission")
    e.location = loc
    o = nodes.new("ShaderNodeOutputMaterial")
    o.location = (loc[0] + 200, loc[1])
    links.new(colour, e.inputs["Color"])
    links.new(e.outputs[0], o.inputs["Surface"])


def _multiply(nodes, links, a, b, loc):
    m = nodes.new("ShaderNodeMix")
    m.data_type = "RGBA"
    m.blend_type = "MULTIPLY"
    m.location = loc
    m.inputs["Factor"].default_value = 1.0
    links.new(a, m.inputs["A"])
    if isinstance(b, tuple):
        m.inputs["B"].default_value = (*b, 1.0)
    else:
        links.new(b, m.inputs["B"])
    return m.outputs["Result"]


def _grain(N, L, scale, noise_scale, detail, loc):
    """Object-space noise stretched along Z: bark runs up the trunk."""
    tc = N.new("ShaderNodeTexCoord")
    tc.location = (loc[0] - 400, loc[1])
    mp = N.new("ShaderNodeMapping")
    mp.location = (loc[0] - 200, loc[1])
    mp.inputs["Scale"].default_value = scale
    L.new(tc.outputs["Object"], mp.inputs["Vector"])
    nz = N.new("ShaderNodeTexNoise")
    nz.location = loc
    nz.inputs["Scale"].default_value = noise_scale
    nz.inputs["Detail"].default_value = detail
    nz.inputs["Roughness"].default_value = 0.6
    L.new(mp.outputs["Vector"], nz.inputs["Vector"])
    return nz.outputs["Fac"]


def _leaf_sprite_mat():
    # tone = 0.52 up + 0.16 random + 0.40 light + puff shade - 0.18
    m, N, L = _new_mat("TreeLeafMat")
    up = _attr(N, "leaf_up", (-1200, 300))
    rnd = _attr(N, "leaf_rand", (-1200, 100))
    sh = _attr(N, "clump_shade", (-1200, -100))
    edge = _attr(N, "leaf_edge", (-1200, -350))
    light = N.new("ShaderNodeRGBToBW")
    light.location = (-750, 550)
    L.new(_toon(N, L, (-1300, 500)).outputs["Color"], light.inputs[0])
    f1 = _math(N, "MULTIPLY", (-600, 400), b=0.52)
    L.new(up.outputs["Fac"], f1.inputs[0])
    f2 = _math(N, "MULTIPLY_ADD", (-450, 300), b=0.16)
    L.new(rnd.outputs["Fac"], f2.inputs[0])
    L.new(f1.outputs[0], f2.inputs[2])
    f3 = _math(N, "MULTIPLY_ADD", (-300, 300), b=0.40)
    L.new(light.outputs[0], f3.inputs[0])
    L.new(f2.outputs[0], f3.inputs[2])
    f4 = _math(N, "ADD", (-150, 300))
    L.new(f3.outputs[0], f4.inputs[0])
    L.new(sh.outputs["Fac"], f4.inputs[1])
    f5 = _math(N, "SUBTRACT", (0, 300), b=0.18)
    f5.use_clamp = True
    L.new(f4.outputs[0], f5.inputs[0])
    pal = _ramp(N, LEAF_PALETTE, "EASE", (150, 300))
    L.new(f5.outputs[0], pal.inputs[0])
    rim = _ramp(N, [(0.0, (0, 0, 0)), (0.55, (1, 1, 1))], "CONSTANT", (150, -300))
    L.new(edge.outputs["Fac"], rim.inputs[0])
    mix = N.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.location = (450, 200)
    L.new(rim.outputs["Color"], mix.inputs["Factor"])
    L.new(pal.outputs["Color"], mix.inputs["A"])
    mix.inputs["B"].default_value = (*LEAF_RIM, 1.0)
    _emit(N, L, mix.outputs["Result"], (650, 200))
    return m


def _core_sprite_mat():
    m, N, L = _new_mat("TreeFoliageCore")
    _emit(N, L, _multiply(N, L, _toon(N, L, (-700, 0)).outputs["Color"], CORE, (0, 0)), (200, 0))
    return m


def _bark_sprite_mat():
    m, N, L = _new_mat("TreeBarkMat")
    br = _ramp(N, BARK, "LINEAR", (-600, 0))
    L.new(_grain(N, L, (7, 7, 0.8), 2.5, 6, (-800, 0)), br.inputs[0])
    _emit(N, L, _multiply(N, L, _toon(N, L, (-900, 400)).outputs["Color"], br.outputs["Color"], (0, 100)),
          (200, 100))
    return m


def _char_sprite_mat():
    """Charred wood in three greys, and ember cracks: a thin iso-line of a
    smooth noise, kept only where a second, coarse noise is high - one noise
    alone draws its iso-line everywhere and the bark reads as rust. Emitted
    over the toon: embers are their own light."""
    m, N, L = _new_mat("TreeCharMat")
    cr = _ramp(N, CHAR, "LINEAR", (-600, 0))
    L.new(_grain(N, L, (6, 6, 0.7), 3.0, 5, (-800, 0)), cr.inputs[0])
    wood = _multiply(N, L, _toon(N, L, (-900, 400)).outputs["Color"], cr.outputs["Color"], (0, 100))
    crack = _ramp(N, [(0.0, (0, 0, 0)), (0.494, (1, 1, 1)), (0.506, (0, 0, 0))], "CONSTANT", (-600, -300))
    L.new(_grain(N, L, (5, 5, 0.9), 2.5, 0, (-800, -300)), crack.inputs[0])
    mask = _ramp(N, [(0.0, (0, 0, 0)), (0.62, (1, 1, 1))], "CONSTANT", (-600, -600))
    L.new(_grain(N, L, (2, 2, 0.6), 2.0, 0, (-800, -600)), mask.inputs[0])
    sparse = _multiply(N, L, crack.outputs["Color"], mask.outputs["Color"], (-400, -400))
    glow = _multiply(N, L, sparse, EMBER, (-300, -300))
    add = N.new("ShaderNodeMix")
    add.data_type = "RGBA"
    add.blend_type = "ADD"
    add.location = (150, 0)
    add.inputs["Factor"].default_value = 1.0
    L.new(wood, add.inputs["A"])
    L.new(glow, add.inputs["B"])
    _emit(N, L, add.outputs["Result"], (350, 0))
    return m


def _ink_mat(name="TreeOutline", colour=INK):
    m, N, L = _new_mat(name)
    e = N.new("ShaderNodeEmission")
    e.inputs["Color"].default_value = (*colour, 1.0)
    o = N.new("ShaderNodeOutputMaterial")
    L.new(e.outputs[0], o.inputs["Surface"])
    m.use_backface_culling = True
    return m


# ---------------------------------------------------------------------------
# game materials: flat Principled, the one texture is the leaf palette
# ---------------------------------------------------------------------------

def palette_image(name="TreeLeafPalette", w=8, h=64):
    """V = leaf tone, 0 dark .. 1 light; columns 6..7 are the rim (U >= 0.75)."""
    img = bpy.data.images.get(name)
    if img is None:
        img = bpy.data.images.new(name, w, h, alpha=False)
    px = np.ones((h, w, 4))
    for y in range(h):
        t = (y + 0.5) / h
        px[y, :, :3] = _srgb(_ease(LEAF_PALETTE, PALETTE_FROM + (1 - PALETTE_FROM) * t))
    px[:, 6:, :3] = _srgb(LEAF_RIM)
    img.pixels.foreach_set(px.astype(np.float32).ravel())
    os.makedirs(OUT, exist_ok=True)
    img.filepath_raw = os.path.join(OUT, name + ".png")
    img.file_format = "PNG"
    img.save()
    img.pack()
    return img


def _flat_mat(name, colour, image=None):
    m, N, L = _new_mat(name)
    b = N.new("ShaderNodeBsdfPrincipled")
    b.location = (0, 0)
    b.inputs["Base Color"].default_value = (*colour, 1.0)
    b.inputs["Roughness"].default_value = 1.0
    b.inputs["Metallic"].default_value = 0.0
    if "Specular IOR Level" in b.inputs:
        b.inputs["Specular IOR Level"].default_value = 0.0
    if image is not None:
        t = N.new("ShaderNodeTexImage")
        t.location = (-350, 0)
        t.image = image
        t.interpolation = "Closest"
        L.new(t.outputs["Color"], b.inputs["Base Color"])
    o = N.new("ShaderNodeOutputMaterial")
    o.location = (300, 0)
    L.new(b.outputs[0], o.inputs["Surface"])
    return m


# ---------------------------------------------------------------------------
# asset cache: made once, reused by every build
# ---------------------------------------------------------------------------

MAT_NAMES = {
    "sprite": {"leaf": "TreeLeafMat", "core": "TreeFoliageCore", "bark": "TreeBarkMat",
               "char": "TreeCharMat", "ink": "TreeOutline", "ink_char": "TreeOutlineChar"},
    "game": {"leaf": "TreeGame.Leaf", "core": "TreeGame.Core", "bark": "TreeGame.Bark",
             "char": "TreeGame.Char", "twig": "TreeGame.Twig"},
}
GROUPS = {"sprite": "TreeClump", "game": "TreeClumpGame"}
LEAVES = {"sprite": "TreeLeaf", "game": "TreeLeafGame"}


def materials(detail):
    names = MAT_NAMES[detail]
    if all(n in bpy.data.materials for n in names.values()):
        return {k: bpy.data.materials[n] for k, n in names.items()}
    if detail == "sprite":
        return {"leaf": _leaf_sprite_mat(), "core": _core_sprite_mat(), "bark": _bark_sprite_mat(),
                "char": _char_sprite_mat(), "ink": _ink_mat(),
                "ink_char": _ink_mat("TreeOutlineChar", INK_CHAR)}
    return {"leaf": _flat_mat(names["leaf"], (1, 1, 1), palette_image()),
            "core": _flat_mat(names["core"], GAME_CORE),
            "bark": _flat_mat(names["bark"], GAME_BARK),
            "char": _flat_mat(names["char"], GAME_CHAR),
            # the same bark, a material of its own because it is the one that
            # *appears* at its threshold where leaf and core disappear
            "twig": _flat_mat(names["twig"], GAME_BARK)}


def reset():
    """Drop cached materials, groups, leaves and the palette; the next build remakes them."""
    for d in MAT_NAMES.values():
        for n in d.values():
            if n in bpy.data.materials:
                bpy.data.materials.remove(bpy.data.materials[n])
    for n in GROUPS.values():
        if n in bpy.data.node_groups:
            bpy.data.node_groups.remove(bpy.data.node_groups[n])
    for n in LEAVES.values():
        if n in bpy.data.objects:
            bpy.data.objects.remove(bpy.data.objects[n], do_unlink=True)
        if n in bpy.data.meshes:
            bpy.data.meshes.remove(bpy.data.meshes[n])
    if "TreeLeafPalette" in bpy.data.images:
        bpy.data.images.remove(bpy.data.images["TreeLeafPalette"])


# ---------------------------------------------------------------------------
# the leaf and the scatter group
# ---------------------------------------------------------------------------

def leaf_mesh(name, sides=16, width=0.56, uv=False):
    """A pointed leaf in XY, base at the origin, tip at +X, length 1.

    Three rings - centre, inner, outer - and `leaf_edge` 0 on the first two, 1
    on the outer: a constant ramp at 0.55 turns the outer band into the dark
    rim, which is the leaf's outline without a second mesh. With `uv`, U runs
    0.35 -> 1.0 across the same band and the palette's rim columns start at
    0.75. Four sides make the game's rhombus.
    """
    half = sides // 2
    xs = [0.5 - 0.5 * math.cos(math.pi * k / half) for k in range(half + 1)]

    def h(x):
        return 0.5 * width * (math.sin(math.pi * x ** 0.8)) ** 0.9

    outer = [(x, h(x)) for x in xs] + [(xs[k], -h(xs[k])) for k in range(half - 1, 0, -1)]
    cx = 0.42
    inner = [(cx + (x - cx) * 0.86, y * 0.62) for x, y in outer]

    def bend(x, y):
        return Vector((x, y, 0.12 * x * x - 0.7 * y * y))

    verts = [bend(cx, 0)] + [bend(*p) for p in inner] + [bend(*p) for p in outer]
    faces = []
    for i in range(sides):
        j = (i + 1) % sides
        faces.append((0, 1 + i, 1 + j))
        faces.append((1 + i, 1 + sides + i, 1 + sides + j, 1 + j))
    me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
    me.clear_geometry()
    me.from_pydata(verts, [], faces)
    edge = [0.0] * (1 + sides) + [1.0] * sides
    a = me.attributes.get("leaf_edge") or me.attributes.new("leaf_edge", "FLOAT", "POINT")
    a.data.foreach_set("value", edge)
    if uv:
        lay = me.uv_layers.get("UVMap") or me.uv_layers.new(name="UVMap")
        vi = np.empty(len(me.loops), dtype=np.int32)
        me.loops.foreach_get("vertex_index", vi)
        u = 0.35 + 0.65 * np.array(edge)[vi]
        lay.data.foreach_set("uv", np.column_stack([u, np.full_like(u, 0.5)]).ravel())
    me.update()
    ob = bpy.data.objects.get(name) or bpy.data.objects.new(name, me)
    return ob


def _rand_float(N, L, seed_socket, lo, hi, off, loc):
    r = N.new("FunctionNodeRandomValue")
    r.data_type = "FLOAT"
    r.location = loc
    [i for i in r.inputs if i.name == "Min" and i.type == "VALUE"][0].default_value = lo
    [i for i in r.inputs if i.name == "Max" and i.type == "VALUE"][0].default_value = hi
    add = N.new("ShaderNodeMath")
    add.operation = "ADD"
    add.location = (loc[0] - 180, loc[1] - 80)
    L.new(seed_socket, add.inputs[0])
    add.inputs[1].default_value = off
    L.new(add.outputs[0], r.inputs["Seed"])
    return [o for o in r.outputs if o.type == "VALUE"][0]


def _named(N, name, dtype, loc):
    n = N.new("GeometryNodeInputNamedAttribute")
    n.data_type = dtype
    n.location = loc
    n.inputs["Name"].default_value = name
    return n.outputs["Attribute"]


def _tone_uv(N, L, geo, loc):
    """Game: bake the leaf's tone into V of `UVMap`, keep U (edge -> rim).
    tone = 0.62 up + 0.22 random + puff shade + 0.12, the sprite's formula
    without its light term - Toon lights the model."""
    x, y = loc
    up = _named(N, "leaf_up", "FLOAT", (x, y + 200))
    rnd = _named(N, "leaf_rand", "FLOAT", (x, y + 50))
    sh = _named(N, "clump_shade", "FLOAT", (x, y - 100))
    t1 = _math(N, "MULTIPLY_ADD", (x + 200, y + 150), b=0.62)
    L.new(up, t1.inputs[0])
    t1.inputs[2].default_value = 0.12
    t2 = _math(N, "MULTIPLY_ADD", (x + 350, y + 100), b=0.22)
    L.new(rnd, t2.inputs[0])
    L.new(t1.outputs[0], t2.inputs[2])
    t3 = _math(N, "ADD", (x + 500, y + 50))
    L.new(t2.outputs[0], t3.inputs[0])
    L.new(sh, t3.inputs[1])
    t3.use_clamp = True
    v = _math(N, "MULTIPLY_ADD", (x + 650, y + 50), b=0.92)
    L.new(t3.outputs[0], v.inputs[0])
    v.inputs[2].default_value = 0.04
    uvin = N.new("ShaderNodeSeparateXYZ")
    uvin.location = (x + 350, y - 250)
    L.new(_named(N, "UVMap", "FLOAT_VECTOR", (x, y - 250)), uvin.inputs[0])
    comb = N.new("ShaderNodeCombineXYZ")
    comb.location = (x + 800, y - 100)
    L.new(uvin.outputs["X"], comb.inputs["X"])
    L.new(v.outputs[0], comb.inputs["Y"])
    st = N.new("GeometryNodeStoreNamedAttribute")
    st.location = (x + 1000, y)
    st.data_type = "FLOAT2"
    st.domain = "CORNER"
    st.inputs["Name"].default_value = "UVMap"
    L.new(geo, st.inputs["Geometry"])
    L.new(comb.outputs[0], st.inputs["Value"])
    return st.outputs[0]


def _store_burn(N, L, geo, value, loc):
    comb = N.new("ShaderNodeCombineXYZ")
    comb.location = (loc[0] - 150, loc[1] - 100)
    L.new(value, comb.inputs["X"])
    st = N.new("GeometryNodeStoreNamedAttribute")
    st.location = loc
    st.data_type = "FLOAT2"
    st.domain = "CORNER"
    st.inputs["Name"].default_value = "Burn"
    L.new(geo, st.inputs["Geometry"])
    L.new(comb.outputs[0], st.inputs["Value"])
    return st.outputs[0]


def clump_group(detail, mats):
    """Leaves over a puff: Poisson points, leaf plane on the surface, spun at
    random round the normal and tilted tip-out 23..57 degrees - the tilt is
    what makes the puff's edge serrated rather than a smooth ball."""
    name = GROUPS[detail]
    ng = bpy.data.node_groups.get(name)
    if ng:
        return ng
    ng = bpy.data.node_groups.new(name, "GeometryNodeTree")
    ng.is_modifier = True
    I = ng.interface
    I.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
    I.new_socket("Leaf", in_out="INPUT", socket_type="NodeSocketObject")
    I.new_socket("Density", in_out="INPUT", socket_type="NodeSocketFloat").default_value = 60.0
    I.new_socket("Leaf Size", in_out="INPUT", socket_type="NodeSocketFloat").default_value = 0.42
    I.new_socket("Seed", in_out="INPUT", socket_type="NodeSocketInt").default_value = 0
    I.new_socket("Shade", in_out="INPUT", socket_type="NodeSocketFloat").default_value = 0.0
    I.new_socket("Min Up", in_out="INPUT", socket_type="NodeSocketFloat").default_value = -1.0
    I.new_socket("Core Burn", in_out="INPUT", socket_type="NodeSocketFloat").default_value = 0.9
    I.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
    N, L = ng.nodes, ng.links
    gi = N.new("NodeGroupInput")
    gi.location = (-1400, 0)
    go = N.new("NodeGroupOutput")
    go.location = (2200, 0)
    seed = gi.outputs["Seed"]

    dist = N.new("GeometryNodeDistributePointsOnFaces")
    dist.location = (-1100, 200)
    dist.distribute_method = "POISSON"
    dist.inputs["Distance Min"].default_value = 0.05
    L.new(gi.outputs["Geometry"], dist.inputs["Mesh"])
    fn = N.new("GeometryNodeInputNormal")
    fn.location = (-1600, 500)
    fz = N.new("ShaderNodeSeparateXYZ")
    fz.location = (-1450, 500)
    L.new(fn.outputs["Normal"], fz.inputs[0])
    cmp = N.new("FunctionNodeCompare")
    cmp.location = (-1300, 500)
    cmp.data_type = "FLOAT"
    cmp.operation = "GREATER_THAN"
    L.new(fz.outputs["Z"], cmp.inputs[0])
    L.new(gi.outputs["Min Up"], cmp.inputs[1])
    L.new(cmp.outputs[0], dist.inputs["Selection"])
    L.new(gi.outputs["Density"], dist.inputs["Density Max"])
    L.new(seed, dist.inputs["Seed"])

    align = N.new("FunctionNodeAlignRotationToVector")
    align.location = (-800, 0)
    align.axis = "Z"
    L.new(dist.outputs["Normal"], align.inputs["Vector"])
    spin = _rand_float(N, L, seed, 0.0, 2 * math.pi, 11, (-900, -250))
    tilt = _rand_float(N, L, seed, 0.40, 1.00, 23, (-900, -450))
    neg = _math(N, "MULTIPLY", (-700, -450), b=-1.0)
    L.new(tilt, neg.inputs[0])
    comb = N.new("ShaderNodeCombineXYZ")
    comb.location = (-550, -300)
    L.new(neg.outputs[0], comb.inputs["Y"])
    L.new(spin, comb.inputs["Z"])
    e2r = N.new("FunctionNodeEulerToRotation")
    e2r.location = (-400, -300)
    L.new(comb.outputs[0], e2r.inputs[0])
    rot = N.new("FunctionNodeRotateRotation")
    rot.location = (-250, -100)
    rot.rotation_space = "LOCAL"
    L.new(align.outputs[0], rot.inputs["Rotation"])
    L.new(e2r.outputs[0], rot.inputs["Rotate By"])

    sc = _rand_float(N, L, seed, 0.7, 1.25, 37, (-600, -650))
    scm = _math(N, "MULTIPLY", (-400, -650))
    L.new(sc, scm.inputs[0])
    L.new(gi.outputs["Leaf Size"], scm.inputs[1])

    sep = N.new("ShaderNodeSeparateXYZ")
    sep.location = (-800, 400)
    L.new(dist.outputs["Normal"], sep.inputs[0])
    upm = _math(N, "MULTIPLY_ADD", (-650, 400))
    L.new(sep.outputs["Z"], upm.inputs[0])
    upm.inputs[1].default_value = 0.5
    upm.inputs[2].default_value = 0.5
    rv = _rand_float(N, L, seed, 0.0, 1.0, 53, (-650, 150))
    # a leaf burns somewhere between `leaf_from` and its own puff's core
    lf = BURN["leaf_from"]
    span = _math(N, "SUBTRACT", (-800, -100), b=lf)
    L.new(gi.outputs["Core Burn"], span.inputs[0])
    bv = _math(N, "MULTIPLY_ADD", (-500, 0))
    L.new(_rand_float(N, L, seed, 0.0, 1.0, 71, (-650, 0)), bv.inputs[0])
    L.new(span.outputs[0], bv.inputs[1])
    bv.inputs[2].default_value = lf
    bv = bv.outputs[0]
    geo = dist.outputs["Points"]
    for i, (aname, value) in enumerate((("leaf_up", upm.outputs[0]), ("leaf_rand", rv),
                                        ("clump_shade", gi.outputs["Shade"]), ("leaf_burn", bv))):
        st = N.new("GeometryNodeStoreNamedAttribute")
        st.location = (-450 + 200 * i, 250)
        st.data_type = "FLOAT"
        st.domain = "POINT"
        st.inputs["Name"].default_value = aname
        L.new(geo, st.inputs["Geometry"])
        L.new(value, st.inputs["Value"])
        geo = st.outputs[0]

    oi = N.new("GeometryNodeObjectInfo")
    oi.location = (-250, -350)
    oi.transform_space = "ORIGINAL"
    L.new(gi.outputs["Leaf"], oi.inputs["Object"])
    iop = N.new("GeometryNodeInstanceOnPoints")
    iop.location = (150, 100)
    L.new(geo, iop.inputs["Points"])
    L.new(oi.outputs["Geometry"], iop.inputs["Instance"])
    L.new(rot.outputs[0], iop.inputs["Rotation"])
    L.new(scm.outputs[0], iop.inputs["Scale"])
    real = N.new("GeometryNodeRealizeInstances")
    real.location = (350, 100)
    L.new(iop.outputs[0], real.inputs[0])
    leaves = real.outputs[0]
    core = gi.outputs["Geometry"]
    if detail == "game":
        leaves = _tone_uv(N, L, leaves, (500, 400))
        # the burn contract: X of `Burn` is when this leaf, or this puff's
        # core, is gone (BURN); one value per leaf, so it cuts whole leaves
        leaves = _store_burn(N, L, leaves, _named(N, "leaf_burn", "FLOAT", (1300, 500)), (1450, 400))
        core = _store_burn(N, L, core, gi.outputs["Core Burn"], (1450, -250))
    sm1 = N.new("GeometryNodeSetMaterial")
    sm1.location = (1700, 100)
    sm1.inputs["Material"].default_value = mats["leaf"]
    L.new(leaves, sm1.inputs["Geometry"])
    sm2 = N.new("GeometryNodeSetMaterial")
    sm2.location = (1700, -150)
    sm2.inputs["Material"].default_value = mats["core"]
    L.new(core, sm2.inputs["Geometry"])
    join = N.new("GeometryNodeJoinGeometry")
    join.location = (1950, 0)
    L.new(sm1.outputs[0], join.inputs[0])
    L.new(sm2.outputs[0], join.inputs[0])
    L.new(join.outputs[0], go.inputs[0])
    return ng


# ---------------------------------------------------------------------------
# layout: envelope -> puffs -> skeleton
# ---------------------------------------------------------------------------

def _envelope(cfg):
    H = cfg["height"]
    z0 = cfg["crown_base"] * H
    b = (H - z0) / 2
    return Vector((0, 0, z0 + b)), (cfg["crown_width"] / 2, cfg["crown_depth"] / 2, b)


def _shell_distance(d, axes):
    a, ad, b = axes
    return 1.0 / math.sqrt((d.x / a) ** 2 + (d.y / ad) ** 2 + (d.z / b) ** 2)


def place_clumps(cfg, rng):
    """Puffs on the envelope's shell by best-candidate sampling.

    Each new puff takes, of 40 random candidates, the one farthest from those
    already placed (distance over the sum of radii), so the crown comes out
    evenly covered without a grid. Directions below z = -0.55 are not offered:
    the reference's lowest puffs hang at the sides, never under the fork.
    """
    c, axes = _envelope(cfg)
    lo, hi = cfg["clump_radius"]
    out = []
    for _ in range(rng.randint(*cfg["clumps"])):
        best, score = None, -1.0
        for _ in range(40):
            z = rng.uniform(-0.55, 1.0)
            phi = rng.uniform(0, 2 * math.pi)
            s = math.sqrt(1 - z * z)
            d = Vector((s * math.cos(phi), s * math.sin(phi), z))
            R = rng.uniform(lo, hi)
            p = c + d * max(_shell_distance(d, axes) - 0.9 * R, 0.3)
            sc = min(((p - q).length / (R + qr) for q, qr, _ in out), default=1e9)
            if sc > score:
                best, score = (p, R, d), sc
        out.append(best)
    a, ad, b = axes
    fills = []
    for _ in range(cfg["fill"]):
        p = c + Vector((rng.uniform(-0.35, 0.35) * a, rng.uniform(-0.1, 0.4) * ad,
                        rng.uniform(-0.2, 0.3) * b))
        fills.append((p, rng.uniform(1.0, 1.15) * hi, None))
    return c, axes, out, fills


def _group_by_azimuth(items, n):
    """Split puffs into n contiguous azimuth sectors, cutting at the widest gap first."""
    if not items:
        return []
    n = max(1, min(n, len(items)))
    items = sorted(items, key=lambda it: math.atan2(it[0].y, it[0].x))
    ang = [math.atan2(it[0].y, it[0].x) for it in items]
    gaps = [(ang[(i + 1) % len(ang)] - ang[i]) % (2 * math.pi) for i in range(len(ang))]
    start = (max(range(len(gaps)), key=lambda i: gaps[i]) + 1) % len(items)
    items = items[start:] + items[:start]
    size = len(items) / n
    return [items[int(round(k * size)):int(round((k + 1) * size))] for k in range(n)]


class _Skel:
    def __init__(self, rng):
        self.rng = rng
        self.co, self.r, self.twig, self.edges, self.roots = [], [], [], [], []
        self.parent = {}
        self.owner = {}   # twig vertex -> index of the puff it grows in

    def add(self, p, r, twig=0):
        self.co.append(Vector(p))
        self.r.append(r)
        self.twig.append(twig)
        return len(self.co) - 1

    def path(self, i0, p1, r0, r1, bend=0.08, droop=0.0, step=0.55, twig=0):
        """Polyline from vertex i0 to p1, bowed sideways and sagging; returns its end."""
        p0 = self.co[i0]
        v = p1 - p0
        L = v.length
        n = max(1, int(math.ceil(L / step)))
        d = v.normalized()
        perp = d.cross(Vector((0, 0, 1)))
        if perp.length < 1e-3:
            perp = Vector((1, 0, 0))
        perp.normalize()
        perp.rotate(Quaternion(d, self.rng.uniform(0, 2 * math.pi)))
        prev = i0
        for k in range(1, n + 1):
            t = k / n
            s = math.sin(math.pi * t)
            q = p0.lerp(p1, t) + perp * (bend * L * s) - Vector((0, 0, droop * L * s))
            j = self.add(q, r0 + (r1 - r0) * t, twig)
            self.edges.append((prev, j))
            self.parent[j] = prev
            prev = j
        return prev

    def heading(self, i):
        """The way the wood runs at vertex i, from its parent; up at the base."""
        p = self.parent.get(i)
        if p is None:
            return Vector((0, 0, 1))
        return (self.co[i] - self.co[p]).normalized()

    def depart(self, i0, d, r0, r1, reach=2.5, cap=None):
        """First piece of a child out of junction i0, straight along `d` and
        long enough - `reach` parent radii - to be clear of the parent before
        it turns towards anything."""
        L0 = max(reach * self.r[i0], 0.3)
        if cap is not None:
            L0 = min(L0, cap)
        return self.path(i0, self.co[i0] + d * L0, r0, r1, bend=0.0, step=1e9)

    def keep(self, twigs):
        """Vertices, radii, edges and roots, with twigs up to level `twigs` (0: none)."""
        idx = [i for i, t in enumerate(self.twig) if t <= twigs]
        new = {o: n for n, o in enumerate(idx)}
        return ([self.co[i] for i in idx], [self.r[i] for i in idx],
                [(new[a], new[b]) for a, b in self.edges if a in new and b in new],
                [new[i] for i in self.roots])

    def twig_part(self, levels, inside=None):
        """Only the twigs up to `levels`, each tree of them rooted on the branch
        end it grows from - that end vertex comes along, so the twig starts
        inside the fused wood instead of short of it. With `inside` (vertex ->
        bool) a twig is cut where it first leaves: the vertex and everything
        grown from it go, so no piece comes back in on its own."""
        tw = {i for i, t in enumerate(self.twig) if 0 < t <= levels}
        if inside is not None:
            gone = set()
            for i in sorted(tw):   # parents are made before their children
                p = self.parent.get(i)
                # a twig off the wood is tested at its base too: its first ring
                # sits on the branch, and a branch at the core's surface left
                # rings up to 1.4 cm out of both core and wood
                base_out = p is not None and self.twig[p] == 0 and not inside(i, at=p)
                if not inside(i) or p in gone or base_out:
                    gone.add(i)
            tw -= gone
        edges = [(a, b) for a, b in self.edges if (a in tw or b in tw) and
                 self.twig[a] <= levels and self.twig[b] <= levels]
        anchors = {v for e in edges for v in e if v not in tw}
        idx = sorted(tw | anchors)
        new = {o: n for n, o in enumerate(idx)}
        return ([self.co[i] for i in idx], [self.r[i] for i in idx],
                [(new[a], new[b]) for a, b in edges], [new[i] for i in sorted(anchors)],
                [self.owner.get(i, -1) for i in idx])


def _unit(rng):
    z = rng.uniform(-1, 1)
    phi = rng.uniform(0, 2 * math.pi)
    s = math.sqrt(1 - z * z)
    return Vector((s * math.cos(phi), s * math.sin(phi), z))


GOLDEN = math.radians(137.508)


def _along(S, axis, start, level, cfg, cap, out):
    """Side twigs along `axis` (vertex indices, base to tip), from arc length
    `start` on: one every `twig_spacing` (finer on finer twigs), each turned a
    golden angle round the axis from the last, `twig_angle` off it and leaning
    `out` - away from the crown's centre, so the twigs fill its dome. Each is
    up to `cap` long, shortening towards the axis tip to 0.45 of that, and
    forks the same way down to `twig_levels`. Every vertex carries its level,
    so a variant can stop at any depth without changing a single draw of the
    random stream.

    Length is a share of `cap`, not of the axis left beyond the twig: that
    way nearly every twig sat in the last half metre, where little axis is
    left, and came out a short thick thorn."""
    rng = S.rng
    pts = [S.co[i] for i in axis]
    arc = [0.0]
    for a, b in zip(pts, pts[1:]):
        arc.append(arc[-1] + (b - a).length)
    total = arc[-1]
    spacing = cfg["twig_spacing"] * (0.7 ** (level - 1))
    lo, hi = cfg["twig_angle"]
    r1 = cfg["twig_radius"]
    phase = rng.uniform(0, 2 * math.pi)
    s = start + spacing * rng.uniform(0.3, 1.0)
    used, k = set(), 0
    while s < total - 0.6 * spacing:
        j = min(range(len(axis)), key=lambda q: abs(arc[q] - s))
        s_next = s + spacing * rng.uniform(0.8, 1.25)
        ang = phase + k * GOLDEN
        tilt = math.radians(rng.uniform(lo, hi))
        left = (total - arc[j]) / max(total - start, 1e-6)       # 1 at the stretch's base, 0 at the tip
        L = cap * rng.uniform(0.7, 1.0) * (0.45 + 0.55 * left)
        k += 1
        s = s_next
        if j in used or j == len(axis) - 1 or L < 0.12:
            continue
        used.add(j)
        v = axis[j]
        t = (S.co[axis[min(j + 1, len(axis) - 1)]] - S.co[axis[max(j - 1, 0)]]).normalized()
        p1 = t.orthogonal().normalized()
        side = p1 * math.cos(ang) + t.cross(p1) * math.sin(ang)
        d = (t * math.cos(tilt) + side * math.sin(tilt) + out * 0.5
             + Vector((0, 0, 0.12))).normalized()
        d.y *= 0.6   # across the picture, as `out` (see `_crown_twigs`)
        d.normalize()
        n0 = len(S.co)
        S.path(v, S.co[v] + d * L, min(S.r[v] * 0.5, cfg["tip_radius"] * 0.7), r1,
               bend=0.12, droop=0.03, step=0.15, twig=level)
        if level < cfg["twig_levels"]:
            _along(S, [v] + list(range(n0, len(S.co))), 0.2 * L, level + 1, cfg, 0.6 * L, out)


def _crown_twigs(S, axis, cl, crown_centre, cfg):
    """The wood of a puff's branch, burnt bare: the branch goes on past the
    puff's centre, outward and a little up, thinning to a twig's end, and
    side twigs grow along its last stretch - from where it comes within 1.3
    radii of the puff to its tip.

    It replaced 4-5 twigs out of the branch end, which drew a broom at every
    tip: a thick branch stopping dead in the puff and a bunch spraying from
    one point. A real branch does not stop; it thins, and what it carries
    leaves it one twig at a time along its length."""
    rng = S.rng
    p, R, _ = cl
    end = axis[-1]
    # outward, but mostly across the picture: the board's camera looks from
    # -Y and the board only mirrors trees in X, so a twig aimed at the camera
    # is seen end-on - pointed straight out from the front puffs the twigs
    # came out as stubs in every render
    out = p - crown_centre
    out = Vector((out.x, out.y * 0.4, out.z)).normalized()
    d = (S.heading(end) + out * 0.6 + Vector((0, 0, 0.15))).normalized()
    n0 = len(S.co)
    S.path(end, S.co[end] + d * R * rng.uniform(1.0, 1.3), cfg["tip_radius"], cfg["twig_radius"],
           bend=0.10, step=0.18, twig=1)
    full = axis + list(range(n0, len(S.co)))
    start = 0.0
    for a, b in zip(full, full[1:]):
        if (S.co[a] - p).length < 1.3 * R:
            break
        start += (S.co[b] - S.co[a]).length
    _along(S, full, start, 1, cfg, 1.4 * R, out)


def _off(d, away, min_deg, hint=None):
    """`d` turned, if it must be, to at least `min_deg` off `away`, keeping its
    side of `away` - or `hint`'s when `d` has no side to speak of."""
    a = math.radians(min_deg)
    if d.angle(away) >= a:
        return d
    side = d - away * d.dot(away)
    if side.length < 0.25 and hint is not None:
        side = hint - away * hint.dot(away)
    if side.length < 1e-3:
        side = away.orthogonal()
    side.normalize()
    return (away * math.cos(a) + side * math.sin(a)).normalized()


def skeleton(cfg, rng, c, axes, clumps):
    """Trunk, limbs to puff groups, leader to the top puffs, twigs, roots.

    **Every child leaves its junction at an angle, and thinner than its parent.**
    Aimed straight at its target, a limb whose puffs spread round the trunk
    aims at their centroid, and that sits on the trunk's own axis: measured on
    seeds 1-4, the limb at the fork left 1-5 degrees off vertical and ran
    2.5-3.9 m alongside the leader, the low limbs 19-27 degrees and ~0.8 m
    along the trunk at 0.7 of its radius. Skin drew two tubes pressed together
    - a trunk of two strands, a step where one peeled off, a fold at the fork -
    and the ink shell of one showed through the other. So a trunk limb first
    goes out along its group's mean azimuth, 45-75 degrees off vertical, for
    2.5 trunk radii; any other child first turns at least 30 degrees off the
    way its parent runs; and a limb starts at no more than 0.6 of the trunk.
    """
    S = _Skel(rng)
    H = cfg["height"]
    Rt, tip = cfg["trunk_radius"], cfg["tip_radius"]
    n_all = max(1, len(clumps))
    ends = []
    up = Vector((0, 0, 1))

    def pipe(n):
        return max(tip * 1.6, Rt * math.sqrt(n / n_all))

    def branch(i0, target, r0, r1, bend=0.10, droop=0.04, min_deg=30.0, step=0.55):
        """A child out of junction i0: depart off the parent's line, then on to target."""
        v = target - S.co[i0]
        d = _off(v.normalized(), S.heading(i0), min_deg)
        j = S.depart(i0, d, r0, r0 * 0.92, reach=2.0, cap=0.4 * v.length)
        return S.path(j, target, S.r[j], r1, bend=bend, droop=droop, step=step)

    def to_puff(i0, cl, r0, droop):
        # vertices every 0.25 m, not 0.55: twigs leave the branch along it
        n0 = len(S.co)
        branch(i0, cl[0], r0, tip, droop=droop, step=0.25)
        ends.append(([i0] + list(range(n0, len(S.co))), cl))

    top = [cl for cl in clumps if cl[2].z > cfg["top"]]
    side = [cl for cl in clumps if cl[2].z <= cfg["top"]]
    groups = [g for g in _group_by_azimuth(side, rng.randint(*cfg["limbs"])) if g]
    groups.sort(key=lambda g: sum(cl[0].z for cl in g) / len(g))

    fork_z = cfg["fork"] * H
    lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * cfg["lean"] * H
    # limbs leave the trunk one above another, the lowest-hanging lowest, the
    # last at the fork beside the leader: no trunk vertex carries two limbs,
    # and 0.42 m apart - more than a trunk diameter - their junctions do not
    # run into each other (at 0.32 they did)
    attach = [fork_z - 0.42 * (len(groups) - 1 - k) for k in range(len(groups))]

    base = S.add((0, 0, 0.08), cfg["base_radius"])
    S.roots.append(base)
    r_here = cfg["base_radius"] * 0.78 + Rt * 0.22
    cur = S.path(base, Vector((0, 0, 0.75)) + lean * 0.1, cfg["base_radius"], r_here, bend=0.0)
    remaining = n_all

    def feed(i0, r0, group):
        """One limb from trunk vertex i0: out along the group's azimuth, then
        to its puffs, forking once more for four or more."""
        k = len(group)
        h = sum((Vector((cl[0].x, cl[0].y, 0)).normalized() for cl in group), Vector())
        if h.length < 0.2:   # puffs all round: go the way the first one hangs
            h = Vector((group[0][0].x, group[0][0].y, 0))
        if h.length < 1e-3:
            h = Vector((1, 0, 0))
        h.normalize()
        # as steep as its puffs lie from here, but never within 45 degrees of
        # vertical; low limbs go out near level (75), as Tree_1's do, and
        # come out of the trunk as a knot - at 50 a limb towards the camera
        # took 0.4 m to clear the bark and read as a ribbon wound round it
        cen = sum((cl[0] for cl in group), Vector()) / k
        aim = math.degrees((cen - S.co[i0]).angle(up)) + rng.uniform(-5, 5)
        tilt = math.radians(min(75.0, max(45.0, aim)))
        j = S.depart(i0, up * math.cos(tilt) + h * math.sin(tilt), r0, r0 * 0.9, reach=2.5)
        if k == 1:
            n0 = len(S.co)
            S.path(j, group[0][0], S.r[j], tip, bend=0.10, droop=0.04, step=0.25)
            ends.append(([j] + list(range(n0, len(S.co))), group[0]))
            return
        mid = S.co[j].lerp(cen, 0.5) + Vector((0, 0, 0.25))
        m = S.path(j, mid, S.r[j], min(S.r[j], pipe(k) * 0.85), bend=0.06, droop=0.02)
        subs = _group_by_azimuth(group, 2) if k >= 4 else [[cl] for cl in group]
        for sg in subs:
            if not sg:
                continue
            r_sub = min(pipe(len(sg)), 0.75 * S.r[m])
            if len(sg) == 1:
                to_puff(m, sg[0], r_sub, 0.05)
            else:
                scen = sum((cl[0] for cl in sg), Vector()) / len(sg)
                m2 = branch(m, S.co[m].lerp(scen, 0.45), r_sub, r_sub * 0.85, bend=0.06, droop=0.02)
                for cl in sg:
                    to_puff(m2, cl, min(pipe(1), 0.75 * S.r[m2]), 0.05)

    if not groups:
        attach = [fork_z]
    for k, z in enumerate(attach):
        p = Vector((0, 0, z)) + lean * (z / fork_z)
        cur = S.path(cur, p, r_here, pipe(remaining), bend=0.0)
        r_here = pipe(remaining)
        if k < len(groups):
            feed(cur, min(pipe(len(groups[k])), 0.6 * r_here), groups[k])
            remaining -= len(groups[k])
    head = Vector((lean.x * 0.5, lean.y * 0.5, c.z + 0.15 * axes[2]))
    lead = S.path(cur, head, pipe(max(remaining, 1)), pipe(max(len(top), 1)) * 0.8, bend=0.04)
    for cl in top:
        to_puff(lead, cl, min(pipe(1), 0.75 * S.r[lead]), 0.03)

    puff_of = {id(cl): k for k, cl in enumerate(clumps)}
    for axis, cl in ends:
        n0 = len(S.co)
        _crown_twigs(S, axis, cl, c, cfg)
        for v in range(n0, len(S.co)):
            S.owner[v] = puff_of[id(cl)]   # whose puff hides it: its burn follows that core

    # roots: separate chains from inside the trunk, so the collar flares
    # without a many-edged vertex.
    #
    # **Mirrored in X**, twin for twin, and one on the Y axis if the count is
    # odd. PropSet takes a tree's foot as the middle of the opaque x-range in
    # the bottom 3% of its rows, and 3% of the live tree and 3% of the burnt one
    # are not the same number of rows: their crowns end at different heights.
    # With roots at random azimuths some root's edge sat on the band's top row
    # in one picture and not the other (seed 2: the feet 50 px apart). A
    # mirrored pair enters the band together, so the middle stays on the
    # trunk's axis in both pictures whatever the band - which is also the 3D
    # model's origin in X.
    n = rng.randint(*cfg["roots"])
    half = n // 2
    rb = cfg["base_radius"] / 0.55
    spec = []
    for i in range(half):
        # the +X half-circle, from the front (-90 deg) round to the back (+90)
        ang = -math.pi / 2 + math.pi * (i + 0.5) / half + rng.uniform(-0.2, 0.2)
        Lr = rng.uniform(0.8, 1.05) * rb
        spec += [(ang, Lr), (math.pi - ang, Lr)]
    if n % 2:
        spec.append((math.pi / 2 if rng.random() < 0.5 else -math.pi / 2, rng.uniform(0.8, 1.05) * rb))
    for ang, Lr in spec:
        d = Vector((math.cos(ang), math.sin(ang), 0))
        pts = [d * 0.05 + Vector((0, 0, 0.85)), d * 0.32 * rb + Vector((0, 0, 0.35)),
               d * Lr * 0.62 + Vector((0, 0, 0.05)), d * Lr + Vector((0, 0, -0.05))]
        rad = [0.2, 0.24, 0.15, 0.07]
        prev = S.add(pts[0], rad[0] * rb)
        S.roots.append(prev)
        for q, r in zip(pts[1:], rad[1:]):
            j = S.add(q, r * rb)
            S.edges.append((prev, j))
            prev = j
    return S


# ---------------------------------------------------------------------------
# objects
# ---------------------------------------------------------------------------

def _collection(name):
    col = bpy.data.collections.get(name)
    if col is None:
        col = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(col)
    return col


def _wipe(name):
    for ob in [o for o in bpy.data.objects if o.name == name or o.name.startswith(f"{name}.")]:
        me = ob.data if ob.type == "MESH" else None
        bpy.data.objects.remove(ob, do_unlink=True)
        if me is not None and me.users == 0:
            bpy.data.meshes.remove(me)


def _chains(part):
    """The skeleton as polylines: from each root follow the thickest child,
    and every other child starts a chain of its own *at the junction vertex* -
    on the parent's centre line, inside it, so the two tubes overlap and no
    gap between them is possible."""
    co, rad, edges, roots = part[:4]
    adj = {i: [] for i in range(len(co))}
    for a, b in edges:
        adj[a].append(b)
        adj[b].append(a)
    seen = set(roots)
    stack = [(r, None) for r in reversed(roots)]
    chains = []
    while stack:
        start, pre = stack.pop()
        idx = ([pre] if pre is not None else []) + [start]
        cur = start
        while True:
            kids = [k for k in adj[cur] if k not in seen]
            if not kids:
                break
            seen.update(kids)
            kids.sort(key=lambda k: -rad[k])
            stack.extend((k, cur) for k in reversed(kids[1:]))
            cur = kids[0]
            idx.append(cur)
        if len(idx) < 2:
            continue
        pts = [Vector(co[i]) for i in idx]
        rs = [rad[i] for i in idx]
        if pre is not None:
            rs[0] = rs[1]          # a side chain starts at its own radius, not the parent's
        chains.append((pts, rs, idx))
    return chains


def _catmull(p0, p1, p2, p3, t):
    t2, t3 = t * t, t * t * t
    return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2
                  + (3 * p1 - p0 - 3 * p2 + p3) * t3)


def _resample(pts, rs, step):
    """Catmull-Rom through the polyline, a point every `step`: bends, not kinks."""
    n = len(pts)
    out_p, out_r = [pts[0]], [rs[0]]
    for i in range(n - 1):
        p1, p2 = pts[i], pts[i + 1]
        p0 = pts[i - 1] if i > 0 else 2 * p1 - p2
        p3 = pts[i + 2] if i + 2 < n else 2 * p2 - p1
        m = max(1, int(math.ceil((p2 - p1).length / step)))
        for k in range(1, m + 1):
            t = k / m
            out_p.append(_catmull(p0, p1, p2, p3, t))
            out_r.append(rs[i] + (rs[i + 1] - rs[i]) * t)
    return out_p, out_r


def tube_mesh(name, part, sides, step, scale=0.85, point=2.5, burn=None):
    """Every chain of `part` as a closed tube of `sides`, parallel-transport
    framed. The start is capped flat - it is always inside a parent or under
    the ground, and a cone there pushed a spike out below the root collar -
    and the end with a cone `point` radii long, so a twig runs out to a point
    instead of stopping like a sawn stub. `scale` keeps the look the Skin +
    Subsurf wood had: Subsurf shrank Skin's section to ~0.85 of its radius.
    With `burn` ((chain index, its part vertices) -> threshold) the tubes
    carry `UVMap` (zero) and `Burn` (X = the chain's threshold) - the game
    model's burn contract."""
    verts, faces, vchain, thresholds = [], [], [], []
    for ci, (pts, rs, idx) in enumerate(_chains(part)):
        thresholds.append(burn(ci, idx) if burn is not None else 0.0)
        pts, rs = _resample(pts, rs, step)
        if len(pts) < 2:
            continue
        tang = []
        for i in range(len(pts)):
            a, b = pts[max(i - 1, 0)], pts[min(i + 1, len(pts) - 1)]
            tang.append((b - a).normalized())
        nrm = tang[0].orthogonal().normalized()
        rings = []
        for p, r, t in zip(pts, rs, tang):
            nrm = (nrm - t * nrm.dot(t)).normalized()
            bi = t.cross(nrm)
            base = len(verts)
            for k in range(sides):
                a = 2 * math.pi * k / sides
                verts.append(p + (nrm * math.cos(a) + bi * math.sin(a)) * (r * scale))
            rings.append(base)
        for r0, r1 in zip(rings, rings[1:]):
            for k in range(sides):
                k1 = (k + 1) % sides
                faces.append((r0 + k, r0 + k1, r1 + k1, r1 + k))
        for ring, p, t, r, sgn, reach in ((rings[0], pts[0], tang[0], rs[0], -1, 0.0),
                                          (rings[-1], pts[-1], tang[-1], rs[-1], 1, point)):
            tip = len(verts)
            verts.append(p + t * (sgn * reach * r * scale))
            for k in range(sides):
                k1 = (k + 1) % sides
                faces.append((ring + k1, ring + k, tip) if sgn > 0 else (ring + k, ring + k1, tip))
        vchain.extend([ci] * (len(verts) - len(vchain)))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    for p in me.polygons:
        p.use_smooth = True
    if burn is not None:
        _uv_layers(me)
        vi = np.empty(len(me.loops), dtype=np.int32)
        me.loops.foreach_get("vertex_index", vi)
        per_chain = np.array(thresholds if thresholds else [0.0])
        x = per_chain[np.array(vchain, dtype=np.int32)[vi]]
        me.uv_layers["Burn"].data.foreach_set("uv", np.column_stack([x, np.zeros_like(x)]).ravel())
    return me


def _uv_layers(me):
    """`UVMap` then `Burn`, in that order, zero where missing: glTF writes them
    as TEXCOORD_0 and TEXCOORD_1 in the order the mesh holds them, and the
    merge in `bake` keeps the order of the first part. Zeroed explicitly: a new
    UV map comes filled with Blender's default unwrap, every face 0..1, and
    the bark went out with burn thresholds all over its range."""
    for n in ("UVMap", "Burn"):
        if n not in me.uv_layers:
            lay = me.uv_layers.new(name=n)
            lay.data.foreach_set("uv", np.zeros(len(me.loops) * 2, dtype=np.float32))
    me.uv_layers.active = me.uv_layers["UVMap"]


def _order_uvs(me):
    """`UVMap` first, `Burn` second, whatever order the merge left: the merged
    mesh takes the layer order of whichever part came first, and on one run
    of seed 1 that put `Burn` in TEXCOORD_0 and the leaf's rim U in
    TEXCOORD_1. Rebuilt from their data, then set active and render-active."""
    data = {}
    for n in ("UVMap", "Burn"):
        lay = me.uv_layers.get(n)
        uv = np.zeros(len(me.loops) * 2, dtype=np.float32)
        if lay is not None:
            lay.data.foreach_get("uv", uv)
        data[n] = uv
    for lay in list(me.uv_layers):
        me.uv_layers.remove(lay)
    for n in ("UVMap", "Burn"):
        me.uv_layers.new(name=n).data.foreach_set("uv", data[n])
    me.uv_layers.active = me.uv_layers["UVMap"]
    me.uv_layers["UVMap"].active_render = True


def _tube_object(oname, part, wood, sides, step, col, world, point=2.5, burn=None):
    ob = bpy.data.objects.new(oname, tube_mesh(oname, part, sides, step, point=point, burn=burn))
    col.objects.link(ob)
    ob.parent = world
    ob.data.materials.append(wood)
    return ob


def _add_ink(ob, ink, width):
    ob.data.materials.append(ink)
    so = ob.modifiers.new("Outline", "SOLIDIFY")
    so.thickness = width
    so.offset = 1.0
    so.use_flip_normals = True
    so.use_rim = False
    so.material_offset = 1


def _fit_tris(ob, target):
    """Collapse `ob` to about `target` triangles, measured on its own stack."""
    dm = ob.modifiers.new("Decimate", "DECIMATE")
    dm.decimate_type = "COLLAPSE"
    dm.ratio = 1.0
    bpy.context.view_layer.update()
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    me.calc_loop_triangles()
    n = len(me.loop_triangles)
    ev.to_mesh_clear()
    dm.ratio = min(1.0, target / max(n, 1))
    return n


def _wood_objects(name, S, wood, ink, cfg, col, world, twigs,
                  twig_mat=None, twig_burn=None, twig_inside=None):
    """The wood as one fused surface, and the twigs, if any, as a second object.

    **Tubes, not Skin.** Skin was the first wood, and it failed twice. Its
    tubes overlap without joining - each branch and each root chain started
    inside the trunk is its own closed surface, and an intersection curve is a
    seam without ink and a dark line with it, where one tube's shell pokes
    through the other. And at a vertex where several thin branches meet it
    fails to build the junction and leaves the tubes apart: on burnt seed 3,
    12 twig trees came out as 95 pieces with real gaps along a twig, hidden
    by Subsurf on the sprite and open on the game model. So every chain is a
    tube of our own (`tube_mesh`), a side chain starting on its parent's
    centre line, which cannot leave a gap; trunk, limbs, branches and roots
    then go through a voxel remesh - a union, one surface with no inner walls
    - and a smooth that turns the union's crease into a fillet. Twigs stay
    tubes: at 0.016 m they are under a voxel, and the overlap at a twig
    junction is a pixel on the board.
    """
    # a blunt end on the fused wood: a 2.5-radius point runs thinner than a
    # voxel, and Remesh cut its tip off as a 1 mm crumb of its own - at branch
    # ends in the puffs and root ends under the ground, 1-3 per tree
    ob = _tube_object(f"{name}.Trunk", S.keep(0), wood, 12, 0.15, col, world, point=0.6)
    rm = ob.modifiers.new("Fuse", "REMESH")
    rm.mode = "VOXEL"
    rm.voxel_size = cfg["voxel"]
    rm.adaptivity = 0.0
    rm.use_smooth_shade = True
    sm = ob.modifiers.new("Smooth", "SMOOTH")
    sm.factor = 0.5
    sm.iterations = 6
    if cfg["wood_tris"]:
        _fit_tris(ob, cfg["wood_tris"])
    if ink is not None:
        _add_ink(ob, ink, INK_WIDTH["wood"])
    out = [ob]
    if twigs:
        part = S.twig_part(twigs, twig_inside)
        owners = part[4]
        burn = None
        if twig_burn is not None:
            def burn(ci, idx):   # a chain is the puff's its last vertex grows in
                return twig_burn(owners[idx[-1]], ci)
        tw = _tube_object(f"{name}.Twigs", part, twig_mat or wood,
                          cfg["twig_sides"], cfg["twig_step"], col, world, burn=burn)
        if ink is not None:
            _add_ink(tw, ink, INK_WIDTH["twig"])
        out.append(tw)
    return out


def _lump(n, R, cfg, seed_vec):
    """A puff's radius along unit direction `n` (before the squash)."""
    # sampled on the unit sphere, so a coarse game puff and a fine sprite puff
    # of the same seed get the same lumps
    return R * (1 + cfg["lumps"] * noise.noise(n * 1.1 + seed_vec))


def _inside(bvh, q, slack=0.0):
    """Whether `q` lies inside the closed mesh of `bvh` with `slack` to spare.
    Against the mesh itself, not the noise it was built from: on a coarse
    icosphere the flat faces cut below the noise between vertices, and a test
    by the formula passed points that were up to 8 cm out of the real core."""
    loc, nrm, _, dist = bvh.find_nearest(q)
    return loc is not None and (q - loc).dot(nrm) < 0 and dist >= slack


def _puff_mesh(name, R, cfg, seed_vec):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=cfg["puff_subdiv"], radius=R)
    for v in bm.verts:
        n = v.co.normalized()
        v.co = n * _lump(n, R, cfg, seed_vec)
        v.co.z *= cfg["squash"]
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = True
    return me


# When each part of the game model burns, as a threshold on one `burn`
# fraction (0 green .. 1 done): a leaf is gone and a puff core is gone once
# burn passes theirs, a twig shows once it passes its own. Scattered, not
# ordered - no front, no bottom-up (asked for) - but **per puff**: the core's
# threshold is drawn, its leaves fall between 0.05 and it, and the twigs that
# grow in it show within 0.02 of it. So a twig is never seen through foliage:
# drawn on their own (0.30-0.75) twigs came out while the crown round them
# was still in leaf and stuck out of it. And the core outlives its leaves, so
# a puff is never a bare green ball nor leaves floating round nothing.
# Cores were 0.70-0.95 first, and the puffs' green mass held the crown whole
# to 0.5 and then went in the last quarter: the burn happened at the end.
BURN = {"leaf_from": 0.05, "core": (0.55, 0.90), "twig_about_core": 0.02}
_BURN_SALT = {"core": 100019, "twig": 100043}


def _burn_at(seed, part, i):
    """Threshold of puff `i`'s core, from a stream of its own."""
    lo, hi = BURN[part]
    return random.Random(seed * _BURN_SALT[part] + i).uniform(lo, hi)


def _twig_burn(seed, owner, ci):
    """A twig chain shows as the core of the puff it grows in goes."""
    j = BURN["twig_about_core"]
    base = _burn_at(seed, "core", owner) if owner >= 0 else BURN["core"][1]
    return base + random.Random(seed * _BURN_SALT["twig"] + ci).uniform(-j, j)


def tree_name(cfg):
    """`<name>`, `<name>_burnt`, `<name>_game`, `<name>_burnt_game`: no dot, so
    wiping one never takes another with it."""
    n = cfg["name"]
    if cfg["state"] == "burnt":
        n += "_burnt"
    if cfg["detail"] == "game":
        n += "_game"
    return n


def build(cfg=None):
    """One tree under `<name>.World` at `offset`. Returns its numbers."""
    cfg = {**CONFIG, **(cfg or {})}
    detail = cfg["detail"]
    cfg = {**DETAIL[detail], **cfg}
    burnt = cfg["state"] == "burnt"
    rng = random.Random(cfg["seed"])
    name = tree_name(cfg)
    col = _collection(cfg["collection"])
    _wipe(name)
    mats = materials(detail)
    ng = clump_group(detail, mats)
    leaf = bpy.data.objects.get(LEAVES[detail])
    if leaf is None:
        leaf = leaf_mesh(LEAVES[detail], sides=cfg["leaf_sides"],
                         width=0.56 if detail == "sprite" else 0.60, uv=detail == "game")
    if leaf.name not in col.objects:
        col.objects.link(leaf)
    leaf.hide_viewport = leaf.hide_render = True

    world = bpy.data.objects.new(f"{name}.World", None)
    col.objects.link(world)
    world.location = cfg["offset"]

    # everything below draws from rng in the same order in every variant; the
    # burn thresholds come from their own seeded streams (`_burn_at`), so the
    # shape of the tree does not depend on whether they were drawn
    c, axes, clumps, fills = place_clumps(cfg, rng)
    S = skeleton(cfg, rng, c, axes, clumps)
    ink = mats.get("ink_char" if burnt else "ink") if cfg["ink"] else None
    game = detail == "game"
    puffs = [(p, R, d, False) for p, R, d in clumps] + [(p, R, None, True) for p, R, _ in fills]
    seeds = [Vector((rng.uniform(0, 100), rng.uniform(0, 100), rng.uniform(0, 100))) for _ in puffs]
    # the cores are made before the wood, so a game twig can be cut by them
    cores = [] if burnt else [_puff_mesh(f"{name}.Clump.{i:02d}", R * cfg["puff_scale"], cfg, sv)
                              for i, ((p, R, d, fill), sv) in enumerate(zip(puffs, seeds))]
    bvh = {}

    def inside(v, at=None):
        """A game twig stays inside the core of the puff it grows in: the
        model carries its twigs hidden, and cut at the core no renderer can
        show them before fire takes the puff - uncut they stood out of the
        leaves in any view that does not play the burn contract. `at` tests
        another vertex (the twig's base on the branch) against v's puff."""
        k = S.owner.get(v, -1)
        if k < 0:
            return True
        if k not in bvh:
            me, p = cores[k], puffs[k][0]
            bvh[k] = BVHTree.FromPolygons([p + x.co for x in me.vertices],
                                          [tuple(f.vertices) for f in me.polygons])
        # the tube's wall and its end cone, not just its centre line: tested
        # on the line alone 8% of the twig's vertices came out 0.5-10 cm
        # beyond the core, the tips of the cones first
        return _inside(bvh[k], S.co[v if at is None else at], slack=S.r[v] * 0.85 * (1.0 + 2.5))

    # the game model is the one that burns (docs/trees.md, "Горение модели"):
    # it carries its twigs from the start, hidden in the puffs, shown by fire
    _wood_objects(name, S, mats["char" if burnt else "bark"], ink,
                  cfg, col, world, twigs=cfg["twig_depth"] if (burnt or game) else 0,
                  twig_mat=mats["twig"] if game else None,
                  twig_burn=(lambda owner, ci: _twig_burn(cfg["seed"], owner, ci)) if game else None,
                  twig_inside=inside if game else None)
    if not burnt:
        ids = {it.name: it.identifier for it in ng.interface.items_tree
               if getattr(it, "in_out", None) == "INPUT"}
        for i, ((p, R, d, fill), sv) in enumerate(zip(puffs, seeds)):
            pname = f"{name}.Clump.{i:02d}"
            ob = bpy.data.objects.new(pname, cores[i])
            col.objects.link(ob)
            ob.parent = world
            ob.location = p
            shade = -0.24 if fill else max(-0.12, min(0.08, 0.07 * -d.y + 0.03 * d.z - 0.02))
            g = ob.modifiers.new("Leaves", "NODES")
            g.node_group = ng
            inp = g.properties.inputs
            getattr(inp, ids["Leaf"]).value = leaf
            getattr(inp, ids["Density"]).value = cfg["density"]
            getattr(inp, ids["Leaf Size"]).value = cfg["leaf_size"]
            getattr(inp, ids["Seed"]).value = cfg["seed"] * 101 + i
            getattr(inp, ids["Shade"]).value = shade
            if "Min Up" in ids:   # groups cached from before the input existed lack it
                getattr(inp, ids["Min Up"]).value = cfg["min_up"]
            if "Core Burn" in ids:
                getattr(inp, ids["Core Burn"]).value = _burn_at(cfg["seed"], "core", i)

    bpy.context.view_layer.update()
    return report(name)


def _parts(name):
    w = bpy.data.objects.get(f"{name}.World")
    return [o for o in (w.children_recursive if w else []) if o.type == "MESH"]


def _world_points(name):
    """Evaluated vertices of every mesh under `<name>.World`, world space, (n, 3)."""
    dg = bpy.context.evaluated_depsgraph_get()
    out = []
    for ob in _parts(name):
        ev = ob.evaluated_get(dg)
        me = ev.to_mesh()
        n = len(me.vertices)
        if n:
            co = np.empty(n * 3)
            me.vertices.foreach_get("co", co)
            mw = np.array(ev.matrix_world)
            out.append(co.reshape(-1, 3) @ mw[:3, :3].T + mw[:3, 3])
        ev.to_mesh_clear()
    return np.concatenate(out) if out else np.zeros((0, 3))


def report(name):
    """Evaluated vertex and triangle counts and world extent of `name`, off the meshes."""
    dg = bpy.context.evaluated_depsgraph_get()
    verts = tris = 0
    for ob in _parts(name):
        ev = ob.evaluated_get(dg)
        me = ev.to_mesh()
        verts += len(me.vertices)
        me.calc_loop_triangles()
        tris += len(me.loop_triangles)
        ev.to_mesh_clear()
    pts = _world_points(name)
    lo, hi = pts.min(0), pts.max(0)
    z0 = bpy.data.objects[f"{name}.World"].location.z
    return {"name": name, "puffs": sum(".Clump." in o.name for o in _parts(name)),
            "verts": verts, "tris": tris, "height": round(float(hi[2]) - z0, 2),
            "width": round(float(hi[0] - lo[0]), 2), "depth": round(float(hi[1] - lo[1]), 2)}


# ---------------------------------------------------------------------------
# looking at it
# ---------------------------------------------------------------------------

def _aim(cam, elevation, azimuth):
    e, a = math.radians(elevation), math.radians(azimuth)
    back = Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))
    cam.rotation_euler = (-back).to_track_quat("-Z", "Y").to_euler()
    return back


def rig(elevation=SPRITE_ELEVATION, azimuth=0.0, target=(0, 0, 3.4), ortho=8.0, distance=40.0):
    """Sun from upper left-front and an ortho camera; Standard view, no world light."""
    sc = bpy.context.scene
    col = _collection(CONFIG["collection"])
    sun = bpy.data.objects.get("TreeGen.Sun")
    if sun is None:
        sun = bpy.data.objects.new("TreeGen.Sun", bpy.data.lights.new("TreeGen.Sun", "SUN"))
        col.objects.link(sun)
    sun.data.energy = math.pi
    sun.data.angle = math.radians(2)
    sun.rotation_euler = Vector((0.9, 0.9, -1.4)).normalized().to_track_quat("-Z", "Y").to_euler()
    cam = bpy.data.objects.get("TreeGen.Camera")
    if cam is None:
        cam = bpy.data.objects.new("TreeGen.Camera", bpy.data.cameras.new("TreeGen.Camera"))
        col.objects.link(cam)
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = ortho
    cam.data.clip_end = distance * 3
    cam.data.shift_x = cam.data.shift_y = 0.0
    back = _aim(cam, elevation, azimuth)
    cam.location = Vector(target) + back * distance
    sc.camera = cam
    sc.render.engine = "BLENDER_EEVEE"
    sc.render.film_transparent = True
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    if sc.world and sc.world.use_nodes and "Background" in sc.world.node_tree.nodes:
        sc.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.0
    return cam


def grid(seeds, spacing=8.0, cfg=None):
    """Live sprite trees `Tree.<seed>` in a row along X, for choosing seeds."""
    return [build({**(cfg or {}), "seed": s, "name": f"Tree.{s}", "offset": (i * spacing, 0, 0)})
            for i, s in enumerate(seeds)]


def preview(path, size=(948, 1010)):
    sc = bpy.context.scene
    sc.render.resolution_x, sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    return path


class _Solo:
    """Render only the named trees (plus sun and camera); restores on exit."""

    def __init__(self, names):
        keep = set()
        for n in names:
            w = bpy.data.objects[f"{n}.World"]
            keep |= {w.name} | {o.name for o in w.children_recursive}
        keep |= {"TreeGen.Sun", "TreeGen.Camera"}
        self.keep = keep

    def __enter__(self):
        self.saved = {o.name: o.hide_render for o in bpy.data.objects}
        for o in bpy.data.objects:
            if o.name not in self.keep and o.type in {"MESH", "LIGHT", "EMPTY", "CURVE"}:
                o.hide_render = True
        for n in self.keep:
            o = bpy.data.objects[n]
            if not o.name.startswith(tuple(LEAVES.values())):
                o.hide_render = False
        return self

    def __exit__(self, *exc):
        for n, h in self.saved.items():
            if n in bpy.data.objects:
                bpy.data.objects[n].hide_render = h


def sprites(names, paths, rise=SPRITE_RISE, elevation=SPRITE_ELEVATION, margin=8):
    """One render per tree, all through one frame fitted to their union.

    The union is what makes the pair a pair: the burnt twin comes out on the
    same canvas with its foot on the same pixel as the live tree's, whatever
    its own extent. Scale is set so the union's vertices span `rise` px; the
    live tree is the taller of the two, so that is its height on the canvas.
    """
    cam = rig(elevation=elevation)
    rot = cam.rotation_euler.to_matrix()
    right, up, back = rot.col[0], rot.col[1], rot.col[2]
    # each tree about its own foot: the pair shares a frame without sharing a
    # place - stood in one, the burnt twin's twigs showed through the live
    # crown in the viewport
    foot = {n: bpy.data.objects[f"{n}.World"].matrix_world.translation.copy() for n in names}
    pts = np.concatenate([_world_points(n) - np.array(foot[n]) for n in names])
    u = pts @ np.array(right)
    v = pts @ np.array(up)
    ppm = rise / (v.max() - v.min())
    pad = 0.12   # the ink shell and leaf tips the vertex sample can miss
    u0, u1, v0, v1 = u.min() - pad, u.max() + pad, v.min() - pad, v.max() + pad
    w_px = int(math.ceil((u1 - u0) * ppm)) + 2 * margin
    height = int(math.ceil((v1 - v0) * ppm)) + 2 * margin
    sc = bpy.context.scene
    sc.render.resolution_x, sc.render.resolution_y = w_px, height
    sc.render.resolution_percentage = 100
    cam.data.ortho_scale = max(w_px, height) / ppm
    cam.data.clip_end = 200.0
    out = []
    for n, p in zip(names, paths):
        cam.location = foot[n] + right * ((u0 + u1) / 2) + up * ((v0 + v1) / 2) + back * 60.0
        with _Solo([n]):
            sc.render.filepath = p
            bpy.ops.render.render(write_still=True)
        out.append(p)
    # where the trunk's centre on the ground lands, top-down px: under 30
    # degrees the lowest opaque row - the foot PropSet measures - is the front
    # edge of the root collar, R sin(30) below the centre, and a 3D model's
    # origin is the centre (the foot itself, 0 in these coordinates)
    centre = [round(margin + (0.0 - u0) * ppm, 1), round(margin + (v1 - 0.0) * ppm, 1)]
    return {"size": [w_px, height], "px_per_m": round(ppm, 1), "centre": centre, "files": out}


def _load_rgba(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)   # rows bottom-up, as Blender keeps them


def foot_of(path, alpha=0.03, band=0.03):
    """The foot as `PropSet.FootOf` measures it: the middle of the opaque
    x-range in the bottom 3% of the used rect, on its bottom row; plus the
    rise (foot to top) and the half-width there (`Root`). Top-down pixels."""
    a = _load_rgba(path)[::-1][..., 3] > alpha
    rows = np.where(a.any(1))[0]
    top, bot = int(rows[0]), int(rows[-1])
    k = max(1, int(round((bot - top + 1) * band)))
    xs = np.where(a[bot - k + 1:bot + 1].any(0))[0]
    return {"foot": [float(xs[0] + xs[-1]) / 2, bot], "rise": bot - top + 1,
            "root": float(xs[-1] - xs[0]) / 2}


def _shrink(px, k):
    """Box filter by k on premultiplied colour: the board's 1/8 draw, roughly."""
    h, w = (px.shape[0] // k) * k, (px.shape[1] // k) * k
    p = px[:h, :w].copy()
    p[..., :3] *= p[..., 3:4]
    p = p.reshape(h // k, k, w // k, k, 4).mean(axis=(1, 3))
    a = p[..., 3:4]
    p[..., :3] = np.where(a > 0, p[..., :3] / np.maximum(a, 1e-6), 0)
    return p


def check_sheet(paths, out, ground=(0.72, 0.70, 0.62), board=8, zoom=4):
    """The sprites side by side on a pale ground, then each drawn at the
    board's 1/`board` and blown up `zoom` times without smoothing - what 125
    px actually leaves of the tree is the thing to judge, not the 1024 px render."""
    ims = [_load_rgba(p) for p in paths]
    small = [np.repeat(np.repeat(_shrink(im, board), zoom, 0), zoom, 1) for im in ims]
    tiles = ims + small
    gap = 24
    H = max(t.shape[0] for t in tiles)
    W = sum(t.shape[1] for t in tiles) + gap * (len(tiles) + 1)
    sheet = np.ones((H + 2 * gap, W, 4), dtype=np.float32)
    sheet[..., :3] = _srgb(ground)
    x = gap
    for t in tiles:
        h, w = t.shape[:2]
        a = t[..., 3:4]
        region = sheet[gap:gap + h, x:x + w, :3]
        sheet[gap:gap + h, x:x + w, :3] = t[..., :3] * a + region * (1 - a)
        x += w + gap
    img = bpy.data.images.new("TreeCheck", sheet.shape[1], sheet.shape[0], alpha=False)
    img.pixels.foreach_set(sheet.ravel())
    img.filepath_raw = out
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    return out


def _mixc(N, L, a, b, fac, loc):
    """Colour mix; `a`, `b` a socket or an RGB tuple, `fac` a socket or a float."""
    m = N.new("ShaderNodeMix")
    m.data_type = "RGBA"
    m.location = loc
    for sock, v in (("A", a), ("B", b)):
        if isinstance(v, tuple):
            m.inputs[sock].default_value = (*v, 1.0)
        else:
            L.new(v, m.inputs[sock])
    if isinstance(fac, float):
        m.inputs["Factor"].default_value = fac
    else:
        L.new(fac, m.inputs["Factor"])
    return m.outputs["Result"]


# How the preview draws a part in its last stretch before its threshold: the
# window over which it goes ember then char. A leaf is a flicker; a core is
# the mass of the puff and chars slowly under its leaves - and only chars:
# with the leaf's ember on it every puff went a lit orange ball at 0.5-0.75,
# which read as an autumn tree or a row of fireballs, not as foliage burning.
BURN_WINDOW = {"leaf": 0.12, "core": 0.45}
BURN_EMBER = {"leaf": True, "core": False}


class toon_preview:
    """Game materials lit in three tones for a check render, restored on exit.

    An approximation of `Toon.cs`, not a copy: its shade tone is cold grey and
    its thresholds are cosines to the board's sun, here it is the sprite's own
    ramp. What it answers is whether the model's masses and tones land where
    the sprite's do - the PBR render with no world light answers nothing, its
    shadow side is black. The verdict is the board's.

    With `burn` it also plays the burn contract at that fraction, as a shader
    on the board would: bark and twigs go to char with `burn`; a leaf or a
    core goes ember then char over its `BURN_WINDOW` and is gone once `burn`
    passes the X of its `Burn` UV; a twig is there only once `burn` passes its.
    """

    def __init__(self, burn=None):
        self.burn = burn

    def _burn(self, N, L, role, colour):
        B = float(self.burn)
        if role in ("bark", "char"):
            return _mixc(N, L, colour, GAME_CHAR, B, (-500, 900)), None
        uv = N.new("ShaderNodeUVMap")
        uv.uv_map = "Burn"
        uv.location = (-1300, 1100)
        sep = N.new("ShaderNodeSeparateXYZ")
        sep.location = (-1100, 1100)
        L.new(uv.outputs["UV"], sep.inputs[0])
        t = sep.outputs["X"]
        if role == "twig":
            vis = _math(N, "GREATER_THAN", (-900, 1100), a=B)
            L.new(t, vis.inputs[1])
            return _mixc(N, L, colour, GAME_CHAR, B, (-500, 900)), vis.outputs[0]
        w = BURN_WINDOW[role]
        d = _math(N, "SUBTRACT", (-900, 1000), a=B)          # B - t
        L.new(t, d.inputs[1])
        g = _math(N, "MULTIPLY_ADD", (-750, 1000), b=1.0 / w)  # (B - t) / w + 1
        g.inputs[2].default_value = 1.0
        g.use_clamp = True
        L.new(d.outputs[0], g.inputs[0])
        e1 = _math(N, "MULTIPLY", (-600, 1050), b=2.0)
        e1.use_clamp = True
        L.new(g.outputs[0], e1.inputs[0])
        e2 = _math(N, "MULTIPLY_ADD", (-600, 950), b=2.0)
        e2.inputs[2].default_value = -1.0
        e2.use_clamp = True
        L.new(g.outputs[0], e2.inputs[0])
        if BURN_EMBER[role]:
            c = _mixc(N, L, colour, EMBER, e1.outputs[0], (-450, 1000))
            c = _mixc(N, L, c, GAME_CHAR, e2.outputs[0], (-300, 1000))
        else:
            c = _mixc(N, L, colour, GAME_CHAR, g.outputs[0], (-300, 1000))
        vis = _math(N, "LESS_THAN", (-900, 1200), a=B)
        L.new(t, vis.inputs[1])
        return c, vis.outputs[0]

    def __enter__(self):
        self.added = []
        for role, n in MAT_NAMES["game"].items():
            m = bpy.data.materials.get(n)
            if m is None:
                continue
            N, L = m.node_tree.nodes, m.node_tree.links
            b = next(x for x in N if x.bl_idname == "ShaderNodeBsdfPrincipled")
            out = next(x for x in N if x.bl_idname == "ShaderNodeOutputMaterial")
            src = b.inputs["Base Color"].links[0].from_socket if b.inputs["Base Color"].links \
                else tuple(b.inputs["Base Color"].default_value[:3])
            before = set(N)
            vis = None
            if self.burn is not None:
                src, vis = self._burn(N, L, role, src)
            _emit_link = _multiply(N, L, _toon(N, L, (-900, 600)).outputs["Color"], src, (-300, 600))
            e = N.new("ShaderNodeEmission")
            L.new(_emit_link, e.inputs["Color"])
            shader = e.outputs[0]
            if vis is not None:
                tr = N.new("ShaderNodeBsdfTransparent")
                mx = N.new("ShaderNodeMixShader")
                L.new(vis, mx.inputs["Fac"])
                L.new(tr.outputs[0], mx.inputs[1])
                L.new(e.outputs[0], mx.inputs[2])
                shader = mx.outputs[0]
            L.new(shader, out.inputs["Surface"])
            self.added.append((m, b, out, [x for x in N if x not in before]))
        return self

    def __exit__(self, *exc):
        for m, b, out, nodes in self.added:
            for x in nodes:
                m.node_tree.nodes.remove(x)
            m.node_tree.links.new(b.outputs[0], out.inputs["Surface"])


def _mesh_shots(name, burns, folder):
    """The exported mesh `<name>.Mesh` through the sprite's frame under
    `toon_preview`, one render per burn fraction (None: not burning)."""
    mesh = bpy.data.objects[f"{name}.Mesh"]
    w = bpy.data.objects[f"{name}.World"]
    tmp = bpy.data.objects.new(f"{name}.Check.World", None)
    w.users_collection[0].objects.link(tmp)
    tmp.location = w.location
    keep = mesh.matrix_world.copy()
    mesh.parent = tmp
    mesh.matrix_world = keep
    mesh.hide_render = False
    shots = []
    try:
        for b in burns:
            shot = os.path.join(folder, f"_{name}" + ("" if b is None else f"_burn{int(b * 100):03d}") + ".png")
            with toon_preview(burn=b):
                sprites([f"{name}.Check"], [shot])
            shots.append(shot)
    finally:
        mesh.parent = None
        mesh.matrix_world = keep
        mesh.hide_render = True
        bpy.data.objects.remove(tmp, do_unlink=True)
    return shots


def game_check(name, path, sprite_png):
    """The exported mesh beside the sprite it has to agree with."""
    return check_sheet([sprite_png] + _mesh_shots(name, [None], os.path.dirname(path)), path)


def burn_check(name, path, live_png, burnt_png, steps=(0.0, 0.25, 0.5, 0.75, 1.0)):
    """The model burning, between the two sprites it runs from and to."""
    shots = _mesh_shots(name, steps, os.path.dirname(path))
    return check_sheet([live_png] + shots + [burnt_png], path)


def glb_burn(path):
    """Read the written file back: per primitive, its material, whether it
    carries TEXCOORD_1, and the range of X in it - the burn contract as the
    board will receive it, not as the scene holds it."""
    import struct
    with open(path, "rb") as f:
        data = f.read()
    ln = struct.unpack_from("<I", data, 12)[0]
    js = json.loads(data[20:20 + ln].decode("utf-8"))
    off = 20 + ln
    bin_len = struct.unpack_from("<I", data, off)[0]
    blob = data[off + 8:off + 8 + bin_len]
    out = []
    for mesh in js["meshes"]:
        for p in mesh["primitives"]:
            row = {"material": js["materials"][p["material"]]["name"] if "material" in p else None,
                   "attributes": sorted(p["attributes"])}
            for k, key in (("TEXCOORD_0", "uv0_x"), ("TEXCOORD_1", "burn_x")):
                if k not in p["attributes"]:
                    continue
                acc = js["accessors"][p["attributes"][k]]
                bv = js["bufferViews"][acc["bufferView"]]
                start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
                stride = bv.get("byteStride", 8)
                uv = np.array([struct.unpack_from("<2f", blob, start + i * stride)
                               for i in range(acc["count"])])
                row[key] = [round(float(uv[:, 0].min()), 3), round(float(uv[:, 0].max()), 3)]
            out.append(row)
    return out


# ---------------------------------------------------------------------------
# the game model
# ---------------------------------------------------------------------------

def bake(name):
    """Every mesh under `<name>.World`, evaluated, as one mesh object `<name>.Mesh`
    in the World's frame: one mesh is one ink scale for Toon and one draw per
    material on the board."""
    world = bpy.data.objects[f"{name}.World"]
    dg = bpy.context.evaluated_depsgraph_get()
    inv = world.matrix_world.inverted()
    mats, bm = [], bmesh.new()
    for ob in _parts(name):
        ev = ob.evaluated_get(dg)
        me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=dg)
        # Decimate can leave degenerate geometry in the fused wood (seed 4):
        # the glTF exporter warned the mesh "may be exported wrongly" and fixed
        # it in place; clean it here, where it is ours to see
        me.validate(clean_customdata=False)
        _uv_layers(me)   # the fused wood lost its UVs to Remesh: zero, never read
        me.transform(inv @ ev.matrix_world)
        remap = []
        for m in me.materials:
            if m not in mats:
                mats.append(m)
            remap.append(mats.index(m))
        mi = np.zeros(len(me.polygons), dtype=np.int32)
        me.polygons.foreach_get("material_index", mi)
        me.polygons.foreach_set("material_index", np.array(remap, dtype=np.int32)[mi])
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)
    out = bpy.data.meshes.new(f"{name}.Mesh")
    bm.to_mesh(out)
    bm.free()
    _order_uvs(out)
    for m in mats:
        out.materials.append(m)
    if out.validate(clean_customdata=False):
        raise RuntimeError(f"{name}: baked mesh needed repair after its parts were clean")
    ob = bpy.data.objects.new(f"{name}.Mesh", out)
    world.users_collection[0].objects.link(ob)
    ob.matrix_world = world.matrix_world.copy()
    ob.hide_render = True
    ob.hide_set(True)   # it stands on its own parts; shown, the viewport drew the tree twice
    out.calc_loop_triangles()
    return ob, len(out.loop_triangles)


def export_glb(ob, path):
    """`ob` at the origin, alone, Y up (Blender -Y front -> glTF +Z), as repro_kit does."""
    keep = ob.matrix_world.copy()
    vl = bpy.context.view_layer
    try:
        ob.matrix_world = Matrix.Identity(4)
        vl.update()
        for o in vl.objects:
            if o.select_get():
                o.select_set(False)
        ob.hide_set(False)
        ob.select_set(True)
        vl.objects.active = ob
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True,
                                  export_yup=True, export_apply=False, export_animations=False,
                                  export_cameras=False, export_lights=False, export_extras=False,
                                  export_materials="EXPORT")
    finally:
        ob.matrix_world = keep
        ob.select_set(False)
        ob.hide_set(True)
        vl.update()
    from repro_kit import check_glb
    return check_glb(path)


def make(seed, name=None, out_dir=None, cfg=None, spacing=9.0):
    """Both sprites, the model and the sidecar for one seed.

    Writes `<out>/<name>/`: `<name>.png`, `<name>_burnt.png`, `tree.glb`,
    `tree.json`, and the sheets `_check.png`, `_check_game.png`,
    `_check_burn.png`. One model, not a live and a burnt one: it burns
    itself (the burn contract, `toon_preview`). The live sprite tree stands at
    the seed's slot, its burnt twin one `spacing` in front, the model one
    behind.
    """
    name = name or f"Oak_{seed:03d}"
    out_dir = os.path.join(out_dir or OUT, name)
    os.makedirs(out_dir, exist_ok=True)
    for stale in ("tree_burnt.glb", "_check_burnt_game.png", "_check_live_game.png",
                  f"_{name}_burnt_game.png"):
        if os.path.exists(os.path.join(out_dir, stale)):   # from before the model burnt itself
            os.remove(os.path.join(out_dir, stale))
    _wipe(f"{name}_burnt_game")
    base = {**(cfg or {}), "seed": seed, "name": name}
    at = Vector(base.get("offset", (0, 0, 0)))
    made = {}
    # three slots in Y: burnt twin in front, live sprite, model behind
    for state, detail, shift in (("live", "sprite", 0.0), ("burnt", "sprite", -spacing),
                                 ("live", "game", spacing)):
        made[(state, detail)] = build({**base, "state": state, "detail": detail,
                                       "offset": tuple(at + Vector((0, shift, 0)))})
    live, burnt = made[("live", "sprite")]["name"], made[("burnt", "sprite")]["name"]
    shot = sprites([live, burnt], [os.path.join(out_dir, f"{name}.png"),
                                   os.path.join(out_dir, f"{name}_burnt.png")])
    shot["check"] = check_sheet(shot["files"], os.path.join(out_dir, "_check.png"))
    shot["feet"] = [foot_of(p) for p in shot["files"]]
    dx = abs(shot["feet"][0]["foot"][0] - shot["feet"][1]["foot"][0])
    if dx > 2.0:   # the pair's one hard contract with PropSet; a pair that breaks it is not written off as a note
        raise RuntimeError(f"{name}: live and burnt feet differ by {dx} px ({shot['feet']})")
    gname = made[("live", "game")]["name"]
    ob, tris = bake(gname)
    glb = os.path.join(out_dir, "tree.glb")
    model = {"file": "tree.glb", "tris": tris, "check": export_glb(ob, glb), "burn": glb_burn(glb),
             "sheets": [game_check(gname, os.path.join(out_dir, "_check_game.png"), shot["files"][0]),
                        burn_check(gname, os.path.join(out_dir, "_check_burn.png"), *shot["files"])]}
    g = made[("live", "game")]
    spec = {
        "name": name, "seed": seed, "generator": "pipeline/tree_gen.py",
        "units": "m", "up": "+Y (glTF)", "front": "+Z (glTF) = -Y Blender",
        "foot": [0.0, 0.0, 0.0],
        "height": g["height"], "width": g["width"], "depth": g["depth"],
        "sprites": {"live": f"{name}.png", "burnt": f"{name}_burnt.png",
                    "size": shot["size"], "px_per_m": shot["px_per_m"],
                    "elevation": SPRITE_ELEVATION,
                    "centre": shot["centre"],
                    "feet": {"live": shot["feet"][0], "burnt": shot["feet"][1]}},
        "model": {"file": "tree.glb", "tris": tris,
                  "burn": {"uv": "TEXCOORD_1.x", "value": "burn, 0 green .. 1 done",
                           MAT_NAMES["game"]["leaf"]: "gone once burn > x",
                           MAT_NAMES["game"]["core"]: "gone once burn > x",
                           MAT_NAMES["game"]["twig"]: "shown once burn > x",
                           MAT_NAMES["game"]["bark"]: "x unused; chars with burn",
                           "ranges": BURN}},
    }
    with open(os.path.join(out_dir, "tree.json"), "w", encoding="utf-8") as f:
        json.dump(spec, f, indent=1, ensure_ascii=False)
    return {"dir": out_dir, "sprites": shot, "model": model,
            "report": {f"{s}/{d}": r for (s, d), r in made.items()}}


if __name__ == "__main__":
    print(make(CONFIG["seed"]))
