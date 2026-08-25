using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The burning tank's smoke column, built here rather than read off the atlas.
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

    // --- engine_fire.SMOKE, transcribed --------------------------------------
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
    public static readonly Color Ink = new(0.282f, 0.259f, 0.243f);

    public float ToneVary = 0.08f;

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
        _shader = new ShaderMaterial { Shader = Column };
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

    private bool Wanted => Tank is { Burning: true, ProceduralSmoke: true }
                           && Tank.Atlas is { HasPorts: true }
                           && Tank.SmokeDensity > 0.002f;

    // --- the model -----------------------------------------------------------

    /// <summary>
    /// Deterministic values in [0, 1) from two integers - <c>muzzle_flash._hash01</c>
    /// with its two keys, so the arrangement is stable between runs and between a
    /// run and the screenshot it is compared against.
    /// </summary>
    /// <remarks>The arrangement it produces is <em>a</em> draw of the same dice
    /// rather than the render's own, because the two hashes differ in how they
    /// fold the second key. Worth saying out loud: the A/B against the rendered
    /// layer is a comparison of two arrangements of one model, not of one
    /// arrangement drawn two ways.</remarks>
    public static float Hash01(int index, int seed)
    {
        unchecked
        {
            ulong x = (ulong)(long)index * 0x9E3779B97F4A7C15UL;
            x ^= (ulong)(long)seed * (0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
            x ^= x >> 29;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 32;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>
    /// Where a puff is at each age, in world units off the port.
    ///
    /// The direction turns from the vent's axis toward world up as the puff ages
    /// and the speed decays, so the result is a curve that leaves the port the
    /// way the port points and ends up climbing. Swinging a straight line instead
    /// would move the <em>whole</em> plume, the part still in the pipe included.
    ///
    /// Normalised on the end <em>point</em>, so <see cref="Rise"/> is how far the
    /// tip gets from the port rather than how far it travelled getting there.
    /// </summary>
    /// <remarks>Integrated and then sampled, exactly as
    /// <c>exhaust_plume._path</c> does. The closed form the forest's flame uses
    /// is not available here: that axis is vertical, and this one points back and
    /// up at anywhere from 49 to 72 degrees depending on the tank.</remarks>
    public static Vector3[] Path(Vector3 axis, float reach, float buoyancy,
                                 float turnPower, float slowing, int samples = 96)
    {
        var pos = new Vector3[samples];
        var run = Vector3.Zero;
        for (int i = 0; i < samples; i++)
        {
            float t = samples <= 1 ? 0.0f : i / (float)(samples - 1);
            float turn = Math.Clamp(buoyancy * MathF.Pow(t, turnPower), 0.0f, 1.0f);
            Vector3 dir = axis * (1.0f - turn) + Up * turn;
            float len = dir.Length();
            if (len > 1e-9f)
                dir /= len;
            run += dir * MathF.Exp(-t / Math.Max(slowing, 1e-4f));
            pos[i] = run;
        }
        Vector3 zero = pos[0];
        for (int i = 0; i < samples; i++)
            pos[i] -= zero;
        float span = pos[samples - 1].Length();
        if (span > 1e-9f)
            for (int i = 0; i < samples; i++)
                pos[i] *= reach / span;
        return pos;
    }

    /// <summary>World up in the model's own frame. Not Godot's up: these are
    /// Blender coordinates, the ones the port is stamped in, where z is
    /// height.</summary>
    public static readonly Vector3 Up = new(0.0f, 0.0f, 1.0f);

    private static Vector3 Along(Vector3[] path, float age)
    {
        float at = Math.Clamp(age, 0.0f, 1.0f) * (path.Length - 1);
        int lo = Math.Clamp((int)at, 0, path.Length - 2);
        return path[lo].Lerp(path[lo + 1], at - lo);
    }

    /// <summary>Two unit vectors across <paramref name="axis"/> - the pair
    /// <c>muzzle_flash._perpendicular</c> builds, by the same rule: the seed
    /// swaps away from up when the axis is nearly up, or the cross product
    /// vanishes.</summary>
    public static (Vector3 U, Vector3 V) Across(Vector3 axis)
    {
        Vector3 seed = MathF.Abs(axis.Z) > 0.9f ? new Vector3(1.0f, 0.0f, 0.0f) : Up;
        Vector3 u = seed.Cross(axis).Normalized();
        return (u, axis.Cross(u));
    }

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
        var (u, v) = Across(port.Dir);
        float hull = (float)hullLength;
        Vector3[] path = Path(port.Dir, hull * Rise, Buoyancy, TurnPower, Slowing);
        var made = new Puff[count];
        for (int k = 0; k < count; k++)
        {
            float slot = (k + 0.5f) / count;
            float nudge = Stagger * (2.0f * Hash01(k, Seed + 1) - 1.0f) / count;
            float age = Mod1(slot + nudge + cycle);
            float turn = MathF.Tau * Hash01(k, Seed + 2);
            float wander = hull * Spread * MathF.Pow(age, 0.7f) * Hash01(k, Seed + 3);
            float born = port.Radius * 0.7f * (2.0f * Hash01(k, Seed + 4) - 1.0f);
            float size = (port.Radius * PuffStart
                          + hull * PuffGrow * MathF.Pow(age, PuffPower))
                         * (1.0f - PuffVary + 2.0f * PuffVary * Hash01(k, Seed + 5));
            // Solidity is a function of age alone, and it has to reach zero at
            // both ends of a life, or a puff appears and vanishes with a step.
            float life = (1.0f - MathF.Exp(-age / Math.Max(Onset, 1e-4f)))
                         * MathF.Pow(Math.Max(1.0f - age, 0.0f), Fade);
            Vector3 centre = port.Point + Along(path, age)
                             + u * (wander * MathF.Cos(turn) + born)
                             + v * (wander * MathF.Sin(turn) + born);
            made[k] = new Puff(centre, size, Math.Clamp(Alpha * life, 0.0f, 1.0f),
                               1.0f + ToneVary * (2.0f * Hash01(k, Seed + 7) - 1.0f),
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
        ZIndex = TankSprite.ZFor(AtlasSet.BurnName,
                                 atlas.OverTurretAt(AtlasSet.BurnName, Tank.HullFacing));

        var puffs = new Godot.Collections.Array();
        // Each puff's own height on the map's scale, so the shader can ask which
        // of the two surfaces over a pixel is nearer. Beside the circles rather
        // than packed into them because a vec4 is full.
        var lifts = new Godot.Collections.Array();
        Rect2? box = null;
        float density = Tank.SmokeDensity;
        foreach (AtlasSet.Port port in atlas.Ports)
        {
            foreach (Puff puff in Build(port, atlas.HullLength, Tank.SmokeCycle))
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
                lifts.Add(LiftOf(atlas, puff.Centre.Z));
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
                "proc smoke: {0} puffs, {1:0}x{2:0}px at ({3:0},{4:0})..({5:0},{6:0}) "
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
        SetDepthMask(atlas);

        DrawSetTransformMatrix(Tank.ShearFor(false));
        Vector2 bump = Tank.HeaveShift(false, false);
        DrawTextureRect(_white!, new Rect2(quad.Position + bump, quad.Size), false);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>Where a world height sits on the height map's own byte scale, in
    /// [0, 1]. Clamped rather than allowed to go negative, which is what a puff
    /// under the belts would be.</summary>
    public static float LiftOf(AtlasSet atlas, float z)
    {
        double span = atlas.HeightHigh - atlas.HeightLow;
        return span <= 0.0 ? 0.0f
            : (float)Math.Clamp((z - atlas.HeightLow) / span, 0.0, 1.0);
    }

    /// <summary>
    /// Hand the shader the tank's height map at this heading, so the column can
    /// be cut where the tank is in front of it.
    ///
    /// <b>This is the holdout, and it is exact rather than a stand-in.</b> The
    /// rendered layer has the hull punched out of its alpha - <c>hull_only</c> in
    /// <c>tank_pipeline</c> - which is a depth test done at render time. The first
    /// pass here did it against the hull's <em>alpha</em> instead, and that is a
    /// different question: it cuts the column wherever the hull's silhouette is,
    /// in front or behind. Measured on four headings, it took the haze off the
    /// deck that the rendered column legitimately puts there.
    ///
    /// Height answers it exactly. At a fixed screen row this camera has
    /// <c>screen_y = -(Y*sin(e) + Z*cos(e))</c> and depth <c>Y*cos(e) - Z*sin(e)</c>;
    /// eliminating Y between them leaves <c>depth = const - Z/sin(e)</c>. So at
    /// one pixel higher is nearer, full stop - and comparing the map's height
    /// against the puff's own <em>is</em> comparing depth. Which is also why the
    /// puffs composite seat first: the same fact, used once per layer instead of
    /// once per fragment.
    ///
    /// The map covers the hull and the belts rather than the hull alone, so this
    /// holds out marginally more than the render does. That is the better answer
    /// and not a discrepancy: a belt in front of the column hides it too.
    /// </summary>
    private void SetDepthMask(AtlasSet atlas)
    {
        if (_shader is null || Tank is null || !atlas.HasHeights
            || !atlas.Has(AtlasSet.HeightName))
        {
            _shader?.SetShaderParameter("depth_on", false);
            return;
        }
        int frame = atlas.EffectFrame(AtlasSet.HeightName, 0, Tank.HullFacing);
        Rect2 region = atlas.Region(AtlasSet.HeightName, frame);
        Vector2 size = atlas.SizeOf(AtlasSet.HeightName, frame);
        if (size.X <= 0.0f || size.Y <= 0.0f)
        {
            _shader.SetShaderParameter("depth_on", false);
            return;
        }
        Texture2D texture = atlas.Texture(AtlasSet.HeightName);
        _shader.SetShaderParameter("depth_on", true);
        _shader.SetShaderParameter("depth_tex", texture);
        _shader.SetShaderParameter("depth_size",
            new Vector2(texture.GetWidth(), texture.GetHeight()));
        _shader.SetShaderParameter("depth_rect",
            new Vector4(region.Position.X, region.Position.Y, size.X, size.Y));
        // Its own anchor, not the hull's: the height layer is rendered in a tile
        // of its own size and the anchor scales with the tile - the trap the
        // effect layers already pay attention to.
        _shader.SetShaderParameter("depth_shift",
            atlas.AnchorOf(AtlasSet.HeightName)
            - atlas.OffsetOf(AtlasSet.HeightName, frame));
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
    private static readonly Shader Column = new() { Code = ColumnCode };

    /// <summary>The shader's text, so the three claims that fail into a
    /// deliberate-looking picture can be asserted rather than remembered -
    /// <see cref="EffectLayer.Glow"/>'s reason.</summary>
    internal const string ColumnCode = @"
shader_type canvas_item;
render_mode blend_mix, unshaded;

uniform vec4 puffs[40];
// Each puff's height on the map's byte scale, so a pixel can be told which of
// the two surfaces over it is nearer.
uniform float lift[40];
uniform int count = 0;
uniform vec2 origin = vec2(0.0);
uniform vec2 span = vec2(1.0);
uniform vec3 ink = vec3(0.28, 0.26, 0.24);
uniform float rim_falloff = 1.15;
uniform float opacity = 0.80;

// The tank's height map, so the column can be cut where the tank is in front of
// it. Nearest rather than linear: the byte is a height, and blending two of them
// across a silhouette edge invents a surface halfway between the tank and
// nothing.
uniform bool depth_on = false;
uniform sampler2D depth_tex : filter_nearest;
uniform vec2 depth_size = vec2(1.0);
uniform vec4 depth_rect = vec4(0.0);
uniform vec2 depth_shift = vec2(0.0);
// How softly the cut is made, on the map's own scale. Small: it is there to stop
// the silhouette's edge stepping, not to blur the tank.
uniform float depth_band = 0.02;

void fragment() {
    // Back out of the quad into the tile's own pixels, which is the space every
    // puff arrived in and the space the hull's frame is addressed in.
    vec2 at = origin + UV * span;

    // What the tank's own surface is doing over this pixel, once for every puff
    // that will ask: -1 where the tank is not drawn at all.
    float tank = -1.0;
    if (depth_on) {
        vec2 dp = at + depth_shift;
        if (dp.x >= 0.0 && dp.x <= depth_rect.z
            && dp.y >= 0.0 && dp.y <= depth_rect.w) {
            float code = texture(depth_tex, (depth_rect.xy + dp) / depth_size).r;
            // 0 is no tank; the rest is the range mapped onto 1..255.
            tank = code > 0.002 ? (code * 255.0 - 1.0) / 254.0 : -1.0;
        }
    }

    float cover = 0.0;
    vec3 body = vec3(0.0);
    for (int i = 0; i < 40; i++) {
        if (i >= count) { break; }
        vec4 puff = puffs[i];
        float r = length(at - puff.xy) / max(puff.z, 0.0001);
        if (r < 1.0) {
            float facing = sqrt(max(1.0 - r * r, 0.0));
            float one = pow(facing, rim_falloff) * puff.w * opacity;
            // A puff is a sphere, and a ray through a sphere crosses it twice:
            // the material has backface culling off and its transparent back
            // shown, so both surfaces blend. Facing is the same on the two -
            // it goes by |N.V| - so the pair comes to 1-(1-p)^2, and leaving it
            // out is what made the first pass a thin wisp beside the rendered
            // column at the same size. Measured: peak puff alpha 0.35 either
            // way, and the stack under it 0.48 rather than 0.28.
            float a = 1.0 - (1.0 - one) * (1.0 - one);
            if (tank >= 0.0) {
                a *= 1.0 - smoothstep(lift[i] - depth_band,
                                      lift[i] + depth_band, tank);
            }
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
