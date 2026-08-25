using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The burning tank's flame, built here rather than read off the atlas -
/// <c>engine_fire</c>'s model, put where the forest's already is.
///
/// <b>The colour is not transcribed, it is shared.</b> The ramp this burns by is
/// <see cref="Stage3D.FlameInk"/>, and that ramp is the tank sprite's own hue,
/// measured off <c>fire_atlas</c> when the forest's fire was written - because a
/// canvas lands in an sRGB framebuffer where <c>engine_fire</c>'s linear stops are
/// six times too dark in green and blue. So the tree and the tank now burn by one
/// statement of what a lick is, which is the whole of what this was asked for. A
/// second copy here would be a second answer to the same question, parting where
/// nobody compares them.
///
/// <b>What is not shared is the placement, and it cannot be.</b> A tree's fire
/// climbs straight up its own quad, so the forest gets the path in closed form. A
/// tank's leaves the vent at whatever the vent points at - 49 degrees on one of
/// these three, 72 on another - so the path is marched in <see cref="Plumes.Path"/>
/// and the elements arrive at the shader already placed. Same split as the column:
/// C# owns the model, the shader owns the section across one element and the
/// stacking.
///
/// <b>Additive, and premultiplied on the way out.</b> The same statement
/// <see cref="EffectLayer.Glow"/> makes for the rendered layer, and for the same
/// reason: the stage shows its render target premultiplied, so a straight-alpha
/// emitter is multiplied by an alpha it never asked to have and dies inside the
/// target. What it is composited against is <see cref="ProcSmoke"/> - the dark
/// half is why the column exists.
///
/// <b>Counts are the forest's, not engine_fire's.</b> Thirty tongues, five seat
/// blobs and eight embers are geometry the rasteriser skips; here each one is work
/// every fragment does. The forest cut its licks to twenty-two for exactly that,
/// and taking its numbers is also what keeps a burning tank and a burning tree the
/// same fire.
/// </summary>
public sealed partial class ProcFire : Node2D
{
    /// <summary>Most elements the shader will composite. Everything the seat, the
    /// queue and the embers ask for has to fit; past this the tail is dropped and
    /// the note says so.</summary>
    public const int MaxParts = 48;

    /// <summary>Whether a bench opens with the flame built rather than read. False
    /// for <see cref="ProcSmoke.OnByDefault"/>'s reason: every shipped atlas draws
    /// the rendered one, and the A/B needs what is shipped on the other side of
    /// it.</summary>
    public const bool OnByDefault = false;

    // --- engine_fire.CONFIG, transcribed -------------------------------------
    // Quoted against the hull's length the way that file quotes them, except the
    // seat, which is quoted against both the hull and the port - and deliberately
    // the other way round from the plume's. Swapping them is what makes a
    // pipe-vented tank burn like a cigarette.
    /// <summary>
    /// How hard it burns, over everything below - engine_fire's own master knob,
    /// at engine_fire's own value.
    ///
    /// <b>Left at one, and that is a decision rather than a default.</b> Against
    /// the rendered layer the built flame comes out the same size and the same
    /// peak (58x25px against 53x25, peak add 123 against 125) and thinner through
    /// the middle: median redness 42 against 61.5 on pale ground, where
    /// engine_fire's own check calls 30 the floor. Raising this to 1.2 closes
    /// about half of that (49.0) - and on the heavy, whose flame is the widest of
    /// the three, it doubles the pixels where red pins at 255: 312 against 143 in
    /// one deck-sized box.
    ///
    /// Which is allowed by the rule engine_fire states and worth restating: red
    /// saturating on its own is not the failure, saturated red is red. The failure
    /// is green and blue coming up with it, and they do not - measured on the
    /// heavy, mean G/R 0.762 against the rendered layer's own 0.786, with not one
    /// near-white pixel at either setting. So the knob is left where its file
    /// leaves it and the gap is named instead: this queue is a little less massive
    /// through the middle than a stack of low-poly spheres, and the lever for that
    /// is elements, not gain.
    /// </summary>
    public float Brightness = 1.0f;

    public float Rise = 0.28f;
    public float Spread = 0.058f;
    public float Buoyancy = 0.95f;
    public float TurnPower = 0.55f;
    public float Gather = 0.70f;
    public float SeatFromHull = 0.032f;
    public float SeatFromPort = 0.21f;
    public float Taper = 0.26f;
    public float TaperPower = 0.90f;
    public float Stretch = 2.3f;
    public int Tongues = 30;
    public float Stagger = 0.42f;
    public float Vary = 0.40f;
    /// <summary>How far the lump noise pushes an element's rim, and so how
    /// far past its half-axes it draws. Shared with FlameInk, which reads it
    /// as a uniform; kept here because the quad has to be big enough for
    /// it.</summary>
    public float Wobble = 0.28f;
    public float Alpha = 0.70f;
    public float Onset = 0.07f;
    public float Fade = 0.55f;
    public float BornScatter = 0.70f;
    public int SeatBlobs = 5;
    public float SeatSize = 1.15f;
    public float SeatStretch = 1.15f;
    public float SeatScatter = 0.60f;
    public float SeatFlicker = 0.45f;
    public float SeatGain = 0.95f;
    public int Embers = 8;
    public float EmberReach = 1.15f;
    public float EmberSize = 0.26f;
    public float EmberSpread = 0.20f;
    public float EmberGain = 0.85f;
    public float EmberFade = 1.10f;
    public int Seed = 61;

    public TankSprite? Tank;

    private ShaderMaterial? _shader;
    private static Texture2D? _white;

    /// <summary>The quad, in px off the shared anchor, and whether the last draw
    /// put anything up. Kept so the checks and the readout can ask how big the
    /// flame came out without reading pixels.</summary>
    public Rect2 Extent { get; private set; }

    public bool Showing { get; private set; }

    public string Note { get; private set; } = "";

    private bool _told;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Linear;
        _shader = new ShaderMaterial { Shader = Blaze };
        Material = _shader;
        _white ??= White();
    }

    private static Texture2D White()
    {
        Image pixel = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        pixel.Fill(Colors.White);
        return ImageTexture.CreateFromImage(pixel);
    }

    public override void _Process(double delta)
    {
        if (Wanted || Showing)
            QueueRedraw();
    }

    private bool Wanted => Tank is { Burning: true, ProceduralFire: true }
                           && Tank.Atlas is { HasPorts: true }
                           && Tank.FireDensity > 0.002f;

    // --- the model -----------------------------------------------------------

    /// <summary>One element on its way to the shader: where it is, how wide and how
    /// long, which way it lies, where it sits on the ramp, how hard it burns and
    /// how high it is.</summary>
    public readonly record struct Part(Vector3 Centre, Vector3 Flow, float Radius,
                                       float Stretch, float Age, float Gain,
                                       bool Spark);

    /// <summary>
    /// The seat, the licks and the embers of one port at cycle position
    /// <paramref name="cycle"/> in [0, 1), in the model's own world.
    ///
    /// <b>The seat is proud of the grille along the port's own axis.</b> Without
    /// that offset half of every blob is inside the deck and only shows on the
    /// headings where the holdout happens not to cut it.
    ///
    /// <b>The stagger is hashed on the element and never on the phase.</b> Hash in
    /// the phase and every lick becomes a different lick every frame: the fire
    /// boils in place instead of rising, and no amount of shape tuning fixes it.
    /// </summary>
    public Part[] Build(AtlasSet.Port port, double hullLength, float cycle)
    {
        float hull = (float)hullLength;
        float seat = hull * SeatFromHull + port.Radius * SeatFromPort;
        var (u, v) = Plumes.Across(port.Dir);
        var made = new System.Collections.Generic.List<Part>(MaxParts);

        // The seat: a squat bright body on the port in every phase. Without it the
        // licks fade in from nothing at age 0 and a flame with no hot seat reads as
        // fog - the muzzle flash's lesson.
        for (int k = 0; k < SeatBlobs && made.Count < MaxParts; k++)
        {
            float offU = port.Radius * SeatScatter
                         * (2.0f * Plumes.Hash01(k, Seed + 4) - 1.0f);
            float offV = port.Radius * SeatScatter
                         * (2.0f * Plumes.Hash01(k, Seed + 5) - 1.0f);
            float lift = seat * 0.35f * Plumes.Hash01(k, Seed + 6);
            float size = seat * SeatSize * (0.7f + 0.6f * Plumes.Hash01(k, Seed + 7));
            // Flickers in place rather than moving, on a whole number of cycles a
            // lap so it cannot jump at the wrap - the one part of the effect that
            // never moves is the one part where a jump is obvious.
            float turns = 1 + k % 3;
            float wave = 0.5f + 0.5f * MathF.Cos(MathF.Tau
                * (turns * cycle + Plumes.Hash01(k, Seed + 3)));
            made.Add(new Part(
                port.Point + port.Dir * (seat * 0.45f) + u * offU + v * offV
                + Plumes.Up * lift,
                Plumes.Up, size, SeatStretch, 0.0f,
                SeatGain * (1.0f - SeatFlicker + SeatFlicker * wave) * Alpha, false));
        }

        // The licks. Positive rate, which is what tells this from the column: it
        // crowds them at the seat and pulls them apart toward the tips.
        Vector3[] path = Plumes.Path(port.Dir, hull * Rise, Buoyancy, TurnPower,
                                     Gather);
        for (int k = 0; k < Tongues && made.Count < MaxParts; k++)
        {
            float age = Age(k, Tongues, Seed + 1, cycle);
            float turn = MathF.Tau * Plumes.Hash01(k, Seed + 11);
            float sway = hull * Spread * MathF.Pow(age, 0.8f)
                         * Plumes.Hash01(k, Seed + 12);
            // Off-centre at birth across the port's own width, or every lick leaves
            // through the exact middle and the base of the column is a needle -
            // wrong for a pipe and very wrong for a grille, where the panel is alight.
            float bornU = port.Radius * BornScatter
                          * (2.0f * Plumes.Hash01(k, Seed + 13) - 1.0f);
            float bornV = port.Radius * BornScatter
                          * (2.0f * Plumes.Hash01(k, Seed + 14) - 1.0f);
            // An element ends by shrinking, not by fading, and that is forced by the
            // blend mode rather than chosen: the game adds this layer, so a low-alpha
            // pixel over the pale field is that field very slightly tinted - grey,
            // whatever colour it was meant to be. Shrinking removes it while every
            // pixel left is still saturated.
            float size = seat * (1.0f - (1.0f - Taper) * MathF.Pow(age, TaperPower))
                         * (1.0f - Vary + 2.0f * Vary * Plumes.Hash01(k, Seed + 15));
            made.Add(new Part(
                port.Point + Plumes.Along(path, age) + u * bornU + v * bornV
                + Sideways(sway, turn),
                Plumes.Going(path, age), size, Stretch, age,
                Alpha * Life(age, Fade), false));
        }

        // The embers: small, red, and further out than the flame. The same queue
        // with a longer reach, and named in practice as one of the things between a
        // mediocre fire and a good one.
        Vector3[] far = Plumes.Path(port.Dir, hull * Rise * EmberReach, Buoyancy,
                                    TurnPower, Gather);
        for (int k = 0; k < Embers && made.Count < MaxParts; k++)
        {
            float age = Age(k, Embers, Seed + 2, cycle);
            float turn = MathF.Tau * Plumes.Hash01(k, Seed + 31);
            float sway = hull * EmberSpread * MathF.Pow(age, 0.9f)
                         * Plumes.Hash01(k, Seed + 32);
            float size = seat * EmberSize * (0.5f + Plumes.Hash01(k, Seed + 33));
            made.Add(new Part(
                port.Point + Plumes.Along(far, age) + Sideways(sway, turn),
                Plumes.Going(far, age), size, 1.6f, age,
                EmberGain * Life(age, EmberFade), true));
        }
        return made.ToArray();
    }

    /// <summary>Sway across the world horizontal, which is what engine_fire sways
    /// in - not across the vent. A flame leans with the air, and the air does not
    /// know which way the grille faces.</summary>
    private static Vector3 Sideways(float sway, float turn) =>
        new(sway * MathF.Cos(turn), sway * MathF.Sin(turn), 0.0f);

    /// <summary>Where one of <paramref name="count"/> elements is in its life this
    /// lap. Advancing every one of them by 1/count a lap is what closes the loop by
    /// construction: there is no last frame to blend into the first.</summary>
    private float Age(int k, int count, int seed, float cycle)
    {
        float slot = (k + 0.5f) / count;
        float nudge = Stagger * (2.0f * Plumes.Hash01(k, seed) - 1.0f) / count;
        float age = slot + nudge + cycle;
        return age - MathF.Floor(age);
    }

    /// <summary>Up from nothing at age 0, back to nothing at age 1 - both ends
    /// exactly, or the lap shows a seam.</summary>
    private float Life(float age, float ends) =>
        (1.0f - MathF.Exp(-age / Math.Max(Onset, 1e-4f)))
        * MathF.Pow(Math.Max(1.0f - age, 0.0f), ends);

    // --- drawing -------------------------------------------------------------

    public override void _Draw()
    {
        Showing = false;
        if (Tank?.Atlas is not { } atlas || !Wanted || _shader is null)
            return;
        _white ??= White();
        ZIndex = TankSprite.ZFor(AtlasSet.FireName,
                                 atlas.OverTurretAt(AtlasSet.FireName, Tank.HullFacing));

        var body = new Godot.Collections.Array();
        var flow = new Godot.Collections.Array();
        var lifts = new Godot.Collections.Array();
        int sparksFrom = int.MaxValue;
        Rect2? box = null;
        foreach (AtlasSet.Port port in atlas.Ports)
        {
            foreach (Part part in Build(port, atlas.HullLength, Tank.FireCycle))
            {
                if (part.Gain <= 0.002f || body.Count >= MaxParts)
                    continue;
                Vector2 at = atlas.Project(part.Centre, Tank.HullFacing);
                // The long axis is projected rather than scaled, so the
                // foreshortening is the render's own: an element pointing at the
                // camera is short because its tip lands near its middle. The cross
                // section is taken as round, which is the muzzle flash's stated
                // approximation - across the flow an element is about circular, and
                // how much of that circle is vertical is not something a sprite
                // knows.
                Vector2 tip = atlas.Project(
                    part.Centre + part.Flow * (part.Radius * part.Stretch),
                    Tank.HullFacing);
                Vector2 run = tip - at;
                float along = MathF.Max(run.Length(), 0.5f);
                float wide = (float)(part.Radius / atlas.UnitsPerPixel);
                if (part.Spark)
                    sparksFrom = Math.Min(sparksFrom, body.Count);
                body.Add(new Vector4(at.X, at.Y, wide, along));
                flow.Add(new Vector4(run.X / along, run.Y / along, part.Age,
                                     part.Gain));
                // The apex rather than the fragment's own surface, which the column
                // does: an element here is a stretched ellipsoid and the shared
                // section does not hand its facing back. It costs nothing that
                // shows - a flame sits above the deck, where the column starts
                // level with it.
                lifts.Add(Plumes.LiftOf(atlas, part.Centre.Z)
                          + Plumes.BulgeOf(atlas, part.Radius));
                // An element draws past its own half-axes: the lump noise
                // multiplies the radius by up to 1 + wobble/2, so the ellipse
                // reaches 1.14 of them. Taking the half-axes alone left the quad
                // slicing the outer, fainter part of the fire - a hard-edged
                // rectangle across the flame, which reads as a missing texture
                // rather than as a clip.
                float reach = MathF.Max(wide, along) * (1.0f + Wobble * 0.5f);
                var one = new Rect2(at - Vector2.One * reach,
                                    Vector2.One * reach * 2.0f);
                box = box is null ? one : box.Value.Merge(one);
            }
        }
        if (box is null || body.Count == 0)
            return;
        // Grown for the lump: flame_facing bails past 1.5 radii, and the noise it
        // multiplies in can push an element that far.
        Rect2 quad = box.Value.Grow(2.0f);
        Extent = quad;
        Showing = true;
        if (!_told)
        {
            _told = true;
            Note = string.Format(
                "proc fire: {0} parts ({1} seat, {2} licks, {3} embers), "
                + "{4:0}x{5:0}px at ({6:0},{7:0})..({8:0},{9:0}) off the anchor",
                body.Count, SeatBlobs, Tongues, Embers, quad.Size.X, quad.Size.Y,
                quad.Position.X, quad.Position.Y, quad.End.X, quad.End.Y);
            GD.Print(Note);
        }

        _shader.SetShaderParameter("parts", body);
        _shader.SetShaderParameter("lies", flow);
        _shader.SetShaderParameter("lift", lifts);
        _shader.SetShaderParameter("count", body.Count);
        _shader.SetShaderParameter("sparks_from",
            sparksFrom == int.MaxValue ? body.Count : sparksFrom);
        _shader.SetShaderParameter("origin", quad.Position);
        _shader.SetShaderParameter("span", quad.Size);
        _shader.SetShaderParameter("level", Tank.FireDensity * Brightness);
        Plumes.SetDepth(_shader, atlas, Tank.HullFacing);

        DrawSetTransformMatrix(Tank.ShearFor(false));
        Vector2 bump = Tank.HeaveShift(false, false);
        DrawTextureRect(_white!, new Rect2(quad.Position + bump, quad.Size), false);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>
    /// The stacking, and nothing else: what one element is comes from
    /// <see cref="Stage3D.FlameInk"/>, which is the forest's text and the tank
    /// sprite's measured hue.
    ///
    /// <b>Elements stack with alpha over, and only the layer adds.</b> That is the
    /// forest's most expensive porting lesson repeated: summed instead, an
    /// element's dark lip <em>brightens</em> what is behind it, every overlap gets
    /// lighter, and the whole queue melts into one bar. Composited here, the layer
    /// is handed over premultiplied and the game adds it exactly once.
    /// </summary>
    internal const string BlazeCode = @"
shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

// Where each element is and how big: centre in the tile's px, half-width across
// the flow, half-length along it.
uniform vec4 parts[48];
// Which way it lies as a unit screen vector, where it sits on the ramp, and how
// hard it burns.
//
// Named `lies` and not `flow` because FlameInk's element takes a `flow` argument,
// and a uniform of that name is a Redefinition - which fails as a shader that did
// not compile and a material that fell back to opaque, not as an error anybody
// reads. Third time in this pair of files; see Plumes.depth_keep.
uniform vec4 lies[48];
// Its height on the map's byte scale, so the tank can stand in front of it.
uniform float lift[48];
uniform int count = 0;
// Everything from here on is an ember: same body, one flat colour, because they
// are what is left rather than what is burning.
uniform int sparks_from = 48;
uniform vec2 origin = vec2(0.0);
uniform vec2 span = vec2(1.0);
uniform float level = 1.0;

FLAME_NOISE
FLAME_INK
DEPTH_CODE

void fragment() {
    vec2 at = origin + UV * span;
    float tank = depth_at(at);
    vec4 fire = vec4(0.0);
    for (int i = 0; i < 48; i++) {
        if (i >= count) { break; }
        vec4 p = parts[i];
        vec4 f = lies[i];
        float gain = f.w * depth_keep(tank, lift[i]);
        if (gain <= 0.0) { continue; }
        vec4 e = i >= sparks_from
            ? flame_spark(at, p.xy, p.z, p.w, f.xy, float(i) + 60.0,
                          ember_colour, gain)
            : flame_blob(at, p.xy, p.z, p.w, f.xy, float(i) + 20.0, f.z, gain);
        // A sphere has two surfaces and the ray crosses both: the material
        // has backface culling off and its transparent back shown, so what
        // reaches the render is one element composited over itself. Written
        // with the operator that already means that.
        //
        // The forest's fire does not do this and does not need to - it is not
        // matching a rendered tree. Here the rendered layer is the target, and
        // without it the flame comes out a third of the fire at the same size.
        fire = flame_over(fire, flame_over(e, e));
    }
    // Already premultiplied by the compositing above, so the alpha is spent and a
    // nought is handed over: an emitting layer says what it is - light to add, and
    // nothing to hide behind. See EffectLayer.Glow.
    COLOR = vec4(fire.rgb * level, 0.0);
}
";

    /// <summary>This flame as it is compiled - the counterpart of
    /// <see cref="Stage3D.BlazingCode"/>, and read for the same reason.
    /// </summary>
    internal static readonly string BlazeShaderCode =
        BlazeCode.Replace("FLAME_NOISE", Stage3D.EmberNoiseCode)
                 .Replace("FLAME_INK", Stage3D.FlameInk)
                 .Replace("DEPTH_CODE", Plumes.DepthCode);

    private static readonly Shader Blaze = new() { Code = BlazeShaderCode };
}
