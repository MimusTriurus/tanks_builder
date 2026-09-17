using Godot;

namespace TankSpriteTest;

/// <summary>
/// The inside of a tank lighting up: what a round that got through looks like
/// from outside the armour - <c>pierce</c> in <c>docs/blast.md</c>.
///
/// <b>The only one of the nine that adds to a picture instead of replacing
/// one, and that is a finding rather than a licence.</b> The ricochet
/// (<see cref="ProcSpall"/>) and the burst on the plate (<see cref="ProcSlam"/>)
/// each went in <em>instead</em> of the rendered pair, because two accounts of
/// one shell arriving is the double model this project refuses. This is not a
/// second account: <see cref="ProcSlam"/>'s own note says what the rendered pair
/// is - "a puff of earth-tan dust with a warm core, which is a shell going in" -
/// so the outside of a penetration is already drawn, and drawn well. What was
/// never drawn is the other side of the plate. One shell, two sides, one picture
/// each.
///
/// <b>So this is also the layer that settles an argument two effects lost.</b>
/// Section A of <c>docs/blast.md</c> says the three hits on armour belong on the
/// sprite's own canvas, and the ricochet and the plate burst both left it - one
/// for depth against the world, one for a family that reaches the ground. The
/// argument was never wrong, it was simply about the third: light from
/// <em>inside</em> is an assertion about the hull's own alpha, and the hull's
/// alpha is here and nowhere else. Nothing about this effect could be drawn on
/// the board at all.
///
/// <b>The mask is the thing being drawn, which is the whole trick.</b> This
/// layer draws the hull's own trimmed frame with a material of its own, so
/// <c>COLOR.a</c> in the fragment <em>is</em> the silhouette - the light can only
/// land where there is armour, without a second texture, a second sample or a
/// second copy of where the hull is this frame. Nothing here samples
/// <c>TEXTURE</c>, and that is not a shortcut but the trap
/// <see cref="TankSprite.CharShader"/> already paid for: on this backend a
/// shader that samples what <c>COLOR</c> already holds gets texel times texel.
///
/// <b>No clock of its own, and that is deliberate.</b> The hit already has one -
/// <see cref="HitLoop"/> counts screen frames so two runs under <c>--capture</c>
/// can be diffed - and this is the same event as the rendered burst rather than
/// a companion to it. A second clock would be a second answer to when the shell
/// landed, and the two would part on the frame somebody changed one.
///
/// <b>Two families, and both are seated on something already measured.</b> The
/// bleed sits at <see cref="TankSprite.HitOffset"/>, the point the rendered burst
/// and the scar both stand on; the leak sits at the origin, which is
/// <c>anchor_px</c>, which is the turret ring - the same free measurement
/// <see cref="ProcRack"/> found. Hatches and vision slits are where the light
/// would really come out and nothing on this project knows where they are: they
/// are not stamped, and stamping them would be hand work per tank, which the
/// root <c>CLAUDE.md</c> spends a section refusing. The ring is the one opening
/// whose position is a fact.
/// </summary>
public sealed partial class ProcPierce : Node2D
{
    public TankSprite? Tank;

    /// <summary>Whether the layer draws at all - <c>--pierce off</c> gives back
    /// the picture a penetration had before this existed, which is the rendered
    /// pair on its own. Every A/B on this board is a flag for
    /// <c>Stage3D.Blast</c>'s reason, and here it is the only one available: there
    /// is no rendered twin of this layer to swap with.</summary>
    public bool Showing = true;

    /// <summary>What the last draw put up, for the readout and the checks.
    /// </summary>
    public Rect2 Extent { get; private set; }

    public bool Drawn { get; private set; }

    private ShaderMaterial? _ink;

    public override void _Ready()
    {
        _ink = new ShaderMaterial { Shader = Glowing };
        Material = _ink;
        // <b>And nothing is done to the z at all, which took a wasted capture to
        // remember.</b> Children draw after their parent in tree order with a
        // relative z, which is the whole of how LayerOrder works; turning that off
        // without setting an index puts this layer at absolute zero, behind every
        // tile on the board. TankSprite.ShadowZ is the one layer here that wants
        // an absolute z and it says why - the shadow is the ground with the tank
        // dark on it. This is light on the tank.
    }

    /// <summary>Whether there is anything to draw: a hit running, on a tank whose
    /// hull is on screen, that <em>got in</em>.
    ///
    /// <b>The last clause is the whole layer.</b> A round that bounced lights
    /// nothing inside, and one that burst on the face lights the face - see
    /// <see cref="ProcSpall"/> and <see cref="ProcSlam"/>, which draw those two.
    /// The fact travels on the hit rather than being re-derived here, because
    /// whether the gun out-classed the armour is <see cref="Gunnery.Penetration"/>'s
    /// answer and this project keeps it in one place.</summary>
    private bool Wanted => Showing
                           && Tank is { HitThrough: true, HitFrame: >= 0 } tank
                           && tank.ShowHull && tank.Atlas is not null;

    public override void _Process(double delta)
    {
        bool want = Wanted;
        if (want || Drawn)
            QueueRedraw();
    }

    public override void _Draw()
    {
        Drawn = false;
        if (!Wanted || Tank?.Atlas is not AtlasSet atlas)
            return;
        int frame = atlas.FrameFor(Tank.HullFacing);
        Vector2 size = atlas.SizeOf("hull", frame);
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return;
        // The hull's own quad, to the pixel - TankSprite.DrawLayer's three terms
        // and no fourth: the trimmed frame's offset, the shared anchor and the
        // heave. Written out rather than shared because what is shared is the
        // atlas, and a layer that computed this differently would light a hull
        // that is not where it thinks.
        var quad = new Rect2(-atlas.Anchor + atlas.OffsetOf("hull", frame)
                             + new Vector2(0.0f, Tank.Heave), size);
        Extent = quad;
        // Where the round went in and where the ring is, both in this quad's own
        // UV so the shader needs neither the anchor nor the trim.
        Vector2 wound = (Tank.HitOffset - quad.Position) / size;
        Vector2 ring = -quad.Position / size;
        _ink?.SetShaderParameter("wound", wound);
        _ink?.SetShaderParameter("ring", ring);
        _ink?.SetShaderParameter("quad", size);
        // <b>And where this frame sits in the atlas, because UV is the
        // TEXTURE's coordinate and not the quad's.</b> A canvas item drawn with
        // DrawTextureRectRegion gets UV over the region inside the whole sheet -
        // for a 5x5 grid, values like 0.31..0.35 - so a shader that reads UV as
        // 0..1 across what it drew is measuring from the sheet's corner. It
        // fails silently and it fails plausibly: the glow still landed on the
        // tank, in two small patches where the expression happened to dip under
        // its own threshold, and every tuning pass moved them without ever
        // making sense of them.
        Rect2 cut = atlas.Region("hull", frame);
        Vector2 sheet = atlas.Texture("hull").GetSize();
        _ink?.SetShaderParameter("uv_at", cut.Position / sheet);
        _ink?.SetShaderParameter("uv_span", cut.Size / sheet);
        _ink?.SetShaderParameter("frame", (float)Tank.HitFrame);
        // In hull spans, the way the muzzle smoke is in bore radii: one number
        // for three tanks, and the only reason it can be one number.
        _ink?.SetShaderParameter("span",
            atlas.HullSpan > 0 ? atlas.HullSpan : size.X);
        _ink?.SetShaderParameter("scale", Tank.HitScale);
        DrawTextureRectRegion(atlas.Texture("hull"), quad,
                              atlas.Region("hull", frame));
        Drawn = true;
    }

    /// <summary>
    /// The light, as one text.
    ///
    /// <b>Additive, and premultiplied on the way out</b> - the statement every
    /// emitter on this board makes, and here it is the model as well as the blend
    /// mode: what is being drawn is light that was already inside the tank, so it
    /// adds to the armour rather than replacing it. A round hole punched in the
    /// paint would be a hole; this is a plate with light behind it.
    ///
    /// <b>Two families and one mask.</b> The bleed is the interior seen through
    /// the metal, brightest at the entry and dying off over a fraction of the
    /// hull; the leak is the ring, which is the one gap in a tank's roof this
    /// project has a measurement for. Both are multiplied by the hull's own
    /// alpha, so neither can put a pixel of light where there is no tank - and
    /// that is what makes this read as illumination rather than as a decal
    /// pasted over the silhouette.
    ///
    /// <b>The frame count is the axis, not seconds.</b> <see cref="HitLoop"/>
    /// counts screen frames for the reason every event on this board does, and
    /// the first two frames are one frame each - which is exactly the resolution
    /// an instant needs.
    /// </summary>
    private const string GlowShader = @"
shader_type canvas_item;
render_mode unshaded, blend_add;

FLAME_NOISE

// Where the round went in and where the ring sits, in this quad's own UV, and
// how big the quad is in px - see ProcPierce._Draw, which is the only thing that
// knows about the trim.
uniform vec2 wound = vec2(0.5);
uniform vec2 ring = vec2(0.5);
uniform vec2 quad = vec2(1.0);
// Where this heading's frame sits in the sheet, in the sheet's own UV - see
// ProcPierce._Draw. Everything below works in the quad's own space and gets
// there through these two.
uniform vec2 uv_at = vec2(0.0);
uniform vec2 uv_span = vec2(1.0);
// Screen frames since the shell landed, and the hull's broadside in px: every
// length below is a share of the second one, which is what lets one set of
// numbers fit three tanks. See ProcFume on bore radii, whose argument this is.
uniform float frame = -1.0;
uniform float span = 1.0;
uniform float scale = 1.0;

// The bleed: the inside of the hull lit through its own plating. Bright for two
// frames and gone by six, because what it is is a charge of burning metal and
// propellant arriving in a closed box - see docs/blast.md. It is the one frame in
// this whole board that says a round went THROUGH rather than into.
uniform float bleed_frames = 6.0;
// How far the light carries from the hole, in hull spans.
//
// <b>A tenth, and the first reading was a third - which washed the whole hull
// pale and read as the tank getting brighter.</b> That is the failure this number
// decides between: a soft radial glow over armour is a lighting change, and what
// light escaping through a hole looks like is small, bright and hard-edged. The
// seams and the hatches are where it would really come out and this project does
// not know where they are (see the class note), so what is left is the hole
// itself - and the hole is the honest thing to draw.
uniform float bleed_reach = 0.09;
// Brighter than it was by the same factor the reach lost, and that is the rule
// this board states everywhere: white is bought with brightness, not with a paler
// colour. Spread over a third of a hull this much light is a wash; over a tenth
// it clips in the middle, which is what a hole with fire behind it does.
uniform float bleed_gain = 4.50;
// How hard the light falls off from the hole. Past one, so most of the glow is
// close in and the tail is long and faint - a hard edge here reads as a circle
// drawn on the armour.
uniform float bleed_fall = 1.40;
// <b>How far inside the plate the light is seated, as a share of the way to the
// ring - and this is the one term the mask made necessary.</b> Seated at the
// entry point the brightest part of the glow lands where COLOR.a is nought: the
// wound is measured on the plate's own face, which is the silhouette's edge, and
// the rendered burst is drawn beyond it. So the mask ate exactly the middle and
// what was left was a soft crescent - a lighting change on the front plate rather
// than a hole with fire behind it.
//
// Toward the ring rather than along the plate's normal, because the ring is the
// middle of the tank and is already here: what is behind a plate is the inside,
// and the inside is that way whichever plate was hit. No new measurement.
uniform float bleed_sink = 0.38;
// The hot end and the cold end of what is coming through. Not the flame ramp:
// this is light through steel, so it is the ramp's own colours pushed toward
// white and thinned, and it never reaches the red the flame ramp ends at -
// nothing here has cooled, it has been occluded.
// <b>Redder than white, even though the middle of this clips white anyway.</b>
// Additive light over a green hull goes to white as soon as it is bright, which
// is right - a hole with a burning charge behind it IS white in the middle - so
// what these two colours actually decide is the SKIRT. Read at 0.93 green the
// falloff came out pale yellow and the whole thing read as a highlight on the
// paint; at 0.80 the ring of it is orange and the white is only the core.
uniform vec3 bleed_hot = vec3(1.000, 0.800, 0.500);
uniform vec3 bleed_cool = vec3(1.000, 0.470, 0.150);

// <b>The seam's own pair, because it was taking the bleed's by accident.</b>
// The colour walked with the bleed's age and then stuck at whatever it was when
// the bleed died, which is a family reading its neighbour's clock. What comes out
// of a ring is gas rather than the charge itself, so it is the cooler of the two
// from the start and it has further to walk.
uniform vec3 leak_hot = vec3(1.000, 0.720, 0.380);
uniform vec3 leak_cool = vec3(1.000, 0.400, 0.120);

// The leak: light out of the turret ring, which is the one opening on a tank
// whose position this project measures - it is anchor_px, and the whole layer
// stack stands on it.
uniform float leak_frames = 14.0;
// <b>Wide enough to come out from under the turret, which is what decides
// it.</b> This family draws under the turret on purpose - light at the ring is
// light escaping from beneath it - so a radius smaller than the turret is a
// family that is drawn entirely behind one and never seen. What shows is the
// crescent past its skirt.
// <b>And it has to hug the turret's base, which a trace settled.</b> Read at
// 0.24 of a hull span the circle stood 42px out from the ring while the turret is
// about 50px across at this heading - so the seam was drawn on the sponsons
// either side of the turret rather than at its foot, and what showed was two
// panels lighting up. At 0.15 the annulus sits on the turret's own skirt and only
// its outer edge escapes, which is what a seam is.
uniform float leak_reach = 0.15;
// <b>A band at the ring rather than a disc on it, and the disc was the whole of
// what made this read as a wash.</b> Read as a disc of 0.24 of a hull span the
// family covered the front of the tank and simply raised its value - which is
// what was reported about the first version of the bleed as well, arrived at from
// the other direction. Light out of a ring is light out of a SEAM: it is a thin
// bright line where the turret meets the roof, and everything about how it reads
// is that it is thin.
uniform float leak_band = 0.045;
uniform float leak_gain = 1.90;
uniform float leak_fall = 2.20;
// How much of the ring's light survives the distance from the wound: a round in
// the far side of the hull lights the ring less than one under the turret. A
// share rather than a switch, because the ring is lit by the same event either
// way.
uniform float leak_carry = 0.55;

// How torn the light is. A glow with a clean profile reads as a soft brush; the
// same field the flame and the dust are broken up with makes it read as light
// getting out through a structure.
// <b>Coarse tearing read as a soft brush, so the field is fine.</b> At grain 9
// over the hull's own UV one lobe of the noise was most of the glow and the shape
// was simply a lopsided circle; at 26 the light is broken into something the eye
// reads as getting out through a structure rather than through a hole cut in
// paper.
uniform float glow_tear = 0.45;
uniform float glow_grain = 26.0;

void fragment() {
    // The hull's own alpha, and nothing else in this shader samples anything.
    float skin = COLOR.a;
    if (frame < 0.0 || skin <= 0.001) {
        COLOR = vec4(0.0);
    } else {
        // The quad's own 0..1, out of the sheet's - see uv_at. Then px from the
        // quad's corner, so a distance is a distance whatever the trim did, and
        // then hull spans, which is what the numbers below are in.
        vec2 local = (UV - uv_at) / max(uv_span, vec2(1e-6));
        vec2 at = local * quad;
        float unit = max(span, 1.0);
        // Seated inside the plate rather than on it - see bleed_sink.
        vec2 seat = mix(wound, ring, bleed_sink);
        float hole = length(at - seat * quad) / unit;
        float gap = length(at - ring * quad) / unit;
        float ring_far = length((ring - seat) * quad) / unit;

        float torn = 1.0 - glow_tear
                     + glow_tear * ember_fbm(local * glow_grain);

        // Premultiplied by hand: each family carries its own colour, so the two
        // are summed as light rather than mixed as pigment. Written the other way
        // - one lit and one body - the seam took whatever colour the bleed had
        // left, which is a family reading a clock that is not its own.
        vec3 light = vec3(0.0);

        float ba = frame / max(bleed_frames, 1.0);
        if (ba < 1.0) {
            // Squared out rather than linear: the inside of a hull goes dark as
            // fast as it lit up, and a linear fall reads as a lamp being turned
            // down.
            float life = pow(max(1.0 - ba, 0.0), 2.20);
            float reach = bleed_reach * scale * (0.70 + 0.55 * ba);
            float body_at = pow(clamp(1.0 - hole / max(reach, 1e-4), 0.0, 1.0),
                                bleed_fall);
            light += mix(bleed_hot, bleed_cool, clamp(ba * 1.30, 0.0, 1.0))
                     * (bleed_gain * life * body_at);
        }

        float la = frame / max(leak_frames, 1.0);
        if (la < 1.0) {
            float life = pow(max(1.0 - la, 0.0), 1.60);
            float reach = leak_reach * scale * (0.55 + 0.80 * la);
            // Distance from the ring's own circle rather than from its middle:
            // an annulus, so what is drawn is the seam and not the roof.
            float rim = abs(gap - reach) / max(leak_band * scale, 1e-4);
            float band = pow(clamp(1.0 - rim, 0.0, 1.0), leak_fall);
            // Less of it the further the hole is from the ring, which is the one
            // thing that makes this the same event as the bleed rather than a
            // second glow that happens to be on.
            float carry = mix(1.0, leak_carry,
                              clamp(ring_far / max(bleed_reach, 1e-4), 0.0, 1.0));
            light += mix(leak_hot, leak_cool, clamp(la * 1.15, 0.0, 1.0))
                     * (leak_gain * life * band * carry);
        }

        // Premultiplied: an adding layer has light to give and nothing to hide
        // behind, and the mask goes on the light rather than on the alpha for the
        // same reason.
        COLOR = vec4(light * (torn * skin), 1.0);
    }
}
";

    internal static readonly string GlowCode =
        GlowShader.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode);

    private static readonly Shader Glowing = new() { Code = GlowCode };
}
