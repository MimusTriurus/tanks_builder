using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The water a hull moving through it leaves and pushes, in the model's look:
/// the wake behind - two arms of foam running back from the bow and spreading
/// apart, and churned water straight behind the stern - and the foam bow wave
/// round the leading end. The board's <see cref="Wake"/> and
/// <see cref="Stage3D.BowSwell"/> are a sprite's (a <see cref="Vehicle"/>'s
/// trail, a swell painted in the pond's shader); this is laid off the model,
/// flat on the water, hard-edged, white and the pond's pale teal, as
/// <see cref="CelSplash"/>'s ring is.
///
/// <b>The arms are a V, and the V is the water's, not the hull's.</b> A point of
/// an arm is laid where the bow was and then left in the water; it moves out
/// sideways from the hull's line at <see cref="Spread"/> of the speed the hull
/// had - the Kelvin wake's 19.5 degrees - so the arms open behind a moving hull
/// and stand still where it stopped. The wash is laid at the stern and widens
/// and breaks up faster. Both are stitches, as the ruts are (<see cref="CelRuts"/>):
/// a stitch every <see cref="Step"/> px of the hull's travel, the pen lifted
/// out of the water.
///
/// <b>The bow wave rides the hull.</b> A band of foam round the leading end in
/// the hull's own shape - the rounded rectangle <see cref="CelSplash"/>'s ring
/// is - thickest ahead of the nose and thinning back along the flanks, its
/// churn scrolling back at the hull's speed so the foam stays in the water
/// while the hull goes through it. Its strength is the pace: nothing standing,
/// full at the water's top speed.
/// </summary>
public sealed partial class CelWake : Node3D
{
    /// <summary>How long an arm's point and a wash's last, s.</summary>
    public const float ArmLife = 3.2f, WashLife = 1.5f;

    /// <summary>How fast an arm's point moves out from the hull's line, as a
    /// share of the speed it was laid at: tan 19.5 degrees.</summary>
    public const float Spread = 0.354f;

    /// <summary>The least spreading speed, px/s: a hull at a crawl still
    /// opens its wake a little.</summary>
    public const float SpreadFloor = 18.0f;

    /// <summary>World px of travel between stitches.</summary>
    public const float Step = 6.0f;

    public const int Capacity = 400;

    private record struct Stitch(Vector3 Bow, Vector3 Stern, Vector3 Across, float HalfWide,
                                 float Pace, float Speed, float Born, float Along, bool Break);

    private readonly List<Stitch> _stitches = new();
    private Vector3? _last;
    private float _along;
    private float _clock;
    private MeshInstance3D _arms = null!, _wash = null!, _bow = null!;
    private ShaderMaterial _bowLook = null!;
    private float _squash = 0.5f, _rise = 0.86f;

    public void Build(float squash, float rise)
    {
        _squash = squash;
        _rise = rise;
        _arms = Strip("Arms", 0.0f);
        _wash = Strip("Wash", 1.0f);
        _bowLook = new ShaderMaterial { Shader = BowShader, RenderPriority = Stage3D.DressOrder };
        _bowLook.SetShaderParameter("foam", CelSplash.Crest);
        _bowLook.SetShaderParameter("edge", CelSplash.Body);
        _bow = new MeshInstance3D
        {
            Name = "BowWave",
            Mesh = new PlaneMesh { Size = Vector2.One },
            MaterialOverride = _bowLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            SortingOffset = -100000.0f,
            Visible = false,
        };
        AddChild(_bow);
    }

    private MeshInstance3D Strip(string name, float mode)
    {
        var look = new ShaderMaterial { Shader = TrailShader, RenderPriority = Stage3D.DressOrder };
        look.SetShaderParameter("foam", CelSplash.Crest);
        look.SetShaderParameter("edge", CelSplash.Body);
        look.SetShaderParameter("mode", mode);
        var mesh = new MeshInstance3D
        {
            Name = name,
            Mesh = new ImmediateMesh(),
            MaterialOverride = look,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // After the pond, which is transparent and drawn late (CelRuts).
            SortingOffset = -100000.0f,
        };
        AddChild(mesh);
        return mesh;
    }

    public void Clear()
    {
        _stitches.Clear();
        _last = null;
        if (_arms is not null)
        {
            ((ImmediateMesh)_arms.Mesh).ClearSurfaces();
            ((ImmediateMesh)_wash.Mesh).ClearSurfaces();
            _bow.Visible = false;
        }
    }

    /// <summary>
    /// The hull this frame: where its leading end and its trailing end cut the
    /// water (world, at the surface), the way across it (flat, unit), its half
    /// width with the belts, its speed (px/s) and its pace (nought to one of
    /// the water's top speed), and whether it is in the water at all - out of
    /// it the pen lifts and the next stitch starts a new run.
    /// </summary>
    public void Lay(Vector3 bow, Vector3 stern, Vector3 across, float halfWide,
                    float speed, float pace, bool wet)
    {
        if (!wet || pace < 0.04f)
        {
            _last = null;
            return;
        }
        float moved = _last is Vector3 was ? new Vector2(stern.X - was.X, stern.Z - was.Z).Length() : 0.0f;
        if (_last is not null && moved < Step)
            return;
        _along += moved;
        _stitches.Add(new Stitch(bow, stern, across, halfWide, pace, Mathf.Max(speed, SpreadFloor),
                                 _clock, _along, _last is null));
        _last = stern;
        if (_stitches.Count > Capacity)
            _stitches.RemoveAt(0);
    }

    /// <summary>The bow wave: the hull's middle at the surface, the way it is
    /// going, its half-lengths with the belts, its pace and speed.</summary>
    public void Bow(Vector3 middle, Vector3 way, float halfLen, float halfWide, float pace, float speed)
    {
        if (pace < 0.02f)
        {
            _bow.Visible = false;
            return;
        }
        var along = new Vector3(way.X, 0.0f, way.Z);
        along = along.LengthSquared() > 1e-6f ? along.Normalized() : Vector3.Back;
        var across = new Vector3(along.Z, 0.0f, -along.X);
        float span = Mathf.Max(halfLen, halfWide) + 40.0f;
        _bow.GlobalTransform = new Transform3D(
            new Basis(across * (2.0f * span), Vector3.Up, along * (2.0f * span)),
            middle + Stage3D.Clear(_squash, _rise));
        _bow.Visible = true;
        _bowLook.SetShaderParameter("half_len", halfLen);
        _bowLook.SetShaderParameter("half_wide", halfWide);
        _bowLook.SetShaderParameter("span", span);
        _bowLook.SetShaderParameter("pace", Mathf.Clamp(pace, 0.0f, 1.0f));
        _bowLook.SetShaderParameter("speed", speed);
    }

    public void Tick(float dt)
    {
        _clock += dt;
        while (_stitches.Count > 0 && _clock - _stitches[0].Born > ArmLife)
            _stitches.RemoveAt(0);
        var arms = new List<(Vector3 At, Vector2 Uv, Color C)>();
        var wash = new List<(Vector3 At, Vector2 Uv, Color C)>();
        for (int i = 1; i < _stitches.Count; i++)
        {
            Stitch a = _stitches[i - 1], b = _stitches[i];
            if (b.Break)
                continue;
            float ageA = _clock - a.Born, ageB = _clock - b.Born;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 pa = a.Bow + a.Across * side * (a.HalfWide + Spread * a.Speed * ageA);
                Vector3 pb = b.Bow + b.Across * side * (b.HalfWide + Spread * b.Speed * ageB);
                float wa = Width(ageA / ArmLife, a.Pace), wb = Width(ageB / ArmLife, b.Pace);
                Quad(arms, pa, pb, a.Across * wa, b.Across * wb, a.Along, b.Along,
                     Look(ageA / ArmLife, a.Pace), Look(ageB / ArmLife, b.Pace));
            }
            if (ageA < WashLife)
            {
                float sa = a.HalfWide * (0.7f + 0.8f * ageA / WashLife);
                float sb = b.HalfWide * (0.7f + 0.8f * Mathf.Min(ageB / WashLife, 1.0f));
                Quad(wash, a.Stern, b.Stern, a.Across * sa, b.Across * sb, a.Along, b.Along,
                     Look(ageA / WashLife, a.Pace), Look(Mathf.Min(ageB / WashLife, 1.0f), b.Pace));
            }
        }
        Fill((ImmediateMesh)_arms.Mesh, arms);
        Fill((ImmediateMesh)_wash.Mesh, wash);
    }

    /// <summary>An arm's width, px: a thin line at the bow, wider as it runs
    /// out, thicker for a faster hull.</summary>
    private static float Width(float age, float pace) => (2.5f + 5.5f * age) * (0.55f + 0.45f * pace);

    private static Color Look(float age, float pace) => new(pace, 0.0f, 0.0f, Mathf.Clamp(age, 0.0f, 1.0f));

    private void Quad(List<(Vector3, Vector2, Color)> into, Vector3 a, Vector3 b, Vector3 sideA, Vector3 sideB,
                      float alongA, float alongB, Color ca, Color cb)
    {
        Vector3 lift = Stage3D.Clear(_squash, _rise);
        Vector3 a0 = a - sideA + lift, a1 = a + sideA + lift, b0 = b - sideB + lift, b1 = b + sideB + lift;
        into.Add((a0, new Vector2(alongA, -1.0f), ca));
        into.Add((b0, new Vector2(alongB, -1.0f), cb));
        into.Add((b1, new Vector2(alongB, 1.0f), cb));
        into.Add((a0, new Vector2(alongA, -1.0f), ca));
        into.Add((b1, new Vector2(alongB, 1.0f), cb));
        into.Add((a1, new Vector2(alongA, 1.0f), ca));
    }

    private static void Fill(ImmediateMesh mesh, List<(Vector3 At, Vector2 Uv, Color C)> verts)
    {
        mesh.ClearSurfaces();
        if (verts.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (at, uv, c) in verts)
        {
            mesh.SurfaceSetColor(c);
            mesh.SurfaceSetUV(uv);
            mesh.SurfaceAddVertex(at);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>
    /// The arms (<c>mode</c> 0) and the wash (1): foam torn by a noise in the
    /// world - so it stays in the water as the strip is rebuilt - that takes
    /// more of it as it ages (<c>COLOR.a</c>). An arm is a line, white along
    /// its middle and pale at its sides; the wash is churn, white patches on
    /// pale, and ragged at its edges. Hard edges, antialiased by the
    /// derivative; weaker for a slower hull (<c>COLOR.r</c>).
    /// </summary>
    private static readonly Shader TrailShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 foam : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 edge : source_color = vec3(0.62, 0.83, 0.87);
uniform float mode = 0.0;
varying vec3 wp;
" + Toon.NoiseCode + @"
void vertex() {
    wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
float cut(float x) {
    return clamp(x / max(fwidth(x), 1e-4) + 0.5, 0.0, 1.0);
}
void fragment() {
    float age = COLOR.a;
    float pace = COLOR.r;
    float v = abs(UV.y);
    float n = noise3(vec3(wp.xz * 0.07, age * 1.3 + mode * 7.0)) * 0.65
            + noise3(vec3(wp.xz * 0.19, 3.0 + age)) * 0.35;
    float cover;
    float white;
    if (mode < 0.5) {
        float body = cut((1.0 - 0.35 * n) - v);
        cover = body * cut(n - (0.28 + 0.5 * age));
        white = cut(0.45 - v) * cut(0.6 - age);
    } else {
        float body = cut((1.0 - 0.5 * n) - v);
        cover = body * cut(n - (0.34 + 0.45 * age));
        white = cut(n - (0.52 + 0.3 * age));
    }
    if (cover <= 0.01) discard;
    ALBEDO = mix(edge, foam, white);
    ALPHA = cover * (1.0 - smoothstep(0.75, 1.0, age)) * clamp(0.35 + pace, 0.0, 1.0);
}
",
    };

    /// <summary>
    /// The bow wave: a band out from the hull's rounded rectangle (UV about the
    /// hull, x across, y along), widest ahead of the nose and thinning back
    /// along the flanks to nothing short of the stern; its churn is a noise
    /// in the water, which in the hull's frame scrolls back at the hull's
    /// speed. White against the hull, the pale water at its outer edge.
    /// </summary>
    private static readonly Shader BowShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 foam : source_color = vec3(0.97, 1.0, 1.0);
uniform vec3 edge : source_color = vec3(0.62, 0.83, 0.87);
uniform float half_len = 70.0;
uniform float half_wide = 45.0;
uniform float span = 120.0;
uniform float pace = 1.0;
uniform float speed = 30.0;
" + Toon.NoiseCode + @"
float cut(float x) {
    return clamp(x / max(fwidth(x), 1e-4) + 0.5, 0.0, 1.0);
}
void fragment() {
    vec2 g = (UV - 0.5) * 2.0 * span;
    float corner = min(half_wide, half_len) * 0.5;
    vec2 d = abs(g) - vec2(half_wide, half_len) + corner;
    float dist = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - corner;
    if (dist < -3.0) discard;
    // In the water's frame: the hull goes forward, the foam stays.
    vec2 w = vec2(g.x, g.y + TIME * speed);
    float n = noise3(vec3(w * 0.09, TIME * 0.8)) * 0.6 + noise3(vec3(w * 0.21, TIME * 1.3 + 4.0)) * 0.4;
    float ahead = smoothstep(-0.75 * half_len, half_len * 0.9, g.y);
    float width = pace * (2.0 + 13.0 * ahead * ahead + 8.0 * smoothstep(0.7 * half_len, half_len, g.y))
                * (0.7 + 0.6 * n);
    float band = cut(width - dist) * cut(dist + 3.0);
    float cover = band * cut(n - (0.62 - 0.35 * ahead));
    if (cover <= 0.01) discard;
    float white = cut(width * 0.55 - dist);
    ALBEDO = mix(edge, foam, white);
    ALPHA = cover * clamp(pace * 1.6, 0.0, 1.0);
}
",
    };
}
