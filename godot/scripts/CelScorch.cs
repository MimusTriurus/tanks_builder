using System;
using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The scorch a 3D tank's death leaves on the ground, in the model's look -
/// the board's <c>Stage3D.Scald</c> for the sprites, which is a soft dark
/// blob in the ash map, eight units to the texel; beside the cel crater it
/// read as a smudge.
///
/// <list type="bullet">
/// <item><b>Three flat bands</b>, no ink and no light, like the ground it is
/// on (the board is unlit): soot in the middle where the fuel stood, char
/// round it, a singed brown ring at the edge - each a step darker multiplied
/// into the ground, which keeps its grain. Each band's edge wanders by a
/// noise, the whole by a slower one, and a few rays reach out of it - where
/// the burning ran.</item>
/// <item><b>A sheet laid on the ground</b>, <see cref="Grid"/> squares a side,
/// each point at the ground's height under it: a flat quad floated over a
/// cell lower than the middle's and cut into one higher. The renderer has no
/// decals (Compatibility).</item>
/// <item><b>It comes with the fire</b>: on the frame of the blast a pale mark
/// of it, opening out over <see cref="GrowTime"/> and darkening over
/// <see cref="DarkTime"/> while the wreck burns, then stays; the oldest of
/// <see cref="Kept"/> goes first.</item>
/// </list>
/// Under the ruts and the craters' bowls (<see cref="Stage3D.RutOrder"/>):
/// a track driven across it marks it, and the crater the blast dug is drawn
/// over it.
/// </summary>
public sealed partial class CelScorch : Node3D
{
    /// <summary>How many the ground keeps.</summary>
    public const int Kept = 8;

    /// <summary>Squares a side of the sheet.</summary>
    public const int Grid = 24;

    /// <summary>How long it takes to open out, s, and to go its darkest - the
    /// fire's rise and most of its blaze (<see cref="Wreck.BlazeSeconds"/>).</summary>
    public float GrowTime = 1.2f, DarkTime = 4.0f;

    /// <summary>How much of it shows on the frame of the blast, and opened how
    /// far.</summary>
    public float FirstInk = 0.35f, FirstReach = 0.6f;

    /// <summary>What each band multiplies the ground by - darkening it, so
    /// the ground's own cracks and grain show through the burn. Mixed over it
    /// at a share, the bands came out paler than asked and all but vanished
    /// on this soil.</summary>
    public static readonly Color Soot = new(0.24f, 0.21f, 0.19f);
    public static readonly Color Char = new(0.38f, 0.32f, 0.27f);
    public static readonly Color Singe = new(0.64f, 0.55f, 0.44f);

    private sealed class Mark
    {
        public required MeshInstance3D Node;
        public required ShaderMaterial Look;
        public float Age;
    }

    private readonly List<Mark> _marks = new();
    private int _next, _burnt;
    private float _squash = 0.5f, _rise = 0.86f;
    private Func<Vector3, float>? _ground;

    /// <param name="ground">The ground's height under a world point.</param>
    public void Build(float squash, float rise, Func<Vector3, float> ground)
    {
        _squash = squash;
        _rise = rise;
        _ground = ground;
    }

    /// <summary>A hull has died at <paramref name="at"/> (world, on the
    /// ground): its scorch, <paramref name="radius"/> world px to the body's
    /// edge - the rays go a quarter further.</summary>
    public void Burn(Vector3 at, float radius)
    {
        _burnt++;
        Mark mark;
        if (_marks.Count < Kept)
        {
            var look = new ShaderMaterial { Shader = ScorchShader, RenderPriority = Stage3D.RutOrder - 1 };
            look.SetShaderParameter("soot", Soot);
            look.SetShaderParameter("burnt", Char);
            look.SetShaderParameter("singe", Singe);
            var node = new MeshInstance3D
            {
                Name = $"Scorch{_marks.Count}", MaterialOverride = look,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(node);
            mark = new Mark { Node = node, Look = look };
            _marks.Add(mark);
        }
        else
            mark = _marks[_next % _marks.Count];
        _next = (_next + 1) % Kept;
        float reach = radius * 1.25f;
        mark.Node.Mesh = Sheet(at, reach);
        mark.Node.GlobalPosition = at;
        mark.Look.SetShaderParameter("seed", CelPuff.Hash(_burnt, 401) * 17.0f);
        mark.Look.SetShaderParameter("turn", CelPuff.Hash(_burnt, 402) * Mathf.Tau);
        mark.Age = 0.0f;
        mark.Node.Visible = true;
        Apply(mark);
    }

    public void Tick(float dt)
    {
        foreach (Mark m in _marks)
            if (m.Node.Visible && m.Age < Mathf.Max(GrowTime, DarkTime))
            {
                m.Age += dt;
                Apply(m);
            }
    }

    /// <summary>Every scorch off the ground.</summary>
    public void Reset()
    {
        foreach (Mark m in _marks)
            m.Node.Visible = false;
    }

    private void Apply(Mark m)
    {
        float g = Mathf.Clamp(m.Age / GrowTime, 0.0f, 1.0f);
        float d = Mathf.Clamp(m.Age / DarkTime, 0.0f, 1.0f);
        m.Look.SetShaderParameter("grow", Mathf.Lerp(FirstReach, 1.0f, 1.0f - (1.0f - g) * (1.0f - g)));
        m.Look.SetShaderParameter("ink", Mathf.Lerp(FirstInk, 1.0f, Mathf.SmoothStep(0.0f, 1.0f, d)));
    }

    /// <summary>The sheet, <paramref name="reach"/> out each way from
    /// <paramref name="at"/>, on the ground: positions about
    /// <paramref name="at"/>, UV -1..1 across it.</summary>
    private ArrayMesh Sheet(Vector3 at, float reach)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 lift = Stage3D.Clear(_squash, _rise);
        for (int j = 0; j <= Grid; j++)
        for (int i = 0; i <= Grid; i++)
        {
            float u = i / (float)Grid * 2.0f - 1.0f, v = j / (float)Grid * 2.0f - 1.0f;
            var w = new Vector3(at.X + u * reach, at.Y, at.Z + v * reach);
            float y = _ground?.Invoke(w) ?? at.Y;
            st.SetUV(new Vector2(u, v));
            st.AddVertex(new Vector3(u * reach, y - at.Y, v * reach) + lift);
        }
        for (int j = 0; j < Grid; j++)
        for (int i = 0; i < Grid; i++)
        {
            int a = j * (Grid + 1) + i, b = a + 1, c = a + Grid + 1, d = c + 1;
            st.AddIndex(a); st.AddIndex(c); st.AddIndex(b);
            st.AddIndex(b); st.AddIndex(c); st.AddIndex(d);
        }
        return st.Commit();
    }

    /// <summary>
    /// The scorch on its sheet: UV -1..1 across, the body's edge at 0.8 of
    /// it wandering by a slow noise round it, rays out to the sheet's edge;
    /// the bands by the share of that edge, each wandering by a finer noise.
    /// <c>grow</c> opens it, <c>ink</c> darkens it.
    /// </summary>
    private static readonly Shader ScorchShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, blend_mul, depth_draw_never, cull_disabled, shadows_disabled;
uniform vec3 soot : source_color;
uniform vec3 burnt : source_color;
uniform vec3 singe : source_color;
uniform float grow = 1.0;
uniform float ink = 1.0;
uniform float seed = 0.0;
uniform float turn = 0.0;
" + Toon.NoiseCode + @"
void fragment() {
    vec2 p = UV;
    float r = length(p);
    float a = atan(p.y, p.x) + turn;
    vec2 around = vec2(cos(a), sin(a));
    // The body: a ragged round, lobed by a slow noise.
    float edge = 0.8 * (1.0 + 0.18 * (noise3(vec3(around * 1.6, seed)) - 0.5) * 2.0);
    // The rays: a few, each its own length, where the burning ran.
    float ray = pow(max(0.0, sin(a * 5.0 + seed)), 10.0) * (0.12 + 0.18 * noise3(vec3(around * 3.0, seed + 5.0)))
              + pow(max(0.0, sin(a * 8.0 + seed * 1.7)), 14.0) * (0.06 + 0.12 * noise3(vec3(around * 4.0, seed + 9.0)));
    edge = min(edge + ray, 0.99) * grow;
    float d = r / edge + (noise3(vec3(p * 7.0, seed + 2.0)) - 0.5) * 0.14;
    if (d > 1.0) discard;
    // The soot reaches past the crater's foot (0.75 of the body's edge on
    // a wreck): under the bowl alone it was never seen.
    vec3 c = d < 0.66 ? soot : d < 0.86 ? burnt : singe;
    ALBEDO = mix(vec3(1.0), c, ink);
}
",
    };
}
