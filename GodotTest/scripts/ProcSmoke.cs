using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// A column of smoke off a vent, built here rather than read off the atlas -
/// the burning tank's and the running engine's both.
///
/// <b>One class for the two because in Blender they are one file.</b>
/// <c>engine_fire.SMOKE</c> <em>is</em> <c>exhaust_plume</c> with different
/// numbers: the fire imports the plume's path outright and drives its material
/// builder. Two transcriptions here would be the thing <see cref="Plumes"/>
/// exists to prevent, and they would part at whichever number nobody put side by
/// side. So the geometry is written once and the two arrive as
/// <see cref="Column"/> and <see cref="Plume"/>, which differ in seventeen
/// numbers, an ink and which layer's slot they draw in - and in nothing else.
///
/// <b>It is the same model, not a new one.</b> Every number below is
/// <c>engine_fire.SMOKE</c> standing on <c>exhaust_plume</c>'s geometry: puffs
/// carried along an integrated path that leaves the vent the way the vent points
/// and ends up going straight up, each one's size, wander and solidity a
/// function of its age alone. That is what closes the loop by construction - age
/// advances by 1/n per lap for every puff at once, so there is no last frame to
/// blend into the first - and it is why there is no phase table in this file.
///
/// <b>Why build it at all when the layer is already rendered.</b> Three things,
/// and memory is the weakest of them.
///
/// The board now has a burning forest drawn from this same model in a shader
/// (<see cref="Stage3D"/>), and the two disagree in a way that shows when they
/// stand in one frame: the tank's column walks twelve rendered phases and steps
/// its heading by fifteen degrees, where the tree's is continuous in both. A
/// conveyor beside a continuous one is legible as a conveyor.
///
/// Tuning it costs a twelve-minute render and a contact sheet. The forest's
/// <c>--flame</c> sweep is the same question answered by dragging a slider.
///
/// And the column is the layer that pays most for being rendered: 384px frames
/// against the tank's 256, because it is the one effect standing *above* the
/// tank - 12.6MB of the packed set and 162MB of the grid it replaced. A quad
/// sized to the effect needs none of that arithmetic; see <c>engine_fire</c> on
/// the 0.4645-of-a-tile the anchor leaves overhead, which cost one pass 28px of
/// the column's top.
///
/// <b>A canvas shader, not a spatial one, and that is not a detail.</b> The
/// forest's fire is a quad in the world, which is why it exists on the 3D board
/// only. This has to live inside the sprite's own canvas, for two reasons that
/// each decide it alone: every layer of a tank draws on the shared anchor and
/// that is the whole of what holds them together, and the hull's alpha is right
/// there to cut the column against. Being a CanvasItem it also draws on the flat
/// harness, which the forest's fire cannot.
///
/// <b>The puffs are worked out here and only composited in the shader.</b> The
/// path is one integration per frame; asked per fragment it would be ninety-six
/// steps times twenty-two puffs. So C# owns the model and the shader owns the
/// section across one puff and the stacking - which is also the split that lets
/// the model be asserted without a rendered pixel.
///
/// <b>Composited seat first, tip last.</b> Under this camera - looking down at
/// the elevation the atlas records - depth along the view runs
/// <c>Y*cos(e) - Z*sin(e)</c>, so a higher puff is a *nearer* one. Drawing the
/// tip last is the depth buffer's answer, arrived at once instead of per
/// fragment.
/// </summary>
public sealed partial class ProcSmoke : Node2D
{
    /// <summary>Most puffs the shader will composite. The model asks for
    /// twenty-two; the headroom is so a slider can be pushed past it and show
    /// what too many looks like, which is the only way twenty-two is a choice
    /// rather than a constant.</summary>
    public const int MaxPuffs = 40;

    /// <summary>
    /// Whether a bench opens with the column built rather than read.
    ///
    /// False, and not because the built one is worse. Every atlas shipped so far
    /// draws the rendered column, and almost every measurement on this bench is
    /// a pixel difference between two frames - so the pair has to start from
    /// what is shipped or the A/B has nothing on the other side of it. Named
    /// here rather than left in a field initialiser for
    /// <see cref="Recoil.ShearOnByDefault"/>'s reason: a default nobody can find
    /// is a default nobody notices changing.
    /// </summary>
    public const bool OnByDefault = false;

    /// <summary>
    /// Which layer's slot this draws in, and thereby which of the tank's two
    /// columns it is: <see cref="AtlasSet.BurnName"/> or
    /// <see cref="AtlasSet.ExhaustName"/>.
    ///
    /// The one field the rest of the class branches on, and it branches in three
    /// places only - is the effect running, where its lap position comes from,
    /// and how much of its alpha survives. Everything else is a number.
    /// </summary>
    public string Layer = AtlasSet.BurnName;

    // --- engine_fire.SMOKE, transcribed --------------------------------------
    // The default is the burning tank's column, so `new ProcSmoke()` is what it
    // always was; Plume() below overwrites them with exhaust_plume.CONFIG.
    //
    // Quoted against the hull's length exactly as that file quotes them, so one
    // set of numbers fits three tanks and whatever a slider does to their size.
    // Reading them as fractions of anything else is the mistake the forest's
    // port paid for once already.
    public float Rise = 0.45f;
    public float Spread = 0.103f;
    public float Buoyancy = 0.95f;
    public float TurnPower = 0.45f;
    public float Slowing = 1.10f;
    public float PuffStart = 0.40f;
    public float PuffGrow = 0.048f;
    public float PuffPower = 0.65f;
    public int Puffs = 22;
    public float Stagger = 0.35f;
    public float PuffVary = 0.45f;
    public float Wobble = 0.22f;
    public float Alpha = 0.50f;
    public float Onset = 0.10f;
    public float Fade = 1.25f;
    public float RimFalloff = 1.15f;
    public float Opacity = 0.80f;
    public int Seed = 91;

    /// <summary>
    /// What one surface of the column is, in the colour space it is composited
    /// in - which is the sRGB framebuffer, not the linear one the config is
    /// written for.
    ///
    /// <b>Measured off the shipped atlas rather than converted from the config,
    /// and that is the forest's lesson repeated.</b> There the same slip put a
    /// flame's green and blue out by a factor of six. Sampled over the burn
    /// layer's own pixels on MTP the body reads (72, 66, 62) and does not move
    /// by more than a level and a half between alpha 16 and alpha 210 - so the
    /// two-colour rim in <c>muzzle_flash.build_material</c>, which the smoke
    /// config gives as (0.10, 0.085, 0.075) against (0.06, 0.05, 0.045),
    /// measures flat in the render and is one ink here.
    ///
    /// The per-puff tone jitter is kept because the model has one, at the size
    /// the render can be shown to carry: a couple of levels either way.
    /// </summary>
    public Color Ink = ColumnInk;

    /// <summary>The burning tank's column: (72, 66, 62) on the shipped
    /// <c>burn_atlas</c>.</summary>
    public static readonly Color ColumnInk = new(0.282f, 0.259f, 0.243f);

    /// <summary>
    /// The idling engine's plume, measured the same way and startlingly lighter:
    /// (132, 129, 127) over <c>exhaust_atlas</c>, flat to within a level from
    /// alpha 16 to alpha 255 and identical on all three tanks.
    ///
    /// <b>Light, and still occlusive.</b> The board's ground is a pale hex field
    /// around 0.72 and this is 0.52, so it darkens - but barely, which is exactly
    /// what <c>tank_pipeline</c>'s <c>exhaust_contrast</c> has been reporting at
    /// 28 against a threshold of 28 on three tanks running. The plume is the
    /// quietest thing the pipeline ships and this ink is why.
    ///
    /// It checks out against the config rather than being taken on trust: the
    /// linear (0.28, 0.26, 0.25) there encodes to about (143, 138, 135), and the
    /// render's tonemap brings that to what is measured. The burn column's much
    /// darker config measures much darker in the same way.
    /// </summary>
    public static readonly Color PlumeInk = new(0.518f, 0.506f, 0.498f);

    public float ToneVary = 0.08f;

    /// <summary>The burning tank's column - the numbers already standing above,
    /// named so the pair reads as a pair at the call site.</summary>
    public static ProcSmoke Column() => new();

    /// <summary>
    /// The running engine's plume: <c>exhaust_plume.CONFIG</c>, transcribed.
    ///
    /// <b>Read the two side by side and the differences are the effect.</b> Rise
    /// 0.24 of a hull against the column's 0.45 - haze over the deck, not a
    /// smokestack. <c>Slowing</c> 0.45 against 1.10, because a jet gives its
    /// momentum up at once where a fire draws all the way: that is the number
    /// which crowds the plume's puffs at the tip and spaces the column's evenly.
    /// <c>TurnPower</c> 1.0 against 0.45 - exhaust really does leave along the
    /// pipe and bend up afterwards, where a fire that flies half a hull on the
    /// vent's aim reads as a jet. And <see cref="Wobble"/> 0.16 against 0.22,
    /// because this one lies over the armour for the whole game and lumps at that
    /// size stop reading as a volume in front of it and start reading as dirt on
    /// it.
    ///
    /// <b><see cref="RimFalloff"/> is the density lever, not <see cref="Alpha"/>,
    /// and that is not visible from the number.</b> Alpha goes as
    /// <c>facing**RimFalloff</c> and most of a sphere faces obliquely, so the
    /// config's own note records raising it from 1.1 to 1.6 for softness quietly
    /// taking a third of the plume with it.
    /// </summary>
    public static ProcSmoke Plume() => new()
    {
        Layer = AtlasSet.ExhaustName,
        Ink = PlumeInk,
        Rise = 0.24f,
        Spread = 0.055f,
        Buoyancy = 0.80f,
        TurnPower = 1.00f,
        Slowing = 0.45f,
        PuffStart = 0.45f,
        PuffGrow = 0.048f,
        PuffPower = 0.70f,
        Puffs = 20,
        Stagger = 0.35f,
        PuffVary = 0.45f,
        Wobble = 0.16f,
        Alpha = 0.60f,
        Onset = 0.14f,
        Fade = 1.60f,
        RimFalloff = 1.25f,
        Opacity = 0.70f,
        Seed = 33,
    };

    public TankSprite? Tank;

    private ShaderMaterial? _shader;
    private static Texture2D? _white;

    /// <summary>The quad, in px off the shared anchor. Kept so the checks and the
    /// readout can ask how big the column came out without reading pixels.</summary>
    public Rect2 Extent { get; private set; }

    /// <summary>Whether the last draw put anything up.</summary>
    public bool Showing { get; private set; }

    /// <summary>What the last draw put up, in one line - how big the column came
    /// out and how solid its densest pixel is.
    ///
    /// Printed once, the way the wall bench prints its settling line, because
    /// these are the numbers the whole thing is judged by and a picture alone
    /// cannot separate "the model is small" from "the model is not drawing".
    /// Twice in one session would be noise; once is a fact about the tank.
    /// </summary>
    public string Note { get; private set; } = "";

    private bool _told;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Linear;
        _shader = new ShaderMaterial { Shader = Section };
        Material = _shader;
        _white ??= MakeWhite();
    }

    private static Texture2D MakeWhite()
    {
        Image pixel = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        pixel.Fill(Colors.White);
        return ImageTexture.CreateFromImage(pixel);
    }

    public override void _Process(double delta)
    {
        // Redrawn while there is anything to show and once more after there is
        // not - the rule EffectLayer states: a layer that stops asking the moment
        // it has nothing never gets the chance to draw nothing.
        if (Wanted || Showing)
            QueueRedraw();
    }

    private bool Plumed => Layer == AtlasSet.ExhaustName;

    /// <summary>Whether the effect this stands in for is running at all - the
    /// burning tank's column while it burns, the engine's plume while the loop
    /// that drives the rendered layer is armed.</summary>
    /// <remarks>Keyed on <c>ExhaustPhase >= 0</c> rather than on the enable flag
    /// because that is the one place already holding every reason a plume is not
    /// showing: the effect switched off, a wreck's dead engine, an atlas with no
    /// exhaust layer. Asking those again here would be a second copy of a list
    /// that grows.</remarks>
    private bool Running => Tank is not null
        && (Plumed ? Tank.ExhaustPhase >= 0 && Tank.ProceduralExhaust
                   : Tank.Burning && Tank.ProceduralSmoke);

    /// <summary>Where round the lap this is, in [0, 1). Continuous - the whole
    /// point - where the rendered layer only has the frame it rounds to.</summary>
    private float Cycle => Tank is null ? 0.0f
        : Plumed ? Tank.ExhaustCycle : Tank.SmokeCycle;

    /// <summary>How much of the alpha survives: the engine's load for the plume,
    /// the wreck's fading for the column.</summary>
    private float Density => Tank is null ? 0.0f
        : Plumed ? Tank.ExhaustDensity : Tank.SmokeDensity;

    private bool Wanted => Running && Tank!.Atlas is { HasPorts: true }
                           && Density > 0.002f;

    // --- the model -----------------------------------------------------------

    /// <summary>One puff: where it is in the model's world, how big, how solid,
    /// and how far through its life.</summary>
    public readonly record struct Puff(Vector3 Centre, float Radius, float Alpha,
                                       float Tone, float Age);

    /// <summary>
    /// Every puff of one port at cycle position <paramref name="cycle"/> in
    /// [0, 1).
    ///
    /// <b>The stagger is hashed on the puff and never on the phase</b>, which is
    /// the one thing here that is easy to get wrong and impossible to tune your
    /// way out of: hash in the phase and every puff becomes a different puff
    /// every frame, so the column boils instead of drifting.
    /// </summary>
    public Puff[] Build(AtlasSet.Port port, double hullLength, float cycle)
    {
        int count = Math.Clamp(Puffs, 1, MaxPuffs);
        var (u, v) = Plumes.Across(port.Dir);
        float hull = (float)hullLength;
        // Negative rate: the plume's `slowing` is exp(-t/slowing), and it is the
        // one number that tells this from a flame - see Plumes.Path.
        Vector3[] path = Plumes.Path(port.Dir, hull * Rise, Buoyancy,
                                     TurnPower, -1.0f / Math.Max(Slowing, 1e-4f));
        var made = new Puff[count];
        for (int k = 0; k < count; k++)
        {
            float slot = (k + 0.5f) / count;
            float nudge = Stagger * (2.0f * Plumes.Hash01(k, Seed + 1) - 1.0f) / count;
            float age = Mod1(slot + nudge + cycle);
            float turn = MathF.Tau * Plumes.Hash01(k, Seed + 2);
            float wander = hull * Spread * MathF.Pow(age, 0.7f) * Plumes.Hash01(k, Seed + 3);
            float born = port.Radius * 0.7f * (2.0f * Plumes.Hash01(k, Seed + 4) - 1.0f);
            float size = (port.Radius * PuffStart
                          + hull * PuffGrow * MathF.Pow(age, PuffPower))
                         * (1.0f - PuffVary + 2.0f * PuffVary * Plumes.Hash01(k, Seed + 5));
            // Solidity is a function of age alone, and it has to reach zero at
            // both ends of a life, or a puff appears and vanishes with a step.
            float life = (1.0f - MathF.Exp(-age / Math.Max(Onset, 1e-4f)))
                         * MathF.Pow(Math.Max(1.0f - age, 0.0f), Fade);
            Vector3 centre = port.Point + Plumes.Along(path, age)
                             + u * (wander * MathF.Cos(turn) + born)
                             + v * (wander * MathF.Sin(turn) + born);
            made[k] = new Puff(centre, size, Math.Clamp(Alpha * life, 0.0f, 1.0f),
                               1.0f + ToneVary * (2.0f * Plumes.Hash01(k, Seed + 7) - 1.0f),
                               age);
        }
        // Seat first, tip last: higher is nearer under this camera, so the tip is
        // what the depth buffer would have put on top.
        Array.Sort(made, (a, b) => a.Age.CompareTo(b.Age));
        return made;
    }

    private static float Mod1(float x) => x - MathF.Floor(x);

    // --- drawing -------------------------------------------------------------

    public override void _Draw()
    {
        Showing = false;
        if (Tank?.Atlas is not { } atlas || !Wanted || _shader is null)
            return;
        _white ??= MakeWhite();
        // The same slot the rendered layer would take, off the same stamped flag.
        // Asked every draw and not only when it moves: z-index sorts siblings
        // ahead of tree order, so one layer using it and the rest sitting at zero
        // puts that one over everything.
        ZIndex = TankSprite.ZFor(Layer,
                                 atlas.OverTurretAt(Layer, Tank.HullFacing));

        var puffs = new Godot.Collections.Array();
        // Each puff's own height on the map's scale, so the shader can ask which
        // of the two surfaces over a pixel is nearer. Beside the circles rather
        // than packed into them because a vec4 is full.
        var lifts = new Godot.Collections.Array();
        Rect2? box = null;
        float density = Density;
        foreach (AtlasSet.Port port in atlas.Ports)
        {
            foreach (Puff puff in Build(port, atlas.HullLength, Cycle))
            {
                if (puff.Alpha <= 0.002f || puffs.Count >= MaxPuffs)
                    continue;
                Vector2 at = atlas.Project(puff.Centre, Tank.HullFacing);
                // The wobble is per-vertex lumping in the render, which a circle
                // cannot have; it is spent on the radius instead so a puff covers
                // about the same ground. Here rather than in Build because it is
                // about what a circle stands in for, not about the model.
                float radius = (float)(puff.Radius * (1.0 + Wobble * 0.5)
                                       / atlas.UnitsPerPixel);
                var one = new Rect2(at - Vector2.One * radius,
                                    Vector2.One * radius * 2.0f);
                box = box is null ? one : box.Value.Merge(one);
                puffs.Add(new Vector4(at.X, at.Y, radius,
                                      puff.Alpha * puff.Tone * density));
                lifts.Add(new Vector2(Plumes.LiftOf(atlas, puff.Centre.Z),
                                      Plumes.BulgeOf(atlas, puff.Radius)));
            }
        }
        if (box is null || puffs.Count == 0)
            return;
        // Grown by a pixel so the outermost puff's own antialiased edge is inside
        // the quad rather than clipped by it.
        Rect2 quad = box.Value.Grow(1.0f);
        Extent = quad;
        Showing = true;
        if (!_told)
        {
            _told = true;
            float peak = 0.0f;
            foreach (Variant one in puffs)
                peak = Math.Max(peak, one.AsVector4().W);
            Note = string.Format(
                "proc " + Layer + ": {0} puffs, {1:0}x{2:0}px at ({3:0},{4:0})..({5:0},{6:0}) "
                + "off the anchor, peak puff alpha {7:0.00}, hull {8:0.000}, "
                + "port r {9:0.000}",
                puffs.Count, quad.Size.X, quad.Size.Y, quad.Position.X,
                quad.Position.Y, quad.End.X, quad.End.Y, peak, atlas.HullLength,
                atlas.Ports.Count > 0 ? atlas.Ports[0].Radius : 0.0f);
            GD.Print(Note);
        }

        _shader.SetShaderParameter("puffs", puffs);
        _shader.SetShaderParameter("lift", lifts);
        _shader.SetShaderParameter("count", puffs.Count);
        _shader.SetShaderParameter("origin", quad.Position);
        _shader.SetShaderParameter("span", quad.Size);
        _shader.SetShaderParameter("ink", new Vector3(Ink.R, Ink.G, Ink.B));
        _shader.SetShaderParameter("rim_falloff", RimFalloff);
        _shader.SetShaderParameter("opacity", Opacity);
        Plumes.SetDepth(_shader, atlas, Tank.HullFacing);

        DrawSetTransformMatrix(Tank.ShearFor(false));
        Vector2 bump = Tank.HeaveShift(false, false);
        DrawTextureRect(_white!, new Rect2(quad.Position + bump, quad.Size), false);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>
    /// The section across one puff and the stacking of them, and nothing else -
    /// the model is worked out in C# and arrives as a list of circles.
    ///
    /// <b>The exponent is on the facing, not on the radius.</b> Layer Weight's
    /// Facing is <c>sqrt(1 - r^2)</c> for a sphere, so writing
    /// <c>pow(1 - r*r, falloff)</c> - which the forest's port did - is the
    /// exponent applied twice, and every element comes out half as soft and a
    /// size smaller than it was asked for.
    ///
    /// <b>Alpha-over inside the layer, never addition.</b> The column is drawn
    /// with ordinary blending because it <em>takes</em> light away - that is the
    /// whole of what it does for the flame - so the puffs stack the way surfaces
    /// stack. The flame is the layer where this goes the other way.
    ///
    /// <b>The colour is unpremultiplied on the way out.</b> Compositing has to
    /// happen premultiplied or a thin puff over a solid one drags the solid one's
    /// colour toward nothing; Godot's blend_mix expects straight, so the divide
    /// puts it back.
    /// </summary>
    /// <summary>This column as it is compiled, so the checks can read what the
    /// shader actually got rather than what the template says.</summary>
    internal static readonly string ColumnShaderCode =
        ColumnCode.Replace("DEPTH_CODE", Plumes.DepthCode);

    /// <summary>One shader for both configurations - it is the section across a
    /// puff and the stacking of them, and neither depends on which vent the puffs
    /// came out of. Named for what it draws rather than for either column, which
    /// the text around it still is.</summary>
    private static readonly Shader Section = new() { Code = ColumnShaderCode };

    /// <summary>The shader's text, so the three claims that fail into a
    /// deliberate-looking picture can be asserted rather than remembered -
    /// <see cref="EffectLayer.Glow"/>'s reason.</summary>
    internal const string ColumnCode = @"
shader_type canvas_item;
render_mode blend_mix, unshaded;

uniform vec4 puffs[40];
// Each puff's height on the map's byte scale, as (centre, bulge): how high its
// middle is and how much higher its near surface gets at the fragment facing the
// eye most. See Plumes.depth_keep.
uniform vec2 lift[40];
uniform int count = 0;
uniform vec2 origin = vec2(0.0);
uniform vec2 span = vec2(1.0);
uniform vec3 ink = vec3(0.28, 0.26, 0.24);
uniform float rim_falloff = 1.15;
uniform float opacity = 0.80;

DEPTH_CODE
void fragment() {
    // Back out of the quad into the tile's own pixels, which is the space every
    // puff arrived in and the space the hull's frame is addressed in.
    vec2 at = origin + UV * span;

    // Once for every element that will ask.
    float tank = depth_at(at);

    float cover = 0.0;
    vec3 body = vec3(0.0);
    for (int i = 0; i < 40; i++) {
        if (i >= count) { break; }
        vec4 puff = puffs[i];
        float r = length(at - puff.xy) / max(puff.z, 0.0001);
        if (r < 1.0) {
            float facing = sqrt(max(1.0 - r * r, 0.0));
            float one = pow(facing, rim_falloff) * puff.w * opacity;
            // <b>One surface per element, and the first pass had two.</b> The
            // material does ask for backfaces - build_material sets
            // use_backface_culling False - but it asks for the second surface
            // behind a hasattr guard on show_transparent_back, and on the
            // Blender these atlases came off that attribute is gone: EEVEE Next
            // dropped Show Backface, and a blended surface composites its front
            // only. So what the render actually put down is one surface, and
            // doubling it here was the model being corrected toward a material
            // setting the renderer ignored.
            //
            // Measured against the shipped layers, three tanks, driving, median
            // levels the layer moves the picture by against no plume at all:
            //
            //   render 16.0 / 22.0 / 20.0
            //   single 14.0 / 22.0 / 19.0     doubled 24.0 / 37.0 / 33.0
            //
            // and the column the same way, as departure from its own render:
            // single 7 / 6 / 6 against doubled 8 / 8 / 7. The earlier thin-wisp
            // reading compared two built columns to each other, not either of
            // them to the render.
            float a = one;
            a *= depth_keep(tank, lift[i].x + lift[i].y * facing);
            // Seat first, tip last - the list arrives sorted, so this is the
            // depth buffer's answer without a depth buffer.
            body = body * (1.0 - a) + ink * a;
            cover = cover + a * (1.0 - cover);
        }
    }
    COLOR = vec4(body / max(cover, 0.0001), cover);
}
";
}
