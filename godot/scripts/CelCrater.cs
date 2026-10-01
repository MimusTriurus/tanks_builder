using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The craters rounds leave in the ground beside a 3D tank, in the model's
/// look - <see cref="CelBurst"/>'s, which digs them. The way the jackal
/// port's 3D preview makes them (ratel, <c>Level3DFx.crater_mesh</c>):
/// <b>a rim, not a mark</b>.
///
/// <list type="bullet">
/// <item><b>The rim</b> - thrown-up earth in a ring of <see cref="Segments"/>
/// points, up from the hole's edge at <see cref="RimIn"/> to a ragged crest
/// at <see cref="RimTop"/>, down to the ground at <see cref="RimOut"/>; its
/// faces flat and each one colour, on the model's cel shader
/// (<see cref="Toon.CelShader"/>), so the sun lights it as it lights a hull.
/// Its inner wall is steep - 59 degrees, steeper than the sun is high (52) -
/// so the half of it with its back to the sun is in shade and the other lit:
/// the hole's two tones come from the light and are not painted. The outer
/// slope is gentle and lit all round. Along the crest a black chamfer - the
/// line the models' sharp edges have - is what draws the ring from above.</item>
/// <item><b>The bowl</b> - flat, a dark mark inside the rim's foot, the far
/// wall from the sun a step lighter (the wall the sun sees into) and the floor
/// a shade darker than the walls - not soot: in soot it read as a black hole
/// (ratel's lesson). ratel cuts a hole in the ground and puts a real bowl in
/// it; the board here is <see cref="Stage3D"/>'s, shared with the sprite
/// benches, and at this camera's 30 degrees the bowl's depth shows little.</item>
/// <item><b>Clods</b> - a few lumps of earth lying out past the rim's foot.</item>
/// <item>It grows in over <see cref="GrowTime"/> s, and stays; the oldest of
/// <see cref="Kept"/> goes first.</item>
/// <item><b>The hulls feel it</b> (<see cref="HeightAt"/>) - ratel's
/// <c>crater_profile</c>: up over the rim, down into the bowl.</item>
/// </list>
/// Lengths are shares of the crater's radius to the rim's outer foot.
/// </summary>
public sealed partial class CelCrater : Node3D
{
    public const float RimIn = 0.5f, RimTop = 0.62f, RimOut = 1.0f, RimHeight = 0.2f;
    public const int Segments = 11;
    /// <summary>The crest's chamfer, a share of the radius: the models' ink
    /// width (<see cref="Toon.InkWidth"/>) on a crater of some 40 px.</summary>
    public const float RimLine = 0.03f;
    public const float GrowTime = 0.2f;
    public const int Kept = 12;
    /// <summary>Rims made at the start, each its own raggedness; a crater
    /// takes one of them, turned.</summary>
    private const int Shapes = 4;

    /// <summary>The rim's inner face, scorched; the earth thrown out; the
    /// chamfer; the clods.</summary>
    /// Darker than the ground they stand for: lit by the sun and the fill,
    /// the ground's own tone came out cream.
    public Color Scorched = new(0.30f, 0.21f, 0.13f), Earth = new(0.44f, 0.34f, 0.22f),
                 Chamfer = new(0.0f, 0.0f, 0.0f), Clod = new(0.30f, 0.22f, 0.14f);
    /// <summary>The bowl: its wall away from the sun, the wall toward it, the
    /// floor.</summary>
    public Color BowlShade = new(0.22f, 0.16f, 0.10f), BowlLit = new(0.40f, 0.29f, 0.18f),
                 BowlFloor = new(0.18f, 0.13f, 0.09f);

    /// <summary>Toward the sun on the ground, set by the owner: which wall
    /// of the bowl the sun sees into.</summary>
    public Vector3 SunWay = new(-0.6f, 0.0f, 0.8f);

    private sealed class Pit
    {
        public required Node3D Node;
        public required MeshInstance3D Rim, Bowl;
        public required ShaderMaterial BowlLook;
        public readonly List<MeshInstance3D> Clods = new();
        public float Radius, Age;
    }

    private readonly List<ArrayMesh> _shapes = new();
    private readonly List<Pit> _pits = new();
    private int _next, _dug;
    private float _squash = 0.5f, _rise = 0.86f;
    private ShaderMaterial? _scorched, _earth, _chamfer, _clod;
    private SphereMesh? _lump;

    public void Build(float squash, float rise)
    {
        _squash = squash;
        _rise = rise;
        _scorched = Cel(Scorched);
        _earth = Cel(Earth);
        _chamfer = Cel(Chamfer);
        _clod = Cel(Clod);
        for (int i = 0; i < Shapes; i++)
            _shapes.Add(RimMesh(10 + i));
        _lump = new SphereMesh { Radius = 1.0f, Height = 1.6f, RadialSegments = 5, Rings = 2 };
    }

    private static ShaderMaterial Cel(Color albedo)
    {
        var m = new ShaderMaterial { Shader = Toon.CelShader };
        m.SetShaderParameter("albedo", albedo);
        return m;
    }

    /// <summary>A crater at <paramref name="at"/> (on the ground), its rim's
    /// outer foot <paramref name="radius"/> out, world px.</summary>
    public void Dig(Vector3 at, float radius)
    {
        _dug++;
        Pit pit;
        if (_pits.Count < Kept)
        {
            var node = new Node3D { Name = $"Crater{_pits.Count}" };
            AddChild(node);
            var rim = new MeshInstance3D { Name = "Rim", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            node.AddChild(rim);
            var look = new ShaderMaterial { Shader = BowlShader, RenderPriority = Stage3D.RutOrder };
            var bowl = new MeshInstance3D
            {
                Name = "Bowl", Mesh = new PlaneMesh { Size = Vector2.One * (2.0f * RimIn * 1.04f) },
                MaterialOverride = look, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(bowl);
            pit = new Pit { Node = node, Rim = rim, Bowl = bowl, BowlLook = look };
            _pits.Add(pit);
        }
        else
            pit = _pits[_next % _pits.Count];
        _next = (_next + 1) % Kept;
        int shape = (int)(CelPuff.Hash(_dug, 301) * Shapes) % Shapes;
        pit.Rim.Mesh = _shapes[shape];
        for (int s = 0; s < 3; s++)
            pit.Rim.SetSurfaceOverrideMaterial(s, s == 0 ? _scorched : s == 1 ? _chamfer : _earth);
        float turn = CelPuff.Hash(_dug, 307) * Mathf.Tau;
        pit.Node.GlobalTransform = new Transform3D(new Basis(Vector3.Up, turn), at);
        // Over the ground a hair, the ruts' and the shadow's way.
        pit.Bowl.Position = Stage3D.Clear(_squash, _rise) * (1.0f / Mathf.Max(radius, 1.0f));
        pit.Bowl.Rotation = Vector3.Zero;
        var sun = new Vector3(SunWay.X, 0.0f, SunWay.Z);
        sun = sun.LengthSquared() > 1e-6f ? sun.Normalized() : Vector3.Forward;
        // Into the crater's own frame, which is turned.
        Vector3 local = new Basis(Vector3.Up, -turn) * sun;
        pit.BowlLook.SetShaderParameter("sun", new Vector2(local.X, local.Z));
        pit.BowlLook.SetShaderParameter("shade", BowlShade);
        pit.BowlLook.SetShaderParameter("lit", BowlLit);
        pit.BowlLook.SetShaderParameter("bottom", BowlFloor);
        pit.BowlLook.SetShaderParameter("seed", CelPuff.Hash(_dug, 311) * 50.0f);
        Clods(pit);
        pit.Radius = radius;
        pit.Age = 0.0f;
        Grow(pit);
        pit.Node.Visible = true;
    }

    /// <summary>Four to six lumps of earth past the foot, sunk a little way.</summary>
    private void Clods(Pit pit)
    {
        int n = 4 + (int)(CelPuff.Hash(_dug, 313) * 3.0f);
        while (pit.Clods.Count < 6)
        {
            var c = new MeshInstance3D
            {
                Name = $"Clod{pit.Clods.Count}", Mesh = _lump, MaterialOverride = _clod,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            pit.Node.AddChild(c);
            pit.Clods.Add(c);
        }
        for (int i = 0; i < pit.Clods.Count; i++)
        {
            MeshInstance3D c = pit.Clods[i];
            c.Visible = i < n;
            if (i >= n)
                continue;
            float h1 = CelPuff.Hash(_dug * 31 + i, 317), h2 = CelPuff.Hash(_dug * 31 + i, 331);
            float h3 = CelPuff.Hash(_dug * 31 + i, 337);
            float a = h1 * Mathf.Tau;
            float r = 0.08f + 0.05f * h3;
            var way = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a)) * (1.05f + 0.4f * h2);
            c.Transform = new Transform3D(
                new Basis(new Vector3(h2, h3, h1).Normalized(), h3 * Mathf.Tau).Scaled(new Vector3(r, r * 0.8f, r)),
                way + Vector3.Up * (r * 0.3f));
        }
    }

    public void Reset()
    {
        foreach (Pit p in _pits)
            p.Node.Visible = false;
    }

    /// <summary>How deep the bowl is felt, a share of the radius - shallower
    /// than ratel's 0.22: its bowl is real, this one flat, and a hull let down
    /// that far went under the board.</summary>
    public const float BowlDepth = 0.10f;

    /// <summary>
    /// How far the craters raise or lower the ground at <paramref name="w"/>
    /// (world), px - each as big as it has grown: <see cref="RimHeight"/> on
    /// the crest, falling smoothly to nothing at either foot, and a bowl
    /// <see cref="BowlDepth"/> deep inside the inner one. ratel's
    /// <c>crater_profile</c>, summed over the craters.
    /// </summary>
    public float HeightAt(Vector3 w)
    {
        float sum = 0.0f;
        foreach (Pit p in _pits)
        {
            if (!p.Node.Visible)
                continue;
            float r = p.Node.Scale.X;
            if (r < 1e-3f)
                continue;
            Vector3 o = p.Node.GlobalPosition;
            float d = new Vector2(w.X - o.X, w.Z - o.Z).Length() / r;
            if (d >= RimOut)
                continue;
            sum += r * Profile(d);
        }
        return sum;
    }

    /// <summary>The unit crater's height <paramref name="d"/> out from its
    /// middle (1 the outer foot).</summary>
    public static float Profile(float d)
    {
        if (d >= RimOut)
            return 0.0f;
        if (d >= RimTop)
            return RimHeight * Mathf.SmoothStep(RimOut, RimTop, d);
        if (d >= RimIn)
            return RimHeight * Mathf.SmoothStep(RimIn, RimTop, d);
        return -BowlDepth * (1.0f - d / RimIn * (d / RimIn));
    }

    public void Tick(float dt)
    {
        foreach (Pit p in _pits)
            if (p.Node.Visible && p.Age < GrowTime)
            {
                p.Age += dt;
                Grow(p);
            }
    }

    /// <summary>Out from a tenth of itself, fast and then slowing.</summary>
    private static void Grow(Pit p)
    {
        float k = Mathf.Clamp(p.Age / GrowTime, 0.0f, 1.0f);
        float s = p.Radius * Mathf.Lerp(0.1f, 1.0f, 1.0f - (1.0f - k) * (1.0f - k));
        p.Node.Scale = Vector3.One * s;
    }

    /// <summary>
    /// A rim, unit radius to its outer foot: rings of <see cref="Segments"/>
    /// points - the inner foot (a regular polygon, the bowl's edge), the
    /// crest's two edges, the outer foot - joined band by band, each band its
    /// own surface: the scorched inner face, the chamfer, the earth outside.
    /// Flat faces, each its own normal. ratel's <c>crater_mesh</c>.
    /// </summary>
    private static ArrayMesh RimMesh(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        var foot = new Vector3[Segments];
        var crestIn = new Vector3[Segments];
        var crestOut = new Vector3[Segments];
        var outer = new Vector3[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float a = Mathf.Tau * (i + rng.RandfRange(-0.3f, 0.3f)) / Segments;
            var way = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a));
            float crest = RimTop * rng.RandfRange(0.95f, 1.05f);
            float height = RimHeight * rng.RandfRange(0.75f, 1.25f);
            crestIn[i] = way * crest + Vector3.Up * height;
            crestOut[i] = way * (crest + RimLine) + Vector3.Up * (height - RimLine * 0.3f);
            float even = Mathf.Tau * i / Segments;
            foot[i] = new Vector3(Mathf.Cos(even), 0.0f, Mathf.Sin(even)) * RimIn;
            outer[i] = way * RimOut * rng.RandfRange(0.9f, 1.08f);
        }
        var mesh = new ArrayMesh();
        Band(mesh, foot, crestIn);
        Band(mesh, crestIn, crestOut);
        Band(mesh, crestOut, outer);
        return mesh;
    }

    private static void Band(ArrayMesh mesh, Vector3[] a, Vector3[] b)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < Segments; i++)
        {
            int j = (i + 1) % Segments;
            FaceUp(st, a[i], a[j], b[j]);
            FaceUp(st, a[i], b[j], b[i]);
        }
        st.Commit(mesh);
    }

    /// <summary>One flat triangle, wound to face up - Godot's front faces
    /// wind clockwise seen from the front, which puts the cross product
    /// behind them.</summary>
    private static void FaceUp(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = (b - a).Cross(c - a).Normalized();
        if (n.Y > 0.0f)
            (b, c) = (c, b);
        else
            n = -n;
        foreach (Vector3 v in new[] { a, b, c })
        {
            st.SetNormal(n);
            st.AddVertex(v);
        }
    }

    /// <summary>
    /// The bowl, flat, in its plane's UV (1 the plane's edge, which is the
    /// rim's foot and a hair more, tucked under it): the floor inside 0.55,
    /// the wall round it - lit on the side away from <c>sun</c>, which is the
    /// wall the sun looks into, in shade on the other - its edges bent by a
    /// noise so the floor is no circle. Hard bands.
    /// </summary>
    private static readonly Shader BowlShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec3 shade : source_color;
uniform vec3 lit : source_color;
uniform vec3 bottom : source_color;
uniform vec2 sun = vec2(0.0, 1.0);
uniform float seed = 0.0;
" + Toon.NoiseCode + @"
void fragment() {
    vec2 q = (UV - 0.5) * 2.0;
    float r = length(q);
    if (r > 1.0) discard;
    float a = atan(q.y, q.x);
    vec2 u = vec2(cos(a), sin(a));
    float bend = noise3(vec3(u * 1.8, seed)) - 0.5;
    vec3 c;
    if (r < 0.55 + 0.12 * bend) {
        c = bottom;
    } else {
        // The wall the sun sees into is the one on the far side from it.
        c = dot(u, sun) < -0.15 + 0.2 * bend ? lit : shade;
    }
    ALBEDO = c;
}
",
    };
}
