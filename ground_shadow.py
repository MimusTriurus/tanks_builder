"""The shadow the tank puts on the ground it stands on.

A *cast* shadow, along the board's own sun - and it was a contact shadow, which
is worth keeping in view, because everything below is what changing that cost.

The complaint it originally answered is that the tanks read as decals laid on
the field rather than as vehicles standing on it, and darkening the ground
where the hull meets it needs no sun direction at all. So this had none: the
lamp was straight overhead, the shadow was the plan-view footprint softened,
and the game was deliberately not committed to a sun that its terrain and
buildings would then have to honour.

**That debt has now been called in.** The board grew trees and relief, and a
hill has no contact shadow: a shadow of a hill is directional or it is nothing.
So the board declares a sun - `Stage3D.Sun`, 55 degrees up - and this pass is
rendered under it, because a tank whose shadow is a symmetric blob standing
beside a tree whose shadow points somewhere is not a cheaper shadow, it is a
second sun.

The three things the overhead lamp bought, and what each costs now:

- **No turret in it, and that is unchanged.** Two shadow layers still do not
  composite - alpha-over compounds, and two 0.55 shadows come out 0.80, a dark
  patch that would rotate with the turret. At 55 degrees the turret's footprint
  no longer sits inside the hull's, so this is now a real loss rather than a
  free one: what is drawn is the shadow of the hull and the belts. It is paid
  because the alternative is a patch that slides about the tank as the gun
  comes round, which reads as broken where a missing turret shadow reads as
  nothing at all.
- **It outgrows the ordinary frame, but only just.** The old note here said it
  reaches 110px and so wants 207px of a 128px half-frame, and that number was
  the whole *tank's* height - which does not cast: what does is the hull and
  the belts, a slab. Measured on the three re-rendered sets, the far corner
  stands 117-119px from the anchor against the 128 a 256 frame gives, which is
  nine pixels of margin and not enough to build on. So this layer takes
  `tile_scale` 1.5 and is rendered at 384, the same width the burn column
  takes, leaving 73px.

  **And it costs nothing, which was the argument the old note was making
  against a number it had wrong.** `atlas_pack` keeps the trimmed rect, not the
  frame, and the shadow of a slab is the slab's own footprint pushed a little
  along the ground: 24 frames at 190-203px wide against the contact shadow's
  192, so the three sets come to 5.7MB decoded - the same 5.7MB they were.
- **It leaves the cell, and it has to.** The catcher was a disc of the tile's
  inradius precisely so the shadow was clipped to ground the tank owns and
  `max_edge_alpha` stayed zero without anyone checking the sun. A cast shadow
  that stopped at the cell boundary would be a shadow with a hexagon cut out of
  it. So the disc is `reach` bigger than the tile now, and this layer is named
  in `tank_pipeline` as the one the frame-edge rule does not apply to - said
  out loud there, because a rule quietly not applying is worse than either
  answer.

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

    # How far the disc reaches, as a fraction of the tile's inradius.
    #
    # It was 0.98 - under 1 by a hair, so a disc built on a rounded radius
    # could not poke a pixel past a tile that was itself checked against the
    # frame. With a sun 55 degrees up that number clips the shadow rather than
    # containing it: the far end of the hull throws its own height times
    # cot(55) = 0.70 beyond its footprint, which on these tanks is about 0.28
    # world against an inradius of 0.64.
    #
    # 1.8 is generous on purpose and costs nothing: the catcher renders at
    # alpha = the sun it lost, so every part of the disc that is not in shadow
    # is not in the frame either. What a too-small disc costs, by contrast, is
    # a shadow with a circle cut out of it - and it would be cut along a curve
    # that no check is looking for.
    "reach": 1.8,

    # How far above the ground the disc sits, in fractions of the tile's
    # radius. Coplanar with the tank's own lowest geometry is a coin toss over
    # which surface the depth test keeps, and it shows up as the shadow being
    # eaten in patches that move with the heading - the same trap `hit_scar`
    # documents for a decal on armour. Small enough that the lift itself is
    # under a pixel.
    "lift": 0.002,

    # The board's own sun, and these two numbers are not free: they are
    # `Stage3D.Sun` written in this rig's convention, so that the shadow baked
    # here and the shadow the board marches for its hills are the same sun.
    #
    # Worked back rather than guessed. A sun points down -Z at rest and
    # `build_lighting` orients it by Rz(az)*Rx(90-elev), so the direction to it
    # is (sin(az), -cos(az), 1) scaled by (sin, sin, cos) of the tilt; the
    # camera at azimuth 0 stands at -Y, so Blender's (x, y, z) is the board's
    # (x, -z, y). Setting that equal to Stage3D.Sun = (-0.40, 0.82, -0.41)
    # gives cos(tilt) = 0.819 -> elevation 55.0, and
    # (sin az, -cos az) = (-0.697, -0.714) -> azimuth -135.7.
    #
    # It is behind the board, which is the board's decision and is argued where
    # it is made: a sun on the camera's side - where a three-point key sits -
    # throws every shadow up the screen and under the thing that cast it.
    #
    # Change either of these and the board disagrees with its own tanks. That
    # used to be unnoticeable - nothing measured a shadow's angle - so the sun
    # now travels in the layer's own JSON and `tank_pipeline.check` asserts the
    # sign of the offset between the shadow and the tank on every heading.
    "elevation": 55.0,
    "azimuth": -135.7,

    # The lamp's angular size, in degrees, which is the whole of the softness.
    #
    # <b>It was 28, and that number belonged to the overhead lamp.</b> Wide on
    # purpose then: what was wanted was the dark that gathers under a body, the
    # shadow was a footprint a few pixels wider than the hull, and a hard edge
    # on it read as a sticker of a tank lying on the field.
    #
    # A cast shadow is the opposite case, because the blur grows with the throw:
    # the penumbra is about `distance * tan(angle/2)`, and at 55 degrees the far
    # end of this one is 110px out, so 28 degrees smears it by some 27px - wider
    # than the shadow is. Rendered at that, it came out as a faint wash over the
    # whole cell and its neighbours with no shape in it at all, which is worse
    # than the contact shadow it replaced rather than better: a shadow with no
    # shape says nothing about what cast it.
    #
    # 8 degrees keeps a couple of pixels of softness at the hull and a few at
    # the tip, so the thing has a silhouette and still is not a cut-out. Bigger
    # than the sun's own half degree by a lot, and deliberately: this board is
    # painted, not photographed, and the trees beside it wear a soft edge too.
    "softness": 8.0,

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
                "centre": [round(float(v), 5) for v in centre[:2]],
                # The sun this was baked under, carried out rather than left
                # for a reader to import this module and merge the same
                # overrides again. The check downstream asserts which way the
                # shadow runs, and it has to be told by the pass that cast it -
                # a second opinion about the sun is the whole failure being
                # guarded against.
                "azimuth": float(cfg["azimuth"]),
                "elevation": float(cfg["elevation"]),
                "run": [round(v, 5) for v in ground_run(cfg)]}


def ground_run(cfg=None):
    """Which way a shadow runs along the ground, as a unit vector in world XY.

    Away from the sun, which is the half of this that a sign error gets wrong
    and a picture does not report: a shadow thrown the other way is a plausible
    picture of a different sun. The direction to the sun is
    `Rz(az)*Rx(90-elev)*(0,0,1)`, whose horizontal part is
    `sin(tilt)*(sin(az), -cos(az))`; a shadow runs against it.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    az = math.radians(float(cfg["azimuth"]))
    return -math.sin(az), math.cos(az)


def rig(cfg=None):
    """The one sun this pass wants, in `sprite_atlas`'s `light_rig` form.

    <b>The energy is divided by `sin(elevation)`, and leaving that out is what
    a tilted sun costs the material's self-calibration.</b> The catcher reads
    `1 - shading` and calls it the fraction of the sun that was lost, which is
    exact only when a fully lit horizontal plate shades to exactly 1.0 - and
    that is what `irradiance = pi` buys for a sun straight overhead. Tilt the
    sun to 55 degrees and the same plate takes `sin(55) = 0.819` of it, so
    every unshadowed pixel of the disc comes back 0.18 occluded.

    It is not subtle once seen and it is invisible in every number: the
    contrast, the anchor, the framing and the edge alpha were all correct, and
    the frame was a good tank-shaped shadow sitting in the middle of a grey
    ellipse the size of the whole catcher. On the board that read as the tank
    having no shadow at all - a faint even wash over its cell and the
    neighbours, which the eye takes for the ground being that colour.

    Written here rather than folded into `irradiance` because irradiance is
    what it always was - pi, the number that makes the plate self-calibrating -
    and this is the correction for where the sun is standing. Two names, so
    that moving the sun cannot quietly turn the calibration off.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    square = math.sin(math.radians(float(cfg["elevation"])))
    return [{"tag": "shadow",
             "energy": float(cfg["irradiance"]) / max(square, 1e-6),
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
        # Half again the tank's frame: the shadow's far corner stands 119px
        # from the anchor and a 256 frame gives 128 - see the module docstring
        # for why the number that used to be here was 207. `tile_scale` widens
        # the frame without changing what a pixel is worth, and recomputes from
        # the rounded tile so `units_per_pixel` comes out bit identical to the
        # layers around it rather than nearly so.
        "tile_scale": 1.5,
        # The sun this layer was baked under, written into its own JSON.
        #
        # <b>So that the atlas carries it and nobody has to be told.</b> The
        # board declares a sun and this pass is rendered under it; two copies of
        # that number, one in a shader constant and one here, is the arrangement
        # that parts silently - and a shadow pointing the wrong way is a
        # perfectly plausible picture of a different sun, so nothing downstream
        # would report it. With it in the metadata the check can ask which way
        # this shadow runs, and a reader can too.
        "meta_extra": {"sun": {"azimuth": float(cfg["azimuth"]),
                               "elevation": float(cfg["elevation"]),
                               "run": [round(v, 5) for v in ground_run(cfg)]}},
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
