using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The muzzle flash, built here rather than read off the atlas -
/// <c>muzzle_flash.CONFIG</c>, transcribed.
///
/// <b>One primitive does all of it, and that is a fact about the camera rather
/// than a simplification.</b> The render is a spindle of revolution about the
/// bore, four nested shells of it, plus tapered tongues and sparks splaying
/// forward - and every one of those is a body of revolution about some axis. Under
/// an orthographic camera the direction perpendicular to both an axis and the
/// view lies wholly in the image plane, so a radius measured there projects at
/// <em>full length</em>: the silhouette's half-width across the drawn axis is the
/// world radius, unforeshortened, and the facing at a fragment offset <c>d</c>
/// into it is <c>sqrt(1 - (d/r)^2)</c> exactly, the same section
/// <see cref="ProcSmoke"/> uses for a sphere. Only the length along the axis
/// foreshortens, and <see cref="AtlasSet.Project"/> already does that. So the
/// whole flash is tapered ribbons between projected endpoints.
///
/// <b>The colour is nearly one paint, and that is measured.</b> The config's ramp
/// runs five stops from a near-white 1.00/0.97/0.73 down to a dark 0.62/0.26/0.09,
/// and none of that reaches the atlas: across the 2nd to 98th percentile of every
/// lit pixel on <c>LTP</c> and <c>MTP</c> the red barely moves (174 to 189) while
/// G/R runs 0.803 to 0.901 and B/R 0.622 to 0.775. Blender's tone map flattens the
/// ramp into one warm cream, exactly as it does to the burning tank's - which is
/// already written down for the forest's fire. So the ramp here spans that paint's
/// own measured spread, hot end to cool, and nothing wider.
///
/// <b>The strengths are <em>not</em> measured, and the split is the point.</b>
/// What the tone map flattened is colour; alpha went through untouched, so the
/// config's five strength stops (1.00, 0.95, 0.78, 0.46, 0.12) are carried as
/// written. Taking both off the pixels would be measuring one quantity twice and
/// taking neither would be guessing at the one it cannot flatten.
///
/// <b>Nothing occludes it - see <see cref="ProcFume"/>.</b> The muzzle stands
/// 0.097 to 0.137 world above the height map's ceiling on all three tanks, and
/// the rendered flash covers 0.0 to 0.9 percent of its own pixels with the
/// turret. The depth test is wired, measures as a no-op, and is kept for the
/// reason given there.
/// </summary>
public sealed partial class ProcFlash : Node2D
{
    /// <summary>Shells of the body. Four in the model; the headroom is so a
    /// slider can be pushed past it.</summary>
    public const int MaxShells = 8;

    /// <summary>Tongues and sparks together. The model asks for 8 and 9.</summary>
    public const int MaxStreaks = 24;

    // --- muzzle_flash.CONFIG, transcribed ------------------------------------
    // Everything is in bore radii, which is what lets one config fit three
    // tanks - see stamp_bore.py on why the radius had to be stamped at all.

    /// <summary>Overall size, over the phase table.</summary>
    public float Scale = 1.35f;

    /// <summary>Overall brightness, over the phase table. Past about 1.5 this
    /// stops buying brightness and starts buying white: alpha clips at 1 and an
    /// additive layer turns any overlap of bright pixels white on its own.
    /// </summary>
    public float Brightness = 1.40f;

    /// <summary>Length of the body, in bore radii.</summary>
    public float Length = 6.0f;

    /// <summary>Half-width of the body at its widest, in bore radii. It has to be
    /// the biggest thing on screen or the tongues take over and it reads as a
    /// firework rather than as burning gas.</summary>
    public float Width = 3.2f;

    /// <summary>How far back the base starts, so the mesh's closed end never
    /// shows outside the brake.</summary>
    public float Recess = 0.6f;

    /// <summary>The Beta profile <c>t^a (1-t)^b</c>. A flash is not a cone: from
    /// the side it swells just past the muzzle and tapers to a point, and the
    /// peak lands at <c>a/(a+b)</c> - a third of the way out.</summary>
    public Vector2 Profile = new(0.45f, 0.90f);

    /// <summary>Petal count, depth, sharpness and per-spike jitter. An odd count
    /// avoids the mirror symmetry that reads as machined; <c>|cos|</c> to a power
    /// gives points rather than the soft scallops a plain cosine gives at any
    /// depth.</summary>
    public Vector4 Petal = new(9.0f, 0.20f, 1.6f, 0.35f);

    /// <summary>Radians of twist from base to tip, so the spikes are not parallel
    /// ridges.</summary>
    public float Twist = 0.55f;

    public int Seed = 7;

    /// <summary>
    /// Length, width and emission per phase, as fractions of the peak.
    ///
    /// <b>The shape peaks at phases 2-3 while the light peaks at phase 0, and
    /// that gap is the effect</b> - the flash is brightest the instant it leaves
    /// the barrel and is still growing as it dims, which is what makes it read as
    /// expanding gas rather than as a lamp switched on.
    /// </summary>
    public static readonly Vector3[] Phases =
    {
        new(0.42f, 0.40f, 1.00f), new(0.80f, 0.76f, 1.00f),
        new(1.00f, 1.00f, 0.92f), new(1.02f, 1.00f, 0.78f),
        new(0.86f, 0.81f, 0.54f), new(0.74f, 0.72f, 0.32f),
        new(0.62f, 0.59f, 0.15f), new(0.30f, 0.25f, 0.04f),
    };

    /// <summary>Nested copies of the body: width and length as fractions of the
    /// full size, and a gain on the strength. Translucent shells accumulate into
    /// a gradient where one surface would draw its own silhouette crisply and
    /// read as a lumpy solid. Every gain is at or under 1 so the sum has headroom
    /// to be the hot part rather than clipping.</summary>
    public static readonly Vector3[] Shells =
    {
        new(1.00f, 1.00f, 0.62f), new(0.72f, 0.86f, 0.80f),
        new(0.46f, 0.62f, 0.94f), new(0.26f, 0.32f, 1.00f),
    };

    /// <summary>Strength along the body: position on the profile against how much
    /// alpha there is. Carried from the config rather than measured - see the
    /// class note on why colour is measured and this is not.</summary>
    public static readonly Vector2[] Stops =
    {
        new(0.00f, 1.00f), new(0.10f, 0.95f), new(0.28f, 0.78f),
        new(0.60f, 0.46f), new(1.00f, 0.12f),
    };

    /// <summary>The hot end of the measured paint, at the base of the body: the
    /// 98th percentile of the shipped flash's own spread.</summary>
    public Color Hot = new(0.706f, 0.706f * 0.901f, 0.706f * 0.775f);

    /// <summary>The cool end, at the tip: the 2nd percentile of the same
    /// spread.</summary>
    public Color Cool = new(0.706f, 0.706f * 0.803f, 0.706f * 0.622f);

    /// <summary>Fat tapered tongues of flame splaying forward - flame mass, not
    /// needles. Thirteen of them at 0.4 radii thick came out a sparkler: a spray
    /// of bright pins with no body behind it. Fewer and much fatter, so each one
    /// reads as a lobe of fire.</summary>
    public Streaks Tongues = new()
    {
        Count = 8, Cone = 50.0f, Length = new Vector2(0.35f, 0.78f),
        Radius = 1.05f, Start = new Vector2(0.05f, 0.70f), Gain = 0.85f,
        Warm = 0.15f, Seed = 40,
    };

    /// <summary>Thinner, longer, brighter, and they outlive the flame - on the
    /// reference they are still flying three frames after the fire has gone.
    /// They start well out because bunched at the origin they add their
    /// brightness where the core already clips.</summary>
    public Streaks Sparks = new()
    {
        Count = 9, Cone = 26.0f, Length = new Vector2(0.45f, 0.95f),
        Radius = 0.085f, Start = new Vector2(1.2f, 3.0f), Gain = 0.70f,
        Warm = 0.85f, Seed = 70,
    };

    /// <summary>How long the sparks last and how far out they have got, per
    /// phase. Both outrun the fire, which is the whole of what a spark is.
    /// </summary>
    public static readonly float[] SparkLife =
        { 0.55f, 1.00f, 1.00f, 0.95f, 0.84f, 0.68f, 0.48f, 0.28f };

    public static readonly float[] SparkReach =
        { 0.45f, 0.80f, 1.00f, 1.08f, 1.16f, 1.24f, 1.32f, 1.40f };

    /// <summary>One family of streaks. Both are the same shape and differ only in
    /// how fat, how far, what colour and how bright.</summary>
    public struct Streaks
    {
        public int Count;
        /// <summary>Half-angle of the cone they splay into, in degrees.</summary>
        public float Cone;
        /// <summary>Length, as a fraction of the body's.</summary>
        public Vector2 Length;
        /// <summary>Thickness at the base, in bore radii.</summary>
        public float Radius;
        /// <summary>How far out they begin, in bore radii.</summary>
        public Vector2 Start;
        public float Gain;
        /// <summary>Where on the measured paint they sit: 0 is the cool end, 1
        /// the hot one. The config gives tongues an orange and sparks a pale
        /// yellow, which is this ordering; the paint itself is the atlas'.
        /// </summary>
        public float Warm;
        public int Seed;
    }

    /// <summary>Alpha goes as <c>facing**RimFalloff</c>. Layer Weight's Facing is
    /// 1 head-on and 0 at grazing, so a power of it melts the rim while the
    /// middle stays bright.</summary>
    public float RimFalloff = 1.35f;

    /// <summary>How solid one surface is head-on, before the shells stack. Kept
    /// under 1 so the shells behind still show through; at 1.0 the outermost
    /// becomes a wall and the volume inside it is wasted.</summary>
    public float Opacity = 0.95f;

    /// <summary>
    /// The thinnest a ribbon is allowed to be drawn, in px. Anything under it is
    /// widened to it and dimmed by however much it was widened.
    ///
    /// <b>A tube thinner than a pixel does not draw faint, it draws as
    /// scratches.</b> The sparks are 0.085 bore radii, which on the medium is
    /// 0.65px across: a fragment centre lands inside one only now and then, so
    /// what reaches the screen is a dotted line rather than a dim one. This is
    /// <c>hit_point</c>'s finding word for word - a 1.15px tube came out a solid
    /// silhouette and the sparks did not draw at all, 35px of flash where the
    /// numbers promised 63 - and it is the reason that effect moved its mass off
    /// needles and onto blobs.
    ///
    /// Widening and dimming by the same factor keeps the integral: a ribbon of
    /// radius r at alpha a covers the same light as one of radius R at
    /// <c>a*r/R</c>, which is what antialiasing would have done had there been
    /// anything to antialias. Set at 0.75 rather than 1.0 because a ribbon is
    /// sampled at fragment centres, so half a pixel each side is already the
    /// point at which it stops being missed.
    /// </summary>
    public float Thinnest = 0.75f;

    public TankSprite? Tank;

    private ShaderMaterial? _shader;
    private static Texture2D? _white;

    /// <summary>The quad, in px off the shared anchor.</summary>
    public Rect2 Extent { get; private set; }

    public bool Showing { get; private set; }

    public string Note { get; private set; } = "";

    private bool _told;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Linear;
        _shader = new ShaderMaterial { Shader = Blaze };
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
        if (Wanted || Showing)
            QueueRedraw();
    }

    private bool Wanted => Tank is { ShotPhase: >= 0 } tank
                           && tank.ActiveSource == FlashSource.Built
                           && tank.Atlas is { HasBore: true };

    // --- the model -----------------------------------------------------------

    /// <summary>One tapered ribbon: two ends in the bore's own frame, the radius
    /// at the first, and how hard it burns.</summary>
    public readonly record struct Lick(Vector3 From, Vector3 To, float Thick,
                                       float Gain, float Warm);

    /// <summary>Where a world height sits on the height map's byte scale,
    /// <em>unclamped</em>.
    ///
    /// <see cref="Plumes.LiftOf"/> clamps into [0, 1], which is right for a puff
    /// that might be under the belts and wrong here: everything about a shot is
    /// over the map's ceiling, so clamping lands it on exactly 1 - and
    /// <c>depth_keep</c> at 1 is a smoothstep evaluated at its own midpoint,
    /// which removes half of it wherever the hull reaches its own tallest pixel.
    /// A number that says "above" has to be allowed to say how far.</summary>
    public static float Above(AtlasSet atlas, float z)
    {
        double span = atlas.HeightHigh - atlas.HeightLow;
        return span <= 0.0 ? 1.0f : (float)((z - atlas.HeightLow) / span);
    }

    /// <summary>The peak of <c>t^a (1-t)^b</c>, which the profile is normalised
    /// by so the widest ring is exactly <see cref="Width"/>.</summary>
    public static float ProfilePeak(Vector2 profile)
    {
        float a = profile.X, b = profile.Y;
        float at = a / Math.Max(a + b, 1e-4f);
        return MathF.Pow(at, a) * MathF.Pow(1.0f - at, b);
    }

    /// <summary>
    /// Every streak of one family at <paramref name="phase"/>, in the bore's own
    /// frame with the muzzle at the origin.
    ///
    /// <b>The tilt goes as the square root of the hash</b>, which spreads the
    /// directions evenly over the cone's cap. Hashed linearly they crowd the axis
    /// and every streak lands on top of the body.
    /// </summary>
    public static Lick[] Splay(Streaks spec, AtlasSet.Port bore, float body,
                               float reach, float gain)
    {
        int count = Math.Max(spec.Count, 0);
        if (count == 0 || gain <= 0.0f)
            return Array.Empty<Lick>();
        var (u, v) = Plumes.Across(bore.Dir);
        float cone = Mathf.DegToRad(spec.Cone);
        var made = new Lick[count];
        for (int k = 0; k < count; k++)
        {
            float tilt = cone * MathF.Sqrt(Plumes.Hash01(k, spec.Seed + 1));
            float turn = MathF.Tau * Plumes.Hash01(k, spec.Seed + 2);
            Vector3 dir = bore.Dir * MathF.Cos(tilt)
                          + (u * MathF.Cos(turn) + v * MathF.Sin(turn))
                            * MathF.Sin(tilt);
            float len = body * reach
                        * Mathf.Lerp(spec.Length.X, spec.Length.Y,
                                     Plumes.Hash01(k, spec.Seed + 3));
            float thick = bore.Radius * spec.Radius
                          * (0.6f + 0.8f * Plumes.Hash01(k, spec.Seed + 4));
            float start = bore.Radius
                          * Mathf.Lerp(spec.Start.X, spec.Start.Y,
                                       Plumes.Hash01(k, spec.Seed + 5));
            made[k] = new Lick(dir * start, dir * (start + len), thick,
                               spec.Gain * gain, spec.Warm);
        }
        return made;
    }

    /// <summary>Both families for a phase, sparks after tongues so the shader can
    /// tell them apart by index the way <see cref="ProcFire"/> does.</summary>
    public Lick[] Build(AtlasSet.Port bore, int phase)
    {
        int at = Math.Clamp(phase, 0, Phases.Length - 1);
        Vector3 step = Phases[at];
        float body = bore.Radius * Length * Scale * step.X;
        float glow = step.Z * Brightness;
        var all = new List<Lick>(MaxStreaks);
        all.AddRange(Splay(Tongues, bore, body, 1.0f, glow));
        all.AddRange(Splay(Sparks, bore, body, SparkReach[at],
                           SparkLife[at] * Brightness));
        if (all.Count > MaxStreaks)
            all.RemoveRange(MaxStreaks, all.Count - MaxStreaks);
        return all.ToArray();
    }

    // --- drawing -------------------------------------------------------------

    public override void _Draw()
    {
        Showing = false;
        if (Tank?.Atlas is not { } atlas || !Wanted || _shader is null)
            return;
        _white ??= MakeWhite();
        AtlasSet.Port bore = atlas.Bore;
        double turret = Tank.TurretFacing;
        int phase = Math.Clamp(Tank.ShotPhase, 0, Phases.Length - 1);
        Vector3 step = Phases[phase];
        ZIndex = TankSprite.ZFor("flash", ProcFume.OverTurret(atlas, bore, turret));

        // Measured, not projected - ProcFume's note on the pivot.
        Vector2 muzzle = atlas.Muzzle(atlas.FrameFor(turret)) - atlas.Anchor;
        float upp = (float)atlas.UnitsPerPixel;

        float body = bore.Radius * Length * Scale * step.X;
        float wide = bore.Radius * Width * Scale * step.Y;
        float glow = step.Z * Brightness;
        // The base sits back inside the brake, so the closed end never shows.
        Vector3 root = bore.Dir * (-bore.Radius * Recess * Scale);
        Vector2 rootPx = muzzle + atlas.Project(root, turret);
        Vector2 spanPx = atlas.Project(bore.Dir * body, turret);
        float drawn = spanPx.Length();
        if (drawn < 0.5f)
            return;
        Vector2 axis = spanPx / drawn;

        var shells = new Godot.Collections.Array();
        Rect2? box = null;
        foreach (Vector3 shell in Shells)
        {
            float len = drawn * shell.Y;
            float half = wide * shell.X / upp;
            shells.Add(new Vector4(len, half, shell.Z * glow, 0.0f));
            Vector2 tip = rootPx + axis * len;
            var one = new Rect2(rootPx, Vector2.Zero).Expand(tip).Grow(half);
            box = box is null ? one : box.Value.Merge(one);
        }

        var licks = new Godot.Collections.Array();
        var fits = new Godot.Collections.Array();
        int tongues = 0;
        foreach (Lick lick in Build(bore, phase))
        {
            Vector2 a = muzzle + atlas.Project(lick.From, turret);
            Vector2 b = muzzle + atlas.Project(lick.To, turret);
            float thick = lick.Thick / upp;
            float gain = lick.Gain;
            if (thick < 0.01f || gain <= 0.002f)
                continue;
            // Sub-pixel ribbons are widened and dimmed together - see Thinnest.
            if (thick < Thinnest)
            {
                gain *= thick / Thinnest;
                thick = Thinnest;
            }
            licks.Add(new Vector4(a.X, a.Y, b.X, b.Y));
            fits.Add(new Vector4(thick, gain, lick.Warm, 0.0f));
            if (lick.Warm <= 0.5f)
                tongues++;
            var one = new Rect2(a, Vector2.Zero).Expand(b).Grow(thick);
            box = box is null ? one : box.Value.Merge(one);
        }
        if (box is null || shells.Count == 0)
            return;

        Rect2 quad = box.Value.Grow(1.0f);
        Extent = quad;
        Showing = true;
        if (!_told)
        {
            _told = true;
            Note = string.Format(
                "proc flash: {0} shells + {1} licks ({2} tongues), {3:0}x{4:0}px "
                + "at ({5:0},{6:0}) off the anchor, body {7:0.0}px long "
                + "{8:0.0}px wide, bore r {9:0.0}px",
                shells.Count, licks.Count, tongues, quad.Size.X, quad.Size.Y,
                quad.Position.X, quad.Position.Y, drawn, wide / upp,
                bore.Radius / upp);
            GD.Print(Note);
        }

        _shader.SetShaderParameter("shells", shells);
        _shader.SetShaderParameter("shell_count", shells.Count);
        _shader.SetShaderParameter("licks", licks);
        _shader.SetShaderParameter("fits", fits);
        _shader.SetShaderParameter("lick_count", licks.Count);
        _shader.SetShaderParameter("root", rootPx);
        _shader.SetShaderParameter("axis", axis);
        _shader.SetShaderParameter("origin", quad.Position);
        _shader.SetShaderParameter("span", quad.Size);
        _shader.SetShaderParameter("profile",
            new Vector3(Profile.X, Profile.Y, ProfilePeak(Profile)));
        _shader.SetShaderParameter("petal", Petal);
        _shader.SetShaderParameter("twist", Twist);
        _shader.SetShaderParameter("seed", (float)Seed);
        _shader.SetShaderParameter("hot", new Vector3(Hot.R, Hot.G, Hot.B));
        _shader.SetShaderParameter("cool", new Vector3(Cool.R, Cool.G, Cool.B));
        _shader.SetShaderParameter("rim_falloff", RimFalloff);
        _shader.SetShaderParameter("opacity", Opacity);
        // Deliberately not Plumes.LiftOf: that clamps to 1, and depth_keep at
        // exactly 1 is a smoothstep sitting on its own midpoint - it takes half
        // the flash away over whichever hull pixel is the map's tallest. The
        // muzzle really is above the ceiling, so it is said as the number it is.
        _shader.SetShaderParameter("lift", Above(atlas, bore.Point.Z));
        var stops = new Godot.Collections.Array();
        foreach (Vector2 stop in Stops)
            stops.Add(stop);
        _shader.SetShaderParameter("stops", stops);
        Plumes.SetDepth(_shader, atlas, Tank.HullFacing);

        DrawSetTransformMatrix(Tank.ShearFor(true));
        Vector2 bump = Tank.HeaveShift(true, false);
        DrawTextureRect(_white!, new Rect2(quad.Position + bump, quad.Size), false);
        DrawSetTransformMatrix(Transform2D.Identity);
    }

    /// <summary>The shader as it is compiled, so the checks can read what it
    /// actually got rather than what the template says.</summary>
    internal static readonly string BlazeShaderCode =
        BlazeCode.Replace("DEPTH_CODE", Plumes.DepthCode);

    private static readonly Shader Blaze = new() { Code = BlazeShaderCode };

    /// <summary>
    /// The section across one ribbon and the stacking of them.
    ///
    /// <b>Alpha-over inside the layer, additive on the way out</b> -
    /// <see cref="ProcFire"/>'s arrangement and for its reason: surfaces stack
    /// the way surfaces stack, and the layer as a whole adds light. Written the
    /// same way, with the alpha spent by the compositing and a nought handed
    /// over.
    ///
    /// <b>One surface per element</b>, the correction the three smoke and fire
    /// layers already took: the material asks for its transparent back behind a
    /// <c>hasattr</c> guard on a property EEVEE Next removed, so the render put
    /// down fronts only.
    /// </summary>
    internal const string BlazeCode = @"
shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

// Nested copies of the body: how long, how wide, how hard it burns.
uniform vec4 shells[8];
uniform int shell_count = 0;
// Tongues and sparks: both ends, in the tile's px.
uniform vec4 licks[24];
// Thickness at the first end, gain, and where on the paint it sits.
uniform vec4 fits[24];
uniform int lick_count = 0;
uniform vec2 root = vec2(0.0);
uniform vec2 axis = vec2(1.0, 0.0);
uniform vec2 origin = vec2(0.0);
uniform vec2 span = vec2(1.0);
// (a, b, peak) of the Beta profile the body's radius follows.
uniform vec3 profile = vec3(0.45, 0.90, 1.0);
// (count, depth, sharpness, jitter)
uniform vec4 petal = vec4(9.0, 0.20, 1.6, 0.35);
uniform float twist = 0.55;
uniform float seed = 7.0;
uniform vec3 hot = vec3(0.71, 0.64, 0.55);
uniform vec3 cool = vec3(0.71, 0.57, 0.44);
uniform vec2 stops[5];
uniform float rim_falloff = 1.35;
uniform float opacity = 0.95;
// The bore's height on the map's byte scale. One number for the whole flash
// rather than one per element: every part of it sits within a few bore radii of
// the muzzle, and the muzzle is already above the map's ceiling on all three
// tanks - see the class note. It is read anyway so a model that ever dips gets
// the right answer.
uniform float lift = 1.0;

DEPTH_CODE

float hash01(float a, float b) {
    return fract(sin(a * 127.1 + b * 311.7) * 43758.5453);
}

// Alpha-over on premultiplied colour. The layer adds light on the way out, but
// inside it a surface in front of another hides it - see ProcFire.
vec4 over(vec4 under, vec4 one) {
    return vec4(under.rgb * (1.0 - one.a) + one.rgb * one.a,
                under.a + one.a * (1.0 - under.a));
}

// How much alpha the body has this far along itself.
float strength_at(float t) {
    float out_of = stops[0].y;
    for (int i = 1; i < 5; i++) {
        if (t <= stops[i].x) {
            float lo = stops[i - 1].x;
            float f = clamp((t - lo) / max(stops[i].x - lo, 0.0001), 0.0, 1.0);
            return mix(stops[i - 1].y, stops[i].y, f);
        }
        out_of = stops[i].y;
    }
    return out_of;
}

// The radial ripple, at the azimuth of the surface facing the eye.
//
// |cos| to a power rather than a plain cosine: a cosine gives soft scallops at
// any depth and the reference silhouette is made of points. Each spike gets its
// own length or the whole thing reads as a gear.
float petal_at(float phi) {
    float n = max(petal.x, 1.0);
    float ridge = pow(abs(cos(n * phi * 0.5)), petal.z);
    float which = floor(n * fract(phi / 6.2831853) );
    ridge *= 1.0 - petal.w * hash01(which, seed);
    return 1.0 - petal.y + petal.y * ridge * 2.0;
}

void fragment() {
    vec2 at = origin + UV * span;
    float tank = depth_at(at);
    vec2 side = vec2(-axis.y, axis.x);
    vec2 rel = at - root;
    float along = dot(rel, axis);
    float across = dot(rel, side);

    vec4 fire = vec4(0.0);

    // --- the body, outermost shell first so the core lands on top ------------
    for (int i = 0; i < 8; i++) {
        if (i >= shell_count) { break; }
        vec4 s = shells[i];
        if (s.x <= 0.0 || s.y <= 0.0 || s.z <= 0.0) { continue; }
        float t = along / s.x;
        if (t <= 0.0 || t >= 1.0) { continue; }
        // Normalised so the widest ring is exactly the asked-for half-width.
        float shape = pow(t, profile.x) * pow(1.0 - t, profile.y)
                      / max(profile.z, 0.0001);
        float plain = s.y * shape;
        if (plain <= 0.0001) { continue; }
        float q = across / plain;
        if (abs(q) >= 1.0) { continue; }
        // The azimuth of the surface facing the eye, solved once against the
        // unrippled radius. An exact solve would be implicit - the ripple moves
        // the radius which moves the azimuth - and one step of it is what the
        // silhouette is made of anyway.
        float phi = asin(clamp(q, -1.0, 1.0)) + twist * t;
        float r = plain * petal_at(phi);
        q = across / max(r, 0.0001);
        if (abs(q) >= 1.0) { continue; }
        float facing = sqrt(max(1.0 - q * q, 0.0));
        float a = pow(facing, rim_falloff) * strength_at(t) * opacity * s.z;
        a *= depth_keep(tank, lift);
        if (a <= 0.0) { continue; }
        fire = over(fire, vec4(mix(hot, cool, t) * a, a));
    }

    // --- tongues, then sparks -----------------------------------------------
    for (int i = 0; i < 24; i++) {
        if (i >= lick_count) { break; }
        vec4 l = licks[i];
        vec4 f = fits[i];
        vec2 ab = l.zw - l.xy;
        float len2 = max(dot(ab, ab), 0.0001);
        float t = clamp(dot(at - l.xy, ab) / len2, 0.0, 1.0);
        // Tapered by (1-t)^0.6, which is the mesh's own ring radius.
        float r = f.x * pow(max(1.0 - t, 0.0), 0.6);
        if (r <= 0.05) { continue; }
        float d = length(at - (l.xy + ab * t)) / r;
        if (d >= 1.0) { continue; }
        float facing = sqrt(max(1.0 - d * d, 0.0));
        // The mesh fades its rings 1 -> 0.25 along the streak.
        float a = pow(facing, rim_falloff) * (1.0 - 0.75 * t) * opacity * f.y;
        a *= depth_keep(tank, lift);
        if (a <= 0.0) { continue; }
        fire = over(fire, vec4(mix(cool, hot, f.z) * a, a));
    }

    // Premultiplied by the compositing above, so the alpha is spent and a nought
    // is handed over: an emitting layer has light to add and nothing to hide
    // behind. See EffectLayer.Glow.
    COLOR = vec4(fire.rgb, 0.0);
}
";
}
