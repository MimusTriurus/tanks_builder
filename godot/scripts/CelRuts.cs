using System.Collections.Generic;
using Godot;

namespace TankSpriteTest;

/// <summary>
/// The ruts a 3D tank's belts leave on the ground: a strip of pressed soil per
/// belt, its two walls a darker line along it and, across it, a bar where each
/// shoe bit - <see cref="TrackMarks"/>' picture, laid off the model rather than
/// off an atlas and a <see cref="Vehicle"/>, which the 3D bench does not have.
/// The sprites' layer is left as it is for the 2D tanks; <see cref="Stage3D"/>
/// draws that one on the board from their vehicles, and this draws the
/// model's in the same place and on the same rung (<see cref="Stage3D.RutOrder"/>),
/// under the model's shadow.
///
/// <b>The unit is the point where each belt touches the ground</b>, and the
/// stitches are laid off each belt's own run - <see cref="TrackMarks"/>' two
/// load-bearing rules, for their reasons: by time a trail thickens at a crawl,
/// by the hull's displacement a pivot lays nothing, and a pivot's two
/// concentric counter-running arcs are the signature of a tracked vehicle.
///
/// <b>The ladder is the shader's, off the distance along the rut</b>, not a
/// quad per stitch. Stitches land at most one a frame, so their spacing is the
/// frame's run as often as it is a link, and a two-pixel quad at each one
/// falls on the pixel grid as it happens: the shoes read as links of
/// different sizes (the user showed it on a picture). The ribbon carries its
/// length from where the run began (<c>UV.x</c>, px of ground, kept on each
/// stitch as it is laid, so a trail forgetting its oldest end does not slide
/// its bars) and the shader puts a bar every <see cref="Build"/> pitch,
/// antialiased by <c>fwidth</c> - and fades the ladder into the strip's
/// average where the camera packs bars closer than it can draw.
///
/// <b>Ground recovers</b>: a stitch fades from the day it is laid - the bars
/// first, then the soil - and is gone at <see cref="Life"/>. <b>Runs, not one
/// list</b>: a jump (reset, a new model) or wet ground lifts the pen, and the
/// trail breaks rather than joins the two ends.
/// </summary>
public sealed partial class CelRuts : Node3D
{
    /// <summary>Stitches kept per belt - a backstop; <see cref="Life"/> is the
    /// mechanism, as in <see cref="TrackMarks.Capacity"/>.</summary>
    public const int Capacity = 1500;

    /// <summary>How long a stitch lasts, s. Shorter than the sprites'
    /// <see cref="TrackMarks.Life"/> (20 s, fading from 8): at that the ruts
    /// did not read as going at all.</summary>
    public float Life = 15.0f;
    /// <summary>When the fade starts, as a share of <see cref="Life"/>: whole
    /// for the first five seconds, then going.</summary>
    public float FadeFrom = 0.33f;
    /// <summary>The pressed soil, the walls along it and the shoe bars. One
    /// layer, not three: the shader mixes them, so a bar's alpha is the whole
    /// of it. Softer than the first cut (0.28 / 0.56 / 0.58, hard-edged), which
    /// the user found too sharp for a mark in dirt.</summary>
    public Color Soil = new(0.20f, 0.16f, 0.11f, 0.26f);
    public Color Wall = new(0.14f, 0.11f, 0.08f, 0.42f);
    public Color Bar = new(0.14f, 0.11f, 0.08f, 0.50f);
    /// <summary>How far in from each edge the soil starts to thin, and where
    /// the wall band stands, as shares of the half-width: the rut has no line
    /// round it, it darkens toward its walls and dies out past them.</summary>
    public float SoftFrom = 0.55f, WallAt = 0.80f;
    /// <summary>How thick a bar is, as a share of the ladder's step, and how
    /// soft its edges are, as a share of its own thickness.</summary>
    public float BarShare = 0.22f, BarSoft = 0.45f;
    /// <summary>How far a bar falls short of the belt's edges, as a share of
    /// the width: a shoe does not reach the wall it presses.</summary>
    public float BarShort = 0.12f;
    /// <summary>The ladder's step is the fewest whole links that span this
    /// share of the rut's width. A bar on every link is what the belt has, but
    /// LTR's 87 links are 3 px apart across a 22 px rut, and at the board's
    /// zoom that is a hatching, not shoes (the user asked for it sparser).
    /// Whole links, so it is still the belt's own rhythm, every Nth grouser.</summary>
    public float StepOfWidth = 0.5f;

    /// <summary>One belt this frame: where its middle touches the ground, what
    /// is added to that only where it is drawn (clear of the ground, and in a
    /// ford up to the surface along the eye's ray - the length, the heading
    /// and the jumps are the ground point's), the way across it (flat, unit -
    /// the hull's left), how far it ran this frame (px), whether the ground
    /// there takes a mark and whether it is a ford.</summary>
    public readonly record struct Belt(Vector3 At, Vector3 Lift, Vector3 Across, float Run, bool Marks, bool Wet);

    private struct Stitch
    {
        public Vector3 At, Lift, Across;
        public float Born, Along;
        public bool Break, Wet;
    }

    private sealed class Trail
    {
        public readonly List<Stitch> Stitches = new();
        public float Spent;
        public bool Down;
        public Vector3 Last;
        public float Along;
    }

    private readonly List<Trail> _trails = new();
    private readonly MeshInstance3D _mesh = new() { Name = "Ruts", Mesh = new ImmediateMesh() };
    private readonly ShaderMaterial _look = new() { Shader = RutShader, RenderPriority = Stage3D.RutOrder };
    /// <summary>
    /// The ruts in a ford: on the water's surface rather than on the bed (the
    /// bench lays them there - a point on the bed lost the depth test against
    /// it), and over the water rather than under it. Under it, as terrain is,
    /// they were not dimmed but gone: the ford's surface is drawn after them
    /// and paints them out. <see cref="Stage3D.RingOrder"/> was not enough
    /// either - measured on captures, nothing at -1, the ruts at 0 and 1 - so
    /// they stand on <see cref="Stage3D.DressOrder"/> with the sort offset far
    /// back, which lets the exhaust clouds on the same rung draw over them.
    /// Their own tones (<see cref="WetSoil"/>), darker than the dry rut's: a
    /// pale silt on this water did not read at any alpha.
    /// </summary>
    private readonly MeshInstance3D _wetMesh = new() { Name = "WetRuts", Mesh = new ImmediateMesh() };
    private ShaderMaterial _wetLook = null!;
    private readonly List<(Vector3 At, Vector2 Uv, float A)> _dry = new(), _wet = new();
    private float _pitch = 5.0f, _width = 20.0f;
    private float _clock;

    /// <summary>A rut in a ford: a shade denser than on dry ground, because
    /// the water's own picture is busier than the soil's.</summary>
    public Color WetSoil = new(0.16f, 0.13f, 0.09f, 0.34f);
    public Color WetWall = new(0.11f, 0.09f, 0.06f, 0.42f);
    public Color WetBar = new(0.11f, 0.09f, 0.06f, 0.40f);
    /// <summary>How much of the fade a ford keeps, as a multiplier on the
    /// vertex alpha (which cannot go past one).</summary>
    public float WetShare = 1.0f;

    public override void _Ready()
    {
        _mesh.MaterialOverride = _look;
        _mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_mesh);
        _wetLook = (ShaderMaterial)_look.Duplicate();
        _wetLook.RenderPriority = Stage3D.DressOrder;
        _wetMesh.MaterialOverride = _wetLook;
        _wetMesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _wetMesh.SortingOffset = -100000.0f;
        AddChild(_wetMesh);
        Dress();
    }

    /// <summary>A belt's link pitch and width, board px.</summary>
    public void Build(float pitchPx, float widthPx)
    {
        _pitch = Mathf.Max(pitchPx, 1.0f);
        _width = Mathf.Max(widthPx, 1.0f);
        Dress();
    }

    private void Dress()
    {
        float step = Step;
        foreach (ShaderMaterial look in new[] { _look, _wetLook })
        {
            if (look is null)
                continue;
            bool wet = look == _wetLook;
            look.SetShaderParameter("soil", wet ? WetSoil : Soil);
            look.SetShaderParameter("wall", wet ? WetWall : Wall);
            look.SetShaderParameter("bar", wet ? WetBar : Bar);
            look.SetShaderParameter("pitch", step);
            look.SetShaderParameter("bar_half", Mathf.Max(0.75f, 0.5f * BarShare * step));
            look.SetShaderParameter("bar_soft", BarSoft);
            look.SetShaderParameter("bar_reach", 1.0f - BarShort);
            look.SetShaderParameter("soft_from", SoftFrom);
            look.SetShaderParameter("wall_at", WallAt);
        }
    }

    /// <summary>The ladder's step, px: whole links, see <see cref="StepOfWidth"/>.</summary>
    public float Step => _pitch * Mathf.Max(1.0f, Mathf.Ceil(StepOfWidth * _width / _pitch - 0.001f));

    /// <summary>Every mark gone - the repair, as <c>R</c> is on the 2D bench.</summary>
    public void Clear()
    {
        _trails.Clear();
        _clock = 0.0f;
        ((ImmediateMesh)_mesh.Mesh).ClearSurfaces();
        ((ImmediateMesh)_wetMesh.Mesh).ClearSurfaces();
    }

    /// <summary>Lift every pen: the next stitch starts a new run. For a tank
    /// that jumps - parked, or a new model on the rig.</summary>
    public void Lift()
    {
        foreach (Trail t in _trails)
            t.Down = false;
    }

    /// <summary>A frame: stitches laid on <paramref name="belts"/> as far as
    /// each ran, the old ones forgotten, the whole redrawn.</summary>
    public void Tick(float dt, IReadOnlyList<Belt> belts)
    {
        _clock += dt;
        while (_trails.Count < belts.Count)
            _trails.Add(new Trail());
        for (int i = 0; i < belts.Count; i++)
            Lay(_trails[i], belts[i]);
        foreach (Trail t in _trails)
        {
            List<Stitch> s = t.Stitches;
            int gone = 0;
            while (gone < s.Count && (_clock - s[gone].Born >= Life || s.Count - gone > Capacity))
                gone++;
            if (gone > 0)
            {
                s.RemoveRange(0, gone);
                // What is left of a run keeps its start.
                if (s.Count > 0)
                    s[0] = s[0] with { Break = true };
            }
        }
        Draw();
    }

    private void Lay(Trail t, Belt b)
    {
        if (!b.Marks)
        {
            t.Down = false;
            return;
        }
        // A jump is a lifted pen, not a stitch across the board.
        if (t.Down && t.Last.DistanceTo(b.At) > Mathf.Max(4.0f * _pitch, 2.0f * _width))
            t.Down = false;
        if (!t.Down)
        {
            t.Down = true;
            t.Spent = 0.0f;
            t.Along = 0.0f;
            t.Last = b.At;
            Add(t, b, true);
            return;
        }
        t.Spent += Mathf.Abs(b.Run);
        if (t.Spent < _pitch)
            return;
        // One a frame, the remainder carried - TrackDust's and TrackMarks' rule.
        // The spacing no longer draws anything: the bars are the shader's.
        t.Spent = Mathf.Min(t.Spent - _pitch, _pitch);
        t.Along += new Vector3(b.At.X - t.Last.X, 0.0f, b.At.Z - t.Last.Z).Length();
        t.Last = b.At;
        Add(t, b, false);
    }

    private void Add(Trail t, Belt b, bool breaks) =>
        t.Stitches.Add(new Stitch
        {
            At = b.At, Lift = b.Lift, Across = b.Across, Born = _clock, Along = t.Along, Break = breaks, Wet = b.Wet,
        });

    private float Fade(in Stitch s) =>
        1.0f - Mathf.SmoothStep(FadeFrom, 1.0f, (_clock - s.Born) / Mathf.Max(Life, 1e-3f));

    private void Draw()
    {
        _dry.Clear();
        _wet.Clear();
        float half = _width * 0.5f;
        foreach (Trail t in _trails)
        {
            List<Stitch> s = t.Stitches;
            for (int start = 0; start < s.Count;)
            {
                int end = start + 1;
                while (end < s.Count && !s[end].Break)
                    end++;
                if (end - start >= 2)
                    Run(s, start, end, half);
                start = end;
            }
        }
        Fill((ImmediateMesh)_mesh.Mesh, _dry);
        Fill((ImmediateMesh)_wetMesh.Mesh, _wet);
    }

    private static void Fill(ImmediateMesh mesh, List<(Vector3 At, Vector2 Uv, float A)> verts)
    {
        mesh.ClearSurfaces();
        if (verts.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach ((Vector3 at, Vector2 uv, float a) in verts)
        {
            // The fade rides as the vertex's alpha.
            mesh.SurfaceSetColor(new Color(1.0f, 1.0f, 1.0f, a));
            mesh.SurfaceSetUV(uv);
            mesh.SurfaceAddVertex(at);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>One run as one strip, with per-point sides averaged between
    /// the steps that meet there - continuous, no seam at a joint,
    /// <see cref="Stage3D"/>'s ribbon. UV: length along, px; across, -1..1.
    /// A step with either end in a ford goes to the wet mesh.</summary>
    private void Run(List<Stitch> s, int start, int end, float half)
    {
        int n = end - start;
        var side = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 back = i > 0 ? Across(s[start + i].At - s[start + i - 1].At) : Vector3.Zero;
            Vector3 on = i + 1 < n ? Across(s[start + i + 1].At - s[start + i].At) : Vector3.Zero;
            Vector3 mean = back + on;
            if (mean.LengthSquared() <= 1e-12f)
                mean = back.LengthSquared() > 1e-12f ? back : on;
            // A path that has not moved yet (a pivot's first stitch) takes the
            // belt's own across.
            side[i] = (mean.LengthSquared() > 1e-12f ? mean.Normalized() : s[start + i].Across) * half;
        }
        for (int i = 0; i + 1 < n; i++)
        {
            Stitch a = s[start + i], b = s[start + i + 1];
            List<(Vector3, Vector2, float)> into = a.Wet || b.Wet ? _wet : _dry;
            float fa = Fade(a) * (a.Wet ? WetShare : 1.0f), fb = Fade(b) * (b.Wet ? WetShare : 1.0f);
            into.Add((a.At + a.Lift - side[i], new Vector2(a.Along, -1.0f), fa));
            into.Add((a.At + a.Lift + side[i], new Vector2(a.Along, 1.0f), fa));
            into.Add((b.At + b.Lift + side[i + 1], new Vector2(b.Along, 1.0f), fb));
            into.Add((a.At + a.Lift - side[i], new Vector2(a.Along, -1.0f), fa));
            into.Add((b.At + b.Lift + side[i + 1], new Vector2(b.Along, 1.0f), fb));
            into.Add((b.At + b.Lift - side[i + 1], new Vector2(b.Along, -1.0f), fb));
        }
    }

    private static Vector3 Across(Vector3 step)
    {
        var flat = new Vector3(step.Z, 0.0f, -step.X);
        return flat.LengthSquared() <= 1e-12f ? Vector3.Zero : flat.Normalized();
    }

    /// <summary>
    /// The rut from the ribbon's UV, soft all round: the soil thins toward the
    /// edges from <c>soft_from</c> and is gone at the edge itself, so the
    /// strip has no border; the walls are a darker band about <c>wall_at</c>,
    /// rising and dying away rather than a line; a bar every <c>pitch</c> px
    /// along with edges a good share of its own thickness soft (never less than
    /// the pixel, <c>fwidth</c>), faded into its own average where bars come
    /// closer than about two pixels, and thinning out toward the walls. The
    /// fade (vertex alpha) takes the bars first: at half-faded the ladder is
    /// gone and the strip is still there, the order a rut weathers in.
    /// </summary>
    private static readonly Shader RutShader = new()
    {
        Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec4 soil : source_color;
uniform vec4 wall : source_color;
uniform vec4 bar : source_color;
uniform float pitch = 5.0;
uniform float bar_half = 1.0;
uniform float bar_soft = 0.45;
uniform float bar_reach = 0.9;
uniform float soft_from = 0.55;
uniform float wall_at = 0.8;
void fragment() {
    float u = UV.x;
    float v = abs(UV.y);
    float fu = max(fwidth(u), 1e-4);
    // The strip: whole in the middle, thinning to nothing at the edge.
    float body = 1.0 - smoothstep(soft_from, 1.0, v);
    // The walls: a band about wall_at, soft both sides.
    float in_wall = smoothstep(soft_from, wall_at, v) * (1.0 - smoothstep(wall_at, 1.0, v));
    float d = abs(fract(u / pitch) - 0.5) * pitch;
    float soft = max(bar_soft * bar_half, 0.5 * fu);
    float on_bar = 1.0 - smoothstep(bar_half - soft, bar_half + soft, d);
    // Closer than the screen can draw: the ladder's average, not its moire.
    on_bar = mix(on_bar, 2.0 * bar_half / pitch, smoothstep(0.25, 0.5, fu / pitch));
    on_bar *= 1.0 - smoothstep(bar_reach - 0.25, bar_reach, v);
    float fade = COLOR.a;
    // The ladder goes first.
    on_bar *= smoothstep(0.35, 0.85, fade);
    vec4 c = mix(soil, bar, on_bar);
    c = mix(c, wall, in_wall);
    ALBEDO = c.rgb;
    ALPHA = c.a * fade * max(body, in_wall);
}
",
    };
}
