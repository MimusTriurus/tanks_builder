using System;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// What a plume and a flame have in common, once they are built here rather than
/// read off an atlas: the dice, the path along which elements travel, the pair of
/// directions across it, and where a world height sits on the height map.
///
/// <b>One home rather than two copies, for the reason the whole exercise exists.</b>
/// <c>engine_fire</c> and <c>exhaust_plume</c> share this arithmetic in Blender -
/// the fire file imports the plume's path outright - and two transcriptions of it
/// would part at whichever number nobody put side by side.
///
/// These are Blender's coordinates throughout: the port is stamped in them, so z
/// is height and the model's own frame is what everything is quoted in. The turn
/// into pixels happens once, in <see cref="AtlasSet.Project"/>.
/// </summary>
public static class Plumes
{
    /// <summary>World up in the model's own frame. Not Godot's up: these are
    /// Blender coordinates, the ones the port is stamped in.</summary>
    public static readonly Vector3 Up = new(0.0f, 0.0f, 1.0f);

    /// <summary>
    /// Deterministic values in [0, 1) from two integers - <c>muzzle_flash._hash01</c>
    /// with its two keys, so the arrangement is stable between runs and between a
    /// run and the screenshot it is compared against.
    /// </summary>
    /// <remarks>The arrangement it produces is <em>a</em> draw of the same dice
    /// rather than the render's own, because the two hashes differ in how they
    /// fold the second key. Worth saying out loud: an A/B against a rendered layer
    /// is a comparison of two arrangements of one model, not of one arrangement
    /// drawn two ways.</remarks>
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
    /// Where an element is at each age, in world units off the port.
    ///
    /// The direction turns from the vent's axis toward world up as the element
    /// ages and the speed changes, so the result is a curve that leaves the port
    /// the way the port points and ends up climbing. Swinging a straight line
    /// instead would move the <em>whole</em> plume, the part still in the pipe
    /// included.
    ///
    /// Normalised on the end <em>point</em>, so the reach is how far the tip gets
    /// from the port rather than how far it travelled getting there.
    ///
    /// <b><paramref name="rate"/> is the one number that tells smoke from fire.</b>
    /// Speed along the path is <c>exp(rate * t)</c>. Negative - which is what the
    /// plume's <c>slowing</c> means, as <c>-1/slowing</c> - spaces the elements out
    /// toward the tip, because they are still slowing down when they die. Positive
    /// - the flame's <c>gather</c> - crowds them at the seat and pulls them apart
    /// toward the tips, which is the shape of a fire. Zero is an evenly spaced
    /// ladder and reads as a machine.
    /// </summary>
    /// <remarks>Integrated and then sampled, exactly as <c>exhaust_plume._path</c>
    /// does. The closed form the forest's flame uses is not available here: that
    /// axis is vertical, so the direction never needs renormalising, and this one
    /// points back and up at anywhere from 49 to 72 degrees depending on the
    /// tank.</remarks>
    public static Vector3[] Path(Vector3 axis, float reach, float buoyancy,
                                 float turnPower, float rate, int samples = 96)
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
            run += dir * MathF.Exp(rate * t);
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

    /// <summary>Where along the path an element of this age has got.</summary>
    public static Vector3 Along(Vector3[] path, float age)
    {
        float at = Math.Clamp(age, 0.0f, 1.0f) * (path.Length - 1);
        int lo = Math.Clamp((int)at, 0, path.Length - 2);
        return path[lo].Lerp(path[lo + 1], at - lo);
    }

    /// <summary>Which way the flow is running at this age - the path's own
    /// tangent, and what a stretched element lies along. A flame's elements are
    /// long, so getting this wrong is a lick lying across its own column.</summary>
    public static Vector3 Going(Vector3[] path, float age)
    {
        float at = Math.Clamp(age, 0.0f, 1.0f) * (path.Length - 1);
        int lo = Math.Clamp((int)at, 0, path.Length - 2);
        Vector3 step = path[lo + 1] - path[lo];
        return step.LengthSquared() > 1e-18f ? step.Normalized() : Up;
    }

    /// <summary>Two unit vectors across <paramref name="axis"/> - the pair
    /// <c>muzzle_flash._perpendicular</c> builds, by the same rule: the seed swaps
    /// away from up when the axis is nearly up, or the cross product
    /// vanishes.</summary>
    public static (Vector3 U, Vector3 V) Across(Vector3 axis)
    {
        Vector3 seed = MathF.Abs(axis.Z) > 0.9f ? new Vector3(1.0f, 0.0f, 0.0f) : Up;
        Vector3 u = seed.Cross(axis).Normalized();
        return (u, axis.Cross(u));
    }

    /// <summary>Where a world height sits on the height map's own byte scale, in
    /// [0, 1]. Clamped rather than allowed to go negative, which is what an element
    /// under the belts would be.</summary>
    public static float LiftOf(AtlasSet atlas, float z)
    {
        double span = atlas.HeightHigh - atlas.HeightLow;
        return span <= 0.0 ? 0.0f
            : (float)Math.Clamp((z - atlas.HeightLow) / span, 0.0, 1.0);
    }

    /// <summary>
    /// The same, for an element of <paramref name="radius"/> - its <em>near</em>
    /// surface rather than its middle.
    ///
    /// <b>The middle is the wrong point to compare, and it shows as a flat
    /// rectangle.</b> The map holds one height per pixel and the depth buffer would
    /// have compared the nearest surface at that pixel; a ball sitting on the deck
    /// has its centre level with it and most of its front half in front of it, so
    /// judged by the centre the whole ball goes. Measured on the heavy: a hard-edged
    /// 30x12px patch cut out of the column, 95% of it over the tank's own rear deck -
    /// the mask doing exactly what it was told and telling it the wrong thing.
    ///
    /// Nearer is higher by <c>r*sin(elevation)</c>, which is this camera's depth
    /// relation read the other way: <c>depth = const - Z/sin(e)</c>, so a step of r
    /// toward the eye is a step of <c>r*sin(e)</c> up.
    /// </summary>
    public static float BulgeOf(AtlasSet atlas, float radius)
    {
        double span = atlas.HeightHigh - atlas.HeightLow;
        return span <= 0.0 ? 0.0f
            : (float)(radius * Math.Sin(Mathf.DegToRad(atlas.Elevation)) / span);
    }

    /// <summary>
    /// Hand a shader the tank's height map at this heading, so a built layer can
    /// be cut where the tank is in front of it. Returns whether there was one.
    ///
    /// <b>This is the holdout, and it is exact rather than a stand-in.</b> The
    /// rendered layers have the hull punched out of their alpha - <c>hull_only</c>
    /// in <c>tank_pipeline</c> - which is a depth test done at render time. Cutting
    /// against the hull's <em>alpha</em> instead is a different question: it takes
    /// the column wherever the hull's silhouette is, in front or behind, and
    /// measured on four headings it took the haze off the deck that the rendered
    /// column legitimately puts there.
    ///
    /// Height answers it exactly. At a fixed screen row this camera has
    /// <c>screen_y = -(Y*sin(e) + Z*cos(e))</c> and depth <c>Y*cos(e) - Z*sin(e)</c>;
    /// eliminating Y between them leaves <c>depth = const - Z/sin(e)</c>. So at one
    /// pixel higher is nearer, full stop - and comparing the map's height against
    /// an element's own <em>is</em> comparing depth. Which is also why the elements
    /// composite seat first: the same fact used once per layer instead of once per
    /// fragment.
    ///
    /// The map covers the hull and the belts rather than the hull alone, so this
    /// holds out marginally more than the render does. That is the better answer
    /// and not a discrepancy: a belt in front of a flame hides it too.
    /// </summary>
    public static bool SetDepth(ShaderMaterial shader, AtlasSet atlas, double facing)
    {
        int frame = atlas.HasHeights && atlas.Has(AtlasSet.HeightName)
            ? atlas.EffectFrame(AtlasSet.HeightName, 0, facing) : -1;
        Vector2 size = frame < 0 ? Vector2.Zero
            : atlas.SizeOf(AtlasSet.HeightName, frame);
        if (size.X <= 0.0f || size.Y <= 0.0f)
        {
            shader.SetShaderParameter("depth_on", false);
            return false;
        }
        Rect2 region = atlas.Region(AtlasSet.HeightName, frame);
        Texture2D texture = atlas.Texture(AtlasSet.HeightName);
        shader.SetShaderParameter("depth_on", true);
        shader.SetShaderParameter("depth_tex", texture);
        shader.SetShaderParameter("depth_size",
            new Vector2(texture.GetWidth(), texture.GetHeight()));
        shader.SetShaderParameter("depth_rect",
            new Vector4(region.Position.X, region.Position.Y, size.X, size.Y));
        // Its own anchor, not the hull's: the height layer is rendered in a tile of
        // its own size and the anchor scales with the tile - the trap the effect
        // layers already pay attention to.
        shader.SetShaderParameter("depth_shift",
            atlas.AnchorOf(AtlasSet.HeightName)
            - atlas.OffsetOf(AtlasSet.HeightName, frame));
        return true;
    }

    /// <summary>
    /// The uniforms and the read that let a built layer cut itself against the
    /// tank, as shader text - one statement, used by the column and the flame.
    ///
    /// Nearest rather than linear on the map: the byte is a height, and blending
    /// two of them across a silhouette edge invents a surface halfway between the
    /// tank and nothing.
    /// </summary>
    public const string DepthCode = @"
uniform bool depth_on = false;
uniform sampler2D depth_tex : filter_nearest;
uniform vec2 depth_size = vec2(1.0);
uniform vec4 depth_rect = vec4(0.0);
uniform vec2 depth_shift = vec2(0.0);
// How softly the cut is made, on the map's own scale. Small: it is there to stop
// the silhouette's edge stepping, not to blur the tank.
uniform float depth_band = 0.02;

// What the tank's own surface is doing over this pixel: -1 where it is not drawn
// at all, otherwise its height on the map's scale.
float depth_at(vec2 at) {
    if (!depth_on) {
        return -1.0;
    }
    vec2 dp = at + depth_shift;
    if (dp.x < 0.0 || dp.x > depth_rect.z || dp.y < 0.0 || dp.y > depth_rect.w) {
        return -1.0;
    }
    float code = texture(depth_tex, (depth_rect.xy + dp) / depth_size).r;
    // 0 is no tank; the rest is the range mapped onto 1..255.
    return code > 0.002 ? (code * 255.0 - 1.0) / 254.0 : -1.0;
}

// How much of an element survives the tank standing over it, given how high its
// near surface is at this fragment.
//
// <b>The height is the surface's, not the middle's, and that is per fragment.</b>
// The map holds one height per pixel and a depth buffer would compare the nearest
// surface at that pixel; judged by its centre a ball resting on the deck is
// removed whole, front half included. Measured on the heavy, that is a hard-edged
// 30x12px hole in the column, 95% of it over the tank's own rear deck - the mask
// doing exactly what it was told and being told the wrong thing.
//
// `high` therefore arrives as (centre, bulge) and the caller adds the bulge in by
// however much of the ball is facing the eye: nearer is higher by r*sin(elevation),
// which is this camera's own depth relation read backwards.
//
// The argument is `high` and not `lift` on purpose: the column hands this the
// element's entry from a `uniform float lift[]`, and a parameter of that name is
// a Redefinition - which does not fail loudly, it fails as a shader that did not
// compile and a material that fell back to opaque. Same trap as `UV` inside a
// function, one file over.
float depth_keep(float tank, float high) {
    return tank < 0.0 ? 1.0
                      : 1.0 - smoothstep(high - depth_band, high + depth_band, tank);
}
";
}
