"""The shadow the tank puts on the tile it stands on.

A *contact* shadow, not a cast one, and the difference is the whole design.

The complaint it answers is that the tanks read as decals laid on the field
rather than as vehicles standing on it. That is fixed by darkening the ground
where the hull meets it, and darkening it needs no sun direction at all - so
this one does not have one. The lamp is straight overhead and wide, the shadow
is the tank's plan-view footprint softened, and the game is not committed to a
sun that its terrain and buildings would then have to honour.

Three things fall out of that choice, and each is a problem a directional
shadow would have had:

- **No turret in it.** A cast shadow of the turret escapes the hull's and turns
  with the gun, so it would need a layer of its own - and two shadow layers do
  not composite: alpha-over compounds, and two 0.55 shadows come out 0.80,
  a dark patch that rotates with the turret. Straight overhead the turret's
  footprint is inside the hull's, so there is one layer and no overlap. The gun
  tube's footprint does escape, past the nose; it is left out, because a
  contact shadow reads as "the tank is standing there" rather than "the sun is
  over there", and nobody looks for the gun's shadow in it.
- **It fits the ordinary frame.** Measured on the three tanks: a shadow cast at
  the rig's own key sun (55 deg up) reaches about 110px, and against a 97px
  half-extent that is 207px of a 128px half-frame - it would have needed the
  wide tile. Overhead it reaches a few pixels.
- **It cannot leave the cell.** The catcher is a disc of the tile's inradius,
  so the shadow is clipped to ground the tank owns, and `max_edge_alpha` stays
  zero without anyone having to check the sun.

How the catcher works, and why it is a material rather than a flag
------------------------------------------------------------------
`object.is_shadow_catcher` exists in 5.2 and EEVEE ignores it: probed on this
scene, the plate came back opaque at alpha 255 everywhere, with the shadow
present only as darker *colour*. So the catcher is built out of nodes instead,
which is the standard EEVEE answer and is exact rather than a fit:

    Diffuse BSDF (white) -> Shader to RGB -> 1 - x  ->  Mix Shader fac
                                       Transparent BSDF  /   black Emission

`Shader to RGB` reads the shaded result *before* the view transform, so the
number it hands back is linear, and with the world at zero and a single sun of
irradiance pi straight down on a horizontal white lambertian plate the lit
value is exactly 1.0. Which makes the fac exactly the fraction of the sun that
was blocked - the shadow, with nothing to calibrate.

The frame therefore comes out black at alpha = occlusion, which is a shadow
sprite and not a picture of a grey plate that something downstream has to
subtract.

`sprite_atlas` supplies the other half: `casters` puts the tank in the render
with `visible_camera = False`, so it throws the shadow without appearing, and
turns with the turntable so the shadow is the tank at *this* heading.
"""

import math

import bpy

CONFIG = {
    # the disc that catches it, and the objects that throw it
    "name": "Shadow.Catcher",
    "material": "Shadow.Catcher.Material",
    "segments": 64,

    # How far the disc reaches, as a fraction of the tile's inradius. Under 1
    # by a hair so that a disc built on a rounded radius cannot poke a pixel
    # past a tile that was itself checked against the frame.
    "reach": 0.98,

    # How far above the ground the disc sits, in fractions of the tile's
    # radius. Coplanar with the tank's own lowest geometry is a coin toss over
    # which surface the depth test keeps, and it shows up as the shadow being
    # eaten in patches that move with the heading - the same trap `hit_scar`
    # documents for a decal on armour. Small enough that the lift itself is
    # under a pixel.
    "lift": 0.002,

    # Straight down. Not a taste: a contact shadow with a direction is a cast
    # shadow, and everything this module is for follows from not having one.
    "elevation": 90.0,
    "azimuth": 0.0,

    # The lamp's angular size, in degrees, which is the whole of the softness.
    # Wide on purpose - what is wanted is the dark that gathers under a body,
    # and a hard-edged footprint reads as a sticker of a tank lying on the
    # field, which is the thing being fixed.
    "softness": 28.0,

    # Sun irradiance. pi exactly, so that a white lambertian plate lit square
    # on comes back at 1.0 and the occlusion needs no reference measured off
    # the frame. Do not tune this - tune `strength` below, or the material
    # stops being self-calibrating and every number here becomes a fit.
    "irradiance": math.pi,

    # How much of the occlusion reaches the alpha. 1.0 is the shadow as
    # measured, which is what ships; this is the dial for "too heavy" and it is
    # separate from `irradiance` on purpose, so that turning the weight down
    # cannot quietly turn the calibration off with it.
    "strength": 1.0,
}


def _catcher_material(cfg):
    """A plate that renders as black at alpha = how much sun it lost."""
    name = cfg["material"]
    old = bpy.data.materials.get(name)
    if old is not None:
        bpy.data.materials.remove(old)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()

    lit = tree.nodes.new("ShaderNodeBsdfDiffuse")
    lit.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
    lit.inputs["Roughness"].default_value = 1.0

    to_rgb = tree.nodes.new("ShaderNodeShaderToRGB")
    dark = tree.nodes.new("ShaderNodeMath")
    dark.operation = "SUBTRACT"
    dark.use_clamp = True
    dark.inputs[0].default_value = 1.0

    weight = tree.nodes.new("ShaderNodeMath")
    weight.operation = "MULTIPLY"
    weight.use_clamp = True
    weight.inputs[1].default_value = float(cfg["strength"])

    clear = tree.nodes.new("ShaderNodeBsdfTransparent")
    solid = tree.nodes.new("ShaderNodeEmission")
    solid.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
    solid.inputs["Strength"].default_value = 1.0

    mix = tree.nodes.new("ShaderNodeMixShader")
    out = tree.nodes.new("ShaderNodeOutputMaterial")

    tree.links.new(to_rgb.inputs["Shader"], lit.outputs["BSDF"])
    tree.links.new(dark.inputs[1], to_rgb.outputs["Color"])
    tree.links.new(weight.inputs[0], dark.outputs["Value"])
    tree.links.new(mix.inputs["Fac"], weight.outputs["Value"])
    tree.links.new(mix.inputs[1], clear.outputs["BSDF"])
    tree.links.new(mix.inputs[2], solid.outputs["Emission"])
    tree.links.new(out.inputs["Surface"], mix.outputs["Shader"])

    # the alpha is the whole output, so the plate has to be allowed to have one
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "BLENDED"
    elif hasattr(mat, "blend_method"):
        mat.blend_method = "BLEND"
    mat.use_backface_culling = False
    return mat


def build(centre, ground_z, radius, cfg=None):
    """The catcher disc, centred on the spin axis at the foot of the tank.

    A disc and not the tile's own hexagon, because the layer's root is spun by
    the turntable like every other layer root, and a hexagon spun about its
    centre changes silhouette every frame while a circle does not. Its radius
    is the tile's inradius, so it never reaches ground the tile does not cover.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    old = bpy.data.objects.get(cfg["name"])
    if old is not None:
        data = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if isinstance(data, bpy.types.Mesh):
            bpy.data.meshes.remove(data)

    n = max(8, int(cfg["segments"]))
    r = float(radius) * float(cfg["reach"])
    z = float(ground_z) + float(radius) * float(cfg["lift"])
    verts = [(centre[0] + r * math.cos(2.0 * math.pi * i / n),
              centre[1] + r * math.sin(2.0 * math.pi * i / n), z)
             for i in range(n)]
    me = bpy.data.meshes.new(cfg["name"])
    me.from_pydata(verts, [], [tuple(range(n))])
    me.update()
    ob = bpy.data.objects.new(cfg["name"], me)
    ob.data.materials.append(_catcher_material(cfg))
    bpy.context.scene.collection.objects.link(ob)
    return ob, {"name": ob.name, "radius": round(r, 5), "z": round(z, 5),
                "centre": [round(float(v), 5) for v in centre[:2]]}


def rig(cfg=None):
    """The one sun this pass wants, in `sprite_atlas`'s `light_rig` form."""
    cfg = dict(CONFIG, **(cfg or {}))
    return [{"tag": "shadow", "energy": float(cfg["irradiance"]),
             "azimuth": float(cfg["azimuth"]),
             "elevation": float(cfg["elevation"]),
             "angle": float(cfg["softness"])}]


def layer(catcher, casters, cfg=None):
    """The `render_set` layer spec.

    `world_strength` 0 and a single sun are not decoration: ambient would lift
    the shadow off the floor of the material's own calibration, and a second
    sun would throw a second shadow. The three-point rig's fill sits 20 deg
    above the horizon, so its shadow would be nearly three times the tank's
    height - long enough to leave the disc, and it would be read as this pass
    being broken rather than as the rig being what it is.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    return {
        "name": "shadow",
        "target": catcher,
        "casters": list(casters),
        "light_rig": rig(cfg),
        "world_strength": 0.0,
        # nothing to fit to: it is a flat disc under a tank that is already
        # framing the job, and letting it into the fit would ask the camera to
        # hold a disc swept round its own centre
        "fit": False,
    }


def remove(cfg=None):
    """Take the disc and its material back out of the scene."""
    cfg = dict(CONFIG, **(cfg or {}))
    ob = bpy.data.objects.get(cfg["name"])
    if ob is not None:
        data = ob.data
        bpy.data.objects.remove(ob, do_unlink=True)
        if isinstance(data, bpy.types.Mesh):
            bpy.data.meshes.remove(data)
    mat = bpy.data.materials.get(cfg["material"])
    if mat is not None:
        bpy.data.materials.remove(mat)
